"""Live v0.2 MCP integration test. Use a clothed test character and known IDs.

Requires explicit --mutate --hair-id ID --top-id ID. Saves a baseline card,
validates rejected edits, edits assets/colors, loads the edited card, then
restores the baseline card and camera in finally.
"""
import argparse
import asyncio
import json
import sys
from datetime import datetime
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

ROOT = Path(__file__).resolve().parents[1]


async def main(args):
    (ROOT / "runtime").mkdir(exist_ok=True)
    report = {"checks": []}
    async with stdio_client(StdioServerParameters(command=sys.executable, args=["-X", "utf8", str(ROOT / "server.py")])) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()

            async def data(tool, **params):
                result = await session.call_tool(tool, params)
                if result.isError:
                    raise RuntimeError(str(result.content))
                return json.loads(next(x.text for x in result.content if x.type == "text"))

            status = await data("maker_status")
            assert status["bridge_version"] == "0.3.0" and status["maker_ready"]
            character = await data("maker_get_character")
            appearance = await data("maker_get_appearance")
            camera = await data("maker_get_camera")
            stamp = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
            baseline = await data("maker_save_card", name="appearance_baseline_" + stamp)
            assert all(baseline[k] for k in ("face_matches", "body_matches", "hair_matches", "clothes_matches"))
            report["baseline_card"] = baseline["path"]
            report["checks"].append("Baseline card reads back with matching face/body/hair/clothes")
            dirty = False
            try:
                for tool, params in [
                    ("maker_set_hair", {"slot": "front", "id": -1}),
                    ("maker_set_hair", {"slot": "front", "base_color": [2, 0, 0]}),
                    ("maker_set_clothes", {"slot": "top", "colors": {"999": [1, 0, 0]}}),
                    ("maker_load_card", {"path": "../escape.png"}),
                ]:
                    result = await session.call_tool(tool, params)
                    assert result.isError, (tool, params)
                assert await data("maker_get_appearance") == appearance
                report["checks"].append("Invalid IDs/colors/channels/paths rejected without appearance changes")
                dirty = True  # Restore even when a mutation reports an error after it started.
                hair = await data("maker_set_hair", slot="front", id=args.hair_id, base_color=[0.16, 0.24, 0.38], start_color=[0.05, 0.09, 0.16], end_color=[0.28, 0.4, 0.6])
                front = next(x for x in hair["appearance"]["hair"] if x["slot"] == "front")
                assert front["id"] == args.hair_id and abs(front["base_color"][0] - 0.16) < 1e-6
                assert Path(hair["backup_card"]).is_file()
                report["checks"].append("Hair asset and RGB colors changed; automatic backup exists")
                clothes = await data("maker_set_clothes", slot="top", id=args.top_id, colors={"0": [0.12, 0.3, 0.24, 1]})
                top = next(x for x in clothes["appearance"]["clothes"] if x["slot"] == "top")
                assert top["id"] == args.top_id and abs(top["colors"][0][1] - 0.3) < 1e-6
                report["checks"].append("Clothes asset and RGBA channel changed in active coordinate")
                capture = await data("maker_capture", width=1280, height=720)
                report["edited_capture"] = capture["path"]
                edited = await data("maker_save_card", name="appearance_edited_" + stamp)
                assert edited["hair_matches"] and edited["clothes_matches"]
                report["edited_card"] = edited["path"]
                report["checks"].append("Edited hair and active-coordinate clothing survive card save/readback")
                await data("maker_load_card", path=baseline["path"])
                assert await data("maker_get_appearance") == appearance
                await data("maker_load_card", path=edited["path"])
                actual = await data("maker_get_appearance")
                assert next(x for x in actual["hair"] if x["slot"] == "front")["id"] == args.hair_id
                assert next(x for x in actual["clothes"] if x["slot"] == "top")["id"] == args.top_id
                report["checks"].append("Baseline and edited cards loaded into the visible Maker")
            finally:
                if dirty:
                    await data("maker_load_card", path=baseline["path"])
                    await data("maker_set_camera", **camera)
                final = await data("maker_get_character")
                assert final["face"] == character["face"] and final["body"] == character["body"]
                assert await data("maker_get_appearance") == appearance
                report["checks"].append("Original hair/clothes/colors/face/body restored from baseline card")
                (ROOT / "runtime" / "appearance-smoke-report.json").write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
            print(json.dumps(report, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser()
    parser.add_argument("--mutate", action="store_true", required=True)
    parser.add_argument("--hair-id", type=int, required=True)
    parser.add_argument("--top-id", type=int, required=True)
    asyncio.run(main(parser.parse_args()))
