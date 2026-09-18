using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProcessResult = OpenXLR.Core.ProcessResult;
using ProcessRunner = OpenXLR.Core.ProcessRunner;
using OpenXLR.Daemon;
using OpenXLR.UI;
using Tmds.DBus.Protocol;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class DesktopKeysTests
{
    [Fact]
    public async Task PortalKeysAndDeckQueriesUseTheSameFocusServiceAndCleanUp()
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        await desktop.RequestNameAsync("org.kde.KWin");
        var fake = new DesktopBackend(new DesktopBus(desktop));
        desktop.AddMethodHandler(fake);
        var commands = new ConcurrentQueue<string>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]!.GetValue<string>() != "routeFocusedApp") continue;
                commands.Enqueue(command["channel"]!.GetValue<string>());
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var keys = new DesktopKeys(client);
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, FocusChannels = ["music", "browser"] }, save: false);
        Assert.StartsWith("Desktop keys are active", keys.Status);
        Assert.Equal(["focus_music", "focus_browser"], fake.ShortcutIds);
        fake.Activate("focus_music");
        await Wait(() => keys.Status.StartsWith("Focused application routed", StringComparison.Ordinal));
        Assert.Equal("music", Assert.Single(commands));
        fake.Activate("focus_deleted");
        fake.Activate("focus_browser", "/not_the_session");
        await Task.Delay(80);
        Assert.Single(commands);
        Assert.Equal(4242, await Task.Run(DesktopFocusQuery.Read));
        Assert.True(fake.Unloads >= 2);
        Assert.False(File.Exists(OpenXLR.Core.OpenXlrPaths.ConfigFile("desktop-focus.js")));
        Directory.CreateDirectory(OpenXLR.Core.OpenXlrPaths.ConfigFile("desktop-keys.json"));
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = false });
        Assert.StartsWith("Desktop keys:", keys.Status);
        Assert.Equal(4242, await Task.Run(DesktopFocusQuery.Read)); // failed save kept the running service
        Directory.Delete(OpenXLR.Core.OpenXlrPaths.ConfigFile("desktop-keys.json"));
        fake.CloseSession();
        await Wait(() => keys.Status.StartsWith("Shortcut session closed", StringComparison.Ordinal));
        fake.Activate("focus_browser");
        await Task.Delay(80);
        Assert.Single(commands);
        fake.CancelBinding = true;
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, FocusChannels = ["music"] }, save: false);
        Assert.Contains("cancelled", keys.Status);
        Assert.Throws<InvalidOperationException>(() => DesktopFocusQuery.Read());
        await keys.ConfigureAsync(new DesktopKeySettings(), save: false);
        Assert.Equal("Desktop keys are disabled.", keys.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbandonedMethodCallsCloseTheConnectionAndReleasePendingReplies(bool cancelled)
    {
        await using var environment = await PrivateBus.Start();
        using var server = new DBusConnection(environment.Address);
        await server.ConnectAsync();
        await server.RequestNameAsync("org.openxlr.Silent");
        using var handler = new SilentDesktop();
        server.AddMethodHandler(handler);
        using var connection = new DBusConnection(environment.Address);
        await connection.ConnectAsync();
        Task disconnected = connection.DisconnectedAsync();
        using var stop = new CancellationTokenSource();
        Task pending = new DesktopBus(connection).Empty("org.openxlr.Silent", "/silent", "org.openxlr.Silent", "Wait", cancel: stop.Token);
        await handler.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (cancelled)
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        else await Assert.ThrowsAsync<TimeoutException>(() => pending);
        await disconnected.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private sealed class SilentDesktop : IPathMethodHandler, IDisposable
    {
        public string Path => "/silent";
        public bool HandlesChildPaths => false;
        internal TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private MethodContext? _context;
        public ValueTask HandleMethodAsync(MethodContext context)
        {
            context.DisposesAsynchronously = true;
            _context = context;
            Arrived.TrySetResult();
            return ValueTask.CompletedTask;
        }
        public void Dispose() => _context?.Dispose();
    }

    [Fact]
    public async Task DeckOnlyIntegrationReportsALostDesktopConnection()
    {
        var environment = await PrivateBus.Start();
        bool stopped = false;
        try
        {
            await using var client = new DaemonClient();
            using var keys = new DesktopKeys(client);
            await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true }, save: false);
            Assert.StartsWith("OpenDeck focus routing is enabled", keys.Status);
            await environment.DisposeAsync();
            stopped = true;
            await Wait(() => keys.Status.StartsWith("Desktop connection lost", StringComparison.Ordinal));
        }
        finally { if (!stopped) await environment.DisposeAsync(); }
    }

    [Fact]
    public async Task SettingsFailureDoesNotReportAnEnabledIntegration()
    {
        await using var environment = await PrivateBus.Start();
        Directory.CreateDirectory(OpenXLR.Core.OpenXlrPaths.ConfigFile("desktop-keys.json"));
        await using var client = new DaemonClient();
        using var keys = new DesktopKeys(client);
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true });
        Assert.StartsWith("Desktop keys:", keys.Status);
    }

    private static async Task Wait(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++) { if (condition()) return; await Task.Delay(25); }
        Assert.True(condition());
    }

    private sealed class DesktopBackend(DesktopBus bus) : IPathMethodHandler
    {
        public string Path => "/";
        public bool HandlesChildPaths => true;
        public List<string> ShortcutIds { get; } = [];
        public bool CancelBinding { get; set; }
        public int Unloads { get; private set; }
        private string _session = "", _destination = "", _cookie = "";

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            var reader = request.GetBodyReader();
            string member = request.MemberAsString!;
            if (request.InterfaceAsString == DesktopBus.Shortcuts)
            {
                Dictionary<string, VariantValue> options;
                if (member == "CreateSession") options = reader.ReadDictionaryOfStringToVariantValue();
                else
                {
                    Assert.Equal("BindShortcuts", member);
                    Assert.Equal(_session, reader.ReadObjectPath().ToString());
                    ShortcutIds.Clear();
                    var end = reader.ReadArrayStart(DBusType.Struct);
                    while (reader.HasNext(end)) { reader.AlignStruct(); ShortcutIds.Add(reader.ReadString()); reader.ReadDictionaryOfStringToVariantValue(); }
                    reader.ReadString(); options = reader.ReadDictionaryOfStringToVariantValue();
                }
                string token = options["handle_token"].GetString();
                string owner = request.SenderAsString![1..].Replace('.', '_');
                string path = "/org/freedesktop/portal/desktop/request/" + owner + "/" + token;
                if (member == "CreateSession") _session = "/org/freedesktop/portal/desktop/session/" + owner + "/" + token;
                using var reply = context.CreateReplyWriter("o"); reply.WriteObjectPath(path); context.Reply(reply.CreateMessage());
                using var signal = bus.Connection.GetMessageWriter();
                signal.WriteSignalHeader(destination: request.SenderAsString, path: path, @interface: "org.freedesktop.portal.Request", member: "Response", signature: "ua{sv}");
                signal.WriteUInt32(member == "BindShortcuts" && CancelBinding ? 1u : 0u);
                signal.WriteDictionary(member == "CreateSession" ? new Dictionary<string, VariantValue> { ["session_handle"] = _session } : []);
                bus.Connection.TrySendMessage(signal.CreateMessage());
            }
            else if (member == "unloadScript")
            {
                Unloads++;
                using var reply = context.CreateReplyWriter("b"); reply.WriteBool(true); context.Reply(reply.CreateMessage());
            }
            else if (member == "loadScript")
            {
                string file = reader.ReadString();
                string[] literals = Regex.Matches(File.ReadAllText(file), "\"(?:\\\\.|[^\"\\\\])*\"")
                    .Select(m => JsonSerializer.Deserialize<string>(m.Value)!).ToArray();
                _destination = literals[0]; _cookie = literals[4];
                using var reply = context.CreateReplyWriter("i"); reply.WriteInt32(7); context.Reply(reply.CreateMessage());
            }
            else if (member == "run")
            {
                using var reply = context.CreateReplyWriter(null); context.Reply(reply.CreateMessage());
                _ = bus.Empty(_destination, "/org/openxlr/Desktop", "org.openxlr.Desktop", "ReportFocus", "ss",
                    (ref MessageWriter w) => { w.WriteString(_cookie); w.WriteString("4242"); });
            }
            else context.ReplyUnknownMethodError();
            return ValueTask.CompletedTask;
        }

        internal void CloseSession()
        {
            using var signal = bus.Connection.GetMessageWriter();
            signal.WriteSignalHeader(path: _session, @interface: "org.freedesktop.portal.Session", member: "Closed", signature: "a{sv}");
            signal.WriteDictionary(new Dictionary<string, VariantValue>());
            bus.Connection.TrySendMessage(signal.CreateMessage());
        }

        internal void Activate(string id, string? session = null)
        {
            using var signal = bus.Connection.GetMessageWriter();
            signal.WriteSignalHeader(path: DesktopBus.PortalPath, @interface: DesktopBus.Shortcuts, member: "Activated", signature: "osta{sv}");
            signal.WriteObjectPath(session ?? _session); signal.WriteString(id); signal.WriteUInt64(100);
            signal.WriteDictionary(new Dictionary<string, VariantValue>());
            bus.Connection.TrySendMessage(signal.CreateMessage());
        }
    }

    private sealed class PrivateBus : IAsyncDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openxlr-keys-" + Guid.NewGuid());
        private readonly string? _oldBus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly CancellationTokenSource _stop = new();
        private Task<ProcessResult>? _process;
        internal string Address => "unix:path=" + _directory + "/bus";
        internal static async Task<PrivateBus> Start()
        {
            var value = new PrivateBus();
            try
            {
                Directory.CreateDirectory(value._directory);
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", value._directory);
                Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", value.Address);
                value._process = ProcessRunner.RunAsync("dbus-daemon", ["--session", "--nofork", "--address=" + value.Address],
                    TimeSpan.FromSeconds(40), cancel: value._stop.Token);
                await Wait(() => File.Exists(value._directory + "/bus"));
                return value;
            }
            catch { await value.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_process is not null) await _process;
            _stop.Dispose();
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", _oldBus);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
