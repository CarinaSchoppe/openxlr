using System.Text.Json;

namespace OpenXLR.Daemon;

/// <summary>Versioned loopback API for native tools and third-party integrations.</summary>
internal static class ApiEndpoints
{
    internal const int DefaultPort = 37890;

    internal static int ResolvePort(string? value) =>
        int.TryParse(value, out int port) && port is >= 1024 and <= 65535 ? port : DefaultPort;

    public static void MapOpenXlrApi(this WebApplication app, int port)
    {
        app.MapGet("/healthz", () => Results.Json(new
        {
            status = "ok",
            apiVersion = ApiVersion.Current,
            daemonVersion = DaemonVersion.Current,
        }, WebSocketHub.Json));

        app.MapGet("/api/v1", () => Results.Json(new
        {
            name = "OpenXLR local control API",
            apiVersion = ApiVersion.Current,
            daemonVersion = DaemonVersion.Current,
            localOnly = true,
            resources = new
            {
                state = $"http://127.0.0.1:{port}/api/v1/state",
                plugins = $"http://127.0.0.1:{port}/api/v1/plugins",
                commands = $"http://127.0.0.1:{port}/api/v1/commands",
                events = $"ws://127.0.0.1:{port}/api/v1/events",
                openapi = $"http://127.0.0.1:{port}/api/v1/openapi.json",
            },
        }, WebSocketHub.Json));

        app.MapGet("/api/v1/state", (WebSocketHub hub) =>
            Results.Json(hub.Snapshot(), WebSocketHub.Json));

        app.MapGet("/api/v1/plugins", async () =>
        {
            IReadOnlyList<OpenXLR.Core.Mixing.PluginInfo> plugins =
                await Task.Run(() => OpenXLR.Core.Mixing.Lv2Catalog.Plugins);
            return Results.Json(new PluginsMessage(plugins), WebSocketHub.Json);
        });

        app.MapPost("/api/v1/commands", ExecuteCommandAsync);
        MapWebSocket(app, "/api/v1/events");
        MapWebSocket(app, "/ws");

        string specification = Path.Combine(AppContext.BaseDirectory, "openapi-v1.json");
        app.MapGet("/api/v1/openapi.json", () => File.Exists(specification)
            ? Results.File(specification, "application/json; charset=utf-8")
            : Results.Problem("The packaged OpenAPI document is missing.", statusCode: 500));
    }

    private static void MapWebSocket(WebApplication app, string path)
    {
        app.Map(path, async (HttpContext context, WebSocketHub hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new ErrorMessage("WebSocket upgrade required"),
                    WebSocketHub.Json);
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await hub.HandleAsync(socket);
        });
    }

    private static async Task<IResult> ExecuteCommandAsync(HttpRequest request, WebSocketHub hub,
        CancellationToken cancellationToken)
    {
        Command? command;
        try
        {
            command = await ReadCommandAsync(request.Body, cancellationToken);
        }
        catch (CommandTooLargeException)
        {
            return Results.Json(new ErrorMessage($"command exceeds {WebSocketHub.MaxCommandBytes} bytes"),
                WebSocketHub.Json, statusCode: StatusCodes.Status413PayloadTooLarge);
        }
        catch (JsonException ex)
        {
            return Results.Json(new ErrorMessage($"bad json: {ex.Message}"), WebSocketHub.Json,
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (command is null)
            return Results.Json(new ErrorMessage("command must be a JSON object"), WebSocketHub.Json,
                statusCode: StatusCodes.Status400BadRequest);
        if (WebSocketHub.ValidateRequestId(command.RequestId) is string requestError)
            return Results.Json(new ErrorMessage(requestError), WebSocketHub.Json,
                statusCode: StatusCodes.Status400BadRequest);

        string requestId = command.RequestId ?? Guid.NewGuid().ToString("N");
        command = command with { RequestId = requestId };
        WebSocketHub.CommandExecution execution = await hub.ExecuteAsync(command);
        var response = new CommandResultMessage(requestId, execution.Error, execution.Result,
            execution.Mutated ? hub.Snapshot() : null);
        return Results.Json(response, WebSocketHub.Json,
            statusCode: execution.Error is null
                ? StatusCodes.Status200OK
                : StatusCodes.Status422UnprocessableEntity);
    }

    internal static async Task<Command?> ReadCommandAsync(Stream body, CancellationToken cancellationToken = default)
    {
        using var limited = new MemoryStream();
        var buffer = new byte[8192];
        int total = 0;
        while (true)
        {
            int read = await body.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > WebSocketHub.MaxCommandBytes) throw new CommandTooLargeException();
            limited.Write(buffer, 0, read);
        }
        limited.Position = 0;
        return await JsonSerializer.DeserializeAsync<Command>(limited, WebSocketHub.Json, cancellationToken);
    }

    private sealed class CommandTooLargeException : Exception { }
}
