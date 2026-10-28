using System.Text;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class NativeEditorPolicyReadTests : IDisposable
{
    private const int MaximumBytes = 1024 * 1024;
    private const string Plugin = "ABCDEF019182FAEB4D616E75466C7665";
    private const string Document = """{"version":1,"overrides":[{"kind":"vst3","plugin":"ABCDEF019182FAEB4D616E75466C7665","blocked":false}]}""";
    private readonly string _root = Directory.CreateTempSubdirectory("native-editor-read-").FullName;
    private string PathName => Path.Combine(_root, "native-editors.json");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APolicyPipeKeepsReleaseDefaultsWithoutBlockingOrWriting(bool linked)
    {
        if (!OperatingSystem.IsLinux()) return;
        string pipe = linked ? Path.Combine(_root, "pipe") : PathName;
        Assert.True(ProcessRunner.Run("mkfifo", [pipe]).Ok);
        if (linked) File.CreateSymbolicLink(PathName, pipe);

        Task<Exception?> reading = Task.Run<Exception?>(() => Record.Exception(() =>
        {
            var policy = new NativeEditorPolicy(PathName);
            Assert.NotNull(policy.Error);
            Assert.True(policy.IsBlocked("vst3", Plugin));
            Assert.Equal(policy.Error, policy.Set("vst3", Plugin, null, false));
        }));
        try { Assert.Null(await reading.WaitAsync(TimeSpan.FromSeconds(2))); }
        finally
        {
            if (!reading.IsCompleted)
            {
                using var writer = new FileStream(pipe, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                writer.Write("{}"u8);
            }
            await reading.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(File.Exists(pipe));

        File.Delete(PathName);
        File.WriteAllText(PathName, Document);
        var repaired = new NativeEditorPolicy(PathName);
        Assert.Null(repaired.Error);
        Assert.False(repaired.IsBlocked("vst3", Plugin));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PolicyByteLimitsApplyToLinkedFilesAndLegacyEncodings(bool linked, bool unicode)
    {
        string target = linked ? Path.Combine(_root, "target.json") : PathName;
        Encoding encoding = unicode ? Encoding.Unicode : Encoding.UTF8;
        int padding = (MaximumBytes - encoding.GetPreamble().Length - encoding.GetByteCount(Document))
            / encoding.GetByteCount(" ");
        File.WriteAllText(target, Document + new string(' ', padding), encoding);
        Assert.Equal(MaximumBytes, new FileInfo(target).Length);
        if (linked) File.CreateSymbolicLink(PathName, target);

        var policy = new NativeEditorPolicy(PathName);
        Assert.Null(policy.Error);
        Assert.False(policy.IsBlocked("vst3", Plugin));

        using (var file = new FileStream(target, FileMode.Append)) file.Write(encoding.GetBytes(" "));
        string previous = File.ReadAllText(target);
        policy = new NativeEditorPolicy(PathName);
        Assert.NotNull(policy.Error);
        Assert.True(policy.IsBlocked("vst3", Plugin));
        Assert.Equal(policy.Error, policy.Set("vst3", Plugin, null, false));
        Assert.Equal(previous, File.ReadAllText(target));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
