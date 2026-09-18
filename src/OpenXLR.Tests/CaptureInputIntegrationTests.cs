using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void IndependentCaptureSourcesSurviveRecallRenameAndHotplug()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(new MixerConfig { Channels = [new("system", "System")],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor)] });
        pw.CreateNullSink("test_capture_one", "First test input");
        pw.CreateNullSink("test_capture_two", "Second test input");
        uint first = pw.CreateVirtualMic("test_source_one", "test_capture_one.monitor", "First source");
        pw.CreateVirtualMic("test_source_two", "test_capture_two.monitor", "Second source");
        Wait(() => pw.ListDevices().Count(d => d.Name.StartsWith("test_source_", StringComparison.Ordinal)) == 2);
        Assert.Throws<InvalidOperationException>(() => mixer.CreateCaptureChannel("Missing", "missing", 0, _ => null));
        Assert.Throws<IOException>(() => mixer.CreateCaptureChannel("Failed", "test_source_one", 0, _ => "disk full"));
        Assert.DoesNotContain(mixer.Snapshot().Channels, c => c.Name == "Failed");
        Assert.DoesNotContain(mixer.ExportSettings().UserChannels!, c => c.Name == "Failed");
        mixer.CreateCaptureChannel("First", "test_source_one", 0, _ => null);
        mixer.CreateCaptureChannel("Second", "test_source_two", 0, _ => null);
        mixer.CreateCaptureChannel("Missing pair", "test_source_one", 31, _ => null);
        Wait(() => { mixer.EnsureInputFeeds(); return mixer.Snapshot().Channels.Count(c => c.CaptureConnected) == 2; });
        Assert.All(mixer.Snapshot().Channels.Where(c => c.CaptureSource is not null), c => Assert.Contains("monitor", c.MutedIn));
        Assert.False(mixer.HasApplicationChannel("first"));
        Assert.True(mixer.HasEditableChannel("first"));
        Assert.NotNull(CommandValidation.Check(new Command { Cmd = "assignApp", Channel = "first", Identity = "app" }, mixer, _ => null));
        Assert.Null(CommandValidation.Check(new Command { Cmd = "renameChannel", Channel = "first", Name = "New" }, mixer, _ => null));
        Assert.Throws<InvalidOperationException>(() => mixer.DeleteApplicationChannel("system", _ => null));
        Capture(0);
        mixer.SetChannelMuted("first", "monitor", false);
        mixer.SetChannelMuted("second", "monitor", false);
        mixer.SetLevel("first", "monitor", .5);
        mixer.SetLevel("second", "monitor", .8);
        Capture(.1 * Math.Pow(.5, 3) + .2 * Math.Pow(.8, 3));
        int? node = pw.FindNodeId("OpenXLR_ch_first");
        mixer.RenameApplicationChannel("first", "Renamed", _ => null);
        Assert.Equal(node, pw.FindNodeId("OpenXLR_ch_first"));
        MixerSettings saved = mixer.ExportSettings();
        MixerConfig restored = MixerConfig.FromSettings(saved);
        Assert.Equal("test_source_one", restored.Channels.Single(c => c.Id == "first").CaptureSource);
        Assert.Throws<IOException>(() => mixer.DeleteApplicationChannel("first", _ => "disk full"));
        Assert.True(mixer.Snapshot().Channels.Single(c => c.Id == "first").CaptureConnected);
        pw.UnloadModule(first);
        Wait(() => { mixer.EnsureInputFeeds(); return !mixer.Snapshot().Channels.Single(c => c.Id == "first").CaptureConnected; });
        Capture(.2 * Math.Pow(.8, 3));
        pw.CreateVirtualMic("test_source_one", "test_capture_one.monitor", "First source returned");
        Wait(() => { mixer.EnsureInputFeeds(); return mixer.Snapshot().Channels.Single(c => c.Id == "first").CaptureConnected; });
        mixer.ApplySettings(saved);
        Capture(.1 * Math.Pow(.5, 3) + .2 * Math.Pow(.8, 3));
        mixer.DeleteApplicationChannel("first", _ => null);
        Wait(() => pw.FindNodeId("OpenXLR_ch_first") is null);
        Assert.Contains(pw.ListDevices(), d => d.Name == "test_source_one");
        Capture(.2 * Math.Pow(.8, 3));

        static void Wait(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)));
        static void Capture(double expected)
        {
            ProcessResult result = ProcessRunner.Run("python3", ["-c", """
                import struct, subprocess, sys, tempfile
                rate = 48000
                args = ['--format=f32', '--rate=48000', '--channels=2']
                if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
                processes = []
                with tempfile.TemporaryFile() as capture, tempfile.TemporaryFile() as one, tempfile.TemporaryFile() as two:
                    for file, level in [(one, .1), (two, .2)]:
                        file.write(struct.pack('<ff', level, level) * (rate * 3)); file.seek(0)
                    try:
                        record = subprocess.Popen(['pw-cat', '--record', *args, '--target', 'OpenXLR_mix_monitor', '--properties={ stream.capture.sink = true }', '-'], stdout=capture, stderr=subprocess.PIPE)
                        processes.append(record)
                        for name, file in [('test_capture_one', one), ('test_capture_two', two)]:
                            processes.append(subprocess.Popen(['pw-cat', '--playback', *args, '--target', name, '-'], stdin=file, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE))
                        for play in processes[1:]:
                            _, err = play.communicate(timeout=10)
                            assert play.returncode == 0, err.decode()
                        record.terminate(); record.communicate(timeout=5)
                        capture.seek(0); data = capture.read()
                        values = struct.unpack('<' + 'f' * (len(data) // 4), data)
                        assert len(values) > rate * 4, 'Capture too short'
                        # A full second in the middle excludes startup and drain.
                        mid = len(values) // 2
                        steady = values[mid - rate : mid + rate]
                        expected = float(sys.argv[1])
                        matching = sum(abs(v - expected) < .002 for v in steady) / len(steady)
                        assert matching > .99, (expected, max(map(abs, steady)), matching)
                    finally:
                        for process in processes:
                            if process.poll() is None: process.kill()
                            process.wait()
                """, expected.ToString(System.Globalization.CultureInfo.InvariantCulture)], TimeSpan.FromSeconds(20));
            Assert.True(result.Ok, result.StdoutText + result.Stderr);
        }
    }
}
