using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class ChannelTransitionIntegrationTests
{
    [MonitorPipeWireFact]
    public async Task MutedSendsStaySilentDuringChannelEffectShapeChanges()
    {
        const string plugin = "urn:openxlr:test:gain";
        Assert.Contains(PluginCatalog.Refresh(), p => p.Plugin == plugin);
        string directory = Directory.CreateTempSubdirectory("openxlr-transition-").FullName;
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        string? oldPactl = Environment.GetEnvironmentVariable("OPENXLR_TRANSITION_PACTL");
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        Task<ProcessResult>? audio = null;
        try
        {
            var mixes = new[] { new MixDefinition("monitor", "Audible", MixKind.Monitor), new("monitor2", "Closed", MixKind.Monitor) };
            mixer.Build(new MixerConfig
            {
                Mixes = mixes,
                Channels = new[] { "test", "other" }.Select(id => new ChannelDefinition(id, id)
                { Levels = mixes.ToDictionary(m => m.Id, _ => 1.0), MutedIn = mixes.Select(m => m.Id).ToHashSet() }).ToArray(),
            });
            mixer.SetChannelMuted("test", "monitor", false);
            mixer.SetChannelMuted("other", "monitor2", false);
            var insert = new InsertDefinition { Id = "gain", Kind = "lv2", Plugin = plugin, Params = new() { ["gain"] = .5 } };
            mixer.SetInserts("test", [insert]);
            mixer.EnsureCellLevels();

            audio = ProcessRunner.RunAsync("python3", ["-c", """
                import pathlib, struct, subprocess, sys, tempfile, threading, time
                directory = pathlib.Path(sys.argv[1])
                args = ['--format=f32', '--rate=48000', '--channels=2']
                if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
                captures, recorders = [], []
                play = None
                try:
                    for mix in ['monitor', 'monitor2']:
                        capture = tempfile.TemporaryFile(); captures.append(capture)
                        recorders.append(subprocess.Popen(['pw-cat', '--record', *args, '--target', 'OpenXLR_mix_' + mix,
                            '--properties={ stream.capture.sink = true }', '-'], stdout=capture, stderr=subprocess.PIPE))
                    play = subprocess.Popen(['pw-cat', '--playback', *args, '--target', 'OpenXLR_ch_test', '-'], stdin=subprocess.PIPE, stderr=subprocess.DEVNULL)
                    def feed():
                        block = struct.pack('<ff', .25, .25) * 4800
                        try:
                            while True: play.stdin.write(block); play.stdin.flush()
                        except (BrokenPipeError, ValueError): pass
                    writer = threading.Thread(target=feed, daemon=True); writer.start()
                    time.sleep(1)
                    (directory / 'ready').touch()
                    deadline = time.monotonic() + 35
                    while not (directory / 'done').exists():
                        assert time.monotonic() < deadline, 'Transition driver did not finish'
                        time.sleep(.01)
                    time.sleep(.3)
                    for record in recorders: record.terminate(); record.communicate(timeout=5)
                    peaks = []
                    for index, capture in enumerate(captures):
                        capture.seek(0); raw = capture.read()
                        values = struct.unpack('<' + 'f' * (len(raw)//4), raw)
                        assert len(values) > 48000, 'Capture did not run'
                        peaks.append(max(map(abs, values)))
                        if index == 0:
                            assert max(map(abs, values[-24000:])) > .1, 'Reference audio did not recover after the transitions'
                    assert peaks[0] > .1, ('No reference audio', peaks)
                    assert peaks[1] < .0001, ('Muted send leaked during channel shape change', peaks)
                    print('Audible and closed-send peaks:', peaks)
                finally:
                    for p in ([play] if play is not None else []) + recorders:
                        if p.poll() is None: p.kill()
                        p.wait()
                    for capture in captures: capture.close()
                """, directory], TimeSpan.FromSeconds(50));
            Assert.True(SpinWait.SpinUntil(() => File.Exists(Path.Combine(directory, "ready")) || audio.IsCompleted,
                TimeSpan.FromSeconds(5)), "Audio fixture did not start.");
            Assert.False(audio.IsCompleted);
            // Simulate a responsive but busy PulseAudio control path. Sends
            // must remain closed while volume and mute commands catch up.
            string pactl = oldPath!.Split(Path.PathSeparator).Select(p => Path.Combine(p, "pactl")).First(File.Exists);
            ExecutableScript.Write(Path.Combine(directory, "pactl"), """
                case "$1" in
                  load-module)
                    fault="$(dirname "$0")/reject-reload"
                    if [ -f "$fault" ]; then rm "$fault"; exit 1; fi;;
                  set-sink-input-volume) sleep .05;;
                  set-sink-input-mute) [ ! -f "$(dirname "$0")/reject-mute" ] || exit 1;;
                  set-sink-mute)
                    if [ "$2" = OpenXLR_ch_test ] && [ "$3" = 0 ]; then
                      [ ! -f "$(dirname "$0")/reject-open" ] || exit 1
                    fi;;
                esac
                exec "$OPENXLR_TRANSITION_PACTL" "$@"
                """);
            Environment.SetEnvironmentVariable("OPENXLR_TRANSITION_PACTL", pactl);
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + oldPath);
            for (int i = 0; i < 4; i++)
            {
                mixer.SetInserts("test", []);
                Assert.True(mixer.IsChannelMutedIn("test", "monitor2"));
                mixer.SetInserts("test", [insert]);
            }
            mixer.SetInserts("test", []);
            mixer.RenameApplicationChannel("test", "Renamed", _ => null);
            mixer.SetInserts("test", [insert]);
            // A rejected cell write keeps the replacement closed. Neither
            // ordinary master reconciliation nor a failed activation may
            // reopen it; removing the fault must recover without an edit.
            string rejectMute = Path.Combine(directory, "reject-mute");
            string rejectOpen = Path.Combine(directory, "reject-open");
            File.WriteAllText(rejectMute, "");
            mixer.SetInserts("test", []);
            Assert.True(pw.GetSinkMuted("OpenXLR_ch_test"));
            mixer.SyncOwnSinkLevels(out _);
            mixer.EnsureCellLevels();
            Assert.True(pw.GetSinkMuted("OpenXLR_ch_test"));
            mixer.RenameApplicationChannel("test", "Renamed while recovering", _ => null);
            Assert.True(pw.GetSinkMuted("OpenXLR_ch_test"));
            File.WriteAllText(rejectOpen, "");
            File.Delete(rejectMute);
            mixer.EnsureCellLevels();
            Assert.True(pw.GetSinkMuted("OpenXLR_ch_test"));
            File.Delete(rejectOpen);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureCellLevels();
                return !pw.GetSinkMuted("OpenXLR_ch_test");
            }, TimeSpan.FromSeconds(5)), "The restored sends did not reopen their channel.");
            Assert.False(mixer.EnsureCellLevels());
            File.WriteAllText(Path.Combine(directory, "reject-reload"), "");
            Assert.Throws<InvalidOperationException>(() => mixer.RenameApplicationChannel("test", "Failed reload", _ => null));
            Assert.False(pw.GetSinkMuted("OpenXLR_ch_test"));
            File.WriteAllText(Path.Combine(directory, "done"), "");
            ProcessResult result = await audio;
            Assert.True(result.Ok, result.StdoutText + result.Stderr);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("OPENXLR_TRANSITION_PACTL", oldPactl);
            if (audio is not null)
            {
                File.WriteAllText(Path.Combine(directory, "done"), "");
                await audio;
            }
            Directory.Delete(directory, true);
        }
    }
}
