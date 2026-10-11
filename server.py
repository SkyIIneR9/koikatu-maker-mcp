"""MCP stdio adapter. The game-facing API is implemented by KK_MakerBridge.dll."""
from __future__ import annotations

import asyncio
import json
from pathlib import Path

from mcp.server.fastmcp import FastMCP, Image
from bridge_client import call

mcp = FastMCP("Koikatu Character Maker", instructions="Controls the active character in Koikatu Character Maker. Read status, get_character and get_appearance first. Shape values are normalized (0.5 means 50%). Use returned enum names, slots and asset IDs; never guess indices. Save a baseline card before a creative session and capture after edits. Hair/clothes edits and same-sex card loads save an automatic backup_card, which can be restored with load_card. Clothes edits affect the active coordinate only. Third-party material/ABMX/overlay authoring is not exposed; their reload hooks and custom load toggles still apply. If a mutation times out, inspect current state before retrying.")
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


if __name__ == "__main__":
    mcp.run(transport="stdio")
