"""Core level data model (the engine-neutral intermediate representation).

The world is a 2D grid of columns. Each column holds a sorted list of
*empty spans* ``(floor, ceiling)`` in map units; everything else is solid.
A column with two spans is a room-over-room situation (e.g. space under a
balcony with the balcony deck above it).

Coordinates are Z-up (like Quake / Unreal). Cell ``(x, y)`` covers
``[x*cell, (x+1)*cell)`` on X and ``[y*cell, (y+1)*cell)`` on Y.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple

Cell = Tuple[int, int]
Span = Tuple[int, int]


@dataclass
class Room:
    id: int
    node: int                 # mission graph node id
    kind: str
    x: int
    y: int
    w: int
    h: int
    floor: int
    ceil: int
    features: List[str] = field(default_factory=list)

    def contains(self, cx: int, cy: int) -> bool:
        return self.x <= cx < self.x + self.w and self.y <= cy < self.y + self.h

    def cells(self):
        for cy in range(self.y, self.y + self.h):
            for cx in range(self.x, self.x + self.w):
                yield (cx, cy)

    def center(self) -> Tuple[float, float]:
        return (self.x + self.w / 2.0, self.y + self.h / 2.0)

    def dist(self, cx: int, cy: int) -> int:
        """Chebyshev distance from a cell to this rectangle (0 if inside)."""
        dx = max(self.x - cx, 0, cx - (self.x + self.w - 1))
        dy = max(self.y - cy, 0, cy - (self.y + self.h - 1))
        return max(dx, dy)


@dataclass
class Corridor:
    id: int
    edge: int                 # mission graph edge id
    a: int                    # room id
    b: int                    # room id
    path: List[Cell]          # centre line, from room a to room b
    floors: List[int]         # floor height for each centre-line cell
    kind: str = "open"        # open | locked | secret
    key: Optional[str] = None


@dataclass
class Entity:
    kind: str
    pos: Tuple[float, float, float]
    props: Dict[str, object] = field(default_factory=dict)
    # Brush-like entities (doors, triggers) carry an axis-aligned box.
    bounds: Optional[Tuple[Tuple[float, float, float], Tuple[float, float, float]]] = None

    def to_dict(self) -> dict:
        d = {"kind": self.kind, "pos": list(self.pos), "props": dict(self.props)}
        if self.bounds:
            d["bounds"] = [list(self.bounds[0]), list(self.bounds[1])]
        return d


@dataclass
class Level:
    seed: int
    cell: int
    width: int
    height: int
    spans: Dict[Cell, List[Span]] = field(default_factory=dict)
    owner: Dict[Cell, str] = field(default_factory=dict)  # "room:3" / "corridor:1"
    rooms: List[Room] = field(default_factory=list)
    corridors: List[Corridor] = field(default_factory=list)
    entities: List[Entity] = field(default_factory=list)
    graph: object = None
    config: object = None
    notes: List[str] = field(default_factory=list)

    # --- helpers -------------------------------------------------------
    def z_range(self) -> Tuple[int, int]:
        lo = min(s[0] for spans in self.spans.values() for s in spans)
        hi = max(s[1] for spans in self.spans.values() for s in spans)
        return lo - 32, hi + 32

    def floor_at(self, c: Cell, top: bool = False) -> int:
        spans = self.spans[c]
        return spans[-1][0] if top else spans[0][0]

    def room_by_node(self, node: int) -> Room:
        for r in self.rooms:
            if r.node == node:
                return r
        raise KeyError(node)

    def world(self, cx: float, cy: float) -> Tuple[float, float]:
        """Cell coordinate (may be fractional) to world units."""
        return (cx * self.cell, cy * self.cell)

    def stats(self) -> dict:
        return {
            "seed": self.seed,
            "grid": [self.width, self.height],
            "rooms": len(self.rooms),
            "corridors": len(self.corridors),
            "entities": len(self.entities),
            "open_cells": len(self.spans),
            "multi_span_cells": sum(1 for s in self.spans.values() if len(s) > 1),
            "z_range": list(self.z_range()),
        }
