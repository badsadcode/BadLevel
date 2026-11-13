"""Command line interface.

    badlevel generate --seed 42 --out out/                  # all formats
    badlevel generate --seed 7 --recipe bunker              # a preset recipe (or a recipe .json)
    badlevel gallery --seeds 1-12 --out out/gallery          # many seeds + index.html
    badlevel compare --recipes all --seeds 1-3 --out out/cmp # presets side by side
    badlevel presets                                         # list the preset recipes
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

FORMATS = ("map", "glb", "json", "html", "analysis")


def _parse_seeds(text: str):
    out = []
    for part in text.split(","):
        if "-" in part:
            a, b = part.split("-")
            out.extend(range(int(a), int(b) + 1))
        else:
            out.append(int(part))
    return out


def _cfg_from_args(args, seed: int, recipe=None) -> GenConfig:
    from .recipes import compile_recipe, get_recipe
    cfg = GenConfig(seed=seed)
    recipe = recipe or (get_recipe(args.recipe) if getattr(args, "recipe", None) else None)
    if recipe is not None:
        cfg = compile_recipe(recipe, seed)
    if args.config:
        with open(args.config) as fh:
            cfg = GenConfig.from_dict({**json.load(fh), "seed": seed})
    for name in ("main_rooms", "side_rooms", "loops", "keys", "storeys", "composition"):
        v = getattr(args, name)
        if v is not None:
            setattr(cfg, name, v)
    if args.no_secret:
        cfg.secret = False
    space = getattr(args, "space", None)
    if space is not None:
        from .recipes import roomier
        roomier(cfg, space)                                      # on top of any recipe
    variety = getattr(args, "variety", None)
    if variety is not None:
        from .recipes import varied
        varied(cfg, variety)
    monsters = getattr(args, "monsters", None)
    if monsters is not None:
        cfg.enemy_density *= max(0.0, monsters)                 # 0 = none, 0.5 = half, 2 = double
    style = getattr(args, "style", None)
    if style is not None:
        cfg.arch_style = "" if style == "none" else style       # any recipe, any architecture
    return cfg


def write_level(level, out_dir: str, formats, stem: str = None, analysis: dict = None, recipe_file: str = None):
    os.makedirs(out_dir, exist_ok=True)
    stem = stem or f"badlevel_{level.seed}"
    written = []
    ext = {"analysis": ".analysis.json"}
    downloads = [stem + ext.get(f, "." + f) for f in formats if f != "html" and (f != "analysis" or analysis)]
    if recipe_file:
        downloads.append(os.path.basename(recipe_file))
    for fmt in formats:
        path = os.path.join(out_dir, f"{stem}.analysis.json" if fmt == "analysis" else f"{stem}.{fmt}")
        if fmt == "map":
            data = to_map(level).encode()
            from .export.wad import WAD_NAME, write_wad
            wad = os.path.join(out_dir, WAD_NAME)
            if not os.path.exists(wad):            # the .map's textures, next to it (for qbsp, TrenchBroom)
                write_wad(wad)
        elif fmt == "glb":
            data = to_glb(level)
        elif fmt == "json":
            data = to_json(level).encode()
        elif fmt == "html":
            data = to_html(level, analysis, downloads).encode()
        elif fmt == "analysis":
            if analysis is None:
                continue
            data = json.dumps(analysis, indent=1).encode()
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
    sub.add_parser("presets", help="list the preset recipes")
    ev = sub.add_parser("evaluate", help="distinctness report: are presets / compositions recognisably different?")
    ev.add_argument("what", choices=["presets", "compositions"], nargs="?", default="presets")
    ev.add_argument("--seeds", default="1-6")
    ev.add_argument("--out", default="out/evaluation")
    for name in ("generate", "gallery", "compare"):
        sp = sub.add_parser(name)
        if name == "generate":
            sp.add_argument("--seed", type=int, default=1)
        else:
            sp.add_argument("--seeds", default="1-8" if name == "gallery" else "1-3")
        if name == "compare":
            sp.add_argument("--recipes", default="all", help="comma separated preset names or .json files, or 'all'")
        else:
            sp.add_argument("--recipe", help="preset name (see `badlevel presets`) or a recipe .json file")
        sp.add_argument("--out", default="out")
        sp.add_argument("--formats", default=",".join(FORMATS))
        sp.add_argument("--config", help="JSON file with GenConfig fields")
        sp.add_argument("--main-rooms", dest="main_rooms", type=int)
        sp.add_argument("--side-rooms", dest="side_rooms", type=int)
        sp.add_argument("--loops", type=int)
        sp.add_argument("--keys", type=int)
        sp.add_argument("--storeys", type=int, help="1 = single storey (default), 2-5 = stacked floors")
        sp.add_argument("--composition", choices=["tree", "hub", "central", "linear"],
                        help="macro structure of the level")
        sp.add_argument("--no-secret", action="store_true")
        from .architecture import STYLES
        sp.add_argument("--space", type=float, metavar="0-1",
                        help="spaciousness: bigger rooms, wider corridors, more headroom (0 = as is)")
        sp.add_argument("--variety", type=float, metavar="0-1",
                        help="room sizes from closets to halls, more shapes: hex, diamond, Z, H (0 = as is)")
        sp.add_argument("--monsters", type=float, metavar="X",
                        help="scale the number of monsters (0 = none, 0.5 = half, 2 = double)")
        sp.add_argument("--style", choices=("none",) + STYLES,
                        help="architecture style, whatever the recipe (none = plain boxes)")
        sp.add_argument("--no-analysis", action="store_true", help="skip combat analysis (needs numpy)")
        sp.add_argument("--candidates", type=int, default=1,
                        help="generate N variants per seed, keep the first that passes the quality gate")
    args = p.parse_args(argv)
    from .recipes import PRESETS, get_recipe, save_recipe
    if args.cmd == "presets":
        for r in PRESETS.values():
            dials = " ".join(f"{k}={v:g}" for k, v in r.dials().items())
            print(f"{r.name:11s} {r.description}\n{'':11s} {dials}")
        return 0
    if args.cmd == "evaluate":
        return run_evaluation(args.what, _parse_seeds(args.seeds), args.out)
    formats = [f.strip() for f in args.formats.split(",") if f.strip()]

    seeds = [args.seed] if args.cmd == "generate" else _parse_seeds(args.seeds)
    if args.cmd == "compare":
        names = list(PRESETS) if args.recipes == "all" else [n.strip() for n in args.recipes.split(",")]
        jobs = [(get_recipe(n), seed) for n in names for seed in seeds]
    else:
        rec = get_recipe(args.recipe) if args.recipe else None
        jobs = [(rec, seed) for seed in seeds]
    cards = []
    for recipe, seed in jobs:
        analysis = None
        stem = f"badlevel_{recipe.name}_{seed}" if args.cmd == "compare" else f"badlevel_{seed}"
        want_analysis = not args.no_analysis and ("html" in formats or "analysis" in formats or args.candidates > 1)
        try:
            if want_analysis:
                try:
                    from .analysis import analyze, generate_selected
                except ImportError:
                    print("numpy not installed: skipping analysis (pip install numpy)", file=sys.stderr)
                    want_analysis = False
            cfg = _cfg_from_args(args, seed, recipe)
            if want_analysis and args.candidates > 1:
                level, analysis = generate_selected(cfg, args.candidates)
            else:
                level = generate(cfg)
                if want_analysis:
                    analysis = analyze(level)
        except GenerationError as e:
            print(f"seed {seed}: FAILED {e}", file=sys.stderr)
            continue
        rpath = os.path.join(args.out, f"{stem}.recipe.json") if recipe is not None else None
        files = write_level(level, args.out, formats, stem=stem, analysis=analysis, recipe_file=rpath)
        if recipe is not None:
            save_recipe(recipe, rpath, level.config)      # the recipe travels with the level
            files.append(rpath)
        st = level.stats()
        extra = ""
        if analysis:
            sev = [f["severity"] for f in analysis["flags"]]
            extra = f", flags: {sev.count('problem')} problem / {sev.count('warn')} warn / {sev.count('info')} info"
        print(f"seed {seed}: {st['rooms']} rooms, {st['corridors']} corridors, "
              f"{st['entities']} entities{extra} -> {', '.join(files)}")
        cards.append((seed, map_svg(level), st, recipe, stem))

    if args.cmd == "compare" and cards:
        rows = []
        for name in dict.fromkeys(c[3].name for c in cards):
            group = [c for c in cards if c[3].name == name]
            rec = group[0][3]
            items = "".join(
                f'<a class="card" href="{stem}.html"><div class="svg">{svg}</div><b>seed {s}</b>'
                f'<span>{st["rooms"]} rooms · {st["storeys"]} storey(s) · {st["halls"]} halls · '
                f'{st["windows"]} windows</span></a>' for s, svg, st, _, stem in group)
            rows.append(f'<section><h2>{rec.name}</h2><p>{rec.description}</p><div class="grid">{items}</div></section>')
        page = GALLERY.replace("BadLevel gallery</h1><div class=\"grid\">__ITEMS__</div>",
                               "BadLevel recipes</h1>" + "".join(rows))
        with open(os.path.join(args.out, "index.html"), "w") as fh:
            fh.write(page)
        print(f"comparison -> {os.path.join(args.out, 'index.html')}")

    if args.cmd == "gallery" and cards:
        items = "".join(
            f'<a class="card" href="badlevel_{s}.html"><div class="svg">{svg}</div>'
            f'<b>seed {s}</b><span>{st["rooms"]} rooms · {st["multi_span_cells"]} over/under cells</span></a>'
            for s, svg, st, _, _ in cards)
        page = GALLERY.replace("__ITEMS__", items)
        with open(os.path.join(args.out, "index.html"), "w") as fh:
            fh.write(page)
        print(f"gallery -> {os.path.join(args.out, 'index.html')}")
    return 0


def run_evaluation(what: str, seeds, out: str, link_levels: bool = False) -> int:
    """Write <out>/index.html and <out>/evaluation.json; with link_levels also each level's preview."""
    from .config import GenConfig
    from .evaluate import evaluate_configs, render_html
    from .export.preview import map_svg
    from .recipes import PRESETS, compile_recipe
    os.makedirs(out, exist_ok=True)
    if what == "presets":
        items = [(n, compile_recipe(r, s)) for n, r in PRESETS.items() for s in seeds]
        title = "BadLevel: are the presets distinct?"
        intro = ("Every preset generated with the same seeds, each level reduced to a fingerprint of 22 features "
                 "(room sizes, ceilings, corridors, storeys, regularity, halls, windows, combat, loops, open sky, "
                 "mega-rooms and the silhouette). Distinct between presets, varied within each one.")
    else:
        items = [(c, GenConfig(seed=s, composition=c, side_rooms=4, pacing_contrast=True,
                               mega_rooms=1 if c == "central" else 0)) for c in ("tree", "hub", "central", "linear")
                 for s in seeds]
        title = "BadLevel: are the compositions distinct?"
        intro = ("The same settings with only the composition changed. Compositions differ in macro shape, so the "
                 "silhouette (elongation, centrality, dead ends) is the measure that matters here; the other "
                 "features are noise for this question.")
    report = evaluate_configs(items)
    thumbs = {}
    for label, cfg in items[:]:
        if len(thumbs.get(label, [])) >= 4:
            continue
        try:
            level = generate(cfg)
        except GenerationError:
            continue
        href = f"{label}_{cfg.seed}.html"
        if link_levels:
            write_level(level, out, ["html", "map"], stem=f"{label}_{cfg.seed}")
        thumbs.setdefault(label, []).append((href if link_levels else "#", map_svg(level)))
    with open(os.path.join(out, "evaluation.json"), "w") as fh:
        json.dump(report, fh, indent=1)
    with open(os.path.join(out, "index.html"), "w") as fh:
        fh.write(render_html(report, title, intro, thumbs))
    print(f"{what}: {report['accuracy']:.0%} recognised (silhouette {report['silhouette_accuracy']:.0%}), "
          f"separation {report['separation']} -> {os.path.join(out, 'index.html')}")
    return 0


GALLERY = """<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>BadLevel Gallery</title>
<style>
:root{--bg:#0f1115;--card:#171a21;--text:#e6e6e6;--muted:#9aa3b2}
@media (prefers-color-scheme: light){:root:not([data-theme="dark"]){--bg:#eef0f3;--card:#fff;--text:#1d2330;--muted:#5b6475}}
body{margin:0;background:var(--bg);color:var(--text);font:14px system-ui,sans-serif;padding:16px}
h1{font-size:20px} h2{font-size:17px;margin:22px 0 2px} section p{color:var(--muted);margin:0 0 10px} .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:14px}
.card{background:var(--card);border-radius:10px;padding:10px;color:inherit;text-decoration:none;display:flex;flex-direction:column;gap:4px}
.card span{color:var(--muted);font-size:12px} .map{width:100%;height:auto;border-radius:6px;background:#0b0d11}
.map .bg{fill:#0b0d11} .map .lbl{fill:#fff;font:bold 9px system-ui;text-anchor:middle;paint-order:stroke;stroke:#000;stroke-width:2.5px}
.map .sub{display:none}
</style></head><body><h1>BadLevel gallery</h1><div class="grid">__ITEMS__</div></body></html>"""


if __name__ == "__main__":
    sys.exit(main())
