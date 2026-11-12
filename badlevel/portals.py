"""Portals: how a corridor opens into a room, framed so the arrival means something.

Step 3 of the architecture plan. Where a corridor meets a room of a styled level:

  jambs   the room cells at both ends of the opening become solid members, so the opening
          reads as a doorway in a wall rather than a gap (bunkers: two deep, a blast frame)
  head    in grand and major rooms the mouth (the corridor's last cell before the room)
          rises above the corridor into the wall: a pointed arch (gothic), a round arch
          (compound), a tall opening under a heavy lintel (industrial). From the low
          corridor you look through a tall frame into the big space

This runs after every room and corridor is populated (decorate's last step), so it sees
the final geometry, every entity, and every door: a mouth that holds a door is never
raised (a locked door must still seal its corridor), and a jamb never lands on an entity
or a raised feature. Raising only cuts into solid rock with one solid cell kept around it,
like a wall recess. The validator still checks the result.

The other half of a framed reveal, putting the hall's end feature on the axis of the way
in, happens earlier in ``walls.py`` (it needs to run before the room's features).
"""
from __future__ import annotations

import random
from typing import Dict, List, Optional, Tuple

from .architecture import _base_connected, pointed, q, round_arch
from .level import Cell, Level, Room

DIRS = ((1, 0), (-1, 0), (0, 1), (0, -1))


def openings(level: Level, room: Room) -> List[dict]:
    """Where corridors meet the room at its floor: runs of room cells along one wall.
    Each: {"cells": [room cells, in order], "out": (dx, dy), "corridor": id, "span": (f, top)}."""
    groups: Dict[tuple, List[Cell]] = {}
    spans_of: Dict[tuple, tuple] = {}
    for c in room.cells():
        if room.is_diag(*c):
            continue
        for d in DIRS:
            n = (c[0] + d[0], c[1] + d[1])
            if room.contains(*n):
                continue
            for sp in level.spans_in(n, room.floor, room.ceil):
                owner = level.owner_of(n, sp) or ""
                if owner.startswith("corridor:") and sp[0] == room.floor:
                    key = (d, int(owner[9:]))
                    groups.setdefault(key, []).append(c)
                    spans_of[key] = sp
    out = []
    for (d, cid), cells in groups.items():
        along = (lambda c: c[1]) if d[0] else (lambda c: c[0])
        line = (lambda c: c[0]) if d[0] else (lambda c: c[1])
        cells = sorted(set(cells), key=lambda c: (line(c), along(c)))
        run = [cells[0]]
        for c in cells[1:]:
            if line(c) == line(run[-1]) and along(c) == along(run[-1]) + 1:
                run.append(c)
            else:
                out.append({"cells": run, "out": d, "corridor": cid, "span": spans_of[(d, cid)]})
                run = [c]
        out.append({"cells": run, "out": d, "corridor": cid, "span": spans_of[(d, cid)]})
    return out


def _entity_cells(level: Level) -> set:
    s = level.cell
    cells = set()
    for e in level.entities:
        cells.add((int(e.pos[0] // s), int(e.pos[1] // s)))
        if e.bounds:
            (x0, y0, _), (x1, y1, _) = e.bounds
            for x in range(int(x0 // s), int((x1 - 1) // s) + 1):
                for y in range(int(y0 // s), int((y1 - 1) // s) + 1):
                    cells.add((x, y))
    return cells


def _door_cells(level: Level) -> set:
    s = level.cell
    cells = set()
    for e in level.entities:
        if e.kind in ("door", "secret_door") and e.bounds:
            (x0, y0, _), (x1, y1, _) = e.bounds
            for x in range(int(x0 // s), int((x1 - 1) // s) + 1):
                for y in range(int(y0 // s), int((y1 - 1) // s) + 1):
                    cells.add((x, y))
    return cells


HEADS = {   # style -> (shape, rise per cell of width, least rise, most rise)
    "gothic": ("pointed", 28, 48, 192),
    "compound": ("round", 16, 32, 128),
    "industrial": ("flat", 0, 64, 112),
}


def build(level: Level, tiers: Dict[int, str]) -> int:
    """Frame every corridor opening of every eligible room. Returns the number of portals."""
    cfg = level.config
    style = getattr(cfg, "arch_style", "")
    if not style or not getattr(cfg, "arch_portals", True):
        return 0
    smooth = getattr(cfg, "arch_smooth", True)
    taken = _entity_cells(level)
    doors = _door_cells(level)
    made = 0
    for room in level.rooms:
        tier = tiers.get(room.id)
        if tier is None:
            continue
        rng = random.Random(f"{cfg.seed}/{cfg.variant}/portal/{room.id}")
        opens = openings(level, room)
        mouth_cells = {c for o in opens for c in o["cells"]}
        for op in opens:
            jambs = _jambs(level, room, op, style, taken, mouth_cells)
            head = 0
            if tier in ("grand", "major") and style in HEADS and len(op["cells"]) >= 2:
                head = _head(level, room, op, style, doors, smooth, rng)
            if jambs or head:
                made += 1
                room.meta.setdefault("portals", []).append(
                    {"corridor": op["corridor"], "width": len(op["cells"]), "jambs": jambs, "head": head})
    return made


def _jambs(level: Level, room: Room, op: dict, style: str, taken: set, mouths: set) -> int:
    d = op["out"]
    t = (0, 1) if d[0] else (1, 0)                     # along the wall
    first, last = op["cells"][0], op["cells"][-1]
    ends = [(first[0] - t[0], first[1] - t[1]), (last[0] + t[0], last[1] + t[1])]
    deep = 2 if style == "bunker" else 1
    placed = 0
    for e in ends:
        cells = [(e[0] - d[0] * k, e[1] - d[1] * k) for k in range(deep)]
        ok = True
        for c in cells:
            sp = level.room_spans(c, room)
            if not room.contains(*c) or room.is_diag(*c) or c in taken or c in mouths or c in level.ceil_slope \
                    or len(sp) != 1 or sp[0][0] != room.floor:
                ok = False
                break
        # the jamb's back must be wall: solid behind it, or it would plug something else
        back = (e[0] + d[0], e[1] + d[1])
        if not ok or level.spans.get(back):
            continue
        snap = {c: list(level.spans.get(c, [])) for c in cells}
        for c in cells:
            level.replace_in(c, *room.slot, [])
        if not _base_connected(level, room):
            for c, spans in snap.items():
                level.spans[c] = spans
            continue
        placed += 1
    return placed


def _head(level: Level, room: Room, op: dict, style: str, doors: set, smooth: bool, rng) -> int:
    """Raise the mouth above the corridor into the wall: an arch or a lintelled opening."""
    d = op["out"]
    f, top = op["span"]
    mouth = [(c[0] + d[0], c[1] + d[1]) for c in op["cells"]]
    if any(m in doors or m in level.diag or m in level.ceil_slope for m in mouth):
        return 0
    if any(not level.spans.get(m) or level.spans[m][-1] != (f, top) for m in mouth):
        return 0                                       # something stacked above, or not the plain corridor
    room_top = min(level.room_spans(c, room)[-1][1] for c in op["cells"])
    shape, per, least, most = HEADS[style]
    w = len(mouth)
    rise = min(most, max(least, q(per * w)) if per else least + 16 * rng.randint(0, 3))
    rise = min(rise, q(room_top - 16 - top))
    if rise < 32:
        return 0
    peak = top + rise
    planned = set(mouth)
    for m in mouth:                                    # rock above the mouth, one solid cell around it
        if level.spans_in(m, top + 1, peak + 16):
            return 0
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                n = (m[0] + dx, m[1] + dy)
                if n in planned or room.contains(*n):
                    continue
                if level.spans_in(n, top, peak + 16) or n in level.glass:
                    return 0
    along = (lambda X, Y: Y) if d[0] else (lambda X, Y: X)
    xs = [along(*m) for m in mouth]
    centre, half = (min(xs) + max(xs) + 1) / 2.0, w / 2.0
    arch = {"pointed": pointed, "round": round_arch}.get(shape)

    def z(A):
        if arch is None or not smooth:
            return peak
        return top + rise * arch(min(1.0, abs(A - centre) / half))
    for m in mouth:
        zs = tuple(int(round(z(along(m[0] + dx, m[1] + dy)))) for dx, dy in ((0, 0), (1, 0), (1, 1), (0, 1)))
        flat = max(top, (min(zs) // 16) * 16)
        level.replace_in(m, f, top, [(f, flat)])
        owners = level.owners.get(m, [])
        for i, (lo, hi, name) in enumerate(owners):
            if lo == f and name.startswith("corridor:"):
                owners[i] = (lo, max(hi, peak), name)
        if max(zs) > flat:
            level.ceil_slope[m] = zs
    _splay(level, room, mouth, d, f, top, q(top + rise // 2) if arch else peak, doors, smooth)
    return rise


def _splay(level: Level, room: Room, mouth: List[Cell], d, f: int, top: int, h_s: int, doors: set,
           smooth: bool, layers: int = 2) -> int:
    """The corridor's last cells before the mouth: the ceiling ramps up towards the portal, so
    the opening grows as you walk up to it (from inside a low corridor a raised head alone is
    hidden behind the corridor's own ceiling). Tries 2 layers, then 1."""
    if not smooth or h_s <= top:
        return 0
    for n in range(layers, 0, -1):
        cells = [(m[0] + d[0] * k, m[1] + d[1] * k) for k in range(1, n + 1) for m in mouth]
        if any(c in doors or c in level.diag or c in level.ceil_slope or not level.spans.get(c)
               or level.spans[c][-1] != (f, top) for c in cells):
            continue
        planned = set(cells) | set(mouth)
        ok = True
        for c in cells:
            if level.spans_in(c, top + 1, h_s + 16):
                ok = False
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    nb = (c[0] + dx, c[1] + dy)
                    if nb in planned or room.contains(*nb):
                        continue
                    for sp in level.spans_in(nb, top, h_s + 16):
                        if sp != (f, top):          # the corridor itself, further back, is fine
                            ok = False
                    if nb in level.glass:
                        ok = False
        if not ok:
            continue
        # axial coordinate of the mouth's back edge, and which way the corridor runs from it
        m0 = mouth[0]
        sign = d[0] or d[1]
        a0 = (m0[0] if d[0] else m0[1]) + (1 if sign > 0 else 0)

        def z(X, Y):
            a = X if d[0] else Y
            dist = max(0.0, min(float(n), (a - a0) * sign))
            return h_s - (h_s - top) * dist / n
        for c in cells:
            zs = tuple(int(round(z(c[0] + dx, c[1] + dy))) for dx, dy in ((0, 0), (1, 0), (1, 1), (0, 1)))
            flat = max(top, (min(zs) // 16) * 16)
            level.replace_in(c, f, top, [(f, flat)])
            owners = level.owners.get(c, [])
            for i, (lo, hi, name) in enumerate(owners):
                if lo == f and name.startswith("corridor:"):
                    owners[i] = (lo, max(hi, h_s), name)
            if max(zs) > flat:
                level.ceil_slope[c] = zs
        return n
    return 0
