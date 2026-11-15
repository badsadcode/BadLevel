"""Packed layouts: a floor plan divided into rooms (Doom-era maps, dense dungeons).

The normal layout grows a level room by room, each placed beside its parent with a corridor
between, so levels come out as islands on strings: 20-38% of the bounding box is floor and a
third or more of that is corridor. Doom-era maps are the other way round: a block of space
divided into rooms that share their walls, doors straight through, most of the block floor.

Here (``cfg.packing`` >= 0.5, single storey):

1. **A site**: a rectangle sized to hold every room of the mission (plus spare plots).
2. **Plots**: the site split again and again (the largest plot first, along its long side, at
   35-65%) until there are enough; neighbouring plots share an edge.
3. **The mission onto the plots**: the spanning tree of the mission graph is embedded
   neighbour to neighbour, depth first: the root on a plot that suits its size, each child on a
   free plot touching its parent's (the size that fits best, and with enough free neighbours of
   its own for its children). If it doesn't fit, a new floor plan.
4. **Rooms in their plots**: each room fills its plot less a wall (3 cells between
   neighbours): the corridor between two neighbours is a short doorway passage through that
   wall. Neighbours differ by at most one step (the passage holds it); larger
   height changes are the rooms' own (platforms, pits, tall rooms). Unused plots stay solid,
   which gives the plan its irregular edge and inner holes. Room shapes (chamfers, octagons,
   hexes) then cut the corners inside the plots.
"""
from __future__ import annotations

import math
import random
from typing import Dict, List, Optional, Tuple

WALL_LO, WALL_HI = 1, 2   # cells of wall at a plot's low and high sides: 3 between neighbouring rooms
MIN_PLOT = 10       # a plot holds a room of at least 6 cells across


class Plot:
    __slots__ = ("x", "y", "w", "h", "id")

    def __init__(self, x, y, w, h):
        self.x, self.y, self.w, self.h, self.id = x, y, w, h, -1

    @property
    def area(self):
        return self.w * self.h


def split_site(W: int, H: int, n: int, rng: random.Random) -> List[Plot]:
    plots = [Plot(0, 0, W, H)]
    while len(plots) < n:
        plots.sort(key=lambda p: -p.area)
        for i, p in enumerate(plots):
            horiz = p.w >= p.h if abs(p.w - p.h) > 2 else rng.random() < 0.5
            length = p.w if horiz else p.h
            if length < 2 * MIN_PLOT:
                horiz = not horiz
                length = p.w if horiz else p.h
                if length < 2 * MIN_PLOT:
                    continue
            cut = int(round(length * rng.uniform(0.35, 0.65)))
            cut = max(MIN_PLOT, min(length - MIN_PLOT, cut))
            if horiz:
                a, b = Plot(p.x, p.y, cut, p.h), Plot(p.x + cut, p.y, p.w - cut, p.h)
            else:
                a, b = Plot(p.x, p.y, p.w, cut), Plot(p.x, p.y + cut, p.w, p.h - cut)
            plots[i:i + 1] = [a, b]
            break
        else:
            break                                   # nothing left to split
    for k, p in enumerate(plots):
        p.id = k
    return plots


def adjacency(plots: List[Plot], min_shared: int) -> Dict[int, List[int]]:
    """Plots sharing an edge at least ``min_shared`` long (room enough for a doorway)."""
    adj: Dict[int, List[int]] = {p.id: [] for p in plots}
    for a in plots:
        for b in plots:
            if a.id >= b.id:
                continue
            shared = 0
            if a.x + a.w == b.x or b.x + b.w == a.x:
                shared = min(a.y + a.h, b.y + b.h) - max(a.y, b.y)
            elif a.y + a.h == b.y or b.y + b.h == a.y:
                shared = min(a.x + a.w, b.x + b.w) - max(a.x, b.x)
            if shared >= min_shared:
                adj[a.id].append(b.id)
                adj[b.id].append(a.id)
    return adj


def embed(g, root: int, plots: List[Plot], adj, target: Dict[int, float], rng: random.Random,
          deadline: List[int]) -> Optional[Dict[int, int]]:
    """Node -> plot for the mission's spanning tree, neighbours on neighbouring plots (depth
    first, with backtracking; ``deadline`` bounds the search)."""
    children: Dict[int, List[int]] = {n.id: [] for n in g.nodes}
    tree = [e for e in g.edges if e.tree]
    seen = {root}
    order = [root]
    while order:
        a = order.pop(0)
        for e in tree:
            if a in (e.a, e.b):
                b = e.b if e.a == a else e.a
                if b not in seen:
                    seen.add(b)
                    children[a].append(b)
                    order.append(b)
    by_id = {p.id: p for p in plots}

    def fit(node, p):
        return abs(math.log(max(1.0, p.area) / max(1.0, target[node])))

    assign: Dict[int, int] = {}
    used = set()

    def free_neighbours(pid):
        return sum(1 for q in adj[pid] if q not in used)

    def place_children(node) -> bool:
        return place_kid(node, children[node], 0)

    def place_kid(node, kids, i) -> bool:
        if i == len(kids):
            snap_a, snap_u = dict(assign), set(used)
            for k in kids:                           # then each child's own subtree
                if not place_children(k):
                    assign.clear()
                    assign.update(snap_a)
                    used.clear()
                    used.update(snap_u)
                    return False
            return True
        deadline[0] -= 1
        if deadline[0] < 0:
            return False
        kid = kids[i]
        opts = [q for q in adj[assign[node]] if q not in used]
        need = len(children[kid])
        opts.sort(key=lambda q: (free_neighbours(q) < need, fit(kid, by_id[q]) + rng.random() * 0.3))
        for q in opts[:4]:
            assign[kid] = q
            used.add(q)
            if place_kid(node, kids, i + 1):
                return True
            used.discard(q)
            assign.pop(kid, None)
        return False

    starts = sorted(plots, key=lambda p: fit(root, p) + rng.random() * 0.5)[:6]
    for p in starts:
        assign.clear()
        used.clear()
        assign[root] = p.id
        used.add(p.id)
        if place_children(root):
            return dict(assign)
    return None


def place_packed(g, cfg, rng: random.Random, make, LayoutError):
    """Rooms for every node on a divided floor plan (see the module docstring)."""
    from .graph import CONNECTOR_KINDS
    if any(n.kind in CONNECTOR_KINDS for n in g.nodes):
        return None                                  # (single storey only: no connector rooms)
    root = g.center if (cfg.composition in ("hub", "central") and g.center is not None) else 0
    protos = {n.id: make(n, 0, 0, 0, 0) for n in g.nodes}
    target = {i: float((r.w + WALL_LO + WALL_HI) * (r.h + WALL_LO + WALL_HI)) for i, r in protos.items()}
    total = sum(target.values())
    for attempt in range(40):
        spare = 1.25 + 0.05 * attempt
        aspect = rng.uniform(1.0, 1.6)
        W = int(math.sqrt(total * spare * aspect)) + 1
        H = int(total * spare / W) + 1
        n = int(len(g.nodes) * spare) + 1
        plots = split_site(W, H, n, rng)
        adj = adjacency(plots, WALL_LO + WALL_HI + 6)
        assign = embed(g, root, plots, adj, target, rng, [4000])
        if assign is None:
            continue
        by_id = {p.id: p for p in plots}
        rooms: Dict[int, object] = {}
        # floors: neighbours differ by at most two steps (the doorway passage holds the stairs)
        step = cfg.step_height
        floor_of = {root: 0}
        order = [root]
        tree = [e for e in g.edges if e.tree]
        while order:
            a = order.pop(0)
            for e in tree:
                if a in (e.a, e.b):
                    b = e.b if e.a == a else e.a
                    if b in floor_of:
                        continue
                    f = floor_of[a] + rng.choice([-1, 0, 0, 1]) * step
                    if e.kind != "open":
                        f = floor_of[a]                  # a locked or secret door needs a level doorway
                    floor_of[b] = max(0, min(f, cfg.max_level * cfg.level_step))
                    order.append(b)
        for node in g.nodes:
            p = by_id[assign[node.id]]
            room = make(node, 0, 0, floor_of[node.id], 0)
            room.x, room.y = p.x + WALL_LO, p.y + WALL_LO
            room.w, room.h = p.w - WALL_LO - WALL_HI, p.h - WALL_LO - WALL_HI
            rooms[node.id] = room
        return rooms
    raise LayoutError("packed: the mission did not fit a floor plan")
