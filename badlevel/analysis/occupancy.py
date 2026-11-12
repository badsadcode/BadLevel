"""3D occupancy grid + vectorised line-of-sight.

The level's column spans are voxelised (XY = grid cells, Z = ``z_step`` units).
45-degree half cells are tested exactly: a point inside a diagonal cell is open
only if it lies in the open triangle. Closed secret doors block sight; locked
doors are treated as open (they open once the player has the key).

Rays are marched in fixed steps (default 8 units, a quarter of a cell, while
walls are at least a full cell thick), all rays in parallel with numpy, and
blocked rays are dropped as soon as they hit something, so the cost is roughly
"total distance travelled before hitting a wall".
"""
from __future__ import annotations

import numpy as np

from ..level import Level

_CORNER_CODE = {"sw": 1, "se": 2, "nw": 3, "ne": 4}


class Occupancy:
    def __init__(self, level: Level, z_step: int = 8, glass_opaque: bool = True):
        self.level = level
        self.cell = level.cell
        self.z_step = z_step
        self.zmin, self.zmax = level.z_range()
        nz = int(np.ceil((self.zmax - self.zmin) / z_step))
        self.open = np.zeros((level.width, level.height, nz), dtype=bool)
        for (x, y), spans in level.spans.items():
            for (f, c) in spans:
                k0 = int(np.ceil((f - self.zmin) / z_step - 0.5))
                k1 = int(np.floor((c - self.zmin) / z_step - 0.5))
                self.open[x, y, max(k0, 0):k1 + 1] = True
        self.diag = np.zeros((level.width, level.height), dtype=np.int8)
        for (x, y), corner in level.diag.items():
            self.diag[x, y] = _CORNER_CODE[corner]
        if glass_opaque:  # combat: glass stops bullets; visual connectivity: it doesn't
            for (x, y) in level.glass:
                self.open[x, y, :] = False
        for e in level.entities:
            if e.kind == "secret_door" and e.bounds:
                (x0, y0, z0), (x1, y1, z1) = e.bounds
                s = self.cell
                self.open[int(x0 // s):int(x1 // s), int(y0 // s):int(y1 // s),
                          int((z0 - self.zmin) // z_step):int(np.ceil((z1 - self.zmin) / z_step))] = False

    def is_open(self, p: np.ndarray) -> np.ndarray:
        """p: (N, 3) world points -> (N,) bool."""
        fx = p[:, 0] / self.cell
        fy = p[:, 1] / self.cell
        ix = np.floor(fx).astype(np.int64)
        iy = np.floor(fy).astype(np.int64)
        iz = np.floor((p[:, 2] - self.zmin) / self.z_step).astype(np.int64)
        W, H, Z = self.open.shape
        inside = (ix >= 0) & (iy >= 0) & (iz >= 0) & (ix < W) & (iy < H) & (iz < Z)
        out = np.zeros(len(p), dtype=bool)
        idx = np.nonzero(inside)[0]
        ix, iy, iz = ix[idx], iy[idx], iz[idx]
        ok = self.open[ix, iy, iz]
        code = self.diag[ix, iy]
        if code.any():
            u = fx[idx] - ix
            v = fy[idx] - iy
            solid = ((code == 1) & (u + v < 1)) | ((code == 2) & (v < u)) | \
                    ((code == 3) & (u < v)) | ((code == 4) & (u + v > 1))
            ok &= ~solid
        out[idx] = ok
        return out

    def visible(self, a: np.ndarray, b: np.ndarray, step: float = 8.0, chunk: int = 400_000) -> np.ndarray:
        """Line of sight for each pair of points a[i] -> b[i] (both (N, 3)); returns (N,) bool."""
        out = np.empty(len(a), dtype=bool)
        for s in range(0, len(a), chunk):
            out[s:s + chunk] = self._visible(a[s:s + chunk], b[s:s + chunk], step)
        return out

    def _visible(self, a, b, step):
        n = len(a)
        d = b - a
        length = np.sqrt((d ** 2).sum(axis=1))
        nsteps = np.maximum(np.ceil(length / step).astype(np.int64), 1)
        result = np.ones(n, dtype=bool)
        active = np.arange(n)
        k = 1
        while active.size:
            # rays that reached their target without hitting anything stay visible
            active = active[nsteps[active] > k]
            if not active.size:
                break
            t = (k / nsteps[active])[:, None]
            pts = a[active] + d[active] * t
            hit = ~self.is_open(pts)
            result[active[hit]] = False
            active = active[~hit]
            k += 1
        return result
