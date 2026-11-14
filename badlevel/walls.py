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
  dress    bases and capitals on the members: a plinth (the floor in front of a member, and
           beside it, raised one step) and a capital (the ceiling in front, and beside it,
           dropped 32-64 units under the member's top), chosen per room from the style's
           palette, so the rhythm along a wall stays one order

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

from .architecture import HEAD, Frame, Section, _base_connected, bay_lines, bay_size, place_piers, q
from .level import Cell, Level, Room

# style -> (bay factor, bay min, bay max)
RHYTHM = {"gothic": (0.42, 3, 8), "industrial": (0.7, 4, 8), "bunker": (0.4, 3, 4), "compound": (0.6, 4, 7),
          "egyptian": (0.5, 3, 6), "scifi": (0.35, 3, 4), "castle": (0.5, 4, 7)}


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
        # (``ctx.used`` is not checked on roomy levels: at this point it holds the rings kept free round the
        # members, and a recess leaves its row of floor as it is; sci-fi members 3-4 apart had
        # rings over every gap, so 3 recesses in 4 levels)
        if not room.contains(*r) or room.is_diag(*r) or r in near_door \
                or level.room_spans(r, room) != plain:
            return False
        if r in ctx.used and not getattr(level.config, "spaciousness", 0.0):
            return False                                # (the original rule, kept for --space 0)
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
               "compound": dict(frac_w=0.3, depth=1, frac_h=0.6, z0=32, pointed=False),
               # a sanctuary niche at the end of the axis; a bulkhead door; a fireplace
               "egyptian": dict(frac_w=0.25, depth=2, frac_h=0.6, z0=0, pointed=False),
               "scifi": dict(frac_w=0.3, depth=1, frac_h=None, top=128, z0=0, pointed=False),
               "castle": dict(frac_w=0.25, depth=1, frac_h=None, top=96, z0=0, pointed=False)}[style]
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
    member_w = 2 if style in ("bunker", "castle") and fr.W >= 9 else 1     # counterforts, buttresses
    placed: List[List[Tuple[int, int]]] = []
    if has_members:                                  # the section's own wall piers get dressed too
        placed += [g for g in sec.info.get("placed", []) if all(v in (0, fr.W - 1) for _, v in g)]
    lines = bay_lines(fr.L, bay, member_w)
    if not has_members and fr.W >= 5 and fr.L >= 6:
        groups = []
        for u in lines:
            for v in (0, fr.W - 1):
                groups.append([(u + i, v) for i in range(member_w)])
        tmp = Section("members", fr)
        tmp.piers = groups
        counts["members"] = place_piers(level, room, tmp, ctx, "members")
        placed += tmp.info.get("placed", [])

    # --- outward: recesses between the members, on the long walls
    spec = {
        "gothic": dict(width=2 if bay >= 5 else 1, z0=0, frac=0.5, every=1, depth=1, pointed=True),
        "industrial": dict(width=3 if bay >= 6 else 2, z0=0, frac=None, top=128, every=2, depth=1, pointed=False),
        "bunker": dict(width=2, z0=0, frac=None, top=96, every=1, depth=1, pointed=False),
        "compound": dict(width=3 if bay >= 6 else 2, z0=32, frac=0.55, every=1, depth=1, pointed=False),
        # false doors; panel bays with a console sill; deep window embrasures
        "egyptian": dict(width=1, z0=0, frac=0.5, every=1, depth=1, pointed=False),
        "scifi": dict(width=2, z0=16, frac=None, top=112, every=1, depth=1, pointed=False),   # (3 wide every
        # other bay rarely fitted between members 3-4 apart: 3 recesses in 4 levels)
        "castle": dict(width=1, z0=0, frac=None, top=128, every=1, depth=2, pointed=False),
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
        placed += tmp.info.get("placed", [])

    # --- bases and capitals on the members
    if placed and getattr(cfg, "arch_dress", True):
        dressed = dress_members(level, room, fr, sec, placed, style, near_door, rng)
        if dressed:
            counts["dress"] = dressed

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


# style -> [(base, capital, weight)]: one order per room. base: None | "front" | "pedestal"
# (front and both sides) | "block" (the front cell, 48 high: walked round, after Tom's examples);
# capital: None | (cells, drop) with cells "front" | "flared" (front and sides, the same drop) |
# "stepped" (the front drops, the sides half as far: a two-tier corbel) | "splay" (the front
# ceiling slopes from low at the wall up to the ceiling) | "splay-flared" (front and sides) |
# "corbel" (two cells deep: flat under the member, then sloping back up). The sloped kinds follow
# Tom's Blender pilasters: capitals that flare on an angle rather than in blocks.
ORDERS = {
    "gothic": [("pedestal", ("front", 48), 2), ("front", ("flared", 32), 1), ("pedestal", None, 1),
               ("pedestal", ("stepped", 64), 1), ("pedestal", ("splay", 48), 2), ("block", ("splay-flared", 48), 1)],
    "castle": [("front", ("front", 64), 2), ("pedestal", ("front", 48), 1), (None, ("flared", 32), 1),
               ("front", ("stepped", 64), 1), ("block", ("corbel", 48), 2), ("front", ("splay", 64), 1)],
    "egyptian": [("pedestal", ("flared", 32), 2), ("pedestal", ("front", 64), 1), ("pedestal", ("stepped", 64), 1),
                 ("block", ("splay", 64), 2)],
    "compound": [("front", ("front", 32), 2), (None, ("front", 48), 1), ("pedestal", None, 1),
                 ("front", ("splay", 32), 1)],
    "industrial": [("front", None, 2), (None, ("front", 32), 1), ("front", ("front", 48), 1),
                   ("block", ("corbel", 48), 1)],
    "bunker": [("pedestal", None, 2), ("front", ("flared", 32), 1), ("block", None, 1)],
    "scifi": [("front", ("front", 32), 2), ("pedestal", ("flared", 32), 1), (None, ("front", 48), 1),
              ("front", ("splay-flared", 32), 1)],
}
BASE_RISE = 16                     # one step: walkable (Quake steps up to 18)
BLOCK_RISE = 48                    # a block base: too tall to step on, walked round


def _inward(fr: Frame, group) -> Optional[Tuple[int, int]]:
    if all(v == 0 for _, v in group):
        return (0, 1)
    if all(v == fr.W - 1 for _, v in group):
        return (0, -1)
    if all(u == 0 for u, _ in group):
        return (1, 0)
    if all(u == fr.L - 1 for u, _ in group):
        return (-1, 0)
    return None


def dress_members(level: Level, room: Room, fr: Frame, sec: Optional[Section], groups, style: str,
                  near_door, rng: random.Random) -> Dict[str, object]:
    """A base and a capital on every wall member of the room, the same order for all of them."""
    opts = ORDERS.get(style)
    if not opts:
        return {}
    base, cap, _ = rng.choices(opts, [w for *_, w in opts])[0]
    if base is None and (cap is None or sec is None):
        return {}
    bases = caps = 0
    for g in groups:
        d = _inward(fr, g)
        if d is None:
            continue
        front = [(u + d[0], v + d[1]) for u, v in g]
        along = (1, 0) if d[1] else (0, 1)
        lo = min(g, key=lambda t: t[0] * along[0] + t[1] * along[1])
        hi = max(g, key=lambda t: t[0] * along[0] + t[1] * along[1])
        sides = [(lo[0] - along[0], lo[1] - along[1]), (hi[0] + along[0], hi[1] + along[1])]
        # a base: the plain floor in front (and beside) the member, one step up
        if base:
            cells = front + (sides if base == "pedestal" else [])
            world = [fr.cell(u, v) for u, v in cells if 0 <= u < fr.L and 0 <= v < fr.W]
            rise = BLOCK_RISE if base == "block" else BASE_RISE
            clear = []
            if base == "block":            # a path two cells wide stays in front of the block
                deep = fr.W if d[1] else fr.L
                clear = [fr.cell(u + d[0] * k, v + d[1] * k) for u, v in g for k in (2, 3)]
                if deep < 8 or not all(0 <= fr.uv(c)[0] < fr.L and 0 <= fr.uv(c)[1] < fr.W for c in clear):
                    world = []
            if world and all(room.contains(*c) and not room.is_diag(*c) and c not in near_door
                             and level.room_spans(c, room) == [(room.floor, room.ceil)] for c in world + clear):
                for c in world:
                    level.replace_in(c, *room.slot, [(room.floor + rise, room.ceil)])
                    level.part_cells[c] = f"room:{room.id}/bases"
                if base == "block" and not _base_connected(level, room):
                    for c in world:
                        level.replace_in(c, *room.slot, [(room.floor, room.ceil)])
                        del level.part_cells[c]
                else:
                    bases += 1
        # a capital: the ceiling in front (and beside) the member, dropped under the member's top
        if cap and sec is not None:
            which, drop = cap
            cells = [(uv, drop) for uv in front]
            if which in ("flared", "stepped", "splay-flared"):
                cells += [(uv, drop if which != "stepped" else drop // 2) for uv in sides]
            slope = which in ("splay", "splay-flared")
            if which == "corbel":          # the second cell out slopes back up: an angled underside
                cells += [((u + d[0], v + d[1]), drop) for u, v in front]
            cells = [(uv, dr) for uv, dr in cells if 0 <= uv[0] < fr.L and 0 <= uv[1] < fr.W]
            lows = []
            for uv, dr in cells:
                here = sec.ceil.get(uv, fr.H)
                low = here - dr
                if low < max(HEAD, getattr(level.config, "arch_headroom", HEAD)) + 32:
                    lows = []
                    break
                lows.append((uv, low))
            wall = fr.cell(0, 0), fr.cell(-d[0], -d[1])      # the world direction towards the wall
            to_wall = (wall[1][0] - wall[0][0], wall[1][1] - wall[0][1])
            outer = {(u + d[0], v + d[1]) for u, v in front} if which == "corbel" else set()
            for uv, low in lows:
                sec.ceil[uv] = low
                sec.forced.add(uv)
                level.part_ceiling[fr.cell(*uv)] = f"room:{room.id}/capitals"
                if slope or uv in outer:          # sloped after apply_ceilings, low at the wall side
                    sec.info.setdefault("splays", []).append((fr.cell(*uv), to_wall, low, low + drop))
            caps += bool(lows)
    if not (bases or caps):
        return {}
    return {"base": base, "capital": f"{cap[0]} {cap[1]}" if cap else None, "bases": bases, "capitals": caps}


def apply_splays(level: Level, room: Room, sec: Section):
    """After apply_ceilings: the sloped capitals. A cell's ceiling stays at its low point (what
    gameplay sees); the slope rises from the wall side to the room's ceiling, as a sloped brush."""
    splays = sec.info.pop("splays", [])
    if not getattr(level.config, "arch_smooth", True):
        return                                         # a strict grid: the capitals stay flat blocks
    for c, to_wall, low, high in splays:
        rs = level.room_spans(c, room)
        if not rs or c in level.ceil_slope or c in level.diag:
            continue
        f, top = rs[-1]
        if top != room.floor + low or level.spans[c][-1] != (f, top):
            continue                                   # a feature or a storey changed it: leave it flat
        hi = room.floor + high
        zs = []
        for dx, dy in ((0, 0), (1, 0), (1, 1), (0, 1)):
            near_wall = (to_wall[0] == 1 and dx == 1) or (to_wall[0] == -1 and dx == 0) or \
                        (to_wall[1] == 1 and dy == 1) or (to_wall[1] == -1 and dy == 0)
            zs.append(top if near_wall else hi)
        level.ceil_slope[c] = tuple(zs)


def tidy_dress(level: Level, room: Room):
    """After the room's features (and again after the portals): a base that a feature built over
    (a terrace, a platform, a jamb) is no longer a base, and a capital whose cell was filled is no
    longer a capital, so they lose the name."""
    tag = f"room:{room.id}/bases"
    for c in [c for c, p in level.part_cells.items() if p == tag]:
        rs = level.room_spans(c, room)
        if not rs or rs[0][0] - room.floor not in (BASE_RISE, BLOCK_RISE):
            del level.part_cells[c]
    # a base that a later feature walled in (a terrace raised beside it) is a pocket: you could drop
    # onto it and never climb out. Such a base (and the bases it joins) is filled: the member deepens
    bases = {c for c, p in level.part_cells.items() if p == tag}
    step = level.config.max_step
    seen = set()
    for c0 in sorted(bases):
        if c0 in seen:
            continue
        group, stack = {c0}, [c0]
        while stack:
            a = stack.pop()
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                n = (a[0] + dx, a[1] + dy)
                if n in bases and n not in group:
                    group.add(n)
                    stack.append(n)
        seen |= group
        out = False
        for a in group:
            fa = level.room_spans(a, room)[0][0]
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                n = (a[0] + dx, a[1] + dy)
                if n in group or n in level.diag:
                    continue
                if any(sp[0] <= fa + step and sp[1] - max(sp[0], fa) >= level.config.player_height
                       for sp in level.room_spans(n, room)):
                    out = True                          # a way off it: level, a step up, or down
        if not out:
            for a in group:
                level.replace_in(a, *room.slot, [])
                del level.part_cells[a]
                level.part_solid[a] = f"room:{room.id}/members"
    tag = f"room:{room.id}/capitals"
    for c in [c for c, p in level.part_ceiling.items() if p == tag]:
        if not level.room_spans(c, room):              # a feature filled the cell
            del level.part_ceiling[c]
            level.ceil_slope.pop(c, None)


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
