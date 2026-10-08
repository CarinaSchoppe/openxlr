using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;

namespace OpenXLR.UI;

/// <summary>
/// The insert windows the UI keeps open: one controls window per insert
/// and one chain window per mix, reused while open so a second click
/// raises the existing window instead of stacking another.
/// </summary>
public static class InsertWindows
{
    private static readonly Dictionary<InsertViewModel, InsertControlsWindow> Controls = new();
    private static readonly Dictionary<string, MixInsertsWindow> Chains = new();

    internal static bool OpensNativeEditor(InsertViewModel insert, UiSettings settings)
        => settings.OpenNativeEditorDirectly && insert.NativeEditorAvailable;

    public static System.Threading.Tasks.Task OpenSettingsAsync(Window owner, InsertViewModel insert)
    {
        try
        {
            if (OpensNativeEditor(insert, UiSettings.LoadRequired())) return insert.Owner.ShowNativeEditorAsync(insert);
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // An unreadable preference must not prevent access to controls.
        }
        OpenControls(owner, insert);
        return System.Threading.Tasks.Task.CompletedTask;
    }

    public static void OpenControls(Window owner, InsertViewModel insert)
    {
        if (Controls.TryGetValue(insert, out InsertControlsWindow? open)) { open.Activate(); return; }
        var w = new InsertControlsWindow { DataContext = insert };
        w.Closed += (_, _) => Controls.Remove(insert);
        Controls[insert] = w;
        w.Show(owner);
    }

    internal static void CloseChain(InsertsViewModel chain)
    {
        // A layout item can disappear while its chain or controls are open.
        // Clear its session and queued edits before its ID can be reused.
        chain.ResetForNewConnection();
        foreach (var window in Controls.Values.Where(w => w.DataContext is InsertViewModel insert
            && ReferenceEquals(insert.Owner, chain)).ToArray()) window.Close();
        foreach (var window in Chains.Values.Where(w => ReferenceEquals(w.DataContext, chain)).ToArray()) window.Close();
        chain.Apply(null);
    }

    internal static void CloseControls(InsertViewModel insert)
    {
        if (Controls.TryGetValue(insert, out var window)) window.Close();
    }

    public static void OpenChain(Window owner, InsertsViewModel chain, string key)
    {
        if (Chains.TryGetValue(key, out MixInsertsWindow? open)) { open.Activate(); return; }
        var w = new MixInsertsWindow { DataContext = chain };
        w.Closed += (_, _) => Chains.Remove(key);
        Chains[key] = w;
        w.Show(owner);
    }
}
