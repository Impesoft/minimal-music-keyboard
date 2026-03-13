# Faye: Bridge load ACK failure hardening

**Date:** 2026-03-13  
**Status:** Implemented  
**Scope:** Native VST3 bridge + managed backend diagnostics

## Decision

When the VST3 bridge fails during a `load` command, we now harden both sides of the contract:

1. **Native bridge (`bridge.cpp`)** catches command-local exceptions around the `load` path and returns `ack:"load_ack", ok:false` with the failure text instead of silently dropping the request.
2. **Managed backend (`Vst3BridgeBackend.cs`)** redirects bridge stdout/stderr and reports buffered native diagnostics plus bridge exit code whenever the expected load ACK never arrives.

## Why

OB-Xd was surfacing as:

`Failed to start or connect to bridge: Bridge rejected load command: <no response>`

That message made it impossible to distinguish between:
- a normal native load error,
- an unhandled native exception,
- or the bridge process crashing before it could answer.

The bridge protocol now guarantees a deterministic human-readable failure path even when the plugin crashes the helper before a normal ACK can be serialized.

## Consequences

- Expected plugin load failures now come back as regular `load_ack` errors.
- Unexpected bridge exits now surface as concrete diagnostics instead of `<no response>`.
- Future VST3 triage can start from the reported stderr/exit code without first reproducing under a debugger.
