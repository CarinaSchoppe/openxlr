using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class ServiceViewModelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestartIsNonBlockingAndDuplicateClicksAreCoalesced(bool accepted)
    {
        var response = new TaskCompletionSource<bool>();
        int calls = 0;
        var vm = new ServiceViewModel(() => { calls++; return response.Task; });
        Task first = vm.RestartAsync();
        Assert.True(vm.Busy);
        Assert.False(first.IsCompleted);
        await vm.RestartAsync();
        Assert.Equal(1, calls);
        response.SetResult(accepted);
        await first;
        Assert.False(vm.Busy);
        Assert.Contains(accepted ? "Restart requested" : "Restart unavailable", vm.Status);
    }

    [Fact]
    public async Task UnexpectedFailureAllowsRetryAndIsLogged()
    {
        int calls = 0;
        var vm = new ServiceViewModel(() => ++calls == 1 ? throw new IOException("test restart failure") : Task.FromResult(true));
        await vm.RestartAsync();
        Assert.False(vm.Busy);
        Assert.Contains("test restart failure", vm.Status);
        Assert.Contains("test restart failure", SessionLog.Snapshot());
        await vm.RestartAsync();
        Assert.Contains("Restart requested", vm.Status);
    }
}
