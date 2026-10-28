using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [FlatpakAudioFact]
    public async Task FlatpakAudioProcessIsMatchedThroughItsInstanceAndRoutesWithoutPidCollision()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(new MixerConfig { Channels = [new("system", "System"), new("music", "Music")],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor)] });
        string runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")!;
        using var stop = new CancellationTokenSource();
        Task<OpenXLR.Core.ProcessResult> play = OpenXLR.Core.ProcessRunner.RunAsync("flatpak", ["run", "--command=sh", "--filesystem=" + runtime,
            "--env=PULSE_SERVER=unix:" + runtime + "/pulse/native", "io.github.emaspa.openxlr", "-c",
            "exec timeout 12s paplay --raw --format=float32le --rate=48000 --channels=2 --device=OpenXLR_ch_system /dev/zero"],
            TimeSpan.FromSeconds(15), cancel: stop.Token);
        try
        {
            AudioStream? stream = null;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                stream = pw.ListStreams().FirstOrDefault(s => s.Binary == "pacat" && s.FlatpakInstanceId is not null);
                return stream?.ProcessId > 0;
            }, TimeSpan.FromSeconds(5)));
            string key = stream!.FocusProcessKey;
            int hostPid = 0;
            foreach (string directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), out int pid)) continue;
                try
                {
                    string? ids = File.ReadLines(directory + "/status").FirstOrDefault(line => line.StartsWith("NSpid:", StringComparison.Ordinal));
                    if (ids is null || ids.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[^1] != stream.ProcessId.ToString()) continue;
                    if (File.ReadAllText(directory + "/root/.flatpak-info").Contains("instance-id=" + stream.FlatpakInstanceId + "\n", StringComparison.Ordinal))
                    { hostPid = pid; break; }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            Assert.True(hostPid > 0 && hostPid != stream.ProcessId);
            string matches = await FocusProcesses.MatchKeysAsync(hostPid, FocusProcesses.ParseKeys(stream.ProcessId + "," + key), CancellationToken.None);
            Assert.Equal(key, matches);
            mixer.RouteFocusedAudioProcesses([matches], "music");
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.SyncStreams();
                return pw.StreamSinkName(stream.Serial) == "OpenXLR_ch_music";
            }, TimeSpan.FromSeconds(3)));
            Assert.Equal("music", mixer.Matcher.Overrides[stream.Identity]);
        }
        finally { stop.Cancel(); await play; }
    }
}

internal sealed class FlatpakAudioFactAttribute : FactAttribute
{
    public FlatpakAudioFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_TEST_FLATPAK_AUDIO") != "1"
            || Environment.GetEnvironmentVariable("OPENXLR_TEST_MONITOR_VOLUME") != "1")
            Skip = "Needs an installed OpenXLR Flatpak, FLATPAK_USER_DIR and the private audio runner with OPENXLR_TEST_FLATPAK_AUDIO=1.";
    }
}
