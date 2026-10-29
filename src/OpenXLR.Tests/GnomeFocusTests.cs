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

    [Theory]
    [InlineData("GNOME-Flashback:GNOME", null, "wayland")]
    [InlineData("GNOME-Flashback:GNOME", "gnome-flashback-metacity", null)]
    [InlineData("gnome-flashback:gnome", null, null)]
    [InlineData("Budgie:GNOME", "budgie-desktop", "wayland")]
    [InlineData("Budgie:GNOME", null, null)]
    [InlineData("GNOME", "gnome-flashback-metacity", null)]
    [InlineData("GNOME", "budgie-desktop", "wayland")]
    [InlineData(null, "gnome-flashback-compiz", null)]
    [InlineData(null, "budgie-desktop", "wayland")]
    public void DesktopsThatOnlyImitateGnomeShellNeverAskForTheExtension(string? current, string? session, string? type)
    {
        string? Env(string key) => key switch
        {
            "XDG_CURRENT_DESKTOP" => current, "XDG_SESSION_DESKTOP" => session, "XDG_SESSION_TYPE" => type,
            "SWAYSOCK" => "/stale", "HYPRLAND_INSTANCE_SIGNATURE" => "stale_hypr", _ => null,
        };
        Assert.Equal(FocusCompositor.Unavailable, CompositorFocus.Select(Env));
        // The same sessions on X11 keep the EWMH source, which needs no extension.
        Assert.Equal(FocusCompositor.X11, CompositorFocus.Select(key => key == "XDG_SESSION_TYPE" ? "x11" : Env(key)));
    }

    [Theory]
    [InlineData("GNOME-Classic:GNOME")]
    [InlineData("ubuntu:GNOME")]
    [InlineData("pop:GNOME")]
    public void OtherGnomeShellSessionsStillUseTheExtension(string current) =>
        Assert.Equal(FocusCompositor.Gnome, CompositorFocus.Select(key => key switch
        { "XDG_CURRENT_DESKTOP" => current, "XDG_SESSION_TYPE" => "wayland", _ => null }));

    [DesktopBusFact]
    public async Task AudioProcessQueriesReadGnomeOnTheDesktopOwnerConnection()
    {
        string[] names = ["XDG_CURRENT_DESKTOP", "XDG_SESSION_TYPE"];
        var saved = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            await using var session = await PrivateBus.Start();
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "GNOME");
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", "wayland");
            using var desktop = new DBusConnection(session.Address);
            await desktop.ConnectAsync();
            desktop.AddMethodHandler(new DesktopFocus(new DesktopBus(desktop)));
            await desktop.RequestNameAsync(DesktopFocus.Service);
            using var shell = new DBusConnection(session.Address);
            await shell.ConnectAsync();
            var source = new Source(desktop.UniqueName!) { Pid = 42 };
            shell.AddMethodHandler(source);
            await shell.RequestNameAsync(GnomeFocus.Service);
            using var requester = new DBusConnection(session.Address);
            await requester.ConnectAsync();
            var client = new DesktopBus(requester);
            Task<string> Query() => client.Call(DesktopFocus.Service, "/org/openxlr/Desktop", DesktopFocus.Service,
                "GetFocusedAudioProcesses", "s", (ref MessageWriter writer) => writer.WriteString("42,73"),
                static (message, _) => message.GetBodyReader().ReadString());

            Assert.Equal("42", await Query());
            source.Error = true;
            var failure = await Assert.ThrowsAsync<DBusErrorReplyException>(Query);
            Assert.Contains("could not read", failure.Message);
            source.Error = false;
            source.Pid = 73;
            Assert.Equal("73", await Query());
            Assert.Equal(3, source.Reads);
            var obsolete = await Assert.ThrowsAsync<DBusErrorReplyException>(() =>
                client.Empty(DesktopFocus.Service, "/org/openxlr/Desktop", DesktopFocus.Service, "GetFocusedProcess"));
            Assert.Equal("org.freedesktop.DBus.Error.UnknownMethod", obsolete.ErrorName);
        }
        finally
        {
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    [DesktopBusFact]
    public async Task OnlyTheDesktopOwnerConnectionReadsFreshFocusAndCanRecoverAnExtensionRestart()
    {
        await using var session = await PrivateBus.Start();
        using var owner = new DBusConnection(session.Address);
        await owner.ConnectAsync();
        await owner.RequestNameAsync(DesktopFocus.Service);
        var focus = new GnomeFocus(new DesktopBus(owner));
        Assert.Contains("no running GNOME Shell", (await Assert.ThrowsAsync<InvalidOperationException>(() => focus.ReadAsync(CancellationToken.None))).Message);
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
            source.Pid = 1;
            await Assert.ThrowsAsync<InvalidOperationException>(() => focus.ReadAsync(CancellationToken.None));
            source.Pid = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => focus.ReadAsync(CancellationToken.None));
            source.Pid = uint.MaxValue;
            await Assert.ThrowsAsync<InvalidOperationException>(() => focus.ReadAsync(CancellationToken.None));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => focus.ReadAsync(CancellationToken.None));
    }

    [DesktopBusFact]
    public async Task AnUnrelatedFocusServiceCannotSpoofShellAndMissingShellDoesNotSuggestAnExtension()
    {
        await using var session = await PrivateBus.Start();
        using var owner = new DBusConnection(session.Address);
        await owner.ConnectAsync();
        await owner.RequestNameAsync(DesktopFocus.Service);
        using var imposter = new DBusConnection(session.Address);
        await imposter.ConnectAsync();
        var fake = new Source(owner.UniqueName!) { Pid = 99 };
        imposter.AddMethodHandler(fake);
        await imposter.RequestNameAsync("org.openxlr.Focus");
        var focus = new GnomeFocus(new DesktopBus(owner));
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => focus.ReadAsync(CancellationToken.None));
        Assert.Contains("no running GNOME Shell", missing.Message);
        Assert.DoesNotContain("Enable", missing.Message);
        Assert.Equal(0, fake.Reads);

        using var shell = new DBusConnection(session.Address);
        await shell.ConnectAsync();
        await shell.RequestNameAsync("org.gnome.Shell");
        var extension = await Assert.ThrowsAsync<InvalidOperationException>(() => focus.ReadAsync(CancellationToken.None));
        Assert.Contains("Enable the OpenXLR Focus GNOME Shell extension", extension.Message);
        shell.AddMethodHandler(new Source(owner.UniqueName!) { Pid = 42 });
        Assert.Equal(42u, await focus.ReadAsync(CancellationToken.None));
        Assert.Equal(0, fake.Reads);
    }

    [DesktopBusFact]
    public async Task AFocusReadErrorDoesNotRetainThePendingRequest()
    {
        await using var session = await PrivateBus.Start();
        using var owner = new DBusConnection(session.Address);
        await owner.ConnectAsync();
        await owner.RequestNameAsync(DesktopFocus.Service);
        using var shell = new DBusConnection(session.Address);
        await shell.ConnectAsync();
        var source = new Source(owner.UniqueName!) { Error = true };
        shell.AddMethodHandler(source);
        await shell.RequestNameAsync(GnomeFocus.Service);
        var focus = new GnomeFocus(new DesktopBus(owner));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => focus.ReadAsync(CancellationToken.None));
        Assert.Contains("could not read", failure.Message);
        source.Error = false;
        source.Pid = 42;
        Assert.Equal(42u, await focus.ReadAsync(CancellationToken.None));
    }

    [DesktopBusTheory]
    [InlineData("s")]
    [InlineData("b")]
    [InlineData("i")]
    [InlineData("uu")]
    public async Task InvalidReplySignaturesCannotBecomeAProcessIdOrCloseDesktopKeys(string signature)
    {
        await using var session = await PrivateBus.Start();
        using var owner = new DBusConnection(session.Address);
        await owner.ConnectAsync();
        await owner.RequestNameAsync(DesktopFocus.Service);
        var bus = new DesktopBus(owner);
        var focus = new GnomeFocus(bus);
        using var shell = new DBusConnection(session.Address);
        await shell.ConnectAsync();
        var source = new Source(owner.UniqueName!) { Signature = signature, Pid = 42 };
        shell.AddMethodHandler(source);
        await shell.RequestNameAsync(GnomeFocus.Service);
        var invalid = await Assert.ThrowsAsync<InvalidOperationException>(() => focus.ReadAsync(CancellationToken.None));
        Assert.Contains("invalid focus reply", invalid.Message);
        await bus.Empty("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "GetId");
        source.Signature = "u";
        source.Pid = 73;
        Assert.Equal(73u, await focus.ReadAsync(CancellationToken.None));
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
        public string Signature = "u";
        public bool Hold, Error;
        public int Reads;
        public List<MethodContext> Held { get; } = [];
        public string Path => GnomeFocus.Path;
        public bool HandlesChildPaths => false;
        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.Request.InterfaceAsString != GnomeFocus.Interface || context.Request.MemberAsString != "GetFocusedProcess")
                context.ReplyUnknownMethodError();
            else if (context.Request.SenderAsString != owner)
                context.ReplyError("org.freedesktop.DBus.Error.AccessDenied", "Only the desktop owner may read focus.");
            else
            {
                Reads++;
                if (Error) context.ReplyError("org.openxlr.Focus.Unavailable", "GNOME Shell could not read the focused application.");
                else if (Hold) { context.DisposesAsynchronously = true; Held.Add(context); }
                else Reply(context);
            }
            return ValueTask.CompletedTask;
        }
        private void Reply(MethodContext context)
        {
            using var reply = context.CreateReplyWriter(Signature);
            if (Signature == "s") reply.WriteString(new string('x', (int)Pid));
            else if (Signature == "b") reply.WriteBool(Pid > 0);
            else if (Signature == "i") reply.WriteInt32((int)Pid);
            else
            {
                reply.WriteUInt32(Pid);
                if (Signature == "uu") reply.WriteUInt32(Pid);
            }
            context.Reply(reply.CreateMessage());
        }
        public void Release()
        {
            foreach (var context in Held) { Reply(context); context.Dispose(); }
            Held.Clear();
        }
    }
}
