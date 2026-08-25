using OpenXLR.Daemon;

var builder = WebApplication.CreateBuilder(args);

// The DeviceManager is both a singleton (queried by the hub) and the hosted
// background service that runs the poll/reconnect loop.
builder.Services.AddSingleton<DeviceManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeviceManager>());

// The submixer is likewise a singleton the hub queries and a hosted service that
// builds the graph on start and tears it down on shutdown.
builder.Services.AddSingleton<MixerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MixerService>());

builder.Services.AddSingleton<WebSocketHub>();

// Local-only control API. 127.0.0.1 keeps the device off the network.
builder.WebHost.UseUrls("http://127.0.0.1:37890");

var app = builder.Build();
app.Services.GetRequiredService<WebSocketHub>();   // construct so it subscribes to StateChanged

app.UseWebSockets();

app.Map("/ws", async (HttpContext ctx, WebSocketHub hub) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.HandleAsync(socket);
});

app.MapGet("/", () => Results.Text("OpenXLR daemon. Control API: ws://127.0.0.1:37890/ws"));

app.Run();
