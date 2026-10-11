"""PNG import validation and staging tests; generated diagnostic fixtures only."""
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest
import zlib

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from texture_assets import SIGNATURE, MAX_BYTES, inspect_png, stage_texture


def chunk(kind, data):
    return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xffffffff)


def png(width=16, height=16):
    # Cyan/magenta diagnostic grid makes mapping and persistence easy to inspect.
    rows = b''.join(b'\0' + b''.join(bytes((20, 180, 210, 255) if (x // 4 + y // 4) % 2 else (210, 40, 140, 255)) for x in range(width)) for y in range(height))
    return SIGNATURE + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(rows)) + chunk(b'IEND', b'')


class TextureTests(unittest.TestCase):
    def test_valid_png_metadata(self):
        info = inspect_png(png())
        self.assertEqual((info['width'], info['height']), (16, 16))
        self.assertEqual(len(info['sha256']), 64)

    def test_corrupt_truncated_appended_and_non_png(self):
        source = png()
        for data in (b'not png', source[:-1], source + b'character card', source[:40] + b'bad' + source[43:]):
            with self.subTest(length=len(data)), self.assertRaises(ValueError):
                inspect_png(data)

    def test_animation_rejected(self):
        data = png()
        with self.assertRaisesRegex(ValueError, 'Animated'):
            inspect_png(data[:33] + chunk(b'acTL', struct.pack('>II', 1, 0)) + data[33:])

    def test_invalid_dimensions_and_header(self):
        for width, height, depth in ((0, 16, 8), (4097, 16, 8), (16, 4097, 8), (16, 16, 7)):
            data = SIGNATURE + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, depth, 6, 0, 0, 0)) + png()[33:]
            with self.subTest(width=width, height=height, depth=depth), self.assertRaises(ValueError):
                inspect_png(data)

    def test_size_limit(self):
        with self.assertRaisesRegex(ValueError, '16 MiB'):
            inspect_png(SIGNATURE + b'x' * MAX_BYTES)

    def test_duplicate_header_missing_data_and_bad_end(self):
        data = png()
        for bad in (data[:33] + data[8:], data[:33] + chunk(b'IEND', b''), data[:-12] + chunk(b'IEND', b'x')):
            with self.assertRaises(ValueError):
                inspect_png(bad)

    def test_hash_staging_is_deduplicated_and_does_not_edit_source(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            path = root / 'input.png'
            path.write_bytes(png())
            a = stage_texture(str(path), root / 'runtime')
            b = stage_texture(str(path), root / 'runtime')
            self.assertEqual(a, b)
            self.assertEqual(Path(a['path']).read_bytes(), path.read_bytes())
            self.assertEqual(len(list((root / 'runtime/textures').iterdir())), 1)

    def test_damaged_staged_file_is_not_silently_replaced(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            path = root / 'input.png'
            path.write_bytes(png())
            a = stage_texture(str(path), root / 'runtime')
            Path(a['path']).write_bytes(b'bad')
            with self.assertRaisesRegex(ValueError, 'content changed'):
                stage_texture(str(path), root / 'runtime')

    def test_relative_or_wrong_extension(self):
        for path in ('relative.png', str(Path.cwd() / 'not.png.txt')):
            with self.assertRaisesRegex(ValueError, 'absolute local'):
                stage_texture(path, Path.cwd())


if __name__ == '__main__':
    unittest.main()
