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
  <a class="card" href="recipes/index.html"><b>Recipes side by side</b><span>Every preset, two seeds each: compact, classic, bunker, fortress, industrial, compound, explore.</span></a>
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
