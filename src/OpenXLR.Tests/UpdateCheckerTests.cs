using System.Net;
using System.Text.Json;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>Offline HTTP contracts: never contact GitHub or modify installed software.</summary>
public sealed class UpdateCheckerTests
{
    private const string Revision = "0123456789012345678901234567890123456789";

    [Theory]
    [InlineData("v1.10.0", "1.9.9", true)]
    [InlineData("1.2.0", "1.2", false)]
    [InlineData("1.2.0", "1.2.0+abc", false)]
    [InlineData("1.2.0", "1.2.0-rc1", true)]
    [InlineData("1.2.0-rc1", "1.1.0", false)]
    [InlineData("v0.1.12", "0.1.13", false)]
    [InlineData("latest", "0.1.13", false)]
    public void VersionsCompareNumerically(string remote, string local, bool expected)
        => Assert.Equal(expected, UpdateChecker.Newer(remote, local));

    [Fact]
    public async Task ReleaseUsesSafeConstructedUrlAndBoundedPlainChangelog()
    {
        using var handler = new Responses(JsonSerializer.Serialize(new
        { tag_name = "v0.2.0", body = new string('a', 15000), html_url = "https://evil.example/run" }));
        using var http = new HttpClient(handler);
        UpdateResult result = await new UpdateChecker(http).CheckAsync("CarinaSchoppe/openxlr", "0.1.13", Revision, default);
        Assert.True(result.Available);
        Assert.Equal("https://github.com/CarinaSchoppe/openxlr/releases/tag/v0.2.0", result.Url);
        Assert.InRange(result.Details.Length, 12000, 12100);
        Assert.Single(handler.Urls);
    }

    [Theory]
    [InlineData("ahead", 2, true)]
    [InlineData("ahead", 0, false)]
    [InlineData("behind", 0, false)]
    [InlineData("identical", 0, false)]
    [InlineData("diverged", 2, false)]
    public async Task SnapshotMustBeAnAncestorOfMain(string status, int ahead, bool expected)
    {
        using var handler = new Responses(null, JsonSerializer.Serialize(new
        { status, ahead_by = ahead, commits = new[] { new { commit = new { message = "Fix routing\n\nDetails" } } } }));
        using var http = new HttpClient(handler);
        UpdateResult result = await new UpdateChecker(http).CheckAsync("CarinaSchoppe/openxlr", "0.1.13", Revision, default);
        Assert.Equal(expected, result.Available);
        Assert.EndsWith($"compare/{Revision}...main", handler.Urls[1]);
        if (expected)
        {
            Assert.Contains("not a new upstream release", result.Details);
            Assert.Contains("Fix routing", result.Details);
            Assert.DoesNotContain("Details", result.Details);
        }
    }

    [Theory]
    [InlineData("{\"tag_name\":\"v99.0.0\",\"prerelease\":true}")]
    [InlineData("{\"tag_name\":\"v99.0.0\",\"draft\":true}")]
    [InlineData("{\"tag_name\":null}")]
    public async Task DraftPrereleaseAndMissingVersionDoNotAdvertiseRelease(string response)
    {
        using var handler = new Responses(response);
        using var http = new HttpClient(handler);
        UpdateResult result = await new UpdateChecker(http).CheckAsync("emaspa/openxlr", "0.1.13", null, default);
        Assert.False(result.Available);
        Assert.Null(result.Url);
    }

    [Fact]
    public async Task MissingCommitDoesNotInventAnUpdate()
    {
        using var handler = new Responses(null, null);
        using var http = new HttpClient(handler);
        UpdateResult result = await new UpdateChecker(http).CheckAsync("emaspa/openxlr", "0.1.13", Revision, default);
        Assert.False(result.Available);
        Assert.Contains("unavailable", result.Details);
    }

    [Theory]
    [InlineData("owner/repo/other")]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("../repo")]
    [InlineData("owner/repo?token=secret")]
    public async Task InvalidRepositoryNeverSendsARequest(string repository)
    {
        using var handler = new Responses();
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new UpdateChecker(http).CheckAsync(repository, "1.0", null, default));
        Assert.Empty(handler.Urls);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task UnexpectedRootIsAnExplicitFailure(string response)
    {
        using var handler = new Responses(response);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateChecker(http).CheckAsync("owner/repo", "1.0", null, default));
    }

    [Fact]
    public async Task OversizedResponseIsRejected()
    {
        using var handler = new Responses(new string('x', 4 * 1024 * 1024 + 1));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateChecker(http).CheckAsync("owner/repo", "1.0", null, default));
    }

    [Fact]
    public async Task CheckIsNonBlockingCoalescedAndClearsStaleSuccessOnFailure()
    {
        var pending = new TaskCompletionSource<UpdateResult>();
        int calls = 0;
        var vm = new UpdatesViewModel(_ => { calls++; return pending.Task; });
        Task first = vm.CheckAsync();
        Assert.True(vm.Busy);
        Assert.False(first.IsCompleted);
        await vm.CheckAsync();
        Assert.Equal(1, calls);
        pending.SetResult(new(true, "New release", "Changes", "https://github.com/owner/repo"));
        await first;
        Assert.True(vm.Available);
        Assert.False(vm.Busy);
        pending = new();
        Task second = vm.CheckAsync();
        pending.SetException(new HttpRequestException("HTTP 429"));
        await second;
        Assert.False(vm.Busy);
        Assert.False(vm.Available);
        Assert.Null(vm.Url);
        Assert.Equal("Update check unavailable", vm.Title);
        Assert.Contains("Audio is unaffected", vm.Details);
    }

    [Fact]
    public async Task ClosingUiCancelsWithoutShowingAnError()
    {
        using var stop = new CancellationTokenSource();
        var pending = new TaskCompletionSource<UpdateResult>();
        var vm = new UpdatesViewModel(token => pending.Task.WaitAsync(token));
        Task check = vm.CheckAsync(stop.Token);
        stop.Cancel();
        await check;
        Assert.False(vm.Busy);
        Assert.False(vm.Available);
        Assert.DoesNotContain("unavailable", vm.Title);
    }

    private sealed class Responses(params string?[] responses) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            Assert.NotEmpty(request.Headers.UserAgent);
            string? body = responses[Urls.Count];
            Urls.Add(request.RequestUri.ToString());
            return Task.FromResult(new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            { Content = new StringContent(body ?? "") });
        }
    }
}
