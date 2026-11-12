"""Merged halls: neighbouring rooms placed touching, one continuous space.

A hall is not a space type of its own but a *relationship*: its rooms stay
separate rooms (semantic regions) and share a seam instead of a corridor. Seams
between rooms at different heights become full-width terrace steps.
"""
from __future__ import annotations

from ..config import GenConfig
from ..graph import MissionGraph
from ..level import Level, Room

DIRS = [(1, 0), (0, 1), (-1, 0), (0, -1)]


def _mergeable(e, parent: Room, node, g: MissionGraph) -> bool:
    """Can this tree edge become a seam inside one hall instead of a corridor?"""
    from ..graph import CONNECTOR_KINDS
    if not e.tree or e.kind != "open" or e.connector:
        return False
    kinds = {parent.kind, node.kind}
    if kinds & set(CONNECTOR_KINDS) or kinds & {"secret", "key"}:
        return False
    return parent.storey == node.storey


def _seam_cells(a: Room, b: Room):
    """Pairs of touching cells (one in a, one in b) along the shared edge of two rectangles."""
    out = []
    for c in a.cells():
        for dx, dy in DIRS:
            n = (c[0] + dx, c[1] + dy)
            if b.contains(*n):
                out.append((c, n, (dx, dy)))
    return out


def _seam_ok(a: Room, b: Room) -> bool:
    return len(_seam_cells(a, b)) >= 6


def build_halls(level: Level, g: MissionGraph, cfg: GenConfig):
    """Record merged halls, and turn seams between rooms at different heights into terrace
    steps across the whole seam (16-unit steps in the higher room, down towards the seam)."""
    seen = set()
    for r in level.rooms:
        h = tuple(r.meta.get("hall", ()))
        if h and h not in seen:
            seen.add(h)
            level.halls.append(list(h))
    for e in g.edges:
        if not e.merged:
            continue
        a, b = level.rooms[e.a], level.rooms[e.b]
        hi, lo = (a, b) if a.floor > b.floor else (b, a)
        d = hi.floor - lo.floor
        rows = d // cfg.step_height - 1
        if rows <= 0:
            continue                          # within one step: seamless
        terrace = []
        for c_hi, _, (dx, dy) in _seam_cells(hi, lo):
            for j in range(rows):
                c = (c_hi[0] - dx * j, c_hi[1] - dy * j)
                if not hi.contains(*c):
                    break
                level.replace_in(c, *hi.slot, [(lo.floor + cfg.step_height * (j + 1), hi.ceil)])
                terrace.append(list(c))
        hi.meta.setdefault("terrace", []).extend(terrace)
        hi.features.append("terrace")
