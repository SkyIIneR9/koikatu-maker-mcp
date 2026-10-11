# Testing

## Offline checks

Run from the source directory with Python 3.13:

```powershell
python -m unittest discover -s tests -p 'test_*.py' -v
python -m compileall -q bridge_client.py server.py texture_assets.py package_source.py tests
```

Nine transport tests use a local fake plugin to exercise authenticated requests, Unicode responses, rejected tokens, recovery paths, operation IDs on uncertain responses, malformed/truncated/oversized responses, bad or missing endpoints and request limits. No game or MCP SDK is needed. They test the Python client contract, not the real plugin's authentication implementation.

## Build and live checks

Install `requirements-lock.txt` in a venv, build with `build.ps1`, install the plugin while Koikatu is closed, restart, and open a **clothed test character**. Set `KK_GAME_ROOT` or `KK_MAKER_ENDPOINT` if source/runtime paths differ. Avoid concurrent manual edits.

```powershell
# MCP handshake, catalog, status and image content; no character edits.
.\.venv\Scripts\python.exe -X utf8 tests/smoke_live.py
# Shapes, validation, card readback and camera, with restoration.
.\.venv\Scripts\python.exe -X utf8 tests/smoke_live.py --mutate
```

For appearance testing, first query `maker_list_assets` for the hair-front and top-clothing categories. Choose installed, compatible IDs from this game session. Substitute those numbers below:

```powershell
.\.venv\Scripts\python.exe -X utf8 tests/smoke_appearance.py --mutate --hair-id <ID> --top-id <ID>
```

The appearance test saves a baseline, rejects invalid edits, changes hair/clothing/colors, captures a render, saves the result, loads both baseline and edited cards, then restores the baseline and camera in `finally`. Reports are under source `runtime`; captures use the plugin's runtime directory; cards are under the game's `UserData/chara`. If the game exits or restoration fails, recover with the saved `appearance_baseline_*.png`. Tests do not delete cards.

## Verified locally for 0.2.0

On 2026-10-10 both mutation suites passed on original Koikatu / Unity 5.6.2f1, BepInEx 5.4.23.5, KKAPI 1.46.1 and Screencap 21.1.2.1:

- Real MCP stdio initialization and 15 tools.
- Male character: 52 face and 44 body shapes; change, readback and undo.
- Hair asset/colors, top-clothing asset/color channel and recovery cards.
- Card save/readback and baseline/edited-card loading in the visible Maker.
- Exact exposed shape/appearance restoration within the same game session; camera restoration.
- PNG as MCP image content; edited render inspected visually.
- Rejection of invalid shapes/assets/colors/channels, traversal and card overwrite.

This does not establish compatibility with all mods or verify all extended card data. Materials can override rendered colors; female characters, other game editions, additional clothing slots and all hair-length combinations need further coverage.

On 2026-10-11, [GitHub CI passed](https://github.com/SkyIIneR9/koikatu-maker-mcp/actions/runs/38126321198) on Windows with Python 3.13: dependency installation, Python compilation, all eight transport tests and source packaging. CI does not compile the game plugin or run live game tests.

## Design workflow, 0.3.0

```powershell
.\.venv\Scripts\python.exe -X utf8 tests/smoke_design.py --mutate
```

This suite needs a clothed male test character, an occupied and an empty standard accessory slot, a head bone, a top material exposing `_ShadowColor`, ABMX and Material Editor. It queries current IDs rather than assuming mod IDs. Keep mod card-load toggles enabled for the data being tested. It saves a snapshot first; the recovery ID/card path are written immediately to `runtime/design-smoke-recovery.json`. A `finally` block restores the snapshot; if the game closes during the test, reopen it and restore that snapshot manually through the bridge.

On 2026-10-11 this suite passed with ABMX 5.4.0.0 and Material Editor 4.0.2.0, alongside the two existing mutation regression suites:

- 33 real MCP tools; typed cosmetics/name patches with invalid-batch rejection.
- Sideloader GUID/original-slot resolution of a modded hairstyle.
- Request replay returns the original response/recovery path without a second mutation; completed operation status is queryable.
- Accessory position/color editing, creation and removal; outfit switching away and back.
- Eye/skin/eyebrow settings, accessories, ABMX scale and Material Editor color survive a card round trip.
- Three PNG image blocks from multi-angle capture; original camera and preset restored.
- Snapshot restores original cosmetic/hair/clothes/accessory values, camera, the tested ABMX bone and material properties.

The test accounts for the installed translation plugin serializing a native nickname while displaying its translation; other edited identity fields are compared. Timeout recovery during a real engine stall, all shader types, all bone operations, every cosmetic asset type, female characters and additional mod versions remain unverified. Passing suites establish the exercised workflow, not universal compatibility.

## Texture workflow, 0.4.0

Twelve additional offline tests cover PNG checksums, headers, dimensions, truncation,
appended data, animation rejection, size bounds, hash deduplication and damaged
staging files, Windows short paths, parent segments and linked directories.
Total offline suite: 21 tests (platform-specific checks may skip where unsupported).
These inspect structure; Unity is
responsible for verifying actual image decoding before applying a texture.

```powershell
.\.venv\Scripts\python.exe -X utf8 tests/smoke_textures.py --mutate
```

Use a clothed male test character with Material Editor enabled and an unmodified
top `_MainTex` (the test selects a renderer other than an emblem). A generated
cyan/magenta grid is used only as a diagnostic fixture; no character assets are
bundled. Recovery information is saved immediately in
`runtime/texture-smoke-recovery.json`. The test restores its snapshot in `finally`.

On 2026-10-11, the suite passed on the same Koikatu/Material Editor installation:

- 37 MCP tools; PNG staging, deduplication, listing and image export.
- Invalid paths/UV bounds, conflicting reset arguments, unknown properties and
  character-card input rejected without changing exported pixels.
- Texture and UV changes applied with a recovery card; edited render inspected.
- Texture pixels and UV survived save/load while the staged PNG was temporarily
  renamed out of the library. The source file was restored afterward.
- Reset removed the persisted texture/UV overrides, restored the original exported
  pixels and survived another card save/load. Stale renderer IDs were rejected.
- Original snapshot, camera and texture export hash restored afterward.

The core, appearance and design integration regressions also passed, including
the added texture inspection in material comparisons.
This does not verify all shaders, normal-map conventions, color-space round trips,
4K stress cases, or every texture property. Export hashes were compared within this
installation; GPU color-space conversion can differ across environments.
