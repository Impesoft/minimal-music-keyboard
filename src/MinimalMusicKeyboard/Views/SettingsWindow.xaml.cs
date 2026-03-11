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
    private sealed record MappingRowState(
        int SlotIndex,
        Button SlotBadge,
        TextBlock SlotLabel,
        RadioButtons TypeSelector,
        StackPanel Sf2Panel,
        StackPanel Vst3Panel,
        ComboBox InstrumentCombo,
        TextBlock Sf2Label,
        TextBlock Vst3PluginLabel,
        TextBlock Vst3PresetLabel,
        TextBlock TriggerLabel,
        Button MapButton,
        InstrumentDefinition? SlotInstrument); // Tracks the full definition for VST3 support
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

            // Load existing instrument definition if mapped
            InstrumentDefinition? slotInstrument = mapping.InstrumentId != null
                ? _catalog.GetById(mapping.InstrumentId)
                : null;

            // Build the row UI
            var rowContainer = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };

            // ── Row 1: Badge | Instrument Name/Type Selector | Trigger | Map | ✕ ───────
            var row1 = new Grid();
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });           // badge
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // type/name
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });           // trigger
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });           // Map
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });           // ✕

            // Slot badge — clickable button to select this instrument from the UI
            var slotLabel = new TextBlock
            {
                Text = $"{i + 1}",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                IsHitTestVisible = false,
            };
            var slotBadge = new Button
            {
                Content = slotLabel,
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 90, 0, 0)),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            // Type selector + instrument config panel
            var configPanel = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };

            var typeSelector = new RadioButtons
            {
                ItemsSource = new[] { "SF2 (SoundFont)", "VST3 Plugin" },
                SelectedIndex = slotInstrument?.Type == InstrumentType.Vst3 ? 1 : 0,
                Margin = new Thickness(0, 0, 0, 4),
            };

            // SF2 panel: Instrument combo + SF2 path + browse
            var sf2Panel = new StackPanel { Spacing = 4 };
            var combo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontSize = 12,
            };
            combo.Items.Add("(none)");
            foreach (var inst in catalogItems)
                combo.Items.Add(inst.DisplayName);

            int initialIdx = 0;
            if (slotInstrument?.Type == InstrumentType.SoundFont && mapping.InstrumentId != null)
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

            var sf2PathGrid = new Grid();
            sf2PathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sf2PathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

            var sf2Label = new TextBlock
            {
                Text = GetSf2TextForSlot(slotInstrument),
                FontSize = 11,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 4, 0),
            };
            var sf2Btn = new Button
            {
                Content = "Browse…",
                FontSize = 11,
                Padding = new Thickness(4, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(sf2Label, 0);
            Grid.SetColumn(sf2Btn, 1);
            sf2PathGrid.Children.Add(sf2Label);
            sf2PathGrid.Children.Add(sf2Btn);

            sf2Panel.Children.Add(combo);
            sf2Panel.Children.Add(sf2PathGrid);
            sf2Panel.Visibility = typeSelector.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;

            // VST3 panel: Plugin path + browse, Preset path + browse
            var vst3Panel = new StackPanel { Spacing = 4 };
            
            var vst3PluginGrid = new Grid();
            vst3PluginGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            vst3PluginGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            var vst3PluginLabel = new TextBlock
            {
                Text = GetVst3PluginTextForSlot(slotInstrument),
                FontSize = 11,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 4, 0),
            };
            var vst3PluginBtn = new Button
            {
                Content = "Browse…",
                FontSize = 11,
                Padding = new Thickness(4, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(vst3PluginLabel, 0);
            Grid.SetColumn(vst3PluginBtn, 1);
            vst3PluginGrid.Children.Add(vst3PluginLabel);
            vst3PluginGrid.Children.Add(vst3PluginBtn);

            var vst3PresetGrid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            vst3PresetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            vst3PresetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            var vst3PresetLabel = new TextBlock
            {
                Text = GetVst3PresetTextForSlot(slotInstrument),
                FontSize = 11,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 4, 0),
            };
            var vst3PresetBtn = new Button
            {
                Content = "Browse…",
                FontSize = 11,
                Padding = new Thickness(4, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(vst3PresetLabel, 0);
            Grid.SetColumn(vst3PresetBtn, 1);
            vst3PresetGrid.Children.Add(vst3PresetLabel);
            vst3PresetGrid.Children.Add(vst3PresetBtn);

            vst3Panel.Children.Add(new TextBlock { Text = "Plugin:", FontSize = 11, Opacity = 0.7 });
            vst3Panel.Children.Add(vst3PluginGrid);
            vst3Panel.Children.Add(new TextBlock { Text = "Preset (optional):", FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0) });
            vst3Panel.Children.Add(vst3PresetGrid);
            vst3Panel.Visibility = typeSelector.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;

            configPanel.Children.Add(typeSelector);
            configPanel.Children.Add(sf2Panel);
            configPanel.Children.Add(vst3Panel);

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

            Grid.SetColumn(slotBadge, 0);
            Grid.SetColumn(configPanel, 1);
            Grid.SetColumn(triggerLabel, 2);
            Grid.SetColumn(mapBtn, 3);
            Grid.SetColumn(clearBtn, 4);

            row1.Children.Add(slotBadge);
            row1.Children.Add(configPanel);
            row1.Children.Add(triggerLabel);
            row1.Children.Add(mapBtn);
            row1.Children.Add(clearBtn);

            rowContainer.Children.Add(row1);

            // Wire up event handlers
            typeSelector.SelectionChanged += (_, _) =>
            {
                sf2Panel.Visibility = typeSelector.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
                vst3Panel.Visibility = typeSelector.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
                UpdateSlotInstrumentType(slotIdx, typeSelector.SelectedIndex == 1 ? InstrumentType.Vst3 : InstrumentType.SoundFont);
            };

            slotBadge.Click += (_, _) =>
            {
                var rowState = _mappingRows.FirstOrDefault(r => r.SlotIndex == slotIdx);
                if (rowState?.SlotInstrument is not null)
                    _switcher.SelectInstrumentFromUi(rowState.SlotInstrument);
            };

            combo.SelectionChanged += (_, _) =>
            {
                var idx = combo.SelectedIndex;
                if (idx <= 0)
                {
                    _settings.ButtonMappings[slotIdx].InstrumentId = null;
                    UpdateSlotInstrument(slotIdx, null);
                    sf2Label.Text = "—";
                }
                else
                {
                    var selectedInst = catalogItems[idx - 1];
                    _settings.ButtonMappings[slotIdx].InstrumentId = selectedInst.Id;
                    UpdateSlotInstrument(slotIdx, selectedInst);
                    sf2Label.Text = GetSf2TextForSlot(selectedInst);
                    ToolTipService.SetToolTip(sf2Label, selectedInst.SoundFontPath);
                }
            };

            sf2Btn.Click += async (_, _) =>
            {
                var path = await PickSf2FileAsync();
                if (path is null) return;

                var rowState = _mappingRows.FirstOrDefault(r => r.SlotIndex == slotIdx);
                if (rowState?.SlotInstrument is not null)
                {
                    var updated = rowState.SlotInstrument with { SoundFontPath = path };
                    UpdateSlotInstrument(slotIdx, updated);
                    sf2Label.Text = Path.GetFileNameWithoutExtension(path);
                    ToolTipService.SetToolTip(sf2Label, path);
                    _catalog.UpdateInstrumentSoundFont(updated.Id, path);
                }
            };

            vst3PluginBtn.Click += async (_, _) =>
            {
                var path = await PickVst3PluginFileAsync();
                if (path is null) return;

                var rowState = _mappingRows.FirstOrDefault(r => r.SlotIndex == slotIdx);
                var currentInst = rowState?.SlotInstrument ?? new InstrumentDefinition
                {
                    Id = $"vst3-slot-{slotIdx}",
                    DisplayName = $"VST3 Slot {slotIdx + 1}",
                    Type = InstrumentType.Vst3,
                    BankNumber = 0,
                    ProgramNumber = slotIdx,
                };

                var updated = currentInst with { Vst3PluginPath = path };
                UpdateSlotInstrument(slotIdx, updated);
                _catalog.AddOrUpdateVst3Instrument(updated);
                vst3PluginLabel.Text = Path.GetFileName(path);
                ToolTipService.SetToolTip(vst3PluginLabel, path);
                _settings.ButtonMappings[slotIdx].InstrumentId = updated.Id;
            };

            vst3PresetBtn.Click += async (_, _) =>
            {
                var path = await PickVst3PresetFileAsync();
                if (path is null) return;

                var rowState = _mappingRows.FirstOrDefault(r => r.SlotIndex == slotIdx);
                if (rowState?.SlotInstrument is not null)
                {
                    var updated = rowState.SlotInstrument with { Vst3PresetPath = path };
                    UpdateSlotInstrument(slotIdx, updated);
                    _catalog.AddOrUpdateVst3Instrument(updated);
                    vst3PresetLabel.Text = Path.GetFileName(path);
                    ToolTipService.SetToolTip(vst3PresetLabel, path);
                }
            };

            ButtonMappingsPanel.Children.Add(rowContainer);
            _mappingRows.Add(new MappingRowState(i, slotBadge, slotLabel, typeSelector, sf2Panel, vst3Panel,
                combo, sf2Label, vst3PluginLabel, vst3PresetLabel, triggerLabel, mapBtn, slotInstrument));
        }
    }

    private string GetSf2TextForSlot(InstrumentDefinition? inst)
    {
        if (inst is null || inst.Type != InstrumentType.SoundFont) return "—";
        return inst.SoundFontPath == "[SoundFont Not Configured]" || string.IsNullOrEmpty(inst.SoundFontPath)
            ? "(no SF2)"
            : Path.GetFileNameWithoutExtension(inst.SoundFontPath);
    }

    private string GetVst3PluginTextForSlot(InstrumentDefinition? inst)
    {
        if (inst is null || inst.Type != InstrumentType.Vst3) return "No plugin selected";
        return string.IsNullOrEmpty(inst.Vst3PluginPath)
            ? "No plugin selected"
            : Path.GetFileName(inst.Vst3PluginPath);
    }

    private string GetVst3PresetTextForSlot(InstrumentDefinition? inst)
    {
        if (inst is null || inst.Type != InstrumentType.Vst3) return "(none)";
        return string.IsNullOrEmpty(inst.Vst3PresetPath)
            ? "(none)"
            : Path.GetFileName(inst.Vst3PresetPath);
    }

    private void UpdateSlotInstrument(int slotIdx, InstrumentDefinition? instrument)
    {
        var idx = _mappingRows.FindIndex(r => r.SlotIndex == slotIdx);
        if (idx < 0) return;
        var old = _mappingRows[idx];
        _mappingRows[idx] = old with { SlotInstrument = instrument };
    }

    private void UpdateSlotInstrumentType(int slotIdx, InstrumentType type)
    {
        var rowState = _mappingRows.FirstOrDefault(r => r.SlotIndex == slotIdx);
        if (rowState?.SlotInstrument is not null)
        {
            var updated = rowState.SlotInstrument with { Type = type };
            UpdateSlotInstrument(slotIdx, updated);
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
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 90, 0, 0));
            row.SlotLabel.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
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

    /// <summary>Shows a FileOpenPicker for .vst3 files and returns the chosen path, or null if cancelled.</summary>
    private async Task<string?> PickVst3PluginFileAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add(".vst3");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>Shows a FileOpenPicker for .vstpreset files and returns the chosen path, or null if cancelled.</summary>
    private async Task<string?> PickVst3PresetFileAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add(".vstpreset");
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
        // Find the first slot that has an instrument with an assigned and existing SF2 file.
        var catalogItems = _catalog.GetAll();
        InstrumentDefinition? instToPlay = null;
        foreach (var row in _mappingRows)
        {
            var idx = row.InstrumentCombo.SelectedIndex;
            if (idx <= 0 || idx - 1 >= catalogItems.Count) continue;
            var inst = _catalog.GetById(catalogItems[idx - 1].Id);
            if (inst is not null &&
                inst.SoundFontPath != "[SoundFont Not Configured]" &&
                !string.IsNullOrEmpty(inst.SoundFontPath) &&
                File.Exists(inst.SoundFontPath))
            {
                instToPlay = inst;
                break;
            }
        }

        if (instToPlay is null)
        {
            TestSoundButton.Content = "⚠ No SF2 configured";
            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            reset.Tick += (_, _) => { TestSoundButton.Content = "▶ Test Sound"; reset.Stop(); };
            reset.Start();
            return;
        }

        TestSoundButton.IsEnabled = false;

        // Select the instrument — this triggers an async SF2 load if not already cached.
        _switcher.SelectInstrumentFromUi(instToPlay);

        // Give the background SF2 load ~400 ms to complete before firing the note.
        var loadWait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        loadWait.Tick += (_, _) =>
        {
            loadWait.Stop();
            _audioEngine.NoteOn(0, 60, 80);

            var noteOff = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            noteOff.Tick += (_, _) =>
            {
                _audioEngine.NoteOff(0, 60);
                TestSoundButton.IsEnabled = true;
                TestSoundButton.Content = "▶ Test Sound";
                noteOff.Stop();
            };
            noteOff.Start();
        };
        loadWait.Start();
    }
}

