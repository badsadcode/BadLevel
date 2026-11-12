"""Vertical connector rooms: switchback stairwells, lift shafts and ladder shafts.

Each connector is a small room between two floor heights (``room.floor`` = lower
landing, ``room.top_floor`` = upper landing). It is built in a local frame:
``u`` runs along the room, ``v`` across it. Corridors attach at *ports*: the
lower port at the bottom landing, the upper port at the top landing.

Traversal semantics (stored in ``level.links`` and on the entities):

  stairwell  walk, two-way, accessible; plain stairs, so it needs no link
  lift       ride, two-way, accessible; a moving platform (Quake func_plat)
  ladder     climb, two-way, not accessible (hands needed, slow, exposed)
"""
from __future__ import annotations

import random
from typing import Dict, Optional, Tuple

from .level import Cell, Entity, Level, Room

LIFT_W, LIFT_L = 5, 6
LADDER_W, LADDER_L = 4, 5
CEIL_ABOVE_TOP = 160


def stairwell_plan(rise: int, step: int = 16) -> Optional[Tuple[int, int]]:
    """Switchback flights: ``cycles`` stacked flight pairs of ``F`` steps each.
    Every cycle climbs 2*F*step and must leave headroom over the cycle below."""
    if rise % (2 * step):
        return None
    m = rise // (2 * step)              # total steps per flight summed over cycles
    for cycles in range(1, m + 1):
        if m % cycles:
            continue
        F = m // cycles
        if 5 <= F <= 9:
            return cycles, F
    return None


def connector_size(kind: str, rise: int, rng: random.Random):
    """(w, h, meta) of the room footprint, or None if this rise can't be built."""
    axis = rng.choice("xy")
    if kind == "stairwell":
        plan = stairwell_plan(rise)
        if plan is None:
            return None
        cycles, F = plan
        L, W = F + 6, 7
        meta = {"axis": axis, "cycles": cycles, "flight": F, "L": L, "W": W}
    elif kind == "lift":
        L, W = LIFT_L, LIFT_W
        meta = {"axis": axis, "L": L, "W": W}
    else:
        L, W = LADDER_L, LADDER_W
        meta = {"axis": axis, "L": L, "W": W}
    w, h = (L, W) if axis == "x" else (W, L)
    return w, h, meta


def to_cell(room: Room, u: int, v: int) -> Cell:
    if room.meta["axis"] == "x":
        return (room.x + u, room.y + v)
    return (room.x + v, room.y + u)


def ports(room: Room) -> Dict[str, Tuple[set, int]]:
    """Perimeter cells where corridors may attach: {"low": (cells, floor), "high": (cells, floor)}."""
    m = room.meta
    L, W = m["L"], m["W"]
    if room.kind == "stairwell":
        low = {to_cell(room, L - 1, 1), to_cell(room, L - 2, 0)}
        high = {to_cell(room, L - 1, W - 2), to_cell(room, L - 2, W - 1)}
    else:
        mid = [W // 2] if W % 2 else [W // 2 - 1, W // 2]
        low = {to_cell(room, L - 1, v) for v in mid}
        high = {to_cell(room, 0, v) for v in mid}
    return {"low": (low, room.floor), "high": (high, room.top_floor)}


def _column(level: Level, room: Room, c: Cell, floors):
    """Stack of open spans at the given floor heights (each up to the next floor minus a slab)."""
    floors = sorted(floors)
    spans = [(f, floors[i + 1] - 16) for i, f in enumerate(floors[:-1])] + [(floors[-1], room.ceil)]
    for sp in spans:
        level.add_span(c, sp)
    level.owners.setdefault(c, []).append((room.slot[0], room.ceil, f"room:{room.id}"))


def build_connector(level: Level, room: Room, step: int = 16):
    m = room.meta
    L, W = m["L"], m["W"]
    low, top = room.floor, room.top_floor
    s = level.cell

    if room.kind == "stairwell":
        cycles, F = m["cycles"], m["flight"]
        rise_c = 2 * F * step
        for u in range(L):
            for v in range(W):
                c = to_cell(room, u, v)
                if v == W // 2 and 3 <= u <= L - 4:
                    continue                                  # the spine between the flights
                if u >= L - 3:                                # far landing: start of every cycle
                    floors = [low + k * rise_c for k in range(cycles + 1)]
                elif u <= 2:                                  # turnaround landing
                    floors = [low + k * rise_c + F * step for k in range(cycles)]
                elif v < W // 2:                              # flight A climbs towards u = 0
                    j = (L - 4) - u
                    floors = [low + k * rise_c + (j + 1) * step for k in range(cycles)]
                else:                                         # flight B climbs back towards u = L
                    j = u - 3
                    floors = [low + k * rise_c + F * step + (j + 1) * step for k in range(cycles)]
                _column(level, room, c, floors)
        return

    # lift / ladder shaft: a top landing (2 rows at u = 0, 1) over open space, the shaft below
    for u in range(L):
        for v in range(W):
            c = to_cell(room, u, v)
            _column(level, room, c, [low, top] if u <= 1 else [low])

    def world(u, v, z):
        x, y = to_cell(room, u, v)
        return [(x + 0.5) * s, (y + 0.5) * s, z]

    vmid = W // 2
    if room.kind == "lift":
        cells = [to_cell(room, u, v) for u in (2, 3, 4) for v in (vmid - 1, vmid, vmid + 1)]
        xs = [c[0] for c in cells]
        ys = [c[1] for c in cells]
        lo = (min(xs) * s, min(ys) * s, top - 16)
        hi = ((max(xs) + 1) * s, (max(ys) + 1) * s, top)
        level.entities.append(Entity("lift", tuple((a + b) / 2 for a, b in zip(lo, hi)),
                                     {"low": low, "high": top, "travel": top - low, "room": room.id}, (lo, hi)))
        level.links.append({"kind": "lift", "a": world(3, vmid, low + 24), "b": world(1, vmid, top + 24),
                            "two_way": True, "interaction": "ride", "accessible": True, "room": room.id})
    else:
        # ladder volume on the front face of the landing (the u = 2 side of row 1)
        x0, y0 = to_cell(room, 2, vmid - 1)
        x1, y1 = to_cell(room, 2, vmid)
        if m["axis"] == "x":
            lo = (x0 * s, min(y0, y1) * s, low)
            hi = (x0 * s + 8, (max(y0, y1) + 1) * s, top + 32)
        else:
            lo = (min(x0, x1) * s, y0 * s, low)
            hi = ((max(x0, x1) + 1) * s, y0 * s + 8, top + 32)
        level.entities.append(Entity("ladder", tuple((a + b) / 2 for a, b in zip(lo, hi)),
                                     {"low": low, "high": top, "room": room.id}, (lo, hi)))
        level.links.append({"kind": "ladder", "a": world(2, vmid, low + 24), "b": world(1, vmid, top + 24),
                            "two_way": True, "interaction": "climb", "accessible": False, "room": room.id})
