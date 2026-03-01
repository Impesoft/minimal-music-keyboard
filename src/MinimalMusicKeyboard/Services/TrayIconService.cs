using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;

namespace MinimalMusicKeyboard.Services;

/// <summary>
/// Owns the system tray icon lifecycle: creation, context menu, event handling, and disposal.
///
/// Threading: All methods must be called on the UI thread (STA).
/// H.NotifyIcon.WinUI's TaskbarIcon is a DependencyObject.
///
/// Ghost icon note: if the process crashes without Dispose() being called, the icon
/// remains in the notification area until the user hovers over it (Windows shell limitation —
/// not fixable in code). See architecture Appendix C concern #1.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private TaskbarIcon? _taskbarIcon;
    private MenuFlyoutItem? _settingsItem;
    private MenuFlyoutItem? _exitItem;
    private bool _disposed;

    /// <summary>Raised when the user requests to open the Settings window.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Raised when the user chooses Exit from the tray menu.</summary>
    public event EventHandler? ExitRequested;

    // -------------------------------------------------------------------------
    // Initialization
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates and shows the tray icon. Call once from the UI thread after app startup.
    /// </summary>
    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = "Minimal Music Keyboard",
            ContextMenuMode = ContextMenuMode.PopupMenu,
            // H.NotifyIcon.WinUI 2.2+ uses ICommand for tray interactions.
            DoubleClickCommand = new RelayCommand(OnDoubleClick),
            // ms-appx:// requires package identity and doesn't work in unpackaged apps.
            // Use an absolute file URI resolved from the executable's directory instead.
            IconSource = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png"))),
        };

        _taskbarIcon.ContextFlyout = BuildContextMenu();

        // Fix: H.NotifyIcon.WinUI v2.x requires ForceCreate() when the icon is created
        // programmatically (not from XAML). Without this the icon is not registered with
        // the Windows shell and will not appear in the notification area.
        // Pass false to disable "Efficiency Mode" (hidden state) — we want the icon visible.
        _taskbarIcon.ForceCreate(false);

        Debug.WriteLine("[TrayIconService] Tray icon initialized.");
    }

    // -------------------------------------------------------------------------
    // Context menu
    // -------------------------------------------------------------------------

    private MenuFlyout BuildContextMenu()
    {
        var menu = new MenuFlyout();

        _settingsItem = new MenuFlyoutItem { Text = "Settings" };
        _settingsItem.Click += OnSettingsClicked;
        menu.Items.Add(_settingsItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        _exitItem = new MenuFlyoutItem { Text = "Exit" };
        _exitItem.Click += OnExitClicked;
        menu.Items.Add(_exitItem);

        return menu;
    }

    // -------------------------------------------------------------------------
    // Event handlers
    // -------------------------------------------------------------------------

    private void OnDoubleClick()
        => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
        => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnExitClicked(object sender, RoutedEventArgs e)
        => ExitRequested?.Invoke(this, EventArgs.Empty);

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>Updates the tooltip text (e.g. to show current instrument name).</summary>
    public void SetTooltip(string text)
    {
        if (_taskbarIcon is not null)
            _taskbarIcon.ToolTipText = text;
    }

    // -------------------------------------------------------------------------
    // IDisposable — prevents ghost tray icon on orderly shutdown
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe all handlers before destroying the icon to prevent
        // ghost callbacks if disposal races with a tray event.
        if (_settingsItem is not null) _settingsItem.Click -= OnSettingsClicked;
        if (_exitItem is not null)     _exitItem.Click    -= OnExitClicked;

        if (_taskbarIcon is not null)
        {
            _taskbarIcon.DoubleClickCommand = null;
            _taskbarIcon.Dispose(); // removes the icon from the notification area
            _taskbarIcon = null;
        }

        _settingsItem = null;
        _exitItem = null;

        // Null out event chains to release subscriber references.
        SettingsRequested = null;
        ExitRequested = null;

        GC.SuppressFinalize(this);
    }

    // -------------------------------------------------------------------------
    // Minimal ICommand implementation — avoids MVVM framework dependency
    // -------------------------------------------------------------------------

    private sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;

        public RelayCommand(Action execute) => _execute = execute;

        // CanExecuteChanged not used — always executable.
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
    }
}
