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


def _homed(plots: List[Plot], need: List[Tuple[int, int]]) -> int:
    """How many required (w, h) get a plot of their own at least that big (greedy, biggest first)."""
    free = sorted(plots, key=lambda p: p.area)
    n = 0
    for w, h in sorted(need, key=lambda wh: -wh[0] * wh[1]):
        for i, p in enumerate(free):
            if p.w >= w and p.h >= h:
                free.pop(i)
                n += 1
                break
    return n


def _holds(plots: List[Plot], need: List[Tuple[int, int]]) -> bool:
    return _homed(plots, need) == len(need)


def split_site(W: int, H: int, n: int, rng: random.Random, need: List[Tuple[int, int]] = ()) -> List[Plot]:
    """Split the site into ``n`` plots (the largest first), never so far that the fixed-size
    rooms (stair cores, lift shafts, mega-halls: ``need``) lose the plots they fit in."""
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
            trial = plots[:i] + [a, b] + plots[i + 1:]
            if need and _homed(trial, need) < _homed(plots, need):
                continue                            # keep this plot whole for a fixed-size room
            plots = trial
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


def embed(g, root: int, plots: List[Plot], adj, target: Dict[int, float], slots, fits, rng: random.Random,
          deadline: List[int]) -> Optional[Dict[int, int]]:
    """Node -> plot for the mission's spanning tree, neighbours on neighbouring plots (depth
    first, with backtracking; ``deadline`` bounds the search). A plot holds several rooms as long
    as their vertical slots don't overlap (storeys stacked, a stair core through them all)."""
    from .layout import vertical_conflict
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
    occ: Dict[int, List[Tuple[int, int]]] = {p.id: [] for p in plots}

    def free_for(node, pid):
        return fits(node, by_id[pid]) and not any(vertical_conflict(slots[node], s) for s in occ[pid])

    def free_neighbours(pid):
        return sum(1 for q in adj[pid] if not occ[q])

    def place_children(node) -> bool:
        return place_kid(node, children[node], 0)

    def place_kid(node, kids, i) -> bool:
        if i == len(kids):
            snap_a, snap_o = dict(assign), {k: list(v) for k, v in occ.items()}
            for k in kids:                           # then each child's own subtree
                if not place_children(k):
                    assign.clear()
                    assign.update(snap_a)
                    occ.clear()
                    occ.update(snap_o)
                    return False
            return True
        deadline[0] -= 1
        if deadline[0] < 0:
            return False
        kid = kids[i]
        opts = [q for q in adj[assign[node]] if free_for(kid, q)]
        need = len(children[kid])
        opts.sort(key=lambda q: (free_neighbours(q) < need, fit(kid, by_id[q]) + rng.random() * 0.3))
        for q in opts[:4]:
            assign[kid] = q
            occ[q].append(slots[kid])
            if place_kid(node, kids, i + 1):
                return True
            occ[q].remove(slots[kid])
            assign.pop(kid, None)
        return False

    starts = sorted((p for p in plots if fits(root, p)), key=lambda p: fit(root, p) + rng.random() * 0.5)[:6]
    for p in starts:
        assign.clear()
        for v in occ.values():
            v.clear()
        assign[root] = p.id
        occ[p.id].append(slots[root])
        if place_children(root):
            return dict(assign)
    return None


FIXED = ("megaroom",)      # (and the connectors): their size is their geometry, centred in a plot


def place_packed(g, cfg, rng: random.Random, make, LayoutError):
    """Rooms for every node on a divided floor plan (see the module docstring). Several storeys
    share one plan: a plot holds a room per storey (or a tall room, or a stair core through all)."""
    from .graph import CONNECTOR_KINDS
    root = g.center if (cfg.composition in ("hub", "central") and g.center is not None) else 0
    step = cfg.step_height
    tree = [e for e in g.edges if e.tree]
    nbrs: Dict[int, List[Tuple[int, object]]] = {n.id: [] for n in g.nodes}
    for e in tree:
        nbrs[e.a].append((e.b, e))
        nbrs[e.b].append((e.a, e))
    multi = cfg.storeys > 1
    floor_of: Dict[int, int] = {}
    if multi:
        # a storey's rooms share its floor (level doorways); connectors climb between storeys
        H_ = cfg.storey_height
        for n in g.nodes:
            if n.kind not in CONNECTOR_KINDS:
                floor_of[n.id] = n.storey * H_
        for n in g.nodes:
            if n.kind in CONNECTOR_KINDS:
                fl = [floor_of[m] for m, _ in nbrs[n.id] if m in floor_of]
                floor_of[n.id], n.top = min(fl), max(fl)
    else:
        # neighbours differ by at most one step (the doorway passage holds it)
        floor_of[root] = 0
        order = [root]
        while order:
            a = order.pop(0)
            for b, e in nbrs[a]:
                if b in floor_of:
                    continue
                f = floor_of[a] + rng.choice([-1, 0, 0, 1]) * step
                if e.kind != "open":
                    f = floor_of[a]                      # a locked or secret door needs a level doorway
                floor_of[b] = max(0, min(f, cfg.max_level * cfg.level_step))
                order.append(b)
    protos = {}
    for n in g.nodes:
        r = make(n, 0, 0, floor_of[n.id], 0)
        if r is None:
            raise LayoutError(f"packed: connector {n.kind} can't climb {n.top - floor_of[n.id]}")
        protos[n.id] = r
    fixed = {i for i, r in protos.items() if r.is_connector or r.kind in FIXED}
    slots = {i: r.slot for i, r in protos.items()}
    walls = WALL_LO + WALL_HI
    target = {i: float((r.w + walls) * (r.h + walls)) for i, r in protos.items()}

    def fits(node, p):
        r = protos[node]
        inner_w, inner_h = p.w - walls, p.h - walls
        if node in fixed:
            return r.w <= inner_w and r.h <= inner_h
        return inner_w >= 6 and inner_h >= 6

    # the site holds the busiest storey band (connectors count on every storey they cross)
    bands: Dict[int, List[int]] = {}
    for i, r in protos.items():
        lo = r.floor // max(1, cfg.storey_height) if multi else 0
        hi = (r.ceil - 1) // max(1, cfg.storey_height) if multi and (r.is_connector or r.kind in FIXED) else lo
        for s in range(lo, hi + 1):
            bands.setdefault(s, []).append(i)
    total = max(sum(target[i] for i in ids) for ids in bands.values())
    count = max(len(ids) for ids in bands.values())
    for attempt in range(40):
        spare = 1.3 + 0.05 * attempt
        aspect = rng.uniform(1.0, 1.6)
        W = int(math.sqrt(total * spare * aspect)) + 1
        H = int(total * spare / W) + 1
        need = [(protos[i].w + walls, protos[i].h + walls) for i in fixed]
        plots = split_site(W, H, int(count * spare) + 2, rng, need)
        if not _holds(plots, need):
            continue
        adj = adjacency(plots, walls + 6)
        assign = embed(g, root, plots, adj, target, slots, fits, rng, [4000])
        if assign is None:
            continue
        by_id = {p.id: p for p in plots}
        rooms: Dict[int, object] = {}
        for node in g.nodes:
            p = by_id[assign[node.id]]
            room = protos[node.id]
            if node.id in fixed:                         # its own size, centred in the plot
                room.x = p.x + WALL_LO + (p.w - walls - room.w) // 2
                room.y = p.y + WALL_LO + (p.h - walls - room.h) // 2
            else:
                room.x, room.y = p.x + WALL_LO, p.y + WALL_LO
                room.w, room.h = p.w - walls, p.h - walls
            rooms[node.id] = room
        return rooms
    raise LayoutError("packed: the mission did not fit a floor plan")
