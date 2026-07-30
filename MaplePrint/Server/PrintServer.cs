using System.Collections.Concurrent;
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
    /// <summary>
    /// A job that outlives this has hit a driver prompt or a powered-off
    /// printer. Answer the caller rather than holding its request open.
    /// </summary>
    private static readonly TimeSpan SpoolTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for the printer ahead of us in the queue.</summary>
    private static readonly TimeSpan QueueWaitTimeout = TimeSpan.FromSeconds(45);

    /// <summary>A receipt is a few kB and a label less. Anything this large is a mistake.</summary>
    private const int MaxPayloadBytes = 8 * 1024 * 1024;

    private readonly ActivityLog _log;

    /// <summary>
    /// One gate per Windows queue. Two jobs interleaved on one printer produce
    /// half of each receipt, and a shop that prints from two tills at once will
    /// find that out at the worst moment.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _queueGates = new(StringComparer.OrdinalIgnoreCase);

    private WebApplication? _app;
    private AppSettings _settings;

    public PrintServer(AppSettings settings, ActivityLog log)
    {
        _settings = settings;
        _log = log;
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

            if (string.IsNullOrWhiteSpace(request.PayloadBase64))
                return Fail(400, JobId(request.JobId), request.DestinationName, null, "payloadBase64 is required.");

            byte[] payload;
            try { payload = Convert.FromBase64String(request.PayloadBase64); }
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
                request.Description, request.JobId, request.Copies);
        });

        // Raw form: POST the bytes themselves. Exists so a technician can prove
        // the whole path with one curl and a file, without base64-ing anything.
        app.MapPost("/print/{destination}", async (HttpContext ctx, string destination) =>
        {
            if (!Authorized(ctx)) return Unauthorized();

            var payload = await ReadBodyAsync(ctx);
            if (payload is null)
                return Fail(400, JobId(null), destination, null,
                    $"Body was empty or larger than {MaxPayloadBytes / 1024}kB.");

            return await ExecuteAsync(
                destination,
                ContentTypeFromHeader(ctx.Request.ContentType),
                payload,
                ctx.Request.Headers["X-MaplePrint-Description"],
                ctx.Request.Headers["X-MaplePrint-Job-Id"],
                copies: 1);
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
            d.Enabled)).ToList();

        return Results.Ok(new DestinationsResponse(_settings.StationName, destinations, queues));
    }

    /// <summary>Resolve, validate, serialise, spool, answer. The whole job.</summary>
    private async Task<IResult> ExecuteAsync(
        string? destinationName, string? contentType, byte[] payload,
        string? description, string? jobId, int copies)
    {
        var id = JobId(jobId);
        var label = string.IsNullOrWhiteSpace(description) ? (destinationName ?? "job") : description!;

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

        if (actual != expected)
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

        // One printer, one job at a time.
        var gate = _queueGates.GetOrAdd(destination.Queue, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(QueueWaitTimeout))
            return Refuse(503, id, destination.Name, destination.Queue, label,
                $"'{destination.Queue}' is still busy with an earlier job.");

        try
        {
            var error = await SpoolAsync(destination, actual, payload, label, copies);

            if (error is null)
            {
                _log.Success($"Printed {label} -> {destination.Name} ({destination.Queue}, {payload.Length} bytes).");
                return Results.Ok(new PrintResponse(id, true, destination.Name, destination.Queue));
            }

            _log.Error($"Failed {label} -> {destination.Name}: {error}");

            // 502: we understood the request, the device let us down. Lets a
            // caller distinguish "retry me" from "your request was wrong" (4xx).
            return Fail(502, id, destination.Name, destination.Queue, error);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string?> SpoolAsync(Destination destination, string contentType, byte[] payload, string label, int copies)
    {
        try
        {
            // Spooling blocks; keep it off the request thread so a wedged driver
            // can't starve the whole listener.
            var work = Task.Run(() => contentType == ContentTypes.Image
                ? PrintImage(destination, payload, label, copies)
                : RawPrinter.Send(destination.Queue, payload, label));

            var finished = await Task.WhenAny(work, Task.Delay(SpoolTimeout));
            return finished == work
                ? await work
                : $"'{destination.Queue}' did not accept the job within {SpoolTimeout.TotalSeconds:0}s.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
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
        if (buffer.Length == 0 || buffer.Length > MaxPayloadBytes) return null;
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

    public async ValueTask DisposeAsync() => await StopAsync();
}
