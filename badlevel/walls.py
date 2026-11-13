"""Walls: structure along a room's walls, so long walls stop being flat cliffs.

Step 2 of the architecture plan (step 1, cross-sections, is ``architecture.py``). Every
eligible room of a styled level gets a rhythm along its walls, aligned with its bays
when its cross-section has them:

  inward   pilasters / shafts / counterforts: full-height members standing against the
           wall on the bay lines (placed before the room's features, like piers)
  outward  recesses cut into the solid wall *between* the members: lancet niches, service
           bays, shelters, window bays; and on the end wall of a big hall one deep feature
           (an apse niche, a loading door, a blast door, a bay window)
  above    a crane corbel along the long walls of tall industrial halls

Per style:
  gothic      shafts; a blind arcade of lancet niches with pointed (sloped) heads;
              an apse niche on the end wall of grand and major halls
  industrial  frame pilasters; wide low service bays in every other bay; a loading door;
              crane corbels in tall halls
  bunker      thick counterforts close together; low shelter recesses; a blast door recess
  compound    pilasters; raised window bays (a sill you could sit on); a bay window

Safety: a recess is carved only into solid rock, and only where at least one solid cell
stays between it and any other space (8-neighbourhood), never at a doorway (2 cells),
never through the level border; it is all-or-nothing per recess. Members keep the pier
rules (doorway margin, walkable ring, floor stays connected). The validator still
checks everything afterwards.
"""
from __future__ import annotations

import random
from typing import Dict, List, Optional, Tuple

from .architecture import HEAD, Frame, Section, bay_lines, bay_size, place_piers, q
from .level import Cell, Level, Room

# style -> (bay factor, bay min, bay max)
RHYTHM = {"gothic": (0.42, 3, 8), "industrial": (0.7, 4, 8), "bunker": (0.4, 3, 4), "compound": (0.6, 4, 7)}


class Side:
    """One wall of the room's bounding rectangle, in frame coordinates."""

    def __init__(self, fr: Frame, which: str):
        self.fr, self.which = fr, which
        self.long = which in ("v0", "v1")
        self.length = fr.L if self.long else fr.W

    def inner(self, a: int) -> Tuple[int, int]:
        """(u, v) of the room cell at position a along this wall."""
        fr = self.fr
        return {"v0": (a, 0), "v1": (a, fr.W - 1), "u0": (0, a), "u1": (fr.L - 1, a)}[self.which]

    def out_dir(self) -> Tuple[int, int]:
        """World direction pointing out of the room through this wall."""
        fr = self.fr
        du, dv = {"v0": (0, -1), "v1": (0, 1), "u0": (-1, 0), "u1": (1, 0)}[self.which]
        if fr.flip:
            dv = -dv
        return (du, dv) if fr.along_x else (dv, du)


def _neighbours8(c: Cell):
    x, y = c
    return [(x + dx, y + dy) for dx in (-1, 0, 1) for dy in (-1, 0, 1) if dx or dy]


def carve_recess(level: Level, room: Room, ctx, cells: List[Cell], d: Tuple[int, int], depth: int,
                 z0: int, z1: int, head=None, near_door=frozenset(), label: str = "niches") -> bool:
    """Cut a recess ``depth`` cells into the wall behind the room cells ``cells`` (in a row),
    open from room.floor + z0 to room.floor + z1. ``head(along) -> z`` (absolute) gives the
    recess a sloped top. All or nothing."""
    plain = [(room.floor, room.ceil)]
    out = [(r[0] + d[0] * k, r[1] + d[1] * k) for r in cells for k in range(1, depth + 1)]
    planned = set(out)
    for r in cells:
        if not room.contains(*r) or room.is_diag(*r) or r in ctx.used or r in near_door \
                or level.room_spans(r, room) != plain:
            return False
    for o in out:
        if level.spans.get(o) or o in level.diag or o in level.glass or level.owners.get(o):
            return False
        if not (1 < o[0] < level.width - 2 and 1 < o[1] < level.height - 2):
            return False
        for n in _neighbours8(o):          # one solid cell stays between the recess and anything else
            if n in planned or room.contains(*n):
                continue
            if level.spans.get(n) or n in level.glass:
                return False
    lo, hi = room.floor + z0, room.floor + z1
    for o in out:
        level.part_cells[o] = f"room:{room.id}/{label}"
        if head is None:
            level.add_span(o, (lo, hi), f"room:{room.id}", slot=room.slot)
            continue
        along = (lambda X, Y: X) if d[1] else (lambda X, Y: Y)
        zs = tuple(int(round(head(along(o[0] + dx, o[1] + dy)))) for dx, dy in ((0, 0), (1, 0), (1, 1), (0, 1)))
        top = (min(zs) // 16) * 16                  # the span keeps the lowest point, the slope the rest
        level.add_span(o, (lo, top), f"room:{room.id}", slot=room.slot)
        if max(zs) > top:
            level.ceil_slope[o] = zs
    return True


def _local_ceiling(sec: Optional[Section], fr: Frame, uv) -> int:
    if sec is not None and uv in sec.ceil:
        return sec.ceil[uv]
    return fr.H


def articulate(level: Level, room: Room, ctx, sec: Optional[Section], tier: str) -> Dict[str, int]:
    """Members and recesses along the walls of one room. Returns counts for room.meta."""
    cfg = level.config
    style = getattr(cfg, "arch_style", "")
    if style not in RHYTHM or not getattr(cfg, "arch_walls", True):
        return {}
    rng = random.Random(f"{cfg.seed}/{cfg.variant}/walls/{room.id}")
    smooth = getattr(cfg, "arch_smooth", True)          # pointed heads are slopes: a strict grid keeps them flat
    fr = sec.frame if sec is not None else Frame(room)
    H = fr.H
    k, lo, hi = RHYTHM[style]
    bay = (sec.info.get("bay") if sec is not None else None) or bay_size(H, fr.L, k, lo, hi)
    near_door = {(x + dx, y + dy) for (x, y) in ctx.doors for dx in range(-2, 3) for dy in range(-2, 3)}
    counts = {"members": 0, "recesses": 0, "end": 0}

    # --- a cavern's walls are rough-cut: recesses of random width, depth and height, no rhythm
    if sec is not None and sec.name == "cavern":
        for side_name in ("v0", "v1", "u0", "u1"):
            side = Side(fr, side_name)
            a = rng.randint(1, 3)
            while a < side.length - 2:
                w = rng.randint(1, 3)
                if a + w > side.length - 1:
                    break
                cells = [fr.cell(*side.inner(x)) for x in range(a, a + w)]
                ceil_here = min(_local_ceiling(sec, fr, side.inner(x)) for x in range(a, a + w))
                top = min(q(rng.uniform(96, H * 0.7)), ceil_here - 16)
                if top >= 96 and carve_recess(level, room, ctx, cells, side.out_dir(), rng.choice([1, 1, 2]), 0,
                                              top, None, near_door, "rough walls"):
                    counts["recesses"] += 1
                a += w + rng.randint(1, 4)
        counts["rough"] = 1
        return counts

    # --- the end walls of big halls: one strong feature, opposite or beside the entrance
    if tier in ("grand", "major") and fr.W >= 7:
        end = {"gothic": dict(frac_w=0.34, depth=2, frac_h=0.7, z0=0, pointed=True),
               "industrial": dict(frac_w=0.3, depth=1, frac_h=None, top=176, z0=0, pointed=False),
               "bunker": dict(frac_w=0.3, depth=1, frac_h=None, top=112, z0=0, pointed=False),
               "compound": dict(frac_w=0.3, depth=1, frac_h=0.6, z0=32, pointed=False)}[style]
        w = max(3, min(7, int(round(fr.W * end["frac_w"]))))
        tries = [(s, (fr.W - w) // 2, False, w) for s in rng.sample(["u0", "u1"], 2)]
        focus = _incoming_axis(level, room, fr, ctx)
        if focus:
            counts["focus"] = 1
        if focus and rng.random() < getattr(cfg, "arch_axial", 0.65):
            far, a_mid = focus                              # a framed reveal: the feature faces the way in
            length = fr.L if far in ("v0", "v1") else fr.W
            a0 = max(1, min(length - 1 - w, int(round(a_mid - w / 2.0))))
            narrow = []
            for shift in (2, -2, 1, -1, 0):                 # it may slide up to 2 cells off a doorway,
                tries.insert(0, (far, a0 + shift, True, w))
                if w > 3:                                   # or narrow down to 3 between wide doorways
                    narrow.insert(0, (far, int(round(a_mid - 1.5)) + shift, True, 3))
            tries[5:5] = narrow
        for side_name, a0, axial, w in tries:
            side = Side(fr, side_name)
            if a0 < 1 or a0 + w > side.length - 1:
                continue
            cells = [fr.cell(*side.inner(a)) for a in range(a0, a0 + w)]
            ceil_here = min(_local_ceiling(sec, fr, side.inner(a)) for a in range(a0, a0 + w))
            top = q(H * end["frac_h"]) if end["frac_h"] else end["top"]
            top = min(top, ceil_here - 16)
            if top - end["z0"] < 96:
                continue
            head = None
            if end["pointed"] and smooth:
                rise = min(q(w * 12), ceil_here - 16 - top)
                if rise >= 16:
                    d = side.out_dir()
                    xs = [c[0] if d[1] else c[1] for c in cells]
                    centre = (min(xs) + max(xs) + 1) / 2.0
                    half = w / 2.0
                    base = room.floor + top
                    head = (lambda A, base=base, rise=rise, centre=centre, half=half:
                            base + rise * max(0.0, 1 - abs(A - centre) / half))
            depth = end["depth"]
            if carve_recess(level, room, ctx, cells, side.out_dir(), depth, end["z0"], top, head, near_door,
                            "end feature") or \
                    (depth > 1 and carve_recess(level, room, ctx, cells, side.out_dir(), 1, end["z0"], top, head,
                                                near_door, "end feature")):
                counts["end"] += 1
                counts["axial"] = int(axial)
                ctx.used.update(cells)                           # its opening stays clear of members
                break                                            # one per hall
        if focus and not counts.get("axial"):
            # the way in looks straight at another doorway: a through-view, framed by its portal
            from .portals import openings
            far, a_mid = focus
            for op in openings(level, room):
                uvs = [fr.uv(c) for c in op["cells"]]
                on_far = all((v == 0 if far == "v0" else v == fr.W - 1 if far == "v1" else
                              u == 0 if far == "u0" else u == fr.L - 1) for u, v in uvs)
                if on_far:
                    pos = [u if far in ("v0", "v1") else v for u, v in uvs]
                    if min(pos) - 2 <= a_mid <= max(pos) + 3:
                        counts["through"] = 1
                        break

    # --- inward: members on the bay lines of the long walls (unless the section already has them)
    has_members = sec is not None and any(
        all(v in (0, fr.W - 1) for _, v in g) for g in sec.piers)
    member_w = 2 if style == "bunker" and fr.W >= 9 else 1
    lines = bay_lines(fr.L, bay, member_w)
    if not has_members and fr.W >= 5 and fr.L >= 6:
        groups = []
        for u in lines:
            for v in (0, fr.W - 1):
                groups.append([(u + i, v) for i in range(member_w)])
        tmp = Section("members", fr)
        tmp.piers = groups
        counts["members"] = place_piers(level, room, tmp, ctx, "members")

    # --- outward: recesses between the members, on the long walls
    spec = {
        "gothic": dict(width=2 if bay >= 5 else 1, z0=0, frac=0.5, every=1, depth=1, pointed=True),
        "industrial": dict(width=3 if bay >= 6 else 2, z0=0, frac=None, top=128, every=2, depth=1, pointed=False),
        "bunker": dict(width=2, z0=0, frac=None, top=96, every=1, depth=1, pointed=False),
        "compound": dict(width=3 if bay >= 6 else 2, z0=32, frac=0.55, every=1, depth=1, pointed=False),
    }[style]
    if tier == "minor" and style in ("gothic", "compound"):
        spec["every"] = 2                                    # quieter in small rooms
    mids = [(a + b + member_w) // 2 for a, b in zip(lines, lines[1:])]
    if not mids and fr.L >= 8:                               # a short wall without bays: one in the middle
        mids = [fr.L // 2]
    for side_name in ("v0", "v1"):
        side = Side(fr, side_name)
        for i, m in enumerate(mids):
            if i % spec["every"]:
                continue
            w = spec["width"]
            a0 = m - w // 2
            if a0 < 1 or a0 + w > side.length - 1:
                continue
            cells = [fr.cell(*side.inner(a)) for a in range(a0, a0 + w)]
            ceil_here = min(_local_ceiling(sec, fr, side.inner(a)) for a in range(a0, a0 + w))
            top = q(H * spec["frac"]) if spec["frac"] else spec["top"]
            top = min(top, ceil_here - 16)
            if top - spec["z0"] < 80:
                continue
            head = None
            if spec["pointed"] and w == 2 and smooth:                   # a lancet: two sloped halves meeting in a point
                rise = min(48, ceil_here - 16 - top)
                if rise >= 16:
                    d = side.out_dir()
                    centre = min(c[0] if d[1] else c[1] for c in cells) + 1   # the edge between the two cells
                    base = room.floor + top
                    head = (lambda A, base=base, rise=rise, centre=centre: base + rise * max(0.0, 1 - abs(A - centre)))
            if carve_recess(level, room, ctx, cells, side.out_dir(), spec["depth"], spec["z0"], top, head, near_door):
                counts["recesses"] += 1

    # --- the end walls get the rhythm too (a 35 m wide hall has 35 m end walls)
    if fr.W >= 9 and not has_members:
        ebay = bay_size(H, fr.W, k, lo, hi)
        groups = [[(u, v + i) for i in range(member_w)] for v in bay_lines(fr.W, ebay, member_w)
                  for u in (0, fr.L - 1)]
        tmp = Section("members", fr)
        tmp.piers = groups
        counts["members"] += place_piers(level, room, tmp, ctx, "members")

    # --- above: crane corbels along the long walls of a tall industrial hall
    if style == "industrial" and sec is not None and H >= 352 and tier in ("grand", "major"):
        rail = q(H * 0.62)
        for u in range(fr.L):
            for v in (0, fr.W - 1):
                if (u, v) in sec.ceil and sec.ceil[(u, v)] > rail:
                    sec.ceil[(u, v)] = rail
                    sec.forced.add((u, v))
        counts["corbel"] = rail
    return counts


def _incoming_axis(level: Level, room: Room, fr: Frame, ctx) -> Optional[Tuple[str, float]]:
    """The wall facing the way in, and where along it the way in points: (side, position).
    The way in is the corridor from the room nearer the start (lower mission-graph depth)."""
    from .portals import openings
    g = level.graph
    depth = g.nodes[room.node].depth
    best = None
    ops = openings(level, room)
    if len(ops) == 1:
        best = ops[0]                                   # the only way in is the way in
    for op in ([] if best else ops):
        corr = level.corridors[op["corridor"]] if op["corridor"] < len(level.corridors) else None
        if corr is None:
            continue
        other = level.rooms[corr.a if corr.b == room.id else corr.b]
        od = g.nodes[other.node].depth
        if od < depth and (best is None or len(op["cells"]) > len(best["cells"])):
            best = op
    if best is None:
        return None
    uvs = [fr.uv(c) for c in best["cells"]]
    us, vs = [u for u, _ in uvs], [v for _, v in uvs]
    if all(v == 0 for v in vs):
        return "v1", sum(us) / len(us) + 0.5
    if all(v == fr.W - 1 for v in vs):
        return "v0", sum(us) / len(us) + 0.5
    if all(u == 0 for u in us):
        return "u1", sum(vs) / len(vs) + 0.5
    if all(u == fr.L - 1 for u in us):
        return "u0", sum(vs) / len(vs) + 0.5
    return None


# ----------------------------------------------------------------------
# measuring it: how long do walls run flat?
# ----------------------------------------------------------------------
def flat_wall_runs(level: Level, room: Room) -> int:
    """The longest straight run (cells = metres) of wall where nothing changes: open room cell
    on one side, solid rock on the other, edge after edge. Members, recesses, doors and
    windows all break a run."""
    runs: Dict[tuple, List[int]] = {}
    for c in room.cells():
        if room.is_diag(*c) or not level.room_spans(c, room):
            continue
        for d in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            n = (c[0] + d[0], c[1] + d[1])
            if room.contains(*n) or level.spans.get(n) or n in level.diag or n in level.glass:
                continue
            line = (d, n[0]) if d[0] else (d, n[1])
            runs.setdefault(line, []).append(c[1] if d[0] else c[0])
    best = 0
    for alongs in runs.values():
        alongs.sort()
        cur = 1
        for a, b in zip(alongs, alongs[1:]):
            cur = cur + 1 if b == a + 1 else 1
            best = max(best, cur)
        best = max(best, 1 if alongs else 0)
    return best
