# VST3 GUI Hosting Thread Model

**Date:** 2026-03-19  
**Author:** Faye (Audio Dev)  
**Status:** Implemented

## Context

VST3 plugins often provide their own graphical editor window via `IEditController::createView()`. The editor window requires a Win32 message loop to process mouse/keyboard input and paint messages. The bridge process already runs a blocking IPC read loop on its main thread.

## Decision

VST3 editor GUI hosting is implemented in the native C++ bridge process using a **separate dedicated thread** for the Win32 message loop. The main IPC command loop remains responsive while the plugin GUI is open.

### Implementation Details

1. **OpenEditor() workflow:**
   - Query `IEditController::createView(ViewType::kEditor)`
   - Validate platform support (`isPlatformTypeSupported(kPlatformTypeHWND)`)
   - Create a Win32 window (`WS_OVERLAPPEDWINDOW | WS_VISIBLE`)
   - Attach the plugin view via `IPlugView::attached(hwnd, kPlatformTypeHWND)`
   - Spawn a thread running `EditorMessageLoop()` to process Win32 messages

2. **CloseEditor() workflow:**
   - Set `editorOpen_` flag to false
   - Post `WM_QUIT` to wake the message loop
   - Join the editor thread
   - Call `IPlugView::removed()`
   - Destroy the window and release COM pointers

3. **Thread safety:**
   - `editorOpen_` is `std::atomic<bool>` checked by the message loop
   - `editorThread_` is joined before releasing any editor resources
   - `CloseEditor()` is idempotent and safe to call multiple times

4. **Lifecycle integration:**
   - `CloseEditor()` is called at the start of `ResetPluginState()` to ensure clean teardown before plugin termination
   - Bridge `openEditor` / `closeEditor` commands return JSON ack messages with `ok` status

### Alternative Considered

**Rejected:** Running the GUI on the main thread and polling the message queue between IPC commands. This approach would introduce unpredictable latency in IPC command processing and could cause the GUI to become unresponsive if commands take time to execute.

## Rationale

- **Responsiveness:** The IPC command loop must never block waiting for GUI input. Plugin editors can take seconds to initialize or respond to user input.
- **Simplicity:** Dedicated thread model is simpler than integrating `PeekMessage()` polling into the bridge's event loop.
- **Compatibility:** Matches standard VST3 hosting patterns — most DAWs run plugin GUIs on separate threads.

## Consequences

- **Positive:** Bridge remains responsive during GUI operations; users can close the editor or switch instruments without waiting for GUI teardown.
- **Positive:** Thread-safe shutdown sequence prevents resource leaks and crashes.
- **Negative:** Slightly increased memory overhead (one thread per open editor). Acceptable since only one plugin is loaded at a time in this application.

## Related

- VST3 SDK documentation: `IPlugView` interface lifecycle
- Bridge IPC protocol: `openEditor` / `closeEditor` commands
- Bug fix: Bus activation (`activateBus()` calls) must precede `setActive(true)` — this was the root cause of audio silence before GUI hosting was implemented
