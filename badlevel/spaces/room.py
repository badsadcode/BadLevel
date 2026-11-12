"""Rooms: the classic enclosed space, in all its gameplay roles.

One space type covers start, combat, arena, hub, supply, key, secret and exit
rooms: same geometry (a footprint shape on a floor height, optionally tall),
different features and contents per role.
"""
from __future__ import annotations

import math
import random
from typing import Optional, Set

from ..level import Cell, Entity, Level, Room
from . import SpaceType, place_lights, register

ROLES = ("start", "combat", "arena", "hub", "supply", "key", "secret", "exit")


class RoomSpace(SpaceType):
    name = "room"
    kinds = ROLES
    ENEMY_AREA = {"combat": 28, "arena": 20, "hub": 40, "key": 45, "supply": 80, "exit": 60}   # m^2 per enemy
    PICKUPS = {"supply": 3, "combat": 1, "arena": 2, "hub": 1, "secret": 2, "key": 1}
    LIGHT_MAX = None          # cap light height above the floor (open areas)
    LIGHT_POWER = None

    def propose(self, node, floor: int, cfg, rng: random.Random, multi: bool) -> Optional[Room]:
        from ..layout import _room_size
        H = cfg.storey_height
        w, h, height = _room_size(cfg, node.size, rng)
        if multi:
            if node.kind in ("arena", "hub") and rng.random() < cfg.tall_room_chance:
                height = rng.randrange(H + 96, 2 * H - 32 + 1, 16)      # reaches into the next storey
            else:
                height = min(height, H - 64)                           # leaves room for a storey above
        return Room(-1, node.id, node.kind, 0, 0, w, h, floor, floor + height, storey=node.storey)

    def assign_shape(self, room: Room, node, cfg, rng: random.Random, merged: bool):
        if merged:
            return                       # merged rooms stay rectangular so their seams line up
        from ..shapes import pick_shape
        shape = pick_shape(room.w, room.h, node.size, node.kind, rng, cfg.shape_weights)
        room.shape = shape.name
        if shape.name != "rect" or shape.diag:
            room.mask, room.diag = shape.cells, shape.diag

    def feature_options(self, room: Room):
        from ..decorate import FEATURES_BY_KIND
        return FEATURES_BY_KIND.get(room.kind, [[]])

    def crate_chance(self, room: Room) -> float:
        from ..decorate import CRATE_CHANCE
        return CRATE_CHANCE.get(room.kind, 0.0)

    def populate(self, level: Level, room: Room, ctx, rng: random.Random, info: dict):
        from ..decorate import _at, _flat_block, _flat_cell, _pick_free
        from ..layout import LayoutError
        g = level.graph
        ents = level.entities
        node = g.nodes[room.node]
        taken: Set[Cell] = set()
        area = room.w * room.h
        tier = 1 + min(2, (node.depth * 3) // (info["max_depth"] + 1))
        cx, cy = room.x + room.w / 2.0, room.y + room.h / 2.0

        place_lights(level, room, self.LIGHT_MAX, self.LIGHT_POWER)

        if room.kind == "start":
            c = min((c for c in room.cells() if _flat_cell(level, room, c)),
                    key=lambda c: (c[0] + 0.5 - cx) ** 2 + (c[1] + 0.5 - cy) ** 2)
            taken.add(c)
            angle = 0
            if ctx.doors:
                dx = sum(d[0] for d in ctx.doors) / len(ctx.doors) + 0.5 - (c[0] + 0.5)
                dy = sum(d[1] for d in ctx.doors) / len(ctx.doors) + 0.5 - (c[1] + 0.5)
                angle = int(round(math.degrees(math.atan2(dy, dx)) / 45.0)) * 45 % 360
            ents.append(Entity("player_start", _at(level, room, c), {"angle": angle}))
            c2 = _pick_free(ctx, rng, taken)
            if c2:
                ents.append(Entity("weapon", _at(level, room, c2, z_off=0), {"type": "shotgun"}))
        elif room.kind == "exit":
            block = _flat_block(ctx, cx, cy)
            if block is None:
                raise LayoutError("no spot for the exit")
            ex, ey = block[0]
            s = level.cell
            lo = (ex * s, ey * s, room.floor)
            hi = ((ex + 2) * s, (ey + 2) * s, room.floor + 64)
            ents.append(Entity("exit", ((ex + 1) * s, (ey + 1) * s, room.floor + 32), {}, (lo, hi)))
            for x in (ex, ex + 1):
                for y in (ey, ey + 1):
                    taken.add((x, y))
        elif room.kind == "key":
            ped = getattr(ctx, "pedestal", None)
            c = ped[0] if ped else _pick_free(ctx, rng, taken)
            if c is None:
                raise LayoutError("no spot for key")
            taken.update(ped or [c])
            ents.append(Entity("key", _at(level, room, c, z_off=24), {"color": node.key}))

        # enemies
        per = self.ENEMY_AREA.get(room.kind)
        if per:
            dens = level.config.enemy_density
            count = int(round(max(1, area // per) * dens)) if dens > 0 else 0
            for _ in range(max(1, count) if dens > 0 else 0):
                c = _pick_free(ctx, rng, taken)
                if c:
                    ents.append(Entity("enemy", _at(level, room, c), {"tier": tier}))
        # high ground gets a sniper and a reward
        for attr in ("deck_cells", "top_cells"):
            cells = [c for c in getattr(ctx, attr, []) if c not in taken]
            if cells and rng.random() < min(1.0, level.config.enemy_density):
                c = rng.choice(cells)
                taken.add(c)
                ents.append(Entity("enemy", _at(level, room, c, top=True), {"tier": tier}))
                cells.remove(c)
                if cells:
                    c = rng.choice(cells)
                    taken.add(c)
                    ents.append(Entity("armor" if room.kind == "arena" else "ammo",
                                       _at(level, room, c, top=True, z_off=0), {}))

        # pickups
        n_items = self.PICKUPS.get(room.kind, 0)
        n_items = int(round(n_items * level.config.pickup_density))
        for k in range(n_items):
            c = _pick_free(ctx, rng, taken)
            if not c:
                break
            if room.kind == "secret":
                kind = ["megahealth", "weapon"][k % 2]
                props = {"type": "rocket_launcher"} if kind == "weapon" else {}
            else:
                kind = rng.choice(["health", "health", "ammo", "armor" if tier > 1 else "ammo"])
                props = {}
            ents.append(Entity(kind, _at(level, room, c, z_off=0), props))


register(RoomSpace())
