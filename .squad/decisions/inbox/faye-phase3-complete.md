# Decision: Phase 3 Complete — mmk-vst3-bridge Native Project

**Author:** Faye (Backend Dev)  
**Date:** 2026-11-03  
**Requested by:** Ward Impe  
**Status:** Ready for review

## Summary

Created the native C++ bridge project at `src/mmk-vst3-bridge/`. The bridge connects to the host's named pipe, opens the host-owned memory-mapped audio buffer, and runs a JSON command loop. Audio rendering is stubbed (silence) with TODOs for VST3 SDK integration.

## Delivered

- CMake + vcpkg setup (`CMakeLists.txt`, `vcpkg.json`)
- Bridge entry point and IPC client (named pipe client, JSON line protocol)
- Shared memory writer with MMF header validation and atomic write position updates
- Audio render thread with a lock-free MIDI event queue (stubbed render)
- README with build instructions and VST3 SDK setup note

## Notes

The VST3 SDK is not bundled. Clone `https://github.com/steinbergmedia/vst3sdk` into `extern/vst3sdk` when wiring up real plugin loading.
