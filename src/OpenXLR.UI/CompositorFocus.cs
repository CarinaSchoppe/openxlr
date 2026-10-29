using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenXLR.UI;

internal enum FocusCompositor { KWin, Sway, Hyprland, Unavailable, Niri, X11, Gnome }

/// <summary>One read on demand, from the compositor that owns this desktop session.</summary>
internal static class CompositorFocus
{
    private static readonly string[] ChildLists = ["nodes", "floating_nodes"];
    internal static FocusCompositor Select(Func<string, string?>? environment = null)
    {
        var env = environment ?? Environment.GetEnvironmentVariable;
        string? current = env("XDG_CURRENT_DESKTOP");
        string desktop = string.IsNullOrWhiteSpace(current) ? env("XDG_SESSION_DESKTOP") ?? "" : current;
        string[] names = desktop.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Has(string name) => names.Contains(name, StringComparer.OrdinalIgnoreCase);
        bool kde = Has("KDE") || Has("Plasma") || Has("plasmawayland") || Has("plasmax11");
        bool sway = Has("sway"), hyprland = Has("Hyprland"), gnome = Has("GNOME") || Has("gnome-classic"), niri = Has("niri");
        bool x11 = env("XDG_SESSION_TYPE") == "x11" || (string.IsNullOrWhiteSpace(env("XDG_SESSION_TYPE"))
            && string.IsNullOrWhiteSpace(env("WAYLAND_DISPLAY")) && !string.IsNullOrWhiteSpace(env("DISPLAY")));
        if ((kde ? 1 : 0) + (sway ? 1 : 0) + (hyprland ? 1 : 0) + (gnome ? 1 : 0) + (niri ? 1 : 0) > 1)
            return FocusCompositor.Unavailable;
        if (kde) return FocusCompositor.KWin;
        if (sway) return FocusCompositor.Sway;
        if (hyprland) return FocusCompositor.Hyprland;
        if (gnome) return x11 ? FocusCompositor.X11 : FocusCompositor.Gnome;
        if (niri) return FocusCompositor.Niri;
        // Never let stale IPC variables select a different compositor on an
        // explicitly named desktop, such as GNOME.
        if (names.Length != 0) return x11 ? FocusCompositor.X11 : FocusCompositor.Unavailable;
        sway = !string.IsNullOrWhiteSpace(env("SWAYSOCK"));
        hyprland = !string.IsNullOrWhiteSpace(env("HYPRLAND_INSTANCE_SIGNATURE"));
        niri = !string.IsNullOrWhiteSpace(env("NIRI_SOCKET"));
        if ((sway ? 1 : 0) + (hyprland ? 1 : 0) + (niri ? 1 : 0) > 1) return FocusCompositor.Unavailable;
        if (sway) return FocusCompositor.Sway;
        if (hyprland) return FocusCompositor.Hyprland;
        if (niri) return FocusCompositor.Niri;
        if (x11) return FocusCompositor.X11;
        // Source launches on Plasma did not always carry a desktop marker.
        // KWin still has to own its bus name before its script can run.
        return FocusCompositor.KWin;
    }

    internal static async Task<uint> ReadAsync(FocusCompositor source, CancellationToken cancel,
        Func<string, string?>? environment = null)
    {
        var env = environment ?? Environment.GetEnvironmentVariable;
        if (source == FocusCompositor.X11) return await X11Focus.ReadAsync(cancel, env);
        string program;
        string[] arguments;
        int limit;
        System.Collections.Generic.Dictionary<string, string>? helperEnvironment = null;
        if (source == FocusCompositor.Sway)
        {
            string path = env("SWAYSOCK") ?? "";
            if (!Path.IsPathFullyQualified(path) || Encoding.UTF8.GetByteCount(path) > 107 || path.Any(char.IsControl))
                throw new InvalidOperationException("Sway focused routing needs this session's absolute SWAYSOCK path.");
            program = "swaymsg";
            arguments = ["-s", path, "-r", "-t", "get_tree"];
            limit = 2 * 1024 * 1024;
        }
        else if (source == FocusCompositor.Hyprland)
        {
            string instance = env("HYPRLAND_INSTANCE_SIGNATURE") ?? "";
            // hyprctl interprets a numeric -i value as an instance index.
            // A signature contains underscores and must never select by index.
            if (instance.Length is 0 or > 256 || !instance.Contains('_')
                || instance.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')))
                throw new InvalidOperationException("Hyprland focused routing needs this session's HYPRLAND_INSTANCE_SIGNATURE.");
            program = "hyprctl";
            arguments = ["-j", "-i", instance, "activewindow"];
            limit = 64 * 1024;
        }
        else if (source == FocusCompositor.Niri)
        {
            string path = env("NIRI_SOCKET") ?? "";
            if (!Path.IsPathFullyQualified(path) || Encoding.UTF8.GetByteCount(path) > 107 || path.Any(char.IsControl))
                throw new InvalidOperationException("Niri focused routing needs this session's absolute NIRI_SOCKET path.");
            helperEnvironment = new() { ["NIRI_SOCKET"] = path };
            program = "niri";
            arguments = ["msg", "--json", "focused-window"];
            limit = 64 * 1024;
        }
        else throw new InvalidOperationException("This desktop has no focused-process source.");

        ProcessResult result;
        try { result = await ProcessRunner.RunAsync(program, arguments, TimeSpan.FromSeconds(2), limit, 4096, cancel: cancel,
            environment: helperEnvironment); }
        catch (Win32Exception ex) { throw new InvalidOperationException($"Focused routing needs the {program} command installed.", ex); }
        cancel.ThrowIfCancellationRequested();
        if (!result.Ok) throw new InvalidOperationException($"{program} did not return a complete focus reply within its time and size limits.");
        return Parse(source, result.Stdout);
    }

    internal static uint Parse(FocusCompositor source, ReadOnlyMemory<byte> message)
    {
        try
        {
            using var document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 128, AllowDuplicateProperties = false });
            JsonElement root = document.RootElement;
            if (source == FocusCompositor.Niri && root.ValueKind == JsonValueKind.Null)
                throw new InvalidOperationException("Niri has no focused application window.");
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            if (source == FocusCompositor.Hyprland)
            {
                if (root.TryGetProperty("mapped", out var mapped) && mapped.ValueKind != JsonValueKind.True
                    || root.TryGetProperty("hidden", out var hidden) && hidden.ValueKind != JsonValueKind.False)
                    throw new InvalidOperationException("Hyprland has no focused application window.");
                return Process(root);
            }
            if (source == FocusCompositor.Niri)
            {
                if (!root.TryGetProperty("is_focused", out var focusedWindow) || focusedWindow.ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException("Niri has no focused application window.");
                return Process(root);
            }
            if (source != FocusCompositor.Sway) throw new InvalidOperationException("This compositor has no JSON focus source.");
            uint? focused = null;
            Visit(root);
            return focused ?? throw new InvalidOperationException("Sway did not report a focused application.");

            void Visit(JsonElement node)
            {
                if (node.ValueKind != JsonValueKind.Object) throw new JsonException();
                if (node.TryGetProperty("focused", out var selected))
                {
                    if (selected.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException();
                    if (selected.ValueKind == JsonValueKind.True && node.TryGetProperty("pid", out var pid) && pid.ValueKind != JsonValueKind.Null)
                    {
                        if (focused is not null) throw new InvalidOperationException("Sway reported more than one focused application.");
                        focused = Process(node);
                    }
                }
                foreach (string children in ChildLists)
                    if (node.TryGetProperty(children, out var list))
                    {
                        if (list.ValueKind != JsonValueKind.Array) throw new JsonException();
                        foreach (var child in list.EnumerateArray()) Visit(child);
                    }
            }
        }
        catch (JsonException ex) { throw new InvalidOperationException("The compositor returned an invalid focus reply.", ex); }
    }

    private static uint Process(JsonElement node)
    {
        if (node.TryGetProperty("pid", out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int pid) && pid > 0) return (uint)pid;
        throw new InvalidOperationException("The compositor did not report a usable application process id.");
    }
}
