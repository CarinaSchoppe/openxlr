using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class ServiceCommandTests
{
    [Fact]
    public async Task CapturePreservesBothOutputsAndNonzeroStatus()
    {
        ServiceCommand.Result result = await ServiceCommand.CaptureAsync("sh", ["-c", "printf output; printf error >&2; exit 4"], TimeSpan.FromSeconds(5));
        Assert.False(result.Success);
        Assert.Equal("output", result.Output);
        Assert.Equal("error", result.Error);
    }

    [Fact]
    public async Task DrainsBothPipesAndReportsExitStatus()
    {
        Assert.True(await ServiceCommand.RunAsync("sh", ["-c", "head -c 200000 /dev/zero; head -c 200000 /dev/zero >&2"], TimeSpan.FromSeconds(5)));
        Assert.False(await ServiceCommand.RunAsync("sh", ["-c", "exit 7"], TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TimeoutDoesNotBlockCallerAndKillsProcessTree()
    {
        Task<bool> running = ServiceCommand.RunAsync("sh", ["-c", "sleep 30"], TimeSpan.FromMilliseconds(200));
        Assert.False(running.IsCompleted);
        Assert.False(await running.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task MissingExecutableIsAReportedFailure()
        => Assert.False(await ServiceCommand.RunAsync("/nonexistent/openxlr-test-command", [], TimeSpan.FromSeconds(1)));
}
