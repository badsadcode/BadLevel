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
    shape: str = "rect"
    storey: int = 0
    # vertical connectors (stairwell / lift / ladder): floor of the upper landing
    top_floor: Optional[int] = None
    meta: Dict[str, object] = field(default_factory=dict)
    # local open cells (None = full rectangle) and local diagonal half-cells
    mask: Optional[set] = None
    diag: Dict[Cell, str] = field(default_factory=dict)

    def contains(self, cx: int, cy: int) -> bool:
        if not (self.x <= cx < self.x + self.w and self.y <= cy < self.y + self.h):
            return False
        return self.mask is None or (cx - self.x, cy - self.y) in self.mask

    @property
    def slot(self) -> Tuple[int, int]:
        """Vertical range this room may use (room for a pit below its floor)."""
        return (self.floor - 64, self.ceil)

    @property
    def is_connector(self) -> bool:
        from .spaces import space_for
        return space_for(self.kind).vertical

    def is_diag(self, cx: int, cy: int) -> bool:
        return (cx - self.x, cy - self.y) in self.diag

    def world_diag(self) -> Dict[Cell, str]:
        return {(lx + self.x, ly + self.y): c for (lx, ly), c in self.diag.items()}

    def cells(self):
        for cy in range(self.y, self.y + self.h):
            for cx in range(self.x, self.x + self.w):
                if self.mask is None or (cx - self.x, cy - self.y) in self.mask:
                    yield (cx, cy)

    def to_dict(self) -> dict:
        d = {k: v for k, v in vars(self).items() if k not in ("mask", "diag")}
        d["cells"] = len(list(self.cells()))
        d["diag"] = [[x, y, c] for (x, y), c in sorted(self.world_diag().items())]
        return d

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
    radius: int = 1           # half width in cells: 1 -> 3 wide, 2 -> 5, 3 -> 7
    connector: Optional[str] = None   # "drop" for one-way drops
    junctions: List[int] = field(default_factory=list)   # corridors this one crosses at the same height
    beats: List[dict] = field(default_factory=list)      # chambers, alcoves, vaults along it
    height: int = 128
    diagonal: bool = False    # has 45-degree stretches


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
    # who owns which vertical slot of a column: cell -> [(lo, hi, "room:3" / "corridor:1")]
    owners: Dict[Cell, List[Tuple[int, int, str]]] = field(default_factory=dict)
    rooms: List[Room] = field(default_factory=list)
    corridors: List[Corridor] = field(default_factory=list)
    entities: List[Entity] = field(default_factory=list)
    # half-solid cells with a 45-degree wall: cell -> corner of the solid triangle
    diag: Dict[Cell, str] = field(default_factory=dict)
    diag_span: Dict[Cell, Span] = field(default_factory=dict)
    # glazed window cells: solid for movement and gunfire, see-through for visual connectivity
    glass: set = field(default_factory=set)
    # open to the sky: the top of these columns is sky, not a ceiling
    sky: set = field(default_factory=set)
    # traversal links that are not plain walking: lifts, ladders (two-way, with an interaction)
    links: List[dict] = field(default_factory=list)
    # merged halls: each a list of room ids that form one continuous space (semantic regions)
    halls: List[List[int]] = field(default_factory=list)
    graph: object = None
    config: object = None
    notes: List[str] = field(default_factory=list)

    # --- columns -------------------------------------------------------
    def add_span(self, c: Cell, span: Span, owner: str = None, slot: Tuple[int, int] = None):
        """Insert an open span into a column (must not overlap what is there)."""
        spans = self.spans.setdefault(c, [])
        for (f, ceil) in spans:
            if span[0] < ceil and f < span[1]:
                raise ValueError(f"span {span} overlaps {(f, ceil)} at {c}")
        spans.append(span)
        spans.sort()
        if owner:
            lo, hi = slot or span
            self.owners.setdefault(c, []).append((lo, hi, owner))

    def spans_in(self, c: Cell, lo: int, hi: int) -> List[Span]:
        return [sp for sp in self.spans.get(c, []) if sp[0] < hi and lo < sp[1]]

    def free_at(self, c: Cell, lo: int, hi: int, gap: int = 0) -> bool:
        """No open span within [lo - gap, hi + gap)."""
        return not self.spans_in(c, lo - gap, hi + gap)

    def replace_in(self, c: Cell, lo: int, hi: int, new: List[Span]):
        """Replace the spans overlapping [lo, hi) by ``new`` (ownership of the slot is kept)."""
        keep = [sp for sp in self.spans.get(c, []) if not (sp[0] < hi and lo < sp[1])]
        out = sorted(keep + list(new))
        if out:
            self.spans[c] = out
        else:
            self.spans.pop(c, None)

    def owner_at(self, c: Cell, z: float) -> Optional[str]:
        for (lo, hi, name) in self.owners.get(c, ()):
            if lo <= z < hi:
                return name
        return None

    def owner_of(self, c: Cell, span: Span) -> Optional[str]:
        return self.owner_at(c, span[0])

    def room_spans(self, c: Cell, room: "Room") -> List[Span]:
        return self.spans_in(c, *room.slot)

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
            "diagonal_cells": len(self.diag),
            "shapes": sorted({r.shape for r in self.rooms if not r.is_connector}),
            "storeys": len({r.storey for r in self.rooms}),
            "connectors": {k: sum(1 for r in self.rooms if r.kind == k) for k in ("stairwell", "lift", "ladder")},
            "links": len(self.links),
            "drops": sum(1 for c in self.corridors if getattr(c, "connector", None) == "drop"),
            "windows": sum(1 for e in self.entities if e.kind == "window"),
            "galleries": sum(1 for r in self.rooms if r.meta.get("gallery")),
            "halls": len(self.halls),
            "open_areas": sum(1 for r in self.rooms if r.kind == "courtyard"),
            "hall_rooms": sum(len(h) for h in self.halls),
            "z_range": list(self.z_range()),
        }
