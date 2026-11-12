"""Command line interface.

    badlevel generate --seed 42 --out out/            # all formats
    badlevel generate --seed 7 --formats map,glb      # pick formats
    badlevel gallery --seeds 1-12 --out out/gallery    # many seeds + index.html
"""
from __future__ import annotations

import argparse
import json
import os
import sys

from . import __version__
from .config import GenConfig
from .export import to_glb, to_html, to_json, to_map
from .export.preview import map_svg
from .generator import GenerationError, generate

FORMATS = ("map", "glb", "json", "html")


def _parse_seeds(text: str):
    out = []
    for part in text.split(","):
        if "-" in part:
            a, b = part.split("-")
            out.extend(range(int(a), int(b) + 1))
        else:
            out.append(int(part))
    return out


def _cfg_from_args(args, seed: int) -> GenConfig:
    cfg = GenConfig(seed=seed)
    if args.config:
        with open(args.config) as fh:
            cfg = GenConfig.from_dict({**json.load(fh), "seed": seed})
    for name in ("main_rooms", "side_rooms", "loops", "keys"):
        v = getattr(args, name)
        if v is not None:
            setattr(cfg, name, v)
    if args.no_secret:
        cfg.secret = False
    return cfg


def write_level(level, out_dir: str, formats, stem: str = None):
    os.makedirs(out_dir, exist_ok=True)
    stem = stem or f"badlevel_{level.seed}"
    written = []
    for fmt in formats:
        path = os.path.join(out_dir, f"{stem}.{fmt}")
        if fmt == "map":
            data = to_map(level).encode()
        elif fmt == "glb":
            data = to_glb(level)
        elif fmt == "json":
            data = to_json(level).encode()
        elif fmt == "html":
            data = to_html(level).encode()
        else:
            raise SystemExit(f"unknown format {fmt}")
        with open(path, "wb") as fh:
            fh.write(data)
        written.append(path)
    return written


def main(argv=None):
    p = argparse.ArgumentParser(prog="badlevel", description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--version", action="version", version=__version__)
    sub = p.add_subparsers(dest="cmd", required=True)
    for name in ("generate", "gallery"):
        sp = sub.add_parser(name)
        if name == "generate":
            sp.add_argument("--seed", type=int, default=1)
        else:
            sp.add_argument("--seeds", default="1-8")
        sp.add_argument("--out", default="out")
        sp.add_argument("--formats", default=",".join(FORMATS))
        sp.add_argument("--config", help="JSON file with GenConfig fields")
        sp.add_argument("--main-rooms", dest="main_rooms", type=int)
        sp.add_argument("--side-rooms", dest="side_rooms", type=int)
        sp.add_argument("--loops", type=int)
        sp.add_argument("--keys", type=int)
        sp.add_argument("--no-secret", action="store_true")
    args = p.parse_args(argv)
    formats = [f.strip() for f in args.formats.split(",") if f.strip()]

    seeds = [args.seed] if args.cmd == "generate" else _parse_seeds(args.seeds)
    cards = []
    for seed in seeds:
        try:
            level = generate(_cfg_from_args(args, seed))
        except GenerationError as e:
            print(f"seed {seed}: FAILED {e}", file=sys.stderr)
            continue
        files = write_level(level, args.out, formats)
        st = level.stats()
        print(f"seed {seed}: {st['rooms']} rooms, {st['corridors']} corridors, "
              f"{st['entities']} entities -> {', '.join(files)}")
        cards.append((seed, map_svg(level), st))

    if args.cmd == "gallery" and cards:
        items = "".join(
            f'<a class="card" href="badlevel_{s}.html"><div class="svg">{svg}</div>'
            f'<b>seed {s}</b><span>{st["rooms"]} rooms · {st["multi_span_cells"]} over/under cells</span></a>'
            for s, svg, st in cards)
        page = GALLERY.replace("__ITEMS__", items)
        with open(os.path.join(args.out, "index.html"), "w") as fh:
            fh.write(page)
        print(f"gallery -> {os.path.join(args.out, 'index.html')}")
    return 0


GALLERY = """<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>BadLevel Gallery</title>
<style>
:root{--bg:#0f1115;--card:#171a21;--text:#e6e6e6;--muted:#9aa3b2}
@media (prefers-color-scheme: light){:root:not([data-theme="dark"]){--bg:#eef0f3;--card:#fff;--text:#1d2330;--muted:#5b6475}}
body{margin:0;background:var(--bg);color:var(--text);font:14px system-ui,sans-serif;padding:16px}
h1{font-size:20px} .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:14px}
.card{background:var(--card);border-radius:10px;padding:10px;color:inherit;text-decoration:none;display:flex;flex-direction:column;gap:4px}
.card span{color:var(--muted);font-size:12px} .map{width:100%;height:auto;border-radius:6px;background:#0b0d11}
.map .bg{fill:#0b0d11} .map .lbl{fill:#fff;font:bold 9px system-ui;text-anchor:middle;paint-order:stroke;stroke:#000;stroke-width:2.5px}
.map .sub{display:none}
</style></head><body><h1>BadLevel gallery</h1><div class="grid">__ITEMS__</div></body></html>"""


if __name__ == "__main__":
    sys.exit(main())
