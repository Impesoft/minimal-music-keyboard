# Phase 3 Code Review: mmk-vst3-bridge C++ Scaffold

**Date:** 2026-03-11
**Reviewer:** Gren
**Author:** Faye

## Files Reviewed

- `src/mmk-vst3-bridge/CMakeLists.txt`
- `src/mmk-vst3-bridge/vcpkg.json`
- `src/mmk-vst3-bridge/src/main.cpp`
- `src/mmk-vst3-bridge/src/bridge.h` / `bridge.cpp`
- `src/mmk-vst3-bridge/src/ipc_client.h` / `ipc_client.cpp`
- `src/mmk-vst3-bridge/src/audio_renderer.h` / `audio_renderer.cpp`
- `src/mmk-vst3-bridge/src/mmf_writer.h` / `mmf_writer.cpp`
- `src/mmk-vst3-bridge/README.md`
- `docs/vst3-architecture-proposal.md` (approved spec, cross-referenced)
- `src/MinimalMusicKeyboard/Services/Vst3BridgeBackend.cs` (approved Phase 2, cross-referenced)

## Summary

The C++ scaffold is structurally sound and correctly implements every IPC contract established by the approved Phase 2 managed side. The bridge is the client; the host is the server — matching the spec and the approved `Vst3BridgeBackend.cs` character-for-character. All six JSON commands (`load`, `noteOn`, `noteOff`, `noteOffAll`, `setProgram`, `shutdown`) are handled, the `load` ack format matches `ParseLoadAck` on the managed side, and the MMF header layout (magic `0x4D4D4B56`, version 1, frameSize, writePos at offsets 0/4/8/12, audio at offset 16) is byte-compatible. The threading model is clean: main thread runs the JSON command loop, a dedicated render thread fills the MMF, and a lock-free SPSC ring buffer transfers MIDI events between them. RAII is used consistently — all three resource-holding classes have destructors that release handles. No blocking or required issues found.

## Checklist Results

### IPC Correctness ✅

| Check | Result |
|-------|--------|
| Bridge uses `CreateFileW` (client) to connect to `\\.\pipe\mmk-vst3-{hostPid}` | ✅ `ipc_client.cpp:17` — `CreateFileW` + `OPEN_EXISTING` |
| Bridge opens existing MMF via `OpenFileMappingW` (not `CreateFileMappingW`) | ✅ `mmf_writer.cpp:96` — `OpenFileMappingW` |
| `hostPid` from `argv[1]` | ✅ `main.cpp:15` — `std::stoul(argv[1])` |
| Pipe name: `mmk-vst3-{hostPid}` | ✅ `ipc_client.cpp:15` |
| MMF name: `mmk-vst3-audio-{hostPid}` | ✅ `mmf_writer.cpp:23` |
| All 6 commands handled | ✅ `bridge.cpp:60–111` |
| `load` ack format: `{"ack":"load","ok":bool,"error":"..."}` | ✅ `bridge.cpp:67–73`, matches `ParseLoadAck` in Phase 2 |
| `WaitNamedPipeW` retry on `ERROR_PIPE_BUSY` | ✅ `ipc_client.cpp:28–41` |

### MMF Ring Buffer Write Side ✅

| Check | Result |
|-------|--------|
| Header: 4B magic `0x4D4D4B56` | ✅ `mmf_writer.cpp:8` |
| Header: 4B version = 1 | ✅ `mmf_writer.cpp:9`, validated at open |
| Header: 4B frameSize at offset 8 | ✅ `mmf_writer.cpp:37` — `header[2]` |
| Header: 4B writePos at offset 12 | ✅ `mmf_writer.cpp:38` — byte offset 12 |
| Audio data: float32 stereo interleaved at offset 16 | ✅ `mmf_writer.cpp:39` — offset `kHeaderSize` = 16 |
| `InterlockedExchange` for atomic writePos update | ✅ `mmf_writer.cpp:60` |
| Bridge WRITES audio, host READS | ✅ Correct direction |
| Silence stub fills zeros | ✅ `audio_renderer.cpp:122` — `std::fill(..., 0.0f)` |

### Threading Model ✅

| Check | Result |
|-------|--------|
| Separate render thread from command loop | ✅ `renderThread_` in AudioRenderer, main thread in Bridge::Run |
| Thread-safe MIDI event queue | ✅ Lock-free SPSC ring buffer with `std::atomic` + acquire/release ordering |
| Graceful shutdown: stops render thread before closing handles | ✅ `Shutdown()` or destructors: `~AudioRenderer` → `Stop()` → `join()`, then `~MmfWriter` → `Close()`, then `~IpcClient` → `Close()` |

### C++ Correctness ✅

| Check | Result |
|-------|--------|
| RAII for HANDLE resources | ✅ All three resource classes have destructors calling `Close()` |
| Atomic writePos update | ✅ `InterlockedExchange` (Win32 equivalent of `std::atomic` store) |
| No obvious UB | ✅ No out-of-bounds, null derefs guarded, MMF header validated before use |
| C++20 standard | ✅ `CMakeLists.txt:3` — `CMAKE_CXX_STANDARD 20` |

### CMake + vcpkg ✅

| Check | Result |
|-------|--------|
| `vcpkg.json` has `nlohmann-json` | ✅ |
| `cmake_minimum_required(VERSION 3.20)` | ✅ |
| `find_package(nlohmann_json CONFIG REQUIRED)` | ✅ |
| `target_link_libraries` correct | ✅ `nlohmann_json::nlohmann_json` |
| VST3 SDK documented as TODO | ✅ Comment in CMakeLists.txt + README.md |
| Win32 defines: `UNICODE`, `_UNICODE`, `WIN32_LEAN_AND_MEAN`, `NOMINMAX` | ✅ `CMakeLists.txt:19` |

## Issues Found

### 💡 NOTE 1 (non-blocking) — `Shutdown()` early-return when triggered by `shutdown` command

`HandleCommand("shutdown")` sets `shutdownRequested_ = true` directly (`bridge.cpp:109`). When `Run()` then calls `Shutdown()`, the `shutdownRequested_.exchange(true)` guard returns `true` (already set), so `Shutdown()` returns without calling `renderer_.Stop()`, `mmfWriter_.Close()`, or `ipc_.Close()`. Cleanup happens correctly via member destructors when `Bridge` goes out of scope — no resource leak. However, if someone later adds a signal handler that calls `Shutdown()` expecting it to be the canonical cleanup path, this guard will be a latent bug. **Suggestion:** Have `HandleCommand("shutdown")` break the loop (it does, via `shutdownRequested_`) and let `Shutdown()` be the sole cleanup entry point, or use a separate `shutdownCalled_` flag in `Shutdown()`.

### 💡 NOTE 2 (non-blocking) — Global MMF namespace probe before Local

`mmf_writer.cpp:24` tries `Global\\mmk-vst3-audio-{hostPid}` before the bare name. The managed side creates the MMF with a bare name (Local namespace). The Global probe always fails in normal operation. In a theoretical local-privilege-escalation scenario, a malicious `Global\\` mapping could be created first. **Suggestion:** Remove the Global probe — the host always creates in the Local (session) namespace.

### 💡 NOTE 3 (non-blocking) — `ReadLine` reads one byte at a time

`ipc_client.cpp:58–71` reads from the pipe one `char` at a time via `ReadFile`. This is functionally correct and adequate for the low-frequency command pipe (6 command types, only during load/MIDI events). If pipe throughput ever becomes a concern (high note-rate performance testing), switch to buffered reads. Not an issue for the scaffold.

### 💡 NOTE 4 (non-blocking) — Silent JSON parse failure in `HandleCommand`

`bridge.cpp:54–57` catches all exceptions from `nlohmann::json::parse` and returns silently. For a scaffold this is acceptable. Future hardening should log malformed lines to stderr for diagnostic visibility.

### 💡 NOTE 5 (non-blocking) — PING/PONG heartbeat not implemented

The architecture spec (§3.2) lists PING/PONG heartbeat messages. Neither the bridge nor the approved Phase 2 managed side implements them. This is consistent across both sides and acceptable for the scaffold. Should be added when bridge crash recovery is hardened.

### 💡 NOTE 6 (non-blocking) — No MMF size validation after mapping

`MmfWriter::Open` validates the header (magic, version, frameSize > 0) but does not verify that the mapped region is large enough for `kHeaderSize + frameSize * 2 * sizeof(float)`. The host creates the correct size, so this cannot fail in normal operation. Defensive check recommended when VST3 integration goes production.

## Verdict: ✅ APPROVED

The C++ scaffold is structurally correct, spec-compliant, and byte-compatible with the approved Phase 2 managed side. IPC ownership direction, resource naming, MMF header layout, JSON protocol, threading model, and RAII resource management all pass review. Six non-blocking notes filed for future hardening — none affect correctness of the scaffold or integration with Phase 2. Phase 3 scaffold is approved for merge.
