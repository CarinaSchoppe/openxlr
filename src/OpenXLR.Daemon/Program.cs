using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using System.Text.Json;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

if (args.Length == 1 && args[0].StartsWith("--plugin-scan=", StringComparison.Ordinal))
{
    string scanFormat = args[0]["--plugin-scan=".Length..];
    if (scanFormat != "lv2") return 64;
    Console.WriteLine(JsonSerializer.Serialize(Lv2Catalog.Plugins,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    return 0;
}

int apiPort = ApiEndpoints.ResolvePort(Environment.GetEnvironmentVariable("OPENXLR_API_PORT"));

var builder = WebApplication.CreateBuilder(args);

// The DeviceManager is both a singleton (queried by the hub) and the hosted
// background service that runs the poll/reconnect loop.
builder.Services.AddSingleton<DeviceManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeviceManager>());

// Plug-in metadata is loaded in bounded helper processes before it is ever
// offered to the mixer. The first scan runs asynchronously and cached results
// are available immediately.
builder.Services.AddSingleton<PluginCatalogService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PluginCatalogService>());

// The submixer is likewise a singleton the hub queries and a hosted service that
// builds the graph on start and tears it down on shutdown.
builder.Services.AddSingleton<MixerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MixerService>());

builder.Services.AddSingleton<WebSocketHub>();
builder.Services.AddHostedService<ServiceWatchdog>();

// Local-only control API. 127.0.0.1 keeps the device off the network.
builder.WebHost.UseUrls($"http://127.0.0.1:{apiPort}");

var app = builder.Build();
app.Services.GetRequiredService<WebSocketHub>();   // construct so it subscribes to StateChanged

app.UseWebSockets();
app.MapOpenXlrApi(apiPort);
app.MapGet("/", () => Results.Redirect("/api/v1"));

// The hosted services (device connect, PipeWire graph build) start before
// Kestrel binds, so a busy port used to mean: build the whole submix graph,
// fail to bind, abort with a core dump, get restarted by systemd, repeat.
// Every cycle tore the user's sinks down and back up. 37890 sits inside the
// kernel's ephemeral range, so any local program's outgoing connection can
// hold it for a while (the packages reserve it via sysctl; source installs
// may not). Wait for the port first, before anything touches PipeWire.
DateTime deadline = DateTime.UtcNow.AddSeconds(60);
for (int attempt = 0; ; attempt++)
{
    try
    {
        var probe = new TcpListener(IPAddress.Loopback, apiPort);
        probe.Start();
        probe.Stop();
        break;
    }
    catch (SocketException) when (DateTime.UtcNow < deadline)
    {
        if (attempt % 10 == 0)
            app.Logger.LogWarning("port {Port} is in use by another local socket; waiting for it", apiPort);
        await Task.Delay(1000);
    }
    catch (SocketException ex)
    {
        app.Logger.LogError("port {Port} still busy after 60 s ({Error}); exiting for systemd to retry", apiPort, ex.Message);
        return 75;   // EX_TEMPFAIL: a clean exit, no core dump; systemd tries again
    }
}

try
{
    app.Run();
}
catch (IOException ex) when (ex.InnerException is AddressInUseException)
{
    // Lost the race between the probe and Kestrel's own bind.
    app.Logger.LogError("port {Port} was taken between probe and bind; exiting for systemd to retry", apiPort);
    return 75;
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "daemon stopped unexpectedly; exiting for the service manager to retry");
    return 1;
}
return 0;
