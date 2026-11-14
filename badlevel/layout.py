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
from .spaces import space_for
from .spaces.corridor import _corridor_style, add_beats, corridor_height
from .spaces.hall import _mergeable, _seam_cells, _seam_ok, build_halls

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
    lo = max(6, int(round(lo * cfg.room_scale)))
    hi = max(lo, int(round(hi * cfg.room_scale)))
    w = rng.randint(lo, hi)
    h = rng.randint(lo, hi)
    height = rng.randrange(hmin, hmax + 1, 16)
    v = getattr(cfg, "size_variety", 0.0)
    if v > 0 and size in ("small", "medium", "large"):
        # a wider spread of sizes: from a closet to a hall in the same level (0.66x - 2x at 1)
        f = 2 ** rng.uniform(-0.6 * v, 1.0 * v)
        w = max(6, min(40, int(round(w * f))))
        h = max(6, min(40, int(round(h * f))))
        height = int(round(height * f ** 0.5 / 16)) * 16
    height = max(128, int(round(height * cfg.height_scale / 16)) * 16)
    return w, h, height


SLAB = 16  # minimum solid between stacked spaces


def vertical_conflict(a: Tuple[int, int], b: Tuple[int, int], gap: int = SLAB) -> bool:
    return a[0] < b[1] + gap and b[0] < a[1] + gap


def _overlaps(r: Room, rooms: List[Room], gap: int, setback: int = 0) -> bool:
    """Footprints too close *and* on overlapping heights (rooms on other storeys may stack).
    ``setback``: extra distance around open areas, so their surroundings can become roofs."""
    for o in rooms:
        if not vertical_conflict(r.slot, o.slot):
            continue
        g = gap + (setback if "courtyard" in (r.kind, o.kind) else 0)
        if (r.x - g < o.x + o.w and o.x - g < r.x + r.w and
                r.y - g < o.y + o.h and o.y - g < r.y + r.h):
            return True
    return False


def _storey_floors(g: MissionGraph, cfg: GenConfig, rng: random.Random) -> Dict[int, int]:
    """Floor heights decided up front for multi-storey levels: storey * height + a small offset."""
    from .graph import CONNECTOR_KINDS
    H = cfg.storey_height
    floors: Dict[int, int] = {}
    tree = [e for e in g.edges if e.tree]
    for n in g.nodes:
        if n.kind not in CONNECTOR_KINDS and n.kind != "secret":
            floors[n.id] = n.storey * H + rng.choice([0, 0, 32, 64])
    for n in g.nodes:
        if n.kind == "secret":
            parent = next(e.a if e.b == n.id else e.b for e in g.edges if n.id in (e.a, e.b))
            floors[n.id] = floors.get(parent, n.storey * H)
    for n in g.nodes:
        if n.kind in CONNECTOR_KINDS:
            nb = [e.a if e.b == n.id else e.b for e in tree if n.id in (e.a, e.b)]
            floors[n.id] = min(floors[m] for m in nb)
            n.top = max(floors[m] for m in nb)
    return floors


def place_rooms(g: MissionGraph, cfg: GenConfig, rng: random.Random) -> List[Room]:
    multi = cfg.storeys > 1
    H = cfg.storey_height
    preset = _storey_floors(g, cfg, rng) if multi else {}

    def make(node, x, y, floor, parent_floor):
        """A candidate room for a node, or None if its space type can't exist at this height."""
        return space_for(node.kind).propose(node, floor, cfg, rng, multi)

    comp = cfg.composition
    root = g.center if (comp in ("hub", "central") and g.center is not None) else 0
    first = g.nodes[root]
    rooms: Dict[int, Room] = {}
    r0 = make(first, 0, 0, preset.get(root, 0), 0)
    if r0 is None:
        raise LayoutError("start room")
    rooms[root] = r0
    spokes = {"next": rng.randrange(4)} if comp in ("hub", "central") else {}
    axis = DIRS[rng.randrange(4)] if comp == "linear" else None

    max_floor = cfg.max_level * cfg.level_step
    halls: List[List[int]] = []          # node ids per merged hall
    order = [root]
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
            # merged hall: some neighbours are placed touching the parent, one space, no corridor
            hall_of = {r: h for h in halls for r in h}
            merge = (_mergeable(e, parent, node, g) and rng.random() < cfg.merge_chance
                     and len(hall_of.get(pid, [pid])) < cfg.max_hall_rooms)
            tries = 60 + int(60 * getattr(cfg, "spaciousness", 0.0))    # bigger rooms are harder to fit
            from .graph import CONNECTOR_KINDS
            pull = getattr(cfg, "diagonal_pull", 0.0)
            diag_wish = (pull > 0 and not merge and node.kind not in CONNECTOR_KINDS + ("secret",)
                         and rng.random() < cfg.diagonal_chance * pull)
            for _try in range(tries):
                if merge and _try >= 25:
                    merge = False                    # couldn't fit: fall back to a corridor
                if multi:
                    floor = preset[cid]
                elif node.kind == "secret":
                    floor = parent.floor
                else:
                    dl = rng.choice([-1, 0, 0, 1, 1, 2])
                    floor = min(max(parent.floor + dl * cfg.level_step, 0), max_floor)
                cand = make(node, 0, 0, floor, parent.floor)
                if cand is None:
                    raise LayoutError(f"connector {node.kind} can't climb {node.top - floor}")
                w, h = cand.w, cand.h
                # corridor climb between the two attachment heights
                pf = _port_floor(parent, cand.floor, cand.top_floor)
                cf = _port_floor(cand, parent.floor, parent.top_floor)
                climb = abs(pf - cf) // cfg.step_height
                gap = max(cfg.room_gap + 4, climb + 2 * cfg.corridor_radius + 8) + rng.randint(0, 5)
                if comp == "central" and pid == root:
                    gap = max(cfg.room_gap + 4, climb + 2 * cfg.corridor_radius + 6)   # a tight ring
                setback = getattr(cfg, "yard_setback", 0)
                if setback and "courtyard" in (node.kind, parent.kind):
                    gap += setback                     # an open area keeps room around it for its roofs
                stack = multi and _try % 3 == 2 and not merge
                diag_place = diag_wish and _try < tries // 2 and not stack
                if merge:
                    if not multi and node.kind != "secret":
                        floor = parent.floor + rng.choice([0, 0, 0, 16, 32, 48, 64, -32, -64])
                        floor = min(max(floor, 0), max_floor)
                        cand.floor, cand.ceil = floor, floor + (cand.ceil - cand.floor)
                    if abs(cand.floor - parent.floor) > 64:
                        continue
                    if comp == "tree" or _try >= 15:
                        dx, dy = rng.choice(DIRS)
                    else:   # merged rooms follow the composition too (a spine stays a spine)
                        dx, dy = _composed_dir(comp, rooms[root], parent, pid == root, node, spokes, axis, rng)
                    lap = 6
                    if dx:
                        if min(h, parent.h) < lap + 2:
                            continue
                        x = parent.x + parent.w if dx > 0 else parent.x - w
                        y = parent.y + rng.randint(-(h - lap), parent.h - lap)
                    else:
                        if min(w, parent.w) < lap + 2:
                            continue
                        y = parent.y + parent.h if dy > 0 else parent.y - h
                        x = parent.x + rng.randint(-(w - lap), parent.w - lap)
                    cand.x, cand.y = x, y
                    others = [o for o in rooms.values() if o is not parent]
                    if _overlaps(cand, others, cfg.room_gap + 2 * cfg.corridor_radius + 2, setback) or \
                            _overlaps(cand, [parent], 0):
                        continue
                    if not _seam_ok(cand, parent):
                        continue
                    placed = cand
                    break
                if stack:
                    # try to sit over / under an existing room on another storey, near the parent
                    others = [o for o in rooms.values() if not vertical_conflict(cand.slot, o.slot)
                              and abs(o.x - parent.x) + abs(o.y - parent.y) < 40]
                    if not others:
                        continue
                    o = rng.choice(others)
                    x = o.x + rng.randint(-(w // 2), max(0, o.w - w // 2))
                    y = o.y + rng.randint(-(h // 2), max(0, o.h - h // 2))
                else:
                    if comp == "tree" or _try >= 30:
                        dx, dy = rng.choice(DIRS)
                    else:
                        dx, dy = _composed_dir(comp, rooms[root], parent, pid == root, node, spokes, axis, rng)
                    if comp == "linear" and getattr(node, "main", False) and _try < 30:
                        # the spine: rooms centred on the axis, not shifted sideways
                        if dx:
                            x = parent.x + parent.w + gap if dx > 0 else parent.x - gap - w
                            y = parent.y + (parent.h - h) // 2 + rng.randint(-1, 1)
                        else:
                            y = parent.y + parent.h + gap if dy > 0 else parent.y - gap - h
                            x = parent.x + (parent.w - w) // 2 + rng.randint(-1, 1)
                    elif diag_place:
                        # off the parent's corner, so the corridor between them cuts across at 45
                        # degrees (the gap on both axes leaves room past the square doorways)
                        sx, sy = dx or rng.choice((-1, 1)), dy or rng.choice((-1, 1))
                        x = parent.x + parent.w + gap if sx > 0 else parent.x - gap - w
                        y = parent.y + parent.h + gap if sy > 0 else parent.y - gap - h
                        x -= sx * rng.randint(0, max(0, min(w, parent.w) // 2))
                        y -= sy * rng.randint(0, max(0, min(h, parent.h) // 2))
                    elif dx:
                        x = parent.x + parent.w + gap if dx > 0 else parent.x - gap - w
                        y = parent.y + rng.randint(-(h - 4), parent.h - 4)
                    else:
                        y = parent.y + parent.h + gap if dy > 0 else parent.y - gap - h
                        x = parent.x + rng.randint(-(w - 4), parent.w - 4)
                cand.x, cand.y = x, y
                if stack and not _overlaps(cand, [parent], gap - 4) and \
                        abs((x + w / 2) - (parent.x + parent.w / 2)) + abs((y + h / 2) - (parent.y + parent.h / 2)) > 60:
                    continue
                if not _overlaps(cand, list(rooms.values()), cfg.room_gap + 2 * cfg.corridor_radius + 2, setback):
                    placed = cand
                    if diag_place:
                        placed.meta["diag_from"] = pid    # its corridor cuts across at 45 degrees
                    break
            if placed is None:
                raise LayoutError(f"could not place room for node {cid}")
            rooms[cid] = placed
            order.append(cid)
            if merge:
                e.merged = True
                h0 = hall_of.get(pid)
                if h0 is None:
                    h0 = [pid]
                    halls.append(h0)
                h0.append(cid)

    if len(rooms) != len(g.nodes):
        raise LayoutError("graph spanning tree did not reach every node")

    # normalise so the layout starts at (PAD, PAD); room ids == node ids
    result = [rooms[n.id] for n in g.nodes]
    merged_nodes = {n for h in halls for n in h}
    for r in result:
        space_for(r.kind).assign_shape(r, g.nodes[r.node], cfg, rng, r.node in merged_nodes)
    minx = min(r.x for r in result)
    miny = min(r.y for r in result)
    for idx, r in enumerate(result):
        r.id = idx
        r.x += PAD - minx
        r.y += PAD - miny
    for h in halls:
        for n in h:
            rooms[n].meta["hall"] = sorted(h)
    return result


def _composed_dir(comp: str, root: Room, parent: Room, from_root: bool, node, spokes: dict, axis, rng):
    """Which side of the parent a new room goes on, by composition.

    hub / central: the root's children go round its four sides in turn (spokes); further
    rooms keep heading outwards, away from the centre. linear: the critical path keeps
    to one axis (the spine), side rooms branch off perpendicular (ribs)."""
    if comp in ("hub", "central"):
        if from_root:
            d = DIRS[spokes["next"] % 4]
            spokes["next"] += 1
            return d
        (rx, ry), (px, py) = root.center(), parent.center()
        ox, oy = px - rx, py - ry
        outward = (1 if ox > 0 else -1, 0) if abs(ox) >= abs(oy) else (0, 1 if oy > 0 else -1)
        if rng.random() < 0.75:
            return outward
        return rng.choice([(outward[1], outward[0]), (-outward[1], -outward[0])])
    if comp == "linear":
        if getattr(node, "main", False):
            return axis
        return rng.choice([(axis[1], axis[0]), (-axis[1], -axis[0])])
    return rng.choice(DIRS)


def _port_floor(room: Room, other_floor: int, other_top: Optional[int] = None) -> int:
    """Height at which a corridor from ``room`` towards a room at ``other_floor`` attaches."""
    if room.top_floor is None:
        return room.floor
    target = other_floor if other_top is None else other_floor
    return room.top_floor if abs(target - room.top_floor) < abs(target - room.floor) else room.floor


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
        self.room_at: Dict[Cell, List[int]] = {}
        for room in rooms:
            for c in room.cells():
                self.room_at.setdefault(c, []).append(room.id)
        # cells around rooms: cell -> [(room id, Chebyshev distance to the room's box)]
        self._near_room: Dict[Cell, List[Tuple[int, int]]] = {}
        R = self.MAX_REACH
        for room in rooms:
            for y in range(room.y - R, room.y + room.h + R):
                for x in range(room.x - R, room.x + room.w + R):
                    if not room.contains(x, y):
                        self._near_room.setdefault((x, y), []).append((room.id, room.dist(x, y)))
        # every corridor cell carved so far, with its vertical range
        self.open_cells: Dict[Cell, List[Tuple[int, int]]] = {}

    def add_room(self, room: Room):
        """Register a room added after placement (an overlook booth): the router avoids it like
        any other room and can route into it."""
        if room not in self.rooms:
            self.rooms.append(room)
        for c in room.cells():
            self.room_at.setdefault(c, []).append(room.id)
        R = self.MAX_REACH
        for y in range(room.y - R, room.y + room.h + R):
            for x in range(room.x - R, room.x + room.w + R):
                if not room.contains(x, y):
                    self._near_room.setdefault((x, y), []).append((room.id, room.dist(x, y)))

    def remove_room(self, room: Room):
        for cells in self.room_at.values():
            if room.id in cells:
                cells.remove(room.id)
        for lst in self._near_room.values():
            lst[:] = [t for t in lst if t[0] != room.id]
        if self.rooms and self.rooms[-1] is room:
            self.rooms.pop()

    def add_open(self, c: Cell, lo: int, hi: int):
        self.open_cells.setdefault(c, []).append((lo, hi))

    def _corridor_block(self, ext: int, env: Tuple[int, int], cross_floor: Optional[int] = None):
        """(blocked, soft): cells too close to other corridors, and cells where a level corridor
        may cross another one at exactly its own floor height (a junction, at a small cost)."""
        reach = ext + 1
        hard, soft = set(), set()
        for (x, y), ranges in self.open_cells.items():
            conflicting = [rg for rg in ranges if vertical_conflict(env, rg, 32)]
            if not conflicting:
                continue
            crossable = cross_floor is not None and all(rg[0] == cross_floor for rg in conflicting)
            target = soft if crossable else hard
            for dy in range(-reach, reach + 1):
                for dx in range(-reach, reach + 1):
                    target.add((x + dx, y + dy))
        return hard, soft - hard

    def _allowed(self, c: Cell, a: Room, b: Room, r: int, ext: int, blocked: Set[Cell],
                 env: Tuple[int, int], b_port: Optional[set], soft: Set[Cell] = frozenset()) -> Optional[float]:
        """Extra cost of having the corridor centre at c, or None if blocked."""
        x, y = c
        if x <= ext + 2 or y <= ext + 2 or x >= self.w - ext - 3 or y >= self.h - ext - 3:
            return None
        rids = self.room_at.get(c)
        if rids:
            for rid in rids:
                room = self.rooms[rid]
                if rid in (a.id, b.id):
                    if room.is_diag(x, y) or (rid == b.id and b_port is not None and c not in b_port):
                        return None
                    return 0.0
            if any(vertical_conflict(self.rooms[rid].slot, env) for rid in rids):
                return None
        if c in blocked:
            return None
        cost = 3.0 if c in soft else 0.0       # crossing: allowed, but cross quickly
        for rid, dist in self._near_room.get(c, ()):
            if rid not in (a.id, b.id):
                if dist <= ext + 1 and vertical_conflict(self.rooms[rid].slot, env):
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

    def _in_room(self, c: Cell, room: Room) -> bool:
        return room.id in self.room_at.get(c, ())

    def route(self, a: Room, b: Room, r: int = 1, diagonal: bool = False, k: int = 1,
              env: Tuple[int, int] = (0, 128), a_port: Optional[set] = None,
              b_port: Optional[set] = None, cross_floor: Optional[int] = None) -> List[Cell]:
        ext = max(r, k) if diagonal else r
        blocked, soft = self._corridor_block(ext, env, cross_floor)
        # perimeter cells of a, away from its corners so the opening fits
        start_cells = []
        for c in a.cells():
            if a.is_diag(*c) or (a_port is not None and c not in a_port):
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

        # with ``diagonal_pull`` a 45-degree step costs less than its length (1.414 -> 1.0 at 1):
        # rooms sit side by side on the grid, so a true-length diagonal is almost never the
        # shortest way and diagonal corridors stayed rare (7 of 338); now offsets become diagonals
        dstep = 1.414 - 0.414 * getattr(self.cfg, "diagonal_pull", 0.0)

        def heur(x, y):
            dx = max(bx0 - x, 0, x - bx1)
            dy = max(by0 - y, 0, y - by1)
            if diagonal:
                return max(dx, dy) + (dstep - 1.0) * min(dx, dy)
            return dx + dy

        dirs = range(8) if diagonal else range(0, 8, 2)
        allowed_cache: Dict[Cell, Optional[float]] = {}

        def allowed(c):
            if c not in allowed_cache:
                allowed_cache[c] = self._allowed(c, a, b, r, ext, blocked, env, b_port, soft)
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
            if b.contains(*c) and not b.is_diag(*c) and (b_port is None or c in b_port):
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
                    if self._in_room(c, a) or self._in_room(c, b) or self._in_room(n, a) or self._in_room(n, b):
                        continue
                    if self._near_endpoint(c, a, b, ext) or self._near_endpoint(n, a, b, ext):
                        continue
                    step = dstep
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
                if len(level.spans[n]) != 1 or level.owner_of(n, level.spans[n][0]) != own or n in level.diag:
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
        level.add_span((x, y), (f, c), own)
        level.diag[(x, y)] = corner
        level.diag_span[(x, y)] = (f, c)
        added.append((x, y))
    return added


def _gap(a: Room, b: Room) -> int:
    """Cells of empty space between two room footprints (0 if they touch or overlap)."""
    dx = max(b.x - (a.x + a.w), a.x - (b.x + b.w), 0)
    dy = max(b.y - (a.y + a.h), a.y - (b.y + b.h), 0)
    return dx + dy


def choose_loops(g: MissionGraph, rooms: List[Room], cfg: GenConfig, rng: random.Random):
    """Loops make circuits: pick rooms apart in the graph (2+ hops: a triangle that bypasses a room,
    3+ preferred) but close in space, so the loop corridor is short. Now and then a long one on purpose."""
    n_loops = cfg.loops + (2 if cfg.composition == "central" else 0)
    for _ in range(n_loops):
        cands = []
        for a in rooms:
            for b in rooms:
                na, nb = g.nodes[a.node], g.nodes[b.node]
                if a.id >= b.id or na.zone != nb.zone or g.connected(a.node, b.node):
                    continue
                if a.is_connector or b.is_connector or {"secret", "exit"} & {a.kind, b.kind}:
                    continue
                hops = g.hop_distance(a.node, b.node)
                if hops < 2:
                    continue
                # close in space; bigger circuits (3+ hops) and same-height pairs (junctions) preferred;
                # radial compositions bring loops back to their centre
                bonus = -20 if g.center is not None and g.center in (a.node, b.node) else 0
                cands.append((_gap(a, b) + (10 if hops == 2 else 0) + (12 if a.floor != b.floor else 0) + bonus,
                              a.node, b.node))
        if not cands:
            break
        cands.sort()
        if rng.random() < cfg.long_loop_chance and cfg.composition != "linear":
            pick = rng.choice(cands[len(cands) // 2:])          # a deliberately long one (not on a spine)
        else:
            pick = rng.choice(cands[:3])
        _, a, b = pick
        e = g.add_edge(a, b, tree=False)
        if g.nodes[a].storey != g.nodes[b].storey:
            e.connector = "drop"


def _attachment(room: Room, other: Room):
    """(port cells or None, floor) where a corridor from ``room`` to ``other`` attaches."""
    return space_for(room.kind).attachment(room, other)


def _drop_fits(hi: Room, lo: Room, cfg: GenConfig, h: int = 128) -> bool:
    """A one-way drop: the corridor runs level from hi, then falls down a short shaft to lo's floor."""
    if hi.is_connector or lo.is_connector:
        return False
    return 64 <= hi.floor - lo.floor <= cfg.max_drop


def plan_drop(path: List[Cell], fa: int, fb: int, r: int, h: int):
    """Floors and ceilings for a drop corridor: level at fa, a 2-cell shaft (floor fb, ceiling
    fa + h) on a straight stretch, then level at fb. Returns (floors, ceils) or None."""
    n = len(path)
    cands = [i for i in range(r + 2, n - r - 3) if _straight(path, i, r + 1)]
    if not cands:
        return None
    t = min(cands, key=lambda i: abs(i - n // 2))
    floors = [fa if i < t else fb for i in range(n)]
    ceils = [fa + h if i < t + 2 else fb + h for i in range(n)]
    return floors, ceils


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
        space_for(room.kind).build(level, room, cfg)

    build_halls(level, g, cfg)
    choose_loops(g, rooms, cfg, rng)
    router = Router(rooms, width, height, cfg)
    # route tree edges first (they must succeed), then loops (optional)
    edges = sorted(g.edges, key=lambda e: (not e.tree, e.id))
    for e in edges:
        if e.merged:
            continue                          # one hall: the rooms touch, no corridor
        a, b = rooms[e.a], rooms[e.b]
        drop = False
        if e.connector == "drop":
            hi, lo = (a, b) if a.floor > b.floor else (b, a)
            if _drop_fits(hi, lo, cfg) and rng.random() < cfg.drop_chance:
                a, b, drop = hi, lo, True
        r, diag = _corridor_style(g, e, a, b, cfg, rng)
        a_port, fa = _attachment(a, b)
        b_port, fb = _attachment(b, a)
        path = floors = None
        ceils = None
        # preferred style first, then fall back to a plain narrow corridor
        for (rr, dd) in dict.fromkeys([(r, diag), (r, False), (1, False)]):
            h = corridor_height(cfg, a, b, rr)
            env = (min(fa, fb), max(fa, fb) + h)
            # a level loop may cross other corridors at its own height: junctions instead of detours
            cross = fa if (not e.tree and fa == fb and not drop) else None
            try:
                cand = router.route(a, b, rr, dd, diag_half_width(rr), env, a_port, b_port, cross)
            except LayoutError:
                continue
            if not e.tree:
                (ax, ay), (bx, by) = a.center(), b.center()
                if len(cand) > 1.5 * (abs(ax - bx) + abs(ay - by)) + 12:
                    continue
            while len(cand) > 1 and a.contains(*cand[1]):
                cand.pop(0)
            inner = cand[1:-1]
            if drop:
                plan = plan_drop(inner, fa, fb, rr, h)
                fl, cl = plan if plan else (None, None)
            else:
                fl = plan_floors(inner, fa, fb, cfg, rr)
                cl = [f + h for f in fl] if fl else None
            if fl is not None:
                path, floors, ceils, r, diag, k = inner, fl, cl, rr, dd, diag_half_width(rr)
                break
        if path is None:
            if e.tree:
                raise LayoutError(f"no usable corridor {a.id}->{b.id}")
            level.notes.append(f"loop {e.a}-{e.b} dropped")
            continue
        diag = diag and any(d[0] and d[1] for d in (_dir(path, i) for i in range(len(path) - 1)))
        h = corridor_height(cfg, a, b, r)
        corr = carve_corridor(level, router, e.id, a, b, path, floors, ceils, r, k, h, diag,
                              e.kind, e.key, "drop" if drop else None, env)
        if cfg.corridor_beats and e.kind != "secret":
            add_beats(level, router, corr, rng)
    if cfg.storeys > 1 and cfg.galleries:
        from .openings import add_galleries
        add_galleries(level, router, rng)
    if cfg.underground and cfg.storeys > 1:
        from .openings import add_overlook
        add_overlook(level, router, rng)
    return level


def carve_corridor(level: Level, router: "Router", edge_id: int, a: Room, b: Room, path, floors, ceils,
                   r: int, k: int, h: int, diag: bool, kind: str = "open", key=None, connector=None,
                   env=None) -> Corridor:
    """Carve a routed corridor into the level and register it."""
    cid = len(level.corridors)
    corr = Corridor(cid, edge_id, a.id, b.id, path, floors, kind, key, radius=r, height=h, diagonal=diag,
                    connector=connector)
    # each carved cell takes the floor of its nearest centre-line cell
    claim: Dict[Cell, Tuple[int, int, int, int]] = {}
    for i, (c0, f, top) in enumerate(zip(path, floors, ceils)):
        dirs = {_dir(path, i)}
        if i > 0:
            dirs.add(_dir(path, i - 1))   # union of both stamps at bends
        for d in dirs:
            for c, dist in _stamp(c0, d, r, k):
                pri = (dist, i, f, top)
                if c not in claim or pri < claim[c]:
                    claim[c] = pri
    carved = []
    for c, (_, _, f, top) in claim.items():
        if not level.free_at(c, f, top, SLAB) or a.contains(*c) or b.contains(*c):
            # the rooms themselves, something stacked too close, or a corridor we cross
            for sp in level.spans_in(c, f, top):
                o = level.owner_of(c, sp) or ""
                if o.startswith("corridor:") and sp[0] == f and int(o[9:]) not in corr.junctions:
                    corr.junctions.append(int(o[9:]))
            continue
        level.add_span(c, (f, top), f"corridor:{cid}")
        carved.append(c)
    # a junction opening against another corridor's 45-degree half cell: make that cell fully open
    from .shapes import SOLID_SIDES
    for (x, y) in carved:
        for dx, dy in DIRS:
            n = (x + dx, y + dy)
            if n in level.diag and (-dx, -dy) in SOLID_SIDES[level.diag[n]] and \
                    (level.owner_of(n, level.spans[n][0]) or "").startswith("corridor:"):
                del level.diag[n]
                level.diag_span.pop(n, None)
    carved += add_half_cells(level, cid, carved)
    env = env or (min(floors), max(ceils))
    for c in carved:
        for (lo_, hi_) in level.spans_in(c, *env):
            router.add_open(c, lo_, hi_)
    level.corridors.append(corr)
    return corr


def cleanup_diagonals(level: Level):
    """A 45-degree half cell must be alone in its column; where something got stacked over or
    under one, its open triangle is filled in (always safe: it only removes open space)."""
    for c in list(level.diag):
        if len(level.spans.get(c, [])) > 1:
            sp = level.diag_span.get(c)
            if sp in level.spans[c]:
                level.spans[c].remove(sp)
            del level.diag[c]
