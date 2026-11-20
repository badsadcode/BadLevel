"""The wizard's tower as a space type (geometry in badlevel.tower)."""
from __future__ import annotations

import math
import random
from typing import Optional

from ..level import Entity, Level, Room
from . import SpaceType, register


class TowerSpace(SpaceType):
    name = "tower"
    kinds = ("tower",)
    vertical = True                     # joins storeys: a connector to placement and routing

    def propose(self, node, floor: int, cfg, rng: random.Random, multi: bool) -> Optional[Room]:
        from .. import tower
        S = max(2, cfg.storeys)
        H = cfg.storey_height
        R = rng.choice([8, 9, 9, 10])
        D = 2 * R + 1
        top = node.top if getattr(node, "top", None) is not None else floor + (S - 1) * H
        room = Room(-1, node.id, "tower", 0, 0, D, D, floor, floor + (S - 1) * H + 320, storey=node.storey,
                    top_floor=top, meta={"R": R, "storeys": S, "fw": 5})
        _, cells = tower._geom(R)
        room.mask = set(cells)
        room.shape = "round"
        tower.plan(room, H, rng)
        room.meta["landings"] = [floor + k * H for k in range(S)]
        return room

    def build(self, level: Level, room: Room, cfg):
        from .. import tower
        tower.build(level, room)

    def attachment(self, room: Room, other: Room):
        from .. import tower
        target = other.top_floor if other.is_connector and other.top_floor is not None else other.floor
        return min(tower.ports(room), key=lambda p: abs(p[1] - target))

    def populate(self, level: Level, room: Room, ctx, rng: random.Random, info: dict):
        """Light at every landing and half way round each lap, over the helix; one at the top."""
        from .. import tower
        m = room.meta
        hx = tower.Helix(m["R"], m["storeys"], m["H"], m["theta0"])
        s = level.cell
        c = room.w / 2.0
        r = m["R"] - tower.RING / 2
        for k in range(m["storeys"]):
            for extra in (0.0, 0.5):
                phi = m["theta0"] + (k + extra) * hx.per
                h = hx.height(phi)
                if h is None:
                    continue
                ang = phi % (2 * math.pi)
                x = (room.x + c + r * math.cos(ang)) * s
                y = (room.y + c + r * math.sin(ang)) * s
                level.entities.append(Entity("light", (x, y, room.floor + h + 120), {"intensity": 260}))
        level.entities.append(Entity("light", ((room.x + c) * s, (room.y + c) * s, room.ceil - 96),
                                     {"intensity": 380}))


register(TowerSpace())
