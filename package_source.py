"""Create a source-only release from an explicit allowlist; never include game data."""
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

ROOT = Path(__file__).resolve().parent
FILES = (
    '.gitignore', '.gitattributes', 'LICENSE', 'THIRD_PARTY_JSON_LICENSE.txt',
    'README.md', 'README.ru.md', 'CHANGELOG.md', 'TESTING.md', 'ROADMAP.md',
    'bridge_client.py', 'server.py', 'texture_assets.py', 'build.ps1', 'setup.ps1',
    'package_source.py', 'requirements.txt', 'requirements-lock.txt',
    'plugin/MakerBridge.cs', 'plugin/MakerBridge.Appearance.cs',
    'plugin/MakerBridge.Design.cs', 'plugin/MakerBridge.Mods.cs', 'plugin/MakerBridge.Workflow.cs',
    'plugin/MakerBridge.Textures.cs',
    'tests/test_bridge_client.py', 'tests/smoke_live.py', 'tests/smoke_appearance.py', 'tests/smoke_design.py',
    'tests/test_texture_assets.py', 'tests/smoke_textures.py',
    '.github/workflows/tests.yml',
)


def package():
    paths = [ROOT / name for name in FILES]
    for path in paths:
        if not path.is_file() or path.resolve() != path.absolute():
            raise ValueError(f'Missing or linked source file: {path}')
    target = ROOT / 'dist' / 'koikatu-maker-mcp-0.4.0-source.zip'
    target.parent.mkdir(exist_ok=True)
    with ZipFile(target, 'w', ZIP_DEFLATED) as archive:
        for name, path in zip(FILES, paths):
            archive.write(path, f'MakerBridge/{name}')
    print(target)
    return target


if __name__ == '__main__':
    package()
