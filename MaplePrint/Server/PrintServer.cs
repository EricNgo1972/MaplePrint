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
/// The local print API. Callers POST device bytes, MaplePrint spools them and
/// says what happened — that is the entire contract.
///
/// MaplePrint knows nothing about who is calling. It has no server URL, no
/// account, and no outbound connection: whoever wants to print is configured
/// with *our* address, not the reverse. That keeps this useful to MapleCoffee,
/// to the Laravel POS, and to a one-line curl in a support call, without a line
/// of code changing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PrintServer : IAsyncDisposable
{
    /// <summary>
    /// A job that outlives this has hit a driver prompt or a powered-off
    /// printer. Answer the caller rather than holding its request open.
    /// </summary>
    private static readonly TimeSpan SpoolTimeout = TimeSpan.FromSeconds(15);

    /// <summary>A receipt is a few kB and a label less. Anything this large is a mistake.</summary>
    private const int MaxPayloadBytes = 4 * 1024 * 1024;

    private readonly ActivityLog _log;
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
            SetState(ServerState.Stopped, "No API key yet — open Settings to generate one.");
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
            Results.Ok(new HealthResponse("ok", _settings.StationName, Version)));

        app.MapGet("/printers", (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Unauthorized();

            return Results.Ok(new PrintersResponse(
                _settings.StationName,
                PrinterQueues.Installed(),
                NullIfBlank(_settings.ReceiptQueue),
                NullIfBlank(_settings.LabelQueue)));
        });

        // JSON form: the general case, and what an app integrates against.
        app.MapPost("/print", async (HttpContext ctx, PrintRequest request) =>
        {
            if (!Authorized(ctx)) return Unauthorized();

            if (string.IsNullOrWhiteSpace(request.PayloadBase64))
                return Results.BadRequest(new PrintResponse(
                    JobId(request.JobId), false, null, "payloadBase64 is required."));

            byte[] payload;
            try { payload = Convert.FromBase64String(request.PayloadBase64); }
            catch (FormatException)
            {
                return Results.BadRequest(new PrintResponse(
                    JobId(request.JobId), false, null, "payloadBase64 was not valid base64."));
            }

            return await ExecuteAsync(request.Target, payload, request.Description, request.JobId);
        });

        // Raw form: POST the bytes themselves. Exists so a technician can prove
        // the whole path with one curl and a file, without base64-ing anything.
        app.MapPost("/print/{target}", async (HttpContext ctx, string target) =>
        {
            if (!Authorized(ctx)) return Unauthorized();

            var payload = await ReadBodyAsync(ctx);
            if (payload is null)
                return Results.BadRequest(new PrintResponse(
                    JobId(null), false, null, $"Body was empty or larger than {MaxPayloadBytes / 1024}kB."));

            return await ExecuteAsync(target, payload, ctx.Request.Headers["X-MaplePrint-Description"], null);
        });
    }

    /// <summary>Resolve the target to a queue, spool, answer. The whole job.</summary>
    private async Task<IResult> ExecuteAsync(string? target, byte[] payload, string? description, string? jobId)
    {
        var id = JobId(jobId);
        var label = string.IsNullOrWhiteSpace(description) ? (target ?? "job") : description!;

        var queue = ResolveQueue(target);
        if (string.IsNullOrWhiteSpace(queue))
        {
            var error = $"No printer is mapped to '{target}' on this station. Set it in MaplePrint → Settings.";
            _log.Error($"Rejected {label}: {error}");
            return Results.Json(new PrintResponse(id, false, null, error), statusCode: 404);
        }

        if (!PrinterQueues.Exists(queue))
        {
            var error = $"The queue '{queue}' is not installed on this PC.";
            _log.Error($"Rejected {label}: {error}");
            return Results.Json(new PrintResponse(id, false, queue, error), statusCode: 404);
        }

        string? failure;
        try
        {
            // Spooling is blocking; keep it off the request thread so a wedged
            // driver can't starve the whole listener.
            var send = Task.Run(() => RawPrinter.Send(queue, payload, label));
            var finished = await Task.WhenAny(send, Task.Delay(SpoolTimeout));
            failure = finished == send
                ? await send
                : $"The queue '{queue}' did not accept the job within {SpoolTimeout.TotalSeconds:0}s.";
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        if (failure is null)
        {
            _log.Success($"Printed {label} → {queue} ({payload.Length} bytes).");
            return Results.Ok(new PrintResponse(id, true, queue));
        }

        _log.Error($"Failed {label} → {queue}: {failure}");
        // 502: we understood the request, the device let us down. Lets a caller
        // distinguish "retry me" from "your request was wrong" (4xx).
        return Results.Json(new PrintResponse(id, false, queue, failure), statusCode: 502);
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpContext ctx)
    {
        using var buffer = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(buffer);
        if (buffer.Length == 0 || buffer.Length > MaxPayloadBytes) return null;
        return buffer.ToArray();
    }

    private string ResolveQueue(string? target) => target?.Trim().ToLowerInvariant() switch
    {
        null or "" => "",
        PrintTargets.Receipt => _settings.ReceiptQueue,
        PrintTargets.Label => _settings.LabelQueue,
        _ => target!.Trim()
    };

    /// <summary>
    /// Accepts the key as a bearer token or an <c>X-MaplePrint-Key</c> header —
    /// whichever the calling stack finds natural. Compared in fixed time,
    /// because this endpoint is reachable from the LAN and a timing oracle on a
    /// 32-byte key is a real, if unglamorous, way to lose it.
    /// </summary>
    private bool Authorized(HttpContext ctx)
    {
        var expected = _settings.ApiKey;
        if (string.IsNullOrWhiteSpace(expected)) return false;

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

    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

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
