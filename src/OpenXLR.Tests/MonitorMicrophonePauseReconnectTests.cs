using System.Diagnostics;
using System.Reflection;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class MonitorMicrophonePauseReconnectTests
{
    [Theory]
    [InlineData("_inputFeeds", false)]
    [InlineData("_eqInputs", false)]
    [InlineData("_chainOuts", false)]
    [InlineData("_chainOuts", true)]
    [InlineData("deadEq", false)]
    [InlineData("deadEq", true)]
    [InlineData("retryEq", false)]
    [InlineData("retryEq", true)]
    [InlineData("editEq", false)]
    [InlineData("editEq", true)]
    public void LostMicrophoneLinksWaitForThePausedSinkToBeConfirmed(string routes, bool capture)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-mic-pause-reconnect-").FullName;
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        using var mixer = new Mixer();
        using Process holder = Process.Start(new ProcessStartInfo("/bin/sleep", "60") { UseShellExecute = false })!;
        using Process? deadHolder = routes == "deadEq"
            ? Process.Start(new ProcessStartInfo("/bin/true") { UseShellExecute = false })! : null;
        deadHolder?.WaitForExit();
        string id = capture ? "capture" : "xlr1";
        string sink = "OpenXLR_ch_" + id;
        bool eqRepair = routes is "deadEq" or "retryEq" or "editEq";
        try
        {
            File.WriteAllText(Path.Combine(directory, "mute"), "false");
            File.WriteAllText(Path.Combine(directory, "refuse"), "");
            ExecutableScript.Write(Path.Combine(directory, "pactl"), """
                dir=${0%/*}
                if [ "$1" = get-sink-mute ]; then
                  if [ "$(/bin/cat "$dir/mute")" = true ]; then echo 'Mute: yes'; else echo 'Mute: no'; fi
                elif [ "$1" = set-sink-mute ]; then
                  if [ -f "$dir/refuse" ]; then exit 1; fi
                  if [ "$3" = 1 ]; then echo true > "$dir/mute"; else echo false > "$dir/mute"; fi
                fi
                """);
            ExecutableScript.Write(Path.Combine(directory, "pw-dump"), $$$$"""
                dir=${0%/*}
                mute=$(/bin/cat "$dir/mute")
                printf '[{"id":1,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"{{{{sink}}}}","media.class":"Audio/Sink"},"params":{"Props":[{"channelVolumes":[1,1],"mute":%s}]}}}]' "$mute"
                """);
            ExecutableScript.Write(Path.Combine(directory, "pw-link"), $$$$"""
                dir=${0%/*}
                if [ -f "$dir/eq-ready" ]; then
                  node=$(/bin/cat "$dir/eq-node")
                  if [ "$1" = -o ]; then printf 'microphone:capture_1\n%s:capture_1\n' "$node"; exit; fi
                  if [ "$1" = -i ]; then printf '{{{{sink}}}}:playback_FL\n{{{{sink}}}}:playback_FR\n%s:playback_1\n' "$node"; exit; fi
                  if [ "$1" = -l ]; then exit; fi
                fi
                printf '%s\n' "$*" >> "$dir/links"
                """);
            Environment.SetEnvironmentVariable("PATH", directory);
            Set("_config", new MixerConfig
            {
                Mixes = [], Channels = capture
                    ? [new(id, "Mic") { CaptureSource = "microphone", CaptureMonoChannel = 0 }]
                    : [new(id, "Mic") { InputPair = 0 }],
            });
            Set("_built", true);
            var link = new PortLink([("microphone:capture_1", sink + ":playback_FL")]);
            Field<Dictionary<string, PortLink>>(eqRepair ? "_inputFeeds" : routes)[id] = link;
            // Keep a strip's other route present so the sweep only repairs
            // its lost link rather than rebuilding an unrelated input path.
            if (routes == "_eqInputs") Field<Dictionary<string, PortLink>>("_inputFeeds")[id] = new PortLink([]);
            if (routes == "_chainOuts")
                Field<Dictionary<string, FilterHandle>>("_chains")[id] = new("fixture", "fixture", "fixture", holder);
            if (eqRepair)
            {
                var adapter = Field<PipeWireAdapter>("_pw");
                adapter.EqHost = Path.Combine(directory, "eq-host");
                ExecutableScript.Write(adapter.EqHost, """
                    dir=${0%/*}
                    # The engine list the state asks for starts no stage.
                    if [ "$1" = engines ]; then echo '{"denoise":["rnnoise"],"rate":48000}'; exit 0; fi
                    printf '%s\n' "$*" >> "$dir/eq-starts"
                    if [ ! -f "$dir/eq-ready" ]; then exit 1; fi
                    echo "$2" > "$dir/eq-node"
                    echo ready
                    while IFS= read -r line; do
                      case "$line" in 'set '*) echo "control ${line#set }" ;; esac
                    done
                    """);
                ExecutableScript.Write(Path.Combine(directory, "pw-metadata"), """
                    echo "key:'clock.rate' value:'48000'"
                    """);
                MicEq eq = MicEq.Default with { Enabled = true };
                Field<Dictionary<string, MicEq>>("_eq")[id] = eq;
                Field<Dictionary<string, string>>("_stripSources")[id] = "microphone";
                Field<Dictionary<string, string>>("_eqErrors")[id] = "A previous EQ start failed.";
                if (deadHolder is not null)
                    Field<Dictionary<string, SoftwareEqBackend>>("_eqStages")[id] =
                        new(adapter, new("fixture-eq", "fixture-eq", "fixture-eq", deadHolder), eq);
                if (routes == "editEq") File.WriteAllText(Path.Combine(directory, "eq-ready"), "");
            }

            mixer.SetMonitorMuteMicrophones(true);
            mixer.SetMonitorMuted(true);
            Assert.True(mixer.Snapshot().MicrophonePausePending);
            if (routes == "editEq")
                for (int gain = 1; gain <= 3; gain++) mixer.SetEqBand(id, 2, new(Gain: gain));
            for (int sweep = 0; sweep < 4; sweep++) Heal();
            Assert.False(File.Exists(Path.Combine(directory, "links")), "The sweep reopened a microphone link before its sink mute was confirmed.");
            Assert.False(File.Exists(Path.Combine(directory, "eq-starts")), "An EQ helper started while its input was waiting for mute confirmation.");
            Assert.False(Field<RestartPolicy>("_restarts").Blocked("eq:" + id));
            if (routes == "editEq") Assert.Contains("mute confirmation", Field<Dictionary<string, string>>("_eqErrors")[id]);

            File.Delete(Path.Combine(directory, "refuse"));
            mixer.SyncOwnSinkLevels(out _);
            Assert.False(mixer.Snapshot().MicrophonePausePending);
            Assert.Equal("true", File.ReadAllText(Path.Combine(directory, "mute")).Trim());
            Heal();
            if (eqRepair) Assert.True(File.Exists(Path.Combine(directory, "eq-starts")), "The confirmed mute should allow EQ recovery again.");
            else Assert.Contains("microphone:capture_1 " + sink + ":playback_FL", File.ReadAllLines(Path.Combine(directory, "links")));
            if (routes == "editEq")
            {
                Assert.Contains("band2_gain=3", Assert.Single(File.ReadAllLines(Path.Combine(directory, "eq-starts"))));
                Assert.True(Field<Dictionary<string, SoftwareEqBackend>>("_eqStages")[id].IsAlive);
                Assert.DoesNotContain(id, Field<Dictionary<string, string>>("_eqErrors").Keys);
            }

            void Heal()
            {
                if (routes == "_chainOuts" || eqRepair && capture) mixer.EnsureFilterRoutes();
                else mixer.EnsureInputFeeds();
            }

            T Field<T>(string name) => (T)typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!;
        }
        finally
        {
            // The holder only supplies an alive process for the chain's
            // health check. The fixture, rather than graph teardown, owns it.
            ((Dictionary<string, FilterHandle>)typeof(Mixer).GetField("_chains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!).Clear();
            var eqStages = (Dictionary<string, SoftwareEqBackend>)typeof(Mixer).GetField("_eqStages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!;
            foreach (SoftwareEqBackend stage in eqStages.Values) stage.Release();
            eqStages.Clear();
            foreach (string field in new[] { "_inputFeeds", "_eqInputs", "_chainOuts" })
                ((Dictionary<string, PortLink>)typeof(Mixer).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!).Clear();
            Set("_built", false);
            if (!holder.HasExited) { holder.Kill(); holder.WaitForExit(); }
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Directory.Delete(directory, true);
        }

        void Set(string name, object value) => typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, value);
    }
}
