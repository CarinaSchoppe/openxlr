using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

// Runs only against the private PipeWire server supplied by test-monitor-volume.py.
[Collection("xdg-config")]
public sealed class HttpMixerFeatureIntegrationTests
{
    [MonitorPipeWireFact]
    public async Task HttpCommandsAndResourcesShareNewMixerAndEffectState()
    {
        const string plugin = "urn:openxlr:test:gain";
        Assert.Contains(PluginCatalog.Refresh(), p => p.Plugin == plugin);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<DeviceManager>();
        builder.Services.AddSingleton<MixerService>();
        builder.Services.AddSingleton<WebSocketHub>();
        await using var app = builder.Build();
        var service = app.Services.GetRequiredService<MixerService>();
        var mixer = (Mixer)typeof(MixerService).GetField("_mixer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service)!;
        mixer.Build(new MixerConfig
        {
            Channels = [new("software", "Software"), new("other", "Other")],
            Mixes = [new("monitor", "Monitor A", MixKind.Monitor), new("monitor2", "Monitor B", MixKind.Monitor)],
        });
        ApiToken.Initialize();
        ApiEndpoints.Map(app);
        await app.StartAsync();
        try
        {
            using var http = new HttpClient
            {
                BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features
                    .Get<IServerAddressesFeature>()!.Addresses.Single()),
            };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken.Current);
            await Send(new { cmd = "createMix", name = "Personal", kind = "monitor" });
            string personal = mixer.Config.Mixes.Single(m => m.Name == "Personal").Id;
            var mix = await Read("mixes/" + personal);
            Assert.Equal("monitor", mix["kind"]!.GetValue<string>());
            Assert.True(mix["editable"]!.GetValue<bool>());
            await Send(new { cmd = "setLayoutAppearance", mix = personal, appearance = new { icon = "♫", colour = "#12ABCD" } });
            await Send(new { cmd = "setLayoutAppearance", channel = "software", appearance = new { hidden = true } });
            await Send(new { cmd = "setDisplayOrder", channels = new[] { "other", "software" },
                mixes = new[] { personal, "monitor2", "monitor" } });
            await Send(new { cmd = "setExclusiveGroup", name = "Inputs", channels = new[] { "software", "other" } });
            await Send(new { cmd = "setChannelMuted", channel = "software", mix = "monitor", value = false });
            await Send(new { cmd = "setChannelMuted", channel = "other", mix = "monitor2", value = false });
            await Send(new { cmd = "setMixLatencyCompensation", value = true });
            await Send(new { cmd = "setInserts", channel = "software", inserts = new[] { new InsertDefinition
                { Id = "gain", Kind = "lv2", Plugin = plugin, Bypass = true, Params = new() { ["gain"] = .5 } } } });
            await Send(new { cmd = "renameInsert", channel = "software", insertId = "gain", name = "Voice level" });
            string hold = Guid.NewGuid().ToString("N");
            await Send(new { cmd = "holdInsert", action = "begin", holdId = hold, channel = "software", insertId = "gain" });
            var held = (await Read("inserts/software"))[0]!;
            Assert.False(held["insert"]!["bypass"]!.GetValue<bool>());
            Assert.Equal("Voice level", held["insert"]!["label"]!.GetValue<string>());
            Assert.Null(held["error"]);
            // An invalid edit must not disturb an active lease or unrelated features.
            await Send(new { cmd = "setLayoutAppearance", mix = personal, appearance = new { hidden = true } }, HttpStatusCode.BadRequest);
            await Send(new { cmd = "soundCheck", channel = "software", action = "record" }, HttpStatusCode.BadRequest);
            await Send(new { cmd = "holdInsert", action = "end", holdId = hold });
            Assert.True((await Read("inserts/software"))[0]!["insert"]!["bypass"]!.GetValue<bool>());
            var full = await Read("mixer");
            Assert.Equal(personal, full["mixes"]![0]!["id"]!.GetValue<string>());
            Assert.Equal("♫", full["mixes"]![0]!["appearance"]!["icon"]!.GetValue<string>());
            Assert.Equal("other", full["channels"]![0]!["id"]!.GetValue<string>());
            Assert.Equal("inputs", full["channels"]![0]!["exclusiveGroup"]!.GetValue<string>());
            Assert.True(full["channels"]![1]!["appearance"]!["hidden"]!.GetValue<bool>());
            Assert.True(full["compensateMixLatency"]!.GetValue<bool>());
            Assert.Equal("idle", full["soundCheck"]!["mode"]!.GetValue<string>());
            Assert.True(mixer.IsChannelMutedIn("other", "monitor"));
            Assert.False(mixer.IsChannelMutedIn("other", "monitor2"));
            var state = await Read("state");
            Assert.True(JsonNode.DeepEquals(full, state["mixer"]));
            // Profile persistence carries the same combined state, including local presentation choices.
            ProfileStore.Save("test", "combined", new Profile { Mixer = mixer.ExportScene(),
                Presentation = new() { Skin = "deck", AppearanceMode = "light", TouchControls = true,
                    CompactMixer = true, CompactChannel = "software", SectionOrder = ["MonitorTile", "InputsTile"] } });
            var saved = ProfileStore.Load("test", "combined")!;
            Assert.Equal("monitor", Assert.Single(mixer.ExportSettings().UserMixes!).Kind);
            Assert.True(mixer.ExportSettings().CompensateMixLatency);
            Assert.Single(saved.Mixer!.ExclusiveGroups!);
            Assert.Equal("Voice level", saved.Mixer.Inserts!["software"][0].Label);
            Assert.True(saved.Mixer.Inserts["software"][0].Bypass);
            Assert.True(saved.Presentation!.TouchControls);
            Assert.Equal("light", saved.Presentation.AppearanceMode);
            await Send(new { cmd = "deleteMix", mix = personal });
            using var deleted = await http.GetAsync("/api/v1/mixes/" + personal);
            Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
            Assert.DoesNotContain("mix:" + personal, mixer.ExportSettings().Appearance.Keys);

            async Task Send(object command, HttpStatusCode status = HttpStatusCode.OK)
            {
                using var response = await http.PostAsJsonAsync("/api/v1/commands", command);
                string body = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == status, body);
                Assert.Equal(status == HttpStatusCode.OK, JsonNode.Parse(body)!["ok"]!.GetValue<bool>());
            }
            async Task<JsonNode> Read(string path)
            {
                using var response = await http.GetAsync("/api/v1/" + path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            }
        }
        finally { await app.StopAsync(); }
    }
}
