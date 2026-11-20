"""Build the BadLevel preview site: fresh levels from the current code.

    python scripts/build_site.py site

Generates every preset side by side, a single-storey gallery and a multi-storey
gallery (each level with its 3D preview, analysis and .map/.glb downloads), plus
a landing page. Used by .github/workflows/pages.yml; works locally too.
"""
from __future__ import annotations

import datetime
import html
import os
import subprocess
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from badlevel import __version__  # noqa: E402
from badlevel.cli import main as cli  # noqa: E402
from badlevel.recipes import PRESETS  # noqa: E402

COMPOSITIONS = {
    "tree": "Branching growth from the start room (the default).",
    "hub": "A centre with spokes: side rooms and branches hang off it, loops come back to it.",
    "central": "Satellites in a tight ring around a big space (here a mega-room).",
    "linear": "A spine: the critical path runs along one axis, side rooms branch off as ribs.",
}


def build_compositions(out: str, seeds=(301, 302, 303, 304)) -> None:
    """The four macro structures side by side, with their measured silhouette."""
    from badlevel import GenConfig, generate
    from badlevel.cli import GALLERY, write_level
    from badlevel.export.preview import map_svg
    from badlevel.fingerprint import fingerprint
    try:
        from badlevel.analysis import analyze
    except ImportError:
        analyze = None
    os.makedirs(out, exist_ok=True)
    rows = []
    for comp, text in COMPOSITIONS.items():
        cards = []
        for seed in seeds:
            cfg = GenConfig(seed=seed, composition=comp, side_rooms=4, pacing_contrast=True,
                            mega_rooms=1 if comp == "central" else 0)
            level = generate(cfg)
            stem = f"badlevel_{comp}_{seed}"
            write_level(level, out, FORMATS.split(","), stem=stem, analysis=analyze(level) if analyze else None)
            fp = fingerprint(level)
            print(f"composition {comp} seed {seed}: elongation {fp['elongation']:.2f}, "
                  f"centrality {fp['centrality']:.2f}", flush=True)
            cards.append(f'<a class="card" href="{stem}.html"><div class="svg">{map_svg(level)}</div>'
                         f'<b>seed {seed}</b><span>elongation {fp["elongation"]:.2f} · centrality '
                         f'{fp["centrality"]:.2f} · dead ends {fp["leaf_share"]:.0%}</span></a>')
        rows.append(f'<section><h2>{comp}</h2><p>{html.escape(text)}</p><div class="grid">{"".join(cards)}</div></section>')
    note = ("<p>The numbers under each map are the measured silhouette: <b>elongation</b> (how long and thin "
            "the layout is), <b>centrality</b> (how much one room gathers the connections) and the share of "
            "<b>dead ends</b>. Click a map for the 3D preview.</p>")
    page = GALLERY.replace("<h1>BadLevel gallery</h1><div class=\"grid\">__ITEMS__</div>",
                           "<h1>BadLevel compositions</h1>" + note + "".join(rows))
    with open(os.path.join(out, "index.html"), "w") as fh:
        fh.write(page)

FORMATS = "html,map,glb,analysis"


LEVEL_TYPES = [
    ("The classic tour", "In the design language of the shooters we grew up with: their habits, never their maps.",
     ["techbase", "runekeep", "outpost", "grand"]),
    ("Deathmatch", "No keys, no exit: circuits with fair spawns, power-ups where the fights are, jump pads; "
                   "and mirrored team maps with a flag per base.", ["arena", "duel", "ctf"]),
    ("Worlds", "Places with a purpose: facilities with departments, Noor's dungeon, the wizard's tower and "
               "the goblin market.", ["facility", "complex", "dungeon", "wizard", "market"]),
]


def build_level_types(out: str) -> None:
    """One page of level types from above, grouped, each linking to its 3D preview on the recipes
    page (built by the 'compare' step, seed 1)."""
    from badlevel import generate
    from badlevel.export.preview import map_svg
    from badlevel.recipes import compile_recipe
    os.makedirs(out, exist_ok=True)
    sections = []
    for title, text, names in LEVEL_TYPES:
        cards = []
        for name in names:
            if name not in PRESETS:
                continue
            level = generate(compile_recipe(PRESETS[name], 1))
            print(f"level type {name}: {len(level.rooms)} rooms", flush=True)
            cards.append(f'<a class="card" href="../recipes/badlevel_{name}_1.html"><div class="svg">{map_svg(level)}</div>'
                         f'<b>{html.escape(name)}</b><span>{html.escape(PRESETS[name].description)}</span></a>')
        sections.append(f'<section><h2>{html.escape(title)}</h2><p>{html.escape(text)}</p>'
                        f'<div class="grid">{"".join(cards)}</div></section>')
    page = TYPES_PAGE.replace("__SECTIONS__", "".join(sections))
    with open(os.path.join(out, "index.html"), "w") as fh:
        fh.write(page)


TYPES_PAGE = """<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>BadLevel Level Types</title>
<style>
:root { --bg:#0f1115; --card:#171a21; --text:#e6e6e6; --muted:#9aa3b2; --accent:#f97316; --line:#2a2f3a; }
@media (prefers-color-scheme: light) { :root:not([data-theme="dark"]) { --bg:#f3f4f6; --card:#fff; --text:#1d2330; --muted:#5b6475; --line:#d8dce3; } }
:root[data-theme="light"] { --bg:#f3f4f6; --card:#fff; --text:#1d2330; --muted:#5b6475; --line:#d8dce3; }
* { box-sizing: border-box; }
body { margin:0; background:var(--bg); color:var(--text); font:15px/1.55 system-ui, -apple-system, Segoe UI, sans-serif; }
main { max-width: 1100px; margin: 0 auto; padding: 32px 16px 64px; }
h1 { font-size: 28px; margin: 0; } h1 span { color: var(--accent); }
h2 { font-size: 20px; margin: 34px 0 4px; } p { color: var(--muted); margin: 0 0 14px; }
.grid { display:grid; grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); gap: 14px; }
a.card { display:block; background:var(--card); border:1px solid var(--line); border-radius:12px; padding:12px;
         color:inherit; text-decoration:none; }
a.card:hover { border-color: var(--accent); }
.card b { display:block; font-size:16px; margin:8px 0 2px; }
.card span { color: var(--muted); font-size: 13px; }
.svg svg { width:100%; height:auto; display:block; border-radius:8px; }
.svg .bg { fill: #0b0d11; }
.back { font-size: 14px; } .back a { color: var(--accent); }
</style></head><body><main>
<h1>Bad<span>Level</span> level types</h1>
<p class="back"><a href="../index.html">&larr; back</a> · each plan is seed 1, from above (brighter is higher); click for the 3D preview.</p>
__SECTIONS__
</main></body></html>
"""


def commit() -> str:
    sha = os.environ.get("GITHUB_SHA")
    if sha:
        return sha[:7]
    try:
        return subprocess.check_output(["git", "rev-parse", "--short", "HEAD"], text=True).strip()
    except Exception:
        return "local"


def main(out: str = "site") -> int:
    os.makedirs(out, exist_ok=True)
    steps = [
        ["compare", "--recipes", "all", "--seeds", "1-2", "--out", f"{out}/recipes", "--formats", FORMATS],
        ["gallery", "--seeds", "1-6", "--out", f"{out}/classic", "--formats", FORMATS],
        ["gallery", "--seeds", "1-4", "--storeys", "3", "--out", f"{out}/multistorey", "--formats", FORMATS],
    ]
    for args in steps:
        print("badlevel", " ".join(args), flush=True)
        if cli(args) != 0:
            return 1
    build_compositions(f"{out}/compositions")
    build_level_types(f"{out}/types")
    from badlevel.cli import run_evaluation
    run_evaluation("presets", range(1, 7), f"{out}/evaluation", link_levels=True)
    run_evaluation("compositions", range(1, 7), f"{out}/evaluation/compositions", link_levels=True)

    # the architectural showcase is a frozen design review (committed output + screenshots), copied as-is
    src = os.path.join(os.path.dirname(__file__), "..", "showcase")
    if os.path.isfile(os.path.join(src, "index.html")):
        import shutil
        shutil.copytree(src, os.path.join(out, "showcase"), dirs_exist_ok=True)
    # the devlog: the story so far, one "map" per post (devlog/posts, scripts/build_devlog.py)
    sys.path.insert(0, os.path.dirname(__file__))
    import build_devlog
    build_devlog.build(os.path.join(out, "devlog"))

    presets = "".join(f"<li><b>{html.escape(r.name)}</b> {html.escape(r.description)}</li>" for r in PRESETS.values())
    when = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    page = INDEX.replace("__VERSION__", __version__).replace("__COMMIT__", commit()) \
        .replace("__WHEN__", when).replace("__PRESETS__", presets)
    with open(os.path.join(out, "index.html"), "w") as fh:
        fh.write(page)
    print(f"site -> {out}/index.html")
    return 0


INDEX = """<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>BadLevel Preview</title>
<style>
:root { --bg:#0f1115; --card:#171a21; --text:#e6e6e6; --muted:#9aa3b2; --accent:#f97316; --line:#2a2f3a; }
@media (prefers-color-scheme: light) { :root:not([data-theme="dark"]) { --bg:#f3f4f6; --card:#fff; --text:#1d2330; --muted:#5b6475; --line:#d8dce3; } }
:root[data-theme="light"] { --bg:#f3f4f6; --card:#fff; --text:#1d2330; --muted:#5b6475; --line:#d8dce3; }
* { box-sizing: border-box; }
body { margin:0; background:var(--bg); color:var(--text); font:15px/1.55 system-ui, -apple-system, Segoe UI, sans-serif; }
main { max-width: 980px; margin: 0 auto; padding: 32px 16px 64px; }
h1 { font-size: 30px; margin: 0; } h1 span { color: var(--accent); }
.sub { color: var(--muted); margin: 4px 0 28px; }
.cards { display:grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 14px; }
a.card { display:block; background:var(--card); border:1px solid var(--line); border-radius:12px; padding:18px;
         color:inherit; text-decoration:none; }
a.card:hover, div.card:hover { border-color: var(--accent); }
.card b { display:block; font-size:17px; margin-bottom:4px; }
.card span { color: var(--muted); font-size: 14px; }
/* cards with several links can't be one link (a link inside a link breaks the layout) */
div.card { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:18px; }
div.card b a { color:inherit; text-decoration:none; }
.card ul.steps { margin:8px 0 0; padding-left:18px; font-size:14px; } .card ul.steps li { margin:2px 0; }
.card ul.steps a { color: var(--accent); }
h2 { font-size: 18px; margin: 36px 0 8px; }
ul { padding-left: 20px; } li { margin: 4px 0; } li b { margin-right: 6px; }
code { background: var(--card); border:1px solid var(--line); border-radius:6px; padding: 1px 6px; font-size: 13px; }
pre { background: var(--card); border:1px solid var(--line); border-radius:10px; padding: 12px 14px; overflow-x:auto; }
footer { color: var(--muted); font-size: 13px; margin-top: 40px; }
</style></head>
<body><main>
<h1>Bad<span>Level</span> preview</h1>
<p class="sub">Procedural FPS level blockouts, generated fresh from the latest code on every push.
Version __VERSION__ · commit __COMMIT__ · built __WHEN__</p>
<div class="cards">
  <a class="card" href="devlog/index.html"><b>Devlog: the story so far</b><span>How BadLevel learned architecture, one map at a time: invisible walls, 132 monsters, domes, spiral stairs, Quake 2. The fun version.</span></a>
  <div class="card"><b><a href="showcase/index.html">Architectural showcase</a></b><span>Design review: 12 levels (fortress, industrial, underground, compound) with player-height screenshots and an honest verdict. Frozen snapshots, not rebuilt, each step before and after:</span><ul class="steps"><li><a href="showcase/index.html">the first review</a></li><li><a href="showcase/step1/index.html">step 1: hall cross-sections</a></li><li><a href="showcase/step2/index.html">step 2: smooth vaults, small rooms, walls</a></li><li><a href="showcase/step3/index.html">step 3: portals and framed reveals</a></li><li><a href="showcase/step4/index.html">step 4: underground</a></li><li><a href="showcase/step5/index.html">step 5: the deep chamber seen from above</a></li><li><a href="showcase/step6/index.html">step 6: compounds, buildings in the yards</a></li><li><a href="showcase/step7/index.html">step 7: room around the yards, life in the buildings</a></li><li><a href="showcase/step8/index.html">step 8: one deliberate landmark per level</a></li><li><a href="showcase/styles/index.html">one layout in all eight styles (incl. egyptian, sci-fi, castle)</a></li><li><a href="showcase/step9/index.html">step 9: room options, bigger rooms, round rooms, domes, spiral stairs</a></li></ul></div>
  <a class="card" href="types/index.html"><b>Level types</b><span>The classic tour (techbase, runekeep, outpost, grand), deathmatch (arena, duel, capture the flag) and worlds (facility, Noor's dungeon, the wizard's tower, the goblin market), from above.</span></a>
  <a class="card" href="recipes/index.html"><b>Recipes side by side</b><span>Every preset, two seeds each, with its 3D preview, analysis and downloads.</span></a>
  <a class="card" href="compositions/index.html"><b>Compositions side by side</b><span>The four macro structures from above: tree, hub, central, linear, with their measured silhouette.</span></a>
  <div class="card"><b><a href="evaluation/index.html">Evaluation: are they distinct?</a></b><span>Measured: how recognisable each preset is, which ones get confused, how varied each one stays.</span><ul class="steps"><li><a href="evaluation/index.html">presets</a></li><li><a href="evaluation/compositions/index.html">compositions</a></li></ul></div>
  <a class="card" href="classic/index.html"><b>Single-storey gallery</b><span>Six classic levels: shaped rooms, loops, keys, secrets, merged halls, corridor beats.</span></a>
  <a class="card" href="multistorey/index.html"><b>Multi-storey gallery</b><span>Three storeys: stairwells, lifts, ladders, drops, galleries and windows.</span></a>
</div>
<h2>In each level</h2>
<ul>
<li>Orbit, or <b>Fly (WASD)</b> from the player start; the <b>Cut</b> slider looks into lower storeys.</li>
<li>The side panel: combat analysis heatmaps, sight links, pacing, flags (click to jump there), rooms.</li>
<li>Next to each preview: the <code>.map</code> (TrenchBroom / Quake), <code>.glb</code> (any engine), <code>.analysis.json</code> and <code>.recipe.json</code>.</li>
</ul>
<h2>Presets</h2>
<ul>__PRESETS__</ul>
<h2>Run it yourself</h2>
<pre>pip install numpy
python -m badlevel compare --recipes all --seeds 1-3 --out out/compare</pre>
<footer>BadLevel · by BadSad, Noor and Sage</footer>
</main></body></html>
"""

if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "site"))
