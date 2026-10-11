"""Exercise the real MCP stdio server and running Maker. --mutate opts into edits.

Saves a baseline/new test card; restores shape arrays and camera in finally.
Run only with a test character open and avoid concurrent manual edits.
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


async def main(mutate=False):
    (ROOT / "runtime").mkdir(exist_ok=True)
    checks = []
    async with stdio_client(StdioServerParameters(command=sys.executable, args=[str(ROOT / "server.py")])) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()
            tools = await session.list_tools()
            assert len(tools.tools) >= 15
            checks.append(f"MCP initialized; {len(tools.tools)} tools discovered")

            async def invoke(tool_name, **args):
                result = await session.call_tool(tool_name, args)
                if result.isError:
                    raise RuntimeError(str(result.content))
                return result

            async def data(tool_name, **args):
                result = await invoke(tool_name, **args)
                return json.loads(next(x.text for x in result.content if x.type == "text"))

            status = await data("maker_status")
            assert status["maker_ready"], status
            initial = await data("maker_get_character")
            camera = await data("maker_get_camera")
            checks.append(f"Maker ready; character sex={initial['sex']}; {len(initial['face'])} face / {len(initial['body'])} body shapes")
            assets = await data("maker_list_assets", category="bo_hair_f", limit=3)
            assert assets["total"] > 0 and len(assets["items"]) == 3
            checks.append("Installed hair asset catalog paginates")

            capture = await invoke("maker_capture", width=1280, height=720)
            assert any(x.type == "image" and x.mimeType == "image/png" for x in capture.content)
            capture_meta = json.loads(next(x.text for x in capture.content if x.type == "text"))
            checks.append("Screenshot returned as MCP image content")

            report = {"checks": checks, "before_capture": capture_meta, "camera": camera}
            if mutate:
                stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
                baseline = await data("maker_save_card", name="smoke_baseline_" + stamp)
                assert all(baseline[k] for k in ("readable", "face_matches", "body_matches", "sex_matches", "name_matches"))
                report["baseline_card"] = baseline
                checks.append("Baseline card saved and read back with matching values")

                # Valid+invalid batch must not apply the valid element.
                invalid = await session.call_tool("maker_set_shapes", {"face": {"0": 0.5, "99999": 0.2}})
                assert invalid.isError
                unchanged = await data("maker_get_character")
                assert unchanged["face"] == initial["face"] and unchanged["undo_count"] == initial["undo_count"]
                checks.append("Invalid shape batch rejected without partial mutation")
                invalid = await session.call_tool("maker_save_card", {"name": "../escape"})
                assert invalid.isError
                overwrite = await session.call_tool("maker_save_card", {"name": "smoke_baseline_" + stamp})
                assert overwrite.isError
                checks.append("Path traversal and card overwrite rejected")

                changed = False
                camera_changed = False
                try:
                    shape = initial["face"][0]
                    value = min(1.0, shape["value"] + 0.12) if shape["value"] < 0.8 else shape["value"] - 0.12
                    edit = await data("maker_set_shapes", face={shape["name"]: value})
                    changed = True
                    assert abs(edit["face"][0]["value"] - value) < 1e-6
                    report["shape_change"] = {"name": shape["name"], "before": shape["value"], "after": edit["face"][0]["value"]}
                    checks.append("Face width changed and read back")
                    result = await invoke("maker_capture", width=1280, height=720)
                    report["after_capture"] = json.loads(next(x.text for x in result.content if x.type == "text"))
                    card = await data("maker_save_card", name="smoke_edited_" + stamp)
                    assert card["face_matches"] and card["body_matches"]
                    report["edited_card"] = card
                    checks.append("Edited card saved and its morphs verified")
                    rotation = list(camera["rotation"])
                    rotation[1] = (rotation[1] + 30) % 360
                    await data("maker_set_camera", rotation=rotation)
                    camera_changed = True
                    actual = await data("maker_get_camera")
                    assert abs(actual["rotation"][1] - rotation[1]) < 1e-4
                    checks.append("Camera rotation updated and read back")
                finally:
                    if camera_changed:
                        await data("maker_set_camera", **camera)
                    if changed:
                        await data("maker_undo_shapes")
                final = await data("maker_get_character")
                assert final["face"] == initial["face"] and final["body"] == initial["body"]
                assert await data("maker_get_camera") == camera
                checks.append("Original face/body arrays and camera restored exactly")
            (ROOT / "runtime" / "smoke-report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
            print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--mutate", action="store_true")
    asyncio.run(main(parser.parse_args().mutate))
