using MinimalMusicKeyboard.Helpers;
using MinimalMusicKeyboard.Interfaces;
using MinimalMusicKeyboard.Models;
using MinimalMusicKeyboard.Views;using Microsoft.UI.Windowing;
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
    private SettingsWindow? _settingsWindow;   // created once, shown/hidden on demand
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
        _audioEngine = new AudioEngine(_settings.AudioOutputDeviceId);

        // Apply saved volume
        _audioEngine.SetVolume(_settings.Volume);

        // Step 5: Instrument switcher
        _switcher = new MidiInstrumentSwitcher(_catalog, _audioEngine, _midi);
        _switcher.UpdateButtonMappings(_settings.ButtonMappings);

        // Auto-reload the first VST3 instrument found in the saved slots so the bridge
        // is ready immediately (same UX as the previous session).
        var savedVst3 = _settings.ButtonMappings
            .Where(m => !string.IsNullOrEmpty(m.InstrumentId))
            .Select(m => _catalog.GetById(m.InstrumentId!))
            .FirstOrDefault(i => i?.Type == InstrumentType.Vst3
                              && !string.IsNullOrEmpty(i.Vst3PluginPath));
        if (savedVst3 is not null)
            _audioEngine.SelectInstrument(savedVst3);

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
        // Create once, reuse forever (show/hide). Recreating on every open would
        // close the previous window and risk destroying the DispatcherQueue.
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_midi, _catalog, _audioEngine!, _switcher!, _settings, OnSettingsSaved);

            // Set window icon (piano keys)
            try { _settingsWindow.AppWindow.SetIcon(@"Assets\AppIcon.ico"); }
            catch (Exception ex) { Debug.WriteLine($"[AppLifecycleManager] Icon load failed: {ex.Message}"); }

            // Position on first creation only.
            try
            {
                var appWindow = _settingsWindow.AppWindow;
                const int W = 640, H = 520;
                var display  = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
                var workArea = display.WorkArea;
                appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                    workArea.X + (workArea.Width  - W) / 2,
                    workArea.Y + (workArea.Height - H) / 2,
                    W, H));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppLifecycleManager] Window positioning failed: {ex.Message}");
            }
        }

        _settingsWindow.AppWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnSettingsSaved(AppSettings newSettings)
    {
        _settings = newSettings;
        _settings.Save();

        // Apply MIDI device change
        if (!string.IsNullOrEmpty(newSettings.MidiDeviceName))
            _midi.TryOpen(newSettings.MidiDeviceName);

        // Apply volume change immediately
        _audioEngine?.SetVolume(newSettings.Volume);

        // Apply audio output device change
        _audioEngine?.ChangeOutputDevice(newSettings.AudioOutputDeviceId);

        // Apply button mappings immediately
        _switcher?.UpdateButtonMappings(newSettings.ButtonMappings);
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

        // Force-close the settings window (bypasses the hide interceptor).
        if (_settingsWindow is not null)
        {
            try { _settingsWindow.ForceClose(); }
            catch (Exception ex) { Debug.WriteLine($"[AppLifecycleManager] Settings window close error: {ex.Message}"); }
            _settingsWindow = null;
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
