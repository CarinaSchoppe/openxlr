using OpenXLR.Core;

namespace OpenXLR.Tests;

// Both stores read XDG_CONFIG_HOME, which these tests redirect, so they must not run in parallel.
[Collection("xdg-config")]
public sealed class ProfileRecallTests
{
    [Fact]
    public void ALegacyNameCollisionDoesNotHideOtherProfilesOrOverwriteEitherCopy()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-profile-migration-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var migrated = typeof(ProfileStore).GetField("_migrated",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        object? wasMigrated = migrated.GetValue(null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
        try
        {
            string root = Directory.CreateDirectory(Path.Combine(directory, "openxlr", "profiles")).FullName;
            File.WriteAllText(Path.Combine(root, "First.json"), "{\"device\":{\"gainDb\":30}}");
            File.WriteAllText(Path.Combine(root, "Second.json"), "{\"device\":{\"gainDb\":40}}");
            // Whichever file the filesystem enumerates first collides; the
            // other must still migrate, independent of directory ordering.
            string[] files = Directory.GetFiles(root, "*.json");
            string collision = Path.GetFileNameWithoutExtension(files[0]);
            string unique = Path.GetFileNameWithoutExtension(files[1]);
            string original = File.ReadAllText(files[0]);
            string scoped = Directory.CreateDirectory(Path.Combine(root, "0fd9-00b4")).FullName;
            File.WriteAllText(Path.Combine(scoped, collision + ".json"), "{\"device\":{\"gainDb\":50}}");
            migrated.SetValue(null, false);

            Assert.Equal(new[] { collision, unique }.OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
                ProfileStore.List("0fd9:00b4"));
            Assert.Equal(50, ProfileStore.Load("0fd9:00b4", collision)!.Device!.GainDb);
            Assert.Equal(original, File.ReadAllText(files[0]));
            Assert.False(File.Exists(files[1]));
        }
        finally
        {
            migrated.SetValue(null, wasMigrated);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RecallOnConnectRoundTripsAndFollowsTheProfile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            const string dev = "0fd9:007d";
            Assert.Null(ProfileStore.RecallOnConnect(dev));

            ProfileStore.Save(dev, "Streaming", new Profile());
            ProfileStore.SetRecallOnConnect(dev, "Streaming");
            Assert.Equal("Streaming", ProfileStore.RecallOnConnect(dev));
            Assert.Equal(["Streaming"], ProfileStore.List(dev));   // the marker is not listed as a profile

            ProfileStore.SetRecallOnConnect(dev, null);
            Assert.Null(ProfileStore.RecallOnConnect(dev));

            ProfileStore.SetRecallOnConnect(dev, "Streaming");
            Assert.True(ProfileStore.Delete(dev, "Streaming"));
            Assert.Null(ProfileStore.RecallOnConnect(dev));   // deleting the profile clears the marker
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
