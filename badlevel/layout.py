"""Spatial layout: mission graph -> rooms on a grid -> corridors with stairs.

1. Rooms are placed by walking the graph's spanning tree from the start
   room. Each child is put next to its parent in a random direction, on a
   floor level close to the parent's, far enough away that a corridor can
   climb the height difference.
2. Corridors are routed with A* over the grid (multi-source from every cell
   of room A to any cell of room B), keeping a solid wall between them and
   other rooms / corridors, with a turn penalty for straighter halls.
3. Height changes become real stairs, placed only on straight stretches so
   every step is full width.
"""
from __future__ import annotations

import heapq
import random
from typing import Dict, List, Optional, Set, Tuple

from .config import GenConfig
from .graph import MissionGraph
from .level import Cell, Corridor, Level, Room
from .shapes import pick_shape

DIRS = [(1, 0), (0, 1), (-1, 0), (0, -1)]
# 8 directions in 45-degree order, so turn angle = index difference
DIRS8 = [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)]
TURN_COST = {0: 0.0, 1: 1.5, 2: 2.5, 3: 8.0}
CORRIDOR_HEIGHT = {1: 128, 2: 160, 3: 192}
PAD = 4  # solid cells around the whole layout


class LayoutError(Exception):
    pass


# ----------------------------------------------------------------------
# room placement
# ----------------------------------------------------------------------
def _room_size(cfg: GenConfig, size: str, rng: random.Random):
    lo, hi, hmin, hmax = cfg.room_sizes[size]
    w = rng.randint(lo, hi)
    h = rng.randint(lo, hi)
    height = rng.randrange(hmin, hmax + 1, 16)
    return w, h, height


def _overlaps(r: Room, rooms: List[Room], gap: int) -> bool:
    for o in rooms:
        if (r.x - gap < o.x + o.w and o.x - gap < r.x + r.w and
                r.y - gap < o.y + o.h and o.y - gap < r.y + r.h):
            return True
    return False


def place_rooms(g: MissionGraph, cfg: GenConfig, rng: random.Random) -> List[Room]:
    rooms: Dict[int, Room] = {}
    w, h, height = _room_size(cfg, g.nodes[0].size, rng)
    rooms[0] = Room(0, 0, g.nodes[0].kind, 0, 0, w, h, 0, height)

    max_floor = cfg.max_level * cfg.level_step
    order = [0]
    tree_edges = [e for e in g.edges if e.tree]
    i = 0
    while i < len(order):
        pid = order[i]
        i += 1
        parent = rooms[pid]
        for e in tree_edges:
            if pid not in (e.a, e.b):
                continue
            cid = e.b if e.a == pid else e.a
            if cid in rooms:
                continue
            node = g.nodes[cid]
            placed = None
            for _try in range(40):
                w, h, height = _room_size(cfg, node.size, rng)
                if node.kind == "secret":
                    dl = 0
                else:
                    dl = rng.choice([-1, 0, 0, 1, 1, 2])
                floor = min(max(parent.floor + dl * cfg.level_step, 0), max_floor)
                climb = abs(floor - parent.floor) // cfg.step_height
                gap = max(cfg.room_gap + 4, climb + 2 * cfg.corridor_radius + 8) + rng.randint(0, 5)
                dx, dy = rng.choice(DIRS)
                if dx:
                    x = parent.x + parent.w + gap if dx > 0 else parent.x - gap - w
                    y = parent.y + rng.randint(-(h - 4), parent.h - 4)
                else:
                    y = parent.y + parent.h + gap if dy > 0 else parent.y - gap - h
                    x = parent.x + rng.randint(-(w - 4), parent.w - 4)
                cand = Room(len(rooms), cid, node.kind, x, y, w, h, floor, floor + height)
                if not _overlaps(cand, list(rooms.values()), cfg.room_gap + 2 * cfg.corridor_radius + 2):
                    placed = cand
                    break
            if placed is None:
                raise LayoutError(f"could not place room for node {cid}")
            rooms[cid] = placed
            order.append(cid)

    if len(rooms) != len(g.nodes):
        raise LayoutError("graph spanning tree did not reach every node")

    # normalise so the layout starts at (PAD, PAD); room ids == node ids
    result = [rooms[n.id] for n in g.nodes]
    for r in result:
        node = g.nodes[r.node]
        shape = pick_shape(r.w, r.h, node.size, node.kind, rng)
        r.shape = shape.name
        if shape.name != "rect" or shape.diag:
            r.mask, r.diag = shape.cells, shape.diag
    minx = min(r.x for r in result)
    miny = min(r.y for r in result)
    for idx, r in enumerate(result):
        r.id = idx
        r.x += PAD - minx
        r.y += PAD - miny
    return result


# ----------------------------------------------------------------------
# corridor routing
# ----------------------------------------------------------------------
class Router:
    """A* corridor router on the cell grid.

    Each route has its own half width ``r`` (3, 5 or 7 cells wide). With
    ``diagonal`` the centre line may also move at 45 degrees; diagonal stretches
    are carved as a band of half-width ``k`` cells (measured across the band),
    so a diagonal corridor is about as wide as a straight one.
    """
    MAX_REACH = 7

    def __init__(self, rooms: List[Room], width: int, height: int, cfg: GenConfig):
        self.rooms = rooms
        self.w = width
        self.h = height
        self.cfg = cfg
        self.room_at: Dict[Cell, int] = {}
        for room in rooms:
            for c in room.cells():
                self.room_at[c] = room.id
        # cells around rooms: cell -> [(room id, Chebyshev distance to the room's box)]
        self._near_room: Dict[Cell, List[Tuple[int, int]]] = {}
        R = self.MAX_REACH
        for room in rooms:
            for y in range(room.y - R, room.y + room.h + R):
                for x in range(room.x - R, room.x + room.w + R):
                    if not room.contains(x, y):
                        self._near_room.setdefault((x, y), []).append((room.id, room.dist(x, y)))
        self.open_cells: Set[Cell] = set()   # every corridor cell carved so far

    def _corridor_block(self, ext: int) -> Set[Cell]:
        reach = ext + 1
        out = set()
        for (x, y) in self.open_cells:
            for dy in range(-reach, reach + 1):
                for dx in range(-reach, reach + 1):
                    out.add((x + dx, y + dy))
        return out

    def _allowed(self, c: Cell, a: Room, b: Room, r: int, ext: int, blocked: Set[Cell]) -> Optional[float]:
        """Extra cost of having the corridor centre at c, or None if blocked."""
        x, y = c
        if x <= ext + 2 or y <= ext + 2 or x >= self.w - ext - 3 or y >= self.h - ext - 3:
            return None
        rid = self.room_at.get(c)
        if rid is not None:
            if rid not in (a.id, b.id) or self.rooms[rid].is_diag(x, y):
                return None
            return 0.0
        if c in blocked:
            return None
        cost = 0.0
        for rid, dist in self._near_room.get(c, ()):
            if rid not in (a.id, b.id):
                if dist <= ext + 1:
                    return None
                continue
            if dist > r + 1:
                continue
            room = a if rid == a.id else b
            # inside the wall margin of an endpoint room: only straight in, away from corners
            inside_x = room.x + r <= x <= room.x + room.w - 1 - r
            inside_y = room.y + r <= y <= room.y + room.h - 1 - r
            above_below = room.x <= x < room.x + room.w
            left_right = room.y <= y < room.y + room.h
            if not ((above_below and inside_x) or (left_right and inside_y)):
                return None
            cost += 6.0
        return cost

    def _near_endpoint(self, c: Cell, a: Room, b: Room, ext: int) -> bool:
        return any(rid in (a.id, b.id) and dist <= ext + 2 for rid, dist in self._near_room.get(c, ()))

    def route(self, a: Room, b: Room, r: int = 1, diagonal: bool = False, k: int = 1) -> List[Cell]:
        ext = max(r, k) if diagonal else r
        blocked = self._corridor_block(ext)
        # perimeter cells of a, away from its corners so the opening fits
        start_cells = []
        for c in a.cells():
            if a.is_diag(*c):
                continue
            on_x_edge = c[0] in (a.x, a.x + a.w - 1)
            on_y_edge = c[1] in (a.y, a.y + a.h - 1)
            ok_x = a.x + r <= c[0] <= a.x + a.w - 1 - r
            ok_y = a.y + r <= c[1] <= a.y + a.h - 1 - r
            if (on_y_edge and ok_x) or (on_x_edge and ok_y):
                start_cells.append(c)
        if not start_cells:
            raise LayoutError(f"room {a.id} too small for a corridor of radius {r}")
        bx0, by0, bx1, by1 = b.x, b.y, b.x + b.w - 1, b.y + b.h - 1

        def heur(x, y):
            dx = max(bx0 - x, 0, x - bx1)
            dy = max(by0 - y, 0, y - by1)
            if diagonal:
                return max(dx, dy) + 0.414 * min(dx, dy)
            return dx + dy

        dirs = range(8) if diagonal else range(0, 8, 2)
        allowed_cache: Dict[Cell, Optional[float]] = {}

        def allowed(c):
            if c not in allowed_cache:
                allowed_cache[c] = self._allowed(c, a, b, r, ext, blocked)
            return allowed_cache[c]

        openq = []
        best: Dict[Tuple[Cell, int], float] = {}
        prev: Dict[Tuple[Cell, int], Tuple[Cell, int]] = {}
        for c in start_cells:
            best[(c, -1)] = 0.0
            heapq.heappush(openq, (heur(*c), 0.0, c, -1))
        goal = None
        limit = self.w * self.h * (8 if diagonal else 4)
        pops = 0
        while openq:
            f, gcost, c, d = heapq.heappop(openq)
            pops += 1
            if pops > limit:
                break
            if best.get((c, d), 1e18) < gcost:
                continue
            if b.contains(*c) and not b.is_diag(*c):
                goal = (c, d)
                break
            for nd in dirs:
                dx, dy = DIRS8[nd]
                n = (c[0] + dx, c[1] + dy)
                if a.contains(*n):
                    continue
                if d >= 0:
                    turn = min(abs(nd - d), 8 - abs(nd - d))
                    if turn > 3:
                        continue
                    turn_cost = TURN_COST[turn]
                else:
                    turn_cost = 0.0
                extra = allowed(n)
                if extra is None:
                    continue
                if dx and dy:
                    # diagonal: no corner cutting, and never next to a doorway
                    if allowed((c[0] + dx, c[1])) is None or allowed((c[0], c[1] + dy)) is None:
                        continue
                    if self.room_at.get(c) is not None or self.room_at.get(n) is not None:
                        continue
                    if self._near_endpoint(c, a, b, ext) or self._near_endpoint(n, a, b, ext):
                        continue
                    step = 1.414
                else:
                    step = 1.0
                ng = gcost + step + extra + turn_cost
                key = (n, nd)
                if ng < best.get(key, 1e18):
                    best[key] = ng
                    prev[key] = (c, d)
                    heapq.heappush(openq, (ng + heur(*n), ng, n, nd))
        if goal is None:
            raise LayoutError(f"no corridor route {a.id}->{b.id}")
        path = [goal[0]]
        st = goal
        while st in prev:
            st = prev[st]
            path.append(st[0])
        path.reverse()
        return path


def _dir(path: List[Cell], i: int) -> Cell:
    j = min(i, len(path) - 2)
    return (path[j + 1][0] - path[j][0], path[j + 1][1] - path[j][1])


def _straight(path: List[Cell], i: int, reach: int, axis_only: bool = False) -> bool:
    lo, hi = i - reach - 1, i + reach + 1
    if lo < 0 or hi >= len(path):
        return False
    d0 = (path[lo + 1][0] - path[lo][0], path[lo + 1][1] - path[lo][1])
    if axis_only and d0[0] and d0[1]:
        return False
    for j in range(lo + 1, hi):
        d = (path[j + 1][0] - path[j][0], path[j + 1][1] - path[j][1])
        if d != d0:
            return False
    return True


def plan_floors(path: List[Cell], fa: int, fb: int, cfg: GenConfig, r: int = None) -> Optional[List[int]]:
    """Floor height for every centre-line cell, with stairs on straight runs."""
    r = cfg.corridor_radius if r is None else r
    steps = abs(fb - fa) // cfg.step_height
    n = len(path)
    if steps == 0:
        return [fa] * n
    margin = r + 2
    cands = [i for i in range(margin, n - margin) if _straight(path, i, r)]
    if len(cands) < steps:
        return None
    mid = (n - 1) / 2.0
    chosen = sorted(sorted(cands, key=lambda i: (abs(i - mid), i))[:steps])
    sign = 1 if fb > fa else -1
    floors = []
    k = 0
    ci = 0
    for i in range(n):
        while ci < len(chosen) and chosen[ci] <= i:
            k += 1
            ci += 1
        floors.append(fa + sign * k * cfg.step_height)
    return floors


def diag_half_width(r: int) -> int:
    """Band half-width (in cells across a 45-degree band) matching a straight corridor of radius r."""
    return max(1, round((2 * r + 1) * 32 / 45.25 - 1))


def _stamp(center: Cell, direction: Cell, r: int, k: int):
    """Cells covered by the corridor around one centre-line cell."""
    cx, cy = center
    dx, dy = direction
    if dx and dy:
        same = dx == dy  # (1,1)/(-1,-1): band across x-y; otherwise across x+y
        for oy in range(-k - 1, k + 2):
            for ox in range(-k - 1, k + 2):
                x, y = cx + ox, cy + oy
                band = (x - y) - (cx - cy) if same else (x + y) - (cx + cy)
                along = (x + y) - (cx + cy) if same else (x - y) - (cx - cy)
                if abs(band) <= k and abs(along) <= 1:
                    yield (x, y), abs(ox) + abs(oy)
    else:
        for oy in range(-r, r + 1):
            for ox in range(-r, r + 1):
                yield (cx + ox, cy + oy), abs(ox) + abs(oy)


_SOLID_CORNER = {frozenset({(0, -1), (-1, 0)}): "sw", frozenset({(0, -1), (1, 0)}): "se",
                 frozenset({(0, 1), (-1, 0)}): "nw", frozenset({(0, 1), (1, 0)}): "ne"}


def add_half_cells(level: Level, cid: int, carved: List[Cell]) -> List[Cell]:
    """Smooth 45-degree walls: a solid cell with exactly two adjacent open sides (both
    cells of this corridor, at most one stair step apart) becomes a diagonal half cell. Covers
    diagonal stretches and chamfers inner corners of bends. Anything ambiguous
    stays solid (a stepped wall, still sealed and valid)."""
    own = f"corridor:{cid}"
    cand = set()
    for (x, y) in carved:
        for dx, dy in DIRS:
            n = (x + dx, y + dy)
            if n not in level.spans:
                cand.add(n)
    added = []
    for (x, y) in sorted(cand):
        open_sides, solid_sides = set(), set()
        spans = set()
        for dx, dy in DIRS:
            n = (x + dx, y + dy)
            if n in level.spans:
                if level.owner.get(n) != own or n in level.diag or len(level.spans[n]) != 1:
                    open_sides = None
                    break
                open_sides.add((dx, dy))
                spans.add(level.spans[n][0])
            else:
                solid_sides.add((dx, dy))
        if not open_sides or len(open_sides) != 2:
            continue
        corner = _SOLID_CORNER.get(frozenset(solid_sides))
        if corner is None:  # open sides opposite each other: not a corner
            continue
        # on stairs the two neighbours may differ by one step: take the overlap
        f = max(sp[0] for sp in spans)
        c = min(sp[1] for sp in spans)
        if f - min(sp[0] for sp in spans) > level.config.step_height or c - f < level.config.player_height:
            continue
        level.spans[(x, y)] = [(f, c)]
        level.owner[(x, y)] = own
        level.diag[(x, y)] = corner
        added.append((x, y))
    return added


def _corridor_style(g: MissionGraph, e, a: Room, b: Room, cfg: GenConfig, rng: random.Random):
    kinds = {g.nodes[e.a].kind, g.nodes[e.b].kind}
    if e.kind == "secret":
        r, diag = 1, False
    elif not e.tree:
        r, diag = rng.choice([1, 1, 2]), rng.random() < cfg.diagonal_chance
    elif kinds & {"arena", "hub"}:
        r, diag = rng.choice([1, 2, 2, 3]), rng.random() < cfg.diagonal_chance
    elif kinds & {"supply", "secret", "key"}:
        r, diag = rng.choice([1, 1, 2]), rng.random() < cfg.diagonal_chance
    else:
        r, diag = rng.choice([1, 2, 2]), rng.random() < cfg.diagonal_chance
    # the opening must fit the smaller room
    smallest = min(a.w, a.h, b.w, b.h)
    r = max(1, min(r, cfg.max_corridor_radius, (smallest - 3) // 2))
    return r, diag


def build_layout(g: MissionGraph, cfg: GenConfig, rng: random.Random, seed: int) -> Level:
    rooms = place_rooms(g, cfg, rng)
    width = max(r.x + r.w for r in rooms) + PAD
    height = max(r.y + r.h for r in rooms) + PAD
    # leave room for corridors to swing around the outside of the layout
    extra = 8
    for r in rooms:
        r.x += extra
        r.y += extra
    width += 2 * extra
    height += 2 * extra

    level = Level(seed=seed, cell=cfg.cell, width=width, height=height,
                  rooms=rooms, graph=g, config=cfg)
    for room in rooms:
        for c in room.cells():
            level.spans[c] = [(room.floor, room.ceil)]
            level.owner[c] = f"room:{room.id}"
        level.diag.update(room.world_diag())

    router = Router(rooms, width, height, cfg)
    # route tree edges first (they must succeed), then loops (optional)
    edges = sorted(g.edges, key=lambda e: (not e.tree, e.id))
    for e in edges:
        a, b = rooms[e.a], rooms[e.b]
        r, diag = _corridor_style(g, e, a, b, cfg, rng)
        k = diag_half_width(r)
        path = floors = None
        # preferred style first, then fall back to a plain narrow corridor
        for (rr, dd) in dict.fromkeys([(r, diag), (r, False), (1, False)]):
            try:
                cand = router.route(a, b, rr, dd, diag_half_width(rr))
            except LayoutError:
                continue
            if not e.tree:
                (ax, ay), (bx, by) = a.center(), b.center()
                if len(cand) > 1.5 * (abs(ax - bx) + abs(ay - by)) + 12:
                    continue
            while len(cand) > 1 and a.contains(*cand[1]):
                cand.pop(0)
            inner = cand[1:-1]
            fl = plan_floors(inner, a.floor, b.floor, cfg, rr)
            if fl is not None:
                path, floors, r, diag, k = inner, fl, rr, dd, diag_half_width(rr)
                break
        if path is None:
            if e.tree:
                raise LayoutError(f"no usable corridor {a.id}->{b.id}")
            level.notes.append(f"loop {e.a}-{e.b} dropped")
            continue
        diag = diag and any(d[0] and d[1] for d in (_dir(path, i) for i in range(len(path) - 1)))
        cid = len(level.corridors)
        h = CORRIDOR_HEIGHT.get(r, 128 + 32 * (r - 1))
        corr = Corridor(cid, e.id, a.id, b.id, path, floors, e.kind, e.key, radius=r, height=h, diagonal=diag)
        # each carved cell takes the floor of its nearest centre-line cell
        claim: Dict[Cell, Tuple[int, int, int]] = {}
        for i, (c0, f) in enumerate(zip(path, floors)):
            dirs = {_dir(path, i)}
            if i > 0:
                dirs.add(_dir(path, i - 1))   # union of both stamps at bends
            for d in dirs:
                for c, dist in _stamp(c0, d, r, k):
                    if c in level.spans:
                        continue
                    pri = (dist, i, f)
                    if c not in claim or pri < claim[c]:
                        claim[c] = pri
        carved = []
        for c, (_, _, f) in claim.items():
            level.spans[c] = [(f, f + h)]
            level.owner[c] = f"corridor:{cid}"
            carved.append(c)
        carved += add_half_cells(level, cid, carved)
        router.open_cells.update(carved)
        level.corridors.append(corr)
    return level
