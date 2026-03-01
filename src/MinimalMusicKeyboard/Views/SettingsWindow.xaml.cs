using Microsoft.UI.Xaml;

namespace MinimalMusicKeyboard.Views;

/// <summary>
/// On-demand settings window. Created fresh each time the user opens Settings;
/// not kept in memory when closed (architecture Section 3.6).
///
/// When adding service event subscriptions (e.g. MidiDeviceService.DeviceConnected),
/// unsubscribe them explicitly in the Closed handler to prevent event handler leaks on
/// this COM-backed WinUI3 window.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Title = "Minimal Music Keyboard";
    }
}
