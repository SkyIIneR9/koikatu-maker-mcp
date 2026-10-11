"""Stage local PNGs by content hash. No network URLs, game assets or image editing."""
from __future__ import annotations

import hashlib
import os
from pathlib import Path
import struct
import tempfile
import zlib

MAX_BYTES = 16 * 1024 * 1024
MAX_SIDE = 4096
SIGNATURE = b'\x89PNG\r\n\x1a\n'


def inspect_png(data: bytes) -> dict:
    """Validate PNG structure/CRCs and bounded dimensions; Unity decodes before editing."""
    if len(data) > MAX_BYTES or not data.startswith(SIGNATURE):
        raise ValueError('Expected a PNG no larger than 16 MiB')
    pos, width, height, idat = 8, 0, 0, False
    ended_idat = False
    while pos + 12 <= len(data):
        size = struct.unpack_from('>I', data, pos)[0]
        kind = data[pos + 4:pos + 8]
        end = pos + 12 + size
        if end > len(data):
            raise ValueError('Truncated PNG chunk')
        payload = data[pos + 8:pos + 8 + size]
        crc = struct.unpack_from('>I', data, end - 4)[0]
        if zlib.crc32(kind + payload) & 0xffffffff != crc:
            raise ValueError('PNG checksum mismatch')
        if pos == 8:
            if kind != b'IHDR' or size != 13:
                raise ValueError('PNG must begin with IHDR')
            width, height, depth, color, compression, filtering, interlace = struct.unpack('>IIBBBBB', payload)
            legal = {0: (1, 2, 4, 8, 16), 2: (8, 16), 3: (1, 2, 4, 8), 4: (8, 16), 6: (8, 16)}
            if not 1 <= width <= MAX_SIDE or not 1 <= height <= MAX_SIDE:
                raise ValueError('PNG dimensions must be 1..4096 per side')
            if depth not in legal.get(color, ()) or compression or filtering or interlace not in (0, 1):
                raise ValueError('Unsupported PNG header')
        elif kind == b'IHDR':
            raise ValueError('Duplicate PNG header')
        if kind in (b'acTL', b'fcTL', b'fdAT'):
            raise ValueError('Animated PNG is not supported')
        if kind == b'IDAT':
            if ended_idat:
                raise ValueError('Non-consecutive PNG image data')
            idat = True
        elif idat:
            ended_idat = True
        if kind == b'IEND':
            if size or not idat or end != len(data):
                raise ValueError('Invalid PNG ending or appended data (character cards are not textures)')
            return {'width': width, 'height': height, 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}
        pos = end
    raise ValueError('Incomplete PNG')


def stage_texture(source: str, runtime: Path) -> dict:
    path = Path(source)
    if not path.is_absolute() or path.suffix.lower() != '.png':
        raise ValueError('Provide an absolute local .png path')
    with path.open('rb') as stream:
        data = stream.read(MAX_BYTES + 1)
    info = inspect_png(data)
    directory = runtime / 'textures'
    directory.mkdir(parents=True, exist_ok=True)
    if directory.resolve() != directory.absolute():
        raise ValueError('Linked texture directories are not supported')
    name = info['sha256'] + '.png'
    target = directory / name
    if target.is_symlink() or (target.exists() and target.resolve() != target.absolute()):
        raise ValueError('Linked texture files are not supported')
    if target.exists():
        with target.open('rb') as stream:
            if stream.read(MAX_BYTES + 1) != data:
                raise ValueError('Staged texture content changed; remove the damaged staging file before importing')
    else:
        fd, temp = tempfile.mkstemp(prefix='upload_', suffix='.tmp', dir=directory)
        try:
            with os.fdopen(fd, 'wb') as stream:
                stream.write(data)
            os.replace(temp, target)
        finally:
            if os.path.exists(temp):
                os.unlink(temp)
    return {**info, 'texture': name, 'path': str(target)}
