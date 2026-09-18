using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void FocusedProcessRoutesItsLiveAudioAndRemembersTheAssignment()
    {
        var pw = new PipeWireAdapter();
        using var mixer = new Mixer(pw);
        mixer.Build(new MixerConfig { Channels = [new("system", "System"), new("music", "Music")],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor)] });
        using var stop = new CancellationTokenSource();
        Task<ProcessResult> play = ProcessRunner.RunAsync("python3", ["-c", """
            import subprocess
            args = ['--format=f32', '--rate=48000', '--channels=2']
            if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
            with open('/dev/zero', 'rb') as source:
                subprocess.run(['pw-cat', '--playback', *args, '--target', 'OpenXLR_ch_system',
                    '--properties={ application.name = focus-test application.process.binary = focus-test }', '-'], stdin=source, check=True)
            """], TimeSpan.FromSeconds(15), cancel: stop.Token);
        try
        {
            AudioStream? stream = null;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.SyncStreams();
                stream = pw.ListStreams().FirstOrDefault(s => s.Identity == "focus-test");
                return stream?.ProcessId > 0;
            }, TimeSpan.FromSeconds(5)));
            mixer.RouteFocusedApplication(stream!.ProcessId, "music");
            Assert.True(SpinWait.SpinUntil(() => pw.StreamSinkName(stream.Serial) == "OpenXLR_ch_music", TimeSpan.FromSeconds(3)));
            Assert.Equal("music", mixer.Matcher.Overrides["focus-test"]);
            Assert.Throws<InvalidOperationException>(() => mixer.RouteFocusedApplication(stream.ProcessId, "missing"));
            Assert.Throws<InvalidOperationException>(() => mixer.RouteFocusedApplication(int.MaxValue, "system"));
            Assert.Equal("OpenXLR_ch_music", pw.StreamSinkName(stream.Serial));
        }
        finally { stop.Cancel(); play.GetAwaiter().GetResult(); }
    }
}
