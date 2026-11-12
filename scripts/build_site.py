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
    from badlevel.cli import run_evaluation
    run_evaluation("presets", range(1, 7), f"{out}/evaluation", link_levels=True)
    run_evaluation("compositions", range(1, 7), f"{out}/evaluation/compositions", link_levels=True)

    # the architectural showcase is a frozen design review (committed output + screenshots), copied as-is
    src = os.path.join(os.path.dirname(__file__), "..", "showcase")
    if os.path.isfile(os.path.join(src, "index.html")):
        import shutil
        shutil.copytree(src, os.path.join(out, "showcase"), dirs_exist_ok=True)

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
a.card:hover { border-color: var(--accent); }
a.card b { display:block; font-size:17px; margin-bottom:4px; }
a.card span { color: var(--muted); font-size: 14px; }
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
  <a class="card" href="showcase/index.html"><b>Architectural showcase</b><span>Design review: 12 levels (fortress, industrial, underground, compound) with player-height screenshots and an honest verdict. A frozen snapshot, not rebuilt. Then <a href="showcase/step1/index.html">step 1: hall cross-sections</a>, before and after.</span></a>
  <a class="card" href="recipes/index.html"><b>Recipes side by side</b><span>Every preset, two seeds each: compact, classic, bunker, fortress, industrial, compound, explore.</span></a>
  <a class="card" href="compositions/index.html"><b>Compositions side by side</b><span>The four macro structures from above: tree, hub, central, linear, with their measured silhouette.</span></a>
  <a class="card" href="evaluation/index.html"><b>Evaluation: are they distinct?</b><span>Measured: how recognisable each preset is, which ones get confused, how varied each one stays. Also <a href="evaluation/compositions/index.html">for compositions</a>.</span></a>
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
