"""The goblin market (Noor's idea): an open yard laid out in streets of stalls.

A market is a courtyard (open to the sky) filled with stalls in double rows, back to back, each
facing a street; streets 4 cells wide (two player boxes), gaps of 2 between some stalls so the
rows can be crossed, a plaza in the middle with a well, a street round the walls. A stall:

* a **counter** at the front, 40 high: cover you can see over (and hop onto);
* the **booth** behind it, under a roof at 112, where the merchant stands;
* **side and back walls** up to the roof, the **roof** a 16-unit slab, open sky above.

Every stall is a named part (``room:N/stall 3``) with a ``merchant`` marker behind its counter
and what it sells (Noor's list, and a few more), so the artist's pass knows what goes where.
"Every stall selling something that bites is an artist's job": BadLevel names the stall and
stays out of it. Lanterns hang at the stall fronts. ``cfg.market``: the chance a yard is one.
"""
from __future__ import annotations

import random
from typing import List

from .level import Entity

GOODS = [
    "suspiciously twitching treasure chests", "meat of no known species", "mushrooms (some escape)",
    "teeth (no explanation)", "swords that scream when swung", "potions of uncertain colour",
    "rats on sticks", "maps to places that aren't there", "second-hand hats", "jars of eyes",
    "slightly cursed rings", "live eels", "goblin poetry", "boots, one of each",
]
DEPTH = 3                 # a stall: counter, booth, back wall
STREET = 4                # between rows of stalls: two player boxes
ROOF, SLAB = 112, 16      # the booth's headroom and its roof


def _stall(ctx, cells_by_row, k: int, sells: str, front_dir) -> None:
    level, room = ctx.level, ctx.room
    f = room.floor
    name = f"room:{room.id}/stall {k}"
    counter, booth, back = cells_by_row
    sides = {booth[0], booth[-1], counter[0], counter[-1]}
    for c in back + [booth[0], booth[-1]]:            # the walls, up to the roof
        level.replace_in(c, *room.slot, [(f + ROOF + SLAB, room.ceil)])
        level.part_solid[c] = name
    for c in counter:
        if c in sides:
            level.replace_in(c, *room.slot, [(f + ROOF + SLAB, room.ceil)])
            level.part_solid[c] = name
        else:                                          # the counter, under the awning
            level.replace_in(c, *room.slot, [(f + 40, f + ROOF), (f + ROOF + SLAB, room.ceil)])
            level.part_cells[c] = name
    for c in booth[1:-1]:                              # the booth, under the roof
        level.replace_in(c, *room.slot, [(f, f + ROOF), (f + ROOF + SLAB, room.ceil)])
        level.part_cells[c] = name
    for c in counter + booth + back:
        ctx.used.add(c)
    s = level.cell
    mid = booth[len(booth) // 2]
    level.entities.append(Entity("merchant", ((mid[0] + 0.5) * s, (mid[1] + 0.5) * s, f + 24),
                                 {"stall": k, "sells": sells, "room": room.id}))
    front = counter[len(counter) // 2]
    lx, ly = (front[0] + 0.5 + front_dir[0] * 0.8) * s, (front[1] + 0.5 + front_dir[1] * 0.8) * s
    level.entities.append(Entity("light", (lx, ly, f + ROOF - 8), {"intensity": 160, "lantern": True}))
    room.meta.setdefault("stalls", []).append({"id": k, "sells": sells, "cells": len(counter) + len(booth) + len(back)})


def f_market(ctx, rng: random.Random) -> bool:
    room, level = ctx.room, ctx.level
    if min(room.w, room.h) < 14:
        return False
    along_x = room.w >= room.h
    L, D = (room.w, room.h) if along_x else (room.h, room.w)
    to_w = (lambda a, b: (room.x + a, room.y + b)) if along_x else (lambda a, b: (room.x + b, room.y + a))
    goods = list(GOODS)
    rng.shuffle(goods)
    # the plaza: the middle third along, kept open (a well in its centre)
    plaza = (L // 2 - max(3, L // 6), L // 2 + max(3, L // 6))
    k = 0
    v = 3                                              # a street round the walls
    while v + 2 * DEPTH <= D - 3:
        # a double row: stalls facing -v (rows v..v+2) and +v (rows v+3..v+5), back to back
        u = 3 + rng.randint(0, 1)
        while u + 3 <= L - 3:
            w = rng.choice([3, 4])
            if u + w > L - 3:
                break
            if not (u + w <= plaza[0] or u >= plaza[1]):
                u = plaza[1]
                continue
            for facing, rows in ((-1, (v, v + 1, v + 2)), (1, (v + 5, v + 4, v + 3))):
                cells = [[to_w(a, r) for a in range(u, u + w)] for r in rows]
                flat = [c for row in cells for c in row]
                if not all(room.contains(*c) and c not in ctx.clear and c not in ctx.used and c not in level.diag
                           and level.room_spans(c, room) == [(room.floor, room.ceil)] for c in flat):
                    continue
                d = (0, facing) if along_x else (facing, 0)
                _stall(ctx, cells, k, goods[k % len(goods)], d)
                k += 1
            u += w + (2 if rng.random() < 0.4 else 0)  # now and then an alley through the row
        v += 2 * DEPTH + STREET
    if not k:
        return False
    # the well in the plaza
    cu, cv = (plaza[0] + plaza[1]) // 2, D // 2
    well = [to_w(cu + i, cv + j) for i in (-1, 0) for j in (-1, 0)]
    if all(room.contains(*c) and c not in ctx.used and c not in ctx.clear for c in well):
        for c in well:
            ctx.set_floor(c, room.floor + 48)
            level.part_cells[c] = f"room:{room.id}/well"
    room.features.append("market")
    return True
