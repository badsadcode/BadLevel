"""Vistas: big windows in grand halls onto the world outside (the Unreal tradition).

The grand halls of the late-90s shooters had a trick: a wall of glass, and beyond it a sky and a
landscape you would never walk on, so the castle seemed to stand on a mountain in a whole world.
Here, for the biggest, tallest halls (``cfg.vistas`` of them):

* **A site**: a straight, flat stretch of the hall's outer wall with clean rock behind it, deep
  and wide enough for a view (18-26 cells deep, the window plus 8 cells each side), with a cell of
  rock all round it, so nothing else is cut into. Nothing may be stacked above or below it either:
  the view goes up to the sky. Where the grid ends, it grows (east and north).
* **The window**: a glazed pane through the wall (4-12 cells wide, from a 48-unit sill to just
  under the hall's ceiling): see-through, not walkable (``level.glass``, like the other glazed
  windows), a ``window`` entity marked ``vista``.
* **The view**: open to the sky. The ground falls away below the sill (96-256 units down), stays
  flat for a few cells, then rises into hills towards the horizon, higher at the sides, so the
  window frames a valley. The height is ``A(depth) + B(across)``: a sum, so each cell's four
  corners lie on one plane, and the hills are smooth sloped floors (``level.floor_slope``) that
  every exporter already draws. A few lights over it. Named ``vista:N/terrain`` for an artist
  to swap for a real skybox and landscape.

Nobody can get there (glass), so the validator and the player box never see it; the compiler
keeps it because it's in sight through the pane.
"""
from __future__ import annotations

import math
import random
from typing import Optional

from .level import Entity, Level, Room

SIDES = ((1, 0), (-1, 0), (0, 1), (0, -1))
SILL = 48
MARGIN = 8                 # cells of view each side of the window


def _q(v: float, k: int = 8) -> int:
    return int(round(v / k)) * k


def _site(level: Level, room: Room, d, rng: random.Random) -> Optional[dict]:
    """A flat stretch of the room's outer wall on side d with room for a view behind it."""
    along_x = d[1] != 0                         # the wall runs along x when the view faces n/s
    lo_, hi_ = (room.x, room.x + room.w) if along_x else (room.y, room.y + room.h)

    def edge(t):                                 # the room's last cell on side d in row/column t
        cells = [(t, y) for y in range(room.y, room.y + room.h)] if along_x else \
                [(x, t) for x in range(room.x, room.x + room.w)]
        cells = [c for c in cells if room.contains(*c)]
        if not cells:
            return None
        key = (lambda c: c[1]) if along_x else (lambda c: c[0])
        return (max if (d[0] + d[1]) > 0 else min)(cells, key=key)

    mine = f"room:{room.id}/"
    runs, run = [], []
    for t in range(lo_, hi_):
        c = edge(t)
        ok = c is not None and c not in level.diag
        cut = False
        if ok:
            sp = [s for s in level.spans.get(c, ()) if s[0] <= room.floor + 16 and s[1] >= room.floor + SILL + 160]
            if not sp and (level.part_solid.get(c, "") or level.part_cells.get(c, "")).startswith(mine):
                # a pilaster or its base against the wall: the window takes its place; the view
                # from the cell inside it decides how tall
                inner = (c[0] - d[0], c[1] - d[1])
                sp = [s for s in level.spans.get(inner, ()) if s[0] <= room.floor + 16
                      and s[1] >= room.floor + SILL + 160] if room.contains(*inner) else []
                sp = [(room.floor, sp[0][1])] if sp else []
                cut = bool(sp)
            ok = bool(sp)
        w = (c[0] + d[0], c[1] + d[1]) if ok else None
        depth = (w[1] if along_x else w[0]) if ok else None
        if ok and (not run or run[-1][1] == depth):
            run.append((t, depth, c, sp[0], cut))
        else:
            if len(run) >= 6:
                runs.append(run)
            run = [(t, depth, c, sp[0], cut)] if ok else []
    if len(run) >= 6:
        runs.append(run)
    rng.shuffle(runs)
    for run in sorted(runs, key=len, reverse=True):
        inner = run[1:-1]                        # a cell of wall left at each end
        W = min(len(inner), rng.randint(6, 12))
        s0 = (len(inner) - W) // 2
        win = inner[s0:s0 + W]
        head = min(sp[1] for _, _, _, sp, _ in win) - 24
        lo = room.floor + SILL
        # a window bay: the two cells in front of the pane rise to the hall's full height (cut up
        # into the vault), so the window can be as tall as the hall, not as its springing line
        bay = []
        for t, depth, c, sp, cut in win:
            for k in (0, 1):
                b = (c[0] - k * d[0], c[1] - k * d[1])
                if not room.contains(*b) or b in level.diag:
                    bay = None
                    break
                own = [x for x in level.spans.get(b, ()) if x[0] <= room.floor + 16]
                if own and level.spans_in(b, own[0][1], room.ceil + 16) or (not own and not cut):
                    bay = None
                    break
                bay.append(b)
            if bay is None:
                break
        if bay:
            head = room.ceil - 32
        if head - lo < 160:
            continue
        wall_depth = win[0][1]
        sgn = d[0] + d[1]

        def cell(u, v, wall_depth=wall_depth, sgn=sgn):   # u: depth (0 = the wall), v: along the wall
            return (v, wall_depth + sgn * u) if along_x else (wall_depth + sgn * u, v)
        # the full view if it fits, else shallower and narrower ones
        for V, M in ((rng.randint(18, 26), MARGIN), (14, 5), (10, 3)):
            t0, t1 = win[0][0] - M, win[-1][0] + M
            ok = True
            for u in range(0, V + 2):
                for v in range(t0 - 1, t1 + 2):
                    c = cell(u, v)
                    if c[0] < 1 or c[1] < 1 or c in level.spans or c in level.diag or c in level.glass:
                        ok = False
                        break
                if not ok:
                    break
            if ok:
                return {"d": d, "along_x": along_x, "win": win, "lo": lo, "hi": head, "V": V, "bay": bay or [],
                        "t0": t0, "t1": t1, "wall": wall_depth, "sgn": sgn, "cell": cell}
    return None


def _build(level: Level, room: Room, site: dict, k: int, rng: random.Random):
    s = level.cell
    V, t0, t1 = site["V"], site["t0"], site["t1"]
    cell = site["cell"]
    win = site["win"]
    g0 = room.floor - rng.choice([96, 128, 160, 192, 256])          # the ground below the sill
    peak = room.floor + rng.choice([160, 224, 288, 352])            # the far hills, seen above the sill
    flat = rng.randint(3, 5)
    ph, per, amp = rng.uniform(0, 6.28), rng.uniform(6, 11), rng.choice([24, 32, 48])
    wa, wb = win[0][0], win[-1][0] + 1                              # the window's span along the wall
    ridge = rng.uniform(0.25, 0.5)

    def A(u):                                                       # depth profile: flat, then rising
        if u <= flat:
            return g0
        x = (u - flat) / max(1, V + 1 - flat)
        rise = (3 * x * x - 2 * x * x * x)
        return _q(g0 + (peak - g0) * rise + ridge * 40 * math.sin(u * 0.9 + ph))

    def B(v):                                                       # across: waves, and the sides rise
        side = max(0, wa - 1 - v, v - wb - 1)
        return _q(amp * math.sin(v / per * 6.283 + ph) + 26 * side)

    tops = []
    cells = {}
    for u in range(1, V + 1):
        for v in range(t0, t1 + 1):
            c = cell(u, v)
            # the heights at this cell's world corners (x0y0, x1y0, x1y1, x0y1)
            corners = []
            for dx, dy in ((0, 0), (1, 0), (1, 1), (0, 1)):
                X, Y = c[0] + dx, c[1] + dy                 # a world corner
                depth, across = (Y, X) if site["along_x"] else (X, Y)
                uc = depth - (site["wall"] + 1) if site["sgn"] > 0 else site["wall"] - depth   # 0 at the wall
                corners.append(A(uc) + B(across))
            cells[c] = tuple(corners)
            tops.append(max(corners))
    sky = max(max(tops) + 256, room.ceil + 192)
    for c, corners in cells.items():
        f = max(corners)
        level.add_span(c, (f, sky), f"vista:{k}")
        if len(set(corners)) > 1:
            level.floor_slope[c] = corners
        level.sky.add(c)
        level.part_cells[c] = f"vista:{k}/terrain"
    # the rock round the view (not the window wall) is sky, so the view has no edge; only where
    # nothing else is open beyond it, so no room ever sees a sky wall
    for u in range(1, V + 2):
        for v in range(t0 - 1, t1 + 2):
            c = cell(u, v)
            if c in cells:
                continue
            if all(n in cells or n not in level.spans
                   for n in ((c[0] + dx, c[1] + dy) for dx in (-1, 0, 1) for dy in (-1, 0, 1))):
                level.sky_walls.add(c)
    # the pane (and its bay, carved below, after any pilaster in the way has gone)
    lo, hi = site["lo"], site["hi"]
    panes = []
    for t, depth, c, sp, cut in win:
        if cut:                                   # the pilaster (or base) gives way to the window
            gone = level.spans_in(c, room.floor - 16, sp[1])
            top = max([sp[1]] + [g[1] for g in gone])
            level.replace_in(c, room.floor - 16, sp[1], [(room.floor, top)])
            if not any(o[2] == f"room:{room.id}" for o in level.owners.get(c, [])):
                level.owners.setdefault(c, []).append((room.floor, top, f"room:{room.id}"))
            level.part_solid.pop(c, None)
            level.part_cells.pop(c, None)
        w = (c[0] + site["d"][0], c[1] + site["d"][1])
        level.add_span(w, (lo, hi))
        level.owners.setdefault(w, []).insert(0, (lo, hi, f"window:vista{k}"))
        level.glass.add(w)
        panes.append(w)
    for b in site.get("bay", ()):                # the bay: up to the hall's ceiling, flat
        own = [x for x in level.spans.get(b, ()) if x[0] <= room.floor + 16]
        if own and own[0][1] < room.ceil:
            level.replace_in(b, own[0][0], own[0][1], [(own[0][0], room.ceil)])
            level.owners[b] = [(lo_, room.ceil if name == f"room:{room.id}" else hi_, name)
                               for lo_, hi_, name in level.owners.get(b, [])]
        level.ceil_slope.pop(b, None)
        level.part_ceiling[b] = f"room:{room.id}/window bay"
    xs, ys = [p[0] for p in panes], [p[1] for p in panes]
    b_lo, b_hi = (min(xs) * s, min(ys) * s, lo), ((max(xs) + 1) * s, (max(ys) + 1) * s, hi)
    level.entities.append(Entity("window", tuple((a + b) / 2 for a, b in zip(b_lo, b_hi)),
                                 {"glazed": True, "vista": k, "areas": [f"room:{room.id}", f"vista:{k}"]},
                                 (b_lo, b_hi)))
    # light over the view (Quake's sky doesn't light it)
    for u in (V // 3, 2 * V // 3):
        for v in (t0 + (t1 - t0) // 4, t0 + 3 * (t1 - t0) // 4):
            c = cell(u, v)
            level.entities.append(Entity("light", ((c[0] + 0.5) * s, (c[1] + 0.5) * s, sky - 96), {"intensity": 450}))
    xs_all = [c[0] for c in cells]
    ys_all = [c[1] for c in cells]
    level.width = max(level.width, max(xs_all) + 3)
    level.height = max(level.height, max(ys_all) + 3)
    room.meta["vista"] = {"id": k, "side": list(site["d"]), "window": len(win), "height": hi - lo,
                          "depth": V, "ground_drop": room.floor - g0, "hills": peak - room.floor}
    room.features.append("vista")


def add_vistas(level: Level) -> int:
    cfg = level.config
    n = getattr(cfg, "vistas", 0)
    if n <= 0:
        return 0
    rng = random.Random(f"{cfg.seed}/{cfg.variant}/vista")
    rooms = [r for r in level.rooms if not r.is_connector and r.kind not in ("courtyard", "secret", "overlook")
             and r.ceil - r.floor >= 256 and min(r.w, r.h) >= 10]
    rooms.sort(key=lambda r: -(r.w * r.h * (r.ceil - r.floor)))
    made = 0
    for room in rooms:
        if made >= n:
            break
        sides = list(SIDES)
        rng.shuffle(sides)
        for d in sides:
            site = _site(level, room, d, rng)
            if site:
                _build(level, room, site, made, rng)
                made += 1
                break
    if made:
        level.notes.append(f"vistas: {made}")
    return made
