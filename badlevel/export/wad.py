"""badlevel.wad: the blockout textures as a Quake WAD2, so a compiled .bsp has real textures.

The .map names its textures (bl_wall, bl_floor, ...). Without a wad, qbsp writes those names
with no pixels, and an engine draws nothing (the Quake rerelease shows the level as empty void,
monsters floating in it). This writes a tiny wad of grey grid textures, made here: one line
every 32 units (a BadLevel cell), like the 3D preview.

Quake textures are 8-bit, indexed into the game's palette; its first 16 entries are a grey ramp
from black to near-white, so grey-only textures look the same in any Quake. Each miptex carries
its 4 mip levels; the sky is 256x128 (Quake's two-layer sky: front half transparent, index 0).
"""
from __future__ import annotations

import struct
from typing import Dict, List, Tuple

WAD_NAME = "badlevel.wad"

# name -> (size, base grey, line grey, pattern)
TEXTURES: Dict[str, Tuple[Tuple[int, int], int, int, str]] = {
    "bl_floor": ((64, 64), 11, 7, "grid"),
    "bl_wall": ((64, 64), 9, 6, "grid"),
    "bl_ceiling": ((64, 64), 5, 3, "grid"),
    "bl_door": ((64, 64), 12, 4, "door"),
    "bl_glass": ((64, 64), 13, 10, "grid"),
    "trigger": ((64, 64), 14, 12, "grid"),
    "bl_lift": ((64, 64), 12, 5, "door"),
    "bl_ladder": ((64, 64), 7, 13, "grid"),
    "sky1": ((256, 128), 12, 10, "sky"),
}


def _pixels(size, base: int, line: int, pattern: str) -> List[int]:
    w, h = size
    px = []
    for y in range(h):
        for x in range(w):
            if pattern == "sky":
                if x < w // 2:
                    px.append(0)                                   # front layer: transparent
                else:
                    px.append(base if (x // 16 + y // 16) % 2 else line)  # a soft back layer
            elif pattern == "door":
                edge = x in (0, 1, w - 2, w - 1) or y in (0, 1, h - 2, h - 1)
                cross = abs(x - y) <= 1 or abs(x - (h - 1 - y)) <= 1
                px.append(line if edge or cross else base)
            else:
                px.append(line if x % 32 == 0 or y % 32 == 0 else base)
    return px


def _mip(px: List[int], w: int, h: int, f: int) -> bytes:
    return bytes(px[y * w + x] for y in range(0, h, f) for x in range(0, w, f))


def _miptex(name: str, size, base: int, line: int, pattern: str) -> bytes:
    w, h = size
    px = _pixels(size, base, line, pattern)
    mips = [_mip(px, w, h, f) for f in (1, 2, 4, 8)]
    head = 16 + 8 + 16
    offsets, pos = [], head
    for m in mips:
        offsets.append(pos)
        pos += len(m)
    return struct.pack("<16sII4I", name.encode()[:15], w, h, *offsets) + b"".join(mips)


def make_wad() -> bytes:
    lumps = [(name, _miptex(name, *spec)) for name, spec in TEXTURES.items()]
    data, entries, pos = b"", [], 12
    for name, lump in lumps:
        entries.append(struct.pack("<iiibbh16s", pos, len(lump), len(lump), 0x44, 0, 0, name.encode()[:15]))
        data += lump
        pos += len(lump)
    return struct.pack("<4sii", b"WAD2", len(lumps), pos) + data + b"".join(entries)


def write_wad(path: str):
    with open(path, "wb") as fh:
        fh.write(make_wad())


# --- Quake 2: the same textures as .wal files (textures/badlevel/<name>.wal). Quake 2's palette also
# starts with a 16-step grey ramp, so the same indices give the same greys.
Q2_NAMES = {"bl_floor": "floor", "bl_wall": "wall", "bl_ceiling": "ceiling", "bl_door": "door",
            "bl_glass": "glass", "trigger": "trigger", "bl_lift": "lift", "bl_ladder": "ladder", "sky1": "sky"}


def make_wal(name: str) -> bytes:
    """One .wal: a 100-byte header (name, size, 4 mip offsets, next frame, flags, contents, value)
    and the 4 mip levels."""
    size, base, line, pattern = TEXTURES[name]
    w, h = size
    px = _pixels(size, base, line, pattern)
    mips = [_mip(px, w, h, f) for f in (1, 2, 4, 8)]
    offsets, pos = [], 100
    for m in mips:
        offsets.append(pos)
        pos += len(m)
    head = struct.pack("<32sII4I32siii", f"badlevel/{Q2_NAMES[name]}".encode(), w, h, *offsets, b"", 0, 0, 0)
    return head + b"".join(mips)


def write_wals(out_dir: str) -> List[str]:
    """textures/badlevel/*.wal under out_dir (a Quake 2 game directory, or next to the .map)."""
    import os
    folder = os.path.join(out_dir, "textures", "badlevel")
    os.makedirs(folder, exist_ok=True)
    paths = []
    for name, short in Q2_NAMES.items():
        path = os.path.join(folder, short + ".wal")
        with open(path, "wb") as fh:
            fh.write(make_wal(name))
        paths.append(path)
    return paths
