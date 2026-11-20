"""The wizard's tower (Noor's idea): a helix stair round a void, impossible-looking flights across it.

One tall round hall is the spine of the level. Inside it:

* **The helix**: a stair 3-4 cells wide climbs round the inside of the wall, about 5-6 units per
  cell (a gentle slope), with a flat landing at every storey. The landings are a quarter turn
  apart (``DELTA``), so each storey's door faces a different way: one and a quarter turns per
  storey. Where two laps pass over the same cell the column holds both (stacked spans, about
  200 units apart); the ground floor runs under the helix wherever there's headroom.
* **The void**: the middle is open from the ground floor to the sky. Fall in and you land on the
  ground floor, where the helix starts: a short cut down, never a trap.
* **Impossible staircases**: straight flights across the void, 3 wide, one per lap, each turned
  a quarter turn from the last, so they pass over and under each other (always with headroom)
  and the tower looks like an Escher drawing. Each joins the helix at two points half a lap
  apart, its slope within a step per cell: perfectly valid geometry, a short cut up.
* **The top** is open to the sky (an observatory roof).

The graph (``build_tower_graph``, ``cfg.tower``): the tower is the hub; each storey hangs one to
three rooms off its landing, the start on the ground floor, the exit (the observatory) at the
top; keycard doors on the upper landings, their keys on the storeys below.
"""
from __future__ import annotations

import math
import random
from typing import Dict, List, Optional, Tuple

from .level import Level, Room

DELTA = math.pi / 2          # the next storey's landing is a quarter turn further round
RING = 3.5                   # the helix's width (cells, measured from the wall inwards)
SLAB = 16                    # stair thickness under a lap or a flight
LANDING_CELLS = 2.5          # half the landing's length along the ring
MIN_GROUND = 96              # the ground floor runs under the helix where it's at least this high


def _geom(R: int):
    D = 2 * R + 1
    c = D / 2.0
    cells = {}
    for u in range(D):
        for v in range(D):
            d = math.hypot(u + 0.5 - c, v + 0.5 - c)
            if d < R + 0.5:
                ang = math.atan2(v + 0.5 - c, u + 0.5 - c) % (2 * math.pi)
                cells[(u, v)] = (d, ang)
    return D, cells


class Helix:
    """Height of the helix at an unrolled angle phi (radians from the ground landing)."""

    def __init__(self, R: int, storeys: int, H: int, theta0: float):
        self.R, self.S, self.H, self.theta0 = R, storeys, H, theta0
        rmid = R - RING / 2
        self.per = 2 * math.pi + DELTA                     # unrolled angle per storey
        self.wl = (LANDING_CELLS / rmid) / self.per        # landing half-width, in storeys

    def storey_at(self, phi: float) -> float:
        return (phi - self.theta0) / self.per

    def height(self, phi: float) -> Optional[int]:
        sf = self.storey_at(phi)
        if sf < -self.wl or sf > self.S - 1 + self.wl:
            return None
        k = round(sf)
        if abs(sf - k) <= self.wl:
            return k * self.H
        k = math.floor(sf)
        t = (sf - k - self.wl) / (1 - 2 * self.wl)
        return int(round(self.H * (k + t) / 8)) * 8

    def floors(self, ang: float) -> List[int]:
        out = []
        for m in range(-1, self.S + 2):
            h = self.height(ang + 2 * math.pi * m)
            if h is not None:
                out.append(h)
        return sorted(set(out))


def plan(room: Room, H: int, rng: random.Random):
    """Fill room.meta with the tower's plan: radius, storeys, landing angles, flights."""
    R = room.meta["R"]
    S = room.meta["storeys"]
    theta0 = rng.choice([0.0, 0.5, 1.0, 1.5]) * math.pi
    room.meta.update({"theta0": theta0, "H": H})


def build(level: Level, room: Room):
    m = room.meta
    R, S, H, theta0 = m["R"], m["storeys"], m["H"], m["theta0"]
    hx = Helix(R, S, H, theta0)
    D, cells = _geom(R)
    base, ceil = room.floor, room.ceil
    owner = f"room:{room.id}"
    columns: Dict[Tuple[int, int], List[int]] = {}
    for (u, v), (d, ang) in cells.items():
        if d >= R - RING:                                  # the helix ring
            fl = hx.floors(ang)
            if not fl or min(fl) >= MIN_GROUND:
                fl = [0] + fl                              # the ground floor under the stair
            columns[(u, v)] = fl
        else:
            columns[(u, v)] = [0]                          # the void
    # the impossible staircases: flights across the void
    flights = []
    rng = random.Random(f"{level.config.seed}/{level.config.variant}/tower/{room.id}")
    void_r = R - RING
    # every flight the helix allows: four directions, every lap; from one point of the helix
    # straight across the void to the point half a lap further round, rising (a short cut up)
    L = 2 * void_r
    options = []
    for j in range(4):
        alpha = theta0 + math.pi / 2 + j * math.pi / 2
        for ma in range(-1, S + 1):
            pa = alpha + 2 * math.pi * ma
            ha = hx.height(pa)
            if ha is None or ha < 140:
                continue
            for mb in range(-1, S + 1):
                hb = hx.height(alpha + math.pi + 2 * math.pi * mb)
                if hb is not None and 0 < hb - ha <= 15 * (L - 1):
                    options.append(((ha + hb) / 2, j, alpha, ha, hb))
    rng.shuffle(options)
    for mid, j, alpha, ha, hb in sorted(options, key=lambda o: o[0]):
        if len(flights) >= S - 1 or any(abs(mid - f["mid"]) < 160 or f["dir"] == j for f in flights):
            continue                                       # spread over the height, each a new direction
        ux, uy = math.cos(alpha), math.sin(alpha)
        deck = {}
        for (u, v), (d, ang) in cells.items():
            if d >= void_r:
                continue
            x, y = u + 0.5 - D / 2.0, v + 0.5 - D / 2.0
            along = x * ux + y * uy                        # -void_r .. void_r, from A to B
            across = -x * uy + y * ux
            if abs(across) <= 1.5:
                t = (along + void_r) / (2 * void_r)
                deck[(u, v)] = int(round((ha + (hb - ha) * t) / 8)) * 8
        # headroom against what's already in those columns (another flight, the ground)
        if not all(min(abs(h - f) for f in columns[c]) >= level.config.player_height + SLAB + 16
                   for c, h in deck.items()):
            continue
        for c, h in deck.items():
            columns[c] = sorted(columns[c] + [h])
        flights.append({"dir": j, "mid": mid, "angle": round(math.degrees(alpha) % 360), "from": ha, "to": hb,
                        "cells": len(deck)})
    for (u, v), fl in columns.items():
        c = (room.x + u, room.y + v)
        spans = []
        for i, f in enumerate(fl):
            top = (fl[i + 1] - SLAB) if i + 1 < len(fl) else ceil - base
            spans.append((base + f, base + top))
        for sp in spans:
            level.add_span(c, sp, owner, slot=(room.floor - 64 if sp == spans[0] else sp[0], sp[1]))
        level.sky.add(c)
        if (u, v) in cells and cells[(u, v)][0] < void_r and len(fl) > 1:
            level.part_cells[c] = f"{owner}/flight"
        elif cells[(u, v)][0] >= void_r:
            level.part_cells[c] = f"{owner}/helix"
    m["flights"] = flights


def ports(room: Room) -> List[Tuple[set, int]]:
    """For each storey: the wall cells of its landing, and its height."""
    m = room.meta
    R, S, H, theta0 = m["R"], m["storeys"], m["H"], m["theta0"]
    D, cells = _geom(R)
    out = []
    rmid = R - RING / 2
    half = (LANDING_CELLS * 0.8) / rmid
    for k in range(S):
        ang = (theta0 + k * DELTA) % (2 * math.pi)
        door = set()
        for (u, v), (d, a) in cells.items():
            diff = abs((a - ang + math.pi) % (2 * math.pi) - math.pi)
            if d >= R - 1.0 and diff <= half:
                door.add((room.x + u, room.y + v))
        out.append((door, room.floor + k * H))
    return out


# ----------------------------------------------------------------------------------------------
# the mission graph
# ----------------------------------------------------------------------------------------------
def build_tower_graph(cfg, rng: random.Random):
    from .graph import KEY_COLORS, MissionGraph
    from .weights import weighted
    g = MissionGraph()
    S = max(3, cfg.storeys)
    tower = g.add_node("tower", "tower", storey=0)
    tower.main = True
    g.center = tower.id
    n_keys = max(0, min(cfg.keys, 2, S - 2))
    locks = sorted(set([S - 1, max(2, S // 2)][:n_keys])) if n_keys else []
    zone_of = lambda s: sum(1 for L in locks if s >= L)   # noqa: E731
    depth = 1
    key_storey = {}
    for i, L in enumerate(locks):
        lo = locks[i - 1] if i else 0
        key_storey[L] = rng.randrange(lo, L) if L - lo > 1 else lo
    for s in range(S):
        z = zone_of(s)
        n_rooms = 1 if s in (0, S - 1) else rng.choice([1, 2, 2, 3])
        prev = tower
        for j in range(n_rooms):
            if s == 0 and j == 0:
                kind, size = "start", "medium"
            elif s == S - 1 and j == n_rooms - 1:
                kind, size = "exit", "medium"
            else:
                kind = rng.choice(["combat", "combat", "hub", "supply"])
                size = weighted(rng, ["small", "medium", "medium", "large"], cfg.size_bias)
            n = g.add_node(kind, size, zone=z, depth=depth, storey=s)
            n.main = j == 0
            depth += 1
            e = g.add_edge(prev.id, n.id)
            if prev is tower and s in locks:
                e.kind, e.key = "locked", KEY_COLORS[locks.index(s)]
            prev = n
        # a key room for a lock further up hangs off this storey's last room
        for L, ks in key_storey.items():
            if ks == s:
                k = g.add_node("key", "medium", zone=z, depth=depth, storey=s, key=KEY_COLORS[locks.index(L)])
                g.add_edge(prev.id, k.id)
    if cfg.secret:
        parents = [n for n in g.nodes if n.kind not in ("tower", "start", "exit", "secret")]
        if parents:
            p = rng.choice(parents)
            sct = g.add_node("secret", "small", zone=p.zone, depth=p.depth, storey=p.storey)
            g.add_edge(p.id, sct.id, kind="secret")
    return g
