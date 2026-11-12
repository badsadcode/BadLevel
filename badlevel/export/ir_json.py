"""Engine-neutral JSON: everything an engine plugin needs to rebuild the level.

Contains the mission graph, rooms, corridors, entities, merged brushes and
the raw column spans. Units are map units, Z-up.
"""
from __future__ import annotations

import json

from ..brushes import build_brushes
from ..level import Level

FORMAT_VERSION = 1


def to_dict(level: Level, include_spans: bool = True) -> dict:
    d = {
        "format": "badlevel",
        "version": FORMAT_VERSION,
        "seed": level.seed,
        "units": {"cell": level.cell, "up": "z", "unit": "quake"},
        "config": level.config.to_dict() if level.config else {},
        "stats": level.stats(),
        "graph": level.graph.to_dict() if level.graph else None,
        "rooms": [r.to_dict() for r in level.rooms],
        "corridors": [vars(c) for c in level.corridors],
        "halls": [{"rooms": h, "regions": [{"room": r, "kind": level.rooms[r].kind} for r in h]}
                  for h in level.halls],
        "entities": [e.to_dict() for e in level.entities],
        "brushes": [dict({"min": list(b.mins), "max": list(b.maxs), "top": b.top,
                          "bottom": b.bottom, "side": b.side}, **({"corner": b.corner} if b.corner else {}))
                    for b in build_brushes(level)],
        "notes": level.notes,
    }
    if include_spans:
        d["grid"] = {"width": level.width, "height": level.height}
        d["spans"] = [[x, y, spans] for (x, y), spans in sorted(level.spans.items())]
        d["diagonal"] = [[x, y, c] for (x, y), c in sorted(level.diag.items())]
        d["sky"] = [list(c) for c in sorted(level.sky)]
    return d


def to_json(level: Level, **kw) -> str:
    return json.dumps(to_dict(level, **kw), indent=1, default=list)
