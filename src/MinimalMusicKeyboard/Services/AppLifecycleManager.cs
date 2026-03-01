using MinimalMusicKeyboard.Helpers;
using MinimalMusicKeyboard.Interfaces;
using MinimalMusicKeyboard.Models;
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
    private AppSettings _settings = new();
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
        // Step 1: Load persisted settings
        _settings = AppSettings.Load();

        // Step 2: MIDI — use saved device if no override provided
        var midiDevice = preferredMidiDevice ?? _settings.MidiDeviceName;
        if (midiDevice is not null)
        {
            var opened = _midi.TryOpen(midiDevice);
            Debug.WriteLine(opened
                ? $"[AppLifecycleManager] MIDI device opened: {midiDevice}"
                : $"[AppLifecycleManager] MIDI device not found at startup, will retry: {midiDevice}");
        }

        // Step 3: Audio engine
        _audioEngine = new AudioEngine();

        // Step 4: Load SoundFont if configured
        if (!string.IsNullOrEmpty(_settings.SoundFontPath) &&
            System.IO.File.Exists(_settings.SoundFontPath))
        {
            _audioEngine.LoadSoundFont(_settings.SoundFontPath);
            Debug.WriteLine($"[AppLifecycleManager] SoundFont loaded: {_settings.SoundFontPath}");
        }

        // Step 5: Instrument switcher
        _switcher = new MidiInstrumentSwitcher(_catalog, _audioEngine, _midi);

        // Step 6: Tray icon
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
            _activeSettingsWindow.Activate();
            return;
        }

        _activeSettingsWindow = new SettingsWindow(_midi, _catalog, _settings, OnSettingsSaved);
        _activeSettingsWindow.Closed += (_, _) => _activeSettingsWindow = null;

        var appWindow = _activeSettingsWindow.AppWindow;
        const int W = 640, H = 520;
        var display  = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;
        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            workArea.X + (workArea.Width  - W) / 2,
            workArea.Y + (workArea.Height - H) / 2,
            W, H));

        _activeSettingsWindow.Activate();
    }

    private void OnSettingsSaved(AppSettings newSettings)
    {
        _settings = newSettings;
        _settings.Save();

        // Apply MIDI device change
        if (!string.IsNullOrEmpty(newSettings.MidiDeviceName))
            _midi.TryOpen(newSettings.MidiDeviceName);

        // Apply SoundFont change
        if (!string.IsNullOrEmpty(newSettings.SoundFontPath) &&
            System.IO.File.Exists(newSettings.SoundFontPath))
        {
            _catalog.UpdateAllSoundFontPaths(newSettings.SoundFontPath);
            _audioEngine?.LoadSoundFont(newSettings.SoundFontPath);
            Debug.WriteLine($"[AppLifecycleManager] SoundFont updated: {newSettings.SoundFontPath}");
        }
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
