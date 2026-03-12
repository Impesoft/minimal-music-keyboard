# Phase 2 Code Review: Vst3BridgeBackend

**Date:** 2026-03-11
**Reviewer:** Gren
**Author:** Faye

## Summary

The Vst3BridgeBackend is a solid implementation with clean state machine design, correct silence-fill behavior, and good XML documentation. However, it reverses the approved IPC resource ownership model (host-as-server → bridge-as-server), introduces string allocations on the audio render thread despite documenting otherwise, and has a Dispose() race that prevents the shutdown command from reaching the bridge.

## Issues Found

### 🔴 BLOCKING — IPC Resource Ownership Reversed

**What the spec says (v1.1 §3.2, approved):**
> "The host (`Vst3BridgeBackend`) creates both the `MemoryMappedFile` and the `NamedPipeServerStream`. The bridge process connects to both as a client."
> "The naming scheme `mmk-vst3-audio-{hostPid}` uses the **host's** PID (not the bridge's), so the name is stable across bridge restarts."

**What the implementation does:**
- Line 212: `new NamedPipeClientStream(...)` — host connects as **client**
- Line 211 comment: `"Connect named pipe (bridge is the server)"`
- Line 227: `MemoryMappedFile.OpenExisting(...)` — host opens existing, meaning **bridge creates** the MMF
- Lines 209/213/228: Names use `_bridgeProcess.Id` — the **bridge** PID, not the host PID

**Why this matters:**
The approved ownership model was a deliberate architectural decision (my v1.1 review, finding #4). With host-as-owner:
1. IPC handles survive bridge crashes — host retains valid references
2. Names are stable across bridge restarts (same host PID → same names)
3. New bridge reconnects to existing resources — no re-creation needed

With bridge-as-owner (current implementation):
1. Bridge crash kills pipe server and MMF handles — host must recreate everything
2. Names change on each restart (new bridge PID)
3. Phase 3 bridge must be designed as pipe server + MMF creator, contradicting the spec

**What must be fixed:**
- Host must create `NamedPipeServerStream` (not client) using `Process.GetCurrentProcess().Id` in the name
- Host must create `MemoryMappedFile.CreateNew(...)` with host PID in the name
- Bridge (Phase 3) will connect as `NamedPipeClientStream` and `MemoryMappedFile.OpenExisting`
- Update `LoadAsync()` to create pipe server before spawning bridge, pass pipe/MMF names via bridge command-line args

### 🟡 REQUIRED — String Allocation on Audio Render Thread

**The documented contract (lines 33–34):**
> "They enqueue JSON command strings into an unbounded `Channel<T>` — no allocations, no blocking."

**What the implementation does (lines 303–304, 311–312, 329–330):**
```csharp
_commandChannel.Writer.TryWrite(
    $"{{\"cmd\":\"noteOn\",\"channel\":{channel},\"pitch\":{note},\"velocity\":{velocity}}}");
```

String interpolation allocates a new `string` on every NoteOn, NoteOff, and SetProgram call. These methods are called from the WASAPI audio render thread. The class documentation explicitly promises "no allocations" but every MIDI event allocates.

**Impact:** For typical playing (10–20 note events per 20ms audio callback), this creates ~500–1000 small string allocations per second on the audio thread. Individually insignificant, but over hours of continuous use in a tray-resident app, this creates steady GC pressure on the thread that must meet a hard real-time deadline.

**What must be fixed:**
Change `Channel<string>` to `Channel<BridgeCommand>` where `BridgeCommand` is a small readonly struct:
```csharp
private readonly record struct BridgeCommand(string Cmd, int Channel, int Pitch, int Velocity);
```
The writer task serializes to JSON **off the audio thread**. All string allocation moves to the background writer task. NoteOn/NoteOff/SetProgram become zero-allocation: they write a stack-allocated struct into the channel.

### 🟡 REQUIRED — Dispose() Race Prevents Graceful Shutdown

**Spec (§4.5):**
1. Send SHUTDOWN (best-effort)
2. Wait for bridge to exit gracefully (3s timeout)
3. Force-kill if still running

**Implementation (lines 350–365):**
```csharp
_commandChannel.Writer.TryWrite("{\"cmd\":\"shutdown\"}");  // enqueue
_commandChannel.Writer.TryComplete();                        // complete channel
_writerCts?.Cancel();                                        // cancel writer task!
// ... immediately Kill() ...
```

Three problems:
1. **Cancel preempts drain:** `_writerCts.Cancel()` cancels the `ReadAllAsync(ct)` token in the writer task. If the writer hasn't yet read the shutdown command from the channel, it throws `OperationCanceledException` and the shutdown command is never sent to the bridge.
2. **Kill without graceful wait:** The spec says wait 3s for graceful exit, then force-kill. The implementation calls `Kill()` immediately after cancel — no grace period. The `WaitForExit(1000)` on line 364 waits for the **kill** to complete, not for graceful shutdown.
3. **Net effect:** The bridge almost certainly gets killed without ever receiving the shutdown command.

**What must be fixed:**
```csharp
// 1. Enqueue shutdown + complete channel (already correct)
_commandChannel.Writer.TryWrite("{\"cmd\":\"shutdown\"}");
_commandChannel.Writer.TryComplete();

// 2. Wait for writer task to drain (gives shutdown command a chance)
try { _pipeWriterTask?.Wait(TimeSpan.FromSeconds(1)); }
catch { }

// 3. Give bridge graceful exit time
if (_bridgeProcess is { HasExited: false })
{
    if (!_bridgeProcess.WaitForExit(2000))
        try { _bridgeProcess.Kill(); } catch { }
}

// 4. Cancel CTS (cleanup, writer task already exited)
_writerCts?.Cancel();
```

## Non-Blocking Observations

These don't require fixes but are worth noting for Phase 3:

1. **SetProgram drops bank and channel** (line 329–330): The JSON only sends `program`, ignoring the `bank` and `channel` parameters. VST3 plugins don't use MIDI banks, so dropping `bank` is defensible, but `channel` should be forwarded for multi-channel VST3 instruments. Add a comment documenting the intent, or include `channel` in the JSON.

2. **No Process.Exited monitoring:** Fault detection relies entirely on pipe write failures in the writer task. If no commands are being sent (silent period), a bridge crash goes undetected until the next NoteOn/Off. Phase 3 should add `Process.Exited` event monitoring or a heartbeat mechanism.

3. **Ring buffer read ignores writePos:** `Read()` always reads from `MmfHeaderSize` offset without checking the `writePos` field at offset 12. This is fine for Phase 2 (no bridge writing data), but Phase 3 must add spin-wait synchronization on `writePos` per spec §7.2.

## What's Done Well

- **State machine is clean:** Five states with `lock(_stateLock)` for thread-safe transitions. `TransitionToFaulted()` correctly checks current state and only raises the event once.
- **Silence fill is thorough:** Three separate guard paths in `Read()` — not-ready, null view, and catch-all exception handler. The audio thread never sees an exception.
- **NoteOffAll works when partially disposed:** Checks `_disposed` (not `_isReady`), so it can silence voices during teardown even after `IsReady` is false. Correct per spec.
- **Channel configuration is correct:** `SingleReader = true` and `AllowSynchronousContinuations = false` prevent the audio thread from accidentally running the writer's continuation inline.
- **BridgeFaultedEventArgs is well-designed:** `Reason` + nullable `Exception` with XML docs. Clean and sufficient.
- **Bridge-absent handling is graceful:** Missing exe → log warning → return → `IsReady` stays false. No throw. Exactly per spec.
- **Connection timeout works:** 5-second `CancelAfter` on pipe connect, correctly distinguished from caller cancellation in the catch blocks.

## Verdict: REJECTED

One blocking issue (IPC resource ownership model reversed from approved spec) and two required fixes (audio-thread allocations, Dispose race). The architecture is fundamentally sound but the IPC ownership reversal would propagate incorrect assumptions into Phase 3 bridge design.

Assign fixes to: **Jet**. Faye is locked out per standard review protocol.

## Re-Review After Jet's Fixes (2026-03-11)
**Reviewer:** Gren

### Fix Verification
- Fix 1 (IPC ownership): ✅ — Host is now `NamedPipeServerStream` (line 218), MMF created with `MemoryMappedFile.CreateNew()` (line 226), all resource names use `Process.GetCurrentProcess().Id` (host PID, lines 213–215). Flow is correct: create server + MMF → launch bridge → `WaitForConnectionAsync` → send load → await ack. Matches spec §3.2 exactly.
- Fix 2 (audio thread allocs): ✅ — Channel is now `Channel<MidiCommand>` where `MidiCommand` is a `readonly struct` (lines 59–72). NoteOn, NoteOff, NoteOffAll, and SetProgram all write the struct to the channel with zero string allocation (lines 319–367). `SerializeCommand()` is called only from `RunPipeWriterAsync` (line 457) — the background drain task — so all string allocation happens off the audio thread.
- Fix 3 (Dispose race): ✅ — Dispose enqueues shutdown + calls `Writer.Complete()` (lines 387–388), then waits for the drain task to finish via `_pipeWriterTask?.Wait(500ms)` (line 391). `Writer.Complete()` causes `ReadAllAsync` to drain remaining items (including shutdown) and exit naturally — no premature CTS cancellation. Bridge then gets 2s graceful exit before force-kill (lines 395–402). CTS cancel is cleanup only, called after the task has exited (line 405). The spec's shutdown contract (send SHUTDOWN → wait for graceful exit → force-kill) is now honoured.

### Build Warnings
Two warnings for unused field `_frameSize` (line 89, written at line 235, never read). **Acceptable — not a required fix.** This field is a clear Phase 3 placeholder: when `Read()` adds spin-wait synchronization on the ring buffer's `writePos` per spec §7.2, it will need the frame size to know how many samples to copy. Removing it now and re-adding in Phase 3 is churn. Recommendation for Phase 3: either use the field or suppress the warning with a pragma.

### New Issues Introduced
None. The `MidiCommand` struct contains `string?` fields (`Path`, `Preset`) which are reference types, but these are only set for `Load` (called from `LoadAsync`, not the audio thread) and default to `null` for all audio-thread commands — no allocation concern. `SerializeCommand` string interpolation is confined to the background drain task. Dispose resource ordering (writers → streams → handles) is correct.

### Final Verdict: APPROVED

Phase 2 is complete. Phase 3 may begin.
