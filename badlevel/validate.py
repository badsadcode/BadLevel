"""Level validation: is the generated level actually completable?

Movement is *directed*: a player can step up at most ``max_step`` units, but
can drop down up to ``max_drop`` (a one-way move). Lifts and ladders are
explicit links (``level.links``). Every move also needs headroom.

Checks:
  * bounds    - nothing open touches the grid border (sealed world)
  * spans     - enough headroom, no overlapping spans in a column
  * entities  - none inside solid or inside a 45-degree half cell
  * gating    - keys are collected in lock order; the exit stays out of reach
                until every locked door is open
  * softlock  - from every spot the player can reach, the next goal (the next
                key, finally the exit) is still reachable: no one-way drop or
                door order can strand the player
  * coverage  - every room can be reached
"""
from __future__ import annotations

from typing import Dict, Iterable, List, Set, Tuple

from .level import Cell, Level

Node = Tuple[int, int, int]  # x, y, span index
DIRS = ((1, 0), (-1, 0), (0, 1), (0, -1))


def _door_cells(level: Level, ent) -> Set[Cell]:
    (x0, y0, _), (x1, y1, _) = ent.bounds
    s = level.cell
    return {(x, y) for x in range(int(x0 // s), int(x1 // s)) for y in range(int(y0 // s), int(y1 // s))}


def _cell_of(level: Level, pos) -> Cell:
    return (int(pos[0] // level.cell), int(pos[1] // level.cell))


def _node_at(level: Level, pos) -> Node:
    c = _cell_of(level, pos)
    for i, (f, ceil) in enumerate(level.spans[c]):
        if f <= pos[2] < ceil:
            return (c[0], c[1], i)
    raise ValueError(f"position {pos} is not inside open space")


def _link_edges(level: Level) -> Dict[Node, List[Node]]:
    out: Dict[Node, List[Node]] = {}
    for link in level.links:
        try:
            a, b = _node_at(level, link["a"]), _node_at(level, link["b"])
        except (KeyError, ValueError):
            continue
        out.setdefault(a, []).append(b)
        if link.get("two_way", True):
            out.setdefault(b, []).append(a)
    return out


def _can_move(level: Level, s0, s1) -> bool:
    cfg = level.config
    f0, c0 = s0
    f1, c1 = s1
    if f1 - f0 > cfg.max_step:              # too high to step up
        return False
    if f0 - f1 > getattr(cfg, "max_drop", cfg.max_step):   # too far to fall
        return False
    return min(c0, c1) - max(f0, f1) >= cfg.player_height


def successors(level: Level, node: Node, blocked: Set[Cell], links=None) -> Iterable[Node]:
    x, y, i = node
    here = level.spans[(x, y)][i]
    for dx, dy in DIRS:
        n = (x + dx, y + dy)
        if n in blocked or n not in level.spans or n in level.diag or n in level.glass:
            continue
        for j, sp in enumerate(level.spans[n]):
            if _can_move(level, here, sp):
                yield (n[0], n[1], j)
    if links:
        for m in links.get(node, ()):
            if (m[0], m[1]) not in blocked:
                yield m


def flood(level: Level, start: Node, blocked: Set[Cell], links=None) -> Set[Node]:
    """Every node reachable from ``start`` (directed: drops are one-way)."""
    if links is None:
        links = _link_edges(level)
    seen = {start}
    stack = [start]
    while stack:
        node = stack.pop()
        for m in successors(level, node, blocked, links):
            if m not in seen:
                seen.add(m)
                stack.append(m)
    return seen


def can_reach(level: Level, targets: Set[Node], blocked: Set[Cell], links=None) -> Set[Node]:
    """Every node from which some target can be reached (reverse search)."""
    if links is None:
        links = _link_edges(level)
    rev: Dict[Node, List[Node]] = {}
    for a, bs in links.items():
        for b in bs:
            rev.setdefault(b, []).append(a)
    seen = set(targets)
    stack = list(targets)
    while stack:
        node = stack.pop()
        x, y, i = node
        here = level.spans[(x, y)][i]
        for dx, dy in DIRS:
            p = (x + dx, y + dy)
            if p in blocked or p not in level.spans or p in level.diag or p in level.glass:
                continue
            for j, sp in enumerate(level.spans[p]):
                if (p[0], p[1], j) not in seen and _can_move(level, sp, here):
                    seen.add((p[0], p[1], j))
                    stack.append((p[0], p[1], j))
        for p in rev.get(node, ()):
            if p not in seen and (p[0], p[1]) not in blocked:
                seen.add(p)
                stack.append(p)
    return seen


def validate(level: Level) -> Dict[str, object]:
    errors: List[str] = []
    for (x, y) in level.spans:
        if x <= 0 or y <= 0 or x >= level.width - 1 or y >= level.height - 1:
            errors.append(f"open cell {x},{y} touches the border")
            break
    for c, spans in level.spans.items():
        for (f, ceil) in spans:
            if ceil - f < level.config.player_height and not (level.owner_at(c, f) or "").startswith("window:"):
                errors.append(f"span at {c} too low ({ceil - f})")
        for a, b in zip(spans, spans[1:]):
            if b[0] <= a[1]:
                errors.append(f"overlapping spans at {c}")

    by_kind: Dict[str, list] = {}
    for e in level.entities:
        by_kind.setdefault(e.kind, []).append(e)
    starts = by_kind.get("player_start", [])
    if len(starts) != 1:
        errors.append(f"expected 1 player_start, got {len(starts)}")
        return {"ok": False, "errors": errors}
    for e in level.entities:
        if e.bounds is None:
            try:
                _node_at(level, e.pos)
                if _cell_of(level, e.pos) in level.diag:
                    raise ValueError("diagonal cell")
            except (KeyError, ValueError):
                errors.append(f"{e.kind} at {e.pos} is inside solid")

    links = _link_edges(level)
    if len(links) < sum(1 for _ in level.links):
        errors.append("a lift or ladder link does not touch open space")
    start = _node_at(level, starts[0].pos)
    doors = by_kind.get("door", [])
    door_cells = {d.props["key"]: _door_cells(level, d) for d in doors}
    keys = {k.props["color"]: _node_at(level, k.pos) for k in by_kind.get("key", [])}
    exits = by_kind.get("exit", [])
    exit_node = _node_at(level, exits[0].pos) if exits else None
    if not exits:
        errors.append("no exit")
        return {"ok": False, "errors": errors}

    # progression: keys in lock order, then the exit; at every stage nothing reachable may be a dead end
    locked_order = [e.key for e in level.graph.edges if e.kind == "locked"]
    have: Set[str] = set()
    stages = [(color, keys.get(color)) for color in locked_order] + [("exit", exit_node)]
    softlocks = 0
    reach = set()
    for name, goal in stages:
        blocked = set().union(*[cells for k, cells in door_cells.items() if k not in have]) \
            if len(door_cells) > len(have) else set()
        reach = flood(level, start, blocked, links)
        if goal is None:
            errors.append(f"missing {name} key")
            break
        if goal not in reach:
            errors.append(f"{name} unreachable" + ("" if name == "exit" else " before its door"))
            break
        if name != "exit" and exit_node in reach:
            errors.append(f"exit reachable without the {name} key")
        back = can_reach(level, {goal}, blocked, links)
        stuck = reach - back
        if stuck:
            softlocks += len(stuck)
            x, y, i = next(iter(sorted(stuck)))
            errors.append(f"softlock: {len(stuck)} reachable spot(s) can't get to the {name} (e.g. {x},{y})")
        if name != "exit":
            have.add(name)

    unreached = []
    for room in level.rooms:
        hit = False
        for c in room.cells():
            for i, sp in enumerate(level.spans.get(c, ())):
                if (c[0], c[1], i) in reach and level.owner_of(c, sp) == f"room:{room.id}":
                    hit = True
                    break
            if hit:
                break
        if not hit:
            unreached.append(room.id)
    if unreached:
        errors.append(f"rooms unreachable: {unreached}")
    open_nodes = sum(len(s) for c, s in level.spans.items() if c not in level.diag)
    return {
        "ok": not errors,
        "errors": errors,
        "reachable_fraction": round(len(reach) / max(1, open_nodes), 3),
        "softlock_spots": softlocks,
        "one_way_drops": sum(1 for c in level.corridors if getattr(c, "connector", None) == "drop"),
        "links": [{k: v for k, v in l.items() if k in ("kind", "two_way", "interaction", "accessible", "room")}
                  for l in level.links],
    }
