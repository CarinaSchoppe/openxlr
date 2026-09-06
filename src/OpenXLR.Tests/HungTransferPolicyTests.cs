using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class HungTransferPolicyTests
{
    [Fact]
    public void ThreeHangsSetADeviceAsideAndAReplugGivesItAFreshCount()
    {
        var policy = new HungTransferPolicy();
        Assert.False(policy.NoteHung(0x007d));
        Assert.False(policy.NoteHung(0x007d));
        Assert.False(policy.IsSetAside(0x007d));
        Assert.True(policy.NoteHung(0x007d));
        Assert.True(policy.IsSetAside(0x007d));
        Assert.Equal([0x007d], policy.SetAside);
        Assert.False(policy.IsSetAside(0x00b4));   // another model is not affected

        Assert.True(policy.Returned(0x007d));
        Assert.False(policy.IsSetAside(0x007d));
        Assert.Equal(0, policy.HungCount(0x007d));
        Assert.False(policy.NoteHung(0x007d));     // counting starts over
    }

    [Fact]
    public void AfterNineHangsInOneRunAReplugNoLongerHelps()
    {
        var policy = new HungTransferPolicy();
        for (int cycle = 0; cycle < 3; cycle++)
        {
            for (int i = 0; i < HungTransferPolicy.Limit; i++) policy.NoteHung(0x007d);
            Assert.True(policy.IsSetAside(0x007d));
            bool usable = policy.Returned(0x007d);
            Assert.Equal(cycle < 2, usable);       // the third replug finds the run's budget spent
        }
        Assert.True(policy.IsSpentForThisRun(0x007d));
        Assert.True(policy.IsSetAside(0x007d));
        Assert.False(policy.IsSpentForThisRun(0x00b4));
    }
}
