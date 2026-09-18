using System.Reflection;
using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class StreamLifecycleTests
{
    // Real helper exchanges with a controlled graph let consecutive sweeps
    // reproduce registry-id reuse without racing the desktop session manager.
    private sealed class Graph : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-streams-").FullName;
        private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
        private readonly PipeWireAdapter _adapter = new();
        public Mixer Mixer { get; }
        public string[] Writes => File.ReadAllLines(Path.Combine(_directory, "writes"));

        public Graph()
        {
            ExecutableScript.Write(Path.Combine(_directory, "pw-dump"), """
                dir=${0%/*}
                /bin/cat "$dir/graph.json"
                """);
            ExecutableScript.Write(Path.Combine(_directory, "pactl"), """
                dir=${0%/*}
                echo "$*" >> "$dir/writes"
                case "$*" in
                    'list sinks short') printf '1\tOpenXLR_ch_system\n2\tOpenXLR_ch_music\n3\tdesktop\n' ;;
                    'list sink-inputs short') /bin/cat "$dir/inputs" ;;
                    'get-default-sink') echo desktop ;;
                    move-sink-input*)
                        if [ -f "$dir/refuse-moves" ]; then exit 1; fi
                        case "$3" in
                            OpenXLR_ch_system) sink=1 ;;
                            OpenXLR_ch_music) sink=2 ;;
                            desktop) sink=3 ;;
                            *) exit 1 ;;
                        esac
                        printf '%s\t%s\n' "$2" "$sink" > "$dir/inputs"
                        ;;
                esac
                """);
            Environment.SetEnvironmentVariable("PATH", _directory);
            Mixer = new(_adapter);
            typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer, true);
            typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer,
                new MixerConfig { Channels = [new("system", "System"), new("music", "Music")], Mixes = [] });
        }

        public void Set(string binary, int serial = 100, bool playing = true)
        {
            string name = JsonSerializer.Serialize(binary);
            string client = $$$$"""
                {"id":20,"type":"PipeWire:Interface:Client","info":{"props":{
                    "application.process.binary":{{{{name}}}},"application.name":{{{{name}}}}
                }}}
                """;
            string stream = $$$$"""
                {"id":10,"type":"PipeWire:Interface:Node","info":{"props":{
                    "client.id":20,"object.serial":{{{{serial}}}},"media.class":"Stream/Output/Audio"
                }}}
                """;
            File.WriteAllText(Path.Combine(_directory, "graph.json"), "[" + client + (playing ? "," + stream : "") + "]");
            File.WriteAllText(Path.Combine(_directory, "inputs"), $"{serial}\t1\n");
            // A normal adapter write retires its short-lived graph cache.
            _adapter.SetSinkMuted("OpenXLR_ch_system", false);
            File.WriteAllText(Path.Combine(_directory, "writes"), "");
        }

        public void RefuseMoves() => File.WriteAllText(Path.Combine(_directory, "refuse-moves"), "");

        public void Dispose()
        {
            try { Mixer.Dispose(); }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", _path);
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("player", 101)]
    [InlineData("other-player", 100)]
    public void ReusedNodeOrChangedIdentityIsRoutedAgain(string identity, int serial)
    {
        using var graph = new Graph();
        graph.Mixer.AssignApp("player", "music");
        graph.Mixer.AssignApp("other-player", "music");
        graph.Set("player");
        Assert.True(graph.Mixer.SyncStreams());
        Assert.False(graph.Mixer.SyncStreams());

        graph.Set(identity, serial);
        Assert.True(graph.Mixer.SyncStreams());

        StreamAssignment stream = Assert.Single(graph.Mixer.Streams);
        Assert.Equal((serial, identity, "music"), (stream.Serial, stream.Identity, stream.ChannelId));
        Assert.Contains($"move-sink-input {serial} OpenXLR_ch_music", graph.Writes);
        Assert.False(graph.Mixer.SyncStreams());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedIdentityCasingDoesNotHideLiveApps(bool playing)
    {
        using var graph = new Graph();
        graph.Mixer.AssignApp("PLAYER", "music");
        graph.Set("player", playing: playing);

        graph.Mixer.SyncStreams();

        StreamAssignment app = Assert.Single(graph.Mixer.Snapshot().Streams);
        Assert.Equal(playing, app.Active);
        Assert.True(app.Running);
        Assert.False(graph.Mixer.SyncStreams());
    }

    [Theory]
    [InlineData("music", "OpenXLR_ch_music")]
    [InlineData("ignore", "desktop")]
    public void CaseInsensitiveAssignmentMovesTheLiveStreamImmediately(string channel, string sink)
    {
        using var graph = new Graph();
        graph.Set("player");
        graph.Mixer.SyncStreams();

        graph.Mixer.AssignApp("PLAYER", channel);

        Assert.Contains($"move-sink-input 100 {sink}", graph.Writes);
        Assert.True(graph.Mixer.SyncStreams());
        Assert.Equal(channel, Assert.Single(graph.Mixer.Streams).ChannelId);
    }

    [Fact]
    public void ForgettingAnActiveAppRestoresAutomaticRoutingOnTheNextSweep()
    {
        using var graph = new Graph();
        graph.Mixer.AssignApp("player", "music");
        graph.Set("player");
        graph.Mixer.SyncStreams();

        graph.Mixer.ForgetApp("PLAYER");
        Assert.Empty(graph.Mixer.Snapshot().Streams);
        Assert.True(graph.Mixer.SyncStreams());

        Assert.Contains("move-sink-input 100 OpenXLR_ch_system", graph.Writes);
        Assert.Equal("system", Assert.Single(graph.Mixer.Streams).ChannelId);
        Assert.Equal("system", Assert.Single(graph.Mixer.Snapshot().Streams).ChannelId);
        Assert.Empty(graph.Mixer.Matcher.Overrides);
    }

    [Theory]
    [InlineData("music")]
    [InlineData("ignore")]
    public void UnknownNodeIdCannotAddressAnotherStreamsPulseSerial(string channel)
    {
        using var graph = new Graph();
        graph.Set("player");
        graph.Mixer.SyncStreams();
        Assert.Equal((10, 100), (Assert.Single(graph.Mixer.Streams).Id, Assert.Single(graph.Mixer.Streams).Serial));

        Assert.Throws<InvalidOperationException>(() => graph.Mixer.AssignStream(100, channel));

        Assert.DoesNotContain(graph.Writes, command => command.StartsWith("move-sink-input", StringComparison.Ordinal));
        Assert.Empty(graph.Mixer.Matcher.Overrides);
        Assert.Equal("system", Assert.Single(graph.Mixer.Streams).ChannelId);
    }

    [Theory]
    [InlineData("music")]
    [InlineData("ignore")]
    public void RefusedStreamMoveDoesNotRememberAnUnappliedAssignment(string channel)
    {
        using var graph = new Graph();
        graph.Set("player");
        graph.Mixer.SyncStreams();
        graph.RefuseMoves();

        Assert.Throws<InvalidOperationException>(() => graph.Mixer.AssignStream(10, channel));

        Assert.Empty(graph.Mixer.Matcher.Overrides);
        Assert.Equal("system", Assert.Single(graph.Mixer.Streams).ChannelId);
    }
}
