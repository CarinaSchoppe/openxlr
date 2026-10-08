using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace OpenXLR.UI;

/// <summary>Portable parameter presets. Importing never installs or executes a plugin.</summary>
internal static class EffectPresetFiles
{
    internal const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly FilePickerFileType Type = new("OpenXLR effect presets") { Patterns = ["*.openxlr-effects.json", "*.json"] };

    internal static byte[] Encode(EffectChainPreset preset)
    {
        if (!EffectChainPresets.ValidName(preset.Name)) throw new InvalidDataException("Invalid effect preset name.");
        preset.Chain.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(preset, Json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Effect presets are limited to 8 MiB.");
        return bytes;
    }

    internal static async Task<EffectChainPreset> DecodeAsync(Stream input, CancellationToken cancellationToken = default)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[32 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + count > MaximumBytes) throw new InvalidDataException("Effect presets are limited to 8 MiB.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        var preset = JsonSerializer.Deserialize<EffectChainPreset>(output.GetBuffer().AsSpan(0, (int)output.Length), Json)
            ?? throw new InvalidDataException("The effect preset is empty.");
        if (!EffectChainPresets.ValidName(preset.Name) || preset.Chain is null)
            throw new InvalidDataException("Invalid effect preset name or chain.");
        preset.Chain.Validate();
        return preset with { Chain = preset.Chain.Copy() };
    }

    internal static async Task<EffectChainPreset?> ImportAsync(Window owner, CancellationToken cancellationToken = default)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import effect preset", AllowMultiple = false, FileTypeFilter = [Type],
        });
        if (files.Count == 0) return null;
        cancellationToken.ThrowIfCancellationRequested();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        await using var stream = await files[0].OpenReadAsync();
        return await DecodeAsync(stream, limit.Token);
    }

    internal static async Task ExportAsync(Window owner, EffectChainPreset preset, CancellationToken cancellationToken = default)
    {
        byte[] bytes = Encode(preset);
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export effect preset", SuggestedFileName = "OpenXLR.openxlr-effects.json", FileTypeChoices = [Type], ShowOverwritePrompt = true,
        });
        if (file is null) return;
        cancellationToken.ThrowIfCancellationRequested();
        // Local exports use the same atomic writer as configuration. A failed
        // replacement must not leave half of an existing preset behind.
        if (file.TryGetLocalPath() is { } path)
        {
            OpenXlrPaths.WriteAtomic(path, System.Text.Encoding.UTF8.GetString(bytes));
            return;
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek) stream.SetLength(0);
        await stream.WriteAsync(bytes, limit.Token); await stream.FlushAsync(limit.Token);
    }
}
