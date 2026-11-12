"""Level -> render mesh (only faces that border open space, greedily merged).

Faces are produced in Z-up map space with world-aligned UVs (1 UV unit per
64 map units), grouped by material role.
"""
from __future__ import annotations

from typing import Dict, List, Tuple

from ..brushes import greedy_rects
from ..level import Level

Vec3 = Tuple[float, float, float]
UV_SCALE = 64.0


class MeshBuilder:
    def __init__(self):
        self.groups: Dict[str, dict] = {}

    def _g(self, mat):
        return self.groups.setdefault(mat, {"pos": [], "nrm": [], "uv": [], "idx": []})

    def quad(self, mat: str, corners: List[Vec3], normal: Vec3, uvs):
        g = self._g(mat)
        # make winding counter-clockwise when seen from the normal side
        a, b, c = corners[0], corners[1], corners[2]
        ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
        vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
        cx, cy, cz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
        if cx * normal[0] + cy * normal[1] + cz * normal[2] < 0:
            corners = corners[::-1]
            uvs = uvs[::-1]
        base = len(g["pos"])
        g["pos"].extend(corners)
        g["nrm"].extend([normal] * 4)
        g["uv"].extend(uvs)
        g["idx"].extend([base, base + 1, base + 2, base, base + 2, base + 3])

    def box(self, mat: str, lo: Vec3, hi: Vec3):
        x0, y0, z0 = lo
        x1, y1, z1 = hi
        s = UV_SCALE
        self.quad(mat, [(x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)], (0, 0, 1),
                  [(x0 / s, y0 / s), (x1 / s, y0 / s), (x1 / s, y1 / s), (x0 / s, y1 / s)])
        self.quad(mat, [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0)], (0, 0, -1),
                  [(x0 / s, y0 / s), (x1 / s, y0 / s), (x1 / s, y1 / s), (x0 / s, y1 / s)])
        for x, n in ((x0, -1), (x1, 1)):
            self.quad(mat, [(x, y0, z0), (x, y1, z0), (x, y1, z1), (x, y0, z1)], (n, 0, 0),
                      [(y0 / s, z0 / s), (y1 / s, z0 / s), (y1 / s, z1 / s), (y0 / s, z1 / s)])
        for y, n in ((y0, -1), (y1, 1)):
            self.quad(mat, [(x0, y, z0), (x1, y, z0), (x1, y, z1), (x0, y, z1)], (0, n, 0),
                      [(x0 / s, z0 / s), (x1 / s, z0 / s), (x1 / s, z1 / s), (x0 / s, z1 / s)])


def _subtract(interval, others):
    """[a,b) minus union of others -> list of intervals."""
    a, b = interval
    out = []
    z = a
    for (f, c) in sorted(others):
        if c <= z or f >= b:
            continue
        if f > z:
            out.append((z, min(f, b)))
        z = max(z, c)
        if z >= b:
            break
    if z < b:
        out.append((z, b))
    return out


def build_mesh(level: Level) -> MeshBuilder:
    s = level.cell
    mb = MeshBuilder()
    flats: Dict[Tuple[str, int], set] = {}
    walls: Dict[Tuple[int, int, int, int, int], List[int]] = {}
    for (x, y), spans in level.spans.items():
        for (f, c) in spans:
            flats.setdefault(("floor", f), set()).add((x, y))
            flats.setdefault(("ceiling", c), set()).add((x, y))
            for d, (dx, dy) in enumerate(((1, 0), (-1, 0), (0, 1), (0, -1))):
                nspans = level.spans.get((x + dx, y + dy), [])
                for (a, b) in _subtract((f, c), nspans):
                    # line coordinate: x boundary for X dirs, y boundary for Y dirs
                    if dx:
                        line, along = x + (1 if dx > 0 else 0), y
                    else:
                        line, along = y + (1 if dy > 0 else 0), x
                    walls.setdefault((d, line, a, b, 0), []).append(along)

    u = UV_SCALE
    for (mat, z), cells in flats.items():
        n = (0, 0, 1) if mat == "floor" else (0, 0, -1)
        for (x0, y0, x1, y1) in greedy_rects(cells):
            X0, Y0, X1, Y1 = x0 * s, y0 * s, x1 * s, y1 * s
            mb.quad(mat, [(X0, Y0, z), (X1, Y0, z), (X1, Y1, z), (X0, Y1, z)], n,
                    [(X0 / u, Y0 / u), (X1 / u, Y0 / u), (X1 / u, Y1 / u), (X0 / u, Y1 / u)])

    for (d, line, a, b, _), alongs in walls.items():
        alongs.sort()
        runs = []
        start = prev = alongs[0]
        for v in alongs[1:]:
            if v == prev + 1:
                prev = v
                continue
            runs.append((start, prev + 1))
            start = prev = v
        runs.append((start, prev + 1))
        normal = [(-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0)][d]
        for (p0, p1) in runs:
            L, P0, P1 = line * s, p0 * s, p1 * s
            if d < 2:
                corners = [(L, P0, a), (L, P1, a), (L, P1, b), (L, P0, b)]
            else:
                corners = [(P0, L, a), (P1, L, a), (P1, L, b), (P0, L, b)]
            uvs = [(P0 / u, a / u), (P1 / u, a / u), (P1 / u, b / u), (P0 / u, b / u)]
            mb.quad("wall", corners, normal, uvs)
    return mb
