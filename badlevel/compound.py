"""Compounds: open areas that read as places between buildings, not roofless rooms.

The architecture showcase kept finding the same thing: a courtyard was a 9-14 m pit with
sheer walls on every side, a few blocks inside. Two changes make it a compound:

  buildings   free-standing blocks inside the yard, with walls, a roof and a doorway
              facing the yard's middle: you can walk in. Some get a ladder to the roof,
              and the rooftop becomes a vantage point (a sniper and a reward on it)
  roofscape   the rock around the yard is carved down to stepped roof heights, open to the
              sky: from the yard you see building fronts and a skyline rising away from you,
              not a cliff. The roofs are unreachable scenery; where something else is near
              (a corridor, a room, another storey) the rock stays, which reads as a gatehouse

Safety: a building keeps a 2-cell walkable ring and never touches a doorway; its rooftop
is only reachable by its ladder. Roofscape cells are carved only where the column is
empty from below the roof to above the sky, and only where no other space is within one
cell at those heights, so nothing new can be walked into. The validator checks the rest.
"""
from __future__ import annotations

import random
from typing import Dict

from .level import Cell, Entity, Level, Room

SLAB = 32


def f_buildings(ctx, rng: random.Random) -> bool:
    """1-3 free-standing buildings in a yard, depending on its size."""
    room, level = ctx.room, ctx.level
    area = room.w * room.h
    want = 3 if area >= 320 else 2 if area >= 200 else 1
    made = 0
    cx, cy = room.center()
    for _ in range(40):
        if made >= want:
            break
        bw, bh = rng.randint(4, 7), rng.randint(4, 7)
        x0 = rng.randint(room.x + 2, max(room.x + 2, room.x + room.w - 2 - bw))
        y0 = rng.randint(room.y + 2, max(room.y + 2, room.y + room.h - 2 - bh))
        rect = [(x, y) for x in range(x0, x0 + bw) for y in range(y0, y0 + bh)]
        ring = [(x, y) for x in range(x0 - 2, x0 + bw + 2) for y in range(y0 - 2, y0 + bh + 2)]
        if not ctx.free(ring):
            continue
        top = room.floor + rng.choice([160, 192, 224])       # wall and roof top
        inner_top = top - SLAB                                # the room inside: >= 128
        # the doorway: 2 cells in the middle of the side facing the yard's middle
        bx, by = x0 + bw / 2.0, y0 + bh / 2.0
        if abs(cx - bx) * bh > abs(cy - by) * bw:
            side = "e" if cx > bx else "w"
        else:
            side = "n" if cy > by else "s"
        if side in "ns":
            yy = y0 + bh - 1 if side == "n" else y0
            door = [(x0 + bw // 2 - 1, yy), (x0 + bw // 2, yy)]
        else:
            xx = x0 + bw - 1 if side == "e" else x0
            door = [(xx, y0 + bh // 2 - 1), (xx, y0 + bh // 2)]
        walls = [c for c in rect if (c[0] in (x0, x0 + bw - 1) or c[1] in (y0, y0 + bh - 1)) and c not in door]
        for c in rect:
            if c in walls:
                level.replace_in(c, *room.slot, [(top, room.ceil)])
            else:
                level.replace_in(c, *room.slot, [(room.floor, inner_top), (top, room.ceil)])
        ctx.used.update(ring)
        made += 1
        info = {"rect": [x0, y0, bw, bh], "top": top, "door": side, "ladder": False}
        # a ladder up an outside wall (not the door side): the roof becomes a vantage point
        if rng.random() < 0.65:
            sides = [s for s in "nsew" if s != side]
            rng.shuffle(sides)
            for s in sides:
                if s in "ns":
                    wall_c = (x0 + bw // 2, y0 + bh - 1 if s == "n" else y0)
                    foot = (wall_c[0], wall_c[1] + (1 if s == "n" else -1))
                else:
                    wall_c = (x0 + bw - 1 if s == "e" else x0, y0 + bh // 2)
                    foot = (wall_c[0] + (1 if s == "e" else -1), wall_c[1])
                if level.room_spans(foot, room) != [(room.floor, room.ceil)]:
                    continue
                sz = level.cell
                ax, ay = (foot[0] + 0.5) * sz, (foot[1] + 0.5) * sz
                bx_, by_ = (wall_c[0] + 0.5) * sz, (wall_c[1] + 0.5) * sz
                mx, my = (ax + bx_) / 2, (ay + by_) / 2
                lo = (mx - (4 if s in "ew" else 16), my - (4 if s in "ns" else 16), room.floor)
                hi = (mx + (4 if s in "ew" else 16), my + (4 if s in "ns" else 16), top + 32)
                level.entities.append(Entity("ladder", ((lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, (room.floor + top) / 2),
                                             {"low": room.floor, "high": top, "room": room.id, "building": True},
                                             (lo, hi)))
                level.links.append({"kind": "ladder", "a": (ax, ay, room.floor + 24), "b": (bx_, by_, top + 24),
                                    "two_way": True, "interaction": "climb", "accessible": False, "room": room.id})
                ctx.top_cells = getattr(ctx, "top_cells", []) + [c for c in rect if c not in door]
                info["ladder"] = True
                break
        room.meta.setdefault("buildings", []).append(info)
    if made:
        room.features.append("buildings")
    return made > 0


def roofscape(level: Level, room: Room, rng: random.Random, band: int = 4) -> int:
    """Carve the rock around a yard down to stepped roofs open to the sky."""
    sky_top = room.ceil
    x0, y0, x1, y1 = room.x, room.y, room.x + room.w, room.y + room.h
    # blocks along the band: each block of 3-6 cells gets its own front height, and the roofs
    # rise away from the yard towards the sky plane
    front: Dict[tuple, int] = {}

    def front_height(key):
        if key not in front:
            # never within a step of anything a player stands on along a yard wall (a balcony deck
            # is 144 up): the roofs stay scenery
            front[key] = room.floor + rng.choice([176, 192, 224, 256])
        return front[key]
    planned: Dict[Cell, int] = {}
    for x in range(x0 - band, x1 + band):
        for y in range(y0 - band, y1 + band):
            if room.contains(x, y) or (x0 <= x < x1 and y0 <= y < y1):
                continue
            d = max(x0 - x, x - (x1 - 1), y0 - y, y - (y1 - 1))      # 1..band: rings out from the yard
            along = x if (y < y0 or y >= y1) else y
            side = ("s" if y < y0 else "n") if (y < y0 or y >= y1) else ("w" if x < x0 else "e")
            block = (side, along // 5)                               # 5-cell blocks of building
            base = front_height(block)
            roof = min(sky_top - 96, base + (d - 1) * 48)            # rising away from the yard
            roof = (roof // 16) * 16
            if roof - room.floor < 112:
                continue
            planned[(x, y)] = roof
    carved = 0
    for c, roof in sorted(planned.items()):
        if not (2 < c[0] < level.width - 3 and 2 < c[1] < level.height - 3):
            continue
        if c in level.diag or c in level.glass or c in level.ceil_slope \
                or level.spans_in(c, roof - 64, sky_top + 32):
            continue                                 # (a sloped ceiling must stay the top of its column)
        ok = True
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                n = (c[0] + dx, c[1] + dy)
                if n == c or room.contains(*n):
                    continue
                near = level.spans_in(n, roof - 64, sky_top + 32)
                if n in level.glass or any(level.owner_of(n, sp) != f"roof:{room.id}" for sp in near):
                    ok = False                       # something else is right there: keep the rock
        if not ok:
            continue
        level.add_span(c, (roof, sky_top), f"roof:{room.id}")
        level.sky.add(c)
        carved += 1
    if carved:
        room.meta["roofscape"] = carved
        room.features.append("roofscape")
    return carved
