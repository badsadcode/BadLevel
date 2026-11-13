"""Vertical connectors as space types: switchback stairwells, lift shafts, ladder shafts, spiral stairs.

The geometry itself lives in ``badlevel.connectors`` (local frame, ports, spans,
traversal links); this module plugs it into the space-type interface.
"""
from __future__ import annotations

import random
from typing import Optional

from ..level import Level, Room
from . import SpaceType, place_lights, register


class ConnectorSpace(SpaceType):
    vertical = True

    def __init__(self, kind: str):
        self.name = kind
        self.kinds = (kind,)

    def propose(self, node, floor: int, cfg, rng: random.Random, multi: bool) -> Optional[Room]:
        from ..connectors import connector_size
        top = node.top
        size = connector_size(node.kind, top - floor, rng)
        if size is None:
            return None
        w, h, meta = size
        room = Room(-1, node.id, node.kind, 0, 0, w, h, floor, top + 160, storey=node.storey,
                    top_floor=top, meta=meta)
        if node.kind == "spiral":                     # a round tower round a solid newel
            from ..connectors import _spiral_cells
            room.mask = set(_spiral_cells(w))
            room.shape = "round"
        return room

    def build(self, level: Level, room: Room, cfg):
        from ..connectors import build_connector
        build_connector(level, room, cfg.step_height)

    def attachment(self, room: Room, other: Room):
        from ..connectors import ports
        p = ports(room)
        target = other.top_floor if other.is_connector else other.floor
        key = "high" if abs(target - room.top_floor) < abs(target - room.floor) else "low"
        return p[key]

    def populate(self, level: Level, room: Room, ctx, rng: random.Random, info: dict):
        place_lights(level, room)        # no fights or pickups on the stairs


for _kind in ("stairwell", "lift", "ladder", "spiral"):
    register(ConnectorSpace(_kind))
