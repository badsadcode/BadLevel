"""Architecture: hall cross-sections that give a space a recognisable structure.

Until now every room was a box with one ceiling height. A *cross-section* treats
a roofed space as a section profile swept along its long axis: ceiling heights
that vary across (and sometimes along) the room, plus structural members that
stand in it: piers, columns, pilasters. This changes the room's actual
silhouette, so it reads as plain greybox, with no textures needed.

Each style offers a palette of profiles (not one template):

  gothic      basilica (tall pointed nave, low aisles, arcade piers, transverse ribs),
              single-aisle (asymmetric), vault (one pointed vault, no piers: open volume),
              undercroft (a forest of piers under groin vaults), gable (steep timber roof)
  industrial  portal (pilaster frames, deep roof trusses, maybe a raised monitor),
              sawtooth (north-light roof teeth along the hall), multispan (two or three
              gabled spans on column rows), clearspan (deep trusses, no columns)
  bunker      ribbed (heavy downstand ribs, haunched edges), barrel (low segmental vault),
              mushroom (squat piers with drop panels)
  compound    gable, shed (mono-pitch), portal, multispan, clearspan

Who gets what (one level, coherent but not uniform):
  * rooms are ranked by volume: the largest space (and any mega-room) is *grand*, the
    next third *major*, the rest *minor*, and each tier draws from its own palette.
    Small and low rooms get a subtle profile, or none
  * proportions drive the structure: bay spacing grows with the room's height,
    piers thicken in tall halls, ribs and trusses deepen with the span
  * every palette keeps an open option (no piers), and asymmetric variants
  * each use of a profile halves its weight for the rest of the level: one identity,
    not one template

Safety (the validator still has the last word):
  * piers are placed *before* a room's features, marked as used with a free ring
    around them, and undone if they would cut the floor into pieces
  * ceilings are applied *after* the features and only ever lowered: never below
    a walkable surface plus HEAD, never below an opening next to the cell (doors,
    seams of merged halls), never over a gallery or the sky
  * a separate random stream per room: levels without a style are byte-identical
"""
from __future__ import annotations

import math
import random
from typing import Callable, Dict, List, Optional, Tuple

from .level import Cell, Level, Room

STYLES = ("gothic", "industrial", "bunker", "compound", "egyptian", "scifi", "castle")
HEAD = 112          # least headroom under any structure: the lowest corridor the generator makes
                    # (pacing compression), so a jump never meets a beam it wouldn't meet there
SKIP_KINDS = ("courtyard", "secret", "overlook")


def q(z: float) -> int:
    """Snap a height to the 16-unit step grid."""
    return int(round(z / 16.0)) * 16


def _clamp(x, lo, hi):
    return max(lo, min(hi, x))


class Frame:
    """A room in hall coordinates: u along the long axis (0..L), v across it (0..W)."""

    def __init__(self, room: Room, flip_v: bool = False):
        self.room = room
        self.along_x = room.w >= room.h
        self.L, self.W = (room.w, room.h) if self.along_x else (room.h, room.w)
        self.H = room.ceil - room.floor
        self.flip = flip_v

    def cell(self, u: int, v: int) -> Cell:
        if self.flip:
            v = self.W - 1 - v
        r = self.room
        return (r.x + u, r.y + v) if self.along_x else (r.x + v, r.y + u)

    def uv(self, c: Cell) -> Tuple[int, int]:
        r = self.room
        u, v = (c[0] - r.x, c[1] - r.y) if self.along_x else (c[1] - r.y, c[0] - r.x)
        return (u, self.W - 1 - v) if self.flip else (u, v)


class Section:
    """A profile's result: ceiling height above the floor per (u, v), and pier groups."""

    def __init__(self, name: str, frame: Frame):
        self.name, self.frame = name, frame
        self.ceil: Dict[Tuple[int, int], int] = {}
        self.piers: List[List[Tuple[int, int]]] = []
        self.info: Dict[str, object] = {}
        # continuous ceiling surface h(u, v, cell_u, cell_v) for smooth vaults and roofs: evaluated
        # at a cell's corners it gives a sloped plane (None: the profile is stepped by nature)
        self.surf: Optional[Callable[[float, float, int, int], float]] = None
        self.forced: set = set()       # cells that stay stepped (members laid over a smooth surface)

    def set(self, u, v, h):
        self.ceil[(u, v)] = q(h)

    def fill(self, h: Callable[[float, float, int, int], float], smooth: bool = True):
        """Set every cell from a surface function (sampled at the cell centre)."""
        for u in range(self.frame.L):
            for v in range(self.frame.W):
                self.set(u, v, h(u + 0.5, v + 0.5, u, v))
        if smooth:
            self.surf = h


# ----------------------------------------------------------------------
# proportions
# ----------------------------------------------------------------------
def bay_lines(L: int, bay: int, width: int = 1) -> List[int]:
    """Start positions of structural lines along a length, centred, never at the very ends."""
    if L < bay + 2:
        return []
    n = (L - 2 - width) // bay + 1
    span = (n - 1) * bay + width
    start = (L - span) // 2
    return [start + k * bay for k in range(n) if 1 <= start + k * bay <= L - 1 - width]


def bay_size(H: int, L: int, k: float, lo: int, hi: int) -> int:
    """Bays scale with the height (a 30 m hall is not spaced like a 12 m one), and a hall
    gets at least three of them."""
    b = int(round(H / 32 * k))
    b = _clamp(b, lo, hi)
    return max(lo, min(b, max(lo, (L - 2) // 3)))


def pier_size(H: int) -> int:
    return 2 if H >= 384 else 1


def rib_depth(H: int, W: int) -> int:
    return q(_clamp(H * 0.08 + W * 1.5, 32, 96))


def pointed(t: float, e: float = 0.5) -> float:
    """Two-centred (pointed) arch: 1 at the apex (t = 0), 0 at the springing (t = 1)."""
    t = _clamp(t, 0.0, 1.0)
    x = (t + e) / (1 + e)
    return math.sqrt(max(0.0, 1 - x * x)) / math.sqrt(1 - (e / (1 + e)) ** 2)


def round_arch(t: float) -> float:
    return math.sqrt(max(0.0, 1 - _clamp(t, 0.0, 1.0) ** 2))


def _across(v: int, v0: int, v1: int) -> float:
    """0 in the middle of the band [v0, v1), 1 at its edges."""
    return _across_f(v + 0.5, v0, v1)


def _across_f(vf: float, v0: int, v1: int) -> float:
    """The same for a continuous position (cell edges included)."""
    half = (v1 - v0) / 2.0
    return _clamp(abs(vf - (v0 + half)) / half, 0.0, 1.0) if half > 0 else 1.0


def _pier(u0: int, v0: int, p: int, pu: int = None) -> List[Tuple[int, int]]:
    pu = p if pu is None else pu
    return [(u, v) for u in range(u0, u0 + pu) for v in range(v0, v0 + p)]


# ----------------------------------------------------------------------
# profiles: (frame, rng) -> Section or None (if it doesn't fit)
# ----------------------------------------------------------------------
def basilica(fr: Frame, rng: random.Random, aisles: int = 2) -> Optional[Section]:
    L, W, H = fr.L, fr.W, fr.H
    s = Section("basilica" if aisles == 2 else "single-aisle", fr)
    aw = _clamp(int(round(W * rng.uniform(0.2, 0.28))), 3, W // 3)
    nave0, nave1 = (aw, W - aw) if aisles == 2 else (aw, W)
    if nave1 - nave0 < 5 or H < 288:
        return None
    aisle_h = q(max(HEAD + 48, H * rng.uniform(0.38, 0.52)))
    spring = q(aisle_h + (H - aisle_h) * rng.uniform(0.3, 0.5))
    p = pier_size(H)
    bay = bay_size(H, L, rng.uniform(0.38, 0.5), 3 + p, 9)
    lines = bay_lines(L, bay, p)
    rib = rib_depth(H, nave1 - nave0)
    on_line = {u + k for u in lines for k in range(p)}

    def h(uf, vf, u, v):
        if nave0 <= v < nave1:
            z = spring + (H - spring) * pointed(_across_f(vf, nave0, nave1))
            return max(spring, z - rib) if u in on_line else z   # transverse ribs between the bays
        return aisle_h - (32 if u in on_line else 0)             # the aisle vaults spring from the piers
    s.fill(h)
    for u in lines:                                    # the arcade: piers on the aisle side of the nave
        s.piers.append(_pier(u, nave0 - p, p))
        if aisles == 2:
            s.piers.append(_pier(u, nave1, p))
    s.info = {"bay": bay, "aisle_width": aw, "aisle_height": aisle_h, "spring": spring, "pier": p}
    return s


def single_aisle(fr, rng):
    return basilica(fr, rng, aisles=1)


def vault(fr: Frame, rng: random.Random, shape: str = "pointed") -> Optional[Section]:
    """One vault over the whole width. No piers: an uninterrupted volume, with ribs
    (and wall shafts in bigger halls) marking the bays."""
    L, W, H = fr.L, fr.W, fr.H
    if H < HEAD + 48:                                  # smooth, even a low vault reads (6 m rooms)
        return None
    s = Section("vault" if shape == "pointed" else "barrel", fr)
    rise = min(H - (HEAD + 16), q(W * (12 if shape == "pointed" else 7)))
    if rise < 32:
        return None
    spring = H - rise
    arch = pointed if shape == "pointed" else round_arch
    bay = bay_size(H, L, rng.uniform(0.4, 0.6), 3, 10)
    lines = bay_lines(L, bay)
    ribbed = rng.random() < 0.75
    rib = rib_depth(H, W) // 2 if shape == "pointed" else 32
    on_line = set(lines) if ribbed else set()

    def h(uf, vf, u, v):
        z = spring + rise * arch(_across_f(vf, 0, W))
        return max(spring, z - rib) if u in on_line else z
    s.fill(h)
    if shape == "pointed" and W >= 10 and H >= 320 and rng.random() < 0.7:
        for u in lines:                                 # wall shafts carry the ribs down
            s.piers.append([(u, 0)])
            s.piers.append([(u, W - 1)])
    s.info = {"bay": bay if ribbed else None, "spring": spring, "rise": rise}
    return s


def barrel(fr, rng):
    return vault(fr, rng, shape="round")


def undercroft(fr: Frame, rng: random.Random) -> Optional[Section]:
    """A grid of piers under groin vaults: each bay's ceiling peaks in its middle."""
    L, W, H = fr.L, fr.W, fr.H
    if W < 9 or H < HEAD + 64 or H > 352:       # an undercroft is low: in a tall hall it turns into a hypostyle
        return None
    s = Section("undercroft", fr)
    p = 1 if H < 352 else 2
    bay = _clamp(int(round(H / 32 * 0.45)), 4, 6)
    us, vs = bay_lines(L, bay, p), bay_lines(W, bay, p)
    if not us or not vs:
        return None
    low = q(max(HEAD + 32, H * 0.6))

    def dist(x, lines):
        return min(abs((x + 0.5) - (l + p / 2.0)) for l in lines) / (bay / 2.0)
    for u in range(L):
        for v in range(W):
            f = min(1.0, dist(u, us), dist(v, vs))
            s.set(u, v, low + (H - low) * math.sqrt(f))
    for u in us:
        for v in vs:
            s.piers.append(_pier(u, v, p))
    s.info = {"bay": bay, "pier": p, "spring": low}
    return s


def gable(fr: Frame, rng: random.Random, mono: bool = False) -> Optional[Section]:
    """A pitched roof (ridge along the hall), or a mono-pitch shed roof."""
    L, W, H = fr.L, fr.W, fr.H
    if H < HEAD + 48:
        return None
    s = Section("shed" if mono else "gable", fr)
    steep = rng.uniform(10, 18) if not mono else rng.uniform(6, 10)
    rise = min(H - (HEAD + 16), q(W * steep / (1 if mono else 2)))
    if rise < 32:
        return None
    eave = H - rise
    s.fill(lambda uf, vf, u, v: eave + rise * (vf / W if mono else 1 - _across_f(vf, 0, W)))
    s.info = {"eave": eave, "rise": rise}
    return s


def shed(fr, rng):
    return gable(fr, rng, mono=True)


def portal(fr: Frame, rng: random.Random, monitor: Optional[bool] = None, columns: bool = True,
           name: str = "portal") -> Optional[Section]:
    """Portal frames: pilasters on both long walls at every bay, deep trusses across the
    roof between them; maybe a raised monitor strip down the middle for light."""
    L, W, H = fr.L, fr.W, fr.H
    if H < HEAD + 48 or W < 6:
        return None
    s = Section(name, fr)
    truss = q(_clamp(W * 4.5, 48, 128))
    if H - truss < HEAD + 16:
        truss = q(H - HEAD - 16)
    if truss < 32:
        return None
    monitor = (W >= 12 and H >= 320 and rng.random() < 0.5) if monitor is None else monitor
    m0, m1 = (W // 3, W - W // 3) if monitor else (0, 0)
    roof = H - (48 if monitor else 0)
    bay = bay_size(H, L, rng.uniform(0.6, 0.85), 4, 8)
    lines = bay_lines(L, bay)
    for u in range(L):
        for v in range(W):
            h = H if m0 <= v < m1 else roof
            if u in lines:
                h = min(h, roof) - truss
            s.set(u, v, h)
    if columns:
        pw = 2 if H >= 384 and rng.random() < 0.6 else 1
        for u in lines:
            s.piers.append(_pier(u, 0, 1, pw))
            s.piers.append(_pier(u, W - 1, 1, pw))
    s.info = {"bay": bay, "truss": truss, "monitor": bool(monitor)}
    return s


def clearspan(fr, rng):
    return portal(fr, rng, monitor=False, columns=False, name="clearspan")


def sawtooth(fr: Frame, rng: random.Random) -> Optional[Section]:
    """North-light roof: along the hall each bay's ceiling ramps up, then drops."""
    L, W, H = fr.L, fr.W, fr.H
    if H < 256 or L < 12:
        return None
    s = Section("sawtooth", fr)
    tooth = q(_clamp(H * 0.22, 48, 128))
    bay = bay_size(H, L, rng.uniform(0.45, 0.6), 4, 9)
    lines = bay_lines(L, bay)
    if len(lines) < 2:
        return None
    def h(uf, vf, u, v):
        prev = max([l for l in lines if l <= u], default=None)
        nxt = min([l for l in lines if l > u], default=None)
        if prev is None or nxt is None:
            return H - tooth
        return H - tooth + tooth * (uf - prev) / (nxt - prev)   # ramps up across the bay, then drops
    s.fill(h)
    for u in lines:
        s.piers.append([(u, 0)])
        s.piers.append([(u, W - 1)])
    s.info = {"bay": bay, "tooth": tooth}
    return s


def multispan(fr: Frame, rng: random.Random) -> Optional[Section]:
    """Two or three gabled spans side by side, on rows of columns."""
    L, W, H = fr.L, fr.W, fr.H
    k = 3 if W >= 21 and rng.random() < 0.5 else 2
    if W < 12 or H < 256:
        return None
    s = Section("multispan", fr)
    edges = [round(W * i / k) for i in range(k + 1)]
    p = pier_size(H)
    rise = q(_clamp(W / k * rng.uniform(7, 11), 48, 160))
    rise = min(rise, H - HEAD - 32)
    eave = H - rise

    def h(uf, vf, u, v):
        i = max(j for j in range(k) if edges[j] <= v)
        return eave + rise * (1 - _across_f(vf, edges[i], edges[i + 1]))
    s.fill(h)
    bay = bay_size(H, L, rng.uniform(0.6, 0.8), 4 + p, 9)
    for e in edges[1:-1]:
        for u in bay_lines(L, bay, p):
            s.piers.append(_pier(u, e - p // 2, p))
    s.info = {"spans": k, "bay": bay, "pier": p, "eave": eave}
    return s


def ribbed(fr: Frame, rng: random.Random) -> Optional[Section]:
    """Heavy concrete: deep downstand ribs every few cells, haunches along the walls."""
    L, W, H = fr.L, fr.W, fr.H
    if H < HEAD + 32:
        return None
    s = Section("ribbed", fr)
    rib = q(_clamp(H * 0.2, 32, 64))
    if H - rib < HEAD:
        rib = q(H - HEAD)
    if rib < 16:
        return None
    every = rng.choice([2, 3])
    haunch = 32 if H - 32 >= HEAD else 0
    for u in range(L):
        for v in range(W):
            h = H
            if u % every == 0:
                h -= rib
            if v in (0, W - 1):
                h = min(h, H - haunch)
            s.set(u, v, h)
    s.info = {"rib": rib, "every": every}
    return s


def mushroom(fr: Frame, rng: random.Random) -> Optional[Section]:
    """Squat piers in a grid, each spreading into a drop panel under a flat slab."""
    L, W, H = fr.L, fr.W, fr.H
    if W < 8 or H < HEAD + 16:          # the piers carry it even where there's no room for drop panels
        return None
    s = Section("mushroom", fr)
    p = 2 if H >= 320 or W >= 16 else 1
    bay = _clamp(int(round(H / 32 * 0.5)) + 2, 4, 7)
    us, vs = bay_lines(L, bay, p), bay_lines(W, bay, p)
    if not us or not vs:
        return None
    drop = 32 if H - 32 >= HEAD else 16 if H - 16 >= HEAD else 0
    near = set()
    for u0 in us:
        for v0 in vs:
            s.piers.append(_pier(u0, v0, p))
            for u in range(u0 - 1, u0 + p + 1):
                for v in range(v0 - 1, v0 + p + 1):
                    near.add((u, v))
    for u in range(L):
        for v in range(W):
            s.set(u, v, H - drop if (u, v) in near else H)
    s.info = {"bay": bay, "pier": p}
    return s


def haunched(fr: Frame, rng: random.Random) -> Optional[Section]:
    """The least a room can be: ceilings chamfered down along the long walls."""
    L, W, H = fr.L, fr.W, fr.H
    if H < HEAD + 32 or W < 5:
        return None
    s = Section("haunched", fr)

    def h(uf, vf, u, v):           # a chamfer: the edge cell slopes down to the wall
        if v == 0:
            return H - 32 * (1 - vf)
        if v == W - 1:
            return H - 32 * (vf - (W - 1))
        return H
    s.fill(h)
    return s


def tray(fr: Frame, rng: random.Random) -> Optional[Section]:
    """A tray ceiling: a raised middle, coved down to the walls all round. For low rooms."""
    L, W, H = fr.L, fr.W, fr.H
    if H < HEAD + 48 or W < 6 or L < 6:
        return None
    s = Section("tray", fr)
    band = 1 if W < 10 else 2
    drop = min(64, q(H - HEAD - 16))
    low = H - drop

    def h(uf, vf, u, v):
        e = min(uf, L - uf, vf, W - vf)
        return low + drop * min(1.0, e / band)
    s.fill(h)                  # the four corner cells aren't planar: they stay stepped
    s.info = {"band": band, "drop": drop}
    return s


def coffered(fr: Frame, rng: random.Random) -> Optional[Section]:
    """A grid of shallow recesses between beams both ways (a waffle slab, in concrete)."""
    L, W, H = fr.L, fr.W, fr.H
    if H < HEAD + 32 or W < 6:
        return None
    s = Section("coffered", fr)
    k = 4 if W >= 14 else 3
    depth = min(48, q(H - HEAD))
    ou, ov = (L % k) // 2, (W % k) // 2
    for u in range(L):
        for v in range(W):
            beam = (u - ou) % k == 0 or (v - ov) % k == 0 or u in (0, L - 1) or v in (0, W - 1)
            s.set(u, v, H - depth if beam else H)
    s.info = {"grid": k, "depth": depth}
    return s


def cavern(fr: Frame, rng: random.Random) -> Optional[Section]:
    """Rough-hewn rock: an irregular ceiling (a coarse random lattice, interpolated, kept in
    16-unit steps: it should look cut, not poured) and a few rock pillars. Underground only."""
    L, W, H = fr.L, fr.W, fr.H
    if H < HEAD + 64 or W < 8:
        return None
    s = Section("cavern", fr)
    step = rng.choice([3, 4, 5])
    low = max(HEAD + 32, int(H * 0.55))
    lat = {(i, j): rng.uniform(low, H) for i in range(L // step + 2) for j in range(W // step + 2)}
    for u in range(L):
        for v in range(W):
            i, j = u / step, v / step
            i0, j0 = int(i), int(j)
            a, b = i - i0, j - j0
            h = (lat[(i0, j0)] * (1 - a) * (1 - b) + lat[(i0 + 1, j0)] * a * (1 - b)
                 + lat[(i0, j0 + 1)] * (1 - a) * b + lat[(i0 + 1, j0 + 1)] * a * b)
            s.set(u, v, h)
    for _ in range(rng.randint(1, 3) if W >= 12 else 0):     # rock left standing
        p = rng.choice([2, 3])
        u0, v0 = rng.randint(3, max(3, L - 4 - p)), rng.randint(3, max(3, W - 4 - p))
        s.piers.append(_pier(u0, v0, p))
    s.info = {"lattice": step, "low": low}
    return s


# ---------------------------------------------------------------- egyptian
def hypostyle(fr: Frame, rng: random.Random) -> Optional[Section]:
    """A forest of thick columns under a flat stone roof, a taller processional aisle down the
    middle lit by a clerestory (Karnak, in greybox)."""
    L, W, H = fr.L, fr.W, fr.H
    if W < 11 or L < 10 or H < HEAD + 96:
        return None
    s = Section("hypostyle", fr)
    p = 2 if W >= 14 else 1
    band = 4 if W >= 16 else 3                           # the processional aisle
    b0 = (W - band) // 2
    b1 = b0 + band
    side_h = q(max(HEAD + 48, H * rng.uniform(0.6, 0.72)))
    for u in range(L):
        for v in range(W):
            s.set(u, v, H if b0 - p <= v < b1 + p else side_h)
    bay = p + rng.choice([2, 3])                         # dense: the columns are the room
    us = bay_lines(L, bay, p)
    vs = [v for v in range(1, b0 - p, bay)] + [v for v in range(b1 + 1, W - p, bay)]
    vs = [v for v in vs if v + p <= b0 - 1 or v >= b1 + 1]    # the aisle stays clear, columns flank it
    for u in us:
        for v in vs:
            if v + p <= W - 1:
                s.piers.append(_pier(u, v, p))
    s.info = {"bay": bay, "pier": p, "aisle": band, "side_height": side_h}
    return s


def lintel(fr: Frame, rng: random.Random) -> Optional[Section]:
    """A flat stone roof on heavy beams laid across the room, close together."""
    L, W, H = fr.L, fr.W, fr.H
    if H < HEAD + 48:
        return None
    s = Section("lintel", fr)
    depth = min(64, q(H - HEAD - 16))
    every = rng.choice([2, 3])
    for u in range(L):
        for v in range(W):
            s.set(u, v, H - depth if u % every == 1 else H)
    s.info = {"every": every, "depth": depth}
    return s


def corbelled(fr: Frame, rng: random.Random) -> Optional[Section]:
    """A corbelled ceiling: courses of stone stepping inwards to a narrow top. Stepped by
    nature: each course is a step, not a slope."""
    L, W, H = fr.L, fr.W, fr.H
    if H < HEAD + 64 or W < 5:
        return None
    s = Section("corbelled", fr)
    course = rng.choice([32, 48])
    rise = min(q(H - HEAD - 16), course * (W // 2), q(H * 0.5))   # the walls rise first, then the courses
    eave = H - rise
    for u in range(L):
        for v in range(W):
            e = min(v, W - 1 - v)                         # courses counted in from the walls
            s.set(u, v, min(H, eave + course * e))
    s.info = {"course": course, "eave": eave}
    return s


def _edge_slope(fr: Frame, name: str, band: int, drop: int) -> Optional[Section]:
    """Ceilings sloping down to both long walls over ``band`` cells (battered walls, chamfers)."""
    L, W, H = fr.L, fr.W, fr.H
    drop = min(drop, q(H - HEAD - 16))
    if drop < 32 or W < 2 * band + 2:
        return None
    s = Section(name, fr)

    def h(uf, vf, u, v):
        e = min(vf, W - vf)
        return H - drop * max(0.0, 1 - e / band)
    s.fill(h)
    s.info = {"band": band, "drop": drop}
    return s


def battered(fr: Frame, rng: random.Random) -> Optional[Section]:
    """Walls leaning in as they rise (the battered walls of a temple or a pylon)."""
    return _edge_slope(fr, "battered", 3 if fr.W >= 14 else 2, q(fr.H * rng.uniform(0.25, 0.35)))


# ---------------------------------------------------------------- sci-fi
def chamfered(fr: Frame, rng: random.Random) -> Optional[Section]:
    """A station module: the upper corners cut at 45 degrees (a near-octagonal section)."""
    band = 2 if fr.W >= 10 else 1
    return _edge_slope(fr, "chamfered", band, 32 * band)


def ribbed_hull(fr: Frame, rng: random.Random) -> Optional[Section]:
    """A hull: chamfered section with frames (ribs) every few cells along it."""
    s = chamfered(fr, rng)
    if s is None:
        return None
    s.name = "hull"
    L, W, H = fr.L, fr.W, fr.H
    every = rng.choice([3, 4])
    rib = min(32, q(H - HEAD - 16 - s.info["drop"]))
    if rib < 16:
        return s
    for u in range(L):
        if u % every == 0:
            for v in range(W):
                s.ceil[(u, v)] = min(s.ceil[(u, v)], q(H - s.info["drop"] - rib))
                s.forced.add((u, v))
    s.info.update({"frames": every})
    return s


def lightwell(fr: Frame, rng: random.Random) -> Optional[Section]:
    """A flat panelled ceiling with a raised light strip down the middle."""
    L, W, H = fr.L, fr.W, fr.H
    if H < HEAD + 48 or W < 6:
        return None
    s = Section("lightwell", fr)
    band = 2 if W < 12 else 3
    b0 = (W - band) // 2
    for u in range(L):
        for v in range(W):
            s.set(u, v, H if b0 <= v < b0 + band else H - 32)
    s.info = {"strip": band}
    return s


# ---------------------------------------------------------------- castle
def timber(fr: Frame, rng: random.Random) -> Optional[Section]:
    """A flat timber ceiling on joists close together."""
    L, W, H = fr.L, fr.W, fr.H
    if H < HEAD + 32:
        return None
    s = Section("timber", fr)
    for u in range(L):
        for v in range(W):
            s.set(u, v, H - 32 if u % 2 == 0 else H)
    s.info = {"joists": 2}
    return s


def trussed(fr: Frame, rng: random.Random) -> Optional[Section]:
    """A great hall: a steep roof open to the ridge, with tie-beams across at the eaves."""
    s = gable(fr, rng)
    if s is None:
        return None
    s.name = "trussed"
    L, W, H = fr.L, fr.W, fr.H
    eave = s.info["eave"]
    bay = bay_size(H, L, rng.uniform(0.4, 0.55), 3, 7)
    for u in bay_lines(L, bay):
        for v in range(W):
            s.ceil[(u, v)] = eave
            s.forced.add((u, v))
    s.info["bay"] = bay
    return s


PROFILES: Dict[str, Callable] = {
    "basilica": basilica, "single-aisle": single_aisle, "vault": vault, "undercroft": undercroft,
    "gable": gable, "shed": shed, "portal": portal, "clearspan": clearspan, "sawtooth": sawtooth,
    "multispan": multispan, "ribbed": ribbed, "barrel": barrel, "mushroom": mushroom, "haunched": haunched,
    "tray": tray, "coffered": coffered, "cavern": cavern,
    "hypostyle": hypostyle, "lintel": lintel, "corbelled": corbelled, "battered": battered,
    "chamfered": chamfered, "hull": ribbed_hull, "lightwell": lightwell, "timber": timber, "trussed": trussed,
    "plain": lambda fr, rng: None,
}

# style -> tier -> {profile: weight}; every tier keeps an option without piers
PALETTES: Dict[str, Dict[str, Dict[str, float]]] = {
    "gothic": {
        "grand": {"basilica": 4.0, "single-aisle": 1.5, "vault": 1.5, "undercroft": 1.0},
        "major": {"vault": 2.0, "basilica": 1.5, "undercroft": 1.5, "single-aisle": 1.0, "gable": 1.0,
                  "coffered": 0.5},
        "minor": {"vault": 2.0, "gable": 1.0, "tray": 1.0, "coffered": 0.7, "haunched": 0.5, "plain": 0.5},
    },
    "industrial": {
        "grand": {"portal": 2.0, "sawtooth": 1.5, "multispan": 2.0, "clearspan": 1.0},
        "major": {"portal": 2.0, "sawtooth": 1.0, "clearspan": 1.0, "multispan": 0.7, "shed": 0.7,
                  "haunched": 0.5, "plain": 0.5},
        "minor": {"portal": 1.5, "clearspan": 0.7, "shed": 0.7, "haunched": 0.5, "plain": 0.5},
    },
    "bunker": {
        "grand": {"mushroom": 2.0, "barrel": 1.5, "ribbed": 1.5},
        "major": {"ribbed": 2.0, "mushroom": 1.0, "barrel": 1.0, "coffered": 1.0, "plain": 0.5},
        "minor": {"ribbed": 1.5, "mushroom": 1.0, "coffered": 1.0, "barrel": 0.7, "haunched": 0.7, "plain": 0.5},
    },
    "compound": {
        "grand": {"multispan": 2.0, "portal": 1.5, "gable": 1.5, "clearspan": 1.0},
        "major": {"gable": 2.0, "shed": 1.0, "portal": 1.0, "plain": 0.5},
        "minor": {"gable": 1.0, "shed": 1.0, "tray": 1.0, "haunched": 0.5, "plain": 0.5},
    },
    "egyptian": {
        "grand": {"hypostyle": 3.0, "corbelled": 1.0, "battered": 1.0},
        "major": {"lintel": 2.0, "corbelled": 1.5, "battered": 1.0, "hypostyle": 1.0},
        "minor": {"lintel": 1.0, "corbelled": 1.0, "battered": 1.0, "plain": 0.5},
    },
    "scifi": {
        "grand": {"hull": 2.0, "chamfered": 1.5, "lightwell": 1.5},
        "major": {"chamfered": 2.0, "hull": 1.0, "lightwell": 1.0, "plain": 0.3},
        "minor": {"chamfered": 1.0, "lightwell": 1.0, "haunched": 0.5, "plain": 0.5},
    },
    "castle": {
        "grand": {"trussed": 3.0, "barrel": 1.0, "undercroft": 1.0},
        "major": {"timber": 2.0, "barrel": 1.0, "trussed": 1.0, "undercroft": 1.0},
        "minor": {"timber": 1.0, "barrel": 1.0, "haunched": 1.0, "plain": 0.5},
    },
}


# ----------------------------------------------------------------------
# planning: which room gets which profile
# ----------------------------------------------------------------------
def _eligible(room: Room) -> bool:
    return not room.is_connector and room.kind not in SKIP_KINDS


def rank_tiers(level: Level) -> Dict[int, str]:
    """grand / major / minor for every eligible room: the largest space (and any mega-room) is
    grand, the next third major, the rest (and small rooms) minor."""
    rooms = [r for r in level.rooms if _eligible(r)]
    rooms.sort(key=lambda r: -(len(list(r.cells())) * (r.ceil - r.floor)))
    n_major = max(1, int(math.ceil(len(rooms) * 0.3)))
    out = {}
    for rank, room in enumerate(rooms):
        tier = "grand" if rank == 0 or room.kind == "megaroom" else "major" if rank <= n_major else "minor"
        if tier != "grand" and room.w * room.h < 60:
            tier = "minor"
        out[room.id] = tier
    return out


def plan(level: Level) -> Dict[int, Section]:
    """Choose a section for every eligible room (rooms ranked by volume into tiers)."""
    cfg = level.config
    style = getattr(cfg, "arch_style", "") or ""
    if style not in PALETTES:
        return {}
    tiers = rank_tiers(level)
    rooms = sorted((r for r in level.rooms if r.id in tiers),
                   key=lambda r: -(len(list(r.cells())) * (r.ceil - r.floor)))
    bias = getattr(cfg, "arch_weights", {}) or {}
    out: Dict[int, Section] = {}
    used: Dict[str, int] = {}                  # each use of a profile halves its weight: coherent, not uniform
    for room in rooms:
        rng = random.Random(f"{cfg.seed}/{cfg.variant}/arch/{room.id}")
        tier = tiers[room.id]
        palette = PALETTES[style][tier]
        if getattr(cfg, "underground", False) and tier == "grand":
            palette = {"cavern": 4.0, "barrel": 1.0}            # the deep chamber is cut from the rock
        pal = {k: w * float(bias.get(k, 1.0)) * 0.5 ** used.get(k, 0) for k, w in palette.items()}
        pal = {k: w for k, w in pal.items() if w > 0}
        fr = Frame(room, flip_v=rng.random() < 0.5)
        sec = None
        while pal and sec is None:
            name = rng.choices(list(pal), list(pal.values()))[0]
            pal.pop(name)
            if name == "plain":
                used["plain"] = used.get("plain", 0) + 1     # leaving a room plain is damped too
                break
            sec = PROFILES[name](fr, rng)
        if sec is not None:
            used[sec.name] = used.get(sec.name, 0) + 1
            sec.info["tier"] = tier
            out[room.id] = sec
    return out


# ----------------------------------------------------------------------
# applying it to the grid
# ----------------------------------------------------------------------
def _base_connected(level: Level, room: Room) -> bool:
    base = [c for c in room.cells() if any(sp[0] == room.floor for sp in level.room_spans(c, room))]
    if not base:
        return False
    bs = set(base)
    seen, stack = {base[0]}, [base[0]]
    while stack:
        x, y = stack.pop()
        for n in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
            if n in bs and n not in seen:
                seen.add(n)
                stack.append(n)
    return len(seen) == len(bs)


def place_piers(level: Level, room: Room, sec: Section, ctx, tag: str = "piers") -> int:
    """Before the room's features: piers become solid, with a free ring kept around each."""
    fr = sec.frame
    placed = 0
    near_door = {(x + dx, y + dy) for (x, y) in ctx.doors for dx in range(-2, 3) for dy in range(-2, 3)}
    for group in sec.piers:
        cells = [fr.cell(u, v) for u, v in group]
        ring = {(x + dx, y + dy) for (x, y) in cells for dx in (-1, 0, 1) for dy in (-1, 0, 1)} - set(cells)
        ring = {c for c in ring if room.contains(*c)}
        # a pier keeps 2 cells from any doorway cell (features keep 3: an arcade should stay whole),
        # stands on the plain floor, and the ring around it stays walkable
        ok = all(room.contains(*c) and not room.is_diag(*c) and c not in ctx.used and c not in near_door
                 and level.room_spans(c, room) == [(room.floor, room.ceil)] for c in cells)
        ok = ok and all(level.room_spans(c, room) and not room.is_diag(*c) for c in ring)
        if not ok:
            continue
        snap = {c: list(level.spans.get(c, [])) for c in cells}
        for c in cells:
            level.replace_in(c, *room.slot, [])
        if not _base_connected(level, room):
            for c, spans in snap.items():
                level.spans[c] = spans
            continue
        ctx.used.update(cells)
        ctx.used.update(ring)
        for c in cells:
            level.part_solid[c] = f"room:{room.id}/{tag}"
        placed += 1
    return placed


def _corners(fr: Frame, sec: Section, c: Cell) -> Tuple[float, float, float, float]:
    """The surface at a cell's corners (x0y0, x1y0, x1y1, x0y1), above the room's floor."""
    r = fr.room
    cu, cv = fr.uv(c)
    out = []
    for dx, dy in ((0, 0), (1, 0), (1, 1), (0, 1)):
        X, Y = c[0] - r.x + dx, c[1] - r.y + dy
        uf, vf = (X, Y) if fr.along_x else (Y, X)
        if fr.flip:
            vf = fr.W - vf
        out.append(sec.surf(uf, vf, cu, cv))
    return tuple(out)


def apply_ceilings(level: Level, room: Room, sec: Section):
    """After the features: lower ceilings to the profile, never below what must stay open.
    With a smooth surface (and ``arch_smooth``), a cell whose corners lie in one plane gets a
    sloped ceiling: the span keeps its lowest point (what gameplay sees, conservative), and
    ``level.ceil_slope`` holds the corners for the exporters."""
    fr = sec.frame
    smooth = sec.surf is not None and getattr(level.config, "arch_smooth", True)
    gallery = {tuple(c) for c in room.meta.get("gallery", [])}
    lowered = 0
    for c in room.cells():
        if c in gallery or c in level.sky:
            continue
        spans = level.room_spans(c, room)
        if not spans or spans[-1][1] != room.ceil:
            continue
        h = sec.ceil.get(fr.uv(c))
        if h is None:
            continue
        f_top = spans[-1][0]
        need = f_top + HEAD
        for n in ((c[0] + 1, c[1]), (c[0] - 1, c[1]), (c[0], c[1] + 1), (c[0], c[1] - 1)):
            if room.contains(*n):
                continue
            for sp in level.spans_in(n, f_top, room.ceil):   # an opening beside this cell
                need = max(need, sp[1])
        if smooth and c not in level.diag and level.spans[c][-1] == spans[-1] \
                and fr.uv(c) not in sec.forced:                                    # (nothing stacked above)
            zs = [room.floor + z for z in _corners(fr, sec, c)]
            planar = abs((zs[0] + zs[2]) - (zs[1] + zs[3])) <= 1.0
            if planar and min(zs) >= need and max(zs) <= room.ceil and min(zs) < room.ceil:
                zi = tuple(int(round(z)) for z in zs)
                flat = max(need, (min(zi) // 16) * 16)
                level.replace_in(c, f_top, room.ceil, [(f_top, flat)])
                level.ceil_slope[c] = zi
                level.part_ceiling[c] = f"room:{room.id}/{sec.name}"
                lowered += 1
                continue
        if room.floor + h >= room.ceil:     # (the apex of a smooth vault is handled above)
            continue
        new = min(room.ceil, max(room.floor + h, need))
        if new >= spans[-1][1]:
            continue
        level.replace_in(c, f_top, room.ceil, [(f_top, new)])
        if c in level.diag_span and level.diag_span[c][1] == room.ceil:
            level.diag_span[c] = (level.diag_span[c][0], new)
        level.part_ceiling[c] = f"room:{room.id}/{sec.name}"
        lowered += 1
    return lowered
