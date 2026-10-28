using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class LiveCompositorFocusTests
{
    [LiveFocusFact]
    public async Task CurrentCompositorReportsTheFixtureWindowsActualProcessThroughTheDesktopService()
    {
        Assert.True(uint.TryParse(Environment.GetEnvironmentVariable("OPENXLR_TEST_FOCUSED_PID"), out uint expected) && expected > 0);
        FocusCompositor source = Environment.GetEnvironmentVariable("OPENXLR_TEST_FOCUS_SOURCE") switch
        {
            "gnome" => FocusCompositor.Gnome, "sway" => FocusCompositor.Sway,
            "niri" => FocusCompositor.Niri, "x11" => FocusCompositor.X11, _ => throw new InvalidOperationException("Choose a live focus source."),
        };
        Assert.Equal(source, CompositorFocus.Select());
        Assert.Equal(expected, await CompositorFocus.ReadAsync(source, CancellationToken.None));
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        using var keys = new DesktopKeys(client);
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true }, save: false);
        Assert.Equal((int)expected, await Task.Run(DesktopFocusQuery.Read));
        string[] audio = await Task.Run(() => DesktopFocusQuery.ReadAudioProcesses([expected.ToString(), int.MaxValue.ToString()]));
        Assert.Equal([expected.ToString()], audio);
    }
}

internal sealed class LiveFocusFactAttribute : FactAttribute
{
    public LiveFocusFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_TEST_FOCUS_SOURCE") is null)
            Skip = "Use the isolated compositor acceptance runner or set OPENXLR_TEST_FOCUS_SOURCE and OPENXLR_TEST_FOCUSED_PID in a live session.";
    }
}
