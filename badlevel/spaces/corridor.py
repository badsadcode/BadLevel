"""Corridors as a space type: their style (width, diagonals), their beats, their contents.

Routing and carving are core infrastructure and live in ``layout``; this module
decides what a corridor *is*: how wide, whether it may run at 45 degrees, where it
gets chambers / alcoves / vaults, and what sits in it (lights, beat encounters,
pickups, doors).
"""
from __future__ import annotations

import random
from typing import Optional

from ..config import GenConfig
from ..graph import MissionGraph
from ..level import Cell, Corridor, Entity, Level, Room


def _straight(*a, **k):
    from ..layout import _straight as s
    return s(*a, **k)


def _dir(*a, **k):
    from ..layout import _dir as d
    return d(*a, **k)


SLAB = 16


def _corridor_style(g: MissionGraph, e, a: Room, b: Room, cfg: GenConfig, rng: random.Random):
    kinds = {g.nodes[e.a].kind, g.nodes[e.b].kind}
    if a.is_connector or b.is_connector:
        return 1, False                  # connector ports are 3 cells wide
    if e.kind == "secret":
        r, diag = 1, False
    else:
        from ..weights import weighted
        if not e.tree:
            opts = [1, 1, 2]
        elif kinds & {"arena", "hub"}:
            opts = [1, 2, 2, 3]
        elif kinds & {"supply", "secret", "key"}:
            opts = [1, 1, 2]
        else:
            opts = [1, 2, 2]
        r, diag = weighted(rng, opts, cfg.corridor_width_bias), rng.random() < cfg.diagonal_chance
    # the opening must fit the smaller room
    smallest = min(a.w, a.h, b.w, b.h)
    r = max(1, min(r, cfg.max_corridor_radius, (smallest - 3) // 2))
    return r, diag


def _perp(d: Cell) -> Cell:
    return (-d[1], d[0])


def _others_near(level: Level, c: Cell, lo: int, hi: int, own: str) -> bool:
    """Is any space of another area within one cell of c, in the vertical range [lo, hi)?"""
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            n = (c[0] + dx, c[1] + dy)
            for sp in level.spans_in(n, lo - SLAB, hi + SLAB):
                if level.owner_of(n, sp) != own:
                    return True
    return False


def add_beats(level: Level, router: "Router", corr: Corridor, rng: random.Random):
    """Break a long corridor up: every ``beat_spacing`` cells, on a straight level stretch,
    something changes: a chamber (wider, taller, maybe pillars for cover), alcoves (side
    niches with a pickup) or a vault (much higher ceiling). Long corridors stay long."""
    cfg = level.config
    path, floors = corr.path, corr.floors
    n, r, h = len(path), corr.radius, corr.height
    sp = cfg.beat_spacing
    if n < 2 * sp or corr.connector in ("drop", "gallery"):
        return
    own = f"corridor:{corr.id}"
    L2 = 3
    targets = list(range(sp, n - sp // 2, sp))
    for t in targets:
        cands = [i for i in range(max(L2 + 3, t - 4), min(n - L2 - 3, t + 5))
                 if _straight(path, i, L2 + 1, axis_only=True) and len(set(floors[i - L2 - 1:i + L2 + 2])) == 1]
        if not cands:
            continue
        i = min(cands, key=lambda j: abs(j - t))
        f = floors[i]
        d = _dir(path, i)
        pd = _perp(d)
        cx, cy = path[i]
        kind = rng.choices(["chamber", "alcoves", "vault"], [0.5, 0.3, 0.2])[0]
        if kind == "chamber":
            R, top = r + 2, f + h + 64
            zone = [(cx + d[0] * u + pd[0] * v, cy + d[1] * u + pd[1] * v)
                    for u in range(-L2, L2 + 1) for v in range(-R, R + 1)]
            pillars = []
            if rng.random() < 0.6:
                pillars = [(cx + pd[0] * v, cy + pd[1] * v) for v in (-(r + 1), r + 1)]
        elif kind == "alcoves":
            top = f + h
            sides = rng.choice([(-1,), (1,), (-1, 1)])
            zone = [(cx + d[0] * u + pd[0] * s * v, cy + d[1] * u + pd[1] * s * v)
                    for s in sides for u in (-1, 0, 1) for v in range(-r, r + 3)]
            pillars = []
        else:
            top = f + h + 96
            zone = [(cx + d[0] * u + pd[0] * v, cy + d[1] * u + pd[1] * v)
                    for u in range(-L2, L2 + 1) for v in range(-r, r + 1)]
            pillars = []
        # everything new must be solid now (or ours, flat at f) and keep a wall to other areas
        ok = True
        for c in zone:
            spans = level.spans.get(c, [])
            mine = [s_ for s_ in spans if level.owner_of(c, s_) == own]
            near_diag = any((c[0] + dx, c[1] + dy) in level.diag for dx in (-1, 0, 1) for dy in (-1, 0, 1))
            if near_diag or any(s_ not in mine and s_[0] < top + SLAB and f - SLAB < s_[1] for s_ in spans):
                ok = False
                break
            if any(m[0] != f for m in mine) or _others_near(level, c, f, top, own):
                ok = False
                break
        if not ok:
            continue
        cells = []
        for c in zone:
            if c in pillars:
                continue
            level.replace_in(c, f - 1, f + h, [])           # our own span (if any) ...
            level.add_span(c, (f, top), own if not level.owner_at(c, f) else None)  # ... now taller / new
            cells.append(c)
        for c in pillars:
            level.replace_in(c, f - 1, top, [])
        for c in cells:
            router.add_open(c, f, top)
        s = level.cell
        corr.beats.append({"type": kind, "index": i, "cells": [list(c) for c in cells],
                           "pos": [(cx + 0.5) * s, (cy + 0.5) * s, f], "top": top,
                           "pillars": len(pillars)})


def populate(level: Level, corr: Corridor, rng: random.Random, info: dict):
    """Lights along the corridor, encounters and rewards in its beats, its door if any."""
    from ..decorate import _door_entity
    g = level.graph
    ents = level.entities
    max_depth = info["max_depth"]
    for i in range(3, len(corr.path) - 3, 7):
        (x, y), f = corr.path[i], corr.floors[i]
        ents.append(Entity("light", ((x + 0.5) * level.cell, (y + 0.5) * level.cell,
                                     f + corr.height - 16), {"intensity": 200}))
    # beats: a fight in a chamber, a reward in an alcove, light in a vault
    depth = max(g.nodes[level.rooms[corr.a].node].depth, g.nodes[level.rooms[corr.b].node].depth)
    tier = 1 + min(2, (depth * 3) // (max_depth + 1))
    s = level.cell
    for beat in corr.beats:
        f, top = beat["pos"][2], beat["top"]
        cells = [tuple(c) for c in beat["cells"]]
        path_cells = set(corr.path)
        side = [c for c in cells if c not in path_cells]
        at = lambda c, z: ((c[0] + 0.5) * s, (c[1] + 0.5) * s, z)
        ents.append(Entity("light", (beat["pos"][0], beat["pos"][1], top - 24), {"intensity": 260}))
        if beat["type"] == "chamber" and side and rng.random() < min(1.0, level.config.enemy_density):
            for c in rng.sample(side, min(len(side), rng.randint(1, 2))):
                ents.append(Entity("enemy", at(c, f + 24), {"tier": tier}))
        elif beat["type"] == "alcoves" and side:
            c = max(side, key=lambda c: abs(c[0] - beat["pos"][0] / s) + abs(c[1] - beat["pos"][1] / s))
            ents.append(Entity(rng.choice(["health", "ammo", "armor"]), at(c, f), {}))
    if corr.kind == "locked":
        ents.append(_door_entity(level, corr, "door"))
    elif corr.kind == "secret":
        ents.append(_door_entity(level, corr, "secret_door"))
