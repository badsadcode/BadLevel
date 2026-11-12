"""Column spans -> convex axis-aligned brushes (boxes).

Every open cell and every cell touching one gets a solid column, minus its
empty spans. Identical solid intervals on neighbouring cells are merged
into larger rectangles (greedy meshing), which keeps the brush count low
and the result pleasant to edit in a level editor.

Because every empty span is boxed in by its own column (below/above) and by
the neighbouring columns (sides), the output is sealed by construction:
no leaks for qbsp-style compilers.
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Dict, List, Optional, Set, Tuple

from .level import Cell, Level

# per-face roles; exporters map these to textures / materials
FLOOR, WALL, CEIL, SKY = "floor", "wall", "ceiling", "sky"


@dataclass
class Brush:
    mins: Tuple[int, int, int]
    maxs: Tuple[int, int, int]
    top: str = WALL      # texture role of the +Z face
    bottom: str = WALL   # texture role of the -Z face
    side: str = WALL
    # set for triangular prisms (45-degree walls): corner the solid triangle points at;
    # mins/maxs are then the cell's bounding box
    corner: Optional[str] = None


def solid_intervals(spans, zmin, zmax):
    """Complement of empty spans within [zmin, zmax], tagged with face roles."""
    out = []
    z = zmin
    for i, (f, c) in enumerate(spans):
        if f > z:
            out.append((z, f, FLOOR, CEIL if i else WALL))
        z = c
    if z < zmax:
        out.append((z, zmax, WALL, CEIL))
    return out


def build_brushes(level: Level) -> List[Brush]:
    zmin, zmax = level.z_range()
    cols: Set[Cell] = set()
    for (x, y) in level.spans:
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                cols.add((x + dx, y + dy))

    groups: Dict[Tuple[int, int, str, str], Set[Cell]] = {}
    for c in cols:
        spans = level.spans.get(c, [])
        sky = c in level.sky and spans
        for (z0, z1, top, bottom) in solid_intervals(spans, zmin, zmax):
            if sky and z0 == spans[-1][1]:
                bottom = SKY                  # the solid above an open area is sky
            groups.setdefault((z0, z1, top, bottom), set()).add(c)

    s = level.cell
    brushes: List[Brush] = []
    for (z0, z1, top, bottom), cells in sorted(groups.items()):
        for (x0, y0, x1, y1) in greedy_rects(cells):
            brushes.append(Brush((x0 * s, y0 * s, z0), (x1 * s, y1 * s, z1), top, bottom))
    # half-solid diagonal cells: one triangular prism filling the solid half of the open span
    for (x, y), corner in sorted(level.diag.items()):
        f, c = level.spans[(x, y)][0]
        brushes.append(Brush((x * s, y * s, f), ((x + 1) * s, (y + 1) * s, c), WALL, WALL, WALL, corner))
    return brushes


def greedy_rects(cells: Set[Cell]) -> List[Tuple[int, int, int, int]]:
    """Cover a set of cells with non-overlapping rectangles (x0, y0, x1, y1), max exclusive."""
    left = set(cells)
    rects = []
    for (x, y) in sorted(cells, key=lambda c: (c[1], c[0])):
        if (x, y) not in left:
            continue
        x1 = x + 1
        while (x1, y) in left:
            x1 += 1
        y1 = y + 1
        while all((xx, y1) in left for xx in range(x, x1)):
            y1 += 1
        for yy in range(y, y1):
            for xx in range(x, x1):
                left.discard((xx, yy))
        rects.append((x, y, x1, y1))
    return rects
