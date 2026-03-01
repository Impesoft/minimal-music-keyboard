using MinimalMusicKeyboard.Helpers;
using MinimalMusicKeyboard.Interfaces;
using MinimalMusicKeyboard.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Diagnostics;

namespace MinimalMusicKeyboard.Services;

/// <summary>
/// Orchestrates the full app lifecycle: startup sequencing, service wiring,
/// on-demand settings window, and ordered shutdown.
///
/// Startup order  (architecture Section 3.1): catalog → audio → midi → switcher → tray
/// Shutdown order (architecture Section 6):   switcher → midi → audio → tray
///
/// Each shutdown step is isolated in try/catch — a failure in one step
/// must not prevent subsequent steps from running.
///
/// Note: SingleInstanceGuard is NOT owned here; it lives in Program.Main's
/// using block so the mutex is released when Application.Start() returns.
/// </summary>
public sealed class AppLifecycleManager : IDisposable
{
    private readonly InstrumentCatalog _catalog;
    private readonly MidiDeviceService _midi;
    private readonly TrayIconService _tray;
    private IAudioEngine? _audioEngine;
    private MidiInstrumentSwitcher? _switcher;
    private SettingsWindow? _activeSettingsWindow;
    private bool _disposed;

    public AppLifecycleManager()
    {
        _catalog = new InstrumentCatalog();
        _midi    = new MidiDeviceService();
        _tray    = new TrayIconService();
    }

    // -------------------------------------------------------------------------
    // Startup
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs the startup sequence. Call once from App.OnLaunched (UI thread).
    /// </summary>
    /// <param name="preferredMidiDevice">
    /// Optional MIDI device name from saved settings.
    /// If null or device not found, the app starts in Disconnected state.
    /// </param>
    public void Start(string? preferredMidiDevice = null)
    {
        // Step 1: MIDI
        if (preferredMidiDevice is not null)
        {
            var opened = _midi.TryOpen(preferredMidiDevice);
            Debug.WriteLine(opened
                ? $"[AppLifecycleManager] MIDI device opened: {preferredMidiDevice}"
                : $"[AppLifecycleManager] MIDI device not found at startup, will retry: {preferredMidiDevice}");
        }

        // Step 2: Audio engine
        _audioEngine = new AudioEngine();

        // Step 3: Instrument switcher — wires MIDI PC/CC events → InstrumentCatalog → AudioEngine
        _switcher = new MidiInstrumentSwitcher(_catalog, _audioEngine, _midi);

        // Step 4: Tray icon
        _tray.Initialize();
        _tray.SettingsRequested += OnSettingsRequested;
        _tray.ExitRequested += OnExitRequested;
    }

    // -------------------------------------------------------------------------
    // Settings window (on-demand pattern, architecture Section 3.6)
    // -------------------------------------------------------------------------

    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        if (_activeSettingsWindow is not null)
        {
            // Window already open — bring to front.
            _activeSettingsWindow.Activate();
            return;
        }

        _activeSettingsWindow = new SettingsWindow();

        // Release the reference when the window is closed so GC can reclaim it.
        _activeSettingsWindow.Closed += (_, _) => _activeSettingsWindow = null;

        // Center the window on the primary display. AppWindow gives us reliable
        // positioning in unpackaged apps where Activate() alone may not grant foreground.
        var appWindow = _activeSettingsWindow.AppWindow;
        const int W = 640, H = 500;
        var display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;
        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            workArea.X + (workArea.Width  - W) / 2,
            workArea.Y + (workArea.Height - H) / 2,
            W, H));

        _activeSettingsWindow.Activate();
    }

    // -------------------------------------------------------------------------
    // Exit
    // -------------------------------------------------------------------------

    private void OnExitRequested(object? sender, EventArgs e)
    {
        Dispose();
        Application.Current.Exit(); // terminates the WinUI3 message loop
    }

    // -------------------------------------------------------------------------
    // IDisposable — ordered shutdown (architecture Section 6)
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe tray events first — prevents re-entrant exit/settings calls
        // if tray events fire during shutdown.
        _tray.SettingsRequested -= OnSettingsRequested;
        _tray.ExitRequested -= OnExitRequested;

        // Force-close any open settings window before services shut down.
        // The window's Closed handler nulls _activeSettingsWindow.
        if (_activeSettingsWindow is not null)
        {
            try { _activeSettingsWindow.Close(); }
            catch (Exception ex) { Debug.WriteLine($"[AppLifecycleManager] Settings window close error: {ex.Message}"); }
            _activeSettingsWindow = null;
        }

        // Shutdown order per architecture Section 6:
        // 1. MidiInstrumentSwitcher — unsubscribes from MidiDeviceService first (no more routing)
        // 2. MidiDeviceService      — stops MIDI so no new events reach the audio engine
        // 3. AudioEngine            — silence voices, release WASAPI
        // 4. TrayIconService        — icon removed last (user sees app is gone)

        _switcher.SafeDispose("MidiInstrumentSwitcher");
        ((IDisposable)_midi).SafeDispose("MidiDeviceService");
        _audioEngine.SafeDispose("AudioEngine");
        ((IDisposable)_tray).SafeDispose("TrayIconService");

        GC.SuppressFinalize(this);
    }
}
