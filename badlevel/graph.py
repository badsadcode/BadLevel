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

KEY_COLORS = ["red", "blue"]


@dataclass
class Node:
    id: int
    kind: str           # start | combat | arena | hub | key | supply | secret | exit
    size: str           # small | medium | large
    zone: int = 0       # number of locks passed to get here
    depth: int = 0      # distance along the critical path (difficulty ramp)
    key: Optional[str] = None


@dataclass
class Edge:
    id: int
    a: int
    b: int
    kind: str = "open"   # open | locked | secret
    key: Optional[str] = None
    tree: bool = True    # False for loop edges


@dataclass
class MissionGraph:
    nodes: List[Node] = field(default_factory=list)
    edges: List[Edge] = field(default_factory=list)

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
            size = rng.choice(["medium", "medium", "large"]) if kind == "hub" else \
                rng.choice(["small", "medium", "medium"])
            main.append(g.add_node(kind, size, depth=i))
    main.append(g.add_node("exit", "medium", depth=n_main - 1))
    for a, b in zip(main, main[1:]):
        g.add_edge(a.id, b.id)

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
        prev = parent
        if rng.random() < 0.6:
            mid = g.add_node(rng.choice(["combat", "supply"]), rng.choice(["small", "medium"]),
                             zone=parent.zone, depth=parent.depth)
            g.add_edge(prev.id, mid.id)
            prev = mid
        key_node = g.add_node("key", "medium", zone=parent.zone, depth=parent.depth, key=color)
        g.add_edge(prev.id, key_node.id)

    # --- side rooms ---------------------------------------------------
    for _ in range(cfg.side_rooms):
        parents = [n for n in g.nodes if n.kind not in ("exit", "key", "secret")]
        parent = rng.choice(parents)
        n = g.add_node(rng.choice(["supply", "combat"]), rng.choice(["small", "medium"]),
                       zone=parent.zone, depth=parent.depth)
        g.add_edge(parent.id, n.id)

    # --- loops ----------------------------------------------------------
    for _ in range(cfg.loops):
        pairs = []
        for a in g.nodes:
            for b in g.nodes:
                if a.id < b.id and a.zone == b.zone and not g.connected(a.id, b.id) \
                        and "secret" not in (a.kind, b.kind) and "exit" not in (a.kind, b.kind):
                    d = g.hop_distance(a.id, b.id)
                    if d >= 3:
                        pairs.append((d, a.id, b.id))
        if not pairs:
            break
        pairs.sort(reverse=True)
        _, a, b = rng.choice(pairs[: max(3, len(pairs) // 3)])
        g.add_edge(a, b, tree=False)

    # --- secret -----------------------------------------------------------
    if cfg.secret:
        parents = [n for n in g.nodes if n.kind not in ("start", "exit", "secret")]
        parent = rng.choice(parents)
        s = g.add_node("secret", "small", zone=parent.zone, depth=parent.depth)
        g.add_edge(parent.id, s.id, kind="secret")

    return g
