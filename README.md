# Minimal Music Keyboard

Minimal Music Keyboard is a small Windows tray app for playing MIDI keyboards and controllers without opening a full DAW.

It supports both **SF2 soundfonts** and **VST3 instruments**, lets you map **8 instrument slots** to MIDI buttons or pads, and keeps the app out of the way in the system tray until you need it.

> [!WARNING]
> A **fresh clone of this repository will not have working VST3 support yet**.
>
> VST3 support depends on a separate native executable, `mmk-vst3-bridge.exe`, which is built from `src\mmk-vst3-bridge` and is **not** committed to git. The Steinberg VST3 SDK is also **not** included in this repository. After cloning, SF2 support can work once you build the managed app, but VST3 support will not work until you install the SDK and build the bridge.

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

### Fresh clone note

If you only run:

```powershell
dotnet build MinimalMusicKeyboard.sln -c Release
```

the app itself will build, but **VST3 support may still be unavailable** because the native bridge has not been produced yet.

The managed project expects the bridge binary at:

```text
src\mmk-vst3-bridge\build\Release\mmk-vst3-bridge.exe
```

That path is wired in `src\MinimalMusicKeyboard\MinimalMusicKeyboard.csproj` via the `BridgeSource` property. During the managed build, the app tries to copy that bridge into its output directory. If the file is missing, the build emits a warning and the resulting app build will only be usable for SF2 instruments.

### 1. Build the native VST3 bridge

The managed app can run SF2-only without the native bridge, but **VST3 support requires it**.

#### 1.1 Clone the Steinberg VST3 SDK

From the repository root, clone the Steinberg SDK into `extern\vst3sdk`:

```powershell
git clone https://github.com/steinbergmedia/vst3sdk.git extern\vst3sdk --recurse-submodules
```

If you prefer a different location, that is fine, but you must pass the correct path to CMake through `-DVST3_SDK_ROOT=...`.

#### 1.2 Install native dependencies

The bridge also uses packages resolved through `vcpkg`.

At minimum, make sure:

- `vcpkg` is installed
- the Visual Studio C++ toolchain is installed
- CMake is available

If needed:

```powershell
vcpkg install
```

#### 1.3 Configure the bridge

From the repository root:

```powershell
cd src\mmk-vst3-bridge
cmake -S . -B build -DCMAKE_TOOLCHAIN_FILE=<path>\vcpkg\scripts\buildsystems\vcpkg.cmake -DVST3_SDK_ROOT=..\..\extern\vst3sdk
```

If your SDK is elsewhere, replace `..\..\extern\vst3sdk` with the actual path.

#### 1.4 Build the bridge

```powershell
cmake --build build --config Release
```

Expected output:

```text
src\mmk-vst3-bridge\build\Release\mmk-vst3-bridge.exe
```

#### 1.5 Build the managed app after the bridge exists

Once the bridge exists, build the app:

```powershell
dotnet build MinimalMusicKeyboard.sln -c Release
```

The managed project will then auto-deploy the bridge into the app output directory, including a versioned bridge copy and a `mmk-vst3-bridge.path` manifest file used at runtime.

### 2. Build the app

From the repository root:

```powershell
dotnet build MinimalMusicKeyboard.sln -c Release
```

The app project copies the built `mmk-vst3-bridge.exe` into the output directory automatically when the native bridge exists. If it does not exist, the app still builds, but VST3 instruments will not load.

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

## Troubleshooting VST3 on a fresh clone

If users clone the repo and immediately try VST3, the most likely problem is one of these:

1. `extern\vst3sdk` has not been cloned yet
2. `src\mmk-vst3-bridge\build\Release\mmk-vst3-bridge.exe` does not exist yet
3. only the managed app was built, not the native bridge
4. the bridge was built in a different configuration or location than the managed project expects

If that happens, rebuild the native bridge first, then rebuild the managed solution.

## Documentation

- Architecture: `docs\architecture.md`
- Native bridge notes: `src\mmk-vst3-bridge\README.md`

