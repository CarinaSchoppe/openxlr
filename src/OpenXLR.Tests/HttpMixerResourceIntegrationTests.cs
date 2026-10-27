using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public async Task HttpMixerResourcesFollowCommandsAndMatchTheState()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        string socketPath = Path.Combine(Path.GetTempPath(), "ox-http-" + Guid.NewGuid().ToString("N"), "control.sock");
        using var listener = ControlSocketListener.Bind(socketPath);
        ControlApi.Configure(builder, listener);
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
        ApiEndpoints.Map(app);
        await app.StartAsync();
        try
        {
            using var http = new HttpClient(ControlConnection.HttpHandler(listener.Path)) { BaseAddress = new Uri("http://localhost") };

            Assert.True((await Read("mixer"))["routeAppsByDefault"]!.GetValue<bool>());
            await Send(new { cmd = "setRouteAppsByDefault", value = false });
            Assert.False((await Read("mixer"))["routeAppsByDefault"]!.GetValue<bool>());
            Assert.False(MixerSettings.Load()!.RouteAppsByDefault);
            await Send(new { cmd = "setRouteAppsByDefault", value = true });
            Assert.True((await Read("mixer"))["routeAppsByDefault"]!.GetValue<bool>());

            var channels = (await Read("channels")).AsArray();
            Assert.Equal(["software", "other"], channels.Select(c => c!["id"]!.GetValue<string>()));
            await Send(new { cmd = "setChannelMuted", channel = "other", mix = "monitor2", value = true });
            var other = await Read("channels/other");
            Assert.Contains("monitor2", other["mutedIn"]!.AsArray().Select(m => m!.GetValue<string>()));
            await Send(new { cmd = "setMixVolume", mix = "monitor2", value = 0.5 });
            Assert.Equal(0.5, (await Read("mixes/monitor2"))["volume"]!.GetValue<double>(), 3);

            // Ids are exact; a channel without a chain has no insert resource.
            foreach (string path in new[] { "channels/Other", "mixes/missing", "inserts/software" })
            {
                using var missing = await http.GetAsync("/api/v1/" + path);
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }
            Assert.Empty((await Read("inserts")).AsObject());
            // A chain key carries a colon; an emptied chain reads as a list.
            await Send(new { cmd = "setInserts", channel = "mix:monitor", inserts = Array.Empty<object>() });
            Assert.Empty((await Read("inserts/mix:monitor")).AsArray());

            var full = await Read("mixer");
            var state = await Read("state");
            Assert.True(JsonNode.DeepEquals(full, state["mixer"]));

            async Task Send(object command)
            {
                using var response = await http.PostAsJsonAsync("/api/v1/commands", command);
                string body = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == HttpStatusCode.OK, body);
            }
            async Task<JsonNode> Read(string path)
            {
                using var response = await http.GetAsync("/api/v1/" + path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            }
        }
        finally
        {
            await app.StopAsync();
            mixer.TearDown();
        }
    }
}
