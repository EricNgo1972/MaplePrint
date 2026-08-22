using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MaplePrint.Diagnostics;
using MaplePrint.Printing;
using MaplePrint.Settings;

namespace MaplePrint.Server;

public enum ServerState { Stopped, Starting, Listening, Faulted }

/// <summary>
/// The local print API. Callers POST a page or device bytes, MaplePrint spools
/// them and says what happened — that is the entire contract.
///
/// MaplePrint knows nothing about who is calling. It has no server URL, no
/// account, and no outbound connection: whoever wants to print is configured
/// with *our* address, not the reverse. That keeps this equally useful to
/// MapleCoffee, to a Laravel POS, and to a one-line curl in a support call,
/// without a line of code changing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PrintServer : IAsyncDisposable
{
    /// <summary>A receipt is a few kB and a label less. Anything this large is a mistake.</summary>
    private const int MaxPayloadBytes = 8 * 1024 * 1024;

    private readonly ActivityLog _log;

    /// <summary>The append-only record of every drawer open. A record, not a gate.</summary>
    private readonly DrawerAudit _drawerAudit;

    /// <summary>
    /// Which jobs have already opened the drawer, so a retry reprints without
    /// reopening the till.
    /// </summary>
    private readonly DrawerKickLedger _kicks = new();

    /// <summary>
    /// One queue per printer, one consumer draining each. Two jobs interleaved
    /// on one printer produce half of each receipt, and a shop that prints from
    /// two tills at once will find that out at the worst moment.
    /// </summary>
    private readonly PrintDispatcher _dispatcher;

    private WebApplication? _app;
    private AppSettings _settings;

    public PrintServer(AppSettings settings, ActivityLog log, DrawerAudit? drawerAudit = null)
    {
        _settings = settings;
        _log = log;
        _drawerAudit = drawerAudit ?? new DrawerAudit(Path.GetDirectoryName(log.FilePath)!, log);
        _dispatcher = new PrintDispatcher(ExecuteJobAsync);
    }

    public ServerState State { get; private set; } = ServerState.Stopped;

    /// <summary>Where callers should point, once we are listening.</summary>
    public string? ListenUrl { get; private set; }

    public event Action<ServerState, string?>? StateChanged;

    public static string Version =>
        typeof(PrintServer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public void UpdateSettings(AppSettings settings) => _settings = settings;

    public async Task StartAsync()
    {
        await StopAsync();

        if (!_settings.IsServable)
        {
            SetState(ServerState.Stopped, $"Port {_settings.ListenPort} isn't usable — pick another in Settings.");
            return;
        }

        // A drawer set up wrong must say so now, not at the counter during a
        // sale. Nothing else in a print server has a failure this quiet: the
        // receipt prints, the till simply does not open.
        var drawerProblems = _settings.Destinations
            .Where(d => d.HasDrawer)
            .Select(DrawerDispatch.Problem)
            .Where(problem => problem is not null)
            .ToList();

        if (drawerProblems.Count > 0)
        {
            SetState(ServerState.Faulted, string.Join(" ", drawerProblems));
            return;
        }

        // All interfaces only when the operator has asked for it. Binding
        // everything by default would quietly put a printer on the shop wifi.
        var bindAddress = _settings.AllowRemote ? IPAddress.Any : IPAddress.Loopback;

        SetState(ServerState.Starting, null);

        try
        {
            var builder = WebApplication.CreateSlimBuilder();

            // No console exists behind a WinForms app; the activity log is our
            // console, and it only wants what a shop would act on.
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(bindAddress, _settings.ListenPort));

            // A tray app has no console to Ctrl-C and no SIGTERM to catch, but
            // the default console lifetime hooks AppDomain.ProcessExit anyway
            // and blocks there waiting on a host it believes is still running.
            // On Exit that is the difference between a closed application and
            // one still sitting in Task Manager.
            builder.Services.AddSingleton<IHostLifetime, NoHostLifetime>();

            var app = builder.Build();
            MapEndpoints(app);

            await app.StartAsync();

            _app = app;
            ListenUrl = $"http://{(_settings.AllowRemote ? LocalAddress() : "127.0.0.1")}:{_settings.ListenPort}";
            SetState(ServerState.Listening, null);
        }
        catch (IOException ex)
        {
            // Overwhelmingly the common failure: something already has the port.
            SetState(ServerState.Faulted,
                $"Port {_settings.ListenPort} is already in use. Pick another in Settings. ({ex.Message})");
        }
        catch (Exception ex)
        {
            SetState(ServerState.Faulted, ex.Message);
        }
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        if (app is null) return;

        try { await app.StopAsync(TimeSpan.FromSeconds(5)); } catch { }
        try { await app.DisposeAsync(); } catch { }

        ListenUrl = null;
        SetState(ServerState.Stopped, null);
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    private void MapEndpoints(WebApplication app)
    {
        // Open on purpose: a monitoring probe shouldn't need the print key, and
        // this leaks nothing an attacker on the LAN couldn't already see.
        app.MapGet("/health", () =>
            Results.Ok(new HealthResponse("ok", _settings.StationName, Version, _settings.RequiresAuth)));

        app.MapGet("/destinations", (HttpContext ctx) => Authorized(ctx) ? Destinations() : Unauthorized());

        // The name this endpoint had before destinations existed.
        app.MapGet("/printers", (HttpContext ctx) => Authorized(ctx) ? Destinations() : Unauthorized());

        // JSON form: the general case, and what an app integrates against.
        app.MapPost("/print", async (HttpContext ctx, PrintRequest request) =>
        {
            if (!Authorized(ctx)) return Unauthorized();

            // Empty is legal only for a no-sale: openDrawer with nothing to
            // print. Without that flag this is the same refusal it always was,
            // so a caller written before drawers existed sees no change.
            if (string.IsNullOrWhiteSpace(request.PayloadBase64) && !request.OpenDrawer)
                return Fail(400, JobId(request.JobId), request.DestinationName, null, "payloadBase64 is required.");

            byte[] payload;
            try
            {
                payload = string.IsNullOrWhiteSpace(request.PayloadBase64)
                    ? []
                    : Convert.FromBase64String(request.PayloadBase64);
            }
            catch (FormatException)
            {
                return Fail(400, JobId(request.JobId), request.DestinationName, null,
                    "payloadBase64 was not valid base64.");
            }

            if (payload.Length > MaxPayloadBytes)
                return Fail(400, JobId(request.JobId), request.DestinationName, null,
                    $"Payload is {payload.Length / 1024}kB; the limit is {MaxPayloadBytes / 1024}kB.");

            return await ExecuteAsync(
                request.DestinationName, request.ContentType, payload,
                request.Description, request.JobId, request.Copies,
                request.OpenDrawer, request.Reason, request.User);
        });

        // Raw form: POST the bytes themselves. Exists so a technician can prove
        // the whole path with one curl and a file, without base64-ing anything.
        app.MapPost("/print/{destination}", async (HttpContext ctx, string destination) =>
        {
            if (!Authorized(ctx)) return Unauthorized();

            // Headers rather than a body, because this endpoint's body is the
            // payload. A no-sale here is an empty POST with the drawer header,
            // which is what makes the whole path provable with one curl.
            var openDrawer = IsTrue(ctx.Request.Headers["X-MaplePrint-Open-Drawer"]);

            var payload = await ReadBodyAsync(ctx);
            if (payload is null)
                return Fail(400, JobId(null), destination, null,
                    $"Body was larger than {MaxPayloadBytes / 1024}kB.");

            if (payload.Length == 0 && !openDrawer)
                return Fail(400, JobId(null), destination, null,
                    $"Body was empty or larger than {MaxPayloadBytes / 1024}kB.");

            return await ExecuteAsync(
                destination,
                ContentTypeFromHeader(ctx.Request.ContentType),
                payload,
                ctx.Request.Headers["X-MaplePrint-Description"],
                ctx.Request.Headers["X-MaplePrint-Job-Id"],
                copies: 1,
                openDrawer,
                ctx.Request.Headers["X-MaplePrint-Reason"],
                ctx.Request.Headers["X-MaplePrint-User"]);
        });
    }

    private IResult Destinations()
    {
        var queues = PrinterQueues.Installed();

        var destinations = _settings.Destinations.Select(d => new DestinationInfo(
            d.Name,
            KindName(d.Kind),
            ExpectedContentType(d),
            d.Language,
            d.Queue,
            d.DotWidth,
            d.RenderDpi,
            queues.Any(q => string.Equals(q, d.Queue, StringComparison.OrdinalIgnoreCase)),
            d.Enabled,
            d.HasDrawer
                ? new DrawerInfo(d.DrawerCommand!.Trim().ToLowerInvariant(), d.EffectiveDrawerPulseMs, d.EffectiveDrawerUnit)
                : null)).ToList();

        return Results.Ok(new DestinationsResponse(_settings.StationName, destinations, queues));
    }

    /// <summary>Resolve, validate, queue, spool, answer. The whole job.</summary>
    private async Task<IResult> ExecuteAsync(
        string? destinationName, string? contentType, byte[] payload,
        string? description, string? jobId, int copies,
        bool openDrawer = false, string? reason = null, string? user = null)
    {
        var id = JobId(jobId);
        var callerSuppliedJobId = !string.IsNullOrWhiteSpace(jobId);
        var label = string.IsNullOrWhiteSpace(description) ? (destinationName ?? "job") : description!;
        var drawerOnly = payload.Length == 0 && openDrawer;

        // A job that asks for nothing at all. Refused rather than quietly
        // succeeding, because a caller that sent it has a bug and wants to know.
        if (payload.Length == 0 && !openDrawer)
            return Refuse(400, id, destinationName, null, label,
                "A job must carry a payload, open the drawer, or both.");

        var destination = _settings.Find(destinationName);
        if (destination is null)
        {
            var known = _settings.Destinations.Count == 0
                ? "none are configured"
                : string.Join(", ", _settings.Destinations.Select(d => d.Name));
            return Refuse(404, id, destinationName, null, label,
                $"No destination named '{destinationName}'. Configured: {known}. Add one in MaplePrint -> Settings.");
        }

        // Request shape first, station state second. A caller integrating against
        // a till whose printer is currently unplugged should still be told its
        // content type is wrong — otherwise the station's problem masks theirs,
        // and they fix the wrong thing.
        var expected = ExpectedContentType(destination);
        var actual = string.IsNullOrWhiteSpace(contentType) ? expected : contentType!.Trim().ToLowerInvariant();

        // A no-sale carries no content, so there is nothing to disagree about.
        if (!drawerOnly && actual != expected)
            return Refuse(415, id, destination.Name, destination.Queue, label, MismatchMessage(destination, actual, expected));

        if (copies is < 1 or > 99)
            return Refuse(400, id, destination.Name, destination.Queue, label, "copies must be between 1 and 99.");

        if (!destination.Enabled)
            return Refuse(409, id, destination.Name, destination.Queue, label,
                $"Destination '{destination.Name}' is turned off.");

        if (!destination.IsConfigured)
            return Refuse(409, id, destination.Name, null, label,
                $"Destination '{destination.Name}' has no printer assigned. Set one in MaplePrint -> Settings.");

        if (!PrinterQueues.Exists(destination.Queue))
            return Refuse(404, id, destination.Name, destination.Queue, label,
                $"The queue '{destination.Queue}' is not installed on this PC.");

        // Asking a printer with no drawer to open one is configuration, not
        // policy — the same shape of refusal as a destination with no queue.
        if (openDrawer && DrawerDispatch.Problem(destination) is { } drawerProblem)
        {
            AuditDrawer(destination, DrawerAudit.Refused, id, user, reason, drawerProblem);
            return Refuse(409, id, destination.Name, destination.Queue, label, drawerProblem);
        }

        var job = new PrintJob
        {
            DeviceKey = new PrinterEndpoint.WindowsQueue(destination.Queue).DeviceKey,
            DeviceLabel = destination.Queue,
            Destination = destination,
            ContentType = actual,
            Payload = payload,
            OpenDrawer = openDrawer,
            JobId = id,
            CallerSuppliedJobId = callerSuppliedJobId,
            Label = label,
            Copies = copies,
            User = string.IsNullOrWhiteSpace(user) ? null : user!.Trim(),
            Reason = Because(reason, drawerOnly, user)
        };

        var result = await _dispatcher.DispatchAsync(job);

        if (result.Outcome == DispatchOutcome.Busy)
            return Refuse(503, id, destination.Name, destination.Queue, label,
                result.Error ?? $"'{destination.Queue}' is still busy with an earlier job.");

        var error = result.Error;

        if (error is null)
        {
            _log.Success(Describe(job));
            return Results.Ok(new PrintResponse(id, true, destination.Name, destination.Queue,
                DrawerOpened: job.DrawerOpened));
        }

        _log.Error($"Failed {label} -> {destination.Name}: {error}");

        // 502: we understood the request, the device let us down. Lets a
        // caller distinguish "retry me" from "your request was wrong" (4xx).
        //
        // Except for a no-sale. A re-printed receipt wastes a slip; a re-kicked
        // drawer is a till standing open with nobody in front of it. This one
        // goes to a person, not round the retry loop.
        return Results.Json(
            new PrintResponse(id, false, destination.Name, destination.Queue, error,
                DrawerOpened: job.DrawerOpened,
                Retryable: drawerOnly ? false : null),
            statusCode: 502);
    }

    /// <summary>
    /// What one job actually does at the printer, run by that printer's single
    /// consumer. Nothing else is touching this device while this runs.
    /// </summary>
    private async Task<string?> ExecuteJobAsync(PrintJob job, CancellationToken ct)
    {
        var destination = job.Destination;

        try
        {
            // Before spooling, so a job that the driver then rejects is still on
            // disk to look at — that is the case worth having a copy of.
            if (_settings.SaveJobCopies && job.Payload.Length > 0 &&
                JobArchive.Save(destination, job.ContentType, job.Payload, DateTime.Now) is { } saved)
                _log.Info($"Saved a copy of {job.Label} to {saved}");

            var commandSet = job.OpenDrawer ? PrinterCommandSets.For(destination) : null;

            // Claimed before anything is sent, deliberately. A kick that went out
            // and a payload that then failed is a drawer that opened, and the
            // retry must not open it twice — so a partial write counts as
            // already-opened, which is the safe direction to be wrong in.
            if (commandSet is not null && !_kicks.TryClaim(job.CallerSuppliedJobId ? job.JobId : null))
            {
                commandSet = null;
                _log.Info($"{job.Label}: job {job.JobId} already opened the drawer; reprinting without it.");
                AuditDrawer(destination, DrawerAudit.Suppressed, job.JobId, job.User, job.Reason, null);
            }

            var sends = DrawerDispatch.Plan(
                destination, commandSet, job.ContentType, job.Payload, job.OpenDrawer);

            foreach (var send in sends)
            {
                var error = send.Kind == SendKind.Image
                    ? await Task.Run(() => PrintImage(destination, send.Bytes, job.Label, job.Copies), ct)
                    : await SendRawAsync(destination, send.Bytes, job.Label, ct);

                if (send.ContainsKick)
                {
                    job.DrawerOpened = error is null;
                    AuditDrawer(
                        destination,
                        error is null ? DrawerAudit.Opened : DrawerAudit.Failed,
                        job.JobId, job.User, job.Reason, error);
                }

                if (error is not null) return error;
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Device bytes to the printer, through the transport seam. RAW datatype,
    /// so they reach the printer's own command parser rather than the driver's
    /// renderer — which is what lets a drawer kick work on a raster-only printer
    /// whose receipts go out as rendered pages.
    /// </summary>
    private static async Task<string?> SendRawAsync(
        Destination destination, byte[] bytes, string label, CancellationToken ct)
    {
        var (transport, problem) = PrinterTransports.For(new PrinterEndpoint.WindowsQueue(destination.Queue));
        return transport is null ? problem : await transport.SendAsync(bytes, label, ct);
    }

    /// <summary>
    /// What to record this open as. The caller's own words when it sent any —
    /// they know the order number and we do not. Otherwise the one thing we can
    /// see for ourselves: paper moved, or it did not. A blank line in the audit
    /// is the one that cannot be answered later, and "no-sale opens by employee"
    /// is exactly the question this file exists for.
    /// </summary>
    private static string Because(string? reason, bool drawerOnly, string? user)
    {
        if (!string.IsNullOrWhiteSpace(reason)) return reason!.Trim();
        if (!drawerOnly) return "sale";

        return string.IsNullOrWhiteSpace(user) ? "no-sale" : $"no-sale:{user!.Trim()}";
    }

    private void AuditDrawer(
        Destination destination, string outcome, string jobId, string? user, string? reason, string? error)
    {
        var set = PrinterCommandSets.For(destination.DrawerCommand);

        _drawerAudit.Write(new DrawerAuditEntry(
            DateTimeOffset.Now,
            _settings.StationName,
            destination.Name,
            destination.Queue,
            outcome,
            CommandSet: set?.Name,
            PulseMs: destination.HasDrawer ? destination.EffectiveDrawerPulseMs : null,
            Unit: destination.HasDrawer ? destination.EffectiveDrawerUnit : null,
            User: user,
            Reason: reason,
            JobId: jobId,
            Error: error));
    }

    private static string Describe(PrintJob job)
    {
        var what = job.Payload.Length == 0
            ? "Opened the drawer on"
            : $"Printed {job.Label} ->";

        var drawer = job.Payload.Length > 0 && job.DrawerOpened ? ", drawer opened" : "";

        return job.Payload.Length == 0
            ? $"{what} {job.Destination.Name} ({job.Destination.Queue})."
            : $"{what} {job.Destination.Name} ({job.Destination.Queue}, {job.Payload.Length} bytes{drawer}).";
    }

    private string? PrintImage(Destination destination, byte[] payload, string label, int copies)
    {
        var (image, error) = ImageDecoder.Decode(payload);
        if (image is null) return error;

        using (image)
        {
            return DocumentPrinter.Print(
                destination.Queue, image, label, copies,
                destination.PaperSizeName,
                destination.RenderDpi,
                note: _log.Info);
        }
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpContext ctx)
    {
        using var buffer = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(buffer);
        // Null means too large. Empty is a real answer now — it is what a
        // drawer-only job looks like — so the endpoint decides what to make of it.
        if (buffer.Length > MaxPayloadBytes) return null;
        return buffer.ToArray();
    }

    /// <summary>
    /// For the raw-body endpoint, the HTTP Content-Type says what was sent.
    /// Null when it says nothing useful, which leaves the destination's kind to
    /// decide.
    /// </summary>
    private static string? ContentTypeFromHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;

        var mime = header.Split(';')[0].Trim().ToLowerInvariant();
        if (mime.StartsWith("image/")) return ContentTypes.Image;
        if (mime is "application/octet-stream" or "text/plain") return ContentTypes.Raw;

        return null;
    }

    private static string ExpectedContentType(Destination destination) =>
        destination.Kind == DestinationKind.Raw ? ContentTypes.Raw : ContentTypes.Image;

    private static string KindName(DestinationKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>
    /// The message that exists so nobody repeats the afternoon where ESC/POS
    /// went to a laser printer and ejected three pages of nonsense.
    /// </summary>
    private static string MismatchMessage(Destination destination, string actual, string expected)
    {
        if (expected == ContentTypes.Image)
            return $"Destination '{destination.Name}' is a document printer: it needs a rendered page "
                 + $"(contentType '{ContentTypes.Image}', a PNG {destination.DotWidth} dots wide at "
                 + $"{destination.RenderDpi} dpi), not '{actual}'. Device bytes sent to a driver printer "
                 + "print as nonsense.";

        var language = string.IsNullOrWhiteSpace(destination.Language) ? "its own language" : destination.Language!.ToUpperInvariant();
        return $"Destination '{destination.Name}' expects device bytes in {language} "
             + $"(contentType '{ContentTypes.Raw}'), not '{actual}'.";
    }

    private IResult Refuse(int status, string jobId, string? destination, string? queue, string label, string error)
    {
        _log.Error($"Rejected {label}: {error}");
        return Fail(status, jobId, destination, queue, error);
    }

    private static IResult Fail(int status, string jobId, string? destination, string? queue, string error) =>
        Results.Json(new PrintResponse(jobId, false, destination, queue, error), statusCode: status);

    /// <summary>
    /// No key configured means no authentication — the normal LAN case, where
    /// every caller is already trusted and a key would be friction without a
    /// threat. When a key *is* set, it may arrive as a bearer token or an
    /// <c>X-MaplePrint-Key</c> header, whichever the calling stack finds
    /// natural, and is compared in fixed time: a timing oracle on a 32-byte key
    /// is a real, if unglamorous, way to lose it.
    /// </summary>
    private bool Authorized(HttpContext ctx)
    {
        if (!_settings.RequiresAuth) return true;

        var expected = _settings.ApiKey;
        var presented = ctx.Request.Headers["X-MaplePrint-Key"].ToString();

        if (string.IsNullOrEmpty(presented))
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                presented = auth["Bearer ".Length..].Trim();
        }

        if (string.IsNullOrEmpty(presented)) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(expected));
    }

    private static IResult Unauthorized() =>
        Results.Json(new { error = "A valid X-MaplePrint-Key is required." }, statusCode: 401);

    /// <summary>
    /// A header meaning yes. Generous about how it is spelled, because this one
    /// is typed by hand in a curl as often as it is set by a program.
    /// </summary>
    private static bool IsTrue(string? value) =>
        value is not null &&
        value.Trim() is var v &&
        (v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         v.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
         v == "1");

    private static string JobId(string? supplied) =>
        string.IsNullOrWhiteSpace(supplied) ? Guid.NewGuid().ToString("n") : supplied!.Trim();

    /// <summary>
    /// Best guess at the address a caller on the LAN should use. Only ever
    /// shown in the UI — it is a convenience for whoever is configuring the
    /// calling app, not something the server relies on.
    /// </summary>
    private static string LocalAddress()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            var v4 = host.AddressList.FirstOrDefault(a =>
                a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(a));
            if (v4 is not null) return v4.ToString();
        }
        catch { }

        return Environment.MachineName;
    }

    private void SetState(ServerState state, string? reason)
    {
        State = state;

        if (state is ServerState.Listening) _log.Success($"Listening on {ListenUrl}");
        else if (state is ServerState.Faulted) _log.Error($"Print server faulted: {reason}");
        else if (reason is not null) _log.Info($"Print server {state.ToString().ToLowerInvariant()}: {reason}");
        else _log.Info($"Print server {state.ToString().ToLowerInvariant()}.");

        StateChanged?.Invoke(state, reason);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _dispatcher.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Nothing to wait for on the way up and nothing to hook on the way down.
    /// Who decides when this process ends is the tray menu, not the host. See
    /// <see cref="StartAsync"/>.
    /// </summary>
    private sealed class NoHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
