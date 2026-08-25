using OpenXLR.Core.Mixing;

namespace OpenXLR.Probe;

/// <summary>
/// Builds the real submix graph in PipeWire, exercises the faders, and tears it
/// all down. Run with: dotnet run --project OpenXLR.Probe -- mixer
/// </summary>
public static class MixerTest
{
    public static int Run()
    {
        var pw = new PipeWireAdapter();
        var mixer = new Mixer(pw);
        try
        {
            // A reduced config keeps the test fast; the shape is identical to the
            // full Wave-Link-derived layout.
            var config = new MixerConfig
            {
                Mixes =
                [
                    new MixDefinition("monitor", "Monitor", MixKind.Monitor) { Volume = 1.0 },
                    new MixDefinition("stream", "Stream", MixKind.VirtualMic) { Volume = 1.0 },
                ],
                Channels =
                [
                    new ChannelDefinition("game", "Game")
                        { Levels = new Dictionary<string, double> { ["monitor"] = 0.5, ["stream"] = 0.5 } },
                    new ChannelDefinition("music", "Music")
                        { Levels = new Dictionary<string, double> { ["monitor"] = 1.0, ["stream"] = 1.0 } },
                ],
            };

            Console.WriteLine("Building submix graph…");
            mixer.Build(config);   // no monitor routing: don't touch the real output in a test
            Console.WriteLine("built.\n");

            Console.WriteLine("Nodes created:");
            foreach (var (id, name, mc) in pw.DumpNodes()
                         .Where(n => n.Name.StartsWith("OpenXLR", StringComparison.Ordinal))
                         .OrderBy(n => n.Name))
                Console.WriteLine($"  {id,5}  {name,-28} {mc}");

            Console.WriteLine("\nFader moves (volume read back from the live node):");
            mixer.SetLevel("game", "monitor", 0.25);
            Report(pw, "game", "monitor", "level 0.25");
            mixer.SetLevel("game", "monitor", 0.9);
            Report(pw, "game", "monitor", "level 0.90");
            mixer.SetChannelMuted("game", "monitor", true);
            Report(pw, "game", "monitor", "muted in monitor only");
            mixer.SetChannelMuted("game", "monitor", false);
            Report(pw, "game", "monitor", "unmuted");
            // Master scales every channel in that mix: music 1.0 x master 0.5.
            mixer.SetMixVolume("stream", 0.5);
            Report(pw, "music", "stream", "mix master 0.5 (expect 0.5)");

            Console.WriteLine("\nSnapshot:");
            MixerState s = mixer.Snapshot();
            foreach (MixStatus m in s.Mixes)
                Console.WriteLine($"  mix {m.Id,-8} vol={m.Volume:0.00} muted={m.Muted}");
            foreach (ChannelStatus c in s.Channels)
                Console.WriteLine($"  ch  {c.Id,-8} levels=[{string.Join(", ", c.Levels.Select(kv => $"{kv.Key}={kv.Value:0.00}"))}]" +
                                  (c.MutedIn.Count > 0 ? $" muted in [{string.Join(",", c.MutedIn)}]" : ""));
            return 0;
        }
        finally
        {
            Console.WriteLine("\nTearing down…");
            mixer.TearDown();
            Thread.Sleep(500);
            var leftover = pw.DumpNodes()
                .Where(n => n.Name.StartsWith("OpenXLR", StringComparison.Ordinal)).ToList();
            Console.WriteLine(leftover.Count == 0
                ? "clean: no OpenXLR nodes remain."
                : "LEFTOVER: " + string.Join(", ", leftover.Select(n => n.Name)));
        }
    }

    private static void Report(PipeWireAdapter pw, string channel, string mix, string what)
    {
        string node = $"OpenXLR_lb_{channel}_{mix}_play";
        int? id = pw.FindNodeId(node);
        string vol = id is null ? "node?" : Wpctl($"get-volume {id}").Trim();
        Console.WriteLine($"  {channel}->{mix}: {what,-32} {vol}");
    }

    private static string Wpctl(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("wpctl") { RedirectStandardOutput = true };
        foreach (string a in args.Split(' ')) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit(3000);
        return o;
    }
}
