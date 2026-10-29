using OpenXLR.UI;
using Tmds.DBus.Protocol;
using PrivateBus = OpenXLR.Tests.DesktopKeysTests.PrivateBus;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class GnomeFocusTests
{
    [Theory]
    [InlineData("GNOME")]
    [InlineData("ubuntu:GNOME")]
    [InlineData("gnome-classic")]
    public void GnomeHasItsOwnSourceWithoutFallingBackToStaleIpcHints(string desktop)
    {
        Assert.Equal(FocusCompositor.Gnome, CompositorFocus.Select(key => key switch
        { "XDG_CURRENT_DESKTOP" => desktop, "SWAYSOCK" => "/stale", "HYPRLAND_INSTANCE_SIGNATURE" => "stale_hypr", _ => null }));
        Assert.Equal(FocusCompositor.Unavailable, CompositorFocus.Select(key => key == "XDG_CURRENT_DESKTOP" ? "GNOME:Hyprland" : null));
    }

    [DesktopBusFact]
    public async Task OnlyTheDesktopOwnerConnectionReadsFreshFocusAndCanRecoverAnExtensionRestart()
    {
        await using var session = await PrivateBus.Start();
        using var owner = new DBusConnection(session.Address);
        await owner.ConnectAsync();
        await owner.RequestNameAsync(DesktopFocus.Service);
        var focus = new GnomeFocus(new DesktopBus(owner));
        Assert.Contains("GNOME Shell extension", (await Assert.ThrowsAsync<InvalidOperationException>(() => focus.ReadAsync(CancellationToken.None))).Message);
        using (var shell = new DBusConnection(session.Address))
        {
            await shell.ConnectAsync();
            var source = new Source(owner.UniqueName!) { Pid = 42 };
            shell.AddMethodHandler(source);
            await shell.RequestNameAsync(GnomeFocus.Service);
            Assert.Equal(42u, await focus.ReadAsync(CancellationToken.None));
            source.Pid = 73;
            Assert.Equal(73u, await focus.ReadAsync(CancellationToken.None));
            using var stranger = new DBusConnection(session.Address);
            await stranger.ConnectAsync();
            var denied = await Assert.ThrowsAsync<InvalidOperationException>(() => new GnomeFocus(new DesktopBus(stranger)).ReadAsync(CancellationToken.None));
            Assert.Contains("restricted", denied.Message);
            Assert.Equal(2, source.Reads);
            source.Pid = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => focus.ReadAsync(CancellationToken.None));
            source.Pid = uint.MaxValue;
            await Assert.ThrowsAsync<InvalidOperationException>(() => focus.ReadAsync(CancellationToken.None));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => focus.ReadAsync(CancellationToken.None));
    }

    [DesktopBusFact]
    public async Task AStalledExtensionKeepsOnePendingReplyWithoutClosingTheDesktopOwner()
    {
        await using var session = await PrivateBus.Start();
        using var owner = new DBusConnection(session.Address);
        await owner.ConnectAsync();
        await owner.RequestNameAsync(DesktopFocus.Service);
        var bus = new DesktopBus(owner);
        var focus = new GnomeFocus(bus);
        using var shell = new DBusConnection(session.Address);
        await shell.ConnectAsync();
        var source = new Source(owner.UniqueName!) { Hold = true, Pid = 73 };
        shell.AddMethodHandler(source);
        await shell.RequestNameAsync(GnomeFocus.Service);
        try
        {
            var timeout = await Assert.ThrowsAsync<InvalidOperationException>(() => focus.ReadAsync(CancellationToken.None));
            Assert.Contains("two seconds", timeout.Message);
            for (int i = 0; i < 4; i++)
                Assert.Contains("previous focus query", (await Assert.ThrowsAsync<InvalidOperationException>(() => focus.ReadAsync(CancellationToken.None))).Message);
            Assert.Single(source.Held);
            await bus.Empty("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "GetId");
            source.Hold = false;
            source.Release();
            source.Pid = 42;
            using var recovered = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (true)
            {
                try { Assert.Equal(42u, await focus.ReadAsync(recovered.Token)); break; }
                catch (InvalidOperationException ex) when (ex.Message.Contains("previous focus query", StringComparison.Ordinal))
                { await Task.Delay(10, recovered.Token); }
            }
            source.Hold = true;
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => focus.ReadAsync(cancel.Token));
            await bus.Empty("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "GetId");
        }
        finally { source.Release(); }
    }

    private sealed class Source(string owner) : IPathMethodHandler
    {
        public uint Pid;
        public bool Hold;
        public int Reads;
        public List<MethodContext> Held { get; } = [];
        public string Path => GnomeFocus.Path;
        public bool HandlesChildPaths => false;
        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.Request.InterfaceAsString != GnomeFocus.Service || context.Request.MemberAsString != "GetFocusedProcess")
                context.ReplyUnknownMethodError();
            else if (context.Request.SenderAsString != owner)
                context.ReplyError("org.freedesktop.DBus.Error.AccessDenied", "Only the desktop owner may read focus.");
            else
            {
                Reads++;
                if (Hold) { context.DisposesAsynchronously = true; Held.Add(context); }
                else Reply(context);
            }
            return ValueTask.CompletedTask;
        }
        private void Reply(MethodContext context)
        {
            using var reply = context.CreateReplyWriter("u");
            reply.WriteUInt32(Pid);
            context.Reply(reply.CreateMessage());
        }
        public void Release()
        {
            foreach (var context in Held) { Reply(context); context.Dispose(); }
            Held.Clear();
        }
    }
}
