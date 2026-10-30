using OpenXLR.Core.Devices;

namespace OpenXLR.Tests;

public sealed class DeviceDefaultsTests
{
    [Fact]
    public void TheProBaselineIsSafeAndKeepsRouting()
    {
        var info = new DeviceInfo("Elgato", "Wave XLR Pro", 0x0fd9, 0x00b4);
        var current = new DeviceState
        {
            GainDb = 72, Gain2Db = 0, Phantom = true, Phantom2 = true, ClipGuard = true, Compressor = true,
            LowCut = true, Expander = true, VoiceTune = true, VoiceTuneStrength = 90, Mute = true,
            HpVolumeDb = 0, Hp2VolumeDb = -60, LowImpedance = true, AuxLevelDb = 0, AuxLevelLock = true,
            OutHp1 = true, OutHp2 = false, OutUsbAux = true, OutLineOut = false,
            HpMixMonitorReturn = true, HpMixMicDirect = false, HpMixMicLevelDb = -6, AuxReturnEnabled = true,
            LineInGainDb = 12, LineInLevelLock = true,
        };
        DeviceState b = DeviceDefaults.Baseline(info, current)!;
        Assert.Equal((30, 30), (b.GainDb, b.Gain2Db));
        Assert.False(b.Phantom || b.Phantom2 || b.ClipGuard || b.Compressor || b.LowCut || b.Expander || b.VoiceTune || b.Mute || b.LowImpedance || b.AuxLevelLock);
        Assert.Equal((-30.0, -30.0, -30.0, 50), (b.HpVolumeDb, b.Hp2VolumeDb, b.AuxLevelDb, b.VoiceTuneStrength));
        // Routing, mix membership and the microphone's level in the jacks'
        // mix are the mixer's business and stay.
        Assert.Equal((true, false, true, false), (b.OutHp1, b.OutHp2, b.OutUsbAux, b.OutLineOut));
        Assert.Equal((true, false, -6.0, true), (b.HpMixMonitorReturn, b.HpMixMicDirect, b.HpMixMicLevelDb, b.AuxReturnEnabled));
        // The Line In's gain and lock are left as they are.
        Assert.Equal(((int?)12, (bool?)true), (b.LineInGainDb, b.LineInLevelLock));
    }

    [Fact]
    public void OnlyModelsWithABaselineGetOne()
    {
        Assert.Null(DeviceDefaults.Baseline(new DeviceInfo("Elgato", "Wave XLR", 0x0fd9, 0x007d), new DeviceState()));
        Assert.False(DeviceDefaults.HasBaseline(new DeviceInfo("Elgato", "XLR Dock MK.2", 0x0fd9, 0x00c7)));
        Assert.True(DeviceDefaults.HasBaseline(new DeviceInfo("Elgato", "Wave XLR Pro", 0x0fd9, 0x00b4)));
    }
}
