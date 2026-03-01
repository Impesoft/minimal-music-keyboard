using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MinimalMusicKeyboard.Models;
using MinimalMusicKeyboard.Services;
using System;
using System.IO;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MinimalMusicKeyboard.Views;

/// <summary>
/// On-demand settings window. Receives services via constructor; does not own them.
/// Created fresh on each open; nulled and GC'd on close (architecture Section 3.6).
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly MidiDeviceService _midi;
    private readonly InstrumentCatalog _catalog;
    private readonly AppSettings       _settings;
    private readonly Action<AppSettings> _onSave;

    public SettingsWindow(
        MidiDeviceService    midi,
        InstrumentCatalog    catalog,
        AppSettings          currentSettings,
        Action<AppSettings>  onSave)
    {
        InitializeComponent();
        Title = "Minimal Music Keyboard — Settings";

        _midi     = midi;
        _catalog  = catalog;
        _settings = new AppSettings
        {
            MidiDeviceName = currentSettings.MidiDeviceName,
            SoundFontPath  = currentSettings.SoundFontPath,
        };
        _onSave = onSave;

        PopulateMidiDevices();
        PopulateInstruments();
        RestoreCurrentValues();
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

    private void PopulateInstruments()
    {
        InstrumentList.Items.Clear();
        foreach (var inst in _catalog.GetAll())
        {
            // DataTemplate binds to anonymous container — use a simple StackPanel approach
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

            var name = new TextBlock { Text = inst.DisplayName, VerticalAlignment = VerticalAlignment.Center };
            var cat  = new TextBlock { Text = inst.Category,    VerticalAlignment = VerticalAlignment.Center,
                                       FontSize = 11, Opacity = 0.5, Margin = new Thickness(12, 0, 12, 0) };
            var prog = new TextBlock { Text = $"PC {inst.ProgramNumber}", VerticalAlignment = VerticalAlignment.Center,
                                       FontSize = 11, Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Right };

            Grid.SetColumn(name, 0);
            Grid.SetColumn(cat,  1);
            Grid.SetColumn(prog, 2);

            row.Children.Add(name);
            row.Children.Add(cat);
            row.Children.Add(prog);

            InstrumentList.Items.Add(row);
        }
    }

    private void RestoreCurrentValues()
    {
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

        // SoundFont
        if (!string.IsNullOrEmpty(_settings.SoundFontPath))
        {
            SoundFontPathBox.Text = _settings.SoundFontPath;
            UpdateSoundFontStatus(_settings.SoundFontPath);
        }
    }

    // -------------------------------------------------------------------------
    // Event handlers
    // -------------------------------------------------------------------------

    private void OnRefreshMidiClicked(object sender, RoutedEventArgs e)
    {
        PopulateMidiDevices();
        RestoreCurrentValues();
    }

    private async void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        // Associate with this window's HWND — required for unpackaged WinUI3 apps.
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".sf2");

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        SoundFontPathBox.Text     = file.Path;
        _settings.SoundFontPath   = file.Path;
        UpdateSoundFontStatus(file.Path);
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (MidiDeviceCombo.SelectedItem is string selectedDevice)
            _settings.MidiDeviceName = selectedDevice;

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
        => Close();

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void UpdateSoundFontStatus(string path)
    {
        SoundFontStatusText.Text = File.Exists(path)
            ? $"✓  {Path.GetFileName(path)}"
            : "⚠  File not found — please select a valid .sf2 file.";
    }
}

