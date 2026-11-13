"""Architectural showcase: a design review of what the current generator makes.

    python scripts/build_showcase.py showcase        # levels, previews, analysis, viewpoints
    node scripts/showcase_shots.js showcase          # screenshots (needs Playwright + Chromium)
    python scripts/build_showcase.py showcase --page # the page, once the screenshots exist

Rules (so the review stays honest):
  * existing presets only, seeds 1, 2, 3 for every category: no cherry-picking
  * player-height viewpoints are chosen by fixed rules from the level and its analysis,
    never placed by hand: the biggest space from a corner, the highest walkable point
    looking down, the strongest corridor-to-hall reveal, the most interesting sight link
"""
from __future__ import annotations

import html
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from badlevel.analysis import analyze  # noqa: E402
from badlevel.cli import write_level  # noqa: E402
from badlevel.export.preview import map_svg  # noqa: E402
from badlevel.generator import generate  # noqa: E402
from badlevel.recipes import PRESETS, compile_recipe  # noqa: E402
from badlevel.validate import _node_at, flood  # noqa: E402

CATEGORIES = [
    ("fortress", "Gothic fortress", "fortress",
     "The generator has no Gothic vocabulary (no arches, vaults, buttresses or tracery). What it can "
     "offer is the fortress preset: ordered, tall, several storeys climbed by stairwells, an inner "
     "courtyard, a terraced great hall, galleries and arrow slits."),
    ("industrial", "Industrial complex", "industrial",
     "The industrial preset: big tall halls, a 'works' mega-room with decks and catwalks, a loading yard, "
     "lifts and ladders, wide service corridors, control-room glass, clutter. Hub composition."),
    ("underground", "Underground facility", "bunker",
     "There is no notion of 'below ground' (no terrain, no depth). 'Underground' can only come from the "
     "bunker preset's character: low ceilings, narrow orthogonal corridors, no sky, firing slits, ladders "
     "and lifts between levels. Spine composition."),
    ("compound", "Open compound", "compound",
     "The compound preset: courtyards under the sky, a hangar mega-room, large merged halls, wide "
     "connections, many windows, one storey, organised around its big spaces. There is no terrain or "
     "real exterior yet: yards are walled open areas."),
]
SEEDS = (1, 2, 3)
EYE = 46


def gl(x, y, z):
    """map units (Z up) -> glTF/three.js (Y up, metres)."""
    return [round(x / 32, 3), round(z / 32, 3), round(-y / 32, 3)]


def _room_center(level, room, frac=0.35):
    cx, cy = room.center()
    return gl(cx * level.cell, cy * level.cell, room.floor + (room.ceil - room.floor) * frac)


def _corner_eye(level, occ, room, cells, target_z):
    """Farthest cell from the room centre that has a free cell on every side (so the camera isn't
    pressed into a wall or a nook) and actually sees the centre. Falls back to the farthest cell."""
    import numpy as np
    s = level.cell
    cx, cy = room.center()
    cs = set(cells)
    order = sorted(cells, key=lambda c: -((c[0] + 0.5 - cx) ** 2 + (c[1] + 0.5 - cy) ** 2))
    for c in order:
        if not all((c[0] + dx, c[1] + dy) in cs for dx in (-1, 0, 1) for dy in (-1, 0, 1)):
            continue
        a = np.array([[(c[0] + 0.5) * s, (c[1] + 0.5) * s, room.floor + EYE]])
        b = np.array([[cx * s, cy * s, target_z]])
        if occ.visible(a, b)[0]:
            return c
    return order[0] if order else None


def viewpoints(level, report):
    """Rule-based player-height shots. Returns [{name, caption, eye, target}] (glTF coords)."""
    s = level.cell
    start = _node_at(level, next(e for e in level.entities if e.kind == "player_start").pos)
    reach = flood(level, start, set())
    shots = []
    rooms = [r for r in level.rooms if not r.is_connector]
    import numpy as np
    from badlevel.analysis.occupancy import Occupancy
    occ = Occupancy(level)

    # 1. the biggest space, from its farthest walkable corner on the base floor
    big = max(rooms, key=lambda r: len(list(r.cells())) * (r.ceil - r.floor))
    cx, cy = big.center()
    base = [c for c in big.cells() if c not in level.diag
            and any((c[0], c[1], i) in reach and sp[0] == big.floor
                    for i, sp in enumerate(level.spans.get(c, [])))]
    if base:
        c = _corner_eye(level, occ, big, base, big.floor + 0.35 * (big.ceil - big.floor))
        shots.append({"name": "biggest", "caption": f"The biggest space: a {big.kind}"
                      + (f" ({big.meta.get('style')})" if big.meta.get("style") else "")
                      + f", {big.w}×{big.h} m, {(big.ceil - big.floor) / 32:.0f} m high, seen from a corner",
                      "eye": gl((c[0] + 0.5) * s, (c[1] + 0.5) * s, big.floor + EYE),
                      "target": _room_center(level, big)})

    # 2. the highest walkable point inside a room (not a stair/lift shaft) that actually SEES the
    #    room's middle from at least 6 m away, looking down into it
    cands = []
    for (x, y, i) in reach:
        f = level.spans[(x, y)][i][0]
        owner = level.owner_at((x, y), f) or ""
        if not owner.startswith("room:"):
            continue
        room = level.rooms[int(owner[5:])]
        if room.is_connector or f - room.floor < 96:
            continue
        cands.append((f - room.floor, (x, y), f, room))
    best = None
    for rise, c, f, room in sorted(cands, key=lambda t: -t[0])[:400]:
        tx, ty = room.center()
        if (c[0] + 0.5 - tx) ** 2 + (c[1] + 0.5 - ty) ** 2 < 36:
            continue
        a = np.array([[(c[0] + 0.5) * s, (c[1] + 0.5) * s, f + EYE]])
        b = np.array([[tx * s, ty * s, room.floor + EYE]])
        if occ.visible(a, b)[0]:
            best = (rise, c, f, room)
            break
    if best:
        rise, c, f, room = best
        tx, ty = room.center()
        shots.append({"name": "overlook", "caption": f"Highest walkable point: {rise / 32:.1f} m up in the "
                      f"{room.kind}, looking down into it",
                      "eye": gl((c[0] + 0.5) * s, (c[1] + 0.5) * s, f + EYE),
                      "target": gl(tx * s, ty * s, room.floor)})

    # 3. the strongest reveal: from inside the approach corridor into the big space
    for t in report.get("transitions", [])[:1]:
        corr = level.corridors[t["corridor"]]
        room = level.rooms[t["room"]]
        path = corr.path if corr.b == room.id else corr.path[::-1]
        floors = corr.floors if corr.b == room.id else corr.floors[::-1]
        # where the corridor enters the hall; the camera looks along the corridor's last stretch,
        # as a player arrives (not at the hall's centre, which is often off the axis)
        m = next((j for j, c in enumerate(path) if room.contains(*c)), len(path) - 1)
        # stand between any door and the hall (doors open in the game; in a screenshot they're a wall)
        k = max(0, m - 5)
        for d in level.entities:
            if d.kind in ("door", "secret_door") and d.props.get("corridor") == corr.id:
                dc = (int(d.pos[0] // s), int(d.pos[1] // s))
                near = min(range(len(path)), key=lambda j: abs(path[j][0] - dc[0]) + abs(path[j][1] - dc[1]))
                k = max(k, near + 2)
        k = max(0, min(k, m - 2))
        (x, y), f = path[k], floors[k]
        a, b = path[max(0, m - 4)], path[max(0, m - 1)]
        dx, dy = b[0] - a[0], b[1] - a[1]
        n = max(1e-6, (dx * dx + dy * dy) ** 0.5)
        tx, ty = b[0] + 0.5 + dx / n * 12, b[1] + 0.5 + dy / n * 12
        target = gl(tx * s, ty * s, f + EYE + 48) if (dx or dy) else _room_center(level, room, 0.25)
        shots.append({"name": "reveal", "caption": f"The strongest reveal, looking along the corridor: a "
                      f"{t['corridor_m'][0]:.0f} m wide, {t['corridor_m'][1]:.1f} m high corridor opening into the "
                      f"{room.kind} ({t['contrast']:.0f}× its cross-section)",
                      "eye": gl((x + 0.5) * s, (y + 0.5) * s, f + EYE), "target": target})

    # 5 (listed before 4). a courtyard, if the level has one: open sky is a focus of the review
    yards = [r for r in rooms if r.kind == "courtyard"]
    if yards:
        yard = max(yards, key=lambda r: len(list(r.cells())))
        ycx, ycy = yard.center()
        ybase = [c for c in yard.cells() if c not in level.diag and any(
            (c[0], c[1], i) in reach and sp[0] == yard.floor for i, sp in enumerate(level.spans.get(c, [])))]
        if ybase:
            c = _corner_eye(level, occ, yard, ybase, yard.floor + 0.45 * (yard.ceil - yard.floor))
            shots.append({"name": "yard", "caption": f"The largest courtyard: {yard.w}×{yard.h} m, open to the sky "
                          f"at {(yard.ceil - yard.floor) / 32:.0f} m ({', '.join(yard.features) or 'nothing in it'})",
                          "eye": gl((c[0] + 0.5) * s, (c[1] + 0.5) * s, yard.floor + EYE),
                          "target": _room_center(level, yard, 0.45)})

    # 6. where an underground level meets the surface: from inside the entrance, looking up at the sky
    for r in rooms:
        if r.meta.get("surface"):
            base = [c for c in r.cells() if c not in level.diag and any(
                (c[0], c[1], i) in reach and sp[0] == r.floor for i, sp in enumerate(level.spans.get(c, [])))]
            if base:
                c = _corner_eye(level, occ, r, base, r.floor + 0.8 * (r.ceil - r.floor))
                cx, cy = r.center()
                shots.append({"name": "surface", "caption": f"The way in from the surface: the {r.kind} room is "
                              f"open to the sky, {(r.ceil - r.floor) / 32:.0f} m up; everything after it is underground",
                              "eye": gl((c[0] + 0.5) * s, (c[1] + 0.5) * s, r.floor + EYE),
                              "target": gl(cx * s, cy * s, r.ceil + 160)})
            break

    # 7. an overlook: from the observation booth, through its window, down into the hall below
    for r in level.rooms:
        if r.kind == "overlook" and r.meta.get("overlooks") is not None:
            hall = level.rooms[r.meta["overlooks"]]
            hx, hy = hall.center()
            # the booth cell nearest the hall's centre, i.e. at the window
            c = min(r.cells(), key=lambda c: (c[0] + 0.5 - hx) ** 2 + (c[1] + 0.5 - hy) ** 2)
            shots.append({"name": "overlook_window", "caption": f"Seen from above before you get there: the "
                          f"{hall.kind} through the observation window, {(r.floor - hall.floor) / 32:.0f} m up",
                          "eye": gl((c[0] + 0.5) * s, (c[1] + 0.5) * s, r.floor + EYE),
                          "target": gl(hx * s, hy * s, hall.floor)})
            break

    # 8. a compound's rooftop: from a building's roof (reached by its ladder), over the yard
    for r in rooms:
        bs = [b for b in r.meta.get("buildings", []) if b.get("ladder")]
        if r.kind == "courtyard" and bs:
            x0, y0, bw, bh = bs[0]["rect"]
            cx, cy = r.center()
            # the roof corner farthest from the yard's middle, looking across it
            corner = max(((x0, y0), (x0 + bw - 1, y0), (x0, y0 + bh - 1), (x0 + bw - 1, y0 + bh - 1)),
                         key=lambda c: (c[0] + 0.5 - cx) ** 2 + (c[1] + 0.5 - cy) ** 2)
            shots.append({"name": "rooftop", "caption": f"From a rooftop in the yard ({(bs[0]['top'] - r.floor) / 32:.0f} m "
                          f"up, reached by its ladder): {len(r.meta['buildings'])} buildings and the roofs around",
                          "eye": gl((corner[0] + 0.5) * s, (corner[1] + 0.5) * s, bs[0]["top"] + EYE),
                          "target": gl(cx * s, cy * s, r.floor + 64)})
            break

    # 9. the level's landmark, from the far side of its room
    for r in rooms:
        lm = r.meta.get("landmark")
        if lm:
            lx, ly = lm["centre"]
            base = [c for c in r.cells() if c not in level.diag and any(
                (c[0], c[1], i) in reach and sp[0] == r.floor for i, sp in enumerate(level.spans.get(c, [])))]
            if base:
                far = sorted(base, key=lambda c: -((c[0] - lx) ** 2 + (c[1] - ly) ** 2))[:40]
                tz = (r.floor + lm["top"]) / 2
                def sees(c):     # a clear line to the landmark's near side (its centre is solid)
                    ex, ey = (c[0] + 0.5) * s, (c[1] + 0.5) * s
                    dx, dy = ex - lx * s, ey - ly * s
                    n = max(1e-6, (dx * dx + dy * dy) ** 0.5)
                    off = 2 * lm.get("scale", 1) + 1.2      # just outside its footprint
                    return occ.visible(np.array([[ex, ey, r.floor + EYE]]),
                                       np.array([[lx * s + dx / n * off * s, ly * s + dy / n * off * s, tz]]))[0]
                pick = next((c for c in far if sees(c)), far[0])
                shots.append({"name": "landmark", "caption": f"The landmark: a {lm['shape']} in the {r.kind}, "
                              f"{(lm['top'] - r.floor) / 32:.0f} m tall",
                              "eye": gl((pick[0] + 0.5) * s, (pick[1] + 0.5) * s, r.floor + EYE),
                              "target": gl(lx * s, ly * s, tz)})
            break

    # 4. the most interesting sight link: across storeys first, then the longest
    links = report.get("visual", {}).get("links", [])
    if links:
        lk = max(links, key=lambda l: (l["cross_storey"], l["through_glass"],
                                       sum((a - b) ** 2 for a, b in zip(l["from"], l["to"]))))
        shots.append({"name": "view", "caption": f"A view between areas you can't walk between directly: "
                      f"{lk['a']} → {lk['b']}" + (" (across storeys)" if lk["cross_storey"] else "")
                      + (" (through glass)" if lk["through_glass"] else ""),
                      "eye": gl(*lk["from"]), "target": gl(*lk["to"])})

    # 5. room options (D32, D34), when a level has them: a dome or a round room from a corner,
    #    a spiral stair from its bottom door
    dome = next((r for r in rooms if r.meta.get("arch", {}).get("profile") == "dome"), None)
    shaped = dome or next((r for r in rooms if r.shape in ("round", "hex")), None)
    if shaped:
        base = [c for c in shaped.cells() if c not in level.diag
                and any((c[0], c[1], i) in reach and sp[0] == shaped.floor
                        for i, sp in enumerate(level.spans.get(c, [])))]
        if base:
            frac = 0.75 if dome else 0.35
            c = _corner_eye(level, occ, shaped, base, shaped.floor + frac * (shaped.ceil - shaped.floor))
            what = "A dome" if dome else f"A {shaped.shape} room"
            shots.append({"name": "shape", "caption": f"{what}: the {shaped.kind}, {shaped.w}×{shaped.h} m"
                          + (f", {shaped.shape} plan" if dome else "") + ", seen from its edge",
                          "eye": gl((c[0] + 0.5) * s, (c[1] + 0.5) * s, shaped.floor + EYE),
                          "target": _room_center(level, shaped, frac)})
    spiral = next((r for r in level.rooms if r.kind == "spiral"), None)
    if spiral:
        import math
        cx, cy = (spiral.x + spiral.w / 2) * s, (spiral.y + spiral.h / 2) * s
        a = spiral.meta["start"] * 2 * math.pi
        shots.append({"name": "spiral", "caption": f"A spiral stair: {spiral.meta['turns']} turn(s) round a newel, "
                      f"{(spiral.top_floor - spiral.floor) / 32:.0f} m up, from the bottom door",
                      "eye": gl(cx + math.cos(a) * 4.4 * s, cy + math.sin(a) * 4.4 * s, spiral.floor + EYE),
                      "target": gl(cx - math.sin(a) * 3 * s, cy + math.cos(a) * 3 * s, spiral.floor + 160)})
    return shots


def summary(level, report):
    st = level.stats()
    lv = report["level"]
    megas = [r for r in level.rooms if r.kind == "megaroom"]
    return {
        "rooms": st["rooms"], "storeys": st["storeys"], "composition": level.config.composition,
        "mega": ", ".join(f"{r.meta.get('style')} ({r.w}×{r.h} m, {len(r.meta.get('regions', []))} regions)"
                          for r in megas) or "none",
        "open_areas": st["open_areas"], "halls": st["halls"], "windows": st["windows"],
        "galleries": st["galleries"], "connectors": ", ".join(f"{v} {k}" for k, v in st["connectors"].items() if v) or "none",
        "drops": st["drops"], "loops": lv["loops"], "landmarks": len(report["visual"]["landmarks"]),
        "cross_storey_views": lv["cross_storey_views"], "previewed": lv["previewed_share"],
        "longest_monotone": lv["longest_monotone_corridor"], "strongest_release": lv["strongest_release"],
        "exposure": lv["exposure_share"], "height_range_m": round((st["z_range"][1] - st["z_range"][0] - 64) / 32, 1),
        "flags": [f"{f['severity']}: {f['message']}" for f in report["flags"]],
        "architecture": arch_summary(level),
        "flat_walls": flat_walls(level),
    }


def flat_walls(level) -> list:
    """[longest flat wall in the level, mean per room] in metres (walls.py)."""
    from badlevel.walls import flat_wall_runs
    runs = [flat_wall_runs(level, r) for r in level.rooms if not r.is_connector and r.kind != "courtyard"]
    return [max(runs), round(sum(runs) / len(runs), 1)] if runs else [0, 0]


def arch_summary(level) -> str:
    """Which cross-sections the level got (architecture.py), grand space first."""
    from collections import Counter
    shaped = [r for r in level.rooms if "arch" in r.meta]
    if not shaped:
        return "none (plain boxes)"
    grand = [r for r in shaped if r.meta["arch"]["tier"] == "grand"]
    parts = [f'{r.meta["arch"]["profile"]} {r.kind} ({r.meta["arch"]["piers"]} piers)' for r in grand]
    rest = Counter(r.meta["arch"]["profile"] for r in shaped if r not in grand)
    if rest:
        parts.append(", ".join(f"{k} ×{v}" for k, v in rest.most_common()))
    plain = sum(1 for r in level.rooms if not r.is_connector and r.kind not in ("courtyard",) and "arch" not in r.meta)
    return "; ".join(parts) + (f"; {plain} plain" if plain else "")


OPTIONS = ()              # (spaciousness, variety): the room options showcase
STYLE_OVERRIDE = {}       # category key -> arch_style (the styles showcase: one layout, every style)

STYLE_NOTES = {
    "none": "No architecture: the level as the layout makes it (boxes).",
    "gothic": "Pointed vaults, basilicas, undercrofts; shafts, lancet niches, an apse; pointed portals.",
    "industrial": "Portal frames, trusses, sawtooth and multispan roofs; frame pilasters, service bays, crane "
                  "corbels; lintelled openings.",
    "bunker": "Ribbed slabs, mushroom piers, low barrels; counterforts, shelters, a blast door; blast frames.",
    "compound": "Gables, sheds, portal frames; pilasters, window bays, a bay window; round arches.",
    "egyptian": "A hypostyle hall with a processional aisle, stone lintels, corbelled ceilings, battered walls; "
                "false-door niches, a sanctuary at the end of the axis; massive lintels and pylon-thick gates.",
    "scifi": "Chamfered station modules, hull frames, a lit spine; thin frames, panel bays with consoles, a "
             "bulkhead door; door frames with their corners cut.",
    "castle": "A trussed great hall with tie-beams, timber joists, barrel vaults; buttresses, deep window "
              "embrasures, a fireplace; round arches.",
}


def use_styles(preset: str, seed: int):
    """Switch the showcase to the styles view: one layout (preset, seed), in every style."""
    global CATEGORIES, SEEDS
    from badlevel.architecture import STYLES
    CATEGORIES = [(st, st.capitalize() if st != "scifi" else "Sci-fi station", preset, STYLE_NOTES.get(st, ""))
                  for st in ("none",) + STYLES]
    SEEDS = (seed,)
    STYLE_OVERRIDE.clear()
    STYLE_OVERRIDE.update({st: ("" if st == "none" else st) for st, *_ in CATEGORIES})


def build(out: str):
    os.makedirs(out, exist_ok=True)
    index = []
    for key, title, preset, note in CATEGORIES:
        for seed in SEEDS:
            cfg = compile_recipe(PRESETS[preset], seed)
            if key in STYLE_OVERRIDE:
                cfg.arch_style = STYLE_OVERRIDE[key]
            if OPTIONS:                                  # --options 0.8:1 (spaciousness, variety)
                from badlevel.recipes import roomier, varied
                roomier(cfg, OPTIONS[0])
                varied(cfg, OPTIONS[1])
            level = generate(cfg)
            report = analyze(level)
            stem = f"{key}_{seed}"
            write_level(level, out, ["html", "map", "glb", "analysis"], stem=stem, analysis=report)
            entry = {"key": key, "title": title, "preset": preset, "seed": seed, "stem": stem,
                     "shots": viewpoints(level, report), "summary": summary(level, report),
                     "map_svg": map_svg(level)}
            index.append(entry)
            print(f"{stem}: {len(entry['shots'])} viewpoints, {entry['summary']['rooms']} rooms", flush=True)
    with open(os.path.join(out, "showcase.json"), "w") as fh:
        json.dump(index, fh)


def _stamp() -> str:
    import datetime
    import subprocess
    from badlevel import __version__
    try:
        sha = subprocess.check_output(["git", "rev-parse", "--short", "HEAD"], text=True).strip()
    except Exception:
        sha = "local"
    return f"{datetime.date.today().isoformat()} by BadLevel {__version__} (generator at commit {sha})"


def page(out: str, review_md: str = "", before: str = None, page_title: str = None):
    """The showcase page. With ``before`` (an earlier showcase directory with the same levels),
    every screenshot is shown next to its predecessor, from the same camera rule."""
    index = json.load(open(os.path.join(out, "showcase.json")))
    rel = os.path.relpath(before, out) if before else None
    old = {e["stem"]: e for e in json.load(open(os.path.join(before, "showcase.json")))} if before else {}

    def fig(e, sh):
        cap = html.escape(sh["caption"])
        img = f'<img loading="lazy" src="{e["stem"]}_{sh["name"]}.jpg" alt="{cap}">'
        if rel and os.path.exists(os.path.join(before, f'{e["stem"]}_{sh["name"]}.jpg')):
            return (f'<figure class="pair"><div><img loading="lazy" src="{rel}/{e["stem"]}_{sh["name"]}.jpg" '
                    f'alt="before"><span>before</span></div><div>{img}<span>after</span></div>'
                    f'<figcaption>{cap}</figcaption></figure>')
        return f'<figure>{img}<figcaption>{cap}{" (new viewpoint)" if rel else ""}</figcaption></figure>' 
    esc = html.escape
    sections = []
    for key, title, preset, note in CATEGORIES:
        cards = []
        for e in [e for e in index if e["key"] == key]:
            sm = e["summary"]
            shots = "".join(fig(e, sh) for sh in e["shots"])
            fw = sm.get("flat_walls")
            rows = [("architecture", sm.get("architecture", "none (plain boxes)"))] + \
                ([("longest flat wall: in the level / mean per room", f"{fw[0]} m / {fw[1]} m")] if fw else []) + [
                    ("composition", sm["composition"]), ("storeys / height range", f'{sm["storeys"]} / {sm["height_range_m"]} m'),
                    ("rooms", sm["rooms"]), ("mega-room", sm["mega"]), ("open areas / merged halls", f'{sm["open_areas"]} / {sm["halls"]}'),
                    ("windows / galleries", f'{sm["windows"]} / {sm["galleries"]}'), ("vertical connectors / drops", f'{sm["connectors"]} / {sm["drops"]}'),
                    ("loops", sm["loops"]), ("landmarks / cross-storey views", f'{sm["landmarks"]} / {sm["cross_storey_views"]}'),
                    ("critical path seen 2+ rooms ahead", f'{sm["previewed"]:.0%}'),
                    ("longest stretch where nothing changes", f'{sm["longest_monotone"]} m'),
                    ("strongest reveal", f'{sm["strongest_release"]:.0f}×' if sm["strongest_release"] else "–"),
                    ("exposure protected / moderate / exposed", " / ".join(f'{v:.0%}' for v in sm["exposure"].values()))]
            table = "".join(f"<tr><td>{esc(str(k))}</td><td>{esc(str(v))}</td></tr>" for k, v in rows)
            flags = "".join(f"<li>{esc(f)}</li>" for f in sm["flags"][:8])
            cards.append(f'''<article class="level"><h3>{esc(title)} · seed {e["seed"]} <span>({esc(preset)} preset)</span></h3>
<div class="top"><figure><img loading="lazy" src="{e["stem"]}_overview.jpg" alt="overview"><figcaption>Overview from above</figcaption></figure>
<div class="map">{e["map_svg"]}<p class="muted">Top-down plan: colour = floor height</p></div></div>
<div class="shots">{shots}</div>
<div class="facts"><table>{table}</table><div><b>Analysis flags</b><ul>{flags}</ul></div></div>
<p class="links"><a href="{e["stem"]}.html">Open the interactive 3D preview</a>{f' · <a href="{rel}/{e["stem"]}.html">the same level before</a>' if rel and e["stem"] in old else ""} · <a href="{e["stem"]}.map" download>.map</a> · <a href="{e["stem"]}.glb" download>.glb</a> · <a href="{e["stem"]}.analysis.json" download>analysis</a></p>
</article>''')
        shown = f"From the first review, before the architecture work: {note}" if before else note
        sections.append(f'<section><h2>{esc(title)}</h2><p class="note">{esc(shown)}</p>{"".join(cards)}</section>')
    with open(os.path.join(out, "index.html"), "w") as fh:
        page_html = PAGE if not before else PAGE.replace("<main>", '<main class="compare">')
        if before and not page_title:
            page_title = "architecture, step 1: hall cross-sections"
        if page_title:
            page_html = page_html.replace("architectural showcase</h1>", f"{html.escape(page_title)}</h1>")
        fh.write(page_html.replace("__STAMP__", _stamp()).replace("__REVIEW__", review_md).replace("__SECTIONS__", "".join(sections)))
    print(f"showcase page -> {out}/index.html")


PAGE = """<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>BadLevel Showcase</title>
<style>
:root { --bg:#f6f7f9; --card:#fff; --text:#1d2330; --muted:#5b6475; --line:#d8dce3; --accent:#f97316; }
@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) { --bg:#0f1115; --card:#171a21; --text:#e6e6e6; --muted:#9aa3b2; --line:#2a2f3a; } }
:root[data-theme="dark"] { --bg:#0f1115; --card:#171a21; --text:#e6e6e6; --muted:#9aa3b2; --line:#2a2f3a; }
* { box-sizing:border-box; } body { margin:0; background:var(--bg); color:var(--text); font:15px/1.55 system-ui, -apple-system, Segoe UI, sans-serif; }
main { max-width:1200px; margin:0 auto; padding:28px 16px 60px; }
h1 { font-size:28px; margin:0; } h1 span { color:var(--accent); } h2 { font-size:22px; margin:44px 0 4px; } h3 { font-size:17px; margin:0 0 10px; } h3 span { color:var(--muted); font-weight:500; }
.muted, .note { color:var(--muted); } .note { max-width:900px; }
.review { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:6px 20px; margin:20px 0; }
.level { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:16px; margin:18px 0; }
.top { display:grid; grid-template-columns:2fr 1fr; gap:12px; } .map { background:#0b0d11; border-radius:8px; padding:6px; }
.map svg { width:100%; height:auto; max-height:420px; } .map .bg { fill:#0b0d11; } .map .lbl { font-size:7px; } .map p { margin:4px; font-size:12px; }
figure { margin:0; } img { width:100%; display:block; border-radius:8px; background:#1a1d24; } figcaption { font-size:12.5px; color:var(--muted); margin:4px 2px 0; }
.shots { display:grid; grid-template-columns:repeat(auto-fit, minmax(260px,1fr)); gap:12px; margin:12px 0; }
.compare .shots { grid-template-columns:1fr; } .pair { display:grid; grid-template-columns:1fr 1fr; gap:8px; }
.pair div { position:relative; } .pair span { position:absolute; left:8px; top:6px; background:rgba(0,0,0,.65); color:#fff;
  font-size:12px; padding:1px 7px; border-radius:5px; } .pair figcaption { grid-column:1 / -1; }
.facts { display:grid; grid-template-columns:1fr 1fr; gap:16px; font-size:13px; } .facts td { padding:3px 8px 3px 0; border-bottom:1px solid var(--line); vertical-align:top; }
.facts td:first-child { color:var(--muted); } .facts ul { margin:6px 0; padding-left:18px; } .links a { color:var(--accent); }
@media (max-width: 800px) { .top, .facts { grid-template-columns:1fr; } }
</style></head><body><main>
<h1>Bad<span>Level</span> architectural showcase</h1>
<p class="muted"><b>Frozen snapshot</b> generated __STAMP__; the rest of the site rebuilds on every push, this page does not. A design review of what the generator makes. Existing presets only, seeds 1, 2 and 3 for every
category (no cherry-picking). Player-height viewpoints are chosen by fixed rules from each level and its analysis, not
placed by hand: the biggest space from a corner, the highest walkable point looking down, the strongest
corridor-to-hall reveal, and the most interesting sight link. Screenshots use the preview's flat lighting; the
textures are the blockout grid.</p>
<div class="review">__REVIEW__</div>
__SECTIONS__
</main></body></html>"""


if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else "showcase"
    if "--styles" in sys.argv:                      # --styles fortress:3
        pr, sd = sys.argv[sys.argv.index("--styles") + 1].split(":")
        use_styles(pr, int(sd))
    if "--options" in sys.argv:                     # --options 0.8:1
        sp, va = sys.argv[sys.argv.index("--options") + 1].split(":")
        OPTIONS = (float(sp), float(va))
    if "--page" in sys.argv:
        review = ""
        path = os.path.join(out, "review.html")
        if os.path.exists(path):
            review = open(path).read()
        before = sys.argv[sys.argv.index("--before") + 1] if "--before" in sys.argv else None
        title = sys.argv[sys.argv.index("--title") + 1] if "--title" in sys.argv else None
        page(out, review, before, *([title] if title else []))
    else:
        build(out)
