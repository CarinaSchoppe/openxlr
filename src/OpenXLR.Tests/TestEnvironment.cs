using System.Runtime.CompilerServices;

namespace OpenXLR.Tests;

/// <summary>Default writable and plugin locations belong to this test process.</summary>
internal static class TestEnvironment
{
    internal static string Root { get; } = Directory.CreateTempSubdirectory("openxlr-tests-").FullName;

    [ModuleInitializer]
    internal static void Initialize()
    {
        foreach ((string variable, string folder) in new[]
                 {
                     ("XDG_CONFIG_HOME", "config"), ("XDG_DATA_HOME", "data"),
                     ("XDG_CACHE_HOME", "cache"), ("XDG_STATE_HOME", "state"),
                     ("CLAP_PATH", "clap"), ("VST3_PATH", "vst3"), ("WINEPREFIX", "wine"),
                 })
            Environment.SetEnvironmentVariable(variable, Directory.CreateDirectory(Path.Combine(Root, folder)).FullName);

        // The starter sounds are copied beside the tests, and a daemon would
        // add them to every board a test starts. Each service reads them from
        // a folder that does not exist, unless its test asks for the set.
        OpenXLR.Daemon.MixerService.DefaultStarterSoundsDirectory = Path.Combine(Root, "no-starter-sounds");

        // Keep the sandbox for background catalogue tasks until the process
        // exits. Restoring the desktop environment could start user plugins.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
    }
}
