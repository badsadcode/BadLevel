"""Room features and gameplay entities.

Features change the column spans inside a room:
  pillars   - 2x2 solid columns (cover)
  platform  - raised block with a staircase (high ground)
  pit       - sunken area with a staircase
  balcony   - walkable deck along a wall, with open space *under* it
              (true room-over-room) and a long staircase up
  pedestal  - one-step plinth for the key

Every feature keeps the area around doorways clear, so connectivity is
preserved; the validator double-checks this afterwards.
"""
from __future__ import annotations

import math
import random
from typing import Dict, List, Optional, Set

from .config import GenConfig
from .layout import LayoutError, _straight
from .level import Cell, Entity, Level, Room


class RoomCtx:
    def __init__(self, level: Level, room: Room):
        self.level = level
        self.room = room
        self.used: Set[Cell] = set(room.world_diag())
        self.used.update(tuple(c) for c in room.meta.get("terrace", []))
        gallery = [tuple(c) for c in room.meta.get("gallery", [])]
        if gallery:  # the upper-storey gallery deck: keep features off it, put a sniper on it
            self.used.update(gallery)
            self.deck_cells = gallery
        self.doors = self._door_cells()
        self.clear: Set[Cell] = set()
        for (dx0, dy0) in self.doors:
            for dy in range(-3, 4):
                for dx in range(-3, 4):
                    c = (dx0 + dx, dy0 + dy)
                    if room.contains(*c):
                        self.clear.add(c)

    def _door_cells(self) -> List[Cell]:
        out = []
        for c in self.room.cells():
            if self.room.is_diag(*c):
                continue
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                n = (c[0] + dx, c[1] + dy)
                if not self.room.contains(*n) and self.level.spans_in(n, self.room.floor, self.room.ceil):
                    out.append(c)
                    break
        return out

    def free(self, cells) -> bool:
        return all(self.room.contains(*c) and c not in self.clear and c not in self.used
                   for c in cells)

    def set_floor(self, c: Cell, f: int):
        self.level.replace_in(c, *self.room.slot, [(f, self.room.ceil)])
        self.used.add(c)

    def make_solid(self, c: Cell):
        self.level.replace_in(c, *self.room.slot, [])
        self.used.add(c)


# ----------------------------------------------------------------------
# features
# ----------------------------------------------------------------------
def f_pillars(ctx: RoomCtx, rng: random.Random) -> bool:
    r = ctx.room
    if min(r.w, r.h) < 9:
        return False
    spacing = rng.choice([4, 5])
    placed = 0
    for py in range(r.y + 2, r.y + r.h - 3, spacing):
        for px in range(r.x + 2, r.x + r.w - 3, spacing):
            cells = [(px, py), (px + 1, py), (px, py + 1), (px + 1, py + 1)]
            # keep one free cell ring around every pillar
            ring = [(px + dx, py + dy) for dx in range(-1, 3) for dy in range(-1, 3)]
            if ctx.free(ring):
                for c in cells:
                    ctx.make_solid(c)
                for c in ring:
                    ctx.used.add(c)
                placed += 1
    if placed:
        r.features.append("pillars")
    return placed > 0


def _stair_block(ctx: RoomCtx, rng: random.Random, sign: int, name: str) -> bool:
    r = ctx.room
    if min(r.w, r.h) < 9:
        return False
    step = ctx.level.config.step_height
    for _ in range(40):
        pw = rng.randint(4, max(4, min(8, r.w - 5)))
        ph = rng.randint(4, max(4, min(8, r.h - 5)))
        px = rng.randint(r.x + 2, r.x + r.w - 2 - pw)
        py = rng.randint(r.y + 2, r.y + r.h - 2 - ph)
        rect = [(x, y) for y in range(py, py + ph) for x in range(px, px + pw)]
        border = [(x, y) for y in range(py - 1, py + ph + 1) for x in range(px - 1, px + pw + 1)]
        if not ctx.free(border):
            continue
        # stairs go in from one side: two strips of 3 cells, then the top
        side = rng.choice(["n", "s", "e", "w"])
        depth = {}
        for (x, y) in rect:
            d = {"w": x - px, "e": px + pw - 1 - x, "s": y - py, "n": py + ph - 1 - y}[side]
            along = y - py if side in ("w", "e") else x - px
            length = ph if side in ("w", "e") else pw
            centre = length // 2
            in_stair = abs(along - centre) <= 1
            level = 3 if (d >= 2 or not in_stair) else d + 1
            depth[(x, y)] = level
        for c, lvl in depth.items():
            ctx.set_floor(c, r.floor + sign * lvl * step)
        for c in border:
            ctx.used.add(c)
        top = [c for c, lvl in depth.items() if lvl == 3]
        ctx.room.features.append(name)
        ctx.top_cells = getattr(ctx, "top_cells", []) + top
        return True
    return False


def f_platform(ctx, rng):
    return _stair_block(ctx, rng, +1, "platform")


def f_pit(ctx, rng):
    return _stair_block(ctx, rng, -1, "pit")


def f_balcony(ctx: RoomCtx, rng: random.Random) -> bool:
    r = ctx.room
    cfg = ctx.level.config
    under, deck = 128, 144
    if r.ceil - r.floor < deck + 96:
        return False
    sides = ["n", "s", "e", "w"]
    rng.shuffle(sides)
    for side in sides:
        for stair_on_low_end in rng.sample([True, False], 2):
            # local frame: u runs along the balcony wall, v away from it
            if side in ("n", "s"):
                ulen, vlen = r.w, r.h
            else:
                ulen, vlen = r.h, r.w
            n_steps = under // cfg.step_height  # 8
            if vlen < 3 + n_steps + 2 or ulen < 6:
                continue

            def to_cell(u, v):
                if side == "s":
                    return (r.x + u, r.y + v)
                if side == "n":
                    return (r.x + u, r.y + r.h - 1 - v)
                if side == "w":
                    return (r.x + v, r.y + u)
                return (r.x + r.w - 1 - v, r.y + u)

            # longest straight stretch of wall where a 3-deep deck fits the room shape
            ok_u = [all(r.contains(*to_cell(u, v)) and to_cell(u, v) not in ctx.used for v in range(3))
                    for u in range(ulen)]
            best, run = (0, 0), None
            for u, ok in enumerate(ok_u + [False]):
                if ok and run is None:
                    run = u
                elif not ok and run is not None:
                    if u - run > best[1] - best[0]:
                        best = (run, u)
                    run = None
            ua, ub = best
            if ub - ua < 6:
                continue
            deck_cells = [to_cell(u, v) for u in range(ua, ub) for v in range(3)]
            u0 = ua if stair_on_low_end else ub - 2
            stairs = {}
            for k in range(n_steps):
                for du in range(2):
                    stairs[to_cell(u0 + du, 3 + k)] = r.floor + under - k * cfg.step_height
            # deck cells may sit over doorways (corridors pass underneath),
            # stairs may not
            if not ctx.free(list(stairs)) or any(c in ctx.used or not r.contains(*c) for c in deck_cells):
                continue
            for c in deck_cells:
                ctx.level.replace_in(c, *r.slot, [(r.floor, r.floor + under), (r.floor + deck, r.ceil)])
                ctx.used.add(c)
            for c, f in stairs.items():
                ctx.set_floor(c, f)
            ctx.room.features.append("balcony")
            ctx.deck_cells = deck_cells
            return True
    return False


def f_pedestal(ctx: RoomCtx, rng: random.Random) -> bool:
    r = ctx.room
    block = _flat_block(ctx, r.x + r.w / 2.0, r.y + r.h / 2.0, avoid=ctx.clear | ctx.used)
    if block is None:
        return False
    cells = block
    for c in cells:
        ctx.set_floor(c, r.floor + ctx.level.config.step_height)
    r.features.append("pedestal")
    ctx.pedestal = cells
    return True


def f_crates(ctx: RoomCtx, rng: random.Random) -> bool:
    """Scatter crates / low walls: 40-unit blocks hide a crouching player (low cover),
    some 64-unit ones block sight entirely. Each keeps a walkable ring around it."""
    r = ctx.room
    area = len(list(r.cells()))
    want = min(8, max(1, area // 28))
    cells = list(r.cells())
    rng.shuffle(cells)
    placed = 0
    for (x, y) in cells:
        if placed >= want:
            break
        w, h = rng.choice([(1, 1), (2, 1), (1, 2), (2, 2), (3, 1), (1, 3)])
        block = [(x + dx, y + dy) for dx in range(w) for dy in range(h)]
        ring = [(x + dx, y + dy) for dx in range(-1, w + 1) for dy in range(-1, h + 1)]
        if not ctx.free(ring):
            continue
        height = 64 if rng.random() < 0.25 else 40
        for c in block:
            ctx.set_floor(c, r.floor + height)
        for c in ring:
            ctx.used.add(c)
        placed += 1
    if placed:
        r.features.append("crates")
    return placed > 0


CRATE_CHANCE = {"combat": 0.8, "arena": 0.7, "hub": 0.6, "supply": 0.7, "exit": 0.5, "start": 0.4}

FEATURES_BY_KIND = {
    "start": [[f_pillars], []],
    "arena": [[f_balcony, f_pillars], [f_pit, f_pillars], [f_platform, f_pillars], [f_balcony, f_platform]],
    "hub": [[f_balcony], [f_platform], [f_pillars, f_platform]],
    "combat": [[f_pillars], [f_platform], [f_pit], [f_balcony], []],
    "supply": [[], [f_pillars]],
    "key": [[f_pedestal]],
    "secret": [[]],
    "exit": [[]],
}


# ----------------------------------------------------------------------
# entities
# ----------------------------------------------------------------------
def _pick_free(ctx: RoomCtx, rng: random.Random, taken: Set[Cell], floor_only=True) -> Optional[Cell]:
    r = ctx.room
    cands = []
    for c in r.cells():
        spans = ctx.level.room_spans(c, r)
        if not spans or len(spans) != 1 or c in taken or c in ctx.clear or c in ctx.level.diag:
            continue
        if floor_only and spans[0][0] != r.floor:
            continue
        if c in ctx.used and floor_only:
            # pillar rings / platform borders are fine to stand on if flat
            pass
        cands.append(c)
    if not cands:
        return None
    c = rng.choice(cands)
    taken.add(c)
    return c


def _flat_cell(level: Level, room: Room, c) -> bool:
    spans = level.room_spans(c, room)
    return bool(spans) and len(spans) == 1 and spans[0][0] == room.floor and c not in level.diag \
        and room.contains(*c)


def _flat_block(ctx: RoomCtx, fx: float, fy: float, avoid=()):
    """2x2 block of flat floor cells nearest to the (fractional) cell point fx, fy."""
    best = None
    for (x, y) in ctx.room.cells():
        cells = [(x, y), (x + 1, y), (x, y + 1), (x + 1, y + 1)]
        if all(_flat_cell(ctx.level, ctx.room, c) and c not in avoid for c in cells):
            d = (x + 1 - fx) ** 2 + (y + 1 - fy) ** 2
            if best is None or d < best[0]:
                best = (d, cells)
    return best[1] if best else None


def _nearest_open(level: Level, room: Room, fx: float, fy: float):
    cands = [c for c in room.cells() if c not in level.diag and level.room_spans(c, room)]
    return min(cands, key=lambda c: (c[0] + 0.5 - fx) ** 2 + (c[1] + 0.5 - fy) ** 2)


def _at(level: Level, room: Room, c: Cell, top=False, z_off=24.0):
    spans = level.room_spans(c, room)
    f = spans[-1][0] if top else spans[0][0]
    return ((c[0] + 0.5) * level.cell, (c[1] + 0.5) * level.cell, f + z_off)


def _door_entity(level: Level, corr, kind: str) -> Entity:
    cfg = level.config
    r = corr.radius
    path, floors = corr.path, corr.floors
    best = None
    for i in range(r + 2, len(path) - r - 2):
        if _straight(path, i, r, axis_only=True) and floors[i - 1] == floors[i] == floors[i + 1]:
            # secret doors hug the visible room side, locked doors sit mid way
            score = i if kind == "secret_door" else abs(i - len(path) // 2)
            if best is None or score < best[0]:
                best = (score, i)
    if best is None:
        raise LayoutError(f"no spot for {kind} in corridor {corr.id}")
    i = best[1]
    (cx, cy), f = path[i], floors[i]
    dx = path[i + 1][0] - path[i][0]
    s = level.cell
    if dx:  # corridor runs along X -> door spans Y
        lo = (cx * s, (cy - r) * s, f)
        hi = ((cx + 1) * s, (cy + r + 1) * s, f + corr.height)
    else:
        lo = ((cx - r) * s, cy * s, f)
        hi = ((cx + r + 1) * s, (cy + 1) * s, f + corr.height)
    pos = tuple((a + b) / 2 for a, b in zip(lo, hi))
    props = {"corridor": corr.id}
    if corr.key:
        props["key"] = corr.key
    return Entity(kind, pos, props, (lo, hi))


def decorate(level: Level, rng: random.Random):
    """Each room's space type adds its features, then its contents; then the corridors' contents.
    (Two passes over the rooms, in this order, so the random sequence is stable.) With an
    ``arch_style``, a room's cross-section puts its piers in before the features and shapes
    its ceilings after them (see architecture.py; it has its own random stream)."""
    from . import architecture, walls
    from .spaces import space_for
    from .spaces import corridor as corridor_space
    ctxs: Dict[int, RoomCtx] = {}
    sections = architecture.plan(level)        # {} without an arch_style: nothing changes
    tiers = architecture.rank_tiers(level) if sections or level.config.arch_style else {}
    for room in level.rooms:
        ctx = RoomCtx(level, room)
        ctxs[room.id] = ctx
        sec = sections.get(room.id)
        piers = architecture.place_piers(level, room, sec, ctx) if sec else 0
        if room.id in tiers:
            wall = walls.articulate(level, room, ctx, sec, tiers[room.id])
            if wall:
                room.meta["walls"] = wall
        space_for(room.kind).decorate(level, room, ctx, rng)
        if sec:
            lowered = architecture.apply_ceilings(level, room, sec)
            room.meta["arch"] = dict(sec.info, style=level.config.arch_style, profile=sec.name,
                                     axis="x" if sec.frame.along_x else "y", piers=piers, shaped_cells=lowered)
            room.features.append(sec.name)
    info = {"max_depth": max(n.depth for n in level.graph.nodes) or 1}
    for room in level.rooms:
        space_for(room.kind).populate(level, room, ctxs[room.id], rng, info)
    for corr in level.corridors:
        corridor_space.populate(level, corr, rng, info)
