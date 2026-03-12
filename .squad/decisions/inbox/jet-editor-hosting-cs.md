# VST3 Editor Hosting — C# Side Implementation

**Author:** Jet  
**Date:** 2026-03-12  
**Status:** Implemented (awaiting C++ bridge integration)

## Context

Ward requested UI to open VST3 plugin editors. Faye is implementing the C++ bridge side (`openEditor`/`closeEditor` commands). Jet implemented the C# side.

## Design Decisions

### 1. Optional Interface Pattern

Created `IEditorCapable` as a separate interface alongside `IInstrumentBackend` rather than adding editor methods directly to `IInstrumentBackend`:

**Rationale:**
- Not all backends support editors (SoundFontBackend doesn't)
- Optional interface avoids forcing all backends to implement stub methods
- Cleaner separation of concerns
- Easy to check capability: `if (backend is IEditorCapable capable && capable.SupportsEditor)`

**Alternative Considered:** Default interface implementation (C# 8.0+). Rejected because it would still require all backends to be aware of editor methods.

### 2. Synchronous Command + ACK Pattern

Editor commands use the same pattern as `load`: send command via pipe, await ACK response synchronously.

**Rationale:**
- Reuses existing pipe reader/writer infrastructure
- Simple error handling (timeout = failure)
- Editor open/close are user-initiated actions (not performance-critical)
- Matches existing `LoadAsync()` pattern for consistency

**Alternative Considered:** Fire-and-forget (send command, don't wait for ACK). Rejected because we need to know if the editor window actually opened for error reporting to the user.

### 3. Single Active Backend Model

Added `GetActiveBackend()` to `IAudioEngine` rather than per-slot backend access.

**Rationale:**
- Architecture has one active backend at a time (SF2 or VST3)
- Slot concept is MIDI routing (handled by `MidiInstrumentSwitcher`)
- Opening editor for non-active backend would be confusing (user wouldn't hear it)
- Simplifies UI: "Editor" button opens editor for currently playing instrument

### 4. Tray Icon — Keep Emoji

H.NotifyIcon.WinUI 2.x does not support loading .ico files. To use AppIcon.ico would require:
- P/Invoke to `user32.dll::LoadImage`
- Access to `TaskbarIcon`'s internal HWND
- Wrapping Win32 `NOTIFYICONDATA` structure directly

**Decision:** Keep `GeneratedIconSource` with emoji. Added detailed code comment explaining limitation and future improvement path.

**Rationale:**
- Emoji renders acceptably on modern Windows 11
- P/Invoke approach is non-trivial and brittle
- Can revisit if H.NotifyIcon.WinUI adds .ico support in future version
- Unblocks main work (VST3 editor support)

## Open Questions

1. **Multi-window editing:** If user switches instruments while an editor is open, should we auto-close the old editor? Current implementation doesn't — user must manually close.

2. **Editor lifecycle:** Should we track open editors and close them on app shutdown? Current implementation relies on bridge process termination to clean up.

3. **Per-plugin editor support detection:** Should we query the bridge after load to see if `hasEditor() == true` and set `_hasEditor` field? Current implementation assumes all VST3 plugins have editors (returns `_isReady`).

## Integration Notes

**For Faye (C++ bridge):**
- Expected commands: `{"cmd":"openEditor"}`, `{"cmd":"closeEditor"}`
- Expected ACKs: `{"ack":"editor_opened"}`, `{"ack":"editor_closed"}`
- No additional parameters needed
- Editor window should be parented to bridge process or run as top-level window

**For Spike (architecture review):**
- Does the single active backend model align with future multi-instrument plans?
- Should editor state be persisted (e.g., window position)?

## Files Changed

- `src/MinimalMusicKeyboard/Interfaces/IInstrumentBackend.cs` — added `IEditorCapable`
- `src/MinimalMusicKeyboard/Services/Vst3BridgeBackend.cs` — implemented editor commands
- `src/MinimalMusicKeyboard/Interfaces/IAudioEngine.cs` — added `GetActiveBackend()`
- `src/MinimalMusicKeyboard/Services/AudioEngine.cs` — implemented `GetActiveBackend()`
- `src/MinimalMusicKeyboard/Views/SettingsWindow.xaml.cs` — added Editor button
- `src/MinimalMusicKeyboard/Services/TrayIconService.cs` — documented .ico limitation
