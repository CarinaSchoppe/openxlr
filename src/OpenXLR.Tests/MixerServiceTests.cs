using OpenXLR.Daemon;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenXLR.Tests;

public sealed class MixerServiceTests
{
    [Fact]
    public void SingletonAndHostedRegistrationCanDisposeTheServiceTwice()
    {
        var config = new ConfigurationBuilder().Build();
        using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config);
        var service = new MixerService(NullLogger<MixerService>.Instance, config, devices);
        service.Dispose();
        service.Dispose();
    }
    [Theory]
    [InlineData("--mixer", true)]
    [InlineData("--MIXER", true)]
    [InlineData("--mixer=false", false)]
    [InlineData("--monitorOutput", false)]
    public void BareMixerSwitchMatchesOnlyDocumentedFlag(string argument, bool expected)
        => Assert.Equal(expected, MixerService.HasBareMixerSwitch(["OpenXLR.Daemon", argument]));
}
