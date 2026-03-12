using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MinimalMusicKeyboard.Interfaces;
using MinimalMusicKeyboard.Models;
using MinimalMusicKeyboard.Services;
using NAudio.Midi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        TextBlock TriggerLabel,
        Button MapButton,
        InstrumentDefinition? SlotInstrument, // Tracks the full definition for VST3 support
        StackPanel? Vst3StatusRow,
        TextBlock? Vst3StatusText,
        Button? Vst3ReloadBtn,
        Button? Vst3EditorBtn);
    private readonly List<MappingRowState> _mappingRows = new();

    // Listening-mode state (-1 = not listening)
    private int _listeningSlotIndex = -1;
    private DispatcherTimer? _listenTimer;

    private bool _forceClose;

    // Tracks which slot index is currently loading a VST3 plugin (-1 = none)
    private int _loadingVst3SlotIndex = -1;

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
            ButtonMappings     = currentSettings.ButtonMappings
                                    .Select(m => new InstrumentButtonMapping
                                    {
                                        SlotIndex      = m.SlotIndex,
                                        TriggerChannel = m.TriggerChannel,
                                        TriggerType    = m.TriggerType,
                                        TriggerNumber  = m.TriggerNumber,
                                        InstrumentId   = m.InstrumentId,
                                    }).ToArray(),
        };
        _onSave = onSave;

        AppWindow.Closing += OnWindowClosing;

        // Subscribe to active-instrument changes so the UI stays current whether
        // the window is visible or hidden.
        _switcher.ActiveInstrumentChanged += OnActiveInstrumentChanged;

        // Surface VST3 load state to the user via inline status and dialogs.
        _audioEngine.InstrumentLoadFailed    += OnInstrumentLoadFailed;
        _audioEngine.InstrumentLoadSucceeded += OnInstrumentLoadSucceeded;

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
        _switcher.ActiveInstrumentChanged   -= OnActiveInstrumentChanged;
        _audioEngine.InstrumentLoadFailed    -= OnInstrumentLoadFailed;
        _audioEngine.InstrumentLoadSucceeded -= OnInstrumentLoadSucceeded;
        _forceClose = true;
        Close();
    }

    private void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
                                  Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_forceClose) return; // let the app-shutdown close go through
        StopListening();         // ensure MIDI listener doesn't leak if window is dismissed mid-listen
        args.Cancel = true;      // intercept normal user close
        AppWindow.Hide();        // hide to tray instead
    }

    private void OnInstrumentLoadFailed(object? sender, string errorMessage)
    {
        // This event fires on a background thread — dispatch to UI thread.
        DispatcherQueue.TryEnqueue(async () =>
        {
            // Update per-slot inline status first.
            int loadingSlot = _loadingVst3SlotIndex;
            _loadingVst3SlotIndex = -1;
            if (loadingSlot >= 0)
                SetVst3SlotStatus(loadingSlot, $"❌ Failed: {errorMessage}", showReload: true, enableEditor: false);

            if (Content?.XamlRoot is null) return; // window not visible/loaded

            var dialog = new ContentDialog
            {
                Title          = "VST3 Load Failed",
                Content        = errorMessage,
                CloseButtonText = "OK",
                XamlRoot       = Content.XamlRoot,
            };
            try { await dialog.ShowAsync(); } catch { /* window closed mid-dialog */ }
        });
    }

    private void OnInstrumentLoadSucceeded(object? sender, string pluginPath)
    {
        // This event fires on a background thread — dispatch to UI thread.
        DispatcherQueue.TryEnqueue(() =>
        {
            int loadingSlot = _loadingVst3SlotIndex;
            _loadingVst3SlotIndex = -1;
            if (loadingSlot >= 0)
            {
                bool editorAvailable = _audioEngine.GetActiveBackend() is IEditorCapable capable && capable.SupportsEditor;
                string statusText = "✅ VST3 plugin loaded";
                if (!editorAvailable)
                    statusText = $"ℹ️ {_audioEngine.GetVst3EditorAvailabilityDescription()}";

                SetVst3SlotStatus(loadingSlot, statusText, showReload: false, enableEditor: editorAvailable);
            }
        });
    }

    private void SetVst3SlotStatus(int slotIdx, string statusText, bool showReload, bool enableEditor)
    {
        var row = _mappingRows.FirstOrDefault(r => r.SlotIndex == slotIdx);
        if (row is null) return;

        if (row.Vst3StatusText is not null)
            row.Vst3StatusText.Text = statusText;

        if (row.Vst3StatusRow is not null)
            row.Vst3StatusRow.Visibility = string.IsNullOrEmpty(statusText)
                ? Visibility.Collapsed
                : Visibility.Visible;

        if (row.Vst3ReloadBtn is not null)
            row.Vst3ReloadBtn.Visibility = showReload ? Visibility.Visible : Visibility.Collapsed;

        if (row.Vst3EditorBtn is not null)
            row.Vst3EditorBtn.IsEnabled = enableEditor;
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

        for (int i = 0; i < _settings.ButtonMappings.Length; i++)
        {
            var mapping = _settings.ButtonMappings[i];
            var slotIdx = i;

            // Load existing instrument definition if mapped
            InstrumentDefinition? slotInstrument = mapping.InstrumentId != null
                ? _catalog.GetById(mapping.InstrumentId)
                : null;

            bool isVst3 = slotInstrument?.Type == InstrumentType.Vst3;

            // ── Card container ────────────────────────────────────────────────────────
            var card = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var cardContent = new StackPanel { Spacing = 2 };
            card.Child = cardContent;

            // ── Single-row Grid ───────────────────────────────────────────────────────
            var rowGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });                                                  // 0: badge
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                                                     // 1: SF2/VST3 radios
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });                                                  // 2: Map
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });                                                  // 3: Clear
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                                                     // 4: CC label
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 100 });                // 5: path label (SF2 filename or VST3 plugin name)
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                                                     // 6: Browse/Plugin button

            // Col 0: Slot badge
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
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            Grid.SetColumn(slotBadge, 0);
            rowGrid.Children.Add(slotBadge);

            // Col 1: SF2/VST3 radio buttons
            var rbSf2 = new RadioButton
            {
                Content = "SF2",
                GroupName = $"type-slot-{i}",
                IsChecked = !isVst3,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
            };
            var rbVst3 = new RadioButton
            {
                Content = "VST3",
                GroupName = $"type-slot-{i}",
                IsChecked = isVst3,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Margin = new Thickness(8, 0, 0, 0),
            };
            var typePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0),
            };
            typePanel.Children.Add(rbSf2);
            typePanel.Children.Add(rbVst3);
            Grid.SetColumn(typePanel, 1);
            rowGrid.Children.Add(typePanel);

            // Col 2: Map button
            var mapBtn = new Button
            {
                Content = "Map",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontSize = 11,
                Margin = new Thickness(0),
            };
            mapBtn.Click += (_, _) => StartListening(slotIdx);
            Grid.SetColumn(mapBtn, 2);
            rowGrid.Children.Add(mapBtn);

            // Col 3: Clear button
            var clearBtn = new Button
            {
                Content = "✕",
                Width = 24,
                FontSize = 11,
                Margin = new Thickness(2, 0, 0, 0),
            };
            clearBtn.Click += (_, _) => ClearMapping(slotIdx);
            Grid.SetColumn(clearBtn, 3);
            rowGrid.Children.Add(clearBtn);

            // Col 4: Trigger label
            var triggerLabel = new TextBlock
            {
                Text = mapping.TriggerLabel,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                FontSize = 11,
                Opacity = 0.6,
                Margin = new Thickness(6, 0, 6, 0),
            };
            Grid.SetColumn(triggerLabel, 4);
            rowGrid.Children.Add(triggerLabel);

            // Col 5 (SF2 mode): SF2 filename label — shows the loaded file, toggled visible/hidden with type
            var sf2PathLabel = new TextBlock
            {
                Text = GetSf2TextForSlot(slotInstrument),
                FontSize = 11,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 4, 0),
                Visibility = isVst3 ? Visibility.Collapsed : Visibility.Visible,
            };
            Grid.SetColumn(sf2PathLabel, 5);
            rowGrid.Children.Add(sf2PathLabel);

            // Col 5 (VST3 mode): plugin path label
            var vst3PluginLabel = new TextBlock
            {
                Text = GetVst3PluginTextForSlot(slotInstrument),
                FontSize = 11,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 4, 0),
                Visibility = isVst3 ? Visibility.Visible : Visibility.Collapsed,
            };
            Grid.SetColumn(vst3PluginLabel, 5);
            rowGrid.Children.Add(vst3PluginLabel);

            // Col 6 (SF2 mode): Browse button
            var sf2Btn = new Button
            {
                Content = "Browse…",
                FontSize = 11,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = isVst3 ? Visibility.Collapsed : Visibility.Visible,
            };
            ToolTipService.SetToolTip(sf2Btn, slotInstrument?.SoundFontPath ?? "—");
            Grid.SetColumn(sf2Btn, 6);
            rowGrid.Children.Add(sf2Btn);

            // Col 6 (VST3 mode): Plugin button
            var vst3PluginBtn = new Button
            {
                Content = "Plugin…",
                FontSize = 11,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = isVst3 ? Visibility.Visible : Visibility.Collapsed,
            };
            Grid.SetColumn(vst3PluginBtn, 6);
            rowGrid.Children.Add(vst3PluginBtn);

            cardContent.Children.Add(rowGrid);

            // VST3 preset row — collapsed when SF2 is active, shown when VST3 is active
            var vst3PresetRow = new Grid
            {
                Margin = new Thickness(134, 2, 0, 0),
                Visibility = isVst3 ? Visibility.Visible : Visibility.Collapsed,
            };
            vst3PresetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            vst3PresetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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
                Content = "Preset…",
                FontSize = 11,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var vst3EditorBtn = new Button
            {
                Content = "Editor",
                FontSize = 11,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(vst3PresetLabel, 0);
            vst3PresetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(vst3PresetBtn, 1);
            Grid.SetColumn(vst3EditorBtn, 2);
            vst3PresetRow.Children.Add(vst3PresetLabel);
            vst3PresetRow.Children.Add(vst3PresetBtn);
            vst3PresetRow.Children.Add(vst3EditorBtn);
            cardContent.Children.Add(vst3PresetRow);

            // VST3 load-status row — hidden until a load is in progress/complete
            var vst3StatusRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(134, 2, 0, 0),
                Spacing = 6,
                Visibility = Visibility.Collapsed,
            };
            var vst3StatusText = new TextBlock
            {
                FontSize = 11,
                Opacity = 0.8,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var vst3ReloadBtn = new Button
            {
                Content = "↻ Retry",
                FontSize = 11,
                Padding = new Thickness(6, 1, 6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            vst3ReloadBtn.Click += (_, _) =>
            {
                var rowState = _mappingRows.FirstOrDefault(r => r.SlotIndex == slotIdx);
                var inst = rowState?.SlotInstrument;
                if (inst is { Type: InstrumentType.Vst3 } && !string.IsNullOrEmpty(inst.Vst3PluginPath))
                {
                    _loadingVst3SlotIndex = slotIdx;
                    SetVst3SlotStatus(slotIdx, "⏳ Loading VST3 plugin...", showReload: false, enableEditor: false);
                    _switcher.SelectInstrumentFromUi(inst);
                }
            };
            vst3StatusRow.Children.Add(vst3StatusText);
            vst3StatusRow.Children.Add(vst3ReloadBtn);
            cardContent.Children.Add(vst3StatusRow);

            // Wire up event handlers
            rbSf2.Checked += (_, _) =>
            {
                sf2PathLabel.Visibility    = Visibility.Visible;
                vst3PluginLabel.Visibility = Visibility.Collapsed;
                sf2Btn.Visibility          = Visibility.Visible;
                vst3PluginBtn.Visibility   = Visibility.Collapsed;
                vst3PresetRow.Visibility   = Visibility.Collapsed;
                vst3StatusRow.Visibility   = Visibility.Collapsed;
                UpdateSlotInstrumentType(slotIdx, InstrumentType.SoundFont);
            };
            rbVst3.Checked += (_, _) =>
            {
                sf2PathLabel.Visibility    = Visibility.Collapsed;
                vst3PluginLabel.Visibility = Visibility.Visible;
                sf2Btn.Visibility          = Visibility.Collapsed;
                vst3PluginBtn.Visibility   = Visibility.Visible;
                vst3PresetRow.Visibility   = Visibility.Visible;
                // Restore status row visibility if there's a status to show
                vst3StatusRow.Visibility   = string.IsNullOrEmpty(vst3StatusText.Text)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                UpdateSlotInstrumentType(slotIdx, InstrumentType.Vst3);
            };

            slotBadge.Click += (_, _) =>
            {
                var rowState = _mappingRows.FirstOrDefault(r => r.SlotIndex == slotIdx);
                if (rowState?.SlotInstrument is not null)
                    _switcher.SelectInstrumentFromUi(rowState.SlotInstrument);
            };

            sf2Btn.Click += async (_, _) =>
            {
                var path = await PickSf2FileAsync();
                if (path is null) return;

                var rowState = _mappingRows.FirstOrDefault(r => r.SlotIndex == slotIdx);
                InstrumentDefinition updated;
                if (rowState?.SlotInstrument is not null)
                {
                    updated = rowState.SlotInstrument with { SoundFontPath = path };
                    _catalog.UpdateInstrumentSoundFont(updated.Id, path);
                }
                else
                {
                    // No instrument assigned to this slot yet — create a slot-specific entry
                    updated = new InstrumentDefinition
                    {
                        Id            = $"sf2-slot-{slotIdx}",
                        DisplayName   = Path.GetFileNameWithoutExtension(path),
                        Type          = InstrumentType.SoundFont,
                        SoundFontPath = path,
                        BankNumber    = 0,
                        ProgramNumber = 0,
                        Category      = "Custom",
                    };
                    _catalog.AddOrUpdateInstrument(updated);
                    _settings.ButtonMappings[slotIdx].InstrumentId = updated.Id;
                }
                UpdateSlotInstrument(slotIdx, updated);
                sf2PathLabel.Text = Path.GetFileNameWithoutExtension(path);
                ToolTipService.SetToolTip(sf2Btn, path);
            };

            vst3PluginBtn.Click += async (_, _) =>
            {
                var path = await PickVst3PluginFileAsync();
                if (path is null) return;

                var rowState = _mappingRows.FirstOrDefault(r => r.SlotIndex == slotIdx);
                var currentInst = rowState?.SlotInstrument ?? new InstrumentDefinition
                {
                    Id = $"vst3-slot-{slotIdx}",
                    DisplayName = Path.GetFileNameWithoutExtension(path),
                    Type = InstrumentType.Vst3,
                    BankNumber = 0,
                    ProgramNumber = slotIdx,
                };

                var updated = currentInst with
                {
                    Vst3PluginPath = path,
                    // Refresh the display name from the new filename each time a plugin is picked.
                    DisplayName = Path.GetFileNameWithoutExtension(path),
                };
                UpdateSlotInstrument(slotIdx, updated);
                _catalog.AddOrUpdateInstrument(updated);
                vst3PluginLabel.Text = Path.GetFileName(path);
                ToolTipService.SetToolTip(vst3PluginLabel, path);
                _settings.ButtonMappings[slotIdx].InstrumentId = updated.Id;

                // Immediately kick off loading and show status.
                _loadingVst3SlotIndex = slotIdx;
                vst3StatusText.Text = "⏳ Loading VST3 plugin...";
                vst3StatusRow.Visibility = Visibility.Visible;
                vst3ReloadBtn.Visibility = Visibility.Collapsed;
                vst3EditorBtn.IsEnabled  = false;
                _switcher.SelectInstrumentFromUi(updated);
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
                    _catalog.AddOrUpdateInstrument(updated);
                    vst3PresetLabel.Text = Path.GetFileName(path);
                    ToolTipService.SetToolTip(vst3PresetLabel, path);
                }
            };

            vst3EditorBtn.Click += async (_, _) =>
            {
                // Disable during the async op — prevents multiple concurrent calls that
                // would each try to show a ContentDialog when they timeout or fail.
                vst3EditorBtn.IsEnabled = false;
                try
                {
                    var backend = _audioEngine.GetActiveBackend();
                    if (backend is IEditorCapable capable && capable.SupportsEditor)
                    {
                        await capable.OpenEditorAsync();
                    }
                    else if (_loadingVst3SlotIndex == slotIdx)
                    {
                        // Loading keeps the previous backend active until the VST3 bridge is ready.
                        var dialog = new ContentDialog
                        {
                            Title           = "VST3 Plugin Still Loading",
                            Content         = "The VST3 plugin is still loading. Please wait a moment and try again.",
                            CloseButtonText = "OK",
                            XamlRoot        = this.Content.XamlRoot,
                        };
                        await dialog.ShowAsync();
                    }
                    else
                    {
                        var message = _audioEngine.GetVst3EditorAvailabilityDescription();

                        var dialog = new ContentDialog
                        {
                            Title           = "Editor Not Available",
                            Content         = message,
                            CloseButtonText = "OK",
                            XamlRoot        = this.Content.XamlRoot,
                        };
                        await dialog.ShowAsync();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SettingsWindow] Failed to open VST3 editor: {ex.Message}");
                    var dialog = new ContentDialog
                    {
                        Title           = "Failed to Open Editor",
                        Content         = ex.Message,
                        CloseButtonText = "OK",
                        XamlRoot        = this.Content.XamlRoot,
                    };
                    await dialog.ShowAsync();
                }
                finally
                {
                    vst3EditorBtn.IsEnabled = _audioEngine.GetActiveBackend() is IEditorCapable currentCapable &&
                                              currentCapable.SupportsEditor;
                }
            };

            ButtonMappingsPanel.Children.Add(card);
            _mappingRows.Add(new MappingRowState(i, slotBadge, slotLabel, triggerLabel, mapBtn, slotInstrument, vst3StatusRow, vst3StatusText, vst3ReloadBtn, vst3EditorBtn));
        }
    }

    private string GetSf2TextForSlot(InstrumentDefinition? inst)
    {
        if (inst is null || inst.Type != InstrumentType.SoundFont) return "—";
        return string.IsNullOrEmpty(inst.SoundFontPath)
            ? "(no SF2 loaded)"
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
        InstrumentDefinition? instToPlay = null;
        foreach (var row in _mappingRows)
        {
            var inst = row.SlotInstrument;
            if (inst is not null &&
                inst.Type == InstrumentType.SoundFont &&
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

