# VST3 Editor GUI Hosting — Scoping Report

**Author:** Jet (Windows Dev)  
**Date:** 2026-03-11  
**Requested by:** Ward Impe  
**Status:** SCOPING — ready for team review

---

## Background

VST3 instruments optionally expose a native GUI via the `IEditController` interface.
The host obtains an `IPlugView*` by calling `editController->createView("editor")`,
then calls `plugView->attached(hwnd, kPlatformTypeHWND)` passing a Win32 HWND as the
parent. The plugin creates a child Win32 window inside that HWND. The host then calls
`plugView->getSize(ViewRect*)` to size the parent window accordingly.

Our current bridge (`mmk-vst3-bridge`) loads `IComponent` + `IAudioProcessor` only.
`IEditController` is not yet queried — so GUI hosting is entirely greenfield.

---

## A. VST3 Editor Protocol — Option Evaluation

### Option A — Native bridge popup window ✅ RECOMMENDED

The C++ bridge creates a borderless Win32 popup window (`CreateWindowExW` with
`WS_POPUP | WS_VISIBLE`). It calls `IPlugView::attached()` with that window's HWND,
queries `IPlugView::getSize()`, resizes the popup to match, and runs a Win32 message
pump on a dedicated thread. The bridge sends an IPC event back to C# with the editor
window dimensions, and the editor appears as a standalone, resizable top-level window.

| Criterion | Rating | Notes |
|---|---|---|
| Implementation complexity | **Medium** | ~200–300 new lines in C++; small IPC additions; tiny C# delta |
| Visual integration | Adequate | Free-floating window, not embedded in settings — acceptable for a plugin editor |
| Stability | **High** | No WinUI3/HWND interop; bridge already owns VST3 lifetime; message pump fully isolated |
| MIDI/audio impact | None | Editor thread is independent of the audio render thread |

The bridge process already owns the VST3 module lifetime. Keeping the editor window in
the same process avoids cross-process COM marshalling and matches the standard host
model used by every major DAW for out-of-process plugin scanning.

---

### Option B — WinUI3 hosted HWND (SetParent embedding)

The C# host calls `WindowNative.GetWindowHandle(settingsWindow)` (pattern already in
`SettingsWindow.xaml.cs` line 631), passes the HWND to the bridge via IPC, and the
bridge calls `IPlugView::attached()` with it. A XAML placeholder element serves as
the layout anchor; `SetParent` / DWMWA tricks try to keep the plugin window inside
the WinUI3 client area.

| Criterion | Rating | Notes |
|---|---|---|
| Implementation complexity | **Large** | WinUI3 HWND interop is notoriously fragile; airspace problems, Z-order bugs, DPI scaling mismatches, focus stealing all documented in Windows App SDK issues tracker |
| Visual integration | Theoretically best | Plugin renders inside settings window, but in practice visual glitches are common |
| Stability | **Low** | Foreign HWNDs in WinUI3's composition tree are unsupported; risk of crashes on plugin teardown |

**Rejected.** The effort-to-value ratio is unfavourable and the WinUI3 + foreign-HWND
combination has well-known stability hazards that would undermine the "runs for days"
reliability requirement.

---

### Option C — Fully standalone bridge window (no connection to C# UI)

Same as Option A but the bridge window is created with no parent and no IPC feedback.
The editor appears, the user works with it, then closes it directly.

| Criterion | Rating | Notes |
|---|---|---|
| Implementation complexity | Small | Simpler than A — no IPC event needed |
| Visual integration | Poor | C# side has no visibility into editor state; "Open Editor" button can't toggle to "Close" |
| Stability | High | Same as A |

**Rejected in favour of Option A.** The marginal simplicity is outweighed by the loss
of UI state feedback (button can't reflect open/closed state, no way to bring window
to front from settings UI).

---

## B. Current WinUI3 HWND Access

`WindowNative.GetWindowHandle(this)` is already called in `SettingsWindow.xaml.cs`
(lines 631, 643, 655) to initialise `FileOpenPicker` — the pattern is established and
works correctly in our unpackaged WinUI3 app.

**Can we pass this HWND to the bridge as a parent for plugin editors?**  
Technically yes (it's a valid HWND), but as evaluated in Option B, using it as a
`SetParent` target is unstable. Under Option A (recommended) the C# HWND is **not**
passed to the bridge at all — the bridge creates its own Win32 window independently.
The settings-window HWND remains only for its current use (file pickers).

---

## C. Settings UI Change — "Open Editor" Button

When a VST3 plugin is loaded into a slot, the settings row currently shows:

```
[1] ○SF2 ●VST3  [Map] [✕]  <trigger>  MyPlugin.vst3  [Plugin…]
                              [Preset…]
```

The proposed addition is an **"Open Editor"** button appended after `[Plugin…]` in
`Col 6` (or as a seventh column). The button:

1. Is **hidden** when no VST3 plugin path is set for the slot.
2. Shows **"Open Editor"** when the slot's bridge is running but editor is closed.
3. Toggles to **"Close Editor"** (or grays out) when the editor is already open.
4. On click, sends `{"cmd": "openEditor"}` to the slot's `Vst3BridgeBackend` IPC channel.
5. On receipt of `{"event": "editorClosed"}` from the bridge (e.g. user closes plugin
   window), reverts to "Open Editor" state.

No modal blocking — the editor is a modeless window; MIDI and audio continue unaffected.

---

## D. Recommended Approach — Summary

**Option A: Bridge-owned native Win32 popup window.**

The bridge adds:
1. An `IEditController` + `IPlugView` acquisition path in `AudioRenderer` (or a new
   `EditorController` class alongside it).
2. A dedicated Win32 message pump thread that owns the editor HWND lifetime.
3. Two new IPC commands + one IPC event (see Section E).

The C# host adds:
1. An "Open Editor" button in the VST3 settings row.
2. `SendOpenEditor()` / `SendCloseEditor()` on `Vst3BridgeBackend`.
3. Handling for the `editorOpened` / `editorClosed` events to toggle button state.

This keeps the architecture consistent: the bridge process fully owns VST3 state,
the C# host only issues commands and reacts to events.

---

## E. IPC Additions Needed

### New host → bridge commands

```json
{"cmd": "openEditor"}
```
Bridge response (async, bridge → host):
```json
{"event": "editorOpened", "width": 800, "height": 600}
```
On success, bridge has created the Win32 window and called `IPlugView::attached()`.
Width/height are from `IPlugView::getSize()`.

```json
{"cmd": "closeEditor"}
```
Bridge destroys the editor window; calls `IPlugView::removed()` and releases `IPlugView`.
Bridge response (async, bridge → host):
```json
{"event": "editorClosed"}
```

Also handle gracefully: if the user closes the bridge-owned Win32 window directly,
the bridge's message pump detects `WM_DESTROY` and sends `{"event": "editorClosed"}`
unprompted.

### Error case

If the plugin does not implement `IEditController` or `createView("editor")` returns
`nullptr`, bridge sends:
```json
{"event": "editorOpened", "error": "Plugin does not provide a GUI editor."}
```
C# side shows a `ContentDialog` with the error text.

---

## F. C# Side Changes

### `Vst3BridgeBackend` (Services)

| Change | Complexity |
|---|---|
| Add `SendOpenEditorAsync()` — sends `openEditor` command | Small |
| Add `SendCloseEditorAsync()` — sends `closeEditor` command | Small |
| Add `EditorOpened` / `EditorClosed` C# events, raised when bridge sends matching IPC event | Small |
| Handle `editorOpened` / `editorClosed` in the existing IPC response dispatcher | Small |

The existing `RunPipeWriterAsync` / response-reading loop in `Vst3BridgeBackend`
already handles `load_ack` — the new events fit the same pattern.

### `SettingsWindow.xaml.cs`

| Change | Complexity |
|---|---|
| Add "Open Editor" `Button` per VST3 slot (Col 7 in row grid, or second line) | Small |
| Wire `Click` → `_switcher.GetBridgeForSlot(slotIdx).SendOpenEditorAsync()` | Small |
| Subscribe to `EditorOpened` / `EditorClosed` to toggle button content/state | Small |
| Hide button when `SlotInstrument?.Vst3PluginPath` is null/empty | Small |

### `AppLifecycleManager.cs`

No changes required. The bridge lifetime is already managed by `Vst3BridgeBackend`
which is owned by `AudioEngine` which is owned by `AppLifecycleManager`. Editor
state is local to the bridge process; closing the app sends `shutdown` which tears
down the bridge (and thus the editor window) naturally.

---

## G. Bridge Side Changes (C++)

### `AudioRenderer` or new `EditorController` class

| Change | Complexity |
|---|---|
| Query `IEditController` from component (try combined component first, then factory separate class) | Small |
| Call `editController->createView("editor")` → `IPlugView*` | Small |
| Create Win32 popup window with `CreateWindowExW` (no parent, `WS_POPUP \| WS_CAPTION \| WS_SYSMENU`) | Small |
| Call `plugView->attached(hwnd, kPlatformTypeHWND)` and `getSize()` → resize window | Small |
| Spawn `std::thread` for Win32 message pump (`GetMessage` / `TranslateMessage` / `DispatchMessage`) | Small |
| Handle `WM_DESTROY` → send `editorClosed` event via IPC | Small |
| `closeEditor` command handler: call `plugView->removed()`, `DestroyWindow()`, join pump thread | Small |
| `HandleCommand` additions in `bridge.cpp` for `openEditor` / `closeEditor` | Small |

### Threading note

The audio render thread (`AudioRenderer::RenderLoop`) must not be blocked by editor
operations. The editor Win32 message pump runs on its own thread. `IPlugView` methods
(attach, resize, remove) must be called from the **message-pump thread** (some plugins
assert this). Use `PostThreadMessage` or a queue to dispatch from the bridge command
thread to the pump thread.

---

## H. Effort Estimate

| Component | Effort | Notes |
|---|---|---|
| Bridge: IEditController + IPlugView acquisition | Small | ~50 lines |
| Bridge: Win32 window creation + message pump thread | Small | ~80 lines |
| Bridge: openEditor / closeEditor command handlers | Small | ~40 lines |
| Bridge: IPC event emission (editorOpened / editorClosed) | Small | ~20 lines |
| IPC protocol additions | Small | 2 commands + 2 events, JSON |
| C# `Vst3BridgeBackend`: SendOpenEditor / SendCloseEditor + events | Small | ~60 lines |
| C# `SettingsWindow`: "Open Editor" button + state wiring | Small | ~40 lines |
| **Total** | **Medium** | All individual pieces are small; integration is the main risk |

The main integration risk is plugin compatibility: not all VST3 instruments implement
`IEditController` (instruments without GUI are valid per the spec). The error path
(no GUI available) must be handled gracefully.

**No changes to `AppLifecycleManager`, settings persistence, MIDI routing, or the
audio render pipeline are required.**

---

## I. Open Questions for the Team

1. Should the editor window title show the plugin name? (Bridge has access to
   `ClassInfo::name()` — easy to add to `editorOpened` event payload.)
2. Should editor window position persist across sessions? (Optional — bridge could
   send final position in `editorClosed`, host persists to `AppSettings`.)
3. Plugin parameter changes via the editor GUI — do we need to capture them for
   preset saving? (Out of scope for initial implementation; VST3 `IComponentHandler`
   callbacks would be required for that.)
