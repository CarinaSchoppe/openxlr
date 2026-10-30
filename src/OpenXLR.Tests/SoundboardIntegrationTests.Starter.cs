using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed partial class SoundboardIntegrationTests
{
    /// <summary>
    /// The whole daemon with the starter sounds that ship beside it: the
    /// first start adds all eighteen on the Starter page and saves that it
    /// did; the next start adds nothing, so a starter clip deleted meanwhile
    /// stays deleted until restoreStarterSounds adds it back.
    /// </summary>
    [MonitorPipeWireFact]
    public async Task TheDaemonAddsTheStarterSoundsOnceAndRestoresADeletedOne()
    {
        string? previousConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? previousData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_directory, "starter-config"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_directory, "starter-data"));
        try
        {
            new DaemonSettings { Submixer = true }.Save();
            int shipped = StarterSounds.Read(StarterSounds.Directory, []).Count;
            Assert.Equal(18, shipped);

            await WithService(async hub =>
            {
                Assert.True(SpinWait.SpinUntil(() => hub.Snapshot().Mixer!.Soundboard.Clips.Count == shipped, TimeSpan.FromSeconds(60)),
                    hub.Snapshot().Mixer!.Soundboard.ToString());
                SoundboardState board = hub.Snapshot().Mixer!.Soundboard;
                Assert.All(board.Clips, c => Assert.Equal("Starter", c.Page));
                Assert.Equal(["Starter"], board.Pages);
                SoundboardLibrary saved = SoundboardLibrary.Load(null, out string? warning);
                Assert.Null(warning);
                Assert.True(saved.StarterAdded);
                Assert.Equal(shipped, saved.Clips.Count(c => c.Starter.Length > 0));
                Assert.Equal(shipped, Directory.GetFiles(SoundboardLibrary.ClipDirectory, "*.ogg").Length);

                Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"deleteClip","clip":"cheer"}""")).Ok);
            });

            await WithService(async hub =>
            {
                // Nothing is added at this start: the library says the set was added.
                Assert.Equal(shipped - 1, hub.Snapshot().Mixer!.Soundboard.Clips.Count);
                Assert.DoesNotContain(hub.Snapshot().Mixer!.Soundboard.Clips, c => c.Name == "Cheer");

                ApiCommandResult restored = await hub.ExecuteForApiAsync("""{"cmd":"restoreStarterSounds"}""");
                Assert.True(restored.Ok);
                StarterSoundsMessage reply = restored.Messages.OfType<StarterSoundsMessage>().Single();
                Assert.Equal(["cheer-2"], reply.Clips);
                Assert.Equal(shipped, hub.Snapshot().Mixer!.Soundboard.Clips.Count);

                reply = (await hub.ExecuteForApiAsync("""{"cmd":"restoreStarterSounds"}""")).Messages.OfType<StarterSoundsMessage>().Single();
                Assert.True(reply.Ok);
                Assert.Empty(reply.Clips);
                Assert.Equal("Every starter sound is on the soundboard already.", reply.Message);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previousConfig);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previousData);
        }
    }

    /// <summary>A daemon reading the starter sounds from where the packages install them, started and stopped around <paramref name="run"/>.</summary>
    private static async Task WithService(Func<WebSocketHub, Task> run)
    {
        var config = new ConfigurationBuilder().Build();
        using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
        using var service = new MixerService(NullLogger<MixerService>.Instance, config, devices) { StarterSoundsDirectory = StarterSounds.Directory };
        using var lifetime = new SocketLifetime();
        var hub = new WebSocketHub(devices, service, NullLogger<WebSocketHub>.Instance, lifetime);
        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(service.Built);
            await run(hub);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
