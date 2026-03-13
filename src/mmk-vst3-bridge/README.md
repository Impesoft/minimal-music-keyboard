# mmk-vst3-bridge

Native out-of-process VST3 host bridge for Minimal Music Keyboard. The managed app (`Vst3BridgeBackend`) launches this executable, sends JSON commands over a named pipe, and reads rendered audio from a memory-mapped file that the bridge writes.

> [!WARNING]
> If someone clones the repository and only builds the managed WinUI project, **VST3 will not work yet**.
>
> This native bridge must be built separately, and the Steinberg VST3 SDK must be installed separately as well. Neither the SDK nor the compiled `mmk-vst3-bridge.exe` is stored in git.

## What this executable is for

`MinimalMusicKeyboard` uses this native process to host VST3 plugins out of process.

The managed project expects the Release bridge here:

```text
src\mmk-vst3-bridge\build\Release\mmk-vst3-bridge.exe
```

After the managed app is built, `src\MinimalMusicKeyboard\MinimalMusicKeyboard.csproj` copies that executable into the app output folder.

## Build (CMake + vcpkg)

### 1. Clone the Steinberg VST3 SDK

From the repository root:

```powershell
git clone https://github.com/steinbergmedia/vst3sdk.git extern\vst3sdk --recurse-submodules
```

If you keep the SDK elsewhere, pass its location explicitly through `-DVST3_SDK_ROOT=<path>`.

### 2. Install native dependencies

```powershell
vcpkg install
```

Also make sure you have:

- Visual Studio C++ build tools
- CMake
- a compatible MSVC toolchain

### 3. Configure the bridge

```powershell
# From repo root
cd src\mmk-vst3-bridge

cmake -S . -B build -DCMAKE_TOOLCHAIN_FILE=<path>\vcpkg\scripts\buildsystems\vcpkg.cmake -DVST3_SDK_ROOT=..\..\extern\vst3sdk
```

Notes:

- Replace `<path>` with your actual `vcpkg` root
- If the SDK is in `extern\vst3sdk` relative to the repository root, use `-DVST3_SDK_ROOT=..\..\extern\vst3sdk`
- If the SDK lives elsewhere, replace `-DVST3_SDK_ROOT=...` with the correct path

### 4. Build the bridge

```powershell
cmake --build build --config Release
```

Expected output:

```text
build\Release\mmk-vst3-bridge.exe
```

## Integrating with the managed app

Once the bridge exists, build the managed app from the repository root:

```powershell
dotnet build MinimalMusicKeyboard.sln -c Release
```

During that build, the app project:

- looks for `src\mmk-vst3-bridge\build\Release\mmk-vst3-bridge.exe`
- copies it beside the app
- also writes a versioned deployed copy and a `mmk-vst3-bridge.path` manifest

If the bridge executable is missing, the managed build can still succeed, but VST3 support in the app will not be available.

## Common fresh-clone failure mode

If VST3 does not work after cloning the repo, usually one of these is true:

- the Steinberg SDK was never cloned
- CMake was never run for `src\mmk-vst3-bridge`
- the bridge was built in the wrong configuration
- the managed app was built before the bridge existed

The bridge renders silence if no plugin is loaded or if the plugin fails to initialize.
