using System.Collections.Generic;
using Avalonia.Controls;

namespace OpenXLR.UI;

/// <summary>
/// The insert windows the UI keeps open: one controls window per insert
/// and one chain window per mix, reused while open so a second click
/// raises the existing window instead of stacking another.
/// </summary>
public static class InsertWindows
{
    // Keyed by the insert itself: a slot id can come back holding another
    // plugin, and two chains can hold the same id.
    private static readonly Dictionary<InsertViewModel, InsertControlsWindow> Controls = new();
    private static readonly Dictionary<string, MixInsertsWindow> Chains = new();

    public static void OpenControls(Window owner, InsertViewModel insert)
    {
        if (Controls.TryGetValue(insert, out InsertControlsWindow? open) && Reuse(open, owner)) return;
        var w = new InsertControlsWindow { DataContext = insert };
        w.Closed += (_, _) => Controls.Remove(insert);
        Controls[insert] = w;
        w.Show(owner);
    }

    /// <summary>Close the controls of an insert that left its chain.</summary>
    internal static void CloseControls(InsertViewModel insert)
    {
        if (Controls.TryGetValue(insert, out InsertControlsWindow? window)) window.Close();
    }

    public static void OpenChain(Window owner, InsertsViewModel chain, string key)
    {
        if (Chains.TryGetValue(key, out MixInsertsWindow? open) && Reuse(open, owner)) return;
        var w = new MixInsertsWindow { DataContext = chain };
        w.Closed += (_, _) => Chains.Remove(key);
        Chains[key] = w;
        w.Show(owner);
    }

    private static bool Reuse(Window window, Window owner)
    {
        // A window's owner cannot move with the mixer panel. Reopen its
        // controls on the same live model under the window that asked for them.
        if (window.Owner != owner) { window.Close(); return false; }
        if (!window.IsVisible) window.Show(owner);
        window.Activate();
        return true;
    }
}
