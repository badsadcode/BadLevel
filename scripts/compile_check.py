"""Compile BadLevel maps with ericw-tools and report what the compilers say.

    python scripts/compile_check.py /path/to/ericw-tools/bin [--seeds 1-2] [--vis] [--light] [--out DIR]
    python scripts/compile_check.py /path/to/ericw-tools-2.0 --game quake2 [...]   (Quake 2: needs 2.0)

Generates every preset in every architecture style (and none), compiles each .map with
qbsp (optionally vis and light), and reports per map: exit code, leaks, any warning other
than "no deathmatch start" (single-player maps), and what the original WinQuake engine would
refuse in the .bsp (textures without pixels, surface extents over 256), plus a note when a
level is bigger than original Quake's +-4096 (modern engines don't mind). Exit code 1 if any
map failed, leaked or warned.
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from badlevel import generate  # noqa: E402
from badlevel.architecture import STYLES  # noqa: E402
from badlevel.export import to_map  # noqa: E402
from badlevel.export.wad import WAD_NAME, write_wad  # noqa: E402
from badlevel.recipes import PRESETS, compile_recipe  # noqa: E402

EXPECTED = re.compile(r"No info_player_deathmatch"        # (a single-player map)
                      r"|failed to find basedir|Couldn't locate texture for skip")   # (Quake 2: no game install)


def warnings(log: str):
    log = re.sub(r"\x1b\[[0-9;]*m", "", log.replace("\r", "\n"))   # (2.0 colours its output)
    return [w.strip() for w in re.findall(r"(?:\*\*\* )?(?:WARNING|ERROR)[^%\n]*", log) if not EXPECTED.search(w)]


def original_quake_check(bsp_path: str) -> list:
    """What the original WinQuake engine refuses, read from the compiled .bsp (BSP29):
    textures without pixels (drawn as nothing or 'notexture', and a sky without pixels loses
    its special flag), faces whose surface extents exceed 256 texels ('Bad surface extents',
    computed as CalcSurfaceExtents does, a texture without pixels counting as ordinary), and
    geometry beyond +-4096 (the original network protocol's range)."""
    import math
    import struct
    data = open(bsp_path, "rb").read()
    lumps = [struct.unpack_from("<ii", data, 4 + 8 * i) for i in range(15)]

    def lump(i):
        o, n = lumps[i]
        return data[o:o + n]
    problems = []
    tex = lump(2)
    n, = struct.unpack_from("<i", tex, 0)
    offs = struct.unpack_from(f"<{n}i", tex, 4)
    pixels = []
    for o in offs:
        ok = o >= 0 and struct.unpack_from("<16sII4I", tex, o)[3] > 0
        pixels.append(ok)
    if not all(pixels):
        problems.append(f"{pixels.count(False)} of {n} textures have no pixels")
    verts = [struct.unpack_from("<3f", lump(3), k) for k in range(0, len(lump(3)), 12)]
    far = max((max(abs(v[0]), abs(v[1]), abs(v[2])) for v in verts), default=0)
    if far > 4096:
        problems.append(f"note: geometry reaches {far:.0f} (only original Quake's protocol is limited to +-4096)")
    texinfo = [struct.unpack_from("<8fii", lump(6), k) for k in range(0, len(lump(6)), 40)]
    edges = [struct.unpack_from("<HH", lump(12), k) for k in range(0, len(lump(12)), 4)]
    surfedges = [struct.unpack_from("<i", lump(13), k)[0] for k in range(0, len(lump(13)), 4)]
    bad = 0
    for k in range(0, len(lump(7)), 20):
        _, _, first, numedges, ti = struct.unpack_from("<HHiHH", lump(7), k)
        vecs, miptex, flags = texinfo[ti][:8], texinfo[ti][8], texinfo[ti][9]
        if not (miptex < n and pixels[miptex]):
            flags = 0                                    # the engine's notexture: checked like any face
        if flags & 1:
            continue
        mins, maxs = [1e9, 1e9], [-1e9, -1e9]
        for e in surfedges[first:first + numedges]:
            v = verts[edges[e][0]] if e >= 0 else verts[edges[-e][1]]
            for j in range(2):
                val = v[0] * vecs[j * 4] + v[1] * vecs[j * 4 + 1] + v[2] * vecs[j * 4 + 2] + vecs[j * 4 + 3]
                mins[j], maxs[j] = min(mins[j], val), max(maxs[j], val)
        if any((math.ceil(maxs[j] / 16) - math.floor(mins[j] / 16)) * 16 > 256 for j in range(2)):
            bad += 1
    if bad:
        problems.append(f"{bad} faces with surface extents > 256 (WinQuake: Bad surface extents)")
    return problems


def run(tool, args, cwd, env=None):
    p = subprocess.run([tool] + args, cwd=cwd, capture_output=True, text=True, timeout=1800, env=env)
    return p.returncode, p.stdout + p.stderr


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("bin", help="ericw-tools bin directory (qbsp, vis, light)")
    ap.add_argument("--seeds", default="1-2")
    ap.add_argument("--out", default="out/compile")
    ap.add_argument("--vis", action="store_true")
    ap.add_argument("--light", action="store_true")
    ap.add_argument("--game", choices=["quake", "quake2"], default="quake")
    a = ap.parse_args(argv)
    lo, _, hi = a.seeds.partition("-")
    seeds = range(int(lo), int(hi or lo) + 1)
    os.makedirs(a.out, exist_ok=True)
    out = os.path.abspath(a.out)
    q2 = a.game == "quake2"
    if q2:
        from badlevel.export.wad import write_wals
        write_wals(out)                              # textures/badlevel/*.wal: out is the game dir
    else:
        write_wad(os.path.join(out, WAD_NAME))       # the textures the .map names
    tools = {t: os.path.join(os.path.abspath(a.bin), t) for t in ("qbsp", "vis", "light")}
    env = dict(os.environ, LD_LIBRARY_PATH=os.path.abspath(a.bin))
    bad = 0
    total = 0
    for name in PRESETS:
        for style in ("none",) + STYLES:
            for seed in seeds:
                cfg = compile_recipe(PRESETS[name], seed)
                cfg.arch_style = "" if style == "none" else style
                stem = f"{name}_{style}_{seed}"
                with open(os.path.join(out, stem + ".map"), "w") as fh:
                    fh.write(to_map(generate(cfg), a.game))
                total += 1
                gd = ["-gamedir", out] if q2 else []
                rc, log = run(tools["qbsp"], (["-q2bsp"] if q2 else []) + gd + [stem + ".map"], out, env)
                leak = "leak" in log.lower() or os.path.exists(os.path.join(out, stem + ".pts"))
                ws = warnings(log)
                notes = []
                if rc == 0 and not q2:
                    for p in original_quake_check(os.path.join(out, stem + ".bsp")):
                        (notes if p.startswith("note:") else ws).append(f"original Quake: {p}")
                steps = [f"qbsp rc={rc}"]
                if rc == 0 and a.vis:
                    vrc, vlog = run(tools["vis"], gd + ["-fast", stem + ".bsp"], out, env)
                    steps.append(f"vis rc={vrc}")
                    ws += warnings(vlog)
                    rc = rc or vrc
                if rc == 0 and a.light:
                    lrc, llog = run(tools["light"], gd + [stem + ".bsp"], out, env)
                    steps.append(f"light rc={lrc}")
                    ws += warnings(llog)
                    rc = rc or lrc
                ok = rc == 0 and not leak and not ws
                bad += not ok
                print(f"{'ok ' if ok else 'BAD'} {stem}: {', '.join(steps)}" + (" LEAK" if leak else "")
                      + ("".join(f"\n      {w}" for w in sorted(set(ws))))
                      + ("".join(f"\n      {w}" for w in notes)), flush=True)
    print(f"{total - bad} of {total} maps compiled cleanly")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
