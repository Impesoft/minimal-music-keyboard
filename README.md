# Minimal Music Keyboard

Minimal Music Keyboard is a small Windows tray app for playing MIDI keyboards and controllers without opening a full DAW.

It supports both **SF2 soundfonts** and **VST3 instruments**, lets you map **8 instrument slots** to MIDI buttons or pads, and keeps the app out of the way in the system tray until you need it.

## Features

- Windows tray app with on-demand settings window
- MIDI input device selection
- Audio output device selection
- SF2 and VST3 instrument support
- Automatic instrument type detection from the selected file (`.sf2` or `.vst3`)
- 8 mappable instrument slots
- MIDI-driven slot switching
- VST3 editor support for plugins that expose one
- Self-contained Release builds

## Quick start

1. Download the latest release from the repository's Releases page.
2. Extract the zip file.
3. Run `MinimalMusicKeyboard.exe`.
4. Open the tray icon and choose `Settings`.
5. Select your MIDI input device.
6. Browse for an instrument file for each slot:
   - `.sf2` for a soundfont
   - `.vst3` for a VST3 instrument
7. Click `Map` on a slot and press the MIDI note/CC you want to use to trigger that slot.

The app stores settings under `%LOCALAPPDATA%\MinimalMusicKeyboard`.

## Building from source

### Prerequisites

- Windows
- .NET 10 SDK
- Visual Studio 2022 or newer
- For VST3 support:
  - Steinberg VST3 SDK
  - vcpkg
  - Visual Studio C++ build tools / CMake

### 1. Build the native VST3 bridge

The managed app can run SF2-only without the native bridge, but **VST3 support requires it**.

Clone the Steinberg SDK to:

```powershell
git clone https://github.com/steinbergmedia/vst3sdk.git extern\vst3sdk --recurse-submodules
```

Then build the bridge:

```powershell
cd src\mmk-vst3-bridge
cmake -S . -B build -DCMAKE_TOOLCHAIN_FILE=<path>\vcpkg\scripts\buildsystems\vcpkg.cmake -DVST3_SDK_ROOT=..\..\extern\vst3sdk
cmake --build build --config Release
```

### 2. Build the app

From the repository root:

```powershell
dotnet build MinimalMusicKeyboard.sln -c Release
```

The app project copies the built `mmk-vst3-bridge.exe` into the output directory automatically when the native bridge exists.

### 3. Run tests

```powershell
dotnet test MinimalMusicKeyboard.sln -c Release --no-build
```

## Release output

The current shipped release artifact is the self-contained `win-x64` output from:

```text
src\MinimalMusicKeyboard\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64
```

## Project layout

```text
src\MinimalMusicKeyboard\           WinUI 3 tray app
src\mmk-vst3-bridge\                Native out-of-process VST3 host bridge
docs\architecture.md                Architecture and implementation notes
tests\                             Test projects and test assets
```

## Notes

- This is a **Windows-only** project.
- The app is designed for lightweight direct use, not as a full DAW replacement.
- VST3 support depends on the native bridge being built and present beside the managed app.
- The current test command succeeds, but the test project still reports **0 discovered tests**.

## Documentation

- Architecture: `docs\architecture.md`
- Native bridge notes: `src\mmk-vst3-bridge\README.md`

