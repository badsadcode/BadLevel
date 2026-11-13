"""Openings: windows and galleries. Seeing is not walking.

Windows are cut through thin walls (1-2 cells) between two *different* areas:

  slit     48 units tall: you can see and shoot through, but a player (56 tall)
           never fits, so it is visual-only by construction. Crouch below the
           sill and you are hidden.
  glazed   a tall pane: you can see through it, but not walk or shoot through
           it. Its cells are in ``level.glass``: walls for movement and for
           combat sight lines, open for visual connectivity.

Galleries (see ``add_galleries``) are decks at the upper storey's height inside
a tall room, reached by a corridor from an upper-storey room: an overlook onto
the hall below. They connect physically, so they only join rooms in the same
lock zone.
"""
from __future__ import annotations

import random
from typing import Dict, List, Optional, Set, Tuple

from .level import Cell, Entity, Level, Room

SLIT = 48


def _area(level: Level, c: Cell, span) -> Optional[str]:
    return level.owner_of(c, span)


def _through(level: Level, c: Cell, d: Tuple[int, int], max_thick: int = 2):
    """From solid cell c, look along d for the first open cell within the wall thickness.
    Returns (wall cells, open cell) or None."""
    wall = [c]
    x, y = c
    for _ in range(max_thick):
        x, y = x + d[0], y + d[1]
        n = (x, y)
        if n in level.spans:
            return wall, n
        wall.append(n)
    return None


def find_window_sites(level: Level) -> List[dict]:
    """Thin wall runs between two different areas, with the vertical overlap of both sides."""
    sites: Dict[tuple, dict] = {}
    for c0 in list(level.spans):
        if c0 in level.diag:
            continue
        for d in ((1, 0), (0, 1)):
            w0 = (c0[0] + d[0], c0[1] + d[1])
            if w0 in level.spans:
                continue
            hit = _through(level, w0, d)
            if not hit:
                continue
            wall, c1 = hit
            if c1 in level.diag or any(w in level.diag for w in wall):
                continue
            # the side cells beside the wall must be solid too, so a window is a hole in a wall
            for sa in level.spans[c0]:
                for sb in level.spans[c1]:
                    a1, a2 = _area(level, c0, sa), _area(level, c1, sb)
                    if not a1 or not a2 or a1 == a2:
                        continue
                    lo, hi = max(sa[0], sb[0]), min(sa[1], sb[1])
                    if hi - lo < SLIT + 40:
                        continue
                    key = tuple(sorted((a1, a2))) + (d, len(wall))
                    site = sites.setdefault(key, {"areas": tuple(sorted((a1, a2))), "dir": d, "cells": [],
                                                  "thick": len(wall)})
                    site["cells"].append((tuple(wall), c0, c1, sa, sb))
    return list(sites.values())


def add_windows(level: Level, rng: random.Random, count: int, glazed_chance: float):
    if count <= 0:
        return
    sites = find_window_sites(level)
    rng.shuffle(sites)
    # prefer views between different storeys / heights and between rooms
    def score(site):
        _, c0, c1, sa, sb = site["cells"][0]
        rooms = sum(a.startswith("room:") for a in site["areas"])
        return -(abs(sa[0] - sb[0]) // 64 + rooms)
    sites.sort(key=score)
    made = 0
    used_pairs: Set[tuple] = set()
    for site in sites:
        if made >= count:
            break
        if site["areas"] in used_pairs:
            continue
        runs = _runs(site)
        runs = [r for r in runs if len(r) >= 2]
        if not runs:
            continue
        run = max(runs, key=len)
        n = min(len(run), rng.randint(2, 4))
        start = (len(run) - n) // 2
        chosen = run[start:start + n]
        _, _, _, sa, sb = chosen[0]
        floor = max(sa[0], sb[0])
        top = min(sa[1], sb[1])
        glazed = rng.random() < glazed_chance and top - floor >= 128
        if glazed:
            lo, hi = floor + 16, top - 16
        else:
            lo, hi = floor + 32, floor + 32 + SLIT
        if hi > top - 8:
            continue
        ok = True
        for (wall, c0, c1, sa_, sb_) in chosen:
            if max(sa_[0], sb_[0]) != floor or min(sa_[1], sb_[1]) < hi + 8:
                ok = False
            for w in wall:
                if w in level.spans:
                    ok = False
        if not ok:
            continue
        cells = []
        for (wall, _, _, _, _) in chosen:
            for w in wall:
                level.add_span(w, (lo, hi))
                # first: a wall cell that was a room cell (a jamb, a member) still has the room's entry
                level.owners.setdefault(w, []).insert(0, (lo, hi, f"window:{made}"))
                cells.append(w)
                if glazed:
                    level.glass.add(w)
        s = level.cell
        xs = [c[0] for c in cells]
        ys = [c[1] for c in cells]
        b_lo = (min(xs) * s, min(ys) * s, lo)
        b_hi = ((max(xs) + 1) * s, (max(ys) + 1) * s, hi)
        level.entities.append(Entity("window", tuple((a + b) / 2 for a, b in zip(b_lo, b_hi)),
                                     {"glazed": glazed, "areas": list(site["areas"]), "id": made}, (b_lo, b_hi)))
        used_pairs.add(site["areas"])
        made += 1


def _runs(site) -> List[list]:
    """Group a site's wall cells into straight consecutive runs along the wall."""
    d = site["dir"]
    along = (1, 0) if d == (0, 1) else (0, 1)
    items = sorted(site["cells"], key=lambda it: (it[0][0][0] * along[0] + it[0][0][1] * along[1]))
    runs, cur = [], []
    for it in items:
        w0 = it[0][0]
        if cur:
            p = cur[-1][0][0]
            same_line = (w0[0] - p[0], w0[1] - p[1]) == along and it[3] == cur[-1][3] and it[4] == cur[-1][4]
            if not same_line:
                runs.append(cur)
                cur = []
        cur.append(it)
    if cur:
        runs.append(cur)
    return runs


# ----------------------------------------------------------------------
# galleries
# ----------------------------------------------------------------------
def _gallery_deck(room: Room, level: Level):
    """Longest straight wall stretch with a 3-deep deck: (deck cells, port cells) per side, best first."""
    out = []
    for side in "nsew":
        ulen = room.w if side in "ns" else room.h

        def cell(u, v):
            if side == "s":
                return (room.x + u, room.y + v)
            if side == "n":
                return (room.x + u, room.y + room.h - 1 - v)
            if side == "w":
                return (room.x + v, room.y + u)
            return (room.x + room.w - 1 - v, room.y + u)

        ok = [all(room.contains(*cell(u, v)) and not room.is_diag(*cell(u, v)) for v in range(3))
              for u in range(ulen)]
        best, run = (0, 0), None
        for u, good in enumerate(ok + [False]):
            if good and run is None:
                run = u
            elif not good and run is not None:
                if u - run > best[1] - best[0]:
                    best = (run, u)
                run = None
        ua, ub = best
        if ub - ua >= 5:
            deck = [cell(u, v) for u in range(ua, ub) for v in range(3)]
            ports = {cell(u, 0) for u in range(ua + 1, ub - 1)}
            out.append((ub - ua, deck, ports))
    out.sort(key=lambda t: -t[0])
    return [(d, p) for _, d, p in out]


def add_galleries(level: Level, router, rng: random.Random):
    """Tall rooms get a deck at the upper storey's height, reached from an upper-storey room
    in the same lock zone: an overlook down into the hall, and a one-way way down."""
    from .layout import carve_corridor, plan_floors
    cfg = level.config
    g = level.graph
    H = cfg.storey_height
    for room in level.rooms:
        if room.is_connector or room.meta.get("gallery"):
            continue
        G = next((z for z in range(room.floor + H, room.floor + 159, -32) if room.ceil >= z + 144), None)
        if G is None or G - room.floor > cfg.max_drop:
            continue
        zone = g.nodes[room.node].zone
        linked = {c.a if c.b == room.id else c.b for c in level.corridors if room.id in (c.a, c.b)}
        uppers = [u for u in level.rooms if not u.is_connector and u.id != room.id and u.id not in linked
                  and g.nodes[u.node].zone == zone and abs(u.floor - G) <= 96 and u.kind != "secret"]
        uppers.sort(key=lambda u: abs(u.center()[0] - room.center()[0]) + abs(u.center()[1] - room.center()[1]))
        done = False
        for deck, port in _gallery_deck(room, level):
            for u in uppers[:4]:
                env = (min(u.floor, G), max(u.floor, G) + 128)
                try:
                    path = router.route(u, room, 1, False, 1, env, None, port)
                except Exception:
                    continue
                while len(path) > 1 and u.contains(*path[1]):
                    path.pop(0)
                inner = path[1:-1]
                floors = plan_floors(inner, u.floor, G, cfg, 1)
                if floors is None or len(inner) < 3:
                    continue
                for c in deck:
                    level.replace_in(c, *room.slot, [(room.floor, G - 16), (G, room.ceil)])
                edge = g.add_edge(u.node, room.node, tree=False, connector="gallery")
                carve_corridor(level, router, edge.id, u, room, inner, floors, [f + 128 for f in floors],
                               1, 1, 128, False, "open", None, "gallery", env)
                room.meta["gallery"] = [list(c) for c in deck]
                room.meta["gallery_floor"] = G
                room.features.append("gallery")
                done = True
                break
            if done:
                break


# ----------------------------------------------------------------------
# overlooks: seeing a hall you can't (yet) enter
# ----------------------------------------------------------------------
def add_overlook(level: Level, router, rng: random.Random) -> bool:
    """Underground levels: the deep chamber is seen from above before you reach it. A gallery
    would be a physical way in (and the chamber usually sits behind a locked door), so this is
    visual only: an observation booth carved beside the chamber at gallery height, reached by a
    corridor from a room on the storey above, looking in through a wide glazed window (glass
    stops movement and gunfire, not sight), or a long slit where the level has no glass (too low
    for a player). One cell of rock stays between booth and hall."""
    from .layout import LayoutError, carve_corridor, plan_floors
    from .spaces import space_for
    cfg = level.config
    g = level.graph
    H = cfg.storey_height
    halls = [r for r in level.rooms if r.kind == "megaroom" and not r.meta.get("gallery")]
    if not halls:
        return False
    hall = halls[0]
    G = next((z for z in range(hall.floor + H, hall.floor + 159, -32) if hall.ceil >= z + 144), None)
    if G is None:
        return False
    depth = g.nodes[hall.node].depth
    uppers = [u for u in level.rooms if not u.is_connector and u.kind not in ("secret", "courtyard", "megaroom")
              and abs(u.floor - G) <= 96 and g.nodes[u.node].depth <= depth]
    uppers.sort(key=lambda u: abs(u.center()[0] - hall.center()[0]) + abs(u.center()[1] - hall.center()[1]))
    bw, bd = 5, 3                                       # booth: along the wall x out of it
    # a level without glass (the bunker: firing slits only) gets a long observation slit at eye
    # height instead of a pane: 48 units, so no player fits through it either
    glazed = cfg.glazed_chance > 0
    win_lo, win_hi = (G + 16, G + 112) if glazed else (G + 24, G + 24 + SLIT)

    def free(c, lo, hi):
        return (2 < c[0] < level.width - 3 and 2 < c[1] < level.height - 3 and not level.spans_in(c, lo, hi)
                and c not in level.diag and c not in level.glass and not router.room_at.get(c))

    sites = []
    for d in ((0, -1), (0, 1), (-1, 0), (1, 0)):
        along = (1, 0) if d[1] else (0, 1)
        length = hall.w if d[1] else hall.h
        if d == (0, -1):
            edge = [(hall.x + i, hall.y) for i in range(length)]
        elif d == (0, 1):
            edge = [(hall.x + i, hall.y + hall.h - 1) for i in range(length)]
        elif d == (-1, 0):
            edge = [(hall.x, hall.y + i) for i in range(length)]
        else:
            edge = [(hall.x + hall.w - 1, hall.y + i) for i in range(length)]
        mid = length // 2
        for off in sorted(range(2, length - bw - 1), key=lambda o: abs(o + bw // 2 - mid)):
            sites.append((d, along, edge[off:off + bw]))
    for d, along, inner in sites:
        if any(not hall.contains(*c) or hall.is_diag(*c) for c in inner):
            continue
        wall = [(c[0] + d[0], c[1] + d[1]) for c in inner]
        booth = [(c[0] + d[0] * k, c[1] + d[1] * k) for c in inner for k in range(2, 2 + bd)]
        ring = {(x + dx, y + dy) for (x, y) in booth for dx in (-1, 0, 1) for dy in (-1, 0, 1)} - set(booth) - set(wall)
        if not all(free(c, G - 48, G + 176) for c in booth) or not all(free(c, G - 48, G + 176) for c in ring) \
                or not all(free(c, win_lo, win_hi) for c in wall):
            continue
        xs, ys = [c[0] for c in booth], [c[1] for c in booth]
        room = Room(len(level.rooms), -1, "overlook", min(xs), min(ys), max(xs) - min(xs) + 1,
                    max(ys) - min(ys) + 1, G, G + 128, storey=hall.storey + 1)
        level.rooms.append(room)
        space_for("overlook").build(level, room, cfg)
        router.add_room(room)
        for u in uppers[:5]:
            env = (min(u.floor, G), max(u.floor, G) + 128)
            try:
                path = router.route(u, room, 1, False, 1, env)
            except LayoutError:
                continue
            while len(path) > 1 and u.contains(*path[1]):
                path.pop(0)
            inner_path = path[1:-1]
            floors = plan_floors(inner_path, u.floor, G, cfg, 1)
            if floors is None or len(inner_path) < 2:
                continue
            node = g.add_node("overlook", "small", zone=g.nodes[u.node].zone, depth=g.nodes[u.node].depth,
                              storey=room.storey)
            room.node = node.id
            edge = g.add_edge(u.node, node.id, tree=False, connector="overlook")
            carve_corridor(level, router, edge.id, u, room, inner_path, floors, [f + 128 for f in floors],
                           1, 1, 128, False, "open", None, None, env)
            # the window: one cell of wall, glazed, between booth and hall
            for w in wall[1:-1]:
                level.add_span(w, (win_lo, win_hi), "window:overlook")
                if glazed:
                    level.glass.add(w)
            s = level.cell
            ws = wall[1:-1]
            lo = (min(c[0] for c in ws) * s, min(c[1] for c in ws) * s, win_lo)
            hi = ((max(c[0] for c in ws) + 1) * s, (max(c[1] for c in ws) + 1) * s, win_hi)
            level.entities.append(Entity("window", tuple((a + b) / 2 for a, b in zip(lo, hi)),
                                         {"glazed": glazed, "areas": [f"room:{room.id}", f"room:{hall.id}"],
                                          "overlook": True}, (lo, hi)))
            # keep the sightline clear: nothing stands right in front of the window
            front = [(c[0] - d[0] * k, c[1] - d[1] * k) for c in inner[1:-1] for k in range(0, 2)]
            hall.meta["overlook"] = [list(c) for c in front if hall.contains(*c)]
            room.meta["overlooks"] = hall.id
            return True
        # no way up to this booth: take it back out
        for c in room.cells():
            level.spans.pop(c, None)
            level.owners.pop(c, None)
        router.remove_room(room)
        if level.rooms and level.rooms[-1] is room:
            level.rooms.pop()
    return False
