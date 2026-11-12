"""Open areas: courtyards, plazas, yards. Spaces under the sky.

What makes an open area different from a big room:

  * **sky instead of a ceiling**: the column is open up to sky height and its top
    face is sky (a Quake ``sky`` texture in the .map, ``bl_sky`` in the .glb).
  * **the perimeter reads as building fronts**: pilasters in a regular rhythm along
    the walls, doorways where corridors arrive, windows from neighbouring spaces.
  * **contents are a choice, including nothing**: an empty yard (breathing room), a
    kiosk or monument in the middle, a colonnade, a sunken plaza, a raised terrace,
    or an arcade with a walkway on top. Recipes weight these like any room feature.

It reuses the room machinery (placement, shapes, gameplay contents) and only
changes what makes it outdoors. Turned on by ``GenConfig.open_areas`` (recipes:
openness), off by default.
"""
from __future__ import annotations

import random
from typing import Optional

from ..level import Level, Room
from . import register
from .room import RoomSpace


def f_facade(ctx, rng: random.Random) -> bool:
    """Pilasters every few cells along the yard's walls: building fronts, not room walls.
    Never in front of a doorway, never on a 45-degree corner."""
    room, level = ctx.room, ctx.level
    step = rng.choice([3, 4])
    placed = 0
    for c in room.cells():
        if c in ctx.used or c in ctx.clear or room.is_diag(*c):
            continue
        # a cell against the outer wall, on one of the yard's straight edges
        outside = [(dx, dy) for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))
                   if not room.contains(c[0] + dx, c[1] + dy) and not level.spans_in((c[0] + dx, c[1] + dy),
                                                                                    room.floor, room.ceil)]
        if len(outside) != 1:
            continue
        dx, dy = outside[0]
        along = c[1] if dx else c[0]
        if along % step:
            continue
        # keep a walkway: the cells beside and in front must stay open
        front = (c[0] - dx, c[1] - dy)
        if not room.contains(*front) or front in ctx.used:
            continue
        ctx.make_solid(c)
        placed += 1
    if placed:
        room.features.append("facade")
    return placed > 0


def f_colonnade(ctx, rng: random.Random) -> bool:
    """Free-standing columns / planters in a grid. Outdoors there is no ceiling to hold up,
    so they stop at 5-7 m with open sky above (indoor pillars run floor to ceiling)."""
    room = ctx.room
    if min(room.w, room.h) < 9:
        return False
    spacing = rng.choice([4, 5])
    height = rng.choice([160, 192, 224])
    size = rng.choice([1, 2])
    placed = 0
    for py in range(room.y + 2, room.y + room.h - 3, spacing):
        for px in range(room.x + 2, room.x + room.w - 3, spacing):
            cells = [(px + i, py + j) for i in range(size) for j in range(size)]
            ring = [(px + dx, py + dy) for dx in range(-1, size + 1) for dy in range(-1, size + 1)]
            if ctx.free(ring):
                for c in cells:
                    ctx.set_floor(c, room.floor + height)
                for c in ring:
                    ctx.used.add(c)
                placed += 1
    if placed:
        room.features.append("colonnade")
    return placed > 0


def f_kiosk(ctx, rng: random.Random) -> bool:
    """A solid structure in the middle of the yard: kiosk, monument, guard post. A landmark
    and cover, not a room (rooms inside open areas come with mega-rooms)."""
    room = ctx.room
    for _ in range(20):
        w, h = rng.randint(3, 6), rng.randint(3, 6)
        x = room.x + room.w // 2 - w // 2 + rng.randint(-2, 2)
        y = room.y + room.h // 2 - h // 2 + rng.randint(-2, 2)
        block = [(x + i, y + j) for i in range(w) for j in range(h)]
        ring = [(x + i, y + j) for i in range(-2, w + 2) for j in range(-2, h + 2)]
        if not ctx.free(ring):
            continue
        height = rng.choice([96, 128, 160, 192])
        for c in block:
            ctx.set_floor(c, room.floor + height)    # its roof: no way up (yet)
        for c in ring:
            ctx.used.add(c)
        room.features.append("kiosk")
        return True
    return False


class OpenAreaSpace(RoomSpace):
    name = "open_area"
    kinds = ("courtyard",)
    ENEMY_AREA = dict(RoomSpace.ENEMY_AREA, courtyard=45)
    PICKUPS = dict(RoomSpace.PICKUPS, courtyard=2)
    LIGHT_MAX = 224               # lamps on posts and walls, not up at the sky
    LIGHT_POWER = 400

    def propose(self, node, floor: int, cfg, rng: random.Random, multi: bool) -> Optional[Room]:
        from ..layout import _room_size
        w, h, height = _room_size(cfg, node.size, rng)     # 'yard' size: where the sky starts
        height = max(288, min(448, height))                  # 9-14 m, whatever the recipe's ceilings
        return Room(-1, node.id, node.kind, 0, 0, w, h, floor, floor + height, storey=node.storey)

    def build(self, level: Level, room: Room, cfg):
        super().build(level, room, cfg)
        level.sky.update(room.cells())

    def feature_options(self, room: Room):
        from ..compound import f_buildings
        from ..decorate import f_balcony, f_pit, f_platform
        return [[], [], [f_kiosk], [f_colonnade], [f_pit], [f_platform], [f_balcony], [f_kiosk, f_colonnade],
                [f_buildings], [f_buildings, f_colonnade]]

    def crate_chance(self, room: Room) -> float:
        return 0.45

    def decorate(self, level: Level, room: Room, ctx, rng: random.Random):
        if getattr(level.config, "open_roofscape", False):
            from ..compound import roofscape
            roofscape(level, room, random.Random(f"{level.config.seed}/{level.config.variant}/roofs/{room.id}"))
        if rng.random() < 0.8:
            f_facade(ctx, rng)
        yb = getattr(level.config, "open_buildings", 0.0)
        if yb > 0:
            brng = random.Random(f"{level.config.seed}/{level.config.variant}/buildings/{room.id}")
            if brng.random() < yb:
                from ..compound import f_buildings
                f_buildings(ctx, brng)                 # a compound's yards are places between buildings
        super().decorate(level, room, ctx, rng)
        if room.meta.get("roofscape"):
            # a pilaster against a roofscape stops at the roof beside it, not at the sky
            for c in room.cells():
                if c in level.spans:
                    continue
                roofs = [sp[0] for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))
                         for sp in level.spans.get((c[0] + dx, c[1] + dy), [])
                         if level.owner_of((c[0] + dx, c[1] + dy), sp) == f"roof:{room.id}"]
                if roofs:
                    level.add_span(c, (min(roofs), room.ceil), f"room:{room.id}", slot=room.slot)
        # pilasters and other solid cells are no longer open to the sky
        level.sky.difference_update([c for c in room.cells() if c not in level.spans])


register(OpenAreaSpace())
