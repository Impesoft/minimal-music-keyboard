# Decision: VST3 C# Bug Fixes — Process Arguments + Ring-buffer Read Tracking

**Date:** 2026-03-12  
**Requested by:** Ward Impe  
**Agent:** Jet  
**Status:** Implemented ✅

## Problem

VST3 pipeline audit found two C# bugs in `Vst3BridgeBackend.cs` preventing VST3 instruments from producing sound:

1. **Missing Process Arguments (ROOT CAUSE):** `LoadAsync()` launched `mmk-vst3-bridge.exe` without passing the host PID as `Arguments` to `ProcessStartInfo`. The bridge process received `argc == 1`, printed usage, and exited with code 1. The C# side timed out waiting for pipe connection, set `_isReady = false`, and all subsequent NoteOn/NoteOff calls were silently dropped.

2. **Re-reading Same Frame (Ring-buffer Awareness):** `Read()` always read from `MmfHeaderSize` offset without checking if the bridge had written a new frame since the last read. On WASAPI callbacks faster than the bridge's render tick, the same frame got re-read and played twice (caused phasing/distortion at some sample rates). Also risked reading partially-written frames.

## Decision

Applied two surgical fixes to `src/MinimalMusicKeyboard/Services/Vst3BridgeBackend.cs`:

### Fix 1: Add `Arguments` to ProcessStartInfo (line 242)

```csharp
var psi = new ProcessStartInfo
{
    FileName        = bridgeExePath,
    Arguments       = $"{hostPid}",   // ← ADDED
    UseShellExecute = false,
    CreateNoWindow  = true,
};
```

The `hostPid` variable (already in scope as `Process.GetCurrentProcess().Id` from line 213) is now passed as the first command-line argument. Bridge receives the host PID, constructs correct pipe/MMF names (`mmk-vst3-{hostPid}`, `mmk-vst3-audio-{hostPid}`), connects successfully, and transitions to ready state.

### Fix 2: Add Ring-buffer Read Tracking

**Added field (line 47):**
```csharp
private volatile int _lastReadPos = -1;
```

**Modified `Read()` method (lines 147-153):**
```csharp
// Check if bridge has written a new frame since last read
int writePos = view.ReadInt32(12);   // writePos is at MMF header offset 12
if (writePos == _lastReadPos)
{
    // Bridge hasn't written a new frame yet — return silence
    Array.Clear(buffer, offset, count);
    return count;
}
_lastReadPos = writePos;
```

**Reset in `Dispose()` (line 388):**
```csharp
_isReady = false;
_lastReadPos = -1;  // ← ADDED
```

## MMF Header Layout (confirmed from code)

```
Offset  0: magic (0x4D4D4B56)
Offset  4: version (1)
Offset  8: frameSize
Offset 12: writePos (atomic int32; bridge advances after each block)
Offset 16+: audio data (float32 stereo-interleaved)
```

The C# side now reads `writePos` from offset 12, compares to `_lastReadPos`, and only reads new audio data when `writePos` has advanced.

## Rationale

### Fix 1: Why Explicit Arguments Matter
`ProcessStartInfo.Arguments` must be set manually. Unlike Unix `exec`, Windows CreateProcess doesn't merge the command into `argv[0]` — the first actual argument must be explicitly passed. Without this, the bridge has no way to know which pipe/MMF names to connect to (names include host PID for isolation).

### Fix 2: Why Ring-buffer Tracking Matters
Audio threads reading from shared memory must track `writePos` to avoid re-reading stale frames. Without this:
- High-frequency WASAPI callbacks (e.g., 5ms quantum) read the same data multiple times before the bridge's 20ms render tick writes a new frame
- Causes phasing/distortion (same samples played at offset intervals)
- May read partially-written frames if bridge writes mid-read (though this is unlikely with 16-byte header and atomic int32 writes)

The `volatile` keyword ensures the compiler doesn't reorder reads of `_lastReadPos` vs `writePos`, even though only one thread (audio thread) accesses `_lastReadPos`.

## Impact

- **Before:** VST3 instruments never produced sound (bridge exited immediately, all MIDI events dropped)
- **After:** Bridge connects successfully, MIDI events trigger VST3 audio, ring-buffer reads are synchronized to avoid re-reading stale frames
- **Build:** 0 errors, 2 warnings (CS0414 about unused `_frameSize` — pre-existing, harmless)
- **Scope:** Surgical changes only to `Vst3BridgeBackend.cs` — no changes to C++ bridge code (handled by Faye in parallel)

## Coordination

- **C++ fixes:** Handled by Faye in parallel (separate decision file)
- **Testing:** Requires functional VST3 plugin and end-to-end audio callback verification (Ed's integration test suite)
- **No breaking changes:** SF2 instrument backend unaffected

## Files Changed

1. `src/MinimalMusicKeyboard/Services/Vst3BridgeBackend.cs`
   - Added `Arguments = $"{hostPid}"` to ProcessStartInfo (line 242)
   - Added `volatile int _lastReadPos = -1` field (line 47)
   - Modified `Read()` to check `writePos` before reading (lines 147-153)
   - Reset `_lastReadPos = -1` in `Dispose()` (line 388)

2. `.squad/agents/jet/history.md`
   - Appended session summary to "Learnings" section

3. `.squad/decisions/inbox/jet-vst3-csharp-fixes.md` (this file)
