"""Compile BadLevel maps with ericw-tools and report what the compilers say.

    python scripts/compile_check.py /path/to/ericw-tools/bin [--seeds 1-2] [--vis] [--light] [--out DIR]

Generates every preset in every architecture style (and none), compiles each .map with
qbsp (optionally vis and light), and reports per map: exit code, leaks, and any warning
other than the expected ones (missing textures: BadLevel ships texture names, not a Quake
wad; no wad key; no deathmatch start in a single-player map). Exit code 1 if any map
failed, leaked or warned unexpectedly.
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
from badlevel.recipes import PRESETS, compile_recipe  # noqa: E402

EXPECTED = re.compile(r"Texture .* not found|No wad or _wad key|No info_player_deathmatch")


def warnings(log: str):
    log = log.replace("\r", "\n")
    return [w.strip() for w in re.findall(r"\*\*\* (?:WARNING|ERROR)[^%\n]*", log) if not EXPECTED.search(w)]


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
    a = ap.parse_args(argv)
    lo, _, hi = a.seeds.partition("-")
    seeds = range(int(lo), int(hi or lo) + 1)
    os.makedirs(a.out, exist_ok=True)
    out = os.path.abspath(a.out)
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
                    fh.write(to_map(generate(cfg)))
                total += 1
                rc, log = run(tools["qbsp"], [stem + ".map"], out)
                leak = "leak" in log.lower() or os.path.exists(os.path.join(out, stem + ".pts"))
                ws = warnings(log)
                steps = [f"qbsp rc={rc}"]
                if rc == 0 and a.vis:
                    vrc, vlog = run(tools["vis"], ["-fast", stem + ".bsp"], out, env)
                    steps.append(f"vis rc={vrc}")
                    ws += warnings(vlog)
                    rc = rc or vrc
                if rc == 0 and a.light:
                    lrc, llog = run(tools["light"], [stem + ".bsp"], out, env)
                    steps.append(f"light rc={lrc}")
                    ws += warnings(llog)
                    rc = rc or lrc
                ok = rc == 0 and not leak and not ws
                bad += not ok
                print(f"{'ok ' if ok else 'BAD'} {stem}: {', '.join(steps)}" + (" LEAK" if leak else "")
                      + ("".join(f"\n      {w}" for w in sorted(set(ws)))), flush=True)
    print(f"{total - bad} of {total} maps compiled cleanly")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
