using System.Text;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class ExtendedCompositorFocusTests
{
    [Theory]
    [InlineData("GNOME", "wayland", "wayland-0", null, "Gnome")]
    [InlineData("ubuntu:GNOME", "wayland", "wayland-0", null, "Gnome")]
    [InlineData("niri", "wayland", "wayland-0", "/run/user/1000/niri.sock", "Niri")]
    [InlineData("XFCE", "x11", null, null, "X11")]
    [InlineData("X-Cinnamon", "x11", null, null, "X11")]
    [InlineData("MATE", "x11", null, null, "X11")]
    [InlineData("GNOME", "x11", null, null, "X11")]
    [InlineData("unknown", "wayland", "wayland-0", "/stale/niri.sock", "Unavailable")]
    [InlineData("GNOME:Hyprland", "wayland", "wayland-0", null, "Unavailable")]
    [InlineData(null, "wayland", "wayland-0", "/run/user/1000/niri.sock", "Niri")]
    public void SelectsTheSessionsOwnSource(string? desktop, string type, string? wayland, string? niri, string expected)
    {
        string? Env(string key) => key switch
        {
            "XDG_CURRENT_DESKTOP" => desktop, "XDG_SESSION_TYPE" => type, "WAYLAND_DISPLAY" => wayland,
            "DISPLAY" => ":1", "NIRI_SOCKET" => niri, _ => null,
        };
        Assert.Equal(Enum.Parse<FocusCompositor>(expected), CompositorFocus.Select(Env));
    }

    [Theory]
    [InlineData("{\"pid\":42,\"is_focused\":true,\"title\":\"ignored\"}", true)]
    [InlineData("{\"pid\":2147483647,\"is_focused\":true}", true)]
    [InlineData("null", false)]
    [InlineData("{}", false)]
    [InlineData("{\"pid\":42,\"is_focused\":false}", false)]
    [InlineData("{\"pid\":42}", false)]
    [InlineData("{\"pid\":null,\"is_focused\":true}", false)]
    [InlineData("{\"pid\":0,\"is_focused\":true}", false)]
    [InlineData("{\"pid\":-1,\"is_focused\":true}", false)]
    [InlineData("{\"pid\":4294967295,\"is_focused\":true}", false)]
    [InlineData("{\"pid\":\"42\",\"is_focused\":true}", false)]
    [InlineData("{\"pid\":42,\"pid\":43,\"is_focused\":true}", false)]
    public void NiriRequiresOneFocusedProcess(string text, bool accepted)
    {
        if (accepted) Assert.True(CompositorFocus.Parse(FocusCompositor.Niri, Encoding.UTF8.GetBytes(text)) > 0);
        else Assert.Throws<InvalidOperationException>(() => CompositorFocus.Parse(FocusCompositor.Niri, Encoding.UTF8.GetBytes(text)));
    }

    [Theory]
    [InlineData("_NET_ACTIVE_WINDOW(WINDOW) = 0x42", true)]
    [InlineData("_NET_ACTIVE_WINDOW(WINDOW) = 0x0", false)]
    [InlineData("_NET_ACTIVE_WINDOW(STRING) = 0x42", false)]
    [InlineData("_NET_ACTIVE_WINDOW(WINDOW) = 0x42, 0x43", false)]
    [InlineData("_NET_ACTIVE_WINDOW(WINDOW) = 0x100000000", false)]
    [InlineData("_NET_ACTIVE_WINDOW: not found.", false)]
    public void X11RequiresOneActiveWindow(string text, bool accepted)
    {
        if (accepted) Assert.Equal(66u, X11Focus.ParseWindow(text));
        else Assert.Throws<InvalidOperationException>(() => X11Focus.ParseWindow(text));
    }

    [Theory]
    [InlineData("42", "local", true)]
    [InlineData("2147483647", "LOCAL", true)]
    [InlineData("0", "local", false)]
    [InlineData("4294967295", "local", false)]
    [InlineData("42, 43", "local", false)]
    [InlineData("42", "remote", false)]
    public void X11RequiresALocalProcess(string pid, string machine, bool accepted)
    {
        string text = $"_NET_WM_PID(CARDINAL) = {pid}\nWM_CLIENT_MACHINE(STRING) = \"{machine}\"\n";
        if (accepted) Assert.True(X11Focus.ParseProcess(text, "local") > 0);
        else Assert.Throws<InvalidOperationException>(() => X11Focus.ParseProcess(text, "local"));
    }
}
