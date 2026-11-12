"""Level validation: is the generated level actually playable?

Checks:
  * bounds  - nothing open touches the grid border (sealed world)
  * walk    - a player can walk from the start to every room: steps of at
              most ``max_step`` up *or* down (no one-way drops), and enough
              headroom on every move
  * gating  - with locked doors closed, each key is reachable in order,
              and the exit is NOT reachable until the doors open
"""
from __future__ import annotations

from typing import Dict, List, Set, Tuple

from .level import Cell, Level

Node = Tuple[int, int, int]  # x, y, span index


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


def flood(level: Level, start: Node, blocked: Set[Cell]) -> Set[Node]:
    cfg = level.config
    step, head = cfg.max_step, cfg.player_height
    seen = {start}
    stack = [start]
    while stack:
        x, y, i = stack.pop()
        f0, c0 = level.spans[(x, y)][i]
        for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            n = (x + dx, y + dy)
            if n in blocked or n not in level.spans or n in level.diag:
                continue
            for j, (f1, c1) in enumerate(level.spans[n]):
                if abs(f1 - f0) > step:
                    continue
                if min(c0, c1) - max(f0, f1) < head:
                    continue
                node = (n[0], n[1], j)
                if node not in seen:
                    seen.add(node)
                    stack.append(node)
    return seen


def validate(level: Level) -> Dict[str, object]:
    errors: List[str] = []
    for (x, y) in level.spans:
        if x <= 0 or y <= 0 or x >= level.width - 1 or y >= level.height - 1:
            errors.append(f"open cell {x},{y} touches the border")
            break
    for c, spans in level.spans.items():
        for (f, ceil) in spans:
            if ceil - f < level.config.player_height:
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

    start = _node_at(level, starts[0].pos)
    doors = by_kind.get("door", [])
    door_cells = {d.props["key"]: _door_cells(level, d) for d in doors}
    keys = {k.props["color"]: _node_at(level, k.pos) for k in by_kind.get("key", [])}
    exits = by_kind.get("exit", [])
    exit_node = _node_at(level, exits[0].pos) if exits else None
    if not exits:
        errors.append("no exit")

    # gating: collect keys in lock order (the graph's locked edges are in path order)
    locked_order = [e.key for e in level.graph.edges if e.kind == "locked"]
    have: Set[str] = set()
    for color in locked_order:
        blocked = set().union(*[cells for k, cells in door_cells.items() if k not in have]) \
            if len(door_cells) > len(have) else set()
        reach = flood(level, start, blocked)
        if color not in keys:
            errors.append(f"missing {color} key")
            continue
        if keys[color] not in reach:
            errors.append(f"{color} key unreachable before its door")
        if exit_node and exit_node in reach:
            errors.append(f"exit reachable without the {color} key")
        have.add(color)

    reach = flood(level, start, set())
    if exit_node and exit_node not in reach:
        errors.append("exit unreachable")
    unreached = []
    for room in level.rooms:
        if not any((c[0], c[1], i) in reach for c in room.cells() if c in level.spans
                   for i in range(len(level.spans[c]))):
            unreached.append(room.id)
    if unreached:
        errors.append(f"rooms unreachable: {unreached}")
    open_nodes = sum(len(s) for c, s in level.spans.items() if c not in level.diag)
    return {
        "ok": not errors,
        "errors": errors,
        "reachable_fraction": round(len(reach) / max(1, open_nodes), 3),
    }
