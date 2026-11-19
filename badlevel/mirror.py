"""Mirrored team maps (capture the flag): one half generated, the other its mirror image.

Team maps are judged on one thing above all: neither side may have an advantage. The surest
way is the one the classic CTF maps use: both bases are the same base, mirrored. So:

1. **A half.** The generator builds an ordinary arena-mode level: rooms, corridors, items, spawns,
   all validated as one circuit. That's one team's side.
2. **Lanes.** From the rooms on the half's open (east) side, 2-3 straight lanes, 5 wide, run out
   to the seam: a vertical line a few cells past everything. They are spread apart (several
   routes between bases), each starts from a flat stretch of its room's wall and runs through
   rock only (a cell of wall kept on both sides). On the middle lane a midfield chamber at the
   seam, taller than the lane.
3. **The base.** The room farthest (walking) from the lanes holds the team's flag; the half's
   spawns become that team's spawns. Its quad is taken out: there is one, on the seam.
4. **The mirror.** Every cell, span, slope, 45-degree corner, hazard, part name, room, corridor,
   entity and link is reflected across the seam (x -> 2A - 1 - x) and renumbered; facing angles
   turn round (180 - a), the flag and spawns become team 2. The lanes meet their mirror images
   at the seam. One quad goes on the seam, in the midfield chamber.

The validator then checks the whole map as one arena (everything reachable from everywhere).
"""
from __future__ import annotations

import copy
import dataclasses
import re
from typing import List, Optional

from .layout import LayoutError
from .level import Corridor, Entity, Level

FLIP = {"sw": "se", "se": "sw", "nw": "ne", "ne": "nw"}
LANE_R = 2                # 5 wide
GAP = 7                   # rock between the half's easternmost cell and the seam (room for the midfield)


def _flip_slope(t):
    return (t[1], t[0], t[3], t[2])       # corners (x0y0, x1y0, x1y1, x0y1) mirrored in x


def _lane_height(level: Level) -> int:
    return max(160, int(getattr(level.config, "corridor_height", 128)))


def _free(level: Level, c, f: int, top: int) -> bool:
    return level.free_at(c, f, top, 16) and c not in level.diag


def cut_lanes(level: Level, A: int) -> List[dict]:
    """Straight lanes from east-facing room walls to the seam column A-1."""
    s = level.cell
    h0 = _lane_height(level)
    cands = []
    for r in level.rooms:
        if r.is_connector or r.kind in ("secret", "overlook"):
            continue
        hmin = level.config.player_height + 32
        mine = f"room:{r.id}/"
        for y in range(r.y + LANE_R + 1, r.y + r.h - LANE_R - 1):
            band = range(y - LANE_R, y + LANE_R + 1)
            # the room's east edge on these rows: flat (shaped rooms have cut corners)
            edges = {max((x for x in range(r.x, r.x + r.w) if r.contains(x, yy)), default=None) for yy in band}
            if len(edges) != 1 or None in edges:
                continue
            ex = edges.pop() + 1                    # the first cell east of the room: its wall
            # the room side: the edge column and the one inside it open at the room's floor, or a
            # piece of the room's own dressing (a pilaster, its base, a pier) the lane may cut through
            h, edge, cut = min(h0, r.ceil - r.floor), True, []
            for k in (1, 2):
                for yy in band:
                    c = (ex - k, yy)
                    if not r.contains(*c) or c in level.diag:
                        edge = False
                        break
                    sp = [sp for sp in level.spans.get(c, ()) if sp[0] == r.floor]
                    if sp:
                        h = min(h, sp[0][1] - r.floor)
                    elif (level.part_solid.get(c, "") or level.part_cells.get(c, "")).startswith(mine):
                        cut.append(c)
                    else:
                        edge = False
                        break
                if not edge:
                    break
            if not edge or h < hmin:
                continue
            ok = True
            for x in range(ex, A):
                ring = range(y - LANE_R - 1, y + LANE_R + 2) if x > ex else band
                if not all(_free(level, (x, yy), r.floor, r.floor + h) for yy in ring):
                    ok = False
                    break
            if ok:
                cands.append({"room": r, "y": y, "x0": ex, "floor": r.floor, "height": h, "cut": cut,
                              "score": (A - ex) + abs(y - (r.y + r.h / 2.0)) * 0.5})
    cands.sort(key=lambda c: c["score"])
    lanes: List[dict] = []
    for c in cands:
        if any(abs(c["y"] - l["y"]) < 2 * LANE_R + 8 for l in lanes):
            continue
        if any(c["room"] is l["room"] for l in lanes):
            continue
        lanes.append(c)
        if len(lanes) == 3:
            break
    if len(lanes) < 2:
        raise LayoutError(f"team map: only {len(lanes)} lane(s) reach the seam")
    lanes.sort(key=lambda l: l["y"])
    return lanes


def carve_lanes(level: Level, lanes: List[dict], A: int) -> Optional[dict]:
    """Carve the lanes as corridors; a midfield chamber on the middle one. Returns that lane."""
    mid = lanes[len(lanes) // 2]
    for lane in lanes:
        cid = len(level.corridors)
        y, f, h = lane["y"], lane["floor"], lane["height"]
        path = [(x, y) for x in range(lane["x0"], A)]
        corr = Corridor(cid, -1, lane["room"].id, lane["room"].id, path, [f] * len(path), radius=LANE_R, height=h)
        corr.lane = True
        for c in lane.get("cut", ()):                # through a pilaster (or its base) in the room's wall
            gone = level.spans_in(c, f, f + h)
            top = max([f + h] + [sp[1] for sp in gone])
            level.replace_in(c, f, f + h, [(f, top)])
            if not any(o[2] == f"room:{lane['room'].id}" for o in level.owners.get(c, [])):
                level.owners.setdefault(c, []).append((f, top, f"room:{lane['room'].id}"))
            level.part_solid.pop(c, None)
            level.part_cells.pop(c, None)
        for (x, _) in path:
            for yy in range(y - LANE_R, y + LANE_R + 1):
                level.add_span((x, yy), (f, f + h), f"corridor:{cid}")
                level.part_cells[(x, yy)] = f"corridor:{cid}/lane"
        level.corridors.append(corr)
        lane["corridor"] = cid
        if lane is mid:
            # the midfield: 5 x 9 cells on this side of the seam (10 x 9 once mirrored), 64 higher
            top = f + h + 64
            cells = [(x, yy) for x in range(A - 5, A) for yy in range(y - 4, y + 5)]
            ring = [(x, yy) for x in range(A - 6, A) for yy in range(y - 5, y + 6)]
            if all(c in level.spans and level.owner_of(c, level.spans[c][0]) == f"corridor:{cid}" or
                   _free(level, c, f, top) for c in ring):
                for c in cells:
                    if c in level.spans:                 # a lane cell: raise its ceiling
                        level.replace_in(c, f, f + h, [(f, top)])
                        level.owners[c] = [(lo, top if name == f"corridor:{cid}" else hi, name)
                                           for lo, hi, name in level.owners.get(c, [])]
                    else:
                        level.add_span(c, (f, top), f"corridor:{cid}")
                    level.part_cells[c] = f"corridor:{cid}/midfield"
                lane["chamber"] = True
    return mid


def _rename(name: str, R: int, C: int) -> str:
    def sub(m):
        k = int(m.group(2))
        return f"{m.group(1)}:{k + (R if m.group(1) == 'room' else C)}"
    name = re.sub(r"^(room|corridor):(\d+)", sub, name)
    if name.startswith("window:"):
        name = "window:m" + name[7:]
    return name


def mirror(level: Level, A: int) -> Level:
    """The level plus its mirror image across the seam between columns A-1 and A."""
    s = level.cell
    R, C = len(level.rooms), len(level.corridors)
    g = level.graph
    N, E = len(g.nodes), len(g.edges)
    mx = lambda x: 2 * A - 1 - x                     # noqa: E731
    MX = lambda X: 2 * A * s - X                     # noqa: E731
    mc = lambda c: (mx(c[0]), c[1])                  # noqa: E731

    def mpos(p):
        return (MX(p[0]), p[1], p[2]) if len(p) == 3 else (MX(p[0]), p[1])

    for c, sps in list(level.spans.items()):
        level.spans[mc(c)] = list(sps)
    for c, os_ in list(level.owners.items()):
        level.owners[mc(c)] = [(lo, hi, _rename(o, R, C)) for lo, hi, o in os_]
    for c, corner in list(level.diag.items()):
        level.diag[mc(c)] = FLIP[corner]
    for c, sp in list(level.diag_span.items()):
        level.diag_span[mc(c)] = sp
    for name in ("glass", "sky"):
        st = getattr(level, name)
        st |= {mc(c) for c in list(st)}
    for name in ("ceil_slope", "floor_slope"):
        d = getattr(level, name)
        for c, t in list(d.items()):
            d[mc(c)] = _flip_slope(t)
    for c, k in list(level.hazard.items()):
        level.hazard[mc(c)] = k
    for name in ("part_solid", "part_cells", "part_ceiling"):
        d = getattr(level, name)
        for c, p in list(d.items()):
            d[mc(c)] = _rename(p, R, C)

    # the mission graph: a twin of every node and edge
    for n in list(g.nodes):
        m = copy.copy(n)
        m.id = n.id + N
        g.nodes.append(m)
    for e in list(g.edges):
        m = copy.copy(e)
        m.id, m.a, m.b = e.id + E, e.a + N, e.b + N
        g.edges.append(m)

    for r in list(level.rooms):
        m = dataclasses.replace(r, id=r.id + R, node=r.node + N, x=2 * A - r.x - r.w,
                                features=list(r.features), meta=copy.deepcopy(r.meta),
                                mask=None if r.mask is None else {(r.w - 1 - lx, ly) for lx, ly in r.mask},
                                diag={(r.w - 1 - lx, ly): FLIP[c] for (lx, ly), c in r.diag.items()})
        m.meta["mirror_of"] = r.id
        level.rooms.append(m)
    for c in list(level.corridors):
        m = dataclasses.replace(c, id=c.id + C, edge=c.edge + E if c.edge >= 0 else -1, a=c.a + R, b=c.b + R,
                                path=[mc(p) for p in c.path], floors=list(c.floors),
                                junctions=[j + C for j in c.junctions], beats=[])
        for b in c.beats:
            nb = dict(b)
            nb["cells"] = [[mx(x), y] for x, y in b.get("cells", [])]
            if "pos" in b:
                nb["pos"] = [MX(b["pos"][0])] + list(b["pos"][1:])
            m.beats.append(nb)
        for attr in ("vent", "lane"):
            if hasattr(c, attr):
                setattr(m, attr, getattr(c, attr))
        level.corridors.append(m)
    level.halls += [[i + R for i in h] for h in list(level.halls)]

    for e in list(level.entities):
        if e.kind == "player_start":
            continue                                 # one start, on team 1's side
        props = copy.deepcopy(e.props)
        if "room" in props:
            props["room"] += R
        if "corridor" in props:
            props["corridor"] += C
        if "angle" in props:
            props["angle"] = (180 - props["angle"]) % 360
        if "target" in props:
            props["target"] = mpos(props["target"])
        if "team" in props:
            props["team"] = 2
        bounds = None
        if e.bounds:
            (x0, y0, z0), (x1, y1, z1) = e.bounds
            bounds = ((MX(x1), y0, z0), (MX(x0), y1, z1))
        level.entities.append(Entity(e.kind, mpos(e.pos), props, bounds))
    for L in list(level.links):
        m = dict(L)
        m["a"], m["b"] = mpos(L["a"]), mpos(L["b"])
        if "room" in m:
            m["room"] += R
        level.links.append(m)
    level.width = 2 * A + 1
    return level


def _team_spawns(level: Level, rooms, to_lane, used, flag_d: int, want: int = 8):
    """Team spawns at the back of the base: deep rooms first (farthest walk from the lanes), one per
    room, then more in the big ones; never in the front 40% of the base, off doorways, facing in."""
    import math
    s = level.cell
    old = [e for e in level.entities if e.kind == "spawn"]
    level.entities = [e for e in level.entities if e.kind != "spawn"]
    cands = []
    for r in rooms:
        cx, cy = r.x + r.w / 2.0, r.y + r.h / 2.0
        for c in r.cells():
            sp = level.room_spans(c, r)
            if not sp or len(sp) != 1 or sp[0][0] != r.floor or c in used or c in level.diag:
                continue
            # off doorways: the 5x5 round the cell is this room (or solid)
            if any(level.spans.get((c[0] + dx, c[1] + dy)) and not r.contains(c[0] + dx, c[1] + dy)
                   for dx in range(-2, 3) for dy in range(-2, 3)):
                continue
            d = to_lane.get((c[0], c[1], level.spans[c].index(sp[0])))
            if d is None or d < 0.4 * flag_d:
                continue
            cands.append((d, r, c, math.degrees(math.atan2(cy - (c[1] + 0.5), cx - (c[0] + 0.5)))))
    cands.sort(key=lambda t: -t[0])
    picked = []
    for rnd in range(3):                                 # one per room, then two, then three
        for d, r, c, ang in cands:
            if len(picked) >= want:
                break
            if sum(1 for p in picked if p[1] is r) > rnd:
                continue
            if any(abs(c[0] - p[2][0]) + abs(c[1] - p[2][1]) < 5 for p in picked):
                continue
            picked.append((d, r, c, ang))
    if len(picked) < 4:                                  # a tiny base: keep the arena's spawns
        level.entities += old
        for e in old:
            e.props["team"] = 1
        return
    for d, r, c, ang in picked:
        level.entities.append(Entity("spawn", ((c[0] + 0.5) * s, (c[1] + 0.5) * s, r.floor + 32.0),
                                     {"angle": int(round(ang / 45.0)) * 45 % 360, "room": r.id, "team": 1}))
    for e in level.entities:
        if e.kind == "player_start":
            e.pos = level.entities[-1].pos
            e.props["angle"] = level.entities[-1].props["angle"]


def make_team_map(level: Level) -> Level:
    """A finished arena half -> a mirrored two-team map (see the module docstring)."""
    from .arena import _anchor, _field
    s = level.cell
    A = max(x for x, _ in level.spans) + 1 + GAP
    lanes = cut_lanes(level, A)
    mid = carve_lanes(level, lanes, A)

    # the base: the room farthest from the lanes holds the flag
    entries = [((l["x0"] + 0.5) * s, (l["y"] + 0.5) * s, l["floor"] + 24) for l in lanes]
    to_lane = _field(level, entries)
    rooms = [r for r in level.rooms if not r.is_connector and r.kind not in ("secret", "overlook")]
    used = {(int(e.pos[0] // s), int(e.pos[1] // s)) for e in level.entities}
    best = None
    for r in rooms:
        a = _anchor(level, r)
        if a is None:
            continue
        sp = level.room_spans(a, r)
        d = to_lane.get((a[0], a[1], level.spans[a].index(sp[0])), -1)
        if best is None or d > best[0]:
            best = (d, r, a)
    if best is None or best[0] < 0:
        raise LayoutError("team map: no base room")
    _, base, a = best
    cells = sorted((c for c in base.cells() if c not in used and c not in level.diag and level.room_spans(c, base)
                    and level.room_spans(c, base)[0][0] == base.floor),
                   key=lambda c: (c[0] - a[0]) ** 2 + (c[1] - a[1]) ** 2)
    if not cells:
        raise LayoutError("team map: no spot for the flag")
    fc = cells[0]
    level.entities.append(Entity("flag", ((fc[0] + 0.5) * s, (fc[1] + 0.5) * s, base.floor), {"team": 1, "room": base.id}))
    _team_spawns(level, rooms, to_lane, used, best[0])
    level.entities = [e for e in level.entities if e.kind != "quad"]
    half_rooms = len(level.rooms)

    mirror(level, A)
    # one quad, on the seam: in the midfield chamber
    y = mid["y"]
    level.entities.append(Entity("quad", (A * s, (y + 0.5) * s, mid["floor"]), {"midfield": True}))
    level.arena.update({"team": True, "lanes": len(lanes), "seam": A, "base_room": base.id,
                        "base_room_2": base.id + half_rooms,
                        "flag_distance": best[0], "midfield": bool(mid.get("chamber"))})
    level.notes.append(f"team map: {len(lanes)} lanes, seam at x={A}, flag {best[0]} steps from the lanes")
    return level
