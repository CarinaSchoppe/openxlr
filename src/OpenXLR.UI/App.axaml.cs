using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace OpenXLR.UI;

public partial class App : Application
{
    // The embedded font must also be the default: a minimal Linux install
    // can have fontconfig but no system fonts for Avalonia to choose from.
    public const string DefaultFontFamily = "fonts:Inter#Inter";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // An earlier version wrote a user unit with a build-tree path on
            // packaged installs; fix it before the user has to notice.
            if (UiSettings.Load().StartDaemonAtLogin)
                _ = StartupIntegration.RepairDaemonUnitAsync();

            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
