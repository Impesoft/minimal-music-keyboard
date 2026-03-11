# mmk-vst3-bridge

Native out-of-process VST3 host bridge for Minimal Music Keyboard. The managed app (`Vst3BridgeBackend`) launches this executable, sends JSON commands over a named pipe, and reads rendered audio from a memory-mapped file that the bridge writes.

## Build (CMake + vcpkg)

```powershell
# From repo root
cd src\mmk-vst3-bridge

# Install dependencies
vcpkg install

# Configure + build (MSVC)
cmake -S . -B build -DCMAKE_TOOLCHAIN_FILE=<path>\vcpkg\scripts\buildsystems\vcpkg.cmake -DVST3_SDK_ROOT=..\..\..\extern\vst3sdk
cmake --build build --config Release
```

## VST3 SDK

The Steinberg VST3 SDK is not on vcpkg. Clone it separately and wire it into your build as needed:

```powershell
# From repo root
git clone https://github.com/steinbergmedia/vst3sdk.git extern/vst3sdk --recurse-submodules
```

If the SDK lives elsewhere, pass `-DVST3_SDK_ROOT=<path>` to CMake.

The bridge renders silence if no plugin is loaded or if the plugin fails to initialize.
