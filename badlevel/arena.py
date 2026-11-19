"""Arena mode: deathmatch levels (the Quake 3 / Unreal Tournament tradition).

A campaign level is a route: start, keys, exit, monsters on the way. A deathmatch level is a
circuit that players run round and round, so it is judged by different things:

* **No dead ends, no traps.** Every spot must lead back to every other (the validator's arena
  check: everything reachable from the first spawn can get back to it, so no drop strands
  anyone). The graph has no locks, start or exit; dead-end rooms get a second way out
  (layout._arena_loops).
* **Spawns spread out**, in the outer rooms, one per room first, facing into the room.
* **Items that make people move.** The power-ups (quad, heavy armour, megahealth) go where fights
  should happen: the most central room first (on high ground if it has any), the others as far
  from it as the central half of the map allows, so the map has more than one place to control.
  Weapons are spread so every spawn is about as far from its nearest gun as every other
  (several tries, the most even kept); the strongest weapons go in the most central of those
  rooms, the weakest near the edge. Then ammo beside each weapon, health in every room, armour
  shards between.
* **High ground by jump pad** (decorate.f_tower, ``cfg.jump_pads``).

Distances are walking distances (cells, by the validator's own moves, drops and links), room
anchor to room anchor. The result is in ``level.arena`` (and the IR): what went where and how
even the spread is (``fairness``: the nearest-weapon distance of the worst-placed spawn over
the best-placed; 1.0 is perfectly even).
"""
from __future__ import annotations

import math
import random
from typing import Dict, List, Optional, Set

from .level import Cell, Entity, Level, Room

# generic weapon roles, weakest first; exporters map them to each game's guns
WEAPONS = ["shotgun", "rapid", "grenade_launcher", "rocket_launcher", "heavy_rapid", "beam", "rail"]
POWER = {"shotgun": 1, "rapid": 1, "grenade_launcher": 2, "heavy_rapid": 2, "rocket_launcher": 3, "beam": 3, "rail": 3}
CLEARED = {"enemy", "health", "ammo", "armor", "megahealth", "weapon", "player_start"}
FAR = 10 ** 6


def jump_launch(pad, target, gravity: float = 800.0, clear: float = 64.0):
    """Velocity for a jump pad throwing a player from ``pad`` onto ``target`` (both x, y, z in
    the game's axes), peaking ``clear`` units above the target. Returns (speed, pitch, yaw) as
    Quake's trigger_push wants them: velocity = speed * 10 along (pitch, yaw), pitch negative up."""
    dx, dy = target[0] - pad[0], target[1] - pad[1]
    rise = target[2] - pad[2]
    d = math.hypot(dx, dy)
    apex = max(rise, 0) + clear
    vz = math.sqrt(2 * gravity * apex)
    t = vz / gravity + math.sqrt(2 * (apex - rise) / gravity)
    vx = d / t
    speed = math.hypot(vx, vz) / 10.0
    pitch = -math.degrees(math.atan2(vz, vx))
    yaw = math.degrees(math.atan2(dy, dx)) % 360
    return round(speed, 1), round(pitch, 1), round(yaw, 1)


def _flat(level: Level, room: Room, c: Cell) -> bool:
    spans = level.room_spans(c, room)
    return bool(spans) and len(spans) == 1 and spans[0][0] == room.floor and c not in level.diag


def _anchor(level: Level, room: Room) -> Optional[Cell]:
    cx, cy = room.x + room.w / 2.0, room.y + room.h / 2.0
    cands = [c for c in room.cells() if _flat(level, room, c)]
    if not cands:
        cands = [c for c in room.cells() if level.room_spans(c, room) and c not in level.diag]
    if not cands:
        return None
    return min(cands, key=lambda c: (c[0] + 0.5 - cx) ** 2 + (c[1] + 0.5 - cy) ** 2)


def _distances(level: Level, rooms: List[Room], anchors: Dict[int, Cell]):
    """Walking steps between room anchors (directed: drops are one way)."""
    from .validate import _link_edges, _node_at, successors
    links = _link_edges(level)
    nodes = {}
    for r in rooms:
        c = anchors[r.id]
        f = level.room_spans(c, r)[0][0]
        nodes[r.id] = _node_at(level, ((c[0] + 0.5) * level.cell, (c[1] + 0.5) * level.cell, f + 24))
    where = {n: rid for rid, n in nodes.items()}
    D: Dict[int, Dict[int, int]] = {}
    for r in rooms:
        dist = {nodes[r.id]: 0}
        frontier = [nodes[r.id]]
        found = {r.id: 0}
        while frontier and len(found) < len(rooms):
            nxt = []
            for n in frontier:
                for m in successors(level, n, set(), links):
                    if m not in dist:
                        dist[m] = dist[n] + 1
                        nxt.append(m)
                        if m in where:
                            found.setdefault(where[m], dist[m])
            frontier = nxt
        D[r.id] = {q.id: found.get(q.id, FAR) for q in rooms}
        D[r.id][r.id] = (r.w + r.h) // 4             # across its own room: not nothing
    return D


def _span_index(level: Level, room: Room, c: Cell) -> int:
    sp = level.room_spans(c, room)[0]
    return level.spans[c].index(sp)


def _free_cells(level: Level, ctx, taken: Set[Cell]) -> List[Cell]:
    """Flat floor cells of the room off the doorways (where decorate._pick_free would pick)."""
    r = ctx.room
    out = []
    for c in r.cells():
        spans = level.room_spans(c, r)
        if spans and len(spans) == 1 and spans[0][0] == r.floor and c not in taken and c not in ctx.clear \
                and c not in level.diag:
            out.append(c)
    return out


def _field(level: Level, targets) -> Dict[tuple, int]:
    """For every spot: the walk (steps) to the nearest target, moving the way a player moves
    (a reverse search over the validator's moves, drops and links)."""
    from .validate import _link_edges, _node_at, successors
    links = _link_edges(level)
    rev: Dict[tuple, List[tuple]] = {}
    for (x, y), spans in level.spans.items():
        for i in range(len(spans)):
            n = (x, y, i)
            for m in successors(level, n, set(), links):
                rev.setdefault(m, []).append(n)
    dist = {}
    frontier = []
    for t in targets:
        try:
            n = _node_at(level, t)
        except (KeyError, ValueError):
            continue
        dist[n] = 0
        frontier.append(n)
    while frontier:
        nxt = []
        for n in frontier:
            for m in rev.get(n, ()):
                if m not in dist:
                    dist[m] = dist[n] + 1
                    nxt.append(m)
        frontier = nxt
    return dist


def _best_band(cands, want: int):
    """The distance band [lo, 1.5 lo] (lo at least 6 steps) holding candidates in the most places."""
    best = (6, 9)
    best_key = (-1, 0)
    for lo in sorted({max(6, x[0]) for x in cands}):
        hi = lo * 1.5
        places = {x[1] for x in cands if lo <= x[0] <= hi}
        rooms = sum(1 for p in places if p[0] == "room")
        key = (min(len(places), want), rooms, -lo)  # enough places, as many rooms as can be, a short walk
        if key > best_key:
            best, best_key = (lo, hi), key
    return best


def populate(level: Level, ctxs, rng: random.Random):
    from .decorate import _at, _pick_free
    cfg = level.config
    level.entities = [e for e in level.entities if e.kind not in CLEARED]
    rooms = [r for r in level.rooms if not r.is_connector and r.kind not in ("overlook", "secret")]
    anchors = {r.id: _anchor(level, r) for r in rooms}
    rooms = [r for r in rooms if anchors[r.id] is not None]
    if not rooms:
        return
    by_id = {r.id: r for r in rooms}
    if getattr(cfg, "jump_pads", 0.0) > 0 and not any(e.kind == "jumppad" for e in level.entities):
        # every arena gets its high ground: a tower in the biggest room that takes one
        from .decorate import f_tower
        for r in sorted(rooms, key=lambda r: -r.w * r.h):
            if r.kind in ("combat", "arena", "hub") and f_tower(ctxs[r.id], rng):
                break
    D = _distances(level, rooms, anchors)
    n = len(rooms)
    cen = {r.id: sum(min(D[r.id][q.id], FAR) + min(D[q.id][r.id], FAR) for q in rooms) / n for r in rooms}
    taken: Dict[int, Set[Cell]] = {r.id: set(getattr(ctxs[r.id], "pads", [])) for r in rooms}
    ents = level.entities
    placed: Dict[str, list] = {"spawns": [], "weapons": [], "powerups": [], "items": 0}

    def high(r: Room) -> List[Cell]:
        ctx = ctxs[r.id]
        cells = list(getattr(ctx, "top_cells", [])) + list(getattr(ctx, "deck_cells", []))
        return [c for c in cells if c not in taken[r.id] and level.room_spans(c, r) and c not in level.diag]

    def spot(r: Room, up: bool = False, z: float = 0.0):
        """A free cell in the room (on its high ground if ``up`` and it has some): its position."""
        if up:
            cells = high(r)
            if cells:
                c = rng.choice(cells)
                taken[r.id].add(c)
                return c, _at(level, r, c, top=True, z_off=z)
        c = _pick_free(ctxs[r.id], rng, taken[r.id])
        if c is None:
            return None, None
        taken[r.id].add(c)
        return c, _at(level, r, c, z_off=z)

    # --- power-ups: the centre first, then spread over the central half ------------------------
    n_power = 1 if n < 5 else 2 if n < 9 else 3
    kinds = ["quad", "armor_heavy", "megahealth"][:n_power] if n_power == 3 else ["armor_heavy", "megahealth"][:n_power]
    order = sorted(rooms, key=lambda r: cen[r.id] * (0.85 if high(r) else 1.0))
    central = order[:max(1, (n + 1) // 2)]
    power_rooms: List[Room] = [order[0]]
    while len(power_rooms) < n_power:
        rest = [r for r in central if r not in power_rooms] or [r for r in rooms if r not in power_rooms]
        if not rest:
            break
        power_rooms.append(max(rest, key=lambda r: min(D[p.id][r.id] + D[r.id][p.id] for p in power_rooms)))
    for kind, r in zip(kinds, power_rooms):
        c, pos = spot(r, up=True)
        if pos:
            ents.append(Entity(kind, pos, {"room": r.id}))
            placed["powerups"].append({"kind": kind, "room": r.id, "high": c in getattr(ctxs[r.id], "top_cells", [])})

    # --- weapons: spread over the map, the strongest in the most central rooms ----------------
    n_weap = max(3, min(len(WEAPONS), n // 2 + 1))
    guns = sorted(WEAPONS[:n_weap], key=lambda w: POWER[w])
    pool = [r for r in rooms if r not in power_rooms] or rooms
    far_out = max(pool, key=lambda r: cen[r.id])
    chosen = [far_out]
    while len(chosen) < min(n_weap, len(pool)):
        rest = [r for r in pool if r not in chosen]
        chosen.append(max(rest, key=lambda r: min(D[c.id][r.id] + D[r.id][c.id] for c in chosen) + rng.random() * 4))
    chosen = sorted(chosen, key=lambda r: -cen[r.id])          # outer first: the weak guns there
    while len(chosen) < len(guns):
        chosen.append(rng.choice(rooms))
    for gun, r in zip(guns, chosen):
        c, pos = spot(r, up=POWER[gun] == 3 and rng.random() < 0.5)
        if pos is None:
            continue
        ents.append(Entity("weapon", pos, {"type": gun, "room": r.id}))
        placed["weapons"].append({"type": gun, "room": r.id})
        # its ammo: in the nearest other room (a reason to move on), or beside it
        others = [q for q in rooms if q is not r]
        q = min(others, key=lambda q: D[r.id][q.id]) if others else r
        c2, pos2 = spot(q)
        if pos2:
            ents.append(Entity("ammo", pos2, {"type": gun}))
            placed["items"] += 1

    # --- spawns: every one about as far from its nearest gun as the others ----------------------
    # places: rooms (a small room holds one spawn, a hall a few) and the straight, level stretches
    # of corridors (one each); each candidate keeps its walk to the nearest gun
    to_gun = _field(level, [e.pos for e in ents if e.kind == "weapon"])
    cands = []                                     # (steps to the nearest gun, place, cap, cell, pos, facing)
    for r in rooms:
        if r in power_rooms and len(rooms) > len(power_rooms) + 2:
            continue                               # not on top of the power-ups
        cx, cy = r.x + r.w / 2.0, r.y + r.h / 2.0
        cap = 1 + (r.w * r.h) // 250
        for c in _free_cells(level, ctxs[r.id], taken[r.id]):
            d = to_gun.get((c[0], c[1], _span_index(level, r, c)))
            if d is None or d >= FAR:
                continue
            face = (cx - (c[0] + 0.5), cy - (c[1] + 0.5))
            if abs(face[0]) + abs(face[1]) < 2 and ctxs[r.id].doors:     # in the middle: face a way out
                dd = ctxs[r.id].doors[0]
                face = (dd[0] - c[0], dd[1] - c[1])
            cands.append((d, ("room", r.id), cap, c, _at(level, r, c, z_off=32.0), face))
    for corr in level.corridors:
        path, floors = corr.path, corr.floors
        for i in range(4, len(path) - 4):
            c = path[i]
            spans = level.spans.get(c, ())
            if c in level.diag or c in level.floor_slope or len(spans) != 1 or spans[0][0] != floors[i] \
                    or floors[i - 1] != floors[i] or floors[i + 1] != floors[i]:
                continue
            if level.owner_of(c, spans[0]) != f"corridor:{corr.id}" or corr.radius < 1:
                continue
            d = to_gun.get((c[0], c[1], 0))
            if d is None or d >= FAR:
                continue
            face = (path[i + 1][0] - c[0], path[i + 1][1] - c[1])
            pos = ((c[0] + 0.5) * level.cell, (c[1] + 0.5) * level.cell, floors[i] + 32.0)
            cands.append((d, ("corr", corr.id), 1, c, pos, face))
    n_spawn = max(4, min(12, n))
    band = _best_band(cands, n_spawn)
    spawns = []                                    # (place, cell, pos, facing, d)
    for lo, hi in (band, (band[0] / 1.5, band[1] * 1.4), (0, FAR)):
        pick = [x for x in cands if lo <= x[0] <= hi]
        while len(spawns) < n_spawn and pick:
            count: Dict[tuple, int] = {}
            for sp in spawns:
                count[sp[0]] = count.get(sp[0], 0) + 1

            def score(x):                          # an empty place first, a room first, far from the others
                d_other = min((abs(x[3][0] - sp[1][0]) + abs(x[3][1] - sp[1][1]) for sp in spawns), default=99)
                return (x[1] not in count, x[1][0] == "room", min(d_other, 30))
            x = max(pick, key=score)
            pick.remove(x)
            d, place, cap, c, pos, face = x
            if any(abs(c[0] - sp[1][0]) + abs(c[1] - sp[1][1]) < 4 for sp in spawns):
                continue
            if count.get(place, 0) >= cap and lo > 0:
                continue
            if place[0] == "room":
                taken[place[1]].add(c)
            spawns.append((place, c, pos, face, d))
        if len(spawns) >= n_spawn:
            break
    for place, c, pos, face, d in spawns:
        angle = int(round(math.degrees(math.atan2(face[1], face[0])) / 45.0)) * 45 % 360
        ents.append(Entity("spawn", pos, {"angle": angle, place[0] if place[0] == "room" else "corridor": place[1]}))
        placed["spawns"].append(list(place))
    if spawns:
        ents.append(Entity("player_start", spawns[0][2], {"angle": ents[-len(spawns)].props["angle"]}))

    # --- health everywhere, armour shards between -----------------------------------------------
    for r in rooms:
        for _ in range(2 if r.w * r.h >= 200 else 1):
            c, pos = spot(r)
            if pos:
                ents.append(Entity("health", pos, {}))
                placed["items"] += 1
    mid = sorted(rooms, key=lambda r: cen[r.id])[n // 3: max(n // 3 + 1, 2 * n // 3)]
    for r in mid[:2 if n >= 8 else 1]:
        c, pos = spot(r)
        if pos:
            ents.append(Entity("armor", pos, {}))
            placed["items"] += 1

    dead = sum(1 for r in rooms if sum(1 for _ in level.graph.neighbors(r.node)) <= 1)
    # the spread as built: each spawn's walk to its nearest gun, in cells
    near = [d for *_, d in spawns]
    fair = round(max(near) / max(6, min(near)), 2) if near else 1.0
    level.arena = {"spawns": len(spawns), "weapons": placed["weapons"], "powerups": placed["powerups"],
                   "items": placed["items"], "fairness": fair, "dead_ends": dead,
                   "jump_pads": sum(1 for e in ents if e.kind == "jumppad"),
                   "nearest_weapon": near}
    level.notes.append(f"arena: {len(spawns)} spawns, {len(placed['weapons'])} weapons, "
                       f"{len(placed['powerups'])} power-ups, fairness {fair}, dead ends {dead}")
