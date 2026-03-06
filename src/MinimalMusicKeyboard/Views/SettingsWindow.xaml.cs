using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MinimalMusicKeyboard.Interfaces;
using MinimalMusicKeyboard.Models;
using MinimalMusicKeyboard.Services;
using NAudio.Midi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MinimalMusicKeyboard.Views;

/// <summary>
/// On-demand settings window. Receives services via constructor; does not own them.
/// Created fresh on each open; nulled and GC'd on close (architecture Section 3.6).
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly MidiDeviceService      _midi;
    private readonly InstrumentCatalog      _catalog;
    private readonly IAudioEngine           _audioEngine;
    private readonly MidiInstrumentSwitcher _switcher;
    private readonly AppSettings            _settings;
    private readonly Action<AppSettings>    _onSave;

    // Per-row state for the instrument slots
    private sealed record MappingRowState(int SlotIndex, Border SlotBadge, TextBlock SlotLabel, ComboBox InstrumentCombo, TextBlock Sf2Label, TextBlock TriggerLabel, Button MapButton);
    private readonly List<MappingRowState> _mappingRows = new();

    // Listening-mode state (-1 = not listening)
    private int _listeningSlotIndex = -1;
    private DispatcherTimer? _listenTimer;

    private bool _forceClose;

    public SettingsWindow(
        MidiDeviceService       midi,
        InstrumentCatalog       catalog,
        IAudioEngine            audioEngine,
        MidiInstrumentSwitcher  switcher,
        AppSettings             currentSettings,
        Action<AppSettings>     onSave)
    {
        InitializeComponent();
        Title = "Minimal Music Keyboard — Settings";

        _midi        = midi;
        _catalog     = catalog;
        _audioEngine = audioEngine;
        _switcher    = switcher;
        _settings = new AppSettings
        {
            MidiDeviceName     = currentSettings.MidiDeviceName,
            AudioOutputDeviceId = currentSettings.AudioOutputDeviceId,
            Volume             = currentSettings.Volume,
            ButtonMappings     = currentSettings.ButtonMappings,
        };
        _onSave = onSave;

        AppWindow.Closing += OnWindowClosing;

        // Subscribe to active-instrument changes so the UI stays current whether
        // the window is visible or hidden.
        _switcher.ActiveInstrumentChanged += OnActiveInstrumentChanged;

        PopulateMidiDevices();
        PopulateAudioOutputDevices();
        PopulateButtonMappings();
        RestoreCurrentValues();
    }

    /// <summary>
    /// Called during app shutdown to actually close the window (bypasses hide intercept).
    /// </summary>
    public void ForceClose()
    {
        StopListening();
        _switcher.ActiveInstrumentChanged -= OnActiveInstrumentChanged;
        _forceClose = true;
        Close();
    }

    private void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
                                  Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_forceClose) return; // let the app-shutdown close go through
        args.Cancel = true;      // intercept normal user close
        AppWindow.Hide();        // hide to tray instead
    }

    // -------------------------------------------------------------------------
    // Initialization helpers
    // -------------------------------------------------------------------------

    private void PopulateMidiDevices()
    {
        MidiDeviceCombo.Items.Clear();
        var devices = _midi.EnumerateDevices();
        foreach (var d in devices)
            MidiDeviceCombo.Items.Add(d.Name);

        MidiStatusText.Text = devices.Count == 0
            ? "No MIDI devices detected. Connect your keyboard and click ↻."
            : $"{devices.Count} device(s) found.";
    }

    private void PopulateAudioOutputDevices()
    {
        AudioDeviceCombo.Items.Clear();
        // First item = system default (no stored device ID)
        AudioDeviceCombo.Items.Add("(System default)");

        var devices = _audioEngine.EnumerateOutputDevices();
        foreach (var d in devices)
            AudioDeviceCombo.Items.Add(d);

        AudioDeviceStatusText.Text = devices.Count == 0
            ? "No audio output devices detected."
            : $"{devices.Count} device(s) found.";
    }

    private void PopulateButtonMappings()
    {
        ButtonMappingsPanel.Children.Clear();
        _mappingRows.Clear();

        var catalogItems = _catalog.GetAll();

        for (int i = 0; i < _settings.ButtonMappings.Length; i++)
        {
            var mapping = _settings.ButtonMappings[i];
            var slotIdx = i;

            // ── Columns: badge | instrument combo | SF2 label | SF2… | trigger | Map | ✕
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });           // badge
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // combo
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });          // sf2 name
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });           // SF2…
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });           // trigger
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });           // Map
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });           // ✕

            // Slot badge
            var slotLabel = new TextBlock
            {
                Text = $"{i + 1}",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };
            var slotBadge = new Border
            {
                Child = slotLabel,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(3, 1, 3, 1),
                VerticalAlignment = VerticalAlignment.Center,
            };

            // Instrument combo
            var combo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(4, 0, 4, 0),
                FontSize = 12,
            };
            combo.Items.Add("(none)");
            foreach (var inst in catalogItems)
                combo.Items.Add(inst.DisplayName);

            int initialIdx = 0;
            if (mapping.InstrumentId != null)
            {
                for (int j = 0; j < catalogItems.Count; j++)
                {
                    if (catalogItems[j].Id == mapping.InstrumentId)
                    {
                        initialIdx = j + 1;
                        break;
                    }
                }
            }
            combo.SelectedIndex = initialIdx;

            // SF2 label — shows the soundfont of the currently selected instrument
            string GetSf2Text(int comboIdx)
            {
                if (comboIdx <= 0) return "—";
                var inst = _catalog.GetById(catalogItems[comboIdx - 1].Id);
                if (inst is null) return "—";
                return inst.SoundFontPath == "[SoundFont Not Configured]" || string.IsNullOrEmpty(inst.SoundFontPath)
                    ? "(no SF2)"
                    : Path.GetFileNameWithoutExtension(inst.SoundFontPath);
            }

            var sf2Label = new TextBlock
            {
                Text = GetSf2Text(initialIdx),
                FontSize = 11,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 2, 0),
            };
            if (initialIdx > 0)
                ToolTipService.SetToolTip(sf2Label, catalogItems[initialIdx - 1].SoundFontPath);

            combo.SelectionChanged += (_, _) =>
            {
                var idx = combo.SelectedIndex;
                _settings.ButtonMappings[slotIdx].InstrumentId = idx <= 0 ? null : catalogItems[idx - 1].Id;
                sf2Label.Text = GetSf2Text(idx);
                if (idx > 0)
                    ToolTipService.SetToolTip(sf2Label, _catalog.GetById(catalogItems[idx - 1].Id)?.SoundFontPath ?? "");
                else
                    ToolTipService.SetToolTip(sf2Label, "");
            };

            // SF2… browse button
            var sf2Btn = new Button
            {
                Content = "SF2…",
                FontSize = 11,
                Padding = new Thickness(4, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 4, 0),
            };
            sf2Btn.Click += async (_, _) =>
            {
                var idx = combo.SelectedIndex;
                if (idx <= 0) return; // no instrument selected — nothing to configure
                var instrumentId = catalogItems[idx - 1].Id;

                var path = await PickSf2FileAsync();
                if (path is null) return;

                _catalog.UpdateInstrumentSoundFont(instrumentId, path);
                sf2Label.Text = Path.GetFileNameWithoutExtension(path);
                ToolTipService.SetToolTip(sf2Label, path);
            };

            // Trigger label
            var triggerLabel = new TextBlock
            {
                Text = mapping.TriggerLabel,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                FontSize = 11, Opacity = 0.6,
                Margin = new Thickness(2, 0, 2, 0),
            };

            // Map button
            var mapBtn = new Button
            {
                Content = "Map",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontSize = 11,
                Margin = new Thickness(2, 0, 2, 0),
            };
            mapBtn.Click += (_, _) => StartListening(slotIdx);

            // Clear button
            var clearBtn = new Button { Content = "✕", Width = 24, FontSize = 11 };
            clearBtn.Click += (_, _) => ClearMapping(slotIdx);

            Grid.SetColumn(slotBadge,    0);
            Grid.SetColumn(combo,        1);
            Grid.SetColumn(sf2Label,     2);
            Grid.SetColumn(sf2Btn,       3);
            Grid.SetColumn(triggerLabel, 4);
            Grid.SetColumn(mapBtn,       5);
            Grid.SetColumn(clearBtn,     6);

            row.Children.Add(slotBadge);
            row.Children.Add(combo);
            row.Children.Add(sf2Label);
            row.Children.Add(sf2Btn);
            row.Children.Add(triggerLabel);
            row.Children.Add(mapBtn);
            row.Children.Add(clearBtn);

            ButtonMappingsPanel.Children.Add(row);
            _mappingRows.Add(new MappingRowState(i, slotBadge, slotLabel, combo, sf2Label, triggerLabel, mapBtn));
        }
    }

    // ── Listening mode ────────────────────────────────────────────────────────

    private void StartListening(int slotIndex)
    {
        if (_listeningSlotIndex >= 0)
            StopListening(); // cancel any in-progress capture first

        _listeningSlotIndex = slotIndex;

        foreach (var r in _mappingRows)
        {
            r.MapButton.Content   = r.SlotIndex == slotIndex ? "⏺ Listening…" : "Map";
            r.MapButton.IsEnabled = r.SlotIndex != slotIndex; // only the active one is "busy"
        }

        _midi.MidiMessageReceived += OnMidiMessageForMapping;

        _listenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _listenTimer.Tick += (_, _) => StopListening(); // timeout — no mapping captured
        _listenTimer.Start();
    }

    private void StopListening()
    {
        _midi.MidiMessageReceived -= OnMidiMessageForMapping;
        _listenTimer?.Stop();
        _listenTimer = null;
        _listeningSlotIndex = -1;

        foreach (var r in _mappingRows)
        {
            r.MapButton.Content   = "Map";
            r.MapButton.IsEnabled = true;
        }
    }

    private void OnMidiMessageForMapping(object? sender, MidiInMessageEventArgs e)
    {
        if (e.MidiEvent is null) return;

        int slotIndex = _listeningSlotIndex;
        if (slotIndex < 0) return;

        // Only capture NoteOn (velocity > 0) or CC (value > 0); ignore everything else.
        int              triggerNumber  = -1;
        MidiTriggerType  triggerType    = MidiTriggerType.Note;
        int              triggerChannel = -1;

        switch (e.MidiEvent.CommandCode)
        {
            case MidiCommandCode.NoteOn when e.MidiEvent is NoteOnEvent noteOn && noteOn.Velocity > 0:
                triggerNumber  = noteOn.NoteNumber;
                triggerType    = MidiTriggerType.Note;
                triggerChannel = noteOn.Channel;
                break;

            case MidiCommandCode.ControlChange when e.MidiEvent is ControlChangeEvent cc && cc.ControllerValue > 0:
                triggerNumber  = (int)cc.Controller;
                triggerType    = MidiTriggerType.ControlChange;
                triggerChannel = cc.Channel;
                break;

            default:
                return; // wait for a usable message
        }

        // Marshal back to UI thread to update controls and mapping state.
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            _settings.ButtonMappings[slotIndex].TriggerNumber  = triggerNumber;
            _settings.ButtonMappings[slotIndex].TriggerType    = triggerType;
            _settings.ButtonMappings[slotIndex].TriggerChannel = triggerChannel;

            var row = _mappingRows.FirstOrDefault(r => r.SlotIndex == slotIndex);
            if (row is not null)
                row.TriggerLabel.Text = _settings.ButtonMappings[slotIndex].TriggerLabel;

            StopListening();
        });
    }

    private void ClearMapping(int slotIndex)
    {
        _settings.ButtonMappings[slotIndex].TriggerNumber  = -1;
        _settings.ButtonMappings[slotIndex].TriggerChannel = -1;

        var row = _mappingRows.FirstOrDefault(r => r.SlotIndex == slotIndex);
        if (row is not null)
            row.TriggerLabel.Text = "—";
    }

    // ── Active-instrument feedback ─────────────────────────────────────────────

    private void OnActiveInstrumentChanged(object? sender, InstrumentDefinition? instrument)
    {
        // Fires on MIDI callback thread — marshal to UI thread.
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
            () => UpdateActiveInstrumentUI(instrument));
    }

    private void UpdateActiveInstrumentUI(InstrumentDefinition? instrument)
    {
        ActiveInstrumentLabel.Text = instrument is null
            ? "Active instrument: —"
            : $"Active instrument: {instrument.DisplayName}";

        // Highlight the slot number whose InstrumentId matches the active instrument.
        foreach (var row in _mappingRows)
        {
            bool isActive = instrument is not null &&
                            _settings.ButtonMappings[row.SlotIndex].InstrumentId == instrument.Id;

            row.SlotBadge.Background = isActive
                ? new SolidColorBrush(Microsoft.UI.Colors.Crimson)
                : null;
            row.SlotLabel.Foreground = isActive
                ? new SolidColorBrush(Microsoft.UI.Colors.White)
                : null;
        }
    }

    /// <summary>Shows a FileOpenPicker for .sf2 files and returns the chosen path, or null if cancelled.</summary>
    private async Task<string?> PickSf2FileAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".sf2");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private void RestoreCurrentValues()
    {
        // Audio output device
        if (!string.IsNullOrEmpty(_settings.AudioOutputDeviceId))
        {
            for (int i = 1; i < AudioDeviceCombo.Items.Count; i++)
            {
                if (AudioDeviceCombo.Items[i] is AudioDeviceInfo d &&
                    d.Id == _settings.AudioOutputDeviceId)
                {
                    AudioDeviceCombo.SelectedIndex = i;
                    break;
                }
            }
        }
        else
        {
            AudioDeviceCombo.SelectedIndex = 0; // system default
        }

        // MIDI device
        if (_settings.MidiDeviceName is not null)
        {
            for (int i = 0; i < MidiDeviceCombo.Items.Count; i++)
            {
                if (MidiDeviceCombo.Items[i] is string name &&
                    name.Equals(_settings.MidiDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    MidiDeviceCombo.SelectedIndex = i;
                    break;
                }
            }
        }

        // Volume (stored 0–1, slider is 0–100)
        VolumeSlider.Value = Math.Clamp(_settings.Volume * 100.0, 0.0, 100.0);
    }

    // -------------------------------------------------------------------------
    // Event handlers
    // -------------------------------------------------------------------------

    private void OnRefreshMidiClicked(object sender, RoutedEventArgs e)
    {
        PopulateMidiDevices();
        RestoreCurrentValues();
    }

    private void OnRefreshAudioDeviceClicked(object sender, RoutedEventArgs e)
    {
        var previousId = _settings.AudioOutputDeviceId;
        PopulateAudioOutputDevices();

        // Re-select the previously chosen device if it's still present
        if (previousId is not null)
        {
            for (int i = 1; i < AudioDeviceCombo.Items.Count; i++)
            {
                if (AudioDeviceCombo.Items[i] is AudioDeviceInfo d && d.Id == previousId)
                {
                    AudioDeviceCombo.SelectedIndex = i;
                    return;
                }
            }
        }
        AudioDeviceCombo.SelectedIndex = 0;
    }

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // Guard: fires during InitializeComponent() before _audioEngine/_settings are assigned.
        if (_audioEngine is null) return;

        var pct = (int)Math.Round(e.NewValue);
        VolumeLabel.Text  = $"{pct}%";
        _settings.Volume  = (float)(e.NewValue / 100.0);
        _audioEngine.SetVolume(_settings.Volume);
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (MidiDeviceCombo.SelectedItem is string selectedDevice)
            _settings.MidiDeviceName = selectedDevice;

        // Audio output device (index 0 = system default → null)
        _settings.AudioOutputDeviceId = AudioDeviceCombo.SelectedIndex > 0 &&
                                         AudioDeviceCombo.SelectedItem is AudioDeviceInfo audioDevice
            ? audioDevice.Id
            : null;

        // Volume already updated live in OnVolumeChanged; _settings.Volume is current
        _onSave(_settings);

        // Visual feedback
        SaveButton.Content  = "Saved ✓";
        SaveButton.IsEnabled = false;
        var timer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            SaveButton.Content  = "Save";
            SaveButton.IsEnabled = true;
            timer.Stop();
        };
        timer.Start();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
        => AppWindow.Hide();

    private void OnTestSoundClicked(object sender, RoutedEventArgs e)
    {
        _audioEngine.NoteOn(0, 60, 80);

        TestSoundButton.IsEnabled = false;
        var timer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            _audioEngine.NoteOff(0, 60);
            TestSoundButton.IsEnabled = true;
            timer.Stop();
        };
        timer.Start();
    }
}

