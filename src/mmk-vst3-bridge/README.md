# mmk-vst3-bridge

Native out-of-process VST3 host bridge for Minimal Music Keyboard. The managed app (`Vst3BridgeBackend`) launches this executable, sends JSON commands over a named pipe, and reads rendered audio from a memory-mapped file that the bridge writes.

## Build (CMake + vcpkg)

```powershell
# From repo root
cd src\mmk-vst3-bridge

# Install dependencies
vcpkg install

# Configure + build (MSVC)
cmake -S . -B build -DCMAKE_TOOLCHAIN_FILE=<path>\vcpkg\scripts\buildsystems\vcpkg.cmake
cmake --build build --config Release
```

## VST3 SDK

The Steinberg VST3 SDK is not on vcpkg. Clone it separately and wire it into your build as needed:

```
extern/vst3sdk
```

The bridge currently stubs VST3 loading and renders silence. TODOs are left in the code for integrating `IPluginFactory`/`IAudioProcessor` once the SDK is available.
