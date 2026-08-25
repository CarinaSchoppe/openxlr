using OpenXLR.Core.Mixing;

namespace OpenXLR.Probe;

/// <summary>
/// Shows every application stream PipeWire reports and the channel the matcher
/// picks for it, without touching the graph. Run:
///   dotnet run --project OpenXLR.Probe -- streams
/// </summary>
public static class StreamTest
{
    public static int Run()
    {
        var pw = new PipeWireAdapter();
        var matcher = new StreamMatcher();

        IReadOnlyList<AudioStream> streams = pw.ListStreams();
        Console.WriteLine($"{streams.Count} application stream(s)\n");
        Console.WriteLine($"{"label",-22} {"binary",-22} {"identity",-28} -> channel");
        Console.WriteLine(new string('-', 88));
        foreach (AudioStream s in streams)
            Console.WriteLine($"{Trim(s.Label, 22),-22} {Trim(s.Binary, 22),-22} {Trim(s.Identity, 28),-28} -> {matcher.Match(s)}");

        // The Proton case: a shared binary must not collapse separate games into
        // one identity, or a per-app override would apply to all of them.
        Console.WriteLine("\nProton/Wine identity check:");
        foreach (var (bin, media) in new[]
                 {
                     ("wine64-preloader", "Kingdom Come Deliverance II"),
                     ("wine64-preloader", "Bloodlines 2"),
                     ("proton", "Expedition 33"),
                 })
        {
            var s = new AudioStream(0, "wine", bin, media);
            Console.WriteLine($"  {bin,-18} media={media,-30} identity={s.Identity,-46} -> {matcher.Match(s)}");
        }

        Console.WriteLine("\nOverride check (pin one Proton game to Music, others unaffected):");
        var kcd = new AudioStream(0, "wine", "wine64-preloader", "Kingdom Come Deliverance II");
        var bl2 = new AudioStream(0, "wine", "wine64-preloader", "Bloodlines 2");
        matcher.SetOverride(kcd.Identity, "music");
        Console.WriteLine($"  Kingdom Come -> {matcher.Match(kcd)}   (pinned)");
        Console.WriteLine($"  Bloodlines 2 -> {matcher.Match(bl2)}   (still rule-matched)");
        return 0;
    }

    private static string Trim(string? s, int n)
    {
        s ??= "";
        return s.Length <= n ? s : s[..(n - 1)] + "…";
    }
}
