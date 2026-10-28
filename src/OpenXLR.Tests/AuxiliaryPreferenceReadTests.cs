using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;
using OpenXLR.UI;
using ProcessRunner = OpenXLR.Core.ProcessRunner;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class AuxiliaryPreferenceReadTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-aux-prefs-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public AuxiliaryPreferenceReadTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
        Directory.CreateDirectory(OpenXLR.Core.OpenXlrPaths.ConfigDir);
    }

    private static string PathFor(string name) => OpenXLR.Core.OpenXlrPaths.ConfigFile(name);
    private static IConfiguration Configuration => new ConfigurationBuilder().Build();
    private static DeviceManager Manager() => new(NullLogger<DeviceManager>.Instance, Configuration, () => []);

    private static string Content(string name) => name switch
    {
        "desktop-keys.json" => """{"enabled":true,"focusChannels":["music"]}""",
        "wave-interfaces.json" => JsonSerializer.Serialize(new[]
        {
            new DeviceInfo("Elgato", "XLR Dock", 0x0fd9, 0x00a6)
                { Location = new(1, 2, "1-2", "fixture") }.InstanceId,
        }),
        _ => """["0fd9:00a6"]""",
    };

    private static (int Count, string? Warning) Read(string name)
    {
        if (name == "desktop-keys.json") return (DesktopKeySettings.Load().FocusChannels.Count, null);
        using DeviceManager primary = Manager();
        if (name == "wave-interfaces.json")
        {
            using var units = new WaveInterfaces(primary, NullLogger<DeviceManager>.Instance, Configuration, () => []);
            return (units.Snapshot().Count, units.Warning);
        }
        // Inspect the loaded set without claiming or writing a USB device.
        var locks = (HashSet<string>)typeof(DeviceManager).GetField("_gainLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(primary)!;
        return (locks.Count, null);
    }

    [Theory]
    [InlineData("gainlock.json")]
    [InlineData("wave-interfaces.json")]
    [InlineData("desktop-keys.json")]
    public void OrdinaryAuxiliaryPreferencesStillLoad(string name)
    {
        string text = Content(name);
        File.WriteAllText(PathFor(name), text);
        var result = Read(name);
        Assert.Equal(1, result.Count);
        Assert.Null(result.Warning);
        Assert.Equal(text, File.ReadAllText(PathFor(name)));
    }

    [Theory]
    [InlineData("gainlock.json")]
    [InlineData("wave-interfaces.json")]
    [InlineData("desktop-keys.json")]
    public void OversizedAuxiliaryPreferencesAreIgnoredWithoutBeingChanged(string name)
    {
        string text = Content(name) + new string(' ', OpenXLR.Core.OpenXlrPaths.MaxPreferenceBytes);
        File.WriteAllText(PathFor(name), text);
        var result = Read(name);
        Assert.Equal(0, result.Count);
        if (name == "wave-interfaces.json") Assert.NotNull(result.Warning);
        Assert.Equal(text, File.ReadAllText(PathFor(name)));
    }

    [Theory]
    [InlineData("gainlock.json", false)]
    [InlineData("gainlock.json", true)]
    [InlineData("wave-interfaces.json", false)]
    [InlineData("wave-interfaces.json", true)]
    [InlineData("desktop-keys.json", false)]
    [InlineData("desktop-keys.json", true)]
    public async Task AnAuxiliaryPreferencePipeCannotBlockStartup(string name, bool symbolicLink)
    {
        if (!OperatingSystem.IsLinux()) return;
        string path = PathFor(name);
        string pipe = symbolicLink ? Path.Combine(_directory, "pipe") : path;
        Assert.True(ProcessRunner.Run("mkfifo", [pipe]).Ok);
        if (symbolicLink) File.CreateSymbolicLink(path, pipe);
        var reading = Task.Run(() => Read(name));
        try
        {
            var result = await reading.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, result.Count);
            if (name == "wave-interfaces.json") Assert.NotNull(result.Warning);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (!reading.IsCompleted)
            {
                // Unblock the old reader so a failing regression leaves no
                // pool thread parked on the pipe. Closing gives it EOF.
                using var release = new FileStream(pipe, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                release.Write(Encoding.UTF8.GetBytes("[]"));
            }
            await reading.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
        Directory.Delete(_directory, true);
    }
}
