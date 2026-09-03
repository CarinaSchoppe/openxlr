using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenXLR.UI;

public sealed record UpdateResult(bool Available, string Title, string Details, string? Url);

/// <summary>
/// Read-only GitHub release/revision checks. A different SHA alone is not an
/// update: the remote branch must be ahead of the installed revision. Fork
/// snapshots are identified separately from upstream releases. No credentials,
/// auto-installation, HTML rendering or execution of release text is involved.
/// </summary>
public sealed class UpdateChecker
{
    private readonly HttpClient _http;
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(8) };

    public UpdateChecker() : this(SharedHttp) { }
    internal UpdateChecker(HttpClient http) => _http = http;

    internal static bool IsRepository(string value)
        => Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*\z");

    public async Task<UpdateResult> CheckAsync(string repository, string version, string? revision, CancellationToken cancellation)
    {
        if (!IsRepository(repository)) throw new ArgumentException("Expected a GitHub owner/repository.", nameof(repository));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        cancellation = deadline.Token;
        using JsonDocument? release = await GetAsync(repository, "releases/latest", cancellation).ConfigureAwait(false);
        if (release is not null)
        {
            JsonElement root = release.RootElement;
            string tag = String(root, "tag_name");
            if (!Flag(root, "draft") && !Flag(root, "prerelease") && Newer(tag, version))
                return new(true, $"New release {tag}", Truncate(String(root, "body")),
                    $"https://github.com/{repository}/releases/tag/{Uri.EscapeDataString(tag)}");
        }

        if (revision is not null && Regex.IsMatch(revision, @"\A[0-9a-fA-F]{40}\z"))
        {
            using JsonDocument? comparison = await GetAsync(repository, $"compare/{revision}...main", cancellation).ConfigureAwait(false);
            if (comparison is not null)
            {
                JsonElement root = comparison.RootElement;
                if (String(root, "status") == "ahead" && root.TryGetProperty("ahead_by", out JsonElement ahead)
                    && ahead.ValueKind == JsonValueKind.Number && ahead.TryGetInt32(out int count) && count > 0)
                {
                    string[] messages = root.TryGetProperty("commits", out JsonElement commits) && commits.ValueKind == JsonValueKind.Array
                        ? commits.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object).TakeLast(10).Select(c => c.TryGetProperty("commit", out JsonElement commit)
                            ? String(commit, "message").Split('\n')[0] : "").Where(m => m.Length > 0).ToArray()
                        : [];
                    string details = "Development commits on main, not a new upstream release.\n\n" + string.Join("\n", messages.Select(m => "• " + m));
                    return new(true, $"{count} new commits in {repository}", Truncate(details),
                        $"https://github.com/{repository}/compare/{revision}...main");
                }
                return new(false, "No newer build found", $"Checked {repository}. Installed: {version}, revision {revision[..7]}.", null);
            }
        }
        return new(false, "No newer release found", $"Checked {repository}. A development revision comparison is unavailable for this build.", null);
    }

    internal static bool Newer(string tag, string installed)
    {
        static Version? Parse(string value) => Version.TryParse(value.TrimStart('v', 'V').Split('+', '-')[0], out Version? parsed)
            ? new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision)) : null;
        Version? remote = Parse(tag), local = Parse(installed);
        return !tag.Split('+')[0].Contains('-', StringComparison.Ordinal) && remote is not null && local is not null
            && (remote > local || remote == local && installed.Split('+')[0].Contains('-', StringComparison.Ordinal));
    }

    private async Task<JsonDocument?> GetAsync(string repository, string endpoint, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/{endpoint}");
        request.Headers.UserAgent.ParseAdd("OpenXLR-UpdateCheck/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        // Compare responses include diffs we do not use. Bound their size so
        // a large/changing repository cannot exhaust desktop memory.
        const int limit = 4 * 1024 * 1024;
        await using Stream input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var content = new MemoryStream();
        byte[] buffer = new byte[8192];
        int length;
        while ((length = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (content.Length + length > limit) throw new InvalidDataException("GitHub response exceeds the update-check size limit.");
            content.Write(buffer, 0, length);
        }
        JsonDocument document = JsonDocument.Parse(content.ToArray());
        if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
        document.Dispose();
        throw new InvalidDataException("GitHub returned an unexpected update response.");
    }

    private static string String(JsonElement value, string key)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out JsonElement item)
            && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    private static bool Flag(JsonElement value, string key)
        => value.TryGetProperty(key, out JsonElement item) && item.ValueKind == JsonValueKind.True;
    private static string Truncate(string text) => text.Length <= 12000 ? text : text[..12000] + "\n… See GitHub for the full changelog.";
}

/// <summary>Non-blocking, coalesced checks; opening Options never starts another automatic check.</summary>
public sealed class UpdatesViewModel : ViewModelBase
{
    private readonly Func<CancellationToken, Task<UpdateResult>> _check;
    public UpdatesViewModel() : this(token => new UpdateChecker().CheckAsync(
        AppVersion.Repository, AppVersion.Current, AppVersion.Revision, token))
    { }
    internal UpdatesViewModel(Func<CancellationToken, Task<UpdateResult>> check) => _check = check;

    public string Build => AppVersion.BuildDescription;
    private bool _busy;
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }
    private bool _available;
    public bool Available { get => _available; private set => Set(ref _available, value); }
    private string _title = "Updates have not been checked yet";
    public string Title { get => _title; private set => Set(ref _title, value); }
    private string _details = "Checks GitHub only. Nothing is installed automatically.";
    public string Details { get => _details; private set => Set(ref _details, value); }
    private string? _url;
    public string? Url { get => _url; private set => Set(ref _url, value); }

    public async Task CheckAsync(CancellationToken cancellation = default)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            UpdateResult result = await _check(cancellation);
            Available = result.Available;
            Title = result.Title;
            Details = result.Details;
            Url = result.Url;
            SessionLog.Write("updates", $"{Build}: {Title}");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Available = false;
            Url = null;
            Title = "Update check unavailable";
            Details = "Audio is unaffected. Retry later or open the repository manually.";
            SessionLog.Write("updates", ex.Message);
        }
        finally { Busy = false; }
    }
}
