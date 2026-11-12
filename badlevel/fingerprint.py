"""Layout fingerprints: a small vector describing what kind of place a level is.

Used to check that recipes produce *recognisably different* architecture (not
just different numbers): levels made from the same preset should sit close
together, levels from different presets far apart. Also useful later for
"show me levels like this one" and for spotting presets that drift into each other.
"""
from __future__ import annotations

import math
from typing import Dict, List

from .level import Level

FEATURES = [
    "rooms", "room_area", "room_area_cv", "ceiling", "corridor_width", "corridor_share",
    "storeys", "vertical_spread", "rect_share", "diagonal_share", "hall_share",
    "windows_per_room", "glass_share", "enemies_per_room", "crate_share", "loops",
    "lift_ladder_share",
]


def fingerprint(level: Level) -> Dict[str, float]:
    rooms = [r for r in level.rooms if not r.is_connector]
    areas = [len(list(r.cells())) for r in rooms] or [0]
    mean_a = sum(areas) / len(areas)
    sd_a = math.sqrt(sum((a - mean_a) ** 2 for a in areas) / len(areas))
    corr_cells = sum(1 for c, spans in level.spans.items() for sp in spans
                     if (level.owner_of(c, sp) or "").startswith("corridor:"))
    open_cells = sum(len(s) for s in level.spans.values()) or 1
    windows = [e for e in level.entities if e.kind == "window"]
    enemies = sum(1 for e in level.entities if e.kind == "enemy")
    conns = [r for r in level.rooms if r.is_connector]
    seams = sum(1 for e in level.graph.edges if getattr(e, "merged", False))
    z = [sp[0] for s in level.spans.values() for sp in s]
    return {
        "rooms": len(rooms),
        "room_area": mean_a,
        "room_area_cv": sd_a / mean_a if mean_a else 0.0,
        "ceiling": sum(r.ceil - r.floor for r in rooms) / max(1, len(rooms)),
        "corridor_width": sum(2 * c.radius + 1 for c in level.corridors) / max(1, len(level.corridors)),
        "corridor_share": corr_cells / open_cells,
        "storeys": len({r.storey for r in level.rooms}),
        "vertical_spread": (max(z) - min(z)) / 256.0 if z else 0.0,
        "rect_share": sum(1 for r in rooms if r.shape == "rect") / max(1, len(rooms)),
        "diagonal_share": len(level.diag) / open_cells,
        "hall_share": sum(len(h) for h in level.halls) / max(1, len(rooms)),
        "windows_per_room": len(windows) / max(1, len(rooms)),
        "glass_share": sum(1 for w in windows if w.props.get("glazed")) / max(1, len(windows)),
        "enemies_per_room": enemies / max(1, len(rooms)),
        "crate_share": sum(1 for r in rooms if "crates" in r.features) / max(1, len(rooms)),
        "loops": len(level.corridors) + seams - len(level.rooms) + 1,
        "lift_ladder_share": sum(1 for r in conns if r.kind in ("lift", "ladder")) / max(1, len(conns)),
    }


def normalise(prints: List[Dict[str, float]]) -> List[List[float]]:
    """z-score every feature over the given set."""
    cols = []
    for f in FEATURES:
        vals = [p[f] for p in prints]
        m = sum(vals) / len(vals)
        sd = math.sqrt(sum((v - m) ** 2 for v in vals) / len(vals)) or 1.0
        cols.append([(v - m) / sd for v in vals])
    return [list(row) for row in zip(*cols)]


def distance(a: List[float], b: List[float]) -> float:
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))


def nearest_centroid_accuracy(labels: List[str], vectors: List[List[float]]) -> float:
    """Leave-one-out: is each level closest to the centroid of its own recipe?"""
    hits = 0
    for i, (lab, v) in enumerate(zip(labels, vectors)):
        cents = {}
        for l2 in set(labels):
            group = [w for j, (l3, w) in enumerate(zip(labels, vectors)) if l3 == l2 and j != i]
            if group:
                cents[l2] = [sum(c) / len(group) for c in zip(*group)]
        best = min(cents, key=lambda l2: distance(v, cents[l2]))
        hits += best == lab
    return hits / len(labels)
