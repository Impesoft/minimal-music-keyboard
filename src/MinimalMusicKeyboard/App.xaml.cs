using MinimalMusicKeyboard.Services;
using Microsoft.UI.Xaml;

namespace MinimalMusicKeyboard;

/// <summary>
/// WinUI3 Application entry point. No window is created at startup —
/// the app lives entirely in the system tray until the user opens Settings.
/// </summary>
public partial class App : Application
{
    private AppLifecycleManager? _lifecycle;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _lifecycle = new AppLifecycleManager();
        _lifecycle.Start();
    }
}
