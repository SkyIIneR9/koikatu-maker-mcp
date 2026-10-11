# Changelog

## 0.3.0 — 2026-10-11

- Expand to 33 tools: eyes/eyebrows/skin/makeup/identity, accessories and outfit selection.
- Add optional ABMX and Material Editor adapters with validated inputs and backup cards.
- Resolve stable Sideloader GUID/original-slot references to current runtime IDs.
- Add persistent snapshots, camera presets and multi-angle image capture.
- Track requests in a bounded journal; report operation IDs and prevent duplicate execution while retained.
- Verify exposed cosmetic settings on card readback and preserve snapshot display names across translation hooks.
- Add opt-in design workflow integration tests.

## 0.2.0 — 2026-10-10

- Add appearance inspection, hair selection/colors/length and active-coordinate clothes selection/colors.
- Add card discovery and same-sex loading through the game's Maker APIs.
- Save recovery cards before appearance edits and loads; include recovery paths in mutation errors.
- Flush the active outfit before saving and verify exposed hair/clothes values on readback.
- Handle nested game coroutines and Koikatu's legacy `LoadFileLimited` false return correctly.
- Temporarily align base-game card-load flags with full loads; preserve third-party custom toggles.
- Serialize Python RPC calls, validate transport envelopes and handle Unicode CLI output.
- Add live appearance tests, offline transport tests, setup script, bilingual documentation and a source-only packager.

## 0.1.0 — 2026-10-10

- Initial bridge: shape read/edit/undo, camera, screenshots, card save/verification and asset discovery.
- BepInEx main-thread dispatcher, loopback token authentication and MCP stdio adapter.
- Private Unity-compatible JSON assembly for the legacy runtime.
