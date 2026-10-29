using System.Text.Json.Nodes;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class DaemonClientTests
{
    [Fact]
    public async Task ANewConnectionDoesNotPresentThePreviousDaemonsStateAsItsOwn()
    {
        int connections = 0, connectedEvents = 0;
        var drop = Completion();
        var release = Completion();
        var firstState = Completion();
        var reconnected = Completion();
        var secondState = Completion();
        var disconnected = new TaskCompletionSource<DaemonClient.StateObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            if (Interlocked.Increment(ref connections) == 1)
            {
                await SocketTestServer.Send(socket, new { type = "state", daemonBuildVersion = "first" }, stop);
                await drop.Task.WaitAsync(stop);
                socket.Abort();
            }
            else
            {
                await release.Task.WaitAsync(stop);
                await SocketTestServer.Send(socket, new { type = "state", daemonBuildVersion = "second" }, stop);
                await Task.Delay(Timeout.Infinite, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        client.ConnectionChanged += up =>
        {
            if (up && Interlocked.Increment(ref connectedEvents) == 2) reconnected.TrySetResult();
            if (!up) disconnected.TrySetResult(client.StateForDiagnostics);
        };
        client.StateReceived += state =>
        {
            if (state["daemonBuildVersion"]!.GetValue<string>() == "first") firstState.TrySetResult();
            else secondState.TrySetResult();
        };
        client.Start();
        await firstState.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("first", client.LastStateJson);
        var initial = client.StateForDiagnostics;
        Assert.True(initial.Connected);
        Assert.NotNull(initial.ReceivedAt);
        Assert.Equal(TimeSpan.Zero, initial.ReceivedAt.Value.Offset);
        drop.SetResult();
        var stopped = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stopped.Connected);
        Assert.Equal(initial.Json, stopped.Json);
        Assert.Equal(initial.ReceivedAt, stopped.ReceivedAt);
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(client.LastStateJson);
        Assert.Null(client.StateForDiagnostics.ReceivedAt);
        Assert.True(client.StateForDiagnostics.Connected);
        release.SetResult();
        await secondState.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("second", client.LastStateJson);
        Assert.NotNull(client.StateForDiagnostics.ReceivedAt);
        Assert.True(initial.Connected);
        Assert.Contains("first", initial.Json);
    }

    [Fact]
    public async Task DisconnectedQueriesDoNotRetainUnsentReplySlots()
    {
        await using var client = new DaemonClient();
        for (int i = 0; i < 100; i++)
            Assert.Null(await client.RequestPluginsAsync(TimeSpan.FromSeconds(5)));
        Assert.False(client.StateForDiagnostics.Connected);
        Assert.Null(client.StateForDiagnostics.Json);
        Assert.Null(client.StateForDiagnostics.ReceivedAt);
        // Inspect retention without adding a diagnostics-only API to the client.
        // _queriesById is the map that kept an entry per unsent query; _queries
        // and _lastReply were already released on the way out.
        var retained = (System.Collections.IDictionary)typeof(DaemonClient).GetField("_queriesById",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
        Assert.Empty(retained);
    }

    [Fact]
    public async Task ConcurrentQueriesShareReplyEvenWhenOneCallerTimesOut()
    {
        int connections = 0, requests = 0;
        var release = Completion();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            Interlocked.Increment(ref connections);
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                // Like the daemon: the reply, then the commandResult for the requestId.
                string requestId = command["requestId"]!.GetValue<string>();
                if (command["cmd"]!.GetValue<string>() == "listPlugins")
                {
                    Interlocked.Increment(ref requests);
                    await release.Task.WaitAsync(stop);
                    await SocketTestServer.Send(socket, new { type = "plugins", plugins = new[] { new { name = "EQ" } } }, stop);
                }
                else
                    await SocketTestServer.Send(socket, new { type = "diagnostics" }, stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = Completion();
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<System.Text.Json.Nodes.JsonNode?> first = client.RequestPluginsAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await client.RequestPluginsAsync(TimeSpan.Zero));
        var second = client.RequestPluginsAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        Assert.Equal("EQ", (await first)![0]!["name"]!.GetValue<string>());
        Assert.Equal("EQ", (await second)![0]!["name"]!.GetValue<string>());
        // This reply is a server-side barrier after any accidental duplicate requests.
        Assert.NotNull(await client.RequestDiagnosticsAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, requests);
        Assert.Equal(1, connections);
    }

    [Fact]
    public async Task ALateReplyToATimedOutQueryNeverAnswersTheNextOne()
    {
        // The first catalog request is answered only after a second one was
        // sent; the answers arrive in order, each followed by its
        // commandResult, and the second caller must get the second answer.
        var release = Completion();
        var answers = new System.Collections.Concurrent.ConcurrentQueue<(string Id, string Name)>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                answers.Enqueue((command["requestId"]!.GetValue<string>(), answers.Count == 0 ? "OLD" : "NEW"));
                if (answers.Count < 2) { await release.Task.WaitAsync(stop); continue; }
                while (answers.TryDequeue(out var a))
                {
                    await SocketTestServer.Send(socket, new { type = "plugins", plugins = new[] { new { name = a.Name } } }, stop);
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = a.Id }, stop);
                }
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = Completion();
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(await client.RequestPluginsAsync(TimeSpan.FromMilliseconds(200)));   // gives up; its reply is still owed
        Task<System.Text.Json.Nodes.JsonNode?> second = client.RequestPluginsAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        Assert.Equal("NEW", (await second)![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task DisconnectReleasesQueryAndReconnectsWithoutReplayingIt()
    {
        int connections = 0, requests = 0;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            if (Interlocked.Increment(ref connections) == 1)
            {
                await SocketTestServer.Receive(socket, stop);
                Interlocked.Increment(ref requests);
                socket.Abort();
            }
            else
            {
                await SocketTestServer.Send(socket, new { type = "state" }, stop);
                await Task.Delay(Timeout.Infinite, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = Completion();
        var restored = Completion();
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.StateReceived += _ => restored.TrySetResult();
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await client.RequestPluginsAsync(TimeSpan.FromMinutes(1)).WaitAsync(TimeSpan.FromSeconds(5)));
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, connections);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task AnEditCutOffByTheConnectionAsksToCheckTheChangeItself()
    {
        int connections = 0;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            if (Interlocked.Increment(ref connections) == 1)
            {
                Assert.Equal("setMonitorFeed", (await SocketTestServer.Receive(socket, stop))["cmd"]!.GetValue<string>());
                socket.Abort();
            }
            else await Task.Delay(Timeout.Infinite, stop);
        });
        await using var client = new DaemonClient(server.Url);
        var connected = Completion();
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Output and feed edits share the layout's acknowledged path, so the
        // message cannot name the layout.
        Assert.Equal(OpenXLR.UI.Localization.Localizer.Text("ConnectionLostCheckChange"),
            await client.SetMonitorFeedAsync("speakers", "monitor+monitor2").WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DisconnectedCommandsReportFailureAndDisposalIsIdempotent()
    {
        var client = new DaemonClient();
        string? error = null;
        client.ErrorReceived += message => error = message;
        await client.SetLevelAsync("music", "monitor", 0.5);
        Assert.Contains("disconnected", error);
        Assert.Null(await client.RequestPluginsAsync(TimeSpan.FromSeconds(30)));
        await Task.WhenAll(client.DisposeAsync().AsTask(), client.DisposeAsync().AsTask());
        Assert.Throws<ObjectDisposedException>(client.Start);
        await client.SetLevelAsync("music", "monitor", 0.5);
        Assert.Null(await client.RequestDiagnosticsAsync(TimeSpan.FromSeconds(30)));
        Assert.Null(await client.AddWindowsPluginFolderAsync("/plugins", TimeSpan.FromSeconds(30)));
        Assert.Null(await client.RemoveWindowsPluginFolderAsync("/plugins", TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task DisposalUnblocksPendingReceiveAndQuery()
    {
        var received = Completion();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Receive(socket, stop);
            received.TrySetResult();
            await Task.Delay(Timeout.Infinite, stop);
        });
        await using var client = new DaemonClient(server.Url);
        var connected = Completion();
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var query = client.RequestPluginsAsync(TimeSpan.FromMinutes(1));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await query.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task PluginFolderChangesAreSentSeparatelyInsteadOfSharingAnInstallReply()
    {
        var received = Completion();
        var release = Completion();
        var commands = new List<(string Command, string? Path)>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                string name = command["cmd"]!.GetValue<string>();
                commands.Add((name, command["path"]?.GetValue<string>()));
                if (commands.Count == 1)
                {
                    received.SetResult();
                    await release.Task.WaitAsync(stop);
                }
                await SocketTestServer.Send(socket, new { type = "pluginInstall", ok = true, message = name }, stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = Completion();
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var add = client.AddWindowsPluginFolderAsync("/plugins/new", TimeSpan.FromSeconds(5));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var remove = client.RemoveWindowsPluginFolderAsync("/plugins/old", TimeSpan.FromSeconds(5));
        var sync = client.SyncWindowsPluginsAsync(TimeSpan.FromSeconds(5));
        var removeUses = client.RemoveWindowsPluginInsertsAsync("/plugins/new/EQ.vst3", TimeSpan.FromSeconds(5));
        release.SetResult();
        Assert.Equal("addWindowsPluginFolder", (await add)!["message"]!.GetValue<string>());
        Assert.Equal("removeWindowsPluginFolder", (await remove)!["message"]!.GetValue<string>());
        Assert.Equal("syncWindowsPlugins", (await sync)!["message"]!.GetValue<string>());
        Assert.Equal("removeWindowsPluginInserts", (await removeUses)!["message"]!.GetValue<string>());
        Assert.Equal(new (string, string?)[]
        {
            ("addWindowsPluginFolder", "/plugins/new"),
            ("removeWindowsPluginFolder", "/plugins/old"),
            ("syncWindowsPlugins", null),
            ("removeWindowsPluginInserts", "/plugins/new/EQ.vst3"),
        }, commands);
    }

    [Fact]
    public async Task FilesFromDifferentFoldersDoNotShareAReply()
    {
        var received = Completion();
        var release = Completion();
        int requests = 0;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (++requests == 1) { received.SetResult(); await release.Task.WaitAsync(stop); }
                await SocketTestServer.Send(socket, new
                {
                    type = "windowsPluginFiles", ok = true,
                    plugins = new[] { new { path = command["path"]!.GetValue<string>() + "/EQ.vst3" } },
                }, stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = Completion();
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = client.RequestWindowsPluginFilesAsync("/first", TimeSpan.FromSeconds(5));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = client.RequestWindowsPluginFilesAsync("/second", TimeSpan.FromSeconds(5));
        release.SetResult();
        Assert.Equal("/first/EQ.vst3", (await first)!["plugins"]![0]!["path"]!.GetValue<string>());
        Assert.Equal("/second/EQ.vst3", (await second)!["plugins"]![0]!["path"]!.GetValue<string>());
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task EditorRuleOverridesAndDefaultResetHaveDistinctWireValues()
    {
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                Assert.Equal("setNativeEditorRule", command["cmd"]!.GetValue<string>());
                bool blocked = command["blocked"]?.GetValue<bool>() ?? true;
                await SocketTestServer.Send(socket, new { type = "nativeEditorRulesChanged" }, stop);
                await SocketTestServer.Send(socket, new { type = "nativeEditorRules", rules = new[] { new { blocked } } }, stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        int changed = 0;
        client.NativeEditorRulesChanged += () => changed++;
        var connected = Completion();
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var allow = await client.SetNativeEditorRuleAsync("clap", "example", "Example", false, TimeSpan.FromSeconds(5));
        Assert.False(allow!["rules"]![0]!["blocked"]!.GetValue<bool>());
        var reset = await client.SetNativeEditorRuleAsync("clap", "example", "Example", null, TimeSpan.FromSeconds(5));
        Assert.True(reset!["rules"]![0]!["blocked"]!.GetValue<bool>());
        Assert.Equal(2, changed);
    }

    [Fact]
    public async Task SetupReadCannotSwallowATraceChangeAndEachChangeGetsItsOwnReply()
    {
        var reading = Completion();
        var release = Completion();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            int step = 0;
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (step == 0)
                {
                    Assert.Equal("getPluginSetup", command["cmd"]!.GetValue<string>());
                    reading.TrySetResult();
                    await release.Task.WaitAsync(stop);
                }
                else
                {
                    Assert.Equal("setPluginWineTrace", command["cmd"]!.GetValue<string>());
                    Assert.Equal(step == 1, command["value"]!.GetValue<bool>());
                }
                await SocketTestServer.Send(socket, new { type = "pluginSetup", wineTrace = step == 1 }, stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
                step++;
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = Completion();
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var read = client.RequestPluginSetupAsync(TimeSpan.FromSeconds(5));
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var enable = client.SetPluginWineTraceAsync(true, TimeSpan.FromSeconds(5));
        var disable = client.SetPluginWineTraceAsync(false, TimeSpan.FromSeconds(5));
        release.TrySetResult();
        Assert.False((await read)!["wineTrace"]!.GetValue<bool>());
        Assert.True((await enable)!["wineTrace"]!.GetValue<bool>());
        Assert.False((await disable)!["wineTrace"]!.GetValue<bool>());
    }

    /// <summary>
    /// The soundboard's commands as the daemon reads them, each answered by
    /// its own commandResult; two imports started together each get their
    /// own clipImport, the refused one with its sentence, and a library
    /// export and import read their own replies.
    /// </summary>
    [Fact]
    public async Task SoundboardCommandsAreSentAsTheDaemonReadsThemAndImportsDoNotShareAnAnswer()
    {
        var commands = new System.Collections.Concurrent.ConcurrentQueue<JsonNode>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                commands.Enqueue(command);
                string id = command["requestId"]!.GetValue<string>();
                if (command["cmd"]!.GetValue<string>() == "importClip")
                {
                    bool ok = command["path"]!.GetValue<string>().EndsWith(".wav", StringComparison.Ordinal);
                    await SocketTestServer.Send(socket, new { type = "clipImport", ok, message = ok ? "Airhorn was added to the soundboard." : "This build cannot decode the file.",
                        clip = ok ? "airhorn" : null }, stop);
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = id, error = ok ? null : "This build cannot decode the file." }, stop);
                }
                else if (command["cmd"]!.GetValue<string>() is "exportSoundboard" or "importSoundboard")
                {
                    bool export = command["cmd"]!.GetValue<string>() == "exportSoundboard";
                    await SocketTestServer.Send(socket, export
                        ? new { type = "soundboardExport", ok = true, message = "2 clips were exported to board.zip.", clips = 2, skipped = Array.Empty<string>() }
                        : (object)new { type = "soundboardImport", ok = true, message = "1 clip was added from the archive.", clips = new[] { "a" }, skipped = new[] { "b: the file is empty" } }, stop);
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = id, error = (string?)null }, stop);
                }
                else
                {
                    string? error = command["cmd"]!.GetValue<string>() == "playClip" ? "playClip: the soundboard is off" : null;
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = id, error }, stop);
                }
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = Completion();
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<ClipImportResult?> wav = client.ImportClipAsync("/music/airhorn.wav", "Intro"), mod = client.ImportClipAsync("/music/song.mod", "");
        Assert.Equal(new ClipImportResult(true, "Airhorn was added to the soundboard.", "airhorn"), await wav);
        Assert.Equal(new ClipImportResult(false, "This build cannot decode the file.", null), await mod);
        Assert.Null(await client.SetSoundboardAsync(true));
        Assert.Null(await client.UpdateClipAsync("airhorn", new Dictionary<string, object> { ["gain"] = 0.5, ["mode"] = "hold" }));
        Assert.Null(await client.SetClipOrderAsync(["b", "airhorn"]));
        Assert.Equal("playClip: the soundboard is off", await client.PlayClipAsync("airhorn"));
        Assert.Null(await client.ReleaseClipAsync("airhorn"));
        Assert.Null(await client.StopClipAsync("airhorn"));
        Assert.Null(await client.StopClipsAsync());
        Assert.Null(await client.DeleteClipAsync("airhorn"));
        Assert.Null(await client.RenameClipPageAsync("Intro", ""));
        Assert.Equal(new SoundboardTransferResult(true, "2 clips were exported to board.zip."), await client.ExportSoundboardAsync("/home/me/board.zip"));
        Assert.Equal(new SoundboardTransferResult(true, "1 clip was added from the archive.", Partial: true), await client.ImportSoundboardAsync("/home/me/board.zip", merge: true));

        static string Wire(JsonNode command)
        {
            var copy = command.DeepClone().AsObject();
            copy.Remove("requestId");
            return copy.ToJsonString();
        }
        Assert.Equal(new[]
        {
            """{"path":"/music/airhorn.wav","page":"Intro","cmd":"importClip"}""",
            """{"path":"/music/song.mod","cmd":"importClip"}""",
            """{"cmd":"setSoundboard","enabled":true}""",
            """{"gain":0.5,"mode":"hold","cmd":"updateClip","clip":"airhorn"}""",
            """{"cmd":"setClipOrder","clips":["b","airhorn"]}""",
            """{"cmd":"playClip","clip":"airhorn"}""",
            """{"cmd":"releaseClip","clip":"airhorn"}""",
            """{"cmd":"stopClip","clip":"airhorn"}""",
            """{"cmd":"stopClips"}""",
            """{"cmd":"deleteClip","clip":"airhorn"}""",
            """{"cmd":"renameClipPage","page":"Intro","name":""}""",
            """{"path":"/home/me/board.zip","cmd":"exportSoundboard"}""",
            """{"path":"/home/me/board.zip","merge":true,"cmd":"importSoundboard"}""",
        }, commands.Select(Wire));
    }

    private static TaskCompletionSource Completion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
