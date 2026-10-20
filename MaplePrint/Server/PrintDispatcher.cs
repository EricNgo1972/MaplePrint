using System.Collections.Concurrent;
using System.Threading.Channels;

using MaplePrint.Settings;

namespace MaplePrint.Server;

/// <summary>
/// One job on its way to one printer.
///
/// <para><see cref="DeviceKey"/> is what decides which queue it joins, and it is
/// <b>transport identity</b> — the Windows queue, and later a host:port or a
/// device path. Not the destination name, not the station: two destinations
/// pointed at one printer are still one printer, and keying on anything else
/// would let them collide on the wire.</para>
/// </summary>
public sealed class PrintJob
{
    public required string DeviceKey { get; init; }

    /// <summary>The device as a person would name it, for messages.</summary>
    public required string DeviceLabel { get; init; }

    public required Destination Destination { get; init; }
    public required string ContentType { get; init; }
    public required byte[] Payload { get; init; }
    public required bool OpenDrawer { get; init; }
    public required string JobId { get; init; }

    /// <summary>
    /// True when <see cref="JobId"/> came from the caller. Only then does it mean
    /// anything across a retry — an id we minted is unique per request, so it can
    /// never match a second attempt at the same job.
    /// </summary>
    public required bool CallerSuppliedJobId { get; init; }

    public required string Label { get; init; }
    public required int Copies { get; init; }

    public string? User { get; init; }
    public string? Reason { get; init; }

    /// <summary>Empty payload plus <see cref="OpenDrawer"/> — a no-sale.</summary>
    public bool IsDrawerOnly => Payload.Length == 0 && OpenDrawer;

    /// <summary>Set by whoever executes the job: did the drawer actually fire?</summary>
    public bool DrawerOpened { get; set; }

    internal TaskCompletionSource<string?> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _started;

    /// <summary>
    /// True once the consumer has picked this job up. It is what separates "the
    /// printer took too long" from "we never got to it" when a caller's wait runs
    /// out — 502 and 503 respectively, which is a distinction callers act on.
    /// </summary>
    public bool Started => Volatile.Read(ref _started) != 0;

    internal void MarkStarted() => Volatile.Write(ref _started, 1);
}

public enum DispatchOutcome
{
    /// <summary>Reached the printer, for better or worse. <see cref="DispatchResult.Error"/> says which.</summary>
    Done,

    /// <summary>Never started — the device is still working through earlier jobs.</summary>
    Busy,

    /// <summary>Started, then ran out of time at the device.</summary>
    TimedOut
}

public sealed record DispatchResult(DispatchOutcome Outcome, string? Error = null)
{
    public static DispatchResult Done(string? error) => new(DispatchOutcome.Done, error);
    public static DispatchResult Busy(string? error) => new(DispatchOutcome.Busy, error);
    public static DispatchResult TimedOut(string error) => new(DispatchOutcome.TimedOut, error);
}

/// <summary>
/// Serialises jobs per printer: one <see cref="Channel{T}"/> per device key with
/// a single consumer draining it.
///
/// <para>Two jobs interleaved on one printer produce half of each receipt, and a
/// shop printing from two tills at once finds that out at the worst moment. The
/// same hazard is worse with a drawer attached, since a mangled job may still
/// have fired the till.</para>
///
/// <para>The caller <b>waits for its own job to finish</b> rather than being told
/// "queued". A print API that answers before the paper moves is no use to
/// whoever is standing at the counter, and it is actively harmful for a drawer:
/// the one thing the caller needs to know is whether the till opened.</para>
///
/// <para>Bounded, because an unbounded queue behind a wedged printer grows until
/// the till runs out of memory. Full means busy, which is the answer callers
/// already understand.</para>
/// </summary>
public sealed class PrintDispatcher : IAsyncDisposable
{
    /// <summary>Jobs that may wait per device before new ones are refused.</summary>
    public const int QueueDepth = 32;

    /// <summary>How long to wait for the printer ahead of us in the queue.</summary>
    public static readonly TimeSpan QueueWaitTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// A job that outlives this has hit a driver prompt or a powered-off
    /// printer. Answer the caller rather than holding its request open.
    /// </summary>
    public static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<PrintJob, CancellationToken, Task<string?>> _execute;
    private readonly ConcurrentDictionary<string, Device> _devices = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();

    public PrintDispatcher(Func<PrintJob, CancellationToken, Task<string?>> execute) => _execute = execute;

    /// <summary>Devices seen so far. Diagnostics only.</summary>
    public int DeviceCount => _devices.Count;

    public async Task<DispatchResult> DispatchAsync(PrintJob job, CancellationToken ct = default)
    {
        if (_stopping.IsCancellationRequested)
            return DispatchResult.Busy("The print server is shutting down.");

        var device = _devices.GetOrAdd(job.DeviceKey, CreateDevice);

        using var queueWait = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
        queueWait.CancelAfter(QueueWaitTimeout);

        try
        {
            while (!device.Channel.Writer.TryWrite(job))
            {
                if (!await device.Channel.Writer.WaitToWriteAsync(queueWait.Token).ConfigureAwait(false))
                    return DispatchResult.Busy("The print server is shutting down.");
            }
        }
        catch (OperationCanceledException)
        {
            return DispatchResult.Busy(null);
        }

        // Its turn, then its time at the device. Both, because a job can wait for
        // either reason and the caller is owed an answer within the sum.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(QueueWaitTimeout + JobTimeout);

        var finished = await Task.WhenAny(
            job.Completion.Task,
            Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token)).ConfigureAwait(false);

        if (finished != job.Completion.Task)
        {
            return job.Started
                ? DispatchResult.TimedOut($"'{job.DeviceLabel}' did not finish the job in time.")
                : DispatchResult.Busy(null);
        }

        return DispatchResult.Done(await job.Completion.Task.ConfigureAwait(false));
    }

    private Device CreateDevice(string key)
    {
        var channel = Channel.CreateBounded<PrintJob>(new BoundedChannelOptions(QueueDepth)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        return new Device(channel, Task.Run(() => ConsumeAsync(channel.Reader)));
    }

    /// <summary>
    /// One job at a time, forever. Nothing thrown by a job may escape: a dead
    /// consumer is a printer that silently stops working for the life of the
    /// process, and nobody would know which one.
    /// </summary>
    private async Task ConsumeAsync(ChannelReader<PrintJob> reader)
    {
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var job))
            {
                job.MarkStarted();

                string? error;
                try
                {
                    error = await RunAsync(job).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                job.Completion.TrySetResult(error);
            }
        }
    }

    private async Task<string?> RunAsync(PrintJob job)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        cts.CancelAfter(JobTimeout);

        var work = _execute(job, cts.Token);

        var finished = await Task.WhenAny(work, Task.Delay(JobTimeout, _stopping.Token)).ConfigureAwait(false);
        if (finished != work)
            return $"'{job.DeviceLabel}' did not accept the job within {JobTimeout.TotalSeconds:0}s.";

        return await work.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();

        foreach (var device in _devices.Values)
            device.Channel.Writer.TryComplete();

        // Give consumers a moment to finish what they are holding, then let go.
        // A shop closing down must not wait on a printer that is not answering.
        try
        {
            await Task.WhenAny(
                Task.WhenAll(_devices.Values.Select(d => d.Consumer)),
                Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }
        catch { }

        foreach (var device in _devices.Values)
            while (device.Channel.Reader.TryRead(out var stranded))
                stranded.Completion.TrySetResult("The print server stopped before this job was sent.");

        _devices.Clear();
        _stopping.Dispose();
    }

    private sealed record Device(Channel<PrintJob> Channel, Task Consumer);
}
