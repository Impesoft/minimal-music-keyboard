# Gren Review: VST3 Bridge Fixes

**Date:** 2026-03-12
**Reviewer:** Gren (Supporting Architect)
**Status:** ⚠️ APPROVED WITH CONDITIONS

## Verdict
**SAFE TO COMMIT ONLY AFTER FIXING BLOCKERS.**
The changes enable audio output but introduce two critical lifecycle defects in the C++ bridge that will cause crashes. The C# thread safety issue is noted but accepted for MVP.

## Blocking Issues (Must Fix Before Commit)

### 1. C++ `HostApplication` Lifetime (Crash Risk)
**Severity:** High (Dangling Pointer)
**Location:** `src/mmk-vst3-bridge/src/audio_renderer.cpp`, `Load()` method
**Problem:** The `HostApplication` object is created as a local `IPtr` in `Load()`. It is passed to `component_->initialize(hostApp)`. Unless the plugin calls `addRef()`, the `HostApplication` is destroyed when `Load()` returns.
**Risk:** Many VST3 plugins store the `IHostApplication*` raw pointer without `addRef()` (violating spec, but common). Accessing this pointer later (e.g. during `terminate()` or `process()`) will cause the bridge process to crash.
**Fix:** Add `Steinberg::IPtr<HostApplication> hostApp_;` as a private member of `AudioRenderer`. Store the created instance in this member in `Load()` and release it in `Unload()` (or let destructor handle it).

### 2. C++ `IEditController` Teardown (Crash Risk)
**Severity:** Medium (Use-After-Free)
**Location:** `src/mmk-vst3-bridge/src/audio_renderer.cpp`, `ResetPluginState()`
**Problem:** The controller and component are `terminate()`ed without first disconnecting their `IConnectionPoint` connection.
**Risk:** If `component_` tries to send a message to `controller_` during its termination sequence (or vice versa), and the other side is already partially destroyed or dead, it may crash.
**Fix:** Store the `IConnectionPoint` pointers (or query them in `ResetPluginState`) and call `disconnect()` on both sides *before* calling `terminate()`.

## Non-Blocking Notes (Fix in Phase 2)

### 3. C# `_lastReadPos` Thread Safety (Audio Glitches)
**Severity:** Low (Artifacts)
**Location:** `src/MinimalMusicKeyboard/Services/Vst3BridgeBackend.cs`, `Read()`
**Problem:** There is a TOCTOU (Time-Of-Check to Time-Of-Use) race between reading `writePos` and reading the audio buffer. If the bridge updates the buffer *while* `Read()` is copying it, the audio will be "torn" (part old frame, part new frame). `volatile` on `_lastReadPos` does not prevent this; it only ensures visibility of the *local* variable.
**Mitigation:** Acceptable for MVP as the race window is small (copy time vs 20ms frame).
**Future Fix:** Implement double-buffering in the MMF or a Seqlock pattern.

## Approval Logic
The architectural changes (MIDI bytes, sample rate, buffer size) are correct. The C# logic is functionally better than before (avoids stale reads). The blockers are specific C++ lifecycle bugs that are easy to fix but fatal if ignored.
