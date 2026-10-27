using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void AutomaticRoutingCanLeavePlaybackToTheDesktopWithoutLosingExplicitAssignments()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(new MixerConfig { Channels = [new("system", "System"), new("music", "Music")],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor)] });
        pw.CreateNullSink("routing_desktop", "Desktop output");
        pw.SetDefaultSink("routing_desktop");
        mixer.SetRouteAppsByDefault(false);
        using var stop = new CancellationTokenSource();
        Task<ProcessResult> play = ProcessRunner.RunAsync("python3", ["-c", """
            import subprocess
            args = ['--format=f32', '--rate=48000', '--channels=2']
            if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
            with open('/dev/zero', 'rb') as source:
                subprocess.run(['pw-cat', '--playback', *args, '--target', 'routing_desktop',
                    '--properties={ application.name = routing-policy-test application.process.binary = routing-policy-test }', '-'], stdin=source, check=True)
            """], TimeSpan.FromSeconds(30), cancel: stop.Token);
        try
        {
            WaitFor(StreamMatcher.Ignore, "routing_desktop");
            mixer.SetRouteAppsByDefault(true);
            WaitFor("system", "OpenXLR_ch_system");
            mixer.SetRouteAppsByDefault(false);
            WaitFor(StreamMatcher.Ignore, "routing_desktop");
            mixer.AssignApp("routing-policy-test", "music");
            WaitFor("music", "OpenXLR_ch_music");
            mixer.SetRouteAppsByDefault(true);
            WaitFor("music", "OpenXLR_ch_music");
            mixer.SetRouteAppsByDefault(false);
            WaitFor("music", "OpenXLR_ch_music");
            // An OpenXLR default remains a valid desktop destination for an
            // unmanaged app. Ignoring an app does not silence it.
            pw.SetDefaultSink("OpenXLR_ch_system");
            mixer.ForgetApp("routing-policy-test");
            WaitFor(StreamMatcher.Ignore, "OpenXLR_ch_system");

            void WaitFor(string channel, string sink)
            {
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    mixer.SyncStreams();
                    StreamAssignment? stream = mixer.Streams.FirstOrDefault(s => s.Identity == "routing-policy-test");
                    return stream?.ChannelId == channel && pw.StreamSinkName(stream.Serial) == sink;
                }, TimeSpan.FromSeconds(4)), $"Playback did not reach {channel} on {sink}");
            }
        }
        finally { stop.Cancel(); play.GetAwaiter().GetResult(); }
    }
}
