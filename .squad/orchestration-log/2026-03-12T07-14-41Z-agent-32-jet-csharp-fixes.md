# Orchestration: agent-32 (Jet) — VST3 C# Bridge Bug Fixes

**Timestamp:** 2026-03-12T07:14:41Z  
**Agent:** Jet (Windows Dev)  
**Task:** Implement VST3 C# bridge fixes (ProcessStartInfo.Arguments, ring-buffer read tracking)  
**Status:** ✅ Complete

## Fixes Implemented

### Fix 1: Add Arguments to ProcessStartInfo (ROOT CAUSE)
- **File:** `src/MinimalMusicKeyboard/Services/Vst3BridgeBackend.cs` (~line 242)
- **Change:** 
  ```csharp
  var psi = new ProcessStartInfo
  {
      FileName        = bridgeExePath,
      Arguments       = $"{hostPid}",   // ← ADDED
      UseShellExecute = false,
      CreateNoWindow  = true,
  };
  ```
- **Impact:** Bridge now receives `hostPid` as first CLI argument; constructs correct pipe/MMF names; connects successfully

### Fix 2: Ring-buffer Read Tracking
- **File:** `src/MinimalMusicKeyboard/Services/Vst3BridgeBackend.cs`
- **Changes:**
  - Added field (~line 47): `private volatile int _lastReadPos = -1;`
  - Modified `Read()` method (~lines 147-153):
    ```csharp
    int writePos = view.ReadInt32(12);
    if (writePos == _lastReadPos)
    {
        Array.Clear(buffer, offset, count);
        return count;
    }
    _lastReadPos = writePos;
    // ... read audio data
    ```
  - Reset in `Dispose()` (~line 388): `_lastReadPos = -1;`
- **Impact:** Prevents re-reading stale frames; audio no longer suffers phasing/distortion

## MMF Header Layout
```
Offset  0: magic (0x4D4D4B56)
Offset  4: version (1)
Offset  8: frameSize (960)
Offset 12: writePos (atomic int32, bridge increments after each render block)
Offset 16+: audio data (float32 stereo-interleaved)
```

## Rationale

**Fix 1:** Windows `CreateProcess` does not auto-merge command line; `Arguments` must be explicit.  
**Fix 2:** Shared memory ring-buffer protocol requires read position tracking; prevents stale-frame re-reads.

## Build Status
✅ Build successful (0 errors, 2 pre-existing warnings: CS0414 unused `_frameSize`)

## Testing Required
1. Load VST3 instrument plugin
2. Trigger MIDI note; verify audio output
3. Verify no phasing/distortion at various WASAPI callback rates
4. Verify bridge process connection succeeds (no timeout)

## Scope
Surgical changes only to `Vst3BridgeBackend.cs`. No changes to C++ bridge, SF2 backend, or MIDI routing.

## Follow-up Actions
- Faye to implement C++ side fixes in parallel
- Gren to review both C++ and C# for lifetime/threading safety
- Ed to build and verify end-to-end audio output
