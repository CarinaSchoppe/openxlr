using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace OpenXLR.Daemon;

/// <summary>Lock-free progress marker: a failed poll is alive; a poll that never returns is not.</summary>
internal sealed class ServiceProgress(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _last = (clock ?? TimeProvider.System).GetTimestamp();

    public void Mark() => Interlocked.Exchange(ref _last, _clock.GetTimestamp());
    public bool IsRecent(TimeSpan limit) => _clock.GetElapsedTime(Interlocked.Read(ref _last)) < limit;
}

/// <summary>
/// Reports readiness and progress to systemd, independent of the UI. No helper
/// processes or blocking health probes are created. Without NOTIFY_SOCKET this
/// service does nothing, so manually launched daemons remain supported.
/// </summary>
internal sealed class ServiceWatchdog(
    DeviceManager devices, MixerService mixer, IHostApplicationLifetime lifetime,
    ILogger<ServiceWatchdog> log) : BackgroundService
{
    internal static TimeSpan? WatchdogInterval(string? usec, string? pid, int currentPid)
    {
        if (pid is not null && (!int.TryParse(pid, out int owner) || owner != currentPid)) return null;
        return long.TryParse(usec, NumberStyles.None, CultureInfo.InvariantCulture, out long value)
               && value >= 3_000 && value <= TimeSpan.MaxValue.Ticks / 10
            ? TimeSpan.FromTicks(value * 10) : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? address = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(address)) return;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => ready.TrySetResult());
        try
        {
            await ready.Task.WaitAsync(stoppingToken);
            await NotifyAsync(address, "READY=1", stoppingToken);
            TimeSpan? interval = WatchdogInterval(Environment.GetEnvironmentVariable("WATCHDOG_USEC"),
                Environment.GetEnvironmentVariable("WATCHDOG_PID"), Environment.ProcessId);
            if (interval is null) return;
            log.LogInformation("systemd watchdog active ({Seconds}s deadline)", interval.Value.TotalSeconds);
            // Normal configuration: poll progress every 20s, require progress
            // within 30s, and let systemd enforce 60s without a healthy heartbeat.
            using var timer = new PeriodicTimer(interval.Value / 3);
            do
            {
                if (devices.Progress.IsRecent(interval.Value / 2) && mixer.IsResponsive(interval.Value / 2))
                    await NotifyAsync(address, "WATCHDOG=1", stoppingToken);
                else
                    log.LogWarning("watchdog heartbeat withheld: device or mixer loop stopped making progress");
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task NotifyAsync(string address, string message, CancellationToken stop)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            var endpoint = new UnixDomainSocketEndPoint(address[0] == '@' ? "\0" + address[1..] : address);
            await socket.SendToAsync(Encoding.UTF8.GetBytes(message), SocketFlags.None, endpoint, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or OperationCanceledException)
        {
            if (!stop.IsCancellationRequested) log.LogWarning("systemd notification failed: {Message}", ex.Message);
        }
    }
}
