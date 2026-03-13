# mmk-vst3-bridge

Native out-of-process VST3 host bridge for Minimal Music Keyboard. The managed app (`Vst3BridgeBackend`) launches this executable, sends JSON commands over a named pipe, and reads rendered audio from a memory-mapped file that the bridge writes.

> [!WARNING]
> If someone clones the repository and only builds the managed WinUI project, **VST3 will not work yet**.
>
> New here? Do these steps in this order:
>
> 1. Clone the Steinberg VST3 SDK into `extern\vst3sdk`
> 2. Configure and build this bridge project
> 3. Build `MinimalMusicKeyboard.sln`
>
> If you skip step 2, the app may still build, but VST3 support in the app will not work.

## What this executable is for

`MinimalMusicKeyboard` uses this native process to host VST3 plugins out of process.

The managed project expects the Release bridge here:

```text
src\mmk-vst3-bridge\build\Release\mmk-vst3-bridge.exe
```

After the managed app is built, `src\MinimalMusicKeyboard\MinimalMusicKeyboard.csproj` copies that executable into the app output folder.

## Build

### 1. Clone the Steinberg VST3 SDK

From the repository root:

```powershell
git clone https://github.com/steinbergmedia/vst3sdk.git extern\vst3sdk --recurse-submodules
```

If you keep the SDK elsewhere, pass its location explicitly through `-DVST3_SDK_ROOT=<path>`.

### 2. Install native build tools

Make sure you have:

- Visual Studio C++ build tools
- CMake
- a compatible MSVC toolchain

You do **not** need `vcpkg` for the default build documented here. This project already falls back to the bundled `include\nlohmann\json.hpp` header if `nlohmann_json` is not provided externally.

### 3. Configure the bridge

```powershell
# From repo root
cd src\mmk-vst3-bridge

cmake -S . -B build -DVST3_SDK_ROOT=..\..\extern\vst3sdk
```

Notes:

- If the SDK is in `extern\vst3sdk` relative to the repository root, `-DVST3_SDK_ROOT=..\..\extern\vst3sdk` is the expected value
- If the SDK lives elsewhere, replace `-DVST3_SDK_ROOT=...` with the correct path
- If you decide to use `vcpkg` anyway, treat that as optional custom setup and configure into a fresh build directory

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
