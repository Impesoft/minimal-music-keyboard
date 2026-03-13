# Session Log: VST3 Bridge Load Failure Hardening

**Date:** 2026-03-13  
**Topic:** VST3 Bridge Load Failure Hardening  
**Primary Agent:** Faye (Audio Dev)

## Summary

Fixed the VST3 bridge to deterministically report load failures instead of timing out with `<no response>`. The problem manifested as OB-Xd and similar plugins appearing "unloadable" when the bridge encountered native exceptions during plugin initialization.

## Problem Statement

When the native VST3 bridge encountered an exception during plugin loading (e.g., OB-Xd missing dependencies or init errors), it would drop the command silently. The managed backend would wait indefinitely for an ACK that never came, eventually timing out with:

```
Failed to start or connect to bridge: Bridge rejected load command: <no response>
```

This made it impossible to diagnose whether the issue was:
- A normal plugin load failure (missing DLLs, init error, etc.)
- An unhandled native exception in the bridge
- The bridge process crashing before serializing an ACK

## Solution

**Two-sided hardening** of the bridge load contract:

### Native Bridge (`src/mmk-vst3-bridge/src/bridge.cpp`)

Wrapped the command handler in exception safety:
```cpp
// Old: Unhandled exception → silent pipe drop
// New: Exception → ack:"load_ack", ok:false, error_text

try {
    // renderer_.Load(...) — may throw
} catch (const std::exception& e) {
    json ack;
    ack["ack"] = "load_ack";
    ack["ok"] = false;
    ack["error"] = e.what();
    // Serialize and send
}
```

Parse failures and ACK write failures now emit stderr diagnostics for diagnostics-on-crash scenarios.

### Managed Backend (`src/MinimalMusicKeyboard/Services/Vst3BridgeBackend.cs`)

Added secondary diagnostics channel:
- Redirect bridge stdout/stderr to buffers
- When expected `load_ack` never arrives, report:
  - Bridge process exit code
  - Last N lines of captured stderr
  - Native error message if available

Result: `<no response>` is replaced with concrete diagnostics.

## Files Modified

| File | Changes |
|------|---------|
| `src/mmk-vst3-bridge/src/bridge.cpp` | Exception handling on load path; stderr diagnostics |
| `src/MinimalMusicKeyboard/Services/Vst3BridgeBackend.cs` | Redirect/buffer child-process stdout/stderr; exit code capture |
| `src/mmk-vst3-bridge/CMakeLists.txt` | (if adjusted for debugging) |

## Verification

- ✅ Native bridge Release build (no warnings/errors)
- ✅ Managed app Release build (no warnings/errors)
- ✅ Existing test suite passed

## Patterns & Learnings

**For line-based ACK protocols with native helpers:**
1. Catch and serialize command-local exceptions on the helper side → deterministic error ACK
2. Capture helper exit code and stderr on the host side → second diagnostic channel
3. Never allow a request to timeout with `<no response>` — always report either structured ACK or concrete crash context

**Why this matters:**
- Reduces debug cycles for VST3 plugin issues
- Enables automated monitoring/logging of plugin load failures
- Creates audit trail for VST3 plugin compatibility work
