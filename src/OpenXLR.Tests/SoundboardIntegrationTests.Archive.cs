using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class SoundboardIntegrationTests
{
    /// <summary>
    /// A replace import stops the player and starts it again on the new
    /// library: the audition pair is linked into the monitor mix again with
    /// it, so an audition is still heard on the monitor output, and still
    /// never on Stream.
    /// </summary>
    [MonitorPipeWireFact]
    public void AnAuditionIsHeardAfterAReplaceImport()
    {
        const string Output = "soundboard_archive_output";
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        // The output belongs to an adapter of its own, so the mixer's teardown leaves it in place.
        var outputs = new PipeWireAdapter();
        var mixer = new Mixer(pw);
        try
        {
            outputs.CreateNullSink(Output, "Archive test output");
            static Dictionary<string, double> Sends() => new() { ["monitor"] = 1, ["stream"] = 1 };
            mixer.Build(new MixerConfig
            {
                Mixes = [new("monitor", "Monitor A", MixKind.Monitor), new("stream", "Stream", MixKind.VirtualMic)],
                Channels = [new("system", "System") { Levels = Sends() }, new("sfx", "SFX") { Levels = Sends() }],
            });
            mixer.SetMonitorOutputs([Output]);
            mixer.UseSoundboard(SoundboardLibrary.Empty, _ => null, Path.Combine(_directory, "clips"));
            mixer.SetSoundboard(true, _ => null);
            string wav = Path.Combine(_directory, "long.wav");
            WriteConstantWav(wav, .1, seconds: 20);
            mixer.ImportClip(wav, "Long");
            WaitLoaded(mixer, "long");
            Assert.Equal(["monitor"], mixer.AuditionMixes);
            string archive = Path.Combine(_directory, "board.zip");
            Assert.Equal(1, mixer.ExportSoundboard(archive).Clips);

            SoundboardPlayer before = Player(mixer)!;
            SoundboardImport import = mixer.ImportSoundboard(archive, merge: false);
            Assert.True(import.Replaced);
            Assert.Equal(["long"], import.Added);
            // A new player, linked into the monitor mix as the old one was.
            Assert.NotSame(before, Player(mixer));
            Assert.Equal(["monitor"], mixer.AuditionMixes);
            WaitLoaded(mixer, "long");

            string heard = Output + ".monitor";
            mixer.AuditionClip("long");
            AssertLevel(.1, heard, "the audition on the monitor output after a replace import");
            AssertLevel(0, "OpenXLR_stream", "an audition never reaches Stream");
            Assert.True(SpinWait.SpinUntil(() => mixer.Soundboard().Auditioning == "long", TimeSpan.FromSeconds(3)));
            mixer.StopAudition();
            AssertLevel(0, heard, "the audition stopped");
        }
        finally
        {
            mixer.Dispose();
            pw.TearDown();
            outputs.TearDown();
        }
    }
}
