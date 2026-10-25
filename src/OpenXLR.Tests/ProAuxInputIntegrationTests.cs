using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    private const string ProAuxSource = "alsa_input.usb-Elgato_Wave_XLR_Pro_AUXTEST-00.multichannel-input";
    private const string ProAuxSink = "pro_aux_signal";

    [MonitorPipeWireFact]
    public async Task ProAuxInputCarriesUsbAuxAndLineInWithoutTheReturnedMixes()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        (uint parent, uint source) = LoadProAuxSource(pw, 18);
        using var stop = new CancellationTokenSource();
        Task<ProcessResult>? signal = null;
        try
        {
            mixer.SetInputDeviceHint(["Wave_XLR_Pro_AUXTEST-"], hardwareOutputRouting: true, modelFallback: false);
            mixer.SetInputJacks(2, true);
            mixer.Build(MixerConfig.Default());
            WaitForRecovery(() => { mixer.EnsureInputFeeds(); return GraphLinksInto("OpenXLR_ch_aux").Length == 4; });
            Assert.Equal(new[] { 4, 5, 6, 7 }.Select(i => ProAuxSource + ":capture_AUX" + i),
                GraphLinksInto("OpenXLR_ch_aux").Select(l => l.From).Order());
            Assert.Contains("auxout", Strip(mixer, "aux").MutedIn);
            Assert.Equal(0, Strip(mixer, "aux").Levels["auxout"]);
            signal = ProcessRunner.RunAsync("python3", ["-c", """
                import struct, subprocess
                args = ['--format=f32', '--rate=48000', '--channels=18']
                if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
                # XLR inputs and the five returned hardware mixes must not leak into Aux In.
                frame = [.7] * 18
                frame[4:8] = [.1, .2, .3, .4]
                p = subprocess.Popen(['pw-cat', '--playback', *args, '--properties={ node.name = OpenXLR_pro_aux_fixture node.link-group = openxlr-test-playback }',
                    '--target', 'pro_aux_signal', '-'], stdin=subprocess.PIPE)
                try:
                    block = struct.pack('<18f', *frame) * 2048
                    for _ in range(0, 48000 * 60, 2048): p.stdin.write(block)
                    p.stdin.close()
                    p.wait(timeout=5)
                finally:
                    if p.poll() is None: p.kill()
                    p.wait()
                """], TimeSpan.FromSeconds(70), cancel: stop.Token);
            AssertProAuxSound(.4, .6);
            mixer.SetChannelMuted("aux", "monitor", false);
            mixer.SetLevel("aux", "monitor", .5);
            MixerSettings saved = mixer.ExportSettings();
            mixer.Build(MixerConfig.FromSettings(saved));
            mixer.ApplySettings(saved);
            WaitForRecovery(() => { mixer.EnsureInputFeeds(); return GraphLinksInto("OpenXLR_ch_aux").Length == 4; });
            Assert.DoesNotContain("monitor", Strip(mixer, "aux").MutedIn);
            Assert.Equal(.5, Strip(mixer, "aux").Levels["monitor"]);
            AssertProAuxSound(.4, .6);
            AssertProAuxSound(.4 * Math.Pow(.5, 3), .6 * Math.Pow(.5, 3), "OpenXLR_mix_monitor");
        }
        finally
        {
            stop.Cancel();
            if (signal is not null) await signal;
            mixer.TearDown();
            UnloadModule(source);
            UnloadModule(parent);
        }
    }

    [MonitorPipeWireFact]
    public void ProAuxInputFollowsTheRawPairsWhenTheCaptureProfileChanges()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        uint parent = 0, source = 0;
        try
        {
            mixer.SetInputDeviceHint(["Wave_XLR_Pro_AUXTEST-"], hardwareOutputRouting: true, modelFallback: false);
            mixer.SetInputJacks(2, true);
            mixer.Build(MixerConfig.Default());
            // The same source name returns with fewer or more ports. Old six-channel
            // profiles keep USB Aux; stereo interfaces must never duplicate the mic.
            foreach ((int ports, int links) in new[] { (6, 2), (18, 4), (6, 2), (2, 0), (18, 4) })
            {
                (parent, source) = LoadProAuxSource(pw, ports);
                if (ports == 18 && GraphLinksInto("OpenXLR_ch_aux").Length == 0)
                {
                    // A late fourth pair beside an already healthy USB Aux pair.
                    // Retained links use names, so a replug can recreate that pair
                    // before the next sweep notices the additional ports.
                    WaitForRecovery(() => pw.RouteInputToChannel(ProAuxSource, "OpenXLR_ch_aux", 2).Pairs.Count == 2);
                }
                WaitForRecovery(() => { mixer.EnsureInputFeeds(); return GraphLinksInto("OpenXLR_ch_aux").Length == links; });
                Assert.All(GraphLinksInto("OpenXLR_ch_aux"), l =>
                    Assert.Contains(l.From, Enumerable.Range(4, links).Select(i => ProAuxSource + ":capture_AUX" + i)));
                if (links == 4)
                {
                    pw.Run("pw-link", "-d", ProAuxSource + ":capture_AUX7", "OpenXLR_ch_aux:playback_FR");
                    WaitForRecovery(() => { mixer.EnsureInputFeeds(); return GraphLinksInto("OpenXLR_ch_aux").Length == 4; });
                }
                UnloadModule(source);
                UnloadModule(parent);
                source = parent = 0;
                WaitForRecovery(() => pw.FindNodeId(ProAuxSource) is null);
            }
        }
        finally
        {
            mixer.TearDown();
            if (source != 0) UnloadModule(source);
            if (parent != 0) UnloadModule(parent);
        }
    }

    [MonitorPipeWireFact]
    public void ProAuxInputNeverUsesAMixReturnWhileALineInPortIsMissing()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        (uint parent, uint source) = LoadProAuxSource(pw, 18);
        string directory = Directory.CreateTempSubdirectory("openxlr-aux-ports-").FullName;
        string? path = Environment.GetEnvironmentVariable("PATH");
        string realLink = (path ?? "").Split(Path.PathSeparator)
            .Select(p => Path.GetFullPath(Path.Combine(p, "pw-link"))).First(File.Exists);
        try
        {
            mixer.SetInputDeviceHint(["Wave_XLR_Pro_AUXTEST-"], hardwareOutputRouting: true, modelFallback: false);
            mixer.SetInputJacks(2, true);
            ExecutableScript.Write(Path.Combine(directory, "pw-link"), $$"""
                #!/bin/sh
                exec python3 - "$@" <<'PY'
                import os, subprocess, sys
                real = {{System.Text.Json.JsonSerializer.Serialize(realLink)}}
                if sys.argv[1:] == ['-o']:
                    listing = subprocess.check_output([real, '-o'], text=True)
                    print('\n'.join(line for line in listing.splitlines()
                        if line.strip() != '{{ProAuxSource}}:capture_AUX7'))
                else: os.execv(real, ['pw-link', *sys.argv[1:]])
                PY
                """);
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + path);
            mixer.Build(MixerConfig.Default());
            WaitForRecovery(() => GraphLinksInto("OpenXLR_ch_aux").Length == 3);
            Assert.Equal(new[] { 4, 5, 6 }.Select(i => ProAuxSource + ":capture_AUX" + i),
                GraphLinksInto("OpenXLR_ch_aux").Select(l => l.From).Order());
            Environment.SetEnvironmentVariable("PATH", path);
            WaitForRecovery(() => { mixer.EnsureInputFeeds(); return GraphLinksInto("OpenXLR_ch_aux").Length == 4; });
            Assert.Contains(GraphLinksInto("OpenXLR_ch_aux"), l => l.From == ProAuxSource + ":capture_AUX7");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            mixer.TearDown();
            UnloadModule(source);
            UnloadModule(parent);
            Directory.Delete(directory, true);
        }
    }

    [MonitorPipeWireFact]
    public void ProAuxInputRetriesARefusedLineInLinkBesideTheUsbAuxLinks()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        (uint parent, uint source) = LoadProAuxSource(pw, 18);
        try
        {
            mixer.SetInputDeviceHint(["Wave_XLR_Pro_AUXTEST-"], hardwareOutputRouting: true, modelFallback: false);
            mixer.SetInputJacks(2, true);
            RejectNextRouteLink(ProAuxSource + ":capture_AUX7", "OpenXLR_ch_aux:playback_FR",
                () => mixer.Build(MixerConfig.Default()));
            Assert.Contains(GraphLinksInto("OpenXLR_ch_aux"), l => l.From == ProAuxSource + ":capture_AUX4");
            WaitForRecovery(() => { mixer.EnsureInputFeeds(); return GraphLinksInto("OpenXLR_ch_aux").Length == 4; });
            Stable(() => mixer.EnsureInputFeeds(), "OpenXLR_ch_aux", 4);
            string directory = Directory.CreateTempSubdirectory("openxlr-aux-sweep-").FullName;
            string? path = Environment.GetEnvironmentVariable("PATH");
            try
            {
                ExecutableScript.Write(Path.Combine(directory, "pw-link"), """
                    printf called >> "${0%/*}/calls"
                    exit 1
                    """);
                Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + path);
                for (int i = 0; i < 10; i++) Assert.False(mixer.EnsureInputFeeds());
                Assert.False(File.Exists(Path.Combine(directory, "calls")));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", path);
                Directory.Delete(directory, true);
            }
        }
        finally
        {
            mixer.TearDown();
            UnloadModule(source);
            UnloadModule(parent);
        }
    }

    private static (uint Parent, uint Source) LoadProAuxSource(PipeWireAdapter pw, int ports)
    {
        string map = string.Join(',', Enumerable.Range(0, ports).Select(i => "aux" + i));
        uint parent = LoadModule("module-null-sink", "sink_name=" + ProAuxSink, "channels=" + ports,
            "channel_map=" + map, "sink_properties=device.description=Pro-Aux-test");
        uint? source = null;
        try
        {
            WaitForRecovery(() => ProcessRunner.Run("pactl", ["list", "sources", "short"]).StdoutText.Contains(ProAuxSink + ".monitor", StringComparison.Ordinal));
            source = LoadModule("module-remap-source", "source_name=" + ProAuxSource, "master=" + ProAuxSink + ".monitor",
                "channels=" + ports, "channel_map=" + map, "master_channel_map=" + map, "remix=no",
                "source_properties=device.description=Pro-Aux-test");
            WaitForRecovery(() => pw.ListDevices().Any(d => d.Name == ProAuxSource && d.Channels == ports));
            return (parent, source.Value);
        }
        catch
        {
            if (source is uint module) UnloadModule(module);
            UnloadModule(parent);
            throw;
        }
    }

    private static void AssertProAuxSound(double left, double right, string target = "OpenXLR_ch_aux")
    {
        ProcessResult captured = ProcessRunner.Run("python3", ["-c", """
            import math, os, struct, subprocess, sys, tempfile, time
            args = ['--format=f32', '--rate=48000', '--channels=2']
            if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
            with tempfile.TemporaryFile() as capture:
                p = subprocess.Popen(['pw-cat', '--record', *args, '--properties={ stream.capture.sink = true node.link-group = openxlr-test-record }',
                    '--target', sys.argv[3], '-'], stdout=capture, stderr=subprocess.PIPE)
                try:
                    expected = [float(v) for v in sys.argv[1:3]]
                    deadline = time.monotonic() + 5
                    last = 'no captured samples'
                    while time.monotonic() < deadline:
                        size = os.fstat(capture.fileno()).st_size // 8 * 8
                        if size >= 12000 * 8:
                            tail = struct.unpack('<24000f', os.pread(capture.fileno(), 12000 * 8, size - 12000 * 8))
                            last = [sum(abs(v - e) < .002 for v in tail[c::2]) / 12000 for c, e in enumerate(expected)]
                            if all(map(math.isfinite, tail)) and min(last) > .99: break
                        if p.poll() is not None: raise AssertionError(p.communicate()[1].decode())
                        time.sleep(.02)
                    else: raise AssertionError(('no stereo Aux audio', expected, last))
                finally:
                    if p.poll() is None: p.kill()
                    p.wait()
            """, left.ToString(System.Globalization.CultureInfo.InvariantCulture), right.ToString(System.Globalization.CultureInfo.InvariantCulture), target],
            TimeSpan.FromSeconds(10));
        Assert.True(captured.Ok, captured.StdoutText + captured.Stderr);
    }
}
