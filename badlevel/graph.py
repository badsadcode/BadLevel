"""Mission graph: the abstract structure of a level.

Nodes are rooms with a gameplay role, edges are connections. The graph is
built so that pacing and gating make sense *before* any geometry exists:

* a critical path ``start -> ... -> exit``
* key branches: the key for a locked door sits on a side branch that is
  reachable before that door
* side rooms for supplies / optional fights
* loop edges, only inside the same lock zone, so they never bypass a lock
* an optional secret room behind a secret door
"""
from __future__ import annotations

import random
from dataclasses import dataclass, field
from typing import List, Optional

from .config import GenConfig
from .weights import weighted

KEY_COLORS = ["red", "blue"]


@dataclass
class Node:
    id: int
    kind: str           # start | combat | arena | hub | key | supply | secret | exit
    size: str           # small | medium | large
    zone: int = 0       # number of locks passed to get here
    depth: int = 0      # distance along the critical path (difficulty ramp)
    key: Optional[str] = None
    storey: int = 0     # for connectors: the lower storey they start from


@dataclass
class Edge:
    id: int
    a: int
    b: int
    kind: str = "open"   # open | locked | secret
    key: Optional[str] = None
    tree: bool = True    # False for loop edges
    connector: Optional[str] = None  # stairs / drop for edges that change storey
    merged: bool = False             # rooms placed touching: one hall, no corridor


@dataclass
class MissionGraph:
    nodes: List[Node] = field(default_factory=list)
    edges: List[Edge] = field(default_factory=list)
    center: Optional[int] = None     # hub / central compositions: the node everything gathers around

    def add_node(self, kind, size, **kw) -> Node:
        n = Node(len(self.nodes), kind, size, **kw)
        self.nodes.append(n)
        return n

    def add_edge(self, a, b, **kw) -> Edge:
        e = Edge(len(self.edges), a, b, **kw)
        self.edges.append(e)
        return e

    def neighbors(self, nid: int):
        for e in self.edges:
            if e.a == nid:
                yield e.b
            elif e.b == nid:
                yield e.a

    def connected(self, a: int, b: int) -> bool:
        return any({e.a, e.b} == {a, b} for e in self.edges)

    def hop_distance(self, a: int, b: int) -> int:
        seen = {a: 0}
        frontier = [a]
        while frontier:
            nxt = []
            for n in frontier:
                for m in self.neighbors(n):
                    if m not in seen:
                        seen[m] = seen[n] + 1
                        nxt.append(m)
            frontier = nxt
        return seen.get(b, 10**9)

    def to_dict(self) -> dict:
        return {
            "nodes": [vars(n) for n in self.nodes],
            "edges": [vars(e) for e in self.edges],
        }


def build_graph(cfg: GenConfig, rng: random.Random) -> MissionGraph:
    g = MissionGraph()
    n_main = max(3, cfg.main_rooms)
    n_keys = max(0, min(cfg.keys, len(KEY_COLORS), n_main - 2))

    # --- critical path -------------------------------------------------
    main = [g.add_node("start", "medium", depth=0)]
    arena_at = max(1, round((n_main - 1) * 0.66))
    for i in range(1, n_main - 1):
        if i == arena_at:
            main.append(g.add_node("arena", "large", depth=i))
        else:
            kind = rng.choice(["combat", "combat", "hub"])
            size = weighted(rng, ["medium", "medium", "large"] if kind == "hub" else ["small", "medium", "medium"],
                            cfg.size_bias)
            main.append(g.add_node(kind, size, depth=i))
    main.append(g.add_node("exit", "medium", depth=n_main - 1))
    for a, b in zip(main, main[1:]):
        g.add_edge(a.id, b.id)
    for n in main:
        n.main = True
    radial = cfg.composition in ("hub", "central")
    if radial:
        g.center = main[arena_at].id     # the arena: mega-rooms and yards pick it first

    # --- locks and key branches ---------------------------------------
    # Lock edges are spread along the path; edge index i joins main[i] -> main[i+1].
    lock_edges = []
    if n_keys:
        lo = 1
        span = (n_main - 1 - lo)
        for k in range(n_keys):
            idx = lo + (k + 1) * span // (n_keys + 1)
            idx = max(idx, (lock_edges[-1] + 1) if lock_edges else 1)
            idx = min(idx, n_main - 2)
            if lock_edges and idx <= lock_edges[-1]:
                break
            lock_edges.append(idx)
    for k, idx in enumerate(lock_edges):
        color = KEY_COLORS[k]
        e = g.edges[idx]  # main path edges were created first, in order
        e.kind, e.key = "locked", color

    # zones along the main path
    zone = 0
    for i, n in enumerate(main):
        n.zone = zone
        if zone < len(lock_edges) and i == lock_edges[zone]:
            zone += 1

    for k, idx in enumerate(lock_edges):
        color = KEY_COLORS[k]
        # branch from a main room in the same zone, before the lock
        first = lock_edges[k - 1] + 1 if k else 0
        candidates = main[first: idx + 1]
        parent = rng.choice(candidates[1:] or candidates)
        if radial and g.nodes[g.center] in candidates and rng.random() < 0.8:
            parent = g.nodes[g.center]       # key branches hang off the centre
        prev = parent
        if rng.random() < 0.6:
            mid = g.add_node(rng.choice(["combat", "supply"]), weighted(rng, ["small", "medium"], cfg.size_bias),
                             zone=parent.zone, depth=parent.depth)
            g.add_edge(prev.id, mid.id)
            prev = mid
        key_node = g.add_node("key", "medium", zone=parent.zone, depth=parent.depth, key=color)
        g.add_edge(prev.id, key_node.id)

    # --- side rooms ---------------------------------------------------
    for _ in range(cfg.side_rooms):
        parents = [n for n in g.nodes if n.kind not in ("exit", "key", "secret")]
        parent = rng.choice(parents)
        if radial and rng.random() < 0.75:
            parent = g.nodes[g.center]       # spokes
        elif cfg.composition == "linear":
            parent = rng.choice([n for n in parents if getattr(n, "main", False)])   # short ribs off the spine
        n = g.add_node(rng.choice(["supply", "combat"]), weighted(rng, ["small", "medium"], cfg.size_bias),
                       zone=parent.zone, depth=parent.depth)
        g.add_edge(parent.id, n.id)

    # --- loops: chosen in the layout, once we know which rooms are close (see choose_loops)

    # --- secret -----------------------------------------------------------
    if cfg.secret:
        parents = [n for n in g.nodes if n.kind not in ("start", "exit", "secret")]
        parent = rng.choice(parents)
        s = g.add_node("secret", "small", zone=parent.zone, depth=parent.depth)
        g.add_edge(parent.id, s.id, kind="secret")

    if cfg.mega_rooms > 0:
        make_mega_rooms(g, cfg, rng)
    if cfg.open_areas > 0:
        make_open_areas(g, cfg, rng)
    if cfg.storeys > 1:
        assign_storeys(g, cfg, rng, [n.id for n in main])
    return g


CONNECTOR_KINDS = ("stairwell", "lift", "ladder")


def make_mega_rooms(g: MissionGraph, cfg: GenConfig, rng: random.Random):
    """Turn the arena (then hubs) into mega-rooms, each with a character (hangar, works, terraces)."""
    cands = [n for n in g.nodes if n.kind in ("arena", "hub")]
    cands.sort(key=lambda n: (n.kind != "arena", -sum(1 for _ in g.neighbors(n.id)), rng.random()))
    styles = ["hangar", "works", "terraces"]
    for n in cands[:cfg.mega_rooms]:
        n.kind, n.size = "megaroom", "mega"
        n.style = weighted(rng, styles, cfg.mega_styles)


def make_open_areas(g: MissionGraph, cfg: GenConfig, rng: random.Random):
    """Turn a few well-connected rooms into courtyards (open to the sky). Hubs and the arena
    first: places where routes meet make good yards; start, exit, keys and secrets stay rooms."""
    cands = [n for n in g.nodes if n.kind in ("hub", "arena", "combat")]
    degree = {n.id: sum(1 for _ in g.neighbors(n.id)) for n in cands}
    rank = {"hub": 0, "arena": 1, "combat": 2}
    cands.sort(key=lambda n: (rank[n.kind], -degree[n.id], rng.random()))
    for n in cands[:cfg.open_areas]:
        n.kind, n.size = "courtyard", "yard"


def assign_storeys(g: MissionGraph, cfg: GenConfig, rng: random.Random, main: List[int]):
    """Give every room a storey, then turn tree edges that change storey into vertical
    connections: a stair corridor, or a connector room (stairwell / lift / ladder)."""
    S = cfg.storeys
    best = None
    for _ in range(24):
        st = {main[0]: rng.randrange(S)}
        for a, b in zip(main, main[1:]):
            s = st[a]
            if rng.random() < cfg.storey_change:
                s += rng.choice([-1, 1]) if 0 < s < S - 1 else (1 if s == 0 else -1)
            st[b] = s
        # branches: mostly the parent's storey, sometimes one up or down (keys in the basement...)
        order = [e for e in g.edges if e.tree]
        changed = True
        while changed:
            changed = False
            for e in order:
                for p, c in ((e.a, e.b), (e.b, e.a)):
                    if p in st and c not in st:
                        s = st[p]
                        node = g.nodes[c]
                        if node.kind != "secret" and rng.random() < 0.35:
                            s = max(0, min(S - 1, s + rng.choice([-1, 1])))
                        st[c] = s
                        changed = True
        used = len(set(st.values()))
        if best is None or used > best[0]:
            best = (used, st)
        if used == S:
            break
    st = best[1]
    for n in g.nodes:
        n.storey = st[n.id]

    weights = cfg.connector_weights
    kinds = list(weights)
    for e in list(g.edges):
        sa, sb = g.nodes[e.a].storey, g.nodes[e.b].storey
        if sa == sb:
            continue
        if not e.tree:
            e.connector = "drop"          # decided for real in the layout (drop if it fits)
            continue
        kind = rng.choices(kinds, [weights[k] for k in kinds])[0]
        if kind == "stairs" or e.kind == "secret":
            e.connector = "stairs"
            continue
        lo_node = g.nodes[e.a] if sa < sb else g.nodes[e.b]
        c = g.add_node(kind, "connector", zone=lo_node.zone, depth=min(g.nodes[e.a].depth, g.nodes[e.b].depth),
                       storey=min(sa, sb))
        old_b = e.b
        e.b = c.id                        # a -> connector keeps the lock, if any
        g.add_edge(c.id, old_b)
