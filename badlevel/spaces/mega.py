"""Mega-rooms: a level within the level.

A mega-room is a huge, tall space that runs its own small generation inside its
footprint. Instead of rooms and corridors it builds *sub-areas* joined by open
transitions, all in one continuous volume:

  platform   a raised block, reached by stairs
  deck       a platform on stilts: you can walk underneath it
  catwalk    a bridge between two decks at the same height, open floor below
  structure  a large solid mass (machinery, cargo, a monolith)
  tier       a terrace along a wall, several stairs up (atrium / great hall)

Each sub-area is a semantic region (``room.meta["regions"]``) for encounters,
diagnostics and editors.

Not every mega-room is an arena. Its *character* picks the vocabulary:

  hangar    vast and mostly empty: a few big structures, maybe a loading dock
  works     an industrial hall: decks, catwalks, stairs, clutter
  terraces  an atrium or great hall: tiers stepping up from the walls

Everything is placed incrementally with its stairs; a placement that would cut the
floor into islands or block a doorway is undone, so every region stays reachable.
"""
from __future__ import annotations

import random
from typing import Dict, List, Optional, Set, Tuple

from ..level import Cell, Level, Room
from . import register
from .room import RoomSpace

STYLES = ("hangar", "works", "terraces")


class _Builder:
    """Places sub-areas inside one mega-room, with undo."""

    def __init__(self, ctx, rng: random.Random):
        self.ctx, self.rng = ctx, rng
        self.room, self.level = ctx.room, ctx.level
        self.f0, self.top = ctx.room.floor, ctx.room.ceil
        self.occupied: Set[Cell] = set()          # platform / stair / structure cells
        self.regions: List[dict] = []
        self.tops: List[Cell] = []                # walkable tops (snipers, rewards)

    # -- bookkeeping -------------------------------------------------------------------
    def _snapshot(self, cells):
        return {c: list(self.level.spans.get(c, [])) for c in cells}

    def _restore(self, snap):
        for c, spans in snap.items():
            if spans:
                self.level.spans[c] = spans
            else:
                self.level.spans.pop(c, None)

    def _ok(self, cells) -> bool:
        return all(self.room.contains(*c) and not self.room.is_diag(*c) and c not in self.ctx.clear
                   and c not in self.ctx.used and c not in self.occupied for c in cells)

    def _floor_connected(self) -> bool:
        """The room's base floor (cells walkable at f0) must remain one connected area."""
        base = [c for c in self.room.cells()
                if any(sp[0] == self.f0 for sp in self.level.room_spans(c, self.room))]
        if not base:
            return False
        base_set = set(base)
        seen, stack = {base[0]}, [base[0]]
        while stack:
            x, y = stack.pop()
            for n in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
                if n in base_set and n not in seen:
                    seen.add(n)
                    stack.append(n)
        return len(seen) == len(base_set)

    def _set(self, c: Cell, spans):
        self.level.replace_in(c, *self.room.slot, spans)

    # -- pieces ----------------------------------------------------------------------------
    def stairs_for(self, rect, height: int, deck: bool) -> Optional[List[Tuple[Cell, int]]]:
        """A 2-wide stair from the base floor up to one side of rect: [(cell, floor)]."""
        x0, y0, x1, y1 = rect
        n = height // 16
        sides = ["n", "s", "e", "w"]
        self.rng.shuffle(sides)
        for side in sides:
            for off in self.rng.sample(range(max(1, (x1 - x0 if side in "ns" else y1 - y0) - 1)),
                                       min(3, max(1, (x1 - x0 if side in "ns" else y1 - y0) - 1))):
                steps = []
                for j in range(n):                  # j = 0 next to the platform; j = n-1: the landing
                    for w in (0, 1):
                        if side == "s":
                            c = (x0 + off + w, y0 - 1 - j)
                        elif side == "n":
                            c = (x0 + off + w, y1 + j)
                        elif side == "w":
                            c = (x0 - 1 - j, y0 + off + w)
                        else:
                            c = (x1 + j, y0 + off + w)
                        steps.append((c, self.f0 + height - 16 * (j + 1)))
                cells = [c for c, _ in steps]
                # the last row is the landing at floor level: kept clear, so nothing blocks the stairs
                if self._ok(cells) and len(set(cells)) == len(cells):
                    return steps
        return None

    def platform(self, w: int, h: int, height: int, deck: bool, name: str, against_wall: bool = False,
                 near: Optional[list] = None):
        """A raised block (or a deck on stilts) with its stairs, placed somewhere free.
        ``near``: try first to line up with that rect (same band, a catwalk's gap away)."""
        r = self.room
        for attempt in range(30):
            if near is not None and attempt < 15:
                nx0, ny0, nx1, ny1 = near
                gap = self.rng.randint(4, 9)
                if self.rng.random() < 0.5:
                    x0 = nx1 + gap if self.rng.random() < 0.5 else nx0 - gap - w
                    y0 = ny0 + self.rng.randint(-1, max(-1, ny1 - ny0 - h + 1))
                else:
                    y0 = ny1 + gap if self.rng.random() < 0.5 else ny0 - gap - h
                    x0 = nx0 + self.rng.randint(-1, max(-1, nx1 - nx0 - w + 1))
            elif against_wall:
                side = self.rng.choice("nsew")
                if side in "ns":
                    x0 = self.rng.randint(r.x + 1, r.x + r.w - 1 - w)
                    y0 = r.y + 1 if side == "s" else r.y + r.h - 1 - h
                else:
                    y0 = self.rng.randint(r.y + 1, r.y + r.h - 1 - h)
                    x0 = r.x + 1 if side == "w" else r.x + r.w - 1 - w
            else:
                x0 = self.rng.randint(r.x + 2, max(r.x + 2, r.x + r.w - 2 - w))
                y0 = self.rng.randint(r.y + 2, max(r.y + 2, r.y + r.h - 2 - h))
            rect = (x0, y0, x0 + w, y0 + h)
            cells = [(x, y) for x in range(x0, x0 + w) for y in range(y0, y0 + h)]
            ring = [(x, y) for x in range(x0 - 1, x0 + w + 1) for y in range(y0 - 1, y0 + h + 1)]
            if not self._ok(cells) or any(c in self.occupied for c in ring):
                continue
            steps = self.stairs_for(rect, height, deck)
            if steps is None:
                continue
            snap = self._snapshot(cells + [c for c, _ in steps])
            for c in cells:
                self._set(c, [(self.f0, self.f0 + height - 16), (self.f0 + height, self.top)] if deck
                          else [(self.f0 + height, self.top)])
            for c, f in steps:
                if f > self.f0:
                    self._set(c, [(f, self.top)])   # (the landing row stays floor)
            if not self._floor_connected():
                self._restore(snap)
                continue
            self.occupied.update(cells)
            self.occupied.update(c for c, _ in steps)
            self.tops.extend(cells)
            self.regions.append({"name": name, "kind": "deck" if deck else "platform", "height": height,
                                 "rect": list(rect), "cells": [list(c) for c in cells]})
            return rect
        return None

    def catwalk(self, a: dict, b: dict) -> bool:
        """A 2-wide bridge between two decks of the same height, if they face each other."""
        if a["height"] != b["height"] or a["kind"] != "deck" or b["kind"] != "deck":
            return False
        ax0, ay0, ax1, ay1 = a["rect"]
        bx0, by0, bx1, by1 = b["rect"]
        h = a["height"]
        cells = []
        lo, hi = max(ay0, by0), min(ay1, by1)          # overlap in y: bridge runs along x
        if hi - lo >= 2 and (ax1 <= bx0 or bx1 <= ax0):
            xa, xb = (ax1, bx0) if ax1 <= bx0 else (bx1, ax0)
            yc = (lo + hi) // 2 - 1
            cells = [(x, y) for x in range(xa, xb) for y in (yc, yc + 1)]
        else:
            lo, hi = max(ax0, bx0), min(ax1, bx1)
            if hi - lo >= 2 and (ay1 <= by0 or by1 <= ay0):
                ya, yb = (ay1, by0) if ay1 <= by0 else (by1, ay0)
                xc = (lo + hi) // 2 - 1
                cells = [(x, y) for y in range(ya, yb) for x in (xc, xc + 1)]
        if not cells or len(cells) > 40:
            return False
        if not all(self.room.contains(*c) and c not in self.occupied and not self.room.is_diag(*c) for c in cells):
            return False
        for c in cells:   # the floor stays walkable underneath
            self._set(c, [(self.f0, self.f0 + h - 16), (self.f0 + h, self.top)])
        self.occupied.update(cells)
        self.regions.append({"name": f"catwalk {len(self.regions)}", "kind": "catwalk", "height": h,
                             "cells": [list(c) for c in cells]})
        return True

    def structure(self, w: int, h: int, height: int, name: str) -> bool:
        r = self.room
        for _ in range(30):
            x0 = self.rng.randint(r.x + 3, max(r.x + 3, r.x + r.w - 3 - w))
            y0 = self.rng.randint(r.y + 3, max(r.y + 3, r.y + r.h - 3 - h))
            cells = [(x, y) for x in range(x0, x0 + w) for y in range(y0, y0 + h)]
            ring = [(x, y) for x in range(x0 - 2, x0 + w + 2) for y in range(y0 - 2, y0 + h + 2)]
            if not self._ok(ring):
                continue
            snap = self._snapshot(cells)
            for c in cells:
                self._set(c, [(self.f0 + height, self.top)] if height < self.top - self.f0 - 64 else [])
            if not self._floor_connected():
                self._restore(snap)
                continue
            self.occupied.update(ring)
            self.regions.append({"name": name, "kind": "structure", "height": height,
                                 "cells": [list(c) for c in cells]})
            return True
        return False

    def tiers(self, levels: int) -> int:
        """Terraces rising towards one wall (an amphitheatre / great hall): the band against the
        wall is the highest. Built front to back; each band's two stairs stand on the band in
        front of it (or on the floor), so every tier is reachable."""
        r = self.room
        made = 0
        prev: Set[Cell] = set()                  # the band in front: this band's stairs stand on it
        side = self.rng.choice("nsew")
        depth = self.rng.choice([3, 4])
        for t in range(levels):                  # t = 0: frontmost and lowest
            k = levels - 1 - t                   # bands from the wall: k = 0 against it
            height = 64 * (t + 1)
            if side in "ns":
                y0 = r.y + 1 + k * depth if side == "s" else r.y + r.h - 1 - (k + 1) * depth
                rect = (r.x + 1, y0, r.x + r.w - 1, y0 + depth)
            else:
                x0 = r.x + 1 + k * depth if side == "w" else r.x + r.w - 1 - (k + 1) * depth
                rect = (x0, r.y + 1, x0 + depth, r.y + r.h - 1)
            x0, y0, x1, y1 = rect
            cells = [(x, y) for x in range(x0, x1) for y in range(y0, y1)
                     if r.contains(x, y) and not r.is_diag(x, y) and (x, y) not in self.ctx.clear]
            if len(cells) < 12:
                break
            # the stairs come up from the front (towards the room's middle)
            stair_cells = []
            ok = True
            for frac in (0.25, 0.75):
                for j in range(4):               # 3 steps of 16 and the landing below them
                    for w in (0, 1):
                        if side in "ns":
                            x = x0 + int((x1 - x0) * frac) + w
                            y = (y1 + j) if side == "s" else (y0 - 1 - j)
                        else:
                            y = y0 + int((y1 - y0) * frac) + w
                            x = (x1 + j) if side == "w" else (x0 - 1 - j)
                        c = (x, y)
                        if not r.contains(*c) or c in self.ctx.clear or (c in self.occupied and c not in prev):
                            ok = False
                        stair_cells.append((c, self.f0 + height - 16 * (j + 1)))
            if not ok:
                break
            snap = self._snapshot(cells + [c for c, _ in stair_cells])
            for c in cells:
                self._set(c, [(self.f0 + height, self.top)])
            for c, f in stair_cells:
                if f > self.f0 + height - 64:    # the landing keeps the height of the band in front
                    self._set(c, [(f, self.top)])
            if not self._floor_connected():
                self._restore(snap)
                break
            self.occupied.update(cells)
            self.occupied.update(c for c, _ in stair_cells)
            prev = set(cells)
            self.tops.extend(cells)
            self.regions.append({"name": f"tier {t + 1}", "kind": "tier", "height": height,
                                 "rect": list(rect), "cells": [list(c) for c in cells]})
            made += 1
        return made


class MegaRoomSpace(RoomSpace):
    name = "mega"
    kinds = ("megaroom",)
    ENEMY_AREA = dict(RoomSpace.ENEMY_AREA, megaroom=55)
    PICKUPS = dict(RoomSpace.PICKUPS, megaroom=4)

    def propose(self, node, floor: int, cfg, rng: random.Random, multi: bool) -> Optional[Room]:
        from ..layout import _room_size
        w, h, height = _room_size(cfg, "mega", rng)
        w, h = min(w, 40), min(h, 40)
        height = max(384, min(576, height))
        room = Room(-1, node.id, node.kind, 0, 0, w, h, floor, floor + height, storey=node.storey)
        room.meta["style"] = getattr(node, "style", None) or rng.choice(STYLES)
        return room

    def assign_shape(self, room: Room, node, cfg, rng: random.Random, merged: bool):
        room.shape = "rect"          # the interior carries the complexity, the outline stays legible

    def decorate(self, level: Level, room: Room, ctx, rng: random.Random):
        style = room.meta.get("style", "works")
        b = _Builder(ctx, rng)
        b.regions.append({"name": "floor", "kind": "floor", "height": 0})
        area = room.w * room.h
        if style == "hangar":
            for k in range(rng.randint(1, 2)):
                b.structure(rng.randint(4, 8), rng.randint(4, 8), rng.choice([128, 192, 256, 9999]),
                            f"structure {k + 1}")
            if rng.random() < 0.7:
                b.platform(rng.randint(6, 10), rng.randint(3, 4), 64, False, "loading dock", against_wall=True)
        elif style == "terraces":
            b.tiers(rng.randint(2, 3))
            if rng.random() < 0.5:
                b.structure(rng.randint(3, 4), rng.randint(3, 4), 9999, "monolith")
        else:  # works
            decks = []
            deck_height = rng.choice([160, 176, 192])        # one deck level per hall: catwalks line up
            for k in range(max(3, area // 160)):
                deck = rng.random() < 0.7
                height = deck_height if deck else rng.choice([64, 96, 128])
                near = rng.choice(decks)["rect"] if deck and decks and rng.random() < 0.75 else None
                rect = b.platform(rng.randint(4, 7), rng.randint(4, 7), height, deck,
                                  f"{'deck' if deck else 'platform'} {k + 1}", near=near)
                if rect and deck:
                    decks.append(b.regions[-1])
            for i in range(len(decks)):
                for j in range(i + 1, len(decks)):
                    b.catwalk(decks[i], decks[j])
        ctx.used.update(b.occupied)
        ctx.top_cells = getattr(ctx, "top_cells", []) + b.tops
        room.meta["regions"] = [{k: v for k, v in rg.items() if k != "cells"} | {"size": len(rg.get("cells", []))}
                                for rg in b.regions]
        room.meta["region_cells"] = {rg["name"]: rg["cells"] for rg in b.regions if rg.get("cells")}
        room.features.append(style)
        # clutter and cover on the floor, as in any room
        from ..decorate import f_crates
        if rng.random() < min(1.0, (0.3 if style == "hangar" else 0.8) * level.config.crate_scale):
            f_crates(ctx, rng)

    def regions(self, room: Room) -> List[int]:
        return [room.id]


register(MegaRoomSpace())
