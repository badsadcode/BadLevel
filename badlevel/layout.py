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

DIRS = [(1, 0), (0, 1), (-1, 0), (0, -1)]
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
                gap = max(cfg.room_gap + 2, climb + 2 * cfg.corridor_radius + 6) + rng.randint(0, 4)
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
    def __init__(self, rooms: List[Room], width: int, height: int, cfg: GenConfig):
        self.rooms = rooms
        self.w = width
        self.h = height
        self.cfg = cfg
        self.r = cfg.corridor_radius
        # corridor cells already carved: cell -> corridor id
        self.carved: Dict[Cell, int] = {}
        # base blocking from rooms: cell -> room id of the blocking room or -1
        self.room_at: Dict[Cell, int] = {}
        for room in rooms:
            for c in room.cells():
                self.room_at[c] = room.id
        self._near_room: Dict[Cell, List[int]] = {}
        reach = self.r + 1
        for room in rooms:
            for y in range(room.y - reach, room.y + room.h + reach):
                for x in range(room.x - reach, room.x + room.w + reach):
                    if not room.contains(x, y):
                        self._near_room.setdefault((x, y), []).append(room.id)
        self._near_corr: Dict[Cell, Set[int]] = {}

    def _mark_corridor_clearance(self, cells, cid):
        reach = 2 * self.r + 1
        for (x, y) in cells:
            for dy in range(-reach, reach + 1):
                for dx in range(-reach, reach + 1):
                    self._near_corr.setdefault((x + dx, y + dy), set()).add(cid)

    def _allowed(self, c: Cell, a: Room, b: Room) -> Optional[float]:
        """Return extra cost of having the corridor centre at c, or None if blocked."""
        x, y = c
        r = self.r
        if x <= r + 1 or y <= r + 1 or x >= self.w - r - 2 or y >= self.h - r - 2:
            return None
        rid = self.room_at.get(c)
        if rid is not None:
            return 0.0 if rid in (a.id, b.id) else None
        if c in self._near_corr:
            return None
        cost = 0.0
        for rid in self._near_room.get(c, ()):
            if rid not in (a.id, b.id):
                return None
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

    def route(self, a: Room, b: Room) -> List[Cell]:
        # perimeter cells of a, away from its corners so a 3-wide opening fits
        start_cells = []
        for c in a.cells():
            on_x_edge = c[0] in (a.x, a.x + a.w - 1)
            on_y_edge = c[1] in (a.y, a.y + a.h - 1)
            ok_x = a.x + self.r <= c[0] <= a.x + a.w - 1 - self.r
            ok_y = a.y + self.r <= c[1] <= a.y + a.h - 1 - self.r
            if (on_y_edge and ok_x) or (on_x_edge and ok_y):
                start_cells.append(c)
        bx0, by0, bx1, by1 = b.x, b.y, b.x + b.w - 1, b.y + b.h - 1

        def heur(x, y):
            return max(bx0 - x, 0, x - bx1) + max(by0 - y, 0, y - by1)

        # state = (cell, dir index); -1 dir for start
        openq = []
        best: Dict[Tuple[Cell, int], float] = {}
        prev: Dict[Tuple[Cell, int], Tuple[Cell, int]] = {}
        for c in start_cells:
            s = (c, -1)
            best[s] = 0.0
            heapq.heappush(openq, (heur(*c), 0.0, c, -1))
        goal = None
        limit = self.w * self.h * 4
        pops = 0
        while openq:
            f, gcost, c, d = heapq.heappop(openq)
            pops += 1
            if pops > limit:
                break
            if best.get((c, d), 1e18) < gcost:
                continue
            if b.contains(*c):
                goal = (c, d)
                break
            for nd, (dx, dy) in enumerate(DIRS):
                n = (c[0] + dx, c[1] + dy)
                if a.contains(*n):
                    continue
                extra = self._allowed(n, a, b)
                if extra is None:
                    continue
                step = 1.0 + extra + (0.0 if d in (-1, nd) else 2.5)
                ng = gcost + step
                key = (n, nd)
                if ng < best.get(key, 1e18):
                    best[key] = ng
                    prev[key] = (c, d)
                    heapq.heappush(openq, (ng + heur(*n), ng, n, nd))
        if goal is None:
            raise LayoutError(f"no corridor route {a.id}->{b.id}")
        path = [goal[0]]
        s = goal
        while s in prev:
            s = prev[s]
            path.append(s[0])
        path.reverse()
        return path


def _straight(path: List[Cell], i: int, reach: int) -> bool:
    lo, hi = i - reach - 1, i + reach + 1
    if lo < 0 or hi >= len(path):
        return False
    d0 = (path[lo + 1][0] - path[lo][0], path[lo + 1][1] - path[lo][1])
    for j in range(lo + 1, hi):
        d = (path[j + 1][0] - path[j][0], path[j + 1][1] - path[j][1])
        if d != d0:
            return False
    return True


def plan_floors(path: List[Cell], fa: int, fb: int, cfg: GenConfig) -> Optional[List[int]]:
    """Floor height for every centre-line cell, with stairs on straight runs."""
    steps = abs(fb - fa) // cfg.step_height
    n = len(path)
    if steps == 0:
        return [fa] * n
    margin = cfg.corridor_radius + 2
    cands = [i for i in range(margin, n - margin) if _straight(path, i, cfg.corridor_radius)]
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


def build_layout(g: MissionGraph, cfg: GenConfig, rng: random.Random, seed: int) -> Level:
    rooms = place_rooms(g, cfg, rng)
    width = max(r.x + r.w for r in rooms) + PAD
    height = max(r.y + r.h for r in rooms) + PAD
    # leave room for corridors to swing around the outside of the layout
    extra = 6
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

    router = Router(rooms, width, height, cfg)
    r = cfg.corridor_radius
    # route tree edges first (they must succeed), then loops (optional)
    edges = sorted(g.edges, key=lambda e: (not e.tree, e.id))
    for e in edges:
        a, b = rooms[e.a], rooms[e.b]
        try:
            path = router.route(a, b)
        except LayoutError:
            if e.tree:
                raise
            level.notes.append(f"loop {e.a}-{e.b} dropped: no route")
            continue
        if not e.tree:
            (ax, ay), (bx, by) = a.center(), b.center()
            direct = abs(ax - bx) + abs(ay - by)
            if len(path) > 1.5 * direct + 12:
                level.notes.append(f"loop {e.a}-{e.b} dropped: detour too long")
                continue
        # trim to: last cell inside a ... first cell inside b
        while len(path) > 1 and a.contains(*path[1]):
            path.pop(0)
        inner = path[1:-1]
        floors = plan_floors(inner, a.floor, b.floor, cfg)
        if floors is None:
            if e.tree:
                raise LayoutError(f"corridor {a.id}->{b.id} too short for stairs")
            level.notes.append(f"loop {e.a}-{e.b} dropped: too short for stairs")
            continue
        cid = len(level.corridors)
        corr = Corridor(cid, e.id, a.id, b.id, inner, floors, e.kind, e.key)
        # each carved cell takes the floor of its nearest centre-line cell
        claim: Dict[Cell, Tuple[int, int, int]] = {}
        for i, ((cx, cy), f) in enumerate(zip(inner, floors)):
            for dy in range(-r, r + 1):
                for dx in range(-r, r + 1):
                    c = (cx + dx, cy + dy)
                    if c in level.spans:
                        continue
                    pri = (abs(dx) + abs(dy), i, f)
                    if c not in claim or pri < claim[c]:
                        claim[c] = pri
        carved = []
        for c, (_, _, f) in claim.items():
            level.spans[c] = [(f, f + cfg.corridor_height)]
            level.owner[c] = f"corridor:{cid}"
            carved.append(c)
        router._mark_corridor_clearance(carved, cid)
        for c in carved:
            router.carved[c] = cid
        level.corridors.append(corr)
    return level
