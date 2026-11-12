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
                if not self.room.contains(*n) and n in self.level.spans:
                    out.append(c)
                    break
        return out

    def free(self, cells) -> bool:
        return all(self.room.contains(*c) and c not in self.clear and c not in self.used
                   for c in cells)

    def set_floor(self, c: Cell, f: int):
        self.level.spans[c] = [(f, self.room.ceil)]
        self.used.add(c)

    def make_solid(self, c: Cell):
        self.level.spans.pop(c, None)
        self.level.owner.pop(c, None)
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
                ctx.level.spans[c] = [(r.floor, r.floor + under), (r.floor + deck, r.ceil)]
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
        spans = ctx.level.spans.get(c)
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
    spans = level.spans.get(c)
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
    cands = [c for c in room.cells() if c not in level.diag and level.spans.get(c)]
    return min(cands, key=lambda c: (c[0] + 0.5 - fx) ** 2 + (c[1] + 0.5 - fy) ** 2)


def _at(level: Level, c: Cell, top=False, z_off=24.0):
    f = level.floor_at(c, top=top)
    return ((c[0] + 0.5) * level.cell, (c[1] + 0.5) * level.cell, f + z_off)


def _door_entity(level: Level, corr, kind: str) -> Entity:
    cfg = level.config
    r = cfg.corridor_radius
    path, floors = corr.path, corr.floors
    best = None
    for i in range(r + 2, len(path) - r - 2):
        if _straight(path, i, r) and floors[i - 1] == floors[i] == floors[i + 1]:
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
        hi = ((cx + 1) * s, (cy + r + 1) * s, f + cfg.corridor_height)
    else:
        lo = ((cx - r) * s, cy * s, f)
        hi = ((cx + r + 1) * s, (cy + 1) * s, f + cfg.corridor_height)
    pos = tuple((a + b) / 2 for a, b in zip(lo, hi))
    props = {"corridor": corr.id}
    if corr.key:
        props["key"] = corr.key
    return Entity(kind, pos, props, (lo, hi))


def decorate(level: Level, rng: random.Random):
    g = level.graph
    ctxs: Dict[int, RoomCtx] = {}
    for room in level.rooms:
        ctx = RoomCtx(level, room)
        ctxs[room.id] = ctx
        options = FEATURES_BY_KIND.get(room.kind, [[]])
        for feat in rng.choice(options):
            feat(ctx, rng)

    ents = level.entities
    max_depth = max(n.depth for n in g.nodes) or 1
    for room in level.rooms:
        ctx = ctxs[room.id]
        node = g.nodes[room.node]
        taken: Set[Cell] = set()
        area = room.w * room.h
        tier = 1 + min(2, (node.depth * 3) // (max_depth + 1))

        # lights
        cx, cy = room.x + room.w / 2.0, room.y + room.h / 2.0
        spots = [(0.25, 0.25), (0.75, 0.25), (0.25, 0.75), (0.75, 0.75)] if area >= 140 else [(0.5, 0.5)]
        lit = set()
        for fx, fy in spots:
            c = _nearest_open(level, room, room.x + room.w * fx, room.y + room.h * fy)
            if c in lit:
                continue
            lit.add(c)
            ents.append(Entity("light", ((c[0] + 0.5) * level.cell, (c[1] + 0.5) * level.cell,
                                         level.spans[c][-1][1] - 24),
                               {"intensity": 250 if len(spots) > 1 else 300}))

        if room.kind == "start":
            c = min((c for c in room.cells() if _flat_cell(level, room, c)),
                    key=lambda c: (c[0] + 0.5 - cx) ** 2 + (c[1] + 0.5 - cy) ** 2)
            taken.add(c)
            angle = 0
            if ctx.doors:
                dx = sum(d[0] for d in ctx.doors) / len(ctx.doors) + 0.5 - (c[0] + 0.5)
                dy = sum(d[1] for d in ctx.doors) / len(ctx.doors) + 0.5 - (c[1] + 0.5)
                angle = int(round(math.degrees(math.atan2(dy, dx)) / 45.0)) * 45 % 360
            ents.append(Entity("player_start", _at(level, c), {"angle": angle}))
            c2 = _pick_free(ctx, rng, taken)
            if c2:
                ents.append(Entity("weapon", _at(level, c2, z_off=0), {"type": "shotgun"}))
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
            ents.append(Entity("key", _at(level, c, z_off=24), {"color": node.key}))

        # enemies
        per = {"combat": 28, "arena": 20, "hub": 40, "key": 45, "supply": 80, "exit": 60}.get(room.kind)
        if per:
            for _ in range(max(1, area // per)):
                c = _pick_free(ctx, rng, taken)
                if c:
                    ents.append(Entity("enemy", _at(level, c), {"tier": tier}))
        # high ground gets a sniper and a reward
        for attr in ("deck_cells", "top_cells"):
            cells = [c for c in getattr(ctx, attr, []) if c not in taken]
            if cells:
                c = rng.choice(cells)
                taken.add(c)
                ents.append(Entity("enemy", _at(level, c, top=True), {"tier": tier}))
                cells.remove(c)
                if cells:
                    c = rng.choice(cells)
                    taken.add(c)
                    ents.append(Entity("armor" if room.kind == "arena" else "ammo",
                                       _at(level, c, top=True, z_off=0), {}))

        # pickups
        n_items = {"supply": 3, "combat": 1, "arena": 2, "hub": 1, "secret": 2, "key": 1}.get(room.kind, 0)
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
            ents.append(Entity(kind, _at(level, c, z_off=0), props))

    # corridor lights and doors
    for corr in level.corridors:
        for i in range(3, len(corr.path) - 3, 7):
            (x, y), f = corr.path[i], corr.floors[i]
            ents.append(Entity("light", ((x + 0.5) * level.cell, (y + 0.5) * level.cell,
                                         f + level.config.corridor_height - 16), {"intensity": 200}))
        if corr.kind == "locked":
            ents.append(_door_entity(level, corr, "door"))
        elif corr.kind == "secret":
            ents.append(_door_entity(level, corr, "secret_door"))
