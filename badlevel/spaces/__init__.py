"""Space types: every kind of space BadLevel can generate, behind one interface.

A *space type* knows how to be one kind of place. The core pipeline (mission
graph -> placement -> routing -> carving -> validation -> analysis) asks the
registry for the type of each room and calls these hooks in order:

  propose(node, floor, ctx)      -> a Room (size, heights, meta) or None if it can't exist here
  assign_shape(room, node, ctx)  -> footprint mask / 45-degree cells after placement
  build(level, room)             -> write its open spans into the grid
  attachment(room, other)        -> (port cells or None, floor) where a corridor to ``other`` attaches
  decorate(level, room, ctx)     -> internal features (pillars, platforms, balconies...)
  populate(level, room, ctx)     -> gameplay entities (lights, start, keys, enemies, pickups...)
  regions(room)                  -> semantic regions it exposes (default: itself)

Adding a new kind of space (open areas, mega-rooms...) means writing one module
that implements these and registering it; the core does not change. Corridors and
merged halls live here too (``corridor``, ``hall``) but are relationships between
rooms rather than room types, so they have their own small modules.
"""
from __future__ import annotations

import random
from typing import Dict, List, Optional, Tuple

from ..level import Level, Room


class SpaceType:
    name = "space"
    kinds: Tuple[str, ...] = ()
    vertical = False          # joins storeys (a connector with a low and a high port)

    # --- planning / placement ------------------------------------------------
    def propose(self, node, floor: int, cfg, rng: random.Random, multi: bool) -> Optional[Room]:
        raise NotImplementedError

    def assign_shape(self, room: Room, node, cfg, rng: random.Random, merged: bool):
        pass

    # --- geometry ---------------------------------------------------------------
    def build(self, level: Level, room: Room, cfg):
        for c in room.cells():
            level.add_span(c, (room.floor, room.ceil), f"room:{room.id}", slot=room.slot)
        for c, corner in room.world_diag().items():
            level.diag[c] = corner
            level.diag_span[c] = (room.floor, room.ceil)

    def attachment(self, room: Room, other: Room):
        return None, room.floor

    # --- content ------------------------------------------------------------------
    def feature_options(self, room: Room) -> List[list]:
        return [[]]

    def crate_chance(self, room: Room) -> float:
        return 0.0

    def decorate(self, level: Level, room: Room, ctx, rng: random.Random):
        """Pick one weighted option of internal features, then maybe crates."""
        from ..decorate import f_crates
        options = self.feature_options(room)
        fw = level.config.feature_weights

        def option_weight(opt):
            w = 1.0
            for f in opt:
                w *= float(fw.get(f.__name__[2:], 1.0))
            return w if opt else float(fw.get("none", 1.0))
        ws = [option_weight(o) for o in options]
        chosen = rng.choices(options, ws)[0] if sum(ws) > 0 else []
        for feat in chosen:
            feat(ctx, rng)
        if rng.random() < min(1.0, self.crate_chance(room) * level.config.crate_scale):
            f_crates(ctx, rng)

    def populate(self, level: Level, room: Room, ctx, rng: random.Random, info: dict):
        place_lights(level, room)

    def regions(self, room: Room) -> List[int]:
        return [room.id]


def place_lights(level: Level, room: Room, max_height: Optional[int] = None, intensity: Optional[int] = None):
    from ..decorate import _nearest_open
    from ..level import Entity
    area = room.w * room.h
    spots = [(0.25, 0.25), (0.75, 0.25), (0.25, 0.75), (0.75, 0.75)] if area >= 140 else [(0.5, 0.5)]
    lit = set()
    for fx, fy in spots:
        c = _nearest_open(level, room, room.x + room.w * fx, room.y + room.h * fy)
        if c in lit:
            continue
        lit.add(c)
        z = level.room_spans(c, room)[-1][1] - 24
        if max_height is not None:
            z = min(z, room.floor + max_height)
        level.entities.append(Entity("light", ((c[0] + 0.5) * level.cell, (c[1] + 0.5) * level.cell, z),
                                     {"intensity": intensity or (250 if len(spots) > 1 else 300)}))


REGISTRY: Dict[str, SpaceType] = {}


def register(space: SpaceType) -> SpaceType:
    for k in space.kinds:
        REGISTRY[k] = space
    return space


def space_for(kind: str) -> SpaceType:
    try:
        return REGISTRY[kind]
    except KeyError:
        raise KeyError(f"no space type registered for room kind {kind!r}") from None


def vertical_kinds() -> Tuple[str, ...]:
    return tuple(k for k, s in REGISTRY.items() if s.vertical)


# built-in space types register themselves on import
from . import room as _room          # noqa: E402,F401
from . import connector as _conn     # noqa: E402,F401
from . import open_area as _open     # noqa: E402,F401
from . import mega as _mega          # noqa: E402,F401
