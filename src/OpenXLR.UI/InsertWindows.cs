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

    /// <summary>Close editors whose live owner/insert disappeared after a layout or chain edit.</summary>
    public static void RetainChains(IEnumerable<InsertsViewModel> active)
    {
        var chains = active.ToHashSet();
        foreach (MixInsertsWindow window in Chains.Values.ToList())
            if (window.DataContext is InsertsViewModel chain && !chains.Contains(chain)) window.Close();
        foreach (InsertControlsWindow window in Controls.Values.ToList())
            if (window.DataContext is InsertViewModel insert &&
                (!chains.Contains(insert.Owner) || !insert.Owner.Items.Contains(insert))) window.Close();
    }

    public static void OpenControls(Window owner, InsertViewModel insert)
    {
        if (Controls.TryGetValue(insert, out InsertControlsWindow? open)) { open.Activate(); return; }
        var w = new InsertControlsWindow { DataContext = insert };
        w.Closed += (_, _) => Controls.Remove(insert);
        Controls[insert] = w;
        w.Show(owner);
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
