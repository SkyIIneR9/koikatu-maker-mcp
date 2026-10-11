# Testing

## Offline checks

Run from the source directory with Python 3.13:

```powershell
python -m unittest discover -s tests -p 'test_*.py' -v
python -m compileall -q bridge_client.py server.py tests
```

Eight transport tests use a local fake plugin to exercise authenticated requests, Unicode responses, rejected tokens, recovery paths, malformed/truncated/oversized responses, bad or missing endpoints and request limits. No game or MCP SDK is needed. They test the Python client contract, not the real plugin's authentication implementation.

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
