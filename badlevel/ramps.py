"""Ramps: a stair corridor's steps become one smooth slope.

A corridor that changes height climbs one step (16 units) per cell. With ``cfg.ramps`` (a chance
per corridor, 0 by default: nothing changes), each such cell's floor slopes from the step below up
to its own height, so the run reads as a ramp, not a stair. The cell keeps its own (higher) floor
as its span: everything that walks the level (the validator, the player box of clearance.py, the
analysis) sees exactly what it saw before, and a 16-in-32 slope (27 degrees) is easy walking in
Quake (anything under about 45 degrees is).

Only plain cases are turned: the corridor's span is the bottom one of the cell, the cell behind is
one step lower and the cell ahead level or one step higher (both the same corridor's), and the
cells beside it are level with it or walls, so the whole width of the corridor rises together.
"""
from __future__ import annotations

import random
from typing import Optional, Tuple

from .level import Cell, Level

D4 = ((1, 0), (-1, 0), (0, 1), (0, -1))


def _corridor_floor(level: Level, c: Cell, owner: str) -> Optional[int]:
    for sp in level.spans.get(c, ()):
        if level.owner_of(c, sp) == owner:
            return sp[0]
    return None


def _corners(c: Cell, d: Tuple[int, int], low: int, high: int):
    """Corner heights (x0y0, x1y0, x1y1, x0y1) of a cell rising in direction d from low to high."""
    out = []
    for dx, dy in ((0, 0), (1, 0), (1, 1), (0, 1)):
        ahead = (d[0] == 1 and dx == 1) or (d[0] == -1 and dx == 0) or \
                (d[1] == 1 and dy == 1) or (d[1] == -1 and dy == 0)
        out.append(high if ahead else low)
    return tuple(out)


def make_ramps(level: Level) -> int:
    cfg = level.config
    chance = getattr(cfg, "ramps", 0.0)
    if chance <= 0:
        return 0
    step = cfg.step_height
    made = 0
    s = level.cell
    ents = {}
    for e in level.entities:
        if not getattr(e, "bounds", None):
            ents.setdefault((int(e.pos[0] // s), int(e.pos[1] // s)), []).append(e)
    by_owner = {}
    for c, os in level.owners.items():
        for o in os:
            by_owner.setdefault(o[2], []).append(c)
    for corr in level.corridors:
        if random.Random(f"{cfg.seed}/{cfg.variant}/ramp/{corr.id}").random() >= chance:
            continue
        owner = f"corridor:{corr.id}"
        for c in sorted(by_owner.get(owner, ())):
            if c in level.diag or c in level.floor_slope:
                continue
            spans = level.spans.get(c)
            if not spans or level.owner_of(c, spans[0]) != owner:
                continue
            f = spans[0][0]
            dirs = []
            for d in D4:
                back = _corridor_floor(level, (c[0] - d[0], c[1] - d[1]), owner)
                ahead = _corridor_floor(level, (c[0] + d[0], c[1] + d[1]), owner)
                if back == f - step and ahead in (f, f + step):
                    dirs.append(d)
            if len(dirs) != 1:
                continue
            d = dirs[0]
            side = [(c[0] + d[1], c[1] + d[0]), (c[0] - d[1], c[1] - d[0])]
            if any(_corridor_floor(level, n, owner) not in (None, f) for n in side):
                continue
            level.floor_slope[c] = _corners(c, d, f - step, f)
            made += 1
            # the ceiling follows, parallel, where nothing is stacked above (its span then keeps the
            # lowest point, one step under where it was: still the corridor's headroom less 16)
            if len(spans) == 1 and c not in level.ceil_slope and c not in level.sky:
                top = spans[0][1]
                if top - step - f >= cfg.player_height + 32:
                    level.replace_in(c, f, top, [(f, top - step)])
                    level.ceil_slope[c] = _corners(c, d, top - step, top)
                    for e in ents.get(c, ()):               # a light under the old ceiling comes down too
                        if e.pos[2] > top - step - 16:
                            e.pos = (e.pos[0], e.pos[1], top - step - 16)
    if made:
        level.notes.append(f"ramps: {made} cells")
    return made
