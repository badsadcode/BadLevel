"""Room footprints: non-rectangular rooms with real 45-degree walls.

A shape is a set of local cells (the room's open cells inside its bounding
box) plus *diagonal cells*: half-solid cells where a 45-degree wall runs
corner to corner. A diagonal cell is stored with the corner its solid
triangle points at (``"sw"``, ``"se"``, ``"nw"``, ``"ne"``); the opposite
half is open. +Y is north.

Shapes: rect, chamfer, octagon, L, T, U, plus, ring (courtyard around a
solid central block with chamfered corners).
"""
from __future__ import annotations

import random
from typing import Dict, Set, Tuple

Local = Tuple[int, int]

CORNERS = ("sw", "se", "nw", "ne")

# solid sides (cell edges fully covered by the solid triangle) for each corner
SOLID_SIDES = {
    "sw": {(0, -1), (-1, 0)},
    "se": {(0, -1), (1, 0)},
    "nw": {(0, 1), (-1, 0)},
    "ne": {(0, 1), (1, 0)},
}


def corner_vertices(corner: str):
    """Unit-square vertices (solid triangle, open triangle) for a diagonal cell."""
    sw, se, nw, ne = (0, 0), (1, 0), (0, 1), (1, 1)
    tri = {
        "sw": ((sw, se, nw), (ne, nw, se)),
        "se": ((se, ne, sw), (nw, sw, ne)),
        "nw": ((nw, sw, ne), (se, ne, sw)),
        "ne": ((ne, nw, se), (sw, se, nw)),
    }
    return tri[corner]


class Shape:
    def __init__(self, w: int, h: int, name: str = "rect"):
        self.w, self.h, self.name = w, h, name
        self.cells: Set[Local] = {(x, y) for x in range(w) for y in range(h)}
        self.diag: Dict[Local, str] = {}

    def remove_rect(self, x0, y0, x1, y1):
        for x in range(x0, x1):
            for y in range(y0, y1):
                self.cells.discard((x, y))
                self.diag.pop((x, y), None)

    def chamfer(self, corner: str, k: int):
        """Cut a 45-degree corner off the bounding box (k cells long)."""
        if k <= 0:
            return
        for i in range(k):
            for j in range(k - i):
                x = i if corner in ("sw", "nw") else self.w - 1 - i
                y = j if corner in ("sw", "se") else self.h - 1 - j
                if (x, y) not in self.cells:
                    continue
                if i + j <= k - 2:
                    self.cells.discard((x, y))
                    self.diag.pop((x, y), None)
                elif i + j == k - 1:
                    self.diag[(x, y)] = corner

    def ok(self) -> bool:
        """Connected, and every open cell sits in a 2x2 block of full cells (no 1-cell slivers)."""
        full = self.cells - set(self.diag)
        if not full:
            return False
        start = next(iter(full))
        seen, stack = {start}, [start]
        while stack:
            x, y = stack.pop()
            for n in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
                if n in full and n not in seen:
                    seen.add(n)
                    stack.append(n)
        if seen != full:
            return False
        covered = set()
        for (x, y) in full:
            block = [(x + dx, y + dy) for dx in range(2) for dy in range(2)]
            if all(c in full for c in block):
                covered.update(block)
        return full <= covered


def _chamfer_corners(s: Shape, rng: random.Random, kmax: int):
    for corner in CORNERS:
        if rng.random() < 0.75:
            s.chamfer(corner, rng.randint(1, kmax))


def make_shape(w: int, h: int, name: str, rng: random.Random) -> Shape:
    s = Shape(w, h, name)
    m = min(w, h)
    if name == "chamfer":
        _chamfer_corners(s, rng, max(1, min(3, m // 4)))
    elif name == "octagon":
        k = max(2, m // 3)
        for corner in CORNERS:
            s.chamfer(corner, k)
    elif name == "L":
        nw, nh = rng.randint(w // 3, w // 2), rng.randint(h // 3, h // 2)
        corner = rng.choice(CORNERS)
        x0 = 0 if corner in ("sw", "nw") else w - nw
        y0 = 0 if corner in ("sw", "se") else h - nh
        s.remove_rect(x0, y0, x0 + nw, y0 + nh)
        opposite = {"sw": "ne", "ne": "sw", "se": "nw", "nw": "se"}[corner]
        s.chamfer(opposite, rng.randint(1, max(1, m // 4)))
    elif name == "T":
        side = rng.choice("nsew")
        if side in "ns":
            cw, ch = (w - rng.randint(4, max(4, w // 2))) // 2, rng.randint(h // 3, h // 2)
            y0 = 0 if side == "s" else h - ch
            s.remove_rect(0, y0, cw, y0 + ch)
            s.remove_rect(w - cw, y0, w, y0 + ch)
        else:
            ch, cw = (h - rng.randint(4, max(4, h // 2))) // 2, rng.randint(w // 3, w // 2)
            x0 = 0 if side == "w" else w - cw
            s.remove_rect(x0, 0, x0 + cw, ch)
            s.remove_rect(x0, h - ch, x0 + cw, h)
    elif name == "U":
        side = rng.choice("nsew")
        arm = rng.randint(4, max(4, m // 3))
        if side in "ns":
            depth = rng.randint(h // 3, h // 2)
            y0 = 0 if side == "s" else h - depth
            s.remove_rect(arm, y0, w - arm, y0 + depth)
        else:
            depth = rng.randint(w // 3, w // 2)
            x0 = 0 if side == "w" else w - depth
            s.remove_rect(x0, arm, x0 + depth, h - arm)
        _chamfer_corners(s, rng, 2)
    elif name == "plus":
        cw = rng.randint(max(2, w // 4), max(2, (w - 4) // 2 - 1))
        ch = rng.randint(max(2, h // 4), max(2, (h - 4) // 2 - 1))
        for x0, y0 in ((0, 0), (w - cw, 0), (0, h - ch), (w - cw, h - ch)):
            s.remove_rect(x0, y0, x0 + cw, y0 + ch)
    elif name == "ring":
        ring = rng.randint(3, max(3, (m - 3) // 3))
        bx0, by0, bx1, by1 = ring, ring, w - ring, h - ring
        s.remove_rect(bx0, by0, bx1, by1)
        # chamfer the central block: its corner cells become open diagonals
        if bx1 - bx0 >= 4 and by1 - by0 >= 4:
            for (x, y, corner) in ((bx0, by0, "ne"), (bx1 - 1, by0, "nw"),
                                   (bx0, by1 - 1, "se"), (bx1 - 1, by1 - 1, "sw")):
                s.cells.add((x, y))
                s.diag[(x, y)] = corner
        _chamfer_corners(s, rng, max(1, min(3, ring - 1)))
    return s


SHAPES_BY_SIZE = {
    "small": ["rect", "rect", "chamfer", "chamfer", "octagon"],
    "medium": ["rect", "chamfer", "chamfer", "octagon", "L", "L", "T", "U"],
    "large": ["chamfer", "octagon", "octagon", "L", "T", "U", "plus", "ring", "ring"],
}
SHAPES_BY_KIND = {
    "secret": ["rect", "chamfer"],
    "arena": ["octagon", "ring", "plus", "chamfer"],
    "key": ["octagon", "chamfer", "rect"],
}


def pick_shape(w: int, h: int, size: str, kind: str, rng: random.Random) -> Shape:
    options = SHAPES_BY_KIND.get(kind, SHAPES_BY_SIZE.get(size, ["rect"]))
    for _ in range(8):
        s = make_shape(w, h, rng.choice(options), rng)
        if s.ok():
            return s
    return Shape(w, h)
