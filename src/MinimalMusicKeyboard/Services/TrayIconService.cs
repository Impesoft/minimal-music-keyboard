using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
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
            DoubleClickCommand = new RelayCommand(OnDoubleClick),
            // Placeholder icon — replaced with the real .ico below via UpdateIcon().
            IconSource = new GeneratedIconSource
            {
                Text       = "🎹",
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                FontSize   = 20,
            },
        };

        _taskbarIcon.ContextFlyout = BuildContextMenu();

        // ForceCreate() is required when the TaskbarIcon is created in code (not XAML).
        _taskbarIcon.ForceCreate(false);

        // Replace the placeholder emoji with the real piano-key .ico file.
        // TaskbarIcon.UpdateIcon(System.Drawing.Icon) updates the Shell NOTIFYICONDATA
        // directly — this is the correct way to load a file-based icon in H.NotifyIcon 2.x.
        TryApplyIcoIcon(_taskbarIcon);

        Debug.WriteLine("[TrayIconService] Tray icon initialized.");
    }

    // -------------------------------------------------------------------------
    // Icon loading
    // -------------------------------------------------------------------------

    private static void TryApplyIcoIcon(TaskbarIcon taskbarIcon)
    {
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (!File.Exists(icoPath))
            {
                Debug.WriteLine($"[TrayIconService] AppIcon.ico not found at: {icoPath} — keeping emoji.");
                return;
            }

            // System.Drawing.Icon loads the .ico file and picks the best-fit size.
            // TaskbarIcon.UpdateIcon() pushes the HICON directly to Shell_NotifyIcon.
            using var icon = new System.Drawing.Icon(icoPath, 16, 16);
            taskbarIcon.UpdateIcon(icon);
            Debug.WriteLine("[TrayIconService] Tray icon set from AppIcon.ico.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TrayIconService] Failed to load AppIcon.ico: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // Context menu
    // -------------------------------------------------------------------------

    private MenuFlyout BuildContextMenu()
    {
        var menu = new MenuFlyout();

        // IMPORTANT: ContextMenuMode.PopupMenu converts the XAML MenuFlyout into a Win32
        // popup menu and only invokes the Command property on each item — Click events are
        // never fired. Use RelayCommand (same pattern as DoubleClickCommand) for both items.
        _settingsItem = new MenuFlyoutItem { Text = "Settings" };
        _settingsItem.Command = new RelayCommand(OnSettingsActivated);
        menu.Items.Add(_settingsItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        _exitItem = new MenuFlyoutItem { Text = "Exit" };
        _exitItem.Command = new RelayCommand(OnExitActivated);
        menu.Items.Add(_exitItem);

        return menu;
    }

    // -------------------------------------------------------------------------
    // Event handlers
    // -------------------------------------------------------------------------

    private void OnDoubleClick()
        => SettingsRequested?.Invoke(this, EventArgs.Empty);

    // H.NotifyIcon dispatches Command.Execute() to the UI thread before invoking —
    // no manual DispatcherQueue marshalling needed (same as DoubleClickCommand).
    private void OnSettingsActivated()
        => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnExitActivated()
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

        // Null out commands to release subscriber references.
        if (_settingsItem is not null) _settingsItem.Command = null;
        if (_exitItem is not null)     _exitItem.Command    = null;

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
