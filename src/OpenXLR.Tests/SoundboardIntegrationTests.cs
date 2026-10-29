using System.Globalization;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The soundboard against a private PipeWire server with the native helper:
/// a clip of constant 0.1 played into SFX is recorded from the Stream and
/// Chat virtual microphones while SFX's mutes, the routing setting, the
/// layout and the helper itself change under it.
/// </summary>
[Collection("xdg-config")]
public sealed class SoundboardIntegrationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-soundboard-audio-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [MonitorPipeWireFact]
    public void AClipPlaysThroughSfxAndFollowsItsChannel()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        var mixer = new Mixer(pw);
        bool disposed = false;
        try
        {
            // Where a stream that lost its sink would end up.
            pw.CreateNullSink("soundboard_default", "Soundboard test default output");
            pw.Run("pactl", "set-default-sink", "soundboard_default");
            static Dictionary<string, double> Sends() => new() { ["monitor"] = 1, ["stream"] = 1, ["chat"] = 1 };
            mixer.Build(new MixerConfig
            {
                Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("stream", "Stream", MixKind.VirtualMic), new("chat", "Chat", MixKind.VirtualMic)],
                Channels = [new("system", "System") { Levels = Sends() }, new("sfx", "SFX") { Levels = Sends() }],
            });
            var saved = new List<SoundboardLibrary>();
            mixer.UseSoundboard(SoundboardLibrary.Empty, next => { saved.Add(next); return null; }, Path.Combine(_directory, "clips"));
            Func<MixerSettings, string?> persist = _ => null;

            mixer.SetSoundboard(true, persist);
            Assert.NotNull(pw.FindNodeId(PipeWireAdapter.PlayerNode));
            Assert.True(saved[^1].Enabled);
            string wav = Path.Combine(_directory, "tone.wav");
            WriteConstantWav(wav, .1, seconds: 2);
            SoundboardClip clip = mixer.ImportClip(wav, "Tone");
            Assert.Equal("tone", clip.Id);
            mixer.UpdateClip("tone", new SoundboardClipChange(Loop: true));
            WaitLoaded(mixer, "tone");
            Assert.Equal(2, Assert.Single(mixer.Soundboard().Clips).Seconds);

            // Nothing plays until the key is pressed.
            AssertLevel(0, "OpenXLR_stream", "before any press");
            mixer.PlayClip("tone", null);
            AssertLevel(.1, "OpenXLR_stream", "the clip through SFX's unity send");
            Assert.True(SpinWait.SpinUntil(() => mixer.ReadMeters().TryGetValue("clip:tone", out double[]? at) && at[1] == 1, TimeSpan.FromSeconds(3)));
            Assert.Equal(["tone"], mixer.Soundboard().Playing);
            Assert.True(mixer.SoundboardActivityChanged());

            // A second press in overlap mode adds a voice; restart replaces them.
            mixer.PlayClip("tone", "overlap");
            AssertLevel(.2, "OpenXLR_stream", "two voices");
            mixer.PlayClip("tone", "restart");
            AssertLevel(.1, "OpenXLR_stream", "restart leaves one voice");

            // SFX's own mute in Chat silences the clip there and nowhere else.
            mixer.SetChannelMuted("sfx", "chat", true);
            AssertLevel(0, "OpenXLR_chat", "SFX muted in Chat");
            AssertLevel(.1, "OpenXLR_stream", "SFX still open in Stream");
            mixer.SetChannelMuted("sfx", "chat", false);

            mixer.StopClips();
            AssertLevel(0, "OpenXLR_stream", "stop all");
            Assert.True(SpinWait.SpinUntil(() => mixer.Soundboard().Playing.Count == 0, TimeSpan.FromSeconds(3)));

            // No routing setting applies to a link the daemon made.
            mixer.SetRouteAppsByDefault(false, persist);
            mixer.PlayClip("tone", null);
            AssertLevel(.1, "OpenXLR_stream", "automatic routing off");

            // A live layout edit that reloads nothing keeps the clip playing.
            mixer.CreateMix("Recorder", persist, MixKind.VirtualMic);
            AssertLevel(.1, "OpenXLR_stream", "a mix created during the clip");

            // A rename reloads SFX's sink; the player follows it by id.
            mixer.RenameApplicationChannel("sfx", "Effects", persist);
            Assert.Equal("sfx", mixer.Soundboard().Channel);
            mixer.PlayClip("tone", "restart");
            AssertLevel(.1, "OpenXLR_stream", "SFX renamed");

            // A helper that dies is started again and loads its clips again.
            var player = (SoundboardPlayer)typeof(Mixer).GetField("_player", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(mixer)!;
            player.Host.Process.Kill();
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureFilterRoutes();
                SoundboardState state = mixer.Soundboard();
                return state.Clips.Single().Loaded && state.Error == "The soundboard player stopped and was started again.";
            }, TimeSpan.FromSeconds(15)), mixer.Soundboard().ToString());
            mixer.PlayClip("tone", null);
            AssertLevel(.1, "OpenXLR_stream", "after the helper was restarted");

            // Deleting SFX mid-clip is silent, and the board says why it is off.
            mixer.DeleteApplicationChannel("sfx", persist);
            AssertLevel(0, "OpenXLR_stream", "SFX deleted mid-clip");
            SoundboardState off = mixer.Soundboard();
            Assert.Null(off.Channel);
            Assert.False(off.Enabled);
            Assert.Equal(Mixer.SoundboardChannelDeleted, off.Error);
            Assert.False(saved[^1].Enabled);
            Assert.Equal(["tone"], saved[^1].Clips.Select(c => c.Id));
            Assert.True(SpinWait.SpinUntil(() => pw.FindNodeId(PipeWireAdapter.PlayerNode) is null, TimeSpan.FromSeconds(3)));
            Assert.Equal(Mixer.SoundboardChannelDeleted, Assert.Throws<InvalidOperationException>(() => mixer.PlayClip("tone", null)).Message);
            mixer.EnsureFilterRoutes();
            Assert.Null(pw.FindNodeId(PipeWireAdapter.PlayerNode));

            // Turning it on again creates SFX, muted in every mix.
            mixer.SetSoundboard(true, persist);
            ChannelStatus sfx = mixer.Snapshot().Channels.Single(c => c.Id == "sfx");
            Assert.Equal("SFX", sfx.Name);
            Assert.Equal(["monitor", "stream", "chat", "recorder"], sfx.MutedIn);
            Assert.Equal("sfx", mixer.Soundboard().Channel);
            Assert.Null(mixer.Soundboard().Error);
            WaitLoaded(mixer, "tone");
            mixer.PlayClip("tone", null);
            AssertLevel(0, "OpenXLR_stream", "a recreated SFX is muted");
            mixer.SetChannelMuted("sfx", "stream", false);
            AssertLevel(.1, "OpenXLR_stream", "the recreated SFX's Stream send opened");

            // An application channel that already holds the id is used as it is.
            mixer.SetSoundboard(false, persist);
            Assert.True(SpinWait.SpinUntil(() => pw.FindNodeId(PipeWireAdapter.PlayerNode) is null, TimeSpan.FromSeconds(3)));
            int channels = mixer.Snapshot().Channels.Count;
            mixer.SetSoundboard(true, persist);
            Assert.Equal(channels, mixer.Snapshot().Channels.Count);
            WaitLoaded(mixer, "tone");

            // A daemon stopped mid-clip leaves no player and nothing on the
            // default output.
            mixer.PlayClip("tone", null);
            AssertLevel(.1, "OpenXLR_stream", "before the stop");
            mixer.Dispose();
            disposed = true;
            Assert.True(SpinWait.SpinUntil(() => pw.FindNodeId(PipeWireAdapter.PlayerNode) is null, TimeSpan.FromSeconds(3)));
            AssertLevel(0, "soundboard_default.monitor", "the default output after the stop");
        }
        finally
        {
            if (!disposed) mixer.Dispose();
            pw.TearDown();
        }
    }

    /// <summary>A graph with Stream and SFX, the board on, and a looping clip of constant 0.1 loaded as <c>tone</c>.</summary>
    private void StartTone(PipeWireAdapter pw, Mixer mixer)
    {
        static Dictionary<string, double> Sends() => new() { ["monitor"] = 1, ["stream"] = 1 };
        mixer.Build(new MixerConfig
        {
            Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("stream", "Stream", MixKind.VirtualMic)],
            Channels = [new("system", "System") { Levels = Sends() }, new("sfx", "SFX") { Levels = Sends() }],
        });
        mixer.UseSoundboard(SoundboardLibrary.Empty, _ => null, Path.Combine(_directory, "clips"));
        mixer.SetSoundboard(true, _ => null);
        string wav = Path.Combine(_directory, "tone.wav");
        WriteConstantWav(wav, .1, seconds: 2);
        mixer.ImportClip(wav, "Tone");
        mixer.UpdateClip("tone", new SoundboardClipChange(Loop: true));
        WaitLoaded(mixer, "tone");
        _ = pw;
    }

    private static SoundboardPlayer? Player(Mixer mixer)
        => (SoundboardPlayer?)typeof(Mixer).GetField("_player", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(mixer);

    [MonitorPipeWireFact]
    public void AChangedGraphRateStartsThePlayerAgainWithItsClips()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        StartTone(pw, mixer);
        int before = Player(mixer)!.Host.SampleRate;
        int other = before == 44100 ? 48000 : 44100;
        mixer.PlayClip("tone", null);
        AssertLevel(.1, "OpenXLR_stream", "before the rate changes");
        try
        {
            pw.Run("pw-metadata", "-n", "settings", "0", "clock.force-rate", other.ToString(CultureInfo.InvariantCulture));
            // The clip was converted to the old rate; the player starts again
            // at the new one and loads it again, converted to that.
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureFilterRoutes();
                return Player(mixer)?.Host.SampleRate == other && mixer.Soundboard().Clips.Single().Loaded;
            }, TimeSpan.FromSeconds(15)), $"{Player(mixer)?.Host.SampleRate} {mixer.Soundboard()}");
            SoundboardState state = mixer.Soundboard();
            Assert.Null(state.Error);
            Assert.Equal(2, state.Clips.Single().Seconds);
            // A restart for the rate is not a failure: three of them never turn the board off.
            Assert.Empty(state.Playing);
            mixer.PlayClip("tone", null);
            AssertLevel(.1, "OpenXLR_stream", "at the new rate");
        }
        finally { pw.Run("pw-metadata", "-n", "settings", "0", "clock.force-rate", "0"); }
        Assert.True(SpinWait.SpinUntil(() =>
        {
            mixer.EnsureFilterRoutes();
            return Player(mixer)?.Host.SampleRate == before && mixer.Soundboard().Clips.Single().Loaded;
        }, TimeSpan.FromSeconds(15)), $"{Player(mixer)?.Host.SampleRate} {mixer.Soundboard()}");
        // A steady rate leaves the running player alone.
        SoundboardPlayer steady = Player(mixer)!;
        for (int sweep = 0; sweep < 3; sweep++) mixer.EnsureFilterRoutes();
        Assert.Same(steady, Player(mixer));
    }

    private static void WaitLoaded(Mixer mixer, string id)
        => Assert.True(SpinWait.SpinUntil(() =>
        {
            mixer.EnsureFilterRoutes();
            return mixer.Soundboard().Clips.Single(c => c.Id == id).Loaded;
        }, TimeSpan.FromSeconds(10)), mixer.Soundboard().ToString());

    /// <summary>A stereo 16-bit WAV at 48 kHz holding one constant value.</summary>
    private static void WriteConstantWav(string path, double value, int seconds)
    {
        const int rate = 48000;
        int frames = rate * seconds;
        short sample = (short)Math.Round(value * 32768);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + frames * 4);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(rate);
        writer.Write(rate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(frames * 4);
        for (int i = 0; i < frames * 2; i++) writer.Write(sample);
    }

    /// <summary>
    /// Record a source until its level settles at <paramref name="expected"/>
    /// on both channels, or at silence for 0, within a few seconds.
    /// </summary>
    private static void AssertLevel(double expected, string source, string because)
    {
        ProcessResult result = ProcessRunner.Run("python3", ["-c", """
            import math, os, struct, subprocess, sys, tempfile, time
            expected = float(sys.argv[1])
            deadline = time.monotonic() + 6
            with tempfile.TemporaryFile() as capture:
                p = subprocess.Popen(['parec', '--raw', '--format=float32le', '--rate=48000', '--channels=2', '--latency-msec=20',
                                      '--device=' + sys.argv[2]], stdout=capture, stderr=subprocess.PIPE)
                try:
                    last = 'no captured samples'
                    while time.monotonic() < deadline:
                        size = os.fstat(capture.fileno()).st_size // 8 * 8
                        if size > 9600 * 8:
                            # pread leaves the writer's shared file offset alone.
                            tail = struct.unpack('<19200f', os.pread(capture.fileno(), 9600 * 8, size - 9600 * 8))
                            matching = sum(abs(v - expected) < .003 for v in tail) / len(tail)
                            rms = math.sqrt(sum(v * v for v in tail) / len(tail))
                            last = (min(tail), max(tail), matching, rms)
                            if all(map(math.isfinite, tail)) and (rms < 1e-4 if expected == 0 else matching > .98):
                                break
                        if p.poll() is not None:
                            raise AssertionError(('capture exited', p.returncode, p.communicate()[1].decode()))
                        time.sleep(.05)
                    else:
                        raise AssertionError(('no settled level before the deadline', expected, last))
                finally:
                    if p.poll() is None: p.kill()
                    p.wait()
            """, expected.ToString(CultureInfo.InvariantCulture), source], TimeSpan.FromSeconds(12));
        Assert.True(result.Ok, $"{because}: {result.StdoutText}{result.Stderr}\n" + ProcessRunner.Run("pw-link", ["-l"]).StdoutText);
    }
}
