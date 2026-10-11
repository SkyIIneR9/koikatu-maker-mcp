"""MCP stdio adapter. The game-facing API is implemented by KK_MakerBridge.dll."""
from __future__ import annotations

import asyncio
import json
from pathlib import Path

from mcp.server.fastmcp import FastMCP, Image
from bridge_client import call

mcp = FastMCP("Koikatu Character Maker", instructions="Controls the active character in Koikatu Character Maker. Read status, get_character and get_appearance first. Shape values are normalized (0.5 means 50%). Use returned enum names, slots and asset IDs; never guess indices. Save a baseline card before a creative session and capture after edits. Hair/clothes edits and same-sex card loads save an automatic backup_card, which can be restored with load_card. Clothes edits affect the active coordinate only. Use capabilities/get_design for supported settings. ABMX and Material Editor adapters require their installed plugins; custom card-load toggles still apply. Renderer IDs expire on reload. Create a snapshot before a design session. If a mutation times out, query get_operation with the ID from the error before retrying.")
_rpc_lock = asyncio.Lock()


async def rpc(method: str, **params):
    async with _rpc_lock:
        return await asyncio.to_thread(call, method, **params)


@mcp.tool()
async def maker_status() -> dict:
    """Check whether the game bridge and Character Maker are ready."""
    return await rpc("status")


@mcp.tool()
async def maker_get_character() -> dict:
    """Read current name, sex and all vanilla face/body shapes with enum names and indices."""
    return await rpc("get_character")


@mcp.tool()
async def maker_set_shapes(face: dict[str, float] | None = None, body: dict[str, float] | None = None) -> dict:
    """Set a batch of vanilla morphs. Keys are names/indices from get_character; values 0..1. Validates the full batch before applying, with shape undo. Does not save a card."""
    return await rpc("set_shapes", **{k: v for k, v in {"face": face, "body": body}.items() if v is not None})


@mcp.tool()
async def maker_undo_shapes() -> dict:
    """Restore face/body arrays from before the last set_shapes call. Cleared on card load or Maker exit. Also replaces manual shape edits made since that call."""
    return await rpc("undo_shapes")


@mcp.tool()
async def maker_capture(width: int = 1280, height: int = 720) -> list:
    """Return a rendered PNG for visual feedback (256..2048 pixels per side), plus its local path."""
    result = await rpc("capture", width=width, height=height)
    return [json.dumps(result), Image(path=result["path"])]


@mcp.tool()
async def maker_save_card(name: str | None = None) -> dict:
    """Save a NEW card under UserData/chara/male|female/MCP. Never overwrites. Read it back and compare vanilla shapes/name/sex and exposed hair/clothes values; custom plugin data is not verified."""
    return await rpc("save_card", **({"name": name} if name else {}))


@mcp.tool()
async def maker_verify_card(path: str) -> dict:
    """Read a card without replacing the visible character; compare vanilla shapes/name/sex and exposed hair/clothes values. Path is absolute or relative to UserData/chara."""
    return await rpc("verify_card", path=path)


@mcp.tool()
async def maker_get_camera() -> dict:
    """Read Maker orbit camera target, rotation in degrees, direction/offset and field of view."""
    return await rpc("get_camera")


@mcp.tool()
async def maker_set_camera(target: list[float] | None = None, rotation: list[float] | None = None, direction: list[float] | None = None, fov: float | None = None) -> dict:
    """Set orbit-camera values; omit fields to preserve them. Read get_camera first and save those values for restoration. Rotation is [pitch,yaw,roll] in degrees."""
    params = {k: v for k, v in {"target": target, "rotation": rotation, "direction": direction, "fov": fov}.items() if v is not None}
    return await rpc("set_camera", **params)


@mcp.tool()
async def maker_list_assets(category: str | None = None, search: str = "", offset: int = 0, limit: int = 30) -> dict | list:
    """List game asset categories (omit category), or page through one category's installed assets by ID/name. Search uses game names, which may be Japanese. Mod asset IDs can change after a game restart: query again before editing."""
    params = {"search": search, "offset": offset, "limit": limit}
    if category:
        params["category"] = category
    return await rpc("list_assets", **params)


@mcp.tool()
async def maker_get_appearance() -> dict:
    """Read current hair/clothes slot names, IDs, asset categories, RGBA colors and active coordinate. Use these to select valid targets for edits."""
    return await rpc("get_appearance")


@mcp.tool()
async def maker_set_hair(slot: str, id: int | None = None, base_color: list[float] | None = None, start_color: list[float] | None = None, end_color: list[float] | None = None, outline_color: list[float] | None = None, length: float | None = None) -> dict:
    """Select/recolor one hair slot: back/front/side/extension. Use an installed ID from its category. Colors are RGB/RGBA 0..1; length 0..1. Omitted fields stay unchanged. Saves a backup card before applying; waits for game asset loading."""
    params = {k: v for k, v in {"slot": slot, "id": id, "base_color": base_color, "start_color": start_color, "end_color": end_color, "outline_color": outline_color, "length": length}.items() if v is not None}
    return await rpc("set_hair", **params)


@mcp.tool()
async def maker_set_clothes(slot: str, id: int | None = None, colors: dict[str, list[float]] | None = None) -> dict:
    """Select/recolor clothing in the active coordinate. Get slot/category from get_appearance and ID from list_assets. Colors maps channel indices to RGB/RGBA 0..1. Saves a backup card; waits for asset loading. Custom materials can override colors; inspect a capture."""
    params = {k: v for k, v in {"slot": slot, "id": id, "colors": colors}.items() if v is not None}
    return await rpc("set_clothes", **params)


@mcp.tool()
async def maker_list_cards(sex: str | None = None, search: str = "", offset: int = 0, limit: int = 30) -> dict:
    """List PNG card paths under UserData/chara (male or female, default current sex). Search matches filenames/relative paths. Excludes automatic backups and linked directories; returns has_more for pagination."""
    params = {"search": search, "offset": offset, "limit": limit}
    if sex:
        params["sex"] = sex
    return await rpc("list_cards", **params)


@mcp.tool()
async def maker_load_card(path: str) -> dict:
    """Replace the visible Maker character with a same-sex card, after saving the current character as backup_card. Path is absolute or relative to UserData/chara. Loads all base-game sections and runs mod reload hooks. Custom mod load toggles are respected. Shape undo is cleared."""
    return await rpc("load_card", path=path)



@mcp.tool()
async def maker_capabilities() -> dict:
    """Read optional plugin availability, versions, editable design schema and explicit limitations."""
    return await rpc("capabilities")


@mcp.tool()
async def maker_get_design() -> dict:
    """Read face/skin/eyes/makeup/name settings and their typed schema. Asset fields include catalog categories. Colors are RGB/RGBA, numeric values normalized 0..1. left_eye/right_eye refer to the character's sides."""
    return await rpc("get_design")


@mcp.tool()
async def maker_set_design(changes: dict) -> dict:
    """Edit groups/fields returned by get_design; e.g. {"left_eye":{"baseColor":[0.2,0.4,0.6,1]}}. Validates the entire patch and backs up before rebuilding the character. Makeup is base-card makeup; coordinate makeup may override it. Does not modify shape sliders."""
    return await rpc("set_design", changes=changes)


@mcp.tool()
async def maker_get_accessories() -> dict:
    """Read base-game accessory slots 0..19, categories, colors, parent keys and two transform corrections. Extra MoreAccessories slots are not exposed."""
    return await rpc("get_accessories")


@mcp.tool()
async def maker_set_accessory(slot: int, category: str | None = None, id: int | None = None, parent: str | None = None, colors: dict | None = None, transform: int = 0, position: list[float] | None = None, rotation: list[float] | None = None, scale: list[float] | None = None) -> dict:
    """Edit an accessory with backup. Use get_accessories and list_assets first. ao_none removes it. Category changes need a matching ID. Position uses game correction units, rotation degrees, scale multipliers (0.01..10). Changes affect the active coordinate."""
    return await rpc("set_accessory", **{k:v for k,v in locals().items() if v is not None})


@mcp.tool()
async def maker_list_coordinates() -> dict:
    """Read outfit coordinate names, indices and active coordinate."""
    return {"items": await rpc("list_coordinates")}


@mcp.tool()
async def maker_select_coordinate(index: int) -> dict:
    """Back up and flush current outfit, then select a returned coordinate index and rebuild its clothes/accessories."""
    return await rpc("select_coordinate", index=index)


@mcp.tool()
async def maker_resolve_asset(category: str, id: int | None = None, guid: str | None = None, slot: int | None = None) -> dict:
    """Convert a current catalog ID to a stable Sideloader GUID/original slot reference, or resolve GUID+slot to this session's ID. Always include category. Missing/ambiguous assets are errors. Base-game IDs need no GUID."""
    return await rpc("resolve_asset", **{k:v for k,v in locals().items() if v is not None})


@mcp.tool()
async def maker_list_bones(search: str = "", offset: int = 0, limit: int = 30) -> dict:
    """ABMX: search BodyTop bones and read modifiers for the active coordinate. Use returned names, never guess."""
    return await rpc("list_bones", search=search, offset=offset, limit=limit)


@mcp.tool()
async def maker_set_bone(name: str, scale: list[float] | None = None, length: float | None = None, position: list[float] | None = None, rotation: list[float] | None = None, coordinate_specific: bool = True) -> dict:
    """ABMX: edit a returned BodyTop bone with backup. Scale/length 0.01..5, position -2..2, rotation degrees -180..180. Default isolates active coordinate; false collapses coordinate-specific modifiers to the current values for all outfits."""
    return await rpc("set_bone", **{k:v for k,v in locals().items() if v is not None})


@mcp.tool()
async def maker_list_materials(target: str = "character", slot: int = 0) -> dict:
    """List renderers/materials and supported typed color/float properties for character, head, hair, clothes or accessory. Renderer IDs expire after reload. Slot addresses hair/clothes/accessory indices."""
    return await rpc("list_materials", target=target, slot=slot)


@mcp.tool()
async def maker_set_material(target: str, renderer_id: int, material_index: int, slot: int = 0, colors: dict | None = None, floats: dict | None = None) -> dict:
    """Material Editor: persist returned color/float properties with backup. Use only keys from list_materials. Same-named materials within the target may share persisted edits. No texture import/shader replacement."""
    return await rpc("set_material", **{k:v for k,v in locals().items() if v is not None})


@mcp.tool()
async def maker_snapshot(name: str = "snapshot") -> dict:
    """Save a new baseline card plus camera, active coordinate and diagnostic values; return a persistent snapshot ID. Names use letters/digits/underscores/hyphens."""
    return await rpc("snapshot", name=name)


@mcp.tool()
async def maker_list_snapshots() -> dict:
    """List up to 100 recent snapshots with card paths and saved camera/design data."""
    return await rpc("list_snapshots")


@mcp.tool()
async def maker_restore_snapshot(id: str) -> dict:
    """Back up current character, restore a same-sex snapshot card, active outfit and camera, and return a base-game verification report. Extended mod fields require adapter inspection; custom load toggles still apply."""
    return await rpc("restore_snapshot", id=id)


@mcp.tool()
async def maker_get_operation(operation_id: str) -> dict:
    """Resolve an uncertain/timeout result by operation ID from its error. Read queued/running/completed/canceled state, response and recovery card. Journal keeps 128 requests in the current game session."""
    return await rpc("get_operation", operation_id=operation_id)


@mcp.tool()
async def maker_camera_preset(action: str = "list", name: str | None = None) -> dict:
    """List, save or load named camera presets. Saving the same name updates it. Names use letters/digits/underscores/hyphens."""
    return await rpc("camera_preset", **{k:v for k,v in locals().items() if v is not None})


@mcp.tool()
async def maker_capture_views(yaw_offsets: list[float] | None = None, width: int = 1280, height: int = 720) -> list:
    """Capture 1..8 relative yaw angles (default -30,0,30 degrees), returning images and paths, then restore the original camera in finally. Dimensions 256..2048."""
    result = await rpc("capture_views", **{k:v for k,v in locals().items() if v is not None})
    return [json.dumps(result), *[Image(path=x["path"]) for x in result["captures"]]]


if __name__ == "__main__":
    mcp.run(transport="stdio")
