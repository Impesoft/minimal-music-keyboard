---
name: "vst3-controller-state-sync"
description: "Keep separate VST3 edit controllers synchronized with component state during load and preset changes"
domain: "audio-plugin-hosting"
confidence: "high"
source: "manual"
---

## Context
Use this when a VST3 plugin uses separate `IComponent` and `IEditController` objects and works in major hosts but misbehaves, opens with wrong defaults, or fails in a minimal host. Many such plugins expect the host to synchronize component state into the controller as part of startup.

## Patterns

### Call `setComponentState()` after controller initialization
For split component/controller plugins, the host should:
1. initialize the component with the host context
2. create and initialize the controller
3. serialize the component state with `component->getState()`
4. rewind the stream
5. pass that stream to `controller->setComponentState()`

This keeps controller-side parameters, UI state, and component state aligned from the start.

### Re-sync after preset loads
If the host loads a `.vstpreset` into the component after startup, repeat the same component→controller sync so the controller/editor reflects the new preset state instead of stale defaults.

### Scope it to separate controllers
Do not do extra controller initialization or state sync work for single-object plugins where the component already implements `IEditController` directly. That pattern already shares one underlying state object.

### Ship the SDK support code you use
If you use `Steinberg::MemoryStream`, make sure the native target links `public.sdk/source/common/memorystream.cpp`; including the header alone is not enough.

## Example
- Host logs: `"controller initialize() succeeded. controller state synchronized from component."`
- After preset load: append a second diagnostic noting the post-preset controller resync result.

## Anti-Patterns
- **Initializing the controller but never calling `setComponentState()`** — leaves split-controller plugins out of sync with the component.
- **Applying presets only to the component** — editor/controller may still show old values.
- **Assuming all VST3 plugins are single-object** — many instruments separate DSP and controller objects.
