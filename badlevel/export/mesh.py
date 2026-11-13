"""Level -> render mesh (only faces that border open space, greedily merged).

Faces are produced in Z-up map space with world-aligned UVs (1 UV unit per
64 map units), grouped by material role.
"""
from __future__ import annotations

from typing import Dict, List, Tuple

from ..brushes import greedy_rects
from ..level import Level
from ..shapes import SOLID_SIDES, corner_vertices

Vec3 = Tuple[float, float, float]
UV_SCALE = 64.0


class MeshBuilder:
    """Triangles grouped by part (a room, a corridor, an element like "room:5/vault") and material."""

    def __init__(self):
        self.parts: Dict[str, Dict[str, dict]] = {}
        self.part = "level"                      # the part new faces go to

    @property
    def groups(self) -> Dict[str, dict]:
        """All parts merged by material (for single-part builders: entity boxes)."""
        if len(self.parts) == 1:
            return next(iter(self.parts.values()))
        out: Dict[str, dict] = {}
        for groups in self.parts.values():
            for mat, g in groups.items():
                o = out.setdefault(mat, {"pos": [], "nrm": [], "uv": [], "idx": []})
                base = len(o["pos"])
                o["pos"] += g["pos"]
                o["nrm"] += g["nrm"]
                o["uv"] += g["uv"]
                o["idx"] += [i + base for i in g["idx"]]
        return out

    def _g(self, mat):
        return self.parts.setdefault(self.part, {}).setdefault(mat, {"pos": [], "nrm": [], "uv": [], "idx": []})

    def quad(self, mat: str, corners: List[Vec3], normal: Vec3, uvs):
        g = self._g(mat)
        # make winding counter-clockwise when seen from the normal side
        for (a, b, c) in ((corners[0], corners[1], corners[2]), (corners[0], corners[2], corners[3])):
            ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
            vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
            cx, cy, cz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
            if abs(cx) + abs(cy) + abs(cz) > 1e-9:    # (a degenerate trapezoid end: try the other half)
                break
        if cx * normal[0] + cy * normal[1] + cz * normal[2] < 0:
            corners = corners[::-1]
            uvs = uvs[::-1]
        base = len(g["pos"])
        g["pos"].extend(corners)
        g["nrm"].extend([normal] * 4)
        g["uv"].extend(uvs)
        g["idx"].extend([base, base + 1, base + 2, base, base + 2, base + 3])

    def tri(self, mat: str, corners: List[Vec3], normal: Vec3, uvs):
        g = self._g(mat)
        a, b, c = corners
        ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
        vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
        cx, cy, cz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
        if cx * normal[0] + cy * normal[1] + cz * normal[2] < 0:
            corners, uvs = corners[::-1], uvs[::-1]
        base = len(g["pos"])
        g["pos"].extend(corners)
        g["nrm"].extend([normal] * 3)
        g["uv"].extend(uvs)
        g["idx"].extend([base, base + 1, base + 2])

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


def area_of(level: Level, c, sp) -> str:
    """The room / corridor a span belongs to ("room:5", "corridor:3"), for naming parts."""
    o = level.owner_of(c, sp) or ""
    if o.startswith("roof:"):
        return f"room:{o[5:]}/roofscape"
    if o.startswith("window:"):
        return "windows"
    return o or "level"


def part_of(level: Level, c, sp, face: str, n=None) -> str:
    """Which named part a face belongs to: an element the face looks into (a pier, a building
    wall), an element ceiling (a vault), an element space (a niche), or else its room / corridor."""
    if face == "wall" and n is not None and n in level.part_solid:
        return level.part_solid[n]
    if face == "ceiling" and c in level.part_ceiling and level.spans[c][-1] == sp:
        return level.part_ceiling[c]
    return level.part_cells.get(c) or area_of(level, c, sp)


def build_mesh(level: Level) -> MeshBuilder:
    s = level.cell
    mb = MeshBuilder()
    flats: Dict[Tuple[str, str, int], set] = {}
    walls: Dict[Tuple[str, int, int, int, int, int], List[int]] = {}
    diag = level.diag
    u = UV_SCALE
    slopes = level.ceil_slope
    fslopes = level.floor_slope
    for (x, y), spans in level.spans.items():
        corner = diag.get((x, y))
        if corner:
            mb.part = part_of(level, (x, y), spans[0], "floor")
            _diag_faces(mb, level, x, y, corner, spans[0])
        if (x, y) in slopes:
            mb.part = part_of(level, (x, y), spans[-1], "ceiling")
            _sloped_ceiling(mb, level, x, y)
        if (x, y) in fslopes and not corner:
            mb.part = part_of(level, (x, y), spans[0], "floor")
            _sloped_floor(mb, level, x, y)
        for (f, c) in spans:
            if not corner:
                if not ((x, y) in fslopes and (f, c) == spans[0]):
                    flats.setdefault((part_of(level, (x, y), (f, c), "floor"), "floor", f), set()).add((x, y))
                top = (x, y) in level.sky and (f, c) == spans[-1]
                if not ((x, y) in slopes and (f, c) == spans[-1]):
                    flats.setdefault((part_of(level, (x, y), (f, c), "ceiling"), "sky" if top else "ceiling", c),
                                     set()).add((x, y))
            for d, (dx, dy) in enumerate(((1, 0), (-1, 0), (0, 1), (0, -1))):
                if corner and (dx, dy) in SOLID_SIDES[corner]:
                    continue  # covered by this cell's own diagonal wall
                n = (x + dx, y + dy)
                nspans = level.spans.get(n, [])
                if n in diag and (-dx, -dy) in SOLID_SIDES[diag[n]]:
                    nspans = []  # neighbour's solid half faces us
                if (x, y) in slopes or n in slopes or (x, y) in fslopes or n in fslopes:
                    mb.part = part_of(level, (x, y), (f, c), "wall", n)
                    _sloped_edge(mb, level, (x, y), (f, c), spans, d, (dx, dy), nspans)
                    continue
                for (a, b) in _subtract((f, c), nspans):
                    # line coordinate: x boundary for X dirs, y boundary for Y dirs
                    if dx:
                        line, along = x + (1 if dx > 0 else 0), y
                    else:
                        line, along = y + (1 if dy > 0 else 0), x
                    # faces where the ceiling steps (above a lower neighbour ceiling) are ceiling parts
                    is_ceil = b == c and any(nc <= a and nf < a for (nf, nc) in nspans)
                    part = part_of(level, (x, y), (f, c), "ceiling" if is_ceil else "wall", n)
                    walls.setdefault((part, d, line, a, b, int(is_ceil)), []).append(along)

    for (part, mat, z), cells in flats.items():
        mb.part = part
        n = (0, 0, 1) if mat == "floor" else (0, 0, -1)
        for (x0, y0, x1, y1) in greedy_rects(cells):
            X0, Y0, X1, Y1 = x0 * s, y0 * s, x1 * s, y1 * s
            mb.quad(mat, [(X0, Y0, z), (X1, Y0, z), (X1, Y1, z), (X0, Y1, z)], n,
                    [(X0 / u, Y0 / u), (X1 / u, Y0 / u), (X1 / u, Y1 / u), (X0 / u, Y1 / u)])

    for (part, d, line, a, b, is_ceil), alongs in walls.items():
        mb.part = part
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
            mb.quad("ceiling" if is_ceil else "wall", corners, normal, uvs)
    return mb


# ----------------------------------------------------------------------
# sloped ceilings (smooth vaults): the ceiling quad, and side faces whose bounds are lines
# ----------------------------------------------------------------------
def _sloped_ceiling(mb: MeshBuilder, level: Level, x: int, y: int):
    s, u = level.cell, UV_SCALE
    z00, z10, z11, z01 = level.ceil_slope[(x, y)]
    X0, Y0, X1, Y1 = x * s, y * s, (x + 1) * s, (y + 1) * s
    pts = [(X0, Y0, z00), (X1, Y0, z10), (X1, Y1, z11), (X0, Y1, z01)]
    # downward normal of the plane
    ax, ay, az = X1 - X0, 0.0, z10 - z00
    bx, by, bz = 0.0, Y1 - Y0, z01 - z00
    nx, ny, nz = ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx
    if nz > 0:
        nx, ny, nz = -nx, -ny, -nz
    ln = (nx * nx + ny * ny + nz * nz) ** 0.5
    mb.quad("ceiling", pts, (nx / ln, ny / ln, nz / ln), [(p[0] / u, p[1] / u) for p in pts])


def _sloped_floor(mb: MeshBuilder, level: Level, x: int, y: int):
    s, u = level.cell, UV_SCALE
    z00, z10, z11, z01 = level.floor_slope[(x, y)]
    X0, Y0, X1, Y1 = x * s, y * s, (x + 1) * s, (y + 1) * s
    pts = [(X0, Y0, z00), (X1, Y0, z10), (X1, Y1, z11), (X0, Y1, z01)]
    ax, az = X1 - X0, z10 - z00
    by, bz = Y1 - Y0, z01 - z00
    nx, ny, nz = -az * by, -ax * bz, ax * by          # (a x b) for a = (ax, 0, az), b = (0, by, bz)
    if nz < 0:
        nx, ny, nz = -nx, -ny, -nz
    ln = (nx * nx + ny * ny + nz * nz) ** 0.5
    mb.quad("floor", pts, (nx / ln, ny / ln, nz / ln), [(p[0] / u, p[1] / u) for p in pts])


def _floor_line(level: Level, c, span, spans, d: int):
    """The bottom of ``span`` along the cell edge in direction d (a sloped floor, or flat)."""
    sl = level.floor_slope.get(c)
    if sl is None or span != spans[0] or c in level.diag:
        return (span[0], span[0])
    z00, z10, z11, z01 = sl
    return [(z10, z11), (z00, z01), (z01, z11), (z00, z10)][d]


def _edge_line(level: Level, c, span, spans, d: int):
    """The top of ``span`` along the cell edge in direction d, as (z at along=0, z at along=1)."""
    sl = level.ceil_slope.get(c)
    if sl is None or span != spans[-1]:
        return (span[1], span[1])
    z00, z10, z11, z01 = sl
    return [(z10, z11), (z00, z01), (z01, z11), (z00, z10)][d]


def _sloped_edge(mb: MeshBuilder, level: Level, c, span, spans, d: int, dxy, nspans):
    """Side faces of one span of cell c towards neighbour n, where either has a sloped ceiling.
    Every bound is linear along the edge; the edge is split where bounds cross, and in each
    piece the open part of c minus the open parts of n becomes quads."""
    s, uvs = level.cell, UV_SCALE
    x, y = c
    dx, dy = dxy
    n = (x + dx, y + dy)
    nd = {0: 1, 1: 0, 2: 3, 3: 2}[d]
    lo_c = _floor_line(level, c, span, spans, d)
    hi_c = _edge_line(level, c, span, spans, d)
    others = [(_floor_line(level, n, (nf, nc), nspans, nd), _edge_line(level, n, (nf, nc), nspans, nd))
              for (nf, nc) in nspans]
    lines = [lo_c, hi_c] + [l for o in others for l in o]

    def at(l, t):
        return l[0] + (l[1] - l[0]) * t
    ts = {0.0, 1.0}
    for i in range(len(lines)):
        for j in range(i + 1, len(lines)):
            a, b = lines[i], lines[j]
            den = (a[0] - b[0]) - (a[1] - b[1])
            if den:
                tt = (a[0] - b[0]) / den
                if 0.0 < tt < 1.0:
                    ts.add(tt)
    ts = sorted(ts)
    if dx:
        line = x + (1 if dx > 0 else 0)
        pos = lambda t: (line * s, (y + t) * s)    # noqa: E731
    else:
        line = y + (1 if dy > 0 else 0)
        pos = lambda t: ((x + t) * s, line * s)    # noqa: E731
    normal = [(-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0)][d]
    for t0, t1 in zip(ts, ts[1:]):
        tm = (t0 + t1) / 2
        # pieces of [lo_c, hi_c] not covered by any neighbour interval, with the lines bounding them
        pieces = [(lo_c, hi_c)]
        for (nlo, nhi) in sorted(others, key=lambda o: at(o[0], tm)):
            out = []
            for (plo, phi) in pieces:
                a, b, na, nb = at(plo, tm), at(phi, tm), at(nlo, tm), at(nhi, tm)
                if nb <= a or na >= b:
                    out.append((plo, phi))
                    continue
                if na > a:
                    out.append((plo, nlo))
                if nb < b:
                    out.append((nhi, phi))
            pieces = out
        for (plo, phi) in pieces:
            z = [at(plo, t0), at(plo, t1), at(phi, t1), at(phi, t0)]
            if z[3] - z[0] < 0.01 and z[2] - z[1] < 0.01:
                continue
            ceil = phi is hi_c and any(at(nhi, tm) <= at(plo, tm) + 0.01 and at(nlo, tm) < at(plo, tm)
                                       for (nlo, nhi) in others)
            p0, p1 = pos(t0), pos(t1)
            corners = [(p0[0], p0[1], z[0]), (p1[0], p1[1], z[1]), (p1[0], p1[1], z[2]), (p0[0], p0[1], z[3])]
            a0, a1 = (p0[1], p1[1]) if dx else (p0[0], p1[0])
            mb.quad("ceiling" if ceil else "wall", corners, normal,
                    [(a0 / uvs, z[0] / uvs), (a1 / uvs, z[1] / uvs), (a1 / uvs, z[2] / uvs), (a0 / uvs, z[3] / uvs)])


def _diag_faces(mb: MeshBuilder, level: Level, x: int, y: int, corner: str, span):
    s, u = level.cell, UV_SCALE
    f, c = span
    _, open_tri = corner_vertices(corner)
    pts = [((x + a) * s, (y + b) * s) for (a, b) in open_tri]
    for z, n, mat in ((f, (0, 0, 1), "floor"), (c, (0, 0, -1), "ceiling")):
        mb.tri(mat, [(px, py, z) for px, py in pts], n, [(px / u, py / u) for px, py in pts])
    # diagonal wall along the hypotenuse, facing the open corner
    (ox, oy), h0, h1 = pts
    mx, my = (h0[0] + h1[0]) / 2, (h0[1] + h1[1]) / 2
    nx, ny = ox - mx, oy - my
    ln = (nx * nx + ny * ny) ** 0.5
    normal = (nx / ln, ny / ln, 0.0)
    w = ((h1[0] - h0[0]) ** 2 + (h1[1] - h0[1]) ** 2) ** 0.5
    base = (h0[0] + h0[1]) / u
    mb.quad("wall", [(h0[0], h0[1], f), (h1[0], h1[1], f), (h1[0], h1[1], c), (h0[0], h0[1], c)], normal,
            [(base, f / u), (base + w / u, f / u), (base + w / u, c / u), (base, c / u)])
