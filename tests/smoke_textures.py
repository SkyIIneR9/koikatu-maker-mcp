"""Live texture import, export, UV, card persistence and reset with snapshot recovery."""
import argparse
import asyncio
import json
from pathlib import Path
import sys
import uuid
from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client
from test_texture_assets import png, chunk, SIGNATURE

ROOT = Path(__file__).resolve().parents[1]


async def main():
    runtime = ROOT / 'runtime'
    runtime.mkdir(exist_ok=True)
    fixture = runtime / 'texture-test.png'
    fixture.write_bytes(png(32, 32))
    report = {'checks': []}
    async with stdio_client(StdioServerParameters(command=sys.executable, args=[str(ROOT / 'server.py')])) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()

            async def result(tool, **params):
                response = await session.call_tool('maker_' + tool, params)
                if response.isError:
                    raise RuntimeError(str(response.content))
                value = json.loads(next(x.text for x in response.content if x.type == 'text'))
                return value, response

            async def data(tool, **params):
                return (await result(tool, **params))[0]

            assert (await data('status'))['bridge_version'] == '0.4.0'
            assert len((await session.list_tools()).tools) == 37
            snapshot = await data('snapshot', name='texture_test')
            report['snapshot_id'] = snapshot['id']
            report['baseline_card'] = snapshot['card']
            (runtime / 'texture-smoke-recovery.json').write_text(json.dumps(report, indent=2), encoding='utf-8')

            async def selection():
                listed = await data('list_materials', target='clothes', slot=0)
                item = next(x for x in listed['items'] if '_MainTex' in x['values']['textures'] and 'emblem' not in x['renderer'].lower())
                return {'target': 'clothes', 'slot': 0, 'renderer_id': item['renderer_id'], 'material_index': item['material_index'], 'property': '_MainTex'}, item

            select, before = await selection()
            exported, export_response = await result('export_material_texture', **select)
            assert any(x.type == 'image' for x in export_response.content)
            before_hash = exported['sha256']
            try:
                imported = await data('import_texture', path=str(fixture))
                assert imported['decoded'] and imported['width'] == 32 and imported['height'] == 32
                assert (await data('import_texture', path=str(fixture)))['texture'] == imported['texture']
                assert any(x['texture'] == imported['texture'] for x in (await data('list_textures', limit=100))['items'])
                report['checks'].append('PNG import, content deduplication, listing and original-texture MCP preview')
                assert (await data('export_material_texture', **select))['sha256'] == before_hash

                for params in (
                    {'texture': '../escape.png'},
                    {'texture': imported['texture'], 'scale': [0, 1]},
                    {'texture': imported['texture'], 'offset': [0, 999]},
                    {'texture': imported['texture'], 'action': 'reset'},
                    {'texture': imported['texture'], 'property': '_NotAProperty'},
                ):
                    assert (await session.call_tool('maker_set_material_texture', {**select, **params})).isError
                card_as_texture = await session.call_tool('maker_import_texture', {'path': snapshot['card']})
                assert card_as_texture.isError
                assert (await data('export_material_texture', **select))['sha256'] == before_hash
                report['checks'].append('Traversal, UV limits, conflicting reset, unknown property and card-as-texture rejected without changes')

                changed = await data('set_material_texture', **select, texture=imported['texture'], scale=[2, 3], offset=[0.125, -0.25])
                assert changed['values']['width'] == 32 and changed['values']['scale'] == [2, 3]
                assert Path(changed['backup_card']).is_file()
                applied = await data('export_material_texture', **select)
                assert applied['sha256'] != before_hash
                report['edited_texture_export'] = applied['path']
                report['edited_capture'] = (await data('capture', width=1280, height=720))['path']
                report['checks'].append('Texture/UV applied with backup and changed exported pixels')
                card = await data('save_card', name='texture_edited_' + uuid.uuid4().hex)
                # Ensure the original import file is not needed for a card reload.
                staged = Path(imported['path'])
                temporarily_hidden = staged.with_suffix('.held')
                if temporarily_hidden.exists():
                    raise RuntimeError('Temporary texture file already exists')
                staged.rename(temporarily_hidden)
                try:
                    await data('load_card', path=card['path'])
                    select, loaded = await selection()
                    tex = loaded['values']['textures']['_MainTex']
                    assert tex['width'] == 32 and tex['height'] == 32
                    assert tex['scale'] == [2, 3] and tex['offset'] == [0.125, -0.25]
                    assert (await data('export_material_texture', **select))['sha256'] == applied['sha256']
                finally:
                    temporarily_hidden.rename(staged)
                report['checks'].append('Card retains texture pixels and UV with staged PNG temporarily absent')

                reset = await data('set_material_texture', **select, action='reset')
                assert reset['reloaded'] and Path(reset['backup_card']).is_file()
                assert (await session.call_tool('maker_export_material_texture', select)).isError
                select, reset_material = await selection()
                reset_texture = reset_material['values']['textures']['_MainTex']
                original_texture = before['values']['textures']['_MainTex']
                assert reset_texture['scale'] == original_texture['scale']
                assert reset_texture['offset'] == original_texture['offset']
                assert (await data('export_material_texture', **select))['sha256'] == before_hash
                report['checks'].append('Reset removes texture+UV overrides and restores original pixels; old renderer ID rejected')
                reset_card = await data('save_card', name='texture_reset_' + uuid.uuid4().hex)
                await data('load_card', path=reset_card['path'])
                select, _ = await selection()
                assert (await data('export_material_texture', **select))['sha256'] == before_hash
                report['checks'].append('Reset survives a further card round trip')
            finally:
                report['restoration'] = await data('restore_snapshot', id=snapshot['id'])
                select, restored = await selection()
                assert (await data('export_material_texture', **select))['sha256'] == before_hash
                assert await data('get_camera') == snapshot['camera']
                report['checks'].append('Original snapshot/camera and exported texture pixels restored')
                (runtime / 'texture-smoke-report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
            print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    parser = argparse.ArgumentParser()
    parser.add_argument('--mutate', action='store_true', required=True)
    parser.parse_args()
    asyncio.run(main())
