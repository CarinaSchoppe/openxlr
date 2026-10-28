using OpenXLR.UI;
using Tmds.DBus.Protocol;
using PrivateBus = OpenXLR.Tests.DesktopKeysTests.PrivateBus;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class GnomeFocusTests
{
    [DesktopBusFact]
    public async Task ExtensionMayAppearDisappearAndChangeFocusWithoutReturningCachedIdentity()
    {
        await using var session = await PrivateBus.Start();
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => GnomeFocus.ReadAsync(CancellationToken.None));
        Assert.Contains("GNOME Shell extension", missing.Message);
        using (var shell = new DBusConnection(session.Address))
        {
            await shell.ConnectAsync();
            var source = new Source { Pid = 42 };
            shell.AddMethodHandler(source);
            await shell.RequestNameAsync(GnomeFocus.Service);
            Assert.Equal(42u, await GnomeFocus.ReadAsync(CancellationToken.None));
            source.Pid = 73;
            Assert.Equal(73u, await GnomeFocus.ReadAsync(CancellationToken.None));
            source.Pid = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => GnomeFocus.ReadAsync(CancellationToken.None));
            source.Pid = uint.MaxValue;
            await Assert.ThrowsAsync<InvalidOperationException>(() => GnomeFocus.ReadAsync(CancellationToken.None));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => GnomeFocus.ReadAsync(CancellationToken.None));
    }

    [DesktopBusFact]
    public async Task AStalledExtensionIsBoundedAndCallerCancellationIsPreserved()
    {
        await using var session = await PrivateBus.Start();
        using var shell = new DBusConnection(session.Address);
        await shell.ConnectAsync();
        var source = new Source { Hold = true };
        shell.AddMethodHandler(source);
        await shell.RequestNameAsync(GnomeFocus.Service);
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GnomeFocus.ReadAsync(cancel.Token));
            var timeout = await Assert.ThrowsAsync<InvalidOperationException>(() => GnomeFocus.ReadAsync(CancellationToken.None));
            Assert.Contains("two seconds", timeout.Message);
            source.Hold = false;
            source.Pid = 42;
            Assert.Equal(42u, await GnomeFocus.ReadAsync(CancellationToken.None));
        }
        finally { foreach (var context in source.Held) context.Dispose(); }
    }

    private sealed class Source : IPathMethodHandler
    {
        public uint Pid;
        public bool Hold;
        public List<MethodContext> Held { get; } = [];
        public string Path => GnomeFocus.Path;
        public bool HandlesChildPaths => false;
        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.Request.InterfaceAsString == GnomeFocus.Service && context.Request.MemberAsString == "GetFocusedProcess"
                && context.Request.SignatureAsString == "")
            {
                if (Hold)
                {
                    context.DisposesAsynchronously = true;
                    Held.Add(context);
                    return ValueTask.CompletedTask;
                }
                using var reply = context.CreateReplyWriter("u");
                reply.WriteUInt32(Pid);
                context.Reply(reply.CreateMessage());
            }
            else context.ReplyUnknownMethodError();
            return ValueTask.CompletedTask;
        }
    }
}
