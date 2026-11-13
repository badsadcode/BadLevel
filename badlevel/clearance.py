"""Clearance: the player is a box, not a point.

The validator walks cell to cell (32 units), but a Quake player is a 32x32 box, and a gap
exactly 32 units wide doesn't let it through (the engine expands every wall by half the
player's width, so a 32-unit gap closes to nothing). Door jambs that narrow a 3-cell opening to
1, a column one cell from a wall, the arm of an L that is one cell wide, the end of a stair or a
deck beside a wall: each passes the point check and stops a real player.

Here the player is a 2x2-cell box (64 units, the smallest grid gap a 32-unit hull fits
through). ``box_reach`` floods where the box can stand and move (steps, drops, lifts and
ladders as in the validator; doors count as open). ``widen_pinches`` runs after generation:
wherever the point can go on but the box can't, it opens a solid cell beside the squeeze (a
column, a jamb, a wall member, a notch), but only a cell whose open neighbours all belong to
the spaces already meeting there, so it never cuts into another room, around a locked door or
out of the level. What can't be widened is reported by ``clearance_report`` and fails
validation, so the generator tries again.
"""
from __future__ import annotations

from typing import Dict, List, Optional, Set, Tuple

from .level import Cell, Level

Box = Tuple[int, int, int]          # (x, y, F): box over cells x-1..x, y-1..y, standing at F
D4 = ((1, 0), (-1, 0), (0, 1), (0, -1))
SLAB = 16


def _quad(x: int, y: int):
    return ((x - 1, y - 1), (x, y - 1), (x - 1, y), (x, y))


def _span_at(level: Level, c: Cell, F: int, H: int):
    if c in level.diag or c in level.glass:
        return None
    for sp in level.spans.get(c, ()):
        if sp[0] <= F and sp[1] >= F + H:
            return sp
    return None


def _boxes_at(level: Level, x: int, y: int, H: int) -> List[int]:
    quad = _quad(x, y)
    out = []
    for F in sorted({sp[0] for c in quad for sp in level.spans.get(c, ())}):
        if all(_span_at(level, c, F, H) for c in quad):
            out.append(F)
    return out


def box_reach(level: Level, start_pos=None) -> Tuple[Set[Box], Dict[Cell, List[int]]]:
    """Every box position reachable from the player start."""
    cfg = level.config
    H, step = cfg.player_height, cfg.max_step
    drop = getattr(cfg, "max_drop", step)
    s = level.cell
    corners = {(x + dx, y + dy) for (x, y) in level.spans for dx in (0, 1) for dy in (0, 1)}
    by: Dict[Cell, List[int]] = {}
    for (x, y) in corners:
        fs = _boxes_at(level, x, y, H)
        if fs:
            by[(x, y)] = fs
    if start_pos is None:
        start_pos = next(e for e in level.entities if e.kind == "player_start").pos
    sx, sy, sz = start_pos
    best = None
    for r in range(0, 3):
        for dx in range(-r, r + 1):
            for dy in range(-r, r + 1):
                c = (int(round(sx / s)) + dx, int(round(sy / s)) + dy)
                for F in by.get(c, ()):
                    if F <= sz <= F + H + 32 and best is None:
                        best = (c[0], c[1], F)
        if best:
            break
    if best is None:
        return set(), by
    links: Dict[Cell, List[tuple]] = {}
    for L in level.links:
        if "a" not in L or "b" not in L:
            continue
        a, b = L["a"], L["b"]
        links.setdefault((int(a[0] // s), int(a[1] // s)), []).append(b)
        if L.get("two_way", True):
            links.setdefault((int(b[0] // s), int(b[1] // s)), []).append(a)
    seen = {best}
    stack = [best]
    while stack:
        x, y, F = stack.pop()
        for dx, dy in D4:
            for G in by.get((x + dx, y + dy), ()):
                if G - F > step or F - G > drop:
                    continue
                m = (x + dx, y + dy, G)
                if m not in seen:
                    seen.add(m)
                    stack.append(m)
        for c in _quad(x, y):
            for p in links.get(c, ()):
                px, py = int(p[0] // s), int(p[1] // s)
                for cx in (px, px + 1):
                    for cy in (py, py + 1):
                        for G in by.get((cx, cy), ()):
                            if abs(G - p[2]) <= 80:
                                m = (cx, cy, G)
                                if m not in seen:
                                    seen.add(m)
                                    stack.append(m)
    return seen, by


def covered(level: Level, seen: Set[Box]) -> Set[Tuple[int, int, int]]:
    """The (cell, span index) nodes some reachable box stands in."""
    H = level.config.player_height
    out = set()
    for x, y, F in seen:
        for c in _quad(x, y):
            for i, sp in enumerate(level.spans.get(c, ())):
                if sp[0] <= F and sp[1] >= F + H:
                    out.add((c[0], c[1], i))
    return out


def _point_reach(level: Level):
    from .validate import _link_edges, _node_at, flood
    start = next(e for e in level.entities if e.kind == "player_start").pos
    links = _link_edges(level)
    return flood(level, _node_at(level, start), set(), links), links


def _door_cells(level: Level) -> Set[Cell]:
    from .validate import _door_cells as dc
    out = set()
    for e in level.entities:
        if e.kind in ("door", "secret_door") and getattr(e, "bounds", None):
            out |= dc(level, e)
    return out


def _stuck_edges(level: Level):
    """Point moves from where the box stands into where it can't, leading somewhere that
    matters (more than a nook of a few cells)."""
    from .validate import successors
    seen, _ = box_reach(level)
    cov = covered(level, seen)
    pr, links = _point_reach(level)
    unc = pr - cov
    comp: Dict[tuple, int] = {}
    sizes: List[int] = []
    for n in sorted(unc):
        if n in comp:
            continue
        k = len(sizes)
        comp[n] = k
        stack, size = [n], 0
        while stack:
            a = stack.pop()
            size += 1
            for m in successors(level, a, set(), links):
                if m in unc and m not in comp:
                    comp[m] = k
                    stack.append(m)
        sizes.append(size)
    # a pocket matters if it is more than a nook of a few cells, or if a key or the exit is in it
    s = level.cell
    goals = set()
    for e in level.entities:
        if e.kind in ("key", "exit"):
            c = (int(e.pos[0] // s), int(e.pos[1] // s))
            goals |= {comp[n] for n in unc if (n[0], n[1]) == c}
    edges = []
    for n in sorted(cov & pr):
        for m in successors(level, n, set(), links):
            if m in unc and (sizes[comp[m]] > 3 or comp[m] in goals):
                edges.append((n, m))
    return edges


def _owners_near(level: Level, c: Cell, lo: int, hi: int) -> Set[Optional[str]]:
    out = set()
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            if dx == dy == 0:
                continue
            n = (c[0] + dx, c[1] + dy)
            for sp in level.spans_in(n, lo, hi):
                out.add(level.owner_of(n, sp))
            if n in level.glass and level.spans_in(n, lo, hi):
                out.add("glass")
    return out


def _try_open(level: Level, b: Cell, F: int, ceil: int, owner: str, allowed: Set, doors: Set[Cell]) -> bool:
    if b in level.spans and _span_at(level, b, F, level.config.player_height):
        return False
    if b in level.glass or b in level.diag or b in level.sky:
        return False
    if any((b[0] + dx, b[1] + dy) in doors for dx in (-1, 0, 1) for dy in (-1, 0, 1)):
        return False                              # never beside a door: it could go round it
    if not (1 <= b[0] < level.width - 1 and 1 <= b[1] < level.height - 1):
        return False
    for sp in level.spans.get(b, ()):              # something open at this height already (a step)
        if sp[0] < ceil + SLAB and F - SLAB < sp[1]:
            return False
    near = _owners_near(level, b, F, F + level.config.player_height)
    if "glass" in near:
        return False
    near.discard(None)
    for o1 in near:                                # never join spaces that aren't joined already
        for o2 in near:
            if o1 != o2 and (o1, o2) not in allowed:
                return False
    yard = [r for r in level.rooms if r.contains(*b) and r.kind == "courtyard"]
    if yard and ceil != yard[0].ceil:              # in a yard only at full height, open to the sky
        return False
    inside = [f"room:{r.id}" for r in level.rooms if r.contains(*b)]
    pick = [o for o in near if o in inside] or [o for o in near if not o.startswith("room:")] or [owner]
    owner = sorted(pick)[0]                         # a room's cell only inside its footprint
    level.add_span(b, (F, ceil), owner)
    level.part_solid.pop(b, None)
    if yard:
        level.sky.add(b)
    return True


def _joined(level: Level, doors: Set[Cell]) -> Set[Tuple[str, str]]:
    """Pairs of spaces the player can already walk between, not through a door."""
    from .validate import successors
    out = set()
    for (x, y), sps in level.spans.items():
        for i, sp in enumerate(sps):
            a = level.owner_of((x, y), sp)
            if (x, y) in doors:
                continue
            for m in successors(level, (x, y, i), doors):
                b = level.owner_of((m[0], m[1]), level.spans[(m[0], m[1])][m[2]])
                if a and b and a != b:
                    out.add((a, b))
                    out.add((b, a))
    return out


def widen_pinches(level: Level, max_rounds: int = 40) -> int:
    """Open solid cells beside squeezes the player box can't pass. Returns cells opened."""
    H = level.config.player_height
    doors = _door_cells(level)
    opened = 0
    for _ in range(max_rounds):
        edges = _stuck_edges(level)
        if not edges:
            break
        progress = False
        done: Set[Cell] = set()
        joined = _joined(level, doors)
        for n, m in edges:
            if (m[0], m[1]) in done:
                continue
            na, ma = level.spans[(n[0], n[1])][n[2]], level.spans[(m[0], m[1])][m[2]]
            F = max(na[0], ma[0])
            owner = level.owner_of((m[0], m[1]), ma) or level.owner_of((n[0], n[1]), na)
            # the boxes that would hold both cells of the move: try to clear each, fewest cells first
            mx, my = m[0], m[1]
            cands = []
            pair = {(n[0], n[1]), (mx, my)}
            corners = [(x, y) for x in (mx, mx + 1) for y in (my, my + 1)]
            corners += [(x, y) for x in (n[0], n[0] + 1) for y in (n[1], n[1] + 1) if (x, y) not in corners]
            for x, y in corners:
                if True:
                    quad = _quad(x, y)
                    block = [c for c in quad if not _span_at(level, c, F, H)]
                    open_sp = [_span_at(level, c, F, H) for c in quad if _span_at(level, c, F, H)]
                    if not open_sp or not block or len(block) > 2:
                        continue
                    ceil = min(sp[1] for sp in open_sp)
                    both = pair <= set(quad)          # a box holding both cells of the move first
                    cands.append((0 if both else 1, len(block), (x, y), block, ceil))
            for _, _, _, block, ceil in sorted(cands, key=lambda t: t[:3]):
                if all(c not in level.spans or not level.spans_in(c, F, ceil) for c in block):
                    trial = []
                    for c in block:
                        part = level.part_solid.get(c)
                        if not _try_open(level, c, F, ceil, owner, joined, doors):
                            break
                        trial.append((c, part))
                    if len(trial) == len(block):
                        opened += len(trial)
                        progress = True
                        done.add((mx, my))
                        break
                    for c, part in trial:              # undo a half-cleared box
                        level.replace_in(c, F, ceil, [])
                        level.sky.discard(c)
                        level.owners[c] = [o for o in level.owners[c] if o[:2] != (F, ceil)]
                        if not level.owners[c]:
                            level.owners.pop(c)
                        if part:
                            level.part_solid[c] = part
        if not progress:
            break
    if opened:
        level.notes.append(f"clearance: opened {opened} cells")
    return opened


def clearance_report(level: Level) -> List[str]:
    """Rooms and goals the player box can't reach (empty when the level is passable)."""
    seen, _ = box_reach(level)
    cov = covered(level, seen)
    cells = {(x, y) for x, y, _ in cov}
    errors = []
    for r in level.rooms:
        if not any(c in cells for c in r.cells()):
            errors.append(f"clearance: room {r.id} ({r.kind}) too narrow to reach for the player")
    s = level.cell
    for e in level.entities:
        if e.kind in ("key", "exit"):
            c = (int(e.pos[0] // s), int(e.pos[1] // s))
            if c not in cells:
                errors.append(f"clearance: {e.kind} at {c} too narrow to reach for the player")
    return errors
