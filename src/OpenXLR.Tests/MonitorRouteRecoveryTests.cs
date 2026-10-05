using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void PartialMonitorRoutesRetryRejectedLinksAndAcceptReplacementMonoOutputs()
    {
        const string output = "test_monitor_partial";
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(new MixerConfig { Channels = [], Mixes = [
            new("monitor", "Monitor", MixKind.Monitor), new("chat", "Chat", MixKind.VirtualMic)] });
        uint stereo = pw.CreateNullSink(output, "Partial monitor output");
        uint? mono = null;
        try
        {
            WaitForRecovery(() => ProcessRunner.Run("pw-link", ["-i"]).StdoutText.Contains(output + ":playback_FR", StringComparison.Ordinal));
            foreach ((string feed, int links) in new[] { ("monitor", 2), ("monitor+chat", 4) })
            {
                RejectNextRouteLink("OpenXLR_mix_", output + ":playback_FR", () =>
                {
                    if (feed == "monitor") mixer.SetMonitorOutputs([output]);
                    else Assert.Null(mixer.SetMonitorFeed(output, feed));
                });
                WaitForRecovery(() => IncomingRouteLinks(output).Length == links - 1);
                WaitForRecovery(() => { mixer.EnsureMonitorRoutes(); return IncomingRouteLinks(output).Length == links; });
                Stable(() => mixer.EnsureMonitorRoutes(), output, links);
            }

            pw.UnloadModule(stereo);
            WaitForRecovery(() => pw.FindNodeId(output) is null);
            mixer.EnsureMonitorRoutes();
            ProcessResult loaded = ProcessRunner.Run("pactl", ["load-module", "module-null-sink",
                "sink_name=" + output, "channels=1", "channel_map=mono"]);
            Assert.True(loaded.Ok, loaded.Stderr);
            mono = uint.Parse(loaded.StdoutText.Trim());
            WaitForRecovery(() => ProcessRunner.Run("pw-link", ["-i"]).StdoutText.Contains(output + ":playback_MONO", StringComparison.Ordinal));
            WaitForRecovery(() => { mixer.EnsureMonitorRoutes(); return IncomingRouteLinks(output).Length == 2; });
            Stable(() => mixer.EnsureMonitorRoutes(), output, 2); // One link per mix is complete for a genuine mono output.
        }
        finally
        {
            if (mono is uint module) ProcessRunner.Run("pactl", ["unload-module", module.ToString()]);
        }
    }
}
