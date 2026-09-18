using OpenXLR.UI;
using Tmds.DBus.Protocol;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class KWinFocusTests
{
    [KWinFact]
    public async Task LiveKWinReportsFocusAndUnloadsItsReadOnlyScript()
    {
        string directory = Path.Combine(Path.GetTempPath(), "openxlr-kwin-" + Guid.NewGuid());
        string? oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
            using var connection = new DBusConnection(DBusAddress.Session!);
            await connection.ConnectAsync();
            var focus = new KWinFocus(new DesktopBus(connection));
            connection.AddMethodHandler(focus);
            // No audio command and no shortcut permission prompt. PID zero is
            // valid when only the desktop is focused.
            uint pid = await focus.ReadAsync(CancellationToken.None);
            Assert.True(pid <= int.MaxValue);
            Assert.False(File.Exists(OpenXLR.Core.OpenXlrPaths.ConfigFile("desktop-focus.js")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldConfig);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

internal sealed class KWinFactAttribute : FactAttribute
{
    public KWinFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_TEST_KWIN") != "1")
            Skip = "Set OPENXLR_TEST_KWIN=1 in a KDE Plasma session to query the current focused process without routing audio.";
    }
}
