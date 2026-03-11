# Decisions Log

## Decision: Gren's VST3 Architecture Proposal Verdict

**Author:** Gren (Supporting Architect)  
**Date:** 2026-07-17  
**Scope:** Review of `docs/vst3-architecture-proposal.md` v1.0 (Spike)  
**Status:** Approved with conditions

### Verdict

**APPROVED WITH CONDITIONS**

### Summary

The VST3 architecture proposal is fundamentally sound — out-of-process bridge is the correct hosting approach, the `IInstrumentBackend` abstraction is clean, and the phased delivery plan is pragmatic. Two BLOCKING issues must be resolved before implementation begins:

1. **Threading model contradiction** — §4.2 code sketch calls `backend.NoteOn()` directly from the MIDI thread, violating the ConcurrentQueue pattern established in Faye's Decision 1 and described in §4.3. The proposal must pick one model and be consistent.
2. **Missing Dispose() specifications** — Neither the refactored `AudioEngine` nor `Vst3BridgeBackend` have disposal sequence sketches. For a project whose core constraint is "run for days without leaking," this is a blocking omission.

Four additional REQUIRED issues (MixingSampleProvider swap semantics, bridge crash behavior, SoundFontBackend code sketch, IPC resource ownership) must be addressed in a proposal revision but do not block the design direction.

### Full Review

See `docs/vst3-architecture-review.md` for the complete 10-issue review with severity ratings, per-section verdicts, and implementation prerequisites.

### Conditions for Implementation

- Spike must revise the proposal to address all BLOCKING and REQUIRED issues
- Gren will re-review the revised sections (not the full proposal) before Phase 1 begins
- Phase 1 (SoundFontBackend extraction) must pass all existing AudioEngine tests unchanged
