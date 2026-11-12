"""Distinctness evaluation: do recipes (and compositions) make recognisably different
places, while staying varied inside each one?

For a set of labelled configs (preset x seed, or composition x seed) this generates the
levels, fingerprints them (``fingerprint.py``) and reports:

  accuracy      leave-one-out nearest-centroid: is each level closest to its own label?
  confusion     which labels get mistaken for which
  nearest       the closest pairs of labels: early warning that two presets are drifting together
  variety       spread *inside* each label (mean distance between its levels) vs the distance
                *between* labels. Distinct but varied is the goal: a preset that always makes
                the same level would be easy to recognise and still a failure
  profile       z-scores per label and feature: what makes each label itself
  spaces        the note's questions: biggest space, share of intimate vs expansive rooms,
                exploration (loops, side rooms, secrets), artist usefulness (brush count)

``render_html`` turns the report into a page; ``badlevel evaluate`` runs it from the CLI.
"""
from __future__ import annotations

import html
import math
from collections import defaultdict
from typing import Dict, List, Tuple

from .brushes import build_brushes
from .fingerprint import FEATURES, SILHOUETTE, distance, fingerprint, nearest_centroid_accuracy, normalise
from .generator import GenerationError, generate


def _space_metrics(level) -> Dict[str, float]:
    rooms = [r for r in level.rooms if not r.is_connector]
    areas = [len(list(r.cells())) for r in rooms] or [0]
    vols = [len(list(r.cells())) * (r.ceil - r.floor) / 32.0 for r in rooms] or [0]
    side = sum(1 for r in rooms if r.kind in ("supply", "secret"))
    return {
        "largest_space_m3": max(vols),
        "intimate_share": sum(1 for a in areas if a <= 64) / max(1, len(areas)),
        "expansive_share": sum(1 for a in areas if a >= 300) / max(1, len(areas)),
        "side_rooms": side,
        "secret": float(any(r.kind == "secret" for r in rooms)),
        "brushes": len(build_brushes(level)),
    }


def evaluate_configs(items: List[Tuple[str, object]]) -> dict:
    """items: [(label, GenConfig)]. Returns the report (plain JSON-serialisable data)."""
    labels, prints, spaces, failures = [], [], [], []
    for label, cfg in items:
        try:
            level = generate(cfg)
        except GenerationError as e:
            failures.append({"label": label, "seed": cfg.seed, "error": str(e)[:200]})
            continue
        labels.append(label)
        prints.append(fingerprint(level))
        spaces.append(_space_metrics(level))
    order = list(dict.fromkeys(labels))
    vecs = normalise(prints)
    sil = normalise(prints, SILHOUETTE)

    # confusion (leave one out)
    confusion = {a: {b: 0 for b in order} for a in order}
    for i, (lab, v) in enumerate(zip(labels, vecs)):
        cents = {}
        for l2 in order:
            group = [w for j, (l3, w) in enumerate(zip(labels, vecs)) if l3 == l2 and j != i]
            if group:
                cents[l2] = [sum(c) / len(group) for c in zip(*group)]
        confusion[lab][min(cents, key=lambda l2: distance(v, cents[l2]))] += 1
    per_label = {a: confusion[a][a] / max(1, sum(confusion[a].values())) for a in order}

    groups = {a: [v for l, v in zip(labels, vecs) if l == a] for a in order}
    cent = {a: [sum(c) / len(g) for c in zip(*g)] for a, g in groups.items()}
    pairs = sorted(((distance(cent[a], cent[b]), a, b) for i, a in enumerate(order) for b in order[i + 1:]))

    def within(g):
        d = [distance(x, y) for i, x in enumerate(g) for y in g[i + 1:]]
        return sum(d) / len(d) if d else 0.0
    variety = {a: within(g) for a, g in groups.items()}

    def separation(vs):
        gs = {a: [v for l, v in zip(labels, vs) if l == a] for a in order}
        cs = {a: [sum(c) / len(g) for c in zip(*g)] for a, g in gs.items()}
        btw = [distance(cs[a], cs[b]) for i, a in enumerate(order) for b in order[i + 1:]]
        win = [within(g) for g in gs.values()]
        return round((sum(btw) / len(btw)) / (sum(win) / len(win)), 2) if btw and sum(win) else None
    between = sum(d for d, _, _ in pairs) / max(1, len(pairs))
    mean_within = sum(variety.values()) / max(1, len(variety))

    means = {a: {f: sum(p[f] for l, p in zip(labels, prints) if l == a) / labels.count(a) for f in FEATURES}
             for a in order}
    profile = {a: {f: v for f, v in zip(FEATURES, cent[a])} for a in order}
    space_means = {a: {k: sum(s[k] for l, s in zip(labels, spaces) if l == a) / labels.count(a)
                       for k in spaces[0]} for a in order} if spaces else {}
    return {
        "format": "badlevel-evaluation", "version": 1,
        "labels": order, "levels": len(labels), "failures": failures,
        "features": FEATURES,
        "accuracy": round(nearest_centroid_accuracy(labels, vecs), 3),
        "silhouette_accuracy": round(nearest_centroid_accuracy(labels, sil), 3),
        "per_label_accuracy": {a: round(v, 3) for a, v in per_label.items()},
        "confusion": confusion,
        "nearest": [{"a": a, "b": b, "distance": round(d, 2)} for d, a, b in pairs[:5]],
        "variety": {a: round(v, 2) for a, v in variety.items()},
        "between": round(between, 2),
        "separation": round(between / mean_within, 2) if mean_within else None,
        "silhouette_separation": separation(sil),
        "profile": {a: {f: round(v, 2) for f, v in fs.items()} for a, fs in profile.items()},
        "means": {a: {f: round(v, 3) for f, v in fs.items()} for a, fs in means.items()},
        "spaces": {a: {k: round(v, 2) for k, v in fs.items()} for a, fs in space_means.items()},
    }


def evaluate_presets(seeds=range(1, 7)) -> dict:
    from .recipes import PRESETS, compile_recipe
    return evaluate_configs([(name, compile_recipe(r, s)) for name, r in PRESETS.items() for s in seeds])


def evaluate_compositions(seeds=range(1, 7)) -> dict:
    from .config import GenConfig
    items = []
    for comp in ("tree", "hub", "central", "linear"):
        for s in seeds:
            items.append((comp, GenConfig(seed=s, composition=comp, side_rooms=4, pacing_contrast=True,
                                          mega_rooms=1 if comp == "central" else 0)))
    return evaluate_configs(items)


# ----------------------------------------------------------------------
# HTML
# ----------------------------------------------------------------------
FEATURE_LABEL = {
    "rooms": "rooms", "room_area": "room size", "room_area_cv": "size variation", "ceiling": "ceiling height",
    "corridor_width": "corridor width", "corridor_share": "corridor share", "storeys": "storeys",
    "vertical_spread": "height range", "rect_share": "rectangular rooms", "diagonal_share": "45° walls",
    "hall_share": "merged halls", "windows_per_room": "windows", "glass_share": "glass", "enemies_per_room": "enemies",
    "crate_share": "clutter", "loops": "loops", "lift_ladder_share": "lifts & ladders", "open_area_share": "open sky",
    "mega_share": "mega-rooms", "elongation": "elongation", "centrality": "centrality", "leaf_share": "dead ends",
}


def _diverging(z: float) -> str:
    """z-score -> colour: blue below average, neutral grey at the average, red above (2 hues + grey)."""
    t = max(-1.0, min(1.0, z / 2.0))
    neutral = (128, 128, 128)
    pole = (42, 120, 214) if t < 0 else (227, 73, 72)
    k = abs(t)
    c = tuple(round(a + (b - a) * k) for a, b in zip(neutral, pole))
    return "rgb(%d,%d,%d)" % c


def _sequential(t: float) -> str:
    """0..1 -> light to dark blue (one hue)."""
    lo, hi = (205, 226, 251), (16, 66, 129)
    return "rgb(%d,%d,%d)" % tuple(round(a + (b - a) * t) for a, b in zip(lo, hi))


def render_html(report: dict, title: str, intro: str, thumbs: Dict[str, List[Tuple[str, str]]] = None) -> str:
    labels, feats = report["labels"], report["features"]
    esc = html.escape
    # profile heat table
    head = "".join(f'<th class="rot"><span>{esc(FEATURE_LABEL.get(f, f))}</span></th>' for f in feats)
    rows = []
    for a in labels:
        cells = []
        for f in feats:
            z = report["profile"][a][f]
            m = report["means"][a][f]
            cells.append(f'<td style="background:{_diverging(z)}" title="{esc(a)} · {esc(FEATURE_LABEL.get(f, f))}: '
                         f'{m:.2f} (z {z:+.1f})"></td>')
        rows.append(f'<tr><th>{esc(a)}</th>{"".join(cells)}</tr>')
    heat = f'<table class="heat"><tr><th></th>{head}</tr>{"".join(rows)}</table>'

    # confusion matrix
    crow = []
    for a in labels:
        tot = max(1, sum(report["confusion"][a].values()))
        cells = "".join(
            f'<td style="background:{_sequential(report["confusion"][a][b] / tot)};'
            f'color:{"#fff" if report["confusion"][a][b] / tot > 0.5 else "var(--text)"}" '
            f'title="{esc(a)} levels recognised as {esc(b)}: {report["confusion"][a][b]} of {tot}">'
            f'{report["confusion"][a][b] or ""}</td>' for b in labels)
        crow.append(f'<tr><th>{esc(a)}</th>{cells}<td class="acc">{report["per_label_accuracy"][a]:.0%}</td></tr>')
    conf = (f'<table class="conf"><tr><th>actual \\ guessed</th>{"".join(f"<th>{esc(b)}</th>" for b in labels)}'
            f'<th>recognised</th></tr>{"".join(crow)}</table>')

    # variety bars (single series: no legend; the heading names it)
    vmax = max(list(report["variety"].values()) + [report["between"]]) or 1
    bars = "".join(
        f'<div class="bar"><span class="lab">{esc(a)}</span><span class="track"><span class="fill" '
        f'style="width:{report["variety"][a] / vmax * 100:.0f}%" title="{esc(a)}: {report["variety"][a]:.2f}"></span>'
        f'</span><span class="val">{report["variety"][a]:.2f}</span></div>' for a in labels)
    bars += (f'<div class="bar ref"><span class="lab">between labels</span><span class="track"><span class="fill" '
             f'style="width:{report["between"] / vmax * 100:.0f}%"></span></span>'
             f'<span class="val">{report["between"]:.2f}</span></div>')

    near = "".join(f"<li><b>{esc(n['a'])}</b> ↔ <b>{esc(n['b'])}</b> · distance {n['distance']:.2f}</li>"
                   for n in report["nearest"][:3])
    sp_keys = [("largest_space_m3", "biggest space (m³)", "{:,.0f}"), ("intimate_share", "intimate rooms", "{:.0%}"),
               ("expansive_share", "expansive rooms", "{:.0%}"), ("side_rooms", "side rooms", "{:.1f}"),
               ("secret", "has a secret", "{:.0%}"), ("brushes", "brushes", "{:,.0f}")]
    sp_rows = "".join(f"<tr><th>{esc(a)}</th>" + "".join(f"<td>{fmt.format(report['spaces'][a][k])}</td>"
                                                          for k, _, fmt in sp_keys) + "</tr>" for a in labels)
    spaces = (f'<table class="num"><tr><th></th>{"".join(f"<th>{t}</th>" for _, t, _ in sp_keys)}</tr>'
              f'{sp_rows}</table>') if report.get("spaces") else ""

    strip = ""
    if thumbs:
        strip = "".join(
            f'<section class="strip"><h3>{esc(a)}</h3><div class="thumbs">'
            + "".join(f'<a href="{esc(href)}">{svg}</a>' for href, svg in thumbs.get(a, [])) + "</div></section>"
            for a in labels)

    sep = report["separation"]
    return EVAL_PAGE.replace("__TITLE__", esc(title)).replace("__INTRO__", intro) \
        .replace("__ACC__", f'{report["accuracy"]:.0%}').replace("__SIL__", f'{report["silhouette_accuracy"]:.0%}') \
        .replace("__SEP__", f"{sep:.1f}×" if sep else "–").replace("__N__", str(report["levels"])) \
        .replace("__K__", str(len(labels))).replace("__CHANCE__", f"{1 / max(1, len(labels)):.0%}") \
        .replace("__HEAT__", heat).replace("__CONF__", conf).replace("__BARS__", bars).replace("__NEAR__", near) \
        .replace("__SPACES__", spaces).replace("__STRIP__", strip)


EVAL_PAGE = """<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>BadLevel Evaluation</title>
<style>
:root { --bg:#f6f7f9; --card:#fff; --text:#1d2330; --muted:#5b6475; --line:#d8dce3; --accent:#f97316; --bar:#2a78d6; }
@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) { --bg:#0f1115; --card:#171a21; --text:#e6e6e6; --muted:#9aa3b2; --line:#2a2f3a; --bar:#3987e5; } }
:root[data-theme="dark"] { --bg:#0f1115; --card:#171a21; --text:#e6e6e6; --muted:#9aa3b2; --line:#2a2f3a; --bar:#3987e5; }
* { box-sizing:border-box; } body { margin:0; background:var(--bg); color:var(--text); font:15px/1.5 system-ui, -apple-system, Segoe UI, sans-serif; }
main { max-width:1180px; margin:0 auto; padding:28px 16px 60px; }
h1 { font-size:26px; margin:0 0 4px; } h2 { font-size:18px; margin:30px 0 6px; } h3 { font-size:15px; margin:14px 0 6px; }
.muted, .intro { color:var(--muted); } .card { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:16px; overflow-x:auto; }
.kpis { display:grid; grid-template-columns:repeat(auto-fit, minmax(200px,1fr)); gap:12px; margin:18px 0; }
.kpi b { display:block; font-size:30px; } .kpi span { color:var(--muted); font-size:13px; }
table { border-collapse:collapse; font-size:13px; } th { font-weight:600; text-align:left; padding:4px 8px; white-space:nowrap; }
.heat td { width:30px; height:26px; border:2px solid var(--card); border-radius:4px; }
.heat th.rot { height:120px; vertical-align:bottom; padding:0; } .heat th.rot span { display:inline-block; transform:rotate(-55deg); transform-origin:left bottom; width:26px; white-space:nowrap; font-weight:500; color:var(--muted); }
.conf td { width:46px; height:34px; text-align:center; border:2px solid var(--card); border-radius:4px; font-variant-numeric:tabular-nums; }
.conf td.acc { background:transparent; color:var(--text); font-weight:600; width:auto; padding:0 10px; }
.num td { padding:4px 10px; text-align:right; font-variant-numeric:tabular-nums; border-bottom:1px solid var(--line); }
.bar { display:grid; grid-template-columns:120px 1fr 50px; align-items:center; gap:10px; margin:6px 0; font-size:13px; }
.bar .track { background:var(--line); height:12px; border-radius:6px; overflow:hidden; } .bar .fill { display:block; height:100%; background:var(--bar); border-radius:6px; }
.bar.ref .fill { background:var(--muted); } .bar .val { text-align:right; font-variant-numeric:tabular-nums; color:var(--muted); }
.legend { display:flex; align-items:center; gap:8px; font-size:12px; color:var(--muted); margin-top:8px; }
.legend i { display:inline-block; width:160px; height:10px; border-radius:3px; background:linear-gradient(90deg,#2a78d6,#808080,#e34948); }
.thumbs { display:grid; grid-template-columns:repeat(auto-fill, minmax(150px,1fr)); gap:8px; }
.thumbs a { background:#0b0d11; border-radius:8px; display:block; } .thumbs svg { width:100%; height:130px; display:block; }
.thumbs .bg { fill:#0b0d11; } .thumbs .lbl, .thumbs .sub { display:none; }
</style></head><body><main>
<h1>__TITLE__</h1><p class="intro">__INTRO__</p>
<div class="kpis">
 <div class="card kpi"><b>__ACC__</b><span>levels recognised from their fingerprint (chance __CHANCE__, __K__ labels, __N__ levels)</span></div>
 <div class="card kpi"><b>__SIL__</b><span>recognised from the silhouette alone (macro shape: elongation, centrality, dead ends)</span></div>
 <div class="card kpi"><b>__SEP__</b><span>separation: distance between labels vs spread inside them. Distinct, but not identical.</span></div>
</div>
<h2>What makes each one itself</h2>
<p class="muted">Each cell: how far the label sits from the average on that feature (hover for the value).</p>
<div class="card">__HEAT__<div class="legend">below average <i></i> above average</div></div>
<h2>Which get confused</h2>
<p class="muted">Every level is guessed from its fingerprint against all the others. Off-diagonal numbers are mix-ups.</p>
<div class="card">__CONF__</div>
<h2>Variety inside each label</h2>
<p class="muted">Average fingerprint distance between two levels of the same label, next to the average distance between labels. Bars near zero would mean a label makes the same level every time.</p>
<div class="card">__BARS__</div>
<h2>Closest pairs</h2>
<p class="muted">Labels most likely to drift into each other.</p>
<div class="card"><ul>__NEAR__</ul></div>
<h2>Spaces</h2>
<p class="muted">Biggest space, intimate (≤ 64 m²) vs expansive (≥ 300 m²) rooms, exploration and brush count, averaged per level.</p>
<div class="card">__SPACES__</div>
<h2>The levels</h2>
__STRIP__
</main></body></html>"""
