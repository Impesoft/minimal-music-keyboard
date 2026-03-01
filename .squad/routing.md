# Routing

| Signal | Agent(s) | Why |
|--------|----------|-----|
| Architecture decisions, overall design, tech stack choices | Spike | Lead Architect |
| Architecture review, second opinion, design challenges | Gren | Supporting Architect |
| "review this design" / "check the architecture" | Spike + Gren | Peer review |
| MIDI device I/O, Windows APIs, tray integration, app lifecycle | Jet | Windows Dev |
| Audio synthesis, soundfonts, instrument pipeline, audio routing | Faye | Audio Dev |
| Testing, memory leaks, QA, edge cases, stability | Ed | Tester / QA |
| Session logging, decision merging, git commits | Scribe | Logger |
| Work queue, issue tracking, backlog | Ralph | Monitor |
| Settings UI, WinUI3 pages, XAML | Jet | Windows Dev (UI is part of lifecycle) |
| Multi-domain task (e.g. "build settings page") | Spike + Jet + Faye | Fan-out |
| Memory leak concern | Jet + Ed | Dev + QA together |
| New feature spanning audio + UI | Faye + Jet | Audio + Windows Dev |
