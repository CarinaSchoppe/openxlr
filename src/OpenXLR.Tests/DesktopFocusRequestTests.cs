using OpenXLR.UI;
using Tmds.DBus.Protocol;
using PrivateBus = OpenXLR.Tests.DesktopKeysTests.PrivateBus;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class DesktopFocusRequestTests
{
    [DesktopBusFact]
    public async Task OneQueryIncludesProcessMappingAndARefusalDoesNotReleaseTheActiveQuery()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        async Task<string> Match(int pid, FocusProcesses.Candidate[] candidates, CancellationToken cancel)
        {
            Assert.Equal(42, pid);
            Assert.Equal([new FocusProcesses.Candidate(42, null)], candidates);
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancel);
            }
            return "42";
        }
        await WithService(async (client, _) =>
        {
            Task<string> pending = Query(client, "42");
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                for (int i = 0; i < 2; i++)
                {
                    var busy = await Assert.ThrowsAsync<DBusErrorReplyException>(() => Query(client, "42"));
                    Assert.Contains("already running", busy.Message);
                    Assert.Equal(1, Volatile.Read(ref calls));
                }
            }
            finally { release.TrySetResult(); await pending; }
            Assert.Equal("42", await Query(client, "42"));
            Assert.Equal(2, calls);
        }, Match);
    }

    [DesktopBusTheory]
    [InlineData("empty")]
    [InlineData("invalid")]
    [InlineData("oversized")]
    public async Task UnusableListsDoNotReadFocusAndTheNextValidQueryStillWorks(string kind)
    {
        string keys = kind switch { "empty" => "", "invalid" => "7@invalid", _ => "42," + new string('ä', 17000) };
        await WithService(async (client, directory) =>
        {
            await Assert.ThrowsAsync<DBusErrorReplyException>(() => Query(client, keys));
            Assert.False(File.Exists(Path.Combine(directory, "reads")), "The rejected query reached the compositor.");
            Assert.Equal("42", await Query(client, "42"));
            Assert.Equal("read", File.ReadAllText(Path.Combine(directory, "reads")));
        });
    }

    private static Task<string> Query(DBusConnection connection, string keys) => new DesktopBus(connection).Call(
        DesktopFocus.Service, "/org/openxlr/Desktop", DesktopFocus.Service, "GetFocusedAudioProcesses", "s",
        (ref MessageWriter writer) => writer.WriteString(keys), static (message, _) => message.GetBodyReader().ReadString());

    private static async Task WithService(Func<DBusConnection, string, Task> check,
        Func<int, FocusProcesses.Candidate[], CancellationToken, Task<string>>? match = null)
    {
        await using var session = await PrivateBus.Start();
        string directory = Directory.CreateTempSubdirectory("ox-focus-request-").FullName;
        string? path = Environment.GetEnvironmentVariable("PATH");
        string? desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        string? sway = Environment.GetEnvironmentVariable("SWAYSOCK");
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "swaymsg"), """
                dir=${0%/*}
                printf read >> "$dir/reads"
                printf '%s' '{"nodes":[{"focused":true,"pid":42}]}'
                """);
            Environment.SetEnvironmentVariable("PATH", directory + ":" + path);
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "sway");
            Environment.SetEnvironmentVariable("SWAYSOCK", "/tmp/openxlr-sway.sock");
            using var server = new DBusConnection(session.Address);
            await server.ConnectAsync();
            server.AddMethodHandler(new DesktopFocus(new DesktopBus(server), match));
            await server.RequestNameAsync(DesktopFocus.Service);
            using var client = new DBusConnection(session.Address);
            await client.ConnectAsync();
            await check(client, directory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", desktop);
            Environment.SetEnvironmentVariable("SWAYSOCK", sway);
            Directory.Delete(directory, true);
        }
    }
}
