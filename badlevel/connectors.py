"""Vertical connector rooms: switchback stairwells, lift shafts and ladder shafts.

Each connector is a small room between two floor heights (``room.floor`` = lower
landing, ``room.top_floor`` = upper landing). It is built in a local frame:
``u`` runs along the room, ``v`` across it. Corridors attach at *ports*: the
lower port at the bottom landing, the upper port at the top landing.

Traversal semantics (stored in ``level.links`` and on the entities):

  stairwell  walk, two-way, accessible; plain stairs, so it needs no link
  lift       ride, two-way, accessible; a moving platform (Quake func_plat)
  ladder     climb, two-way, not accessible (hands needed, slow, exposed)
  spiral     walk, two-way, accessible; a spiral stair round a newel in a round tower
"""
from __future__ import annotations

import random
import math
from typing import Dict, List, Optional, Tuple

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


SPIRAL_SIZE = 11            # tower footprint in cells (a circle 352 units across)
SPIRAL_CORE = 2.0           # cells whose centre is nearer than this to the axis are the newel
SPIRAL_LANDING = 0.15       # turns of flat landing at the bottom and the top
SPIRAL_HEADROOM = 80        # least clear height over a step, under the turn above (player: 56)


def _spiral_cells(S: int):
    """Open cells of the tower ring: (u, v) -> angle in turns (0..1, counter-clockwise from +u)."""
    c = S / 2.0
    out = {}
    for u in range(S):
        for v in range(S):
            du, dv = u + 0.5 - c, v + 0.5 - c
            r = math.hypot(du, dv)
            if SPIRAL_CORE <= r <= c:
                out[(u, v)] = (math.atan2(dv, du) / (2 * math.pi)) % 1.0
    return out


def spiral_floors(S: int, rise: int, turns: int, start: float, cw: bool,
                  step: int = 16) -> Optional[Dict[Tuple[int, int], List[int]]]:
    """Each ring cell's stacked floors (relative to the bottom landing) for a spiral of ``turns``
    turns, or None if a step would be too high or a turn too low over the one below it.
    The path parameter t runs 0 .. turns + landing: flat at 0 for the first ``SPIRAL_LANDING``
    turns, flat at ``rise`` for the last, a straight climb between."""
    a = SPIRAL_LANDING
    span = turns - a
    if span <= 0:
        return None
    cells = _spiral_cells(S)
    floors = {}
    for uv, ang in cells.items():
        phi = ((start - ang) if cw else (ang - start)) % 1.0
        phi = (phi + a / 2) % 1.0                         # the landing sector is centred on ``start``
        fl = []
        for k in range(turns + 1):
            t = k + phi
            if t > turns + a:
                break
            h = 0.0 if t <= a else rise if t >= turns else (t - a) / span * rise
            fl.append(int(round(h / step)) * step)
        floors[uv] = sorted(set(fl))
    # check: steps between neighbours (matched turn by turn) and headroom between turns
    for (u, v), fl in floors.items():
        if any(b - a_ < SPIRAL_HEADROOM + 16 for a_, b in zip(fl, fl[1:])):
            return None
        for n in ((u + 1, v), (u, v + 1)):
            if n not in floors:
                continue
            for f in fl:
                if f == rise:                          # the top landing's edge drops to the turn below
                    continue
                near = min(floors[n], key=lambda g: abs(g - f))
                if abs(near - f) > step and near != rise:
                    return None
    return floors


def spiral_plan(rise: int, rng: random.Random):
    if rise < 128 or rise % 16:
        return None
    sides = [(s, cw) for s in (0.0, 0.25, 0.5, 0.75) for cw in (False, True)]   # door side, direction
    rng.shuffle(sides)
    for turns in range(1, 8):
        for start, cw in sides:
            if spiral_floors(SPIRAL_SIZE, rise, turns, start, cw) is not None:
                return turns, start, cw
    return None


def connector_size(kind: str, rise: int, rng: random.Random, wide: bool = False):
    """(w, h, meta) of the room footprint, or None if this rise can't be built. ``wide`` (with
    spaciousness): flights and shafts 5 cells wide instead of 3, and so are their doors."""
    axis = rng.choice("xy")
    fw = 5 if wide else 3                        # flight width (and landing depth)
    if kind == "stairwell":
        plan = stairwell_plan(rise)
        if plan is None:
            return None
        cycles, F = plan
        L, W = F + 2 * fw, 2 * fw + 1
        meta = {"axis": axis, "cycles": cycles, "flight": F, "L": L, "W": W, "fw": fw}
    elif kind == "spiral":
        plan = spiral_plan(rise, rng)
        if plan is None:
            return None
        turns, start, cw = plan
        S = SPIRAL_SIZE
        return S, S, {"axis": "x", "turns": turns, "start": start, "cw": cw, "L": S, "W": S}
    elif kind == "lift":
        L, W = LIFT_L, LIFT_W + (2 if wide else 0)
        meta = {"axis": axis, "L": L, "W": W, "fw": fw}
    else:
        L, W = LADDER_L, LADDER_W + (3 if wide else 0)
        meta = {"axis": axis, "L": L, "W": W, "fw": fw}
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
    if room.kind == "spiral":
        # the doors: perimeter cells on the landing side (both landings are on the same side)
        ring = _spiral_cells(L)
        c = L / 2.0
        ang = m["start"] * 2 * math.pi
        side = (math.cos(ang), math.sin(ang))
        best = max((u + 0.5 - c) * side[0] + (v + 0.5 - c) * side[1] for (u, v) in ring)
        door = {to_cell(room, u, v) for (u, v) in ring
                if (u + 0.5 - c) * side[0] + (v + 0.5 - c) * side[1] > best - 0.5}
        return {"low": (door, room.floor), "high": (door, room.top_floor)}
    if room.kind == "stairwell":
        h = m.get("fw", 3) // 2                   # the door's centre, half a flight in from the side
        low = {to_cell(room, L - 1, h), to_cell(room, L - 1 - h, 0)}
        high = {to_cell(room, L - 1, W - 1 - h), to_cell(room, L - 1 - h, W - 1)}
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

    if room.kind == "spiral":
        floors = spiral_floors(L, top - low, m["turns"], m["start"], m["cw"], step)
        for (u, v), fl in floors.items():
            _column(level, room, to_cell(room, u, v), [low + f for f in fl])
        newel = {to_cell(room, u, v) for u in range(L) for v in range(W) if (u, v) not in floors
                 and math.hypot(u + 0.5 - L / 2, v + 0.5 - W / 2) < SPIRAL_CORE}
        for c in newel:
            level.part_solid[c] = f"room:{room.id}/newel"
        return

    if room.kind == "stairwell":
        cycles, F = m["cycles"], m["flight"]
        fw = m.get("fw", 3)
        rise_c = 2 * F * step
        for u in range(L):
            for v in range(W):
                c = to_cell(room, u, v)
                if v == W // 2 and fw <= u <= L - fw - 1:
                    continue                                  # the spine between the flights
                if u >= L - fw:                               # far landing: start of every cycle
                    floors = [low + k * rise_c for k in range(cycles + 1)]
                elif u <= fw - 1:                             # turnaround landing
                    floors = [low + k * rise_c + F * step for k in range(cycles)]
                elif v < W // 2:                              # flight A climbs towards u = 0
                    j = (L - fw - 1) - u
                    floors = [low + k * rise_c + (j + 1) * step for k in range(cycles)]
                else:                                         # flight B climbs back towards u = L
                    j = u - fw
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
