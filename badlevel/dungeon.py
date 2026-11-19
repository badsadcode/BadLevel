"""Dungeon dressing: torches, grates and traps (after Noor's concept art, devlog secret level).

The art: a torch-lit stone dungeon, a narrow bridge over spikes (decorate.f_chasm), a swinging
blade, goblins on a balcony, arches, light falling through a grate. The pieces here:

* **Torches** (``cfg.torches``): instead of lights hanging in the middle of a room, torches on
  its walls, about every 6 cells round the room, 96 units up, warm and dim; never beside a
  doorway. A big room keeps one faint light in the middle so its floor isn't black. Quake 1
  exports the real wall torch (``light_torch_small_walltorch``), other games a coloured light.
* **Grates** (``cfg.skylights``, a chance per room): a 2x2 or 3x3 opening in the ceiling to the
  sky, with a shaft of light under it, where nothing is stacked above the room. Done after the
  vaults are shaped, so the grate cuts through them (``room:N/grate``).
* **Traps** (``cfg.traps``, a chance per corridor): markers on a straight, level stretch of
  corridor: a swinging blade, dart holes, a spike floor. Markers only (``trap`` entities with a
  type and an axis): each game builds its own trap there; they never change the geometry, so
  they can't block a route.
"""
from __future__ import annotations

import math
import random

from .level import Entity, Level, Room

TORCH_UP = 96
TORCH_LIGHT = 170
TRAPS = ("blade", "darts", "spikes")


def place_torches(level: Level, room: Room) -> int:
    s = level.cell
    cx, cy = room.x + room.w / 2.0, room.y + room.h / 2.0
    cands = []
    for c in room.cells():
        sp = level.room_spans(c, room)
        if not sp or c in level.diag or sp[0][0] > room.floor + 16 or sp[0][1] < room.floor + TORCH_UP + 40:
            continue
        for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            n = (c[0] + dx, c[1] + dy)
            if level.spans_in(n, room.floor + TORCH_UP - 8, room.floor + TORCH_UP + 8) or n in level.diag:
                continue                                  # open (or half open) that way: not a wall
            # never beside a doorway: no corridor within 2 cells
            near = [(c[0] + a, c[1] + b) for a in range(-2, 3) for b in range(-2, 3)]
            if any((level.owner_at(m, room.floor + 24) or "").startswith("corridor:") for m in near):
                continue
            cands.append((math.atan2(c[1] + 0.5 - cy, c[0] + 0.5 - cx), c, (dx, dy)))
            break
    cands.sort()
    perimeter = 2 * (room.w + room.h)
    want = max(2, min(14, perimeter // 6))
    picked = []
    for _, c, d in cands:
        if all(abs(c[0] - p[0][0]) + abs(c[1] - p[0][1]) >= 5 for p in picked):
            picked.append((c, d))
        if len(picked) >= want:
            break
    for c, (dx, dy) in picked:
        z = min(room.floor + TORCH_UP, level.room_spans(c, room)[0][1] - 32)
        pos = ((c[0] + 0.5 + dx * 0.3) * s, (c[1] + 0.5 + dy * 0.3) * s, z)
        level.entities.append(Entity("torch", pos, {"intensity": TORCH_LIGHT, "wall": [dx, dy], "room": room.id}))
    if room.w * room.h >= 160:                           # a big room: one faint light in the middle
        from .decorate import _nearest_open
        c = _nearest_open(level, room, cx, cy)
        bottom, top = level.room_spans(c, room)[-1]
        level.entities.append(Entity("light", ((c[0] + 0.5) * s, (c[1] + 0.5) * s,
                                               min(top - 24, max(bottom, room.floor) + 160)), {"intensity": 140}))
    return len(picked)


def add_skylights(level: Level) -> int:
    cfg = level.config
    chance = getattr(cfg, "skylights", 0.0)
    if chance <= 0:
        return 0
    s = level.cell
    made = 0
    for room in level.rooms:
        if room.is_connector or room.kind in ("courtyard", "secret", "overlook") or min(room.w, room.h) < 7:
            continue
        rng = random.Random(f"{cfg.seed}/{cfg.variant}/grate/{room.id}")
        if rng.random() >= chance:
            continue
        k = 3 if min(room.w, room.h) >= 12 else 2
        cx, cy = room.x + (room.w - k) // 2, room.y + (room.h - k) // 2
        for ox, oy in ((0, 0), (1, 0), (-1, 0), (0, 1), (0, -1), (2, 0), (-2, 0), (0, 2), (0, -2)):
            cells = [(cx + ox + i, cy + oy + j) for i in range(k) for j in range(k)]
            ok = True
            for c in cells:
                sp = level.room_spans(c, room)
                # the room's own span must be the column's top one: nothing stacked above
                if not room.contains(*c) or c in level.diag or c in level.sky or not sp or level.spans[c][-1] != sp[-1]:
                    ok = False
                    break
            if ok:
                break
        if not ok:
            continue
        for c in cells:
            f, top = level.spans[c][-1]
            if top < room.ceil:                          # a vault dips here: the grate cuts up through it
                level.replace_in(c, f, top, [(f, room.ceil)])
                level.owners[c] = [(lo, room.ceil if name == f"room:{room.id}" else hi, name)
                                   for lo, hi, name in level.owners.get(c, [])]
            level.ceil_slope.pop(c, None)
            level.sky.add(c)
            level.part_ceiling[c] = f"room:{room.id}/grate"
        mx = (cx + ox + k / 2.0) * s
        my = (cy + oy + k / 2.0) * s
        level.entities.append(Entity("light", (mx, my, room.ceil - 24), {"intensity": 320, "shaft": True}))
        room.features.append("grate")
        made += 1
    if made:
        level.notes.append(f"grates: {made}")
    return made


def add_traps(level: Level) -> int:
    from .layout import _straight
    cfg = level.config
    chance = getattr(cfg, "traps", 0.0)
    if chance <= 0:
        return 0
    s = level.cell
    made = 0
    for corr in level.corridors:
        if getattr(corr, "vent", False) or len(corr.path) < 10:
            continue
        rng = random.Random(f"{cfg.seed}/{cfg.variant}/trap/{corr.id}")
        if rng.random() >= chance:
            continue
        path, floors = corr.path, corr.floors
        spots = [i for i in range(4, len(path) - 4)
                 if _straight(path, i, corr.radius, axis_only=True) and floors[i - 1] == floors[i] == floors[i + 1]]
        if not spots:
            continue
        i = rng.choice(spots)
        (x, y), f = path[i], floors[i]
        axis = "x" if path[i + 1][0] != x else "y"
        kind = rng.choice(TRAPS)
        level.entities.append(Entity("trap", ((x + 0.5) * s, (y + 0.5) * s, f + 24),
                                     {"type": kind, "axis": axis, "corridor": corr.id, "width": 2 * corr.radius + 1}))
        made += 1
    if made:
        level.notes.append(f"traps: {made}")
    return made
