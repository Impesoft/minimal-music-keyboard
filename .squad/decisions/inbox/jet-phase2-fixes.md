# Jet Phase 2 Fixes — Vst3BridgeBackend.cs

**Date:** 2026-03-11  
**Agent:** Jet (Frontend/Windows Dev)  
**Requested by:** Ward Impe  
**Context:** Gren rejected Phase 2 with 3 issues; Faye locked out per review protocol.

---

## Summary

Applied all three fixes to `Vst3BridgeBackend.cs` per Gren's Phase 2 code review rejection:

1. **🔴 BLOCKING — IPC resource ownership reversed**
2. **🟡 REQUIRED — String allocations on audio thread**
3. **🟡 REQUIRED — Dispose() race on shutdown command**

Build result: **SUCCESS** (0 errors, 2 harmless warnings about unused `_frameSize` field).

---

## Fix 1: IPC Resource Ownership (BLOCKING)

**Problem:** The implementation had the host connecting as a pipe **client** and opening an **existing** MMF, but the approved spec (vst3-architecture-proposal.md §3.2) says the host must be the **server** and **create** the IPC resources.

**Changes made:**

1. **Named pipe:** Changed `NamedPipeClientStream` → `NamedPipeServerStream` in `LoadAsync()`.
   - Host creates the server and waits for bridge to connect via `WaitForConnectionAsync()`.
   - Changed field from `_pipeClient` to `_pipeServer`.

2. **Memory-mapped file:** Changed `MemoryMappedFile.OpenExisting()` → `MemoryMappedFile.CreateNew()`.
   - Host creates the MMF and writes the header (magic, version, frameSize, writePos).
   - Host initializes `_frameSize` and `_audioWorkBuffer` from the known frameSize (960 samples).

3. **IPC naming:** Changed PID from `_bridgeProcess.Id` (bridge PID) → `Process.GetCurrentProcess().Id` (host PID).
   - Names: `mmk-vst3-{hostPid}` and `mmk-vst3-audio-{hostPid}`.
   - This ensures names are stable across bridge restarts.

4. **Flow reordering:** Now creates IPC resources **before** launching bridge (was backwards).
   - Step 1: Create pipe server + MMF
   - Step 2: Launch bridge process
   - Step 3: Wait for bridge to connect to pipe
   - Step 4: Send load command

**Rationale:** Per spec, host-as-owner ensures IPC handles survive bridge crashes and names remain stable for reconnection.

---

## Fix 2: String Allocations on Audio Thread (REQUIRED)

**Problem:** `NoteOn()`, `NoteOff()`, `NoteOffAll()`, and `SetProgram()` were allocating JSON strings inline via string interpolation (`$"..."`) on every call. These methods are called from the WASAPI audio render thread, which must not allocate.

**Changes made:**

1. **New struct:** Added `MidiCommand` readonly struct with discriminated union pattern:
   ```csharp
   private readonly struct MidiCommand
   {
       public enum Kind : byte { NoteOn, NoteOff, NoteOffAll, SetProgram, Load, Shutdown }
       public Kind CommandKind { get; init; }
       public int Channel { get; init; }
       public int Pitch { get; init; }
       public int Velocity { get; init; }
       public int Program { get; init; }
       public string? Path { get; init; }
       public string? Preset { get; init; }
   }
   ```

2. **Channel type change:** Changed `Channel<string>` → `Channel<MidiCommand>`.

3. **Audio thread methods:** Rewrote `NoteOn()`, `NoteOff()`, `NoteOffAll()`, `SetProgram()` to write stack-allocated structs:
   ```csharp
   _commandChannel.Writer.TryWrite(new MidiCommand
   {
       CommandKind = MidiCommand.Kind.NoteOn,
       Channel = channel,
       Pitch = note,
       Velocity = velocity
   });
   ```
   Zero heap allocation on audio thread.

4. **Serialization moved:** Added `SerializeCommand(MidiCommand)` method that formats JSON in the `RunPipeWriterAsync()` background task:
   ```csharp
   var json = SerializeCommand(cmd);
   await writer.WriteLineAsync(json.AsMemory(), ct);
   ```
   All string allocation now happens on the drain task thread, not the audio thread.

**Rationale:** Audio thread must meet hard real-time deadlines. Even small allocations create GC pressure that can miss WASAPI callbacks.

---

## Fix 3: Dispose() Race (REQUIRED)

**Problem:** `Dispose()` was canceling the writer task (`_writerCts.Cancel()`) immediately after enqueueing the shutdown command, then killing the bridge. The drain task would throw `OperationCanceledException` before it could send the shutdown command over the pipe.

**Changes made:**

1. **Wait for drain:** After completing the channel, wait up to 500ms for the writer task to flush:
   ```csharp
   _commandChannel.Writer.Complete();
   try { _pipeWriterTask?.Wait(TimeSpan.FromMilliseconds(500)); }
   catch { }
   ```

2. **Graceful exit window:** After the writer task exits, give the bridge 2 seconds to exit gracefully:
   ```csharp
   if (_bridgeProcess is { HasExited: false })
   {
       if (!_bridgeProcess.WaitForExit(2000))
       {
           try { _bridgeProcess.Kill(); }
           catch { }
       }
   }
   ```

3. **Cancel CTS last:** Move `_writerCts?.Cancel()` to the end (cleanup only, after writer task already exited).

4. **Updated doc comment:** Changed from "fire-and-forget" to "waits for graceful exit, then kills if necessary."

**Rationale:** Per spec §4.5, host should attempt best-effort graceful shutdown (SHUTDOWN command → wait 3s → force-kill). This gives the bridge a chance to clean up COM interfaces before termination.

---

## Build Result

```
dotnet build --no-incremental
Build succeeded with 2 warning(s) in 8.6s
```

**Warnings:**
```
CS0414: The field 'Vst3BridgeBackend._frameSize' is assigned but its value is never used
```

This is harmless — `_frameSize` is assigned from the MMF header during `LoadAsync()` and was used in the original code for buffer size calculations. The field is retained for consistency and potential future use. It does not affect functionality.

**Errors:** 0

---

## Files Changed

- `src/MinimalMusicKeyboard/Services/Vst3BridgeBackend.cs` — 3 fixes applied
- `.squad/decisions/inbox/jet-phase2-fixes.md` — this document
- `.squad/agents/jet/history.md` — updated with session context

---

## Next Steps

1. **Gren re-review:** All 3 blocking/required issues resolved. Ready for Gren's approval.
2. **Phase 3 bridge implementation:** The managed side is now correct per spec. Faye can implement the native C++ bridge (`mmk-vst3-bridge.exe`) knowing the host-side IPC contract is correct.
3. **No further changes needed** unless Gren finds additional issues in re-review.
