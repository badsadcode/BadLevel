"""Landmarks: one deliberate, unique, tall object per level, in its biggest space.

The first architectural review found that "landmarks" were only the most visible rooms,
measured: nothing in a level was a thing you'd point at and say "meet me by the obelisk".
Now the grand space of a styled level gets one landmark, shaped by the style:

  gothic    spire       a stepped tower with corner pinnacles
  castle    keep        a square tower with a crenellated top
  egyptian  obelisk     a tall shaft on a low plinth
  industrial stack      a chimney on a base, solid up through the roof
  scifi     core        a reactor column floor to ceiling, with floating rings around it
  compound  water tower a tank raised on a shaft, open underneath
  bunker    monolith    a dark slab on a step
  (any)     column      a great column on a plinth, floor to ceiling: where the room is too
                        low for the style's own shape

It is placed before the room's features (they avoid it), as near the room's middle as fits,
with a 2-cell walkable ring, never at a doorway, and undone if it would cut the floor in two.
A free-standing top stays at least HEAD below the room's ceiling profile, so a vault above is
never broken; the full-height shapes are solid floor to ceiling. Tagged as a part
("room:N/landmark") so it can be replaced like any element.
"""
from __future__ import annotations

import random
from typing import Dict, List, Optional, Tuple

from .architecture import HEAD, Section, _base_connected, q
from .level import Cell, Level, Room

SHAPES = {"gothic": "spire", "castle": "keep", "egyptian": "obelisk", "industrial": "stack",
          "scifi": "core", "compound": "water tower", "bunker": "monolith"}


def _shape(name: str, H: int, top_max: int) -> Optional[Dict[Tuple[int, int], List[Tuple[int, int]]]]:
    """Cell offset -> solid ranges (above the floor) for a shape; None if the room can't take it.
    ``top_max``: the highest a free-standing top may reach; ``H`` the room's height (for the
    shapes that run to the ceiling)."""
    def block(x0, x1, y0, y1):
        return [(x, y) for x in range(x0, x1) for y in range(y0, y1)]
    out: Dict[Tuple[int, int], List[Tuple[int, int]]] = {}
    if name in ("spire", "keep", "obelisk", "monolith", "water tower") and top_max < 160:
        return None
    if name == "spire":
        for c in block(-2, 2, -2, 2):
            out[c] = [(0, q(top_max * 0.3))]
        for c in ((-2, -2), (1, -2), (-2, 1), (1, 1)):                 # corner pinnacles
            out[c] = [(0, q(top_max * 0.45))]
        for c in block(-1, 1, -1, 1):
            out[c] = [(0, top_max)]
    elif name == "keep":
        for c in block(-2, 2, -2, 2):
            edge = c[0] in (-2, 1) or c[1] in (-2, 1)
            merlon = edge and (c[0] + c[1]) % 2 == 0
            out[c] = [(0, q(top_max * 0.8) + (32 if merlon else 0))]
    elif name == "obelisk":
        for c in block(-2, 2, -2, 2):
            out[c] = [(0, 48)]
        for c in block(-1, 1, -1, 1):
            out[c] = [(0, top_max)]
    elif name == "monolith":
        for c in block(-2, 2, -1, 1):
            out[c] = [(0, 32)]
        for c in block(-1, 1, -1, 1):
            out[c] = [(0, q(top_max * 0.8))]
    elif name == "stack":
        for c in block(-2, 3, -2, 3):
            out[c] = [(0, 96)]
        for c in block(-1, 2, -1, 2):
            out[c] = [(0, H)]                                        # through the roof
    elif name == "core":
        if H < HEAD * 2 + 64:
            return None
        r1, r2 = q(H * 0.35), q(H * 0.65)
        for c in block(-2, 3, -2, 3):
            if r1 >= HEAD:
                out[c] = [(r1, r1 + 16), (r2, r2 + 16)]              # rings floating around it
        for c in block(-1, 2, -1, 2):
            out[c] = [(0, H)]
    elif name == "column":                                           # the fallback: fits any height
        for c in block(-2, 2, -2, 2):
            out[c] = [(0, 48)]
        for c in block(-1, 1, -1, 1):
            out[c] = [(0, H)]
    elif name == "water tower":
        tank0, tank1 = q(top_max * 0.6), q(top_max * 0.85)
        if tank0 < HEAD:
            return None
        for c in block(-2, 2, -2, 2):
            out[c] = [(tank0, tank1)]
        for c in block(-1, 1, -1, 1):
            out[c] = [(0, tank1)]
    return out or None


def place(level: Level, room: Room, ctx, sec: Optional[Section], style: str,
          rng: random.Random) -> Optional[dict]:
    """The style's landmark, or where the room is too low for it, a great column. As big as
    the room allows: scaled 3x / 2x / 1x with the room's size, smaller if it doesn't fit."""
    name = SHAPES.get(style)
    if name is None:
        return None
    big = min(room.w, room.h)
    for scale in [k for k in (3, 2, 1) if big >= 14 * k or k == 1]:
        got = _place(level, room, ctx, sec, name, scale) or (scale == 1 and _place(level, room, ctx, sec, "column", 1))
        if got:
            return got
    return None


def _place(level: Level, room: Room, ctx, sec: Optional[Section], name: str, scale: int = 1) -> Optional[dict]:
    H = room.ceil - room.floor
    # the lowest ceiling over the room's middle (after the section) bounds a free-standing top
    fr = sec.frame if sec is not None else None
    cx, cy = room.center()
    ox, oy = int(cx), int(cy)
    order = sorted(((x, y) for x in range(ox - 6, ox + 7) for y in range(oy - 6, oy + 7)),
                   key=lambda c: (c[0] + 0.5 - cx) ** 2 + (c[1] + 0.5 - cy) ** 2)
    near_door = {(x + dx, y + dy) for (x, y) in ctx.doors for dx in range(-3, 4) for dy in range(-3, 4)}
    plain = [(room.floor, room.ceil)]
    for (px, py) in order:
        local = H
        if fr is not None:
            for dx in range(-3, 4):
                for dy in range(-3, 4):
                    uv = fr.uv((px + dx, py + dy))
                    if uv in sec.ceil:
                        local = min(local, sec.ceil[uv])
        shape = _shape(name, H, q(local - HEAD - 16))
        if shape is None:
            continue                                    # (a lower spot of the profile may not fit it)
        # scaled with the room: a landmark has to dominate its space (2x in a mega-hall)
        k = scale
        cells = {(px + dx * k + i, py + dy * k + j): rngs for (dx, dy), rngs in shape.items()
                 for i in range(k) for j in range(k)}
        ring = {(x + dx, y + dy) for (x, y) in cells for dx in range(-2, 3) for dy in range(-2, 3)} - set(cells)
        if not all(room.contains(*c) and not room.is_diag(*c) and c not in ctx.used and c not in near_door
                   and level.room_spans(c, room) == plain for c in cells):
            continue
        if not all(room.contains(*c) and c not in ctx.used and level.room_spans(c, room) for c in ring):
            continue
        snap = {c: list(level.spans.get(c, [])) for c in cells}
        for c, solids in cells.items():
            open_spans, z = [], room.floor
            for lo, hi in sorted(solids):
                if room.floor + lo > z:
                    open_spans.append((z, room.floor + lo))
                z = room.floor + hi
            if z < room.ceil:
                open_spans.append((z, room.ceil))
            level.replace_in(c, *room.slot, [sp for sp in open_spans if sp[1] - sp[0] >= 16])
        if not _base_connected(level, room):
            for c, spans in snap.items():
                level.spans[c] = spans
            continue
        part = f"room:{room.id}/landmark"
        for c in cells:
            level.part_solid[c] = part
            if level.spans.get(c):
                level.part_cells[c] = part
        ctx.used.update(cells)
        ctx.used.update(ring)
        top = max(hi for rngs in shape.values() for _, hi in rngs)
        info = {"shape": name, "centre": [px + (scale - 1) / 2.0, py + (scale - 1) / 2.0], "top": room.floor + top,
                "cells": len(cells), "scale": scale}
        room.meta["landmark"] = info
        room.features.append(name)
        return info
    return None
