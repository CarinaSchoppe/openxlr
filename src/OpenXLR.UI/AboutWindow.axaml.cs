using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{AppVersion.Current}";
        BuildText.Text = AppVersion.BuildDescription;
    }

    private static void OpenUrl(string url)
        => Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });

    private void OnRepo(object? sender, RoutedEventArgs e) => OpenUrl("https://github.com/emaspa/openxlr");
    private void OnCoffee(object? sender, RoutedEventArgs e) => OpenUrl("https://buymeacoffee.com/emaspa");
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

/// <summary>The app version, from the assembly (set once in Directory.Build.props).</summary>
public static class AppVersion
{
    private static readonly string Information =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0";
    public static readonly string Current = Information.Split('+')[0];
    public static readonly string? Revision = Regex.Match(Information, @"\+([0-9a-fA-F]{40})(?:\z|\.)") is { Success: true } match
        ? match.Groups[1].Value : null;
    public static readonly string Repository = Metadata("OpenXLR.UpdateRepository") ?? "emaspa/openxlr";
    public static readonly string BuildKind = Metadata("OpenXLR.BuildKind") ?? "development snapshot";
    public static string BuildDescription => $"{Repository} · {Current} · {Revision?[..7] ?? "unknown revision"} · {BuildKind}";
    private static string? Metadata(string key) => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == key)?.Value;
}
