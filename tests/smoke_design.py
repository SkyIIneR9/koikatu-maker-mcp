"""Opt-in v0.3 live tests. Requires a clothed male test character with ABMX/ME.

Restores a snapshot in finally. Test assets are selected from current game data.
"""
import argparse
import asyncio
import json
import sys
import uuid
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from bridge_client import call, BridgeError


async def main():
    (ROOT / 'runtime').mkdir(exist_ok=True)
    report = {'checks': []}
    async with stdio_client(StdioServerParameters(command=sys.executable, args=[str(ROOT / 'server.py')])) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()

            async def data(tool_name, **params):
                result = await session.call_tool('maker_' + tool_name, params)
                if result.isError:
                    raise RuntimeError(str(result.content))
                return json.loads(next(x.text for x in result.content if x.type == 'text'))

            tools = await session.list_tools()
            assert len(tools.tools) == 37
            report['checks'].append('Real MCP exposes 37 tools')
            assert (await data('status'))['bridge_version'] == '0.4.0'
            baseline = await data('snapshot', name='design_test')
            report['snapshot_id'] = baseline['id']
            report['baseline_card'] = baseline['card']
            (ROOT / 'runtime/design-smoke-recovery.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
            appearance = await data('get_appearance')
            accessories = await data('get_accessories')
            design = await data('get_design')
            bone_rows = (await data('list_bones', search='Head', limit=100))['items']
            bone = next(x for x in bone_rows if x['name'].lower().endswith('_head') and x['name'].lower().startswith(('cf_j_', 'cm_j_')))
            bone_before = bone
            material_before = await data('list_materials', target='clothes', slot=0)
            try:
                for tool, params in [
                    ('set_design', {'changes': {'body': {'skinMainColor': [0, 1, 0]}, 'face': {'unsupported': 1}}}),
                    ('set_accessory', {'slot': 19, 'scale': [-1, 1, 1]}),
                    ('set_bone', {'name': bone['name'], 'scale': [0, 1, 1]}),
                    ('restore_snapshot', {'id': '../outside'}),
                ]:
                    assert (await session.call_tool('maker_' + tool, params)).isError
                assert await data('get_design') == design
                assert await data('get_accessories') == accessories
                report['checks'].append('Invalid batches/transforms/bones/snapshot paths rejected without changes')

                front = next(x for x in appearance['hair'] if x['slot'] == 'front')
                reference = await data('resolve_asset', category=front['category'], id=front['id'])
                if 'guid' in reference:
                    resolved = await data('resolve_asset', category=reference['category'], guid=reference['guid'], slot=reference['slot'])
                    assert resolved['id'] == front['id']
                    report['checks'].append('Sideloader GUID/original slot resolves to the current mod asset')

                request_id = uuid.uuid4().hex
                changes = {'left_eye': {'baseColor': [0.2, 0.4, 0.6, 1]}, 'face': {'eyebrowColor': [0.2, 0.15, 0.1, 1]}, 'body': {'skinMainColor': [0.9, 0.75, 0.65, 1]}, 'identity': {'firstname': 'BridgeTest'}}
                edited = await asyncio.to_thread(call, 'set_design', request_id=request_id, changes=changes)
                replay = await asyncio.to_thread(call, 'set_design', request_id=request_id, changes=changes)
                assert replay == edited  # Including identical backup path: mutation wasn't repeated.
                op = await data('get_operation', operation_id=request_id)
                assert op['state'] == 'completed' and op['response']['ok']
                assert abs(edited['values']['left_eye']['baseColor'][0] - 0.2) < 1e-6
                assert edited['values']['right_eye'] == design['values']['right_eye']
                report['checks'].append('Design patch applied; other eye preserved; request replay does not repeat mutation')

                occupied = next(x for x in accessories['items'] if x['category'] != 'ao_none')
                changed = await data('set_accessory', slot=occupied['slot'], position=[0, 0, -0.9], colors={'0': [0.15, 0.25, 0.35, 1]})
                part = changed['accessories']['items'][occupied['slot']]
                assert abs(part['transforms'][0]['position'][2] + 0.9) < 1e-6
                empty = next(x for x in accessories['items'] if x['category'] == 'ao_none')['slot']
                await data('set_accessory', slot=empty, category=occupied['category'], id=occupied['id'])
                await data('set_accessory', slot=empty, category='ao_none')
                report['checks'].append('Accessory transform/color, insertion and removal passed')

                b = await data('set_bone', name=bone['name'], scale=[1.02, 1.01, 1.02])
                assert abs(b['values']['scale'][0] - 1.02) < 1e-6
                report['checks'].append('ABMX bone modification applied through its controller')
                materials = await data('list_materials', target='clothes', slot=0)
                material = next(x for x in materials['items'] if '_ShadowColor' in x['values']['colors'])
                m = await data('set_material', target='clothes', slot=0, renderer_id=material['renderer_id'], material_index=material['material_index'], colors={'_ShadowColor': [0.6, 0.65, 0.7, 1]})
                assert abs(m['values']['colors']['_ShadowColor'][0] - 0.6) < 1e-6
                report['checks'].append('Material Editor color persisted through its controller')

                edited_design = await data('get_design')
                edited_accessories = await data('get_accessories')
                card = await data('save_card', name='design_edited_' + uuid.uuid4().hex)
                await data('load_card', path=card['path'])
                loaded_design = await data('get_design')
                # Translation plugins serialize native nicknames but display translated ones.
                loaded_design['values']['identity'].pop('nickname')
                edited_design['values']['identity'].pop('nickname')
                assert loaded_design == edited_design
                assert await data('get_accessories') == edited_accessories
                loaded_bone = next(x for x in (await data('list_bones', search=bone['name'], limit=100))['items'] if x['name'] == bone['name'])
                assert abs(loaded_bone['values']['scale'][0] - 1.02) < 1e-6
                loaded_materials = await data('list_materials', target='clothes', slot=0)
                loaded_material = next(x for x in loaded_materials['items'] if x['renderer'] == material['renderer'])
                assert abs(loaded_material['values']['colors']['_ShadowColor'][0] - 0.6) < 1e-6
                report['checks'].append('Design/accessories/ABMX/material values survive card save and reload')

                coords = (await data('list_coordinates'))['items']
                other = next(x['index'] for x in coords if not x['active'])
                await data('select_coordinate', index=other)
                assert next(x['index'] for x in (await data('list_coordinates'))['items'] if x['active']) == other
                await data('select_coordinate', index=baseline['coordinate'])
                report['checks'].append('Outfit coordinate switches away and back')

                camera = await data('get_camera')
                await data('camera_preset', action='save', name='smoke_camera')
                result = await session.call_tool('maker_capture_views', {'yaw_offsets': [-20, 0, 20], 'width': 640, 'height': 480})
                assert not result.isError and sum(x.type == 'image' for x in result.content) == 3
                assert await data('get_camera') == camera
                await data('camera_preset', action='load', name='smoke_camera')
                report['checks'].append('Multi-angle MCP images and camera preset; camera restored')
            finally:
                restored = await data('restore_snapshot', id=baseline['id'])
                report['restoration'] = restored
                (ROOT / 'runtime/design-smoke-report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
                assert await data('get_design') == design
                assert await data('get_appearance') == appearance
                assert await data('get_accessories') == accessories
                assert await data('get_camera') == baseline['camera']
                bone_final = next(x for x in (await data('list_bones', search=bone['name'], limit=100))['items'] if x['name'] == bone['name'])
                assert bone_final == bone_before
                material_final = await data('list_materials', target='clothes', slot=0)
                def material_values(result):
                    return {(x['renderer'], x['material_index']): x['values'] for x in result['items']}
                assert material_values(material_final) == material_values(material_before)
                report['checks'].append('Snapshot restored original design/appearance/accessories/camera, ABMX and material values')
                (ROOT / 'runtime/design-smoke-report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
            print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    parser = argparse.ArgumentParser()
    parser.add_argument('--mutate', action='store_true', required=True)
    parser.parse_args()
    asyncio.run(main())
