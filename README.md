# Koikatu Character Maker MCP — 0.2.0

[Русская документация](README.ru.md)

A local MCP bridge for **Koikatu's Character Maker**. An assistant can edit characters, choose installed hair and clothing, inspect renders and save/load cards. This experimental Maker plugin is separate from Studio MCP projects.

See the [roadmap](ROADMAP.md) for proposed next steps and [testing notes](TESTING.md) for the verified scope.

## Features

- Read face/body sliders by game enum name; apply validated batches and undo shape edits.
- Select four hair slots; adjust RGB/RGBA colors, outline and length.
- Select clothing and color channels in the active outfit coordinate.
- Search installed asset catalogs, including mod assets.
- Read/control the Maker camera and return PNG images as MCP image content.
- Save new cards without overwriting; read back and compare exposed values.
- Find cards and load a same-sex card into the visible Maker.
- Save recovery cards before hair/clothing edits and card loads.

## Requirements

Windows, Python 3.13, original `Koikatu.exe`, BepInEx 5, KKAPI and BepisPlugins Screencap. Build references come from your game installation; no game binaries or assets are included.

Tested with Unity 5.6.2f1, BepInEx 5.4.23.5, KKAPI 1.46.1 and Screencap 21.1.2.1, using a male test character. Female characters and other plugin combinations have not been separately verified. Party and Sunshine are untested; the plugin explicitly targets `Koikatu.exe`. Studio and Unity Editor are outside its scope.

## Install from source

Close Koikatu, extract the source and run PowerShell in its folder:

```powershell
.\setup.ps1 -GameRoot 'D:\Games\Koikatu' -Install
```

Replace the example path. The script creates `.venv`, installs pinned Python dependencies, builds the plugin and installs two DLLs under `BepInEx/plugins/KKMakerBridge`. It prints an MCP configuration with the correct paths. Without `-Install`, it builds without copying DLLs into the game.

The build uses the Windows .NET Framework compiler; no separate .NET SDK is needed. It expects `BepInEx/plugins/KKAPI.dll`, `BepInEx/plugins/KK_BepisPlugins/Screencap.dll` and `BepInEx/core/Mono.Cecil.dll`. Adjust `build.ps1` if your mod installation uses other paths.

On first build, it downloads **Json.Net.Unity3D 9.0.1** from NuGet and checks SHA-256. The library gets a private assembly identity, `KKMakerBridge.Json`, to avoid mod conflicts. Ordinary Newtonsoft.Json net35 uses framework types missing from this Unity runtime.

Start Koikatu, enter Character Maker and wait until the character loads. Restart the game whenever the plugin DLL changes.

## Connect an MCP client

Use the configuration printed by `setup.ps1`, or adapt this example to your MCP host:

```json
{
  "mcpServers": {
    "koikatu_maker": {
      "command": "D:\\Tools\\MakerBridge\\.venv\\Scripts\\python.exe",
      "args": ["D:\\Tools\\MakerBridge\\server.py"],
      "env": {"KK_GAME_ROOT": "D:\\Games\\Koikatu"}
    }
  }
}
```

`KK_GAME_ROOT` locates `<game>/MCP/MakerBridge/runtime/endpoint.json`, the plugin default. Without it the client looks in `runtime` beside `server.py`. If you change RuntimeDirectory in `BepInEx/config/local.koikatu.makerbridge.cfg`, set `KK_MAKER_ENDPOINT` to its absolute `endpoint.json` path. This override takes precedence. Reconnect your MCP host after configuration changes.

## Tools

| Tool | Purpose |
|---|---|
| `maker_status` | Connection, version and Maker readiness |
| `maker_get_character` | Name, sex and face/body slider names/values |
| `maker_set_shapes` | Validated face/body batch |
| `maker_undo_shapes` | Restore previous face/body arrays |
| `maker_get_appearance` | Hair/clothes slots, categories, IDs and colors |
| `maker_set_hair` | Select/recolor hair and change length |
| `maker_set_clothes` | Select/recolor active-coordinate clothes |
| `maker_list_assets` | Search installed resources by category |
| `maker_get_camera` / `maker_set_camera` | Read/change the orbit camera |
| `maker_capture` | PNG, 256–2048 pixels per side |
| `maker_save_card` | Save a new card and verify exposed values |
| `maker_verify_card` | Read/compare a card without replacing the character |
| `maker_list_cards` | Search card filenames with pagination |
| `maker_load_card` | Load a same-sex card after backing up the character |

Read status, character and appearance first. Save a baseline card before a creative session. Use returned names/categories/slots and query asset IDs from the current catalog. **Sideloader mod IDs can change at every game launch.** Query again instead of reusing IDs from a previous session. Catalog names may be Japanese; raw `sex` metadata is not uniform across categories.

Shape values and color components are normalized: `0.5` means 50 in the standard UI. New shape values and hair length are limited to `0..1`; existing out-of-range shapes remain readable and are preserved by undo. Colors accept RGB or RGBA. Clothing `colors` maps channel strings to colors, e.g. `{"0": [0.2, 0.4, 0.3, 1]}`. Capture after editing: materials and lighting affect the rendered color.

Shape undo replaces both entire arrays, including manual edits made since the bridge edit. History clears on card load or Maker exit. Save camera values separately. Avoid manual editing while a bridge operation runs.

## Recovery and limitations

New cards go to `UserData/chara/male/MCP` or `female/MCP`; recovery cards go in `_backups` beneath that folder. Existing cards are never overwritten. Load accepts absolute paths or paths relative to `UserData/chara`, including recovery cards. Traversal and linked card paths are rejected. Card listing omits `_backups`.

Hair/clothing edits and loads back up before changing the character. If an operation fails after starting, inspect its recovery path and current state, then load that card if needed. A timeout does not prove failure: do not retry mutations blindly. Screenshots, test cards and backups are not automatically deleted.

Save/load uses game APIs and installed mod hooks. Full base-game load flags are enabled temporarily, but **third-party custom load toggles remain in effect**. Verification compares sex, name, vanilla shapes, exposed hair values and active-coordinate clothes. It does not verify every extended mod field or pixel-identical rendering. Direct ABMX, Material Editor, overlay and accessory authoring are not implemented. Clothing edits affect the active coordinate only; there is no coordinate-selection tool yet.

## Development

```powershell
$env:KK_GAME_ROOT = 'D:\Games\Koikatu'
.\.venv\Scripts\python.exe bridge_client.py status
.\.venv\Scripts\python.exe -m unittest discover -s tests -p 'test_*.py' -v
.\build.ps1 -GameRoot $env:KK_GAME_ROOT
.\.venv\Scripts\python.exe package_source.py
```

The direct client supports `--params-file request.json`. See [TESTING.md](TESTING.md) for live mutation tests and restoration. GitHub CI runs Python checks; compiling/testing the plugin requires a local game installation. The source archive uses an explicit allowlist and excludes runtime data and binaries.

## Architecture and local access

MCP stdio → Python → authenticated localhost TCP → Unity main-thread queue → MakerAPI/ChaControl. The default is `127.0.0.1:8766`, with configurable port and a random token per game launch. The endpoint file contains that token: keep it private. Local processes that can read it can control the bridge. The plugin lets the game continue running in the background.

## Uninstall

Close Koikatu, remove `BepInEx/plugins/KKMakerBridge`, and remove the server from your MCP host. Cards, source, configuration and captures remain until you remove them.

## License and dependencies

Bridge source: [MIT](LICENSE). Dependencies retain their own licenses; see [THIRD_PARTY_JSON_LICENSE.txt](THIRD_PARTY_JSON_LICENSE.txt). This project is not affiliated with the game developer.

- [IllusionModdingAPI / MakerAPI](https://github.com/IllusionMods/IllusionModdingAPI)
- [BepisPlugins / Screencap](https://github.com/IllusionMods/BepisPlugins)
- [Json.Net.Unity3D](https://github.com/SaladLab/Json.Net.Unity3D)
- [Python MCP SDK](https://github.com/modelcontextprotocol/python-sdk)
