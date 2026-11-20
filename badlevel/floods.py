"""Flooded crypts: water to wade through, and corridors you must swim (after Noor's idea).

Two kinds of water, because they play differently (Noor liked the distinction, and it makes the
checker more useful):

* **Wading water** (``cfg.floods``, a chance per crypt): a room fills to ``WADE`` units (32: knee
  to waist on a 56-unit player, well under the eyes at 46, so nobody drowns walking). The water
  spreads the way water does: through doorways and along level corridors, at one height, and
  stops where the floor rises above it, a natural shore. It doesn't change a single floor, so
  every route the checker walked is still there. If it would spread over more than MAX_FLOOD
  cells, or reach a pit, a chasm or a lava pool, that room stays dry.
* **Swimming** (``cfg.sumps``, a chance per corridor inside a flood): a straight, level corridor
  between flooded areas dips: down a few steps, along a stretch whose ceiling is under the water
  (no air), and up again. The steps are ordinary steps and the duct is 80 high, so the route is
  walkable by the checker's rules; what's new is breath. ``breath_report`` (part of validation)
  finds every stretch without air and fails the level if one is longer than MAX_SWIM cells: a
  player must always reach air on one breath.

``level.water``: cell -> the water's surface height over that cell's lowest span.
"""
from __future__ import annotations

import random
from typing import Dict, List, Set

from .level import Level

WADE = 32               # wading depth: under a Quake player's eyes (46 above the feet)
MAX_FLOOD = 900         # cells: a flood bigger than this is a lake; the room stays dry
SUMP_DROP = 5           # steps down (16 each): at the bottom the duct is all under water
SUMP_HEIGHT = 80        # the duct: 64 of headroom between one step and the next (the player is 56)
MAX_SWIM = 14           # cells without air on one breath (about 450 units: 2-3 s of swimming)


def _flood(level: Level, start_cells, S: int):
    """Cells the water reaches from the start cells at surface height S, or None if it spills
    into a pit or grows past MAX_FLOOD."""
    seen: Set = set()
    stack = []
    for c in start_cells:
        sp = level.spans.get(c)
        if sp and sp[0][0] < S:
            seen.add(c)
            stack.append(c)
    while stack:
        c = stack.pop()
        f0, c0 = level.spans[c][0]
        for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            n = (c[0] + dx, c[1] + dy)
            if n in seen or n not in level.spans or n in level.glass:
                continue
            f1, c1 = level.spans[n][0]
            if f1 >= S or min(c0, c1) <= max(f0, f1):
                continue                                   # dry ground, or no opening between them
            if n in level.hazard or f1 < S - 160:
                return None                                # a pit, a chasm, a pool: not a crypt flood
            seen.add(n)
            stack.append(n)
            if len(seen) > MAX_FLOOD:
                return None
    return seen


def add_floods(level: Level) -> int:
    cfg = level.config
    chance = getattr(cfg, "floods", 0.0)
    if chance <= 0:
        make_sumps(level)
        return 0
    made = 0
    for room in level.rooms:
        if room.is_connector or room.kind in ("courtyard", "overlook", "megaroom"):
            continue
        prog = room.meta.get("program", "")
        if getattr(cfg, "program_set", "") == "dungeon" and prog and prog not in ("crypt", "catacomb hall", "shrine", "cells"):
            continue                                      # dungeons flood their crypts; elsewhere any room
        if any(c in level.water for c in room.cells()):
            continue
        rng = random.Random(f"{cfg.seed}/{cfg.variant}/flood/{room.id}")
        if rng.random() >= chance:
            continue
        S = room.floor + WADE
        cells = _flood(level, [c for c in room.cells() if level.room_spans(c, room)
                               and level.spans[c][0][0] == room.floor], S)
        if not cells or len(cells) < 20:
            continue
        for c in cells:
            level.water[c] = S
        room.features.append("flooded")
        room.meta["flood"] = {"surface": S, "cells": len(cells)}
        made += 1
    make_sumps(level)
    if made:
        level.notes.append(f"floods: {made}")
    return made


def make_sumps(level: Level) -> int:
    """A straight, level stretch of corridor dips under water: a sump (a U-bend). Down SUMP_DROP
    steps, along a flat bottom, up again; the dip holds water flush with the corridor's floor (or
    the flood's, if it's in one), and its duct is 80 high, so at the bottom there's no air: a swim."""
    from .layout import _straight
    cfg = level.config
    chance = getattr(cfg, "sumps", 0.0)
    if chance <= 0:
        return 0
    made = 0
    for corr in level.corridors:
        if getattr(corr, "vent", False) or corr.connector or corr.kind != "open":
            continue
        rng = random.Random(f"{cfg.seed}/{cfg.variant}/sump/{corr.id}")
        if rng.random() >= chance:
            continue
        path, floors = corr.path, corr.floors
        tag = f"corridor:{corr.id}"
        n_flat = rng.randint(2, 4)
        prof_rel = [-16 * (k + 1) for k in range(SUMP_DROP)] + [-16 * SUMP_DROP] * n_flat + \
                   [-16 * (SUMP_DROP - k) for k in range(SUMP_DROP)]
        L = len(prof_rel)
        # a window of the path: straight and level, with 2 cells of margin each side
        starts = [i for i in range(2, len(path) - L - 2)
                  if all(_straight(path, j, corr.radius, axis_only=True) for j in range(i - 1, i + L + 1))
                  and len(set(floors[i - 2:i + L + 2])) == 1]
        if not starts:
            continue
        i0 = starts[len(starts) // 2]
        f = floors[i0]
        idx = list(range(i0, i0 + L))
        mine = [c for c, os_ in level.owners.items() if any(o[2] == tag for o in os_)]
        cells = {}
        for c in mine:
            i = min(range(len(path)), key=lambda j: abs(path[j][0] - c[0]) + abs(path[j][1] - c[1]))
            if i0 <= i < i0 + L:
                cells[c] = f + prof_rel[i - i0]
        lowest = f - 16 * SUMP_DROP
        ok = bool(cells)
        for c in cells:
            sp = level.spans.get(c, [])
            if len(sp) != 1 or sp[0][0] != f or c in level.floor_slope or c in level.diag or c in level.hazard:
                ok = False
                break
            for dx in (-1, 0, 1):                          # nothing open down there, beside or below
                for dy in (-1, 0, 1):
                    n = (c[0] + dx, c[1] + dy)
                    if n not in cells and level.spans_in(n, lowest - 16, f):
                        ok = False
        if not ok:
            continue
        for c, nf in cells.items():
            top = min(level.spans[c][0][1], nf + SUMP_HEIGHT)
            level.replace_in(c, f, level.spans[c][0][1], [(nf, top)])
            level.owners[c] = [(min(lo, nf) if name == tag else lo, hi, name) for lo, hi, name in level.owners[c]]
            level.ceil_slope.pop(c, None)
            level.part_cells[c] = f"{tag}/sump"
            level.water[c] = max(level.water.get(c, f), f)
        s_ = level.cell
        for e in level.entities:                           # whatever stood here goes down with the floor
            c = (int(e.pos[0] // s_), int(e.pos[1] // s_))
            if c in cells and not e.bounds:
                nf = cells[c]
                top = level.spans[c][0][1]
                z = e.pos[2] - (f - nf)
                e.pos = (e.pos[0], e.pos[1], min(max(z, nf + 8), top - 8))
        for i in idx:
            corr.floors[i] = f + prof_rel[i - i0]
        corr.sump = True
        made += 1
    if made:
        level.notes.append(f"sumps: {made}")
    return made


def breath_report(level: Level) -> List[str]:
    """Every stretch without air (the ceiling under the water) must be swum on one breath."""
    if not level.water:
        return []
    # no air: less than 12 units between the water and the ceiling (no room for a head)
    noair = {c for c, S in level.water.items() if level.spans.get(c) and level.spans[c][0][1] - S < 12}
    errors = []
    seen: Set = set()
    for c in noair:
        if c in seen:
            continue
        comp, stack = {c}, [c]
        seen.add(c)
        while stack:
            a = stack.pop()
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                n = (a[0] + dx, a[1] + dy)
                if n in noair and n not in seen:
                    seen.add(n)
                    comp.add(n)
                    stack.append(n)
        xs, ys = [p[0] for p in comp], [p[1] for p in comp]
        if max(max(xs) - min(xs), max(ys) - min(ys)) + 1 > MAX_SWIM:
            errors.append(f"breath: {max(max(xs) - min(xs), max(ys) - min(ys)) + 1} cells under water without air "
                          f"at {min(comp)} (one breath: {MAX_SWIM})")
    return errors
