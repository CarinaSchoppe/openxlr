using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class CardProfileTests : IDisposable
{
    private const string Fragment = "Elgato_Wave_XLR_Pro";
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-card-profile-").FullName;
    private readonly string? _path = Environment.GetEnvironmentVariable("PATH");

    public CardProfileTests()
    {
        ExecutableScript.Write(Path.Combine(_directory, "pw-dump"), """
            cat "$(dirname "$0")/dump.json"
            """);
        ExecutableScript.Write(Path.Combine(_directory, "wpctl"), """
            printf '%s\n' "$*" >> "$(dirname "$0")/writes"
            """);
        Environment.SetEnvironmentVariable("PATH", _directory + Path.PathSeparator + _path);
    }

    [Fact]
    public void ParkingAndRestoringUseTheLatestCardAndProfileIndices()
    {
        Dump($"[{Card(7, "HiFi", 4)}]\n[{Card(7, "HiFi", 9)}]");
        Assert.Equal(("HiFi", "HiFi"), CardProfile.EnsureProAudio(Fragment));
        Assert.Equal(["set-profile 7 9"], Writes());

        Dump($$"""
            [{{Card(7, "pro-audio", 9)}}]
            [{"id":7,"info":null},{{Card(99, "pro-audio", 12, 6)}}]
            """);
        CardProfile.SetProfile(Fragment, "HiFi");
        Assert.Equal(["set-profile 7 9", "set-profile 99 6"], Writes());
    }

    [Theory]
    [InlineData("\"info\":null")]
    [InlineData("\"props\":null")]
    public void ARemovedCardIsNeverSelected(string tombstone)
    {
        Dump($$"""[{{Card(7, "HiFi")}}] [{"id":7,{{tombstone}}}]""");
        Assert.Equal((null, null), CardProfile.EnsureProAudio(Fragment));
        CardProfile.SetProfile(Fragment, "HiFi");
        Assert.Empty(Writes());
    }

    [Fact]
    public void ADeviceWithoutInfoDoesNotHideThePresentCard()
    {
        Dump($$"""[{"id":8,"type":"PipeWire:Interface:Device","info":null},{{Card(9, "Direct")}}]""");
        Assert.Equal(("Direct", "Direct"), CardProfile.EnsureProAudio(Fragment));
        Assert.Equal(["set-profile 9 3"], Writes());
    }

    [Theory]
    [InlineData("pro-audio")]
    [InlineData("off")]
    public void NonUcmProfilesStayUntouched(string active)
    {
        Dump($"[{Card(7, active)}]");
        Assert.Equal((active, null), CardProfile.EnsureProAudio(Fragment));
        Assert.Empty(Writes());
    }

    [Fact]
    public void IncompleteUpdatesCannotWriteAStaleProfile()
    {
        Dump($"[{Card(7, "HiFi")}] [");
        Assert.ThrowsAny<JsonException>(() => CardProfile.EnsureProAudio(Fragment));
        Assert.Empty(Writes());
    }

    private static string Card(int id, string active, int pro = 3, int hifi = 2) => $$"""
        {"id":{{id}},"type":"PipeWire:Interface:Device","info":{
          "props":{"device.name":"alsa_card.usb-Elgato_Wave_XLR_Pro-00"},
          "params":{"EnumProfile":[{"name":"pro-audio","index":{{pro}}},{"name":"HiFi","index":{{hifi}}}],
                    "Profile":[{"name":{{JsonSerializer.Serialize(active)}}}] } } }
        """;

    private void Dump(string value) => File.WriteAllText(Path.Combine(_directory, "dump.json"), value);
    private string[] Writes() => File.Exists(Path.Combine(_directory, "writes"))
        ? File.ReadAllLines(Path.Combine(_directory, "writes")) : [];

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _path);
        Directory.Delete(_directory, true);
    }
}
