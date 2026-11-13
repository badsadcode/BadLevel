"""Quake 1 and Quake 2 .map export (Standard format).

Opens directly in TrenchBroom (Quake game config), compiles with qbsp/ericw-tools,
and imports into Godot (func_godot / Qodot), Unity (e.g. Qonverter-style .map
importers) and Unreal (.map / BSP importers).

Generic entity kinds are mapped to Quake classnames; textures are named after
their role so artists can bulk-replace them.

Quake 2 (``to_map(level, game="quake2")``) is the same brushes in the same format, with
three numbers more on every face (contents, surface flags, value), textures as .wal files
in ``textures/badlevel/`` instead of a .wad, and Quake 2's entities: soldiers and gunners,
keys that open doors through a trigger_key, secrets and the exit through targets. Quake 2
has ladders (a brush with CONTENTS_LADDER), so every BadLevel level can be finished in it.
"""
from __future__ import annotations

from typing import Dict, List

from ..brushes import Brush, build_brushes
from ..shapes import corner_vertices
from ..level import Level
from .wad import WAD_NAME

TEXTURES = {"floor": "bl_floor", "wall": "bl_wall", "ceiling": "bl_ceiling", "sky": "sky1",
            "door": "bl_door", "trigger": "trigger", "secret": "bl_wall"}

KEY_ITEMS = {"red": ("item_key2", 8), "blue": ("item_key1", 16)}  # gold / silver
ENEMIES = {1: "monster_army", 2: "monster_knight", 3: "monster_ogre"}
# Quake's spawnflags 256 / 512 / 1024: not in easy / normal / hard. Of every 4 monsters, all 4 are
# in hard, 3 in normal, 2 in easy (the order the level lists them, so each room thins evenly)
SKILL_FLAGS = (0, 256, 0, 256 | 512)
LIGHT_FADE = 0.5           # light "wait": fade over twice the distance (BadLevel's rooms are big; Tom: too dark)
MINLIGHT = 24              # worldspawn "light": ericw-tools' minimum light, so no face is pitch black
ITEMS = {
    "health": "item_health", "megahealth": "item_health", "ammo": "item_shells",
    "armor": "item_armor1",
}
WEAPONS = {"shotgun": "weapon_supershotgun", "rocket_launcher": "weapon_rocketlauncher"}

# --- Quake 2
# BadLevel texture -> (Quake 2 texture, contents, surface flags)
Q2_SURF_SKY, Q2_SURF_TRANS33, Q2_SURF_NODRAW = 4, 16, 128
Q2_CONTENTS_SOLID, Q2_CONTENTS_WINDOW, Q2_CONTENTS_LADDER = 1, 2, 0x20000000
Q2_TEXTURES = {
    "bl_floor": ("badlevel/floor", 0, 0), "bl_wall": ("badlevel/wall", 0, 0),
    "bl_ceiling": ("badlevel/ceiling", 0, 0), "sky1": ("badlevel/sky", 0, Q2_SURF_SKY),
    "bl_door": ("badlevel/door", 0, 0), "trigger": ("badlevel/trigger", 0, Q2_SURF_NODRAW),
    "bl_glass": ("badlevel/glass", Q2_CONTENTS_WINDOW, Q2_SURF_TRANS33), "bl_lift": ("badlevel/lift", 0, 0),
    "bl_ladder": ("badlevel/ladder", Q2_CONTENTS_SOLID | Q2_CONTENTS_LADDER, 0),
}
Q2_KEY_ITEMS = {"red": "key_red_key", "blue": "key_blue_key"}
Q2_ENEMIES = {1: "monster_soldier_light", 2: "monster_soldier", 3: "monster_gunner"}
Q2_ITEMS = {"health": "item_health", "megahealth": "item_health_mega", "ammo": "ammo_shells",
            "armor": "item_armor_jacket"}
Q2_WEAPONS = {"shotgun": "weapon_supershotgun", "rocket_launcher": "weapon_rocketlauncher"}


def _q2_faces(text: str) -> str:
    """Quake 1 face lines -> Quake 2: the Quake 2 texture name, then contents, flags, value."""
    import re

    def face(m):
        name, contents, flags = Q2_TEXTURES.get(m.group(1), (m.group(1), 0, 0))
        return f") {name} 0 0 0 1 1 {contents} {flags} 0"
    return re.sub(r"\) (\S+) 0 0 0 1 1$", face, text, flags=re.M)


def _fmt(v: float) -> str:
    return str(int(v)) if float(v).is_integer() else f"{v:.2f}"


def _qbsp_normal(p0, p1, p2):
    # qbsp: normal = (p0 - p1) x (p2 - p1); it must point out of the brush
    t1 = [p0[i] - p1[i] for i in range(3)]
    t2 = [p2[i] - p1[i] for i in range(3)]
    return (t1[1] * t2[2] - t1[2] * t2[1], t1[2] * t2[0] - t1[0] * t2[2], t1[0] * t2[1] - t1[1] * t2[0])


def faces_text(faces, inside) -> str:
    """faces: [(p0, p1, p2, texture)] on each plane; orders points so normals face away from inside."""
    lines = []
    for p0, p1, p2, tex in faces:
        n = _qbsp_normal(p0, p1, p2)
        if sum(n[i] * (p0[i] - inside[i]) for i in range(3)) < 0:
            p1, p2 = p2, p1
        pts = " ".join("( %d %d %d )" % tuple(p) for p in (p0, p1, p2))
        lines.append(f"{pts} {tex} 0 0 0 1 1")
    return "{\n" + "\n".join(lines) + "\n}"


def brush_text(mins, maxs, top: str, bottom: str, side: str) -> str:
    x1, y1, z1 = mins
    x2, y2, z2 = maxs
    faces = [
        ((x1, y1, z1), (x1, y1 + 1, z1), (x1, y1, z1 + 1), side),    # -X
        ((x1, y1, z1), (x1, y1, z1 + 1), (x1 + 1, y1, z1), side),    # -Y
        ((x1, y1, z1), (x1 + 1, y1, z1), (x1, y1 + 1, z1), bottom),  # -Z
        ((x2, y2, z2), (x2, y2 + 1, z2), (x2 + 1, y2, z2), top),     # +Z
        ((x2, y2, z2), (x2 + 1, y2, z2), (x2, y2, z2 + 1), side),    # +Y
        ((x2, y2, z2), (x2, y2, z2 + 1), (x2, y2 + 1, z2), side),    # +X
    ]
    inside = ((x1 + x2) / 2, (y1 + y2) / 2, (z1 + z2) / 2)
    return faces_text(faces, inside)


def sloped_text(b: Brush, top: str, bottom: str, side: str) -> str:
    """A box whose bottom is a sloped plane through its corner heights (a smooth vault cell)."""
    (x1, y1, _), (x2, y2, z2) = b.mins, b.maxs
    z00, z10, z11, _ = b.slope
    faces = [
        ((x1, y1, z2), (x1, y1 + 1, z2), (x1, y1, z2 + 1), side),     # -X
        ((x1, y1, z2), (x1, y1, z2 + 1), (x1 + 1, y1, z2), side),     # -Y
        ((x2, y2, z2), (x2, y2 + 1, z2), (x2 + 1, y2, z2), top),      # +Z
        ((x2, y2, z2), (x2 + 1, y2, z2), (x2, y2, z2 + 1), side),     # +Y
        ((x2, y2, z2), (x2, y2, z2 + 1), (x2, y2 + 1, z2), side),     # +X
        ((x1, y1, z00), (x2, y1, z10), (x2, y2, z11), bottom),   # the bottom: three of the four
    ]                                                           # (coplanar) corners
    inside = ((x1 + x2) / 2, (y1 + y2) / 2, (max(b.slope) + z2) / 2)
    return faces_text(faces, inside)


def top_sloped_text(b: Brush, top: str, bottom: str, side: str) -> str:
    """A box whose top is a sloped plane through its corner heights (a ramp cell)."""
    (x1, y1, z1), (x2, y2, _) = b.mins, b.maxs
    z00, z10, z11, _ = b.top_slope
    faces = [
        ((x1, y1, z1), (x1, y1 + 1, z1), (x1, y1, z1 + 1), side),     # -X
        ((x1, y1, z1), (x1, y1, z1 + 1), (x1 + 1, y1, z1), side),     # -Y
        ((x1, y1, z1), (x1 + 1, y1, z1), (x1, y1 + 1, z1), bottom),   # -Z
        ((x2, y2, z1), (x2 + 1, y2, z1), (x2, y2, z1 + 1), side),     # +Y
        ((x2, y2, z1), (x2, y2, z1 + 1), (x2, y2 + 1, z1), side),     # +X
        ((x1, y1, z00), (x2, y1, z10), (x2, y2, z11), top),           # the top: three of the four corners
    ]
    inside = ((x1 + x2) / 2, (y1 + y2) / 2, (z1 + min(b.top_slope)) / 2)
    return faces_text(faces, inside)


def prism_text(b: Brush, tex: str) -> str:
    """Triangular prism for a 45-degree wall cell."""
    (x1, y1, z1), (x2, y2, z2) = b.mins, b.maxs
    s = x2 - x1
    tri = [(x1 + u * s, y1 + v * s) for (u, v) in corner_vertices(b.corner)[0]]
    cx, cy = sum(p[0] for p in tri) / 3, sum(p[1] for p in tri) / 3
    faces = [
        ((tri[0][0], tri[0][1], z1), (tri[1][0], tri[1][1], z1), (tri[2][0], tri[2][1], z1), tex),
        ((tri[0][0], tri[0][1], z2), (tri[1][0], tri[1][1], z2), (tri[2][0], tri[2][1], z2), tex),
    ]
    for i in range(3):
        a, c = tri[i], tri[(i + 1) % 3]
        faces.append(((a[0], a[1], z1), (c[0], c[1], z1), (a[0], a[1], z2), tex))
    return faces_text(faces, (cx, cy, (z1 + z2) / 2))


def _entity(props: dict, brushes: List[str] = ()) -> str:
    lines = ["{"] + [f'"{k}" "{v}"' for k, v in props.items()] + list(brushes) + ["}"]
    return "\n".join(lines)


def _ibox(bounds):
    lo, hi = bounds
    return tuple(int(v) for v in lo), tuple(int(v) for v in hi)


def _centre(brushes: List[Brush]):
    """The (x, y) shift that centres the level on the origin, in steps of 64 (the texture grid):
    original Quake's network protocol only carries positions within +-4096, and BadLevel builds
    levels from (0, 0) outwards, so an uncentred 200-cell level runs to 6400."""
    xs = [v for b in brushes for v in (b.mins[0], b.maxs[0])]
    ys = [v for b in brushes for v in (b.mins[1], b.maxs[1])]
    if not xs:
        return 0, 0
    return (-int(round((min(xs) + max(xs)) / 2 / 64)) * 64, -int(round((min(ys) + max(ys)) / 2 / 64)) * 64)


def to_map(level: Level, game: str = "quake") -> str:
    import dataclasses
    q2 = game == "quake2"
    brushes: List[Brush] = build_brushes(level)
    ox, oy = _centre(brushes)
    brushes = [dataclasses.replace(b, mins=(b.mins[0] + ox, b.mins[1] + oy, b.mins[2]),
                                   maxs=(b.maxs[0] + ox, b.maxs[1] + oy, b.maxs[2])) for b in brushes]

    def at(pos):
        return (pos[0] + ox, pos[1] + oy, pos[2])

    def box(bounds):
        lo, hi = _ibox(bounds)
        return (lo[0] + ox, lo[1] + oy, lo[2]), (hi[0] + ox, hi[1] + oy, hi[2])
    def text(b):
        return (prism_text(b, TEXTURES[b.side]) if b.corner else
                sloped_text(b, TEXTURES[b.top], TEXTURES[b.bottom], TEXTURES[b.side]) if b.slope else
                top_sloped_text(b, TEXTURES[b.top], TEXTURES[b.bottom], TEXTURES[b.side]) if b.top_slope else
                brush_text(b.mins, b.maxs, TEXTURES[b.top], TEXTURES[b.bottom], TEXTURES[b.side]))
    world = [text(b) for b in brushes if not b.part]
    # named elements (a vault, piers, a portal, a building) as TrenchBroom groups: func_group, which
    # compilers merge into the world, so the map compiles the same; in the editor each is one click
    parts: Dict[str, List[str]] = {}
    for b in brushes:
        if b.part:
            parts.setdefault(b.part, []).append(text(b))
    world_props = {"classname": "worldspawn", "message": f"BadLevel seed {level.seed}",
                   "_badlevel_seed": level.seed, "wad": WAD_NAME, "light": MINLIGHT,
                   "_badlevel_offset": f"{ox} {oy} 0"}            # .map = level coordinates + offset
    if q2:
        del world_props["wad"]                     # Quake 2 reads textures/badlevel/*.wal
        world_props["sky"] = "unit1_"
    out = [
        "// Game: Quake 2" if q2 else "// Game: Quake",
        "// Format: Quake2" if q2 else "// Format: Standard",
        f"// Generated by BadLevel (seed {level.seed})",
        _entity(world_props, world),
    ]
    for gid, (part, texts) in enumerate(sorted(parts.items()), start=1):
        area, _, element = part.partition("/")
        kind, _, n = area.partition(":")
        label = (f"{kind} {n} {level.rooms[int(n)].kind}" if kind == "room" and n.isdigit()
                 and int(n) < len(level.rooms) else area.replace(":", " "))
        out.append(_entity({"classname": "func_group", "_tb_type": "_tb_group", "_tb_name": f"{label}: {element}",
                            "_tb_id": gid, "_badlevel_part": part}, texts))
    n_enemy = n_door = 0
    for e in level.entities:
        p = {}
        origin = " ".join(_fmt(v) for v in at(e.pos))
        if e.kind == "player_start":
            p = {"classname": "info_player_start", "origin": origin, "angle": e.props.get("angle", 0)}
        elif e.kind == "light":
            p = {"classname": "light", "origin": origin, "light": e.props.get("intensity", 300), "wait": LIGHT_FADE}
        elif e.kind == "enemy":
            p = {"classname": (Q2_ENEMIES if q2 else ENEMIES).get(e.props.get("tier", 1), "monster_army"),
                 "origin": origin}
            k = n_enemy = n_enemy + 1                      # skill levels: easy 1/2, normal 3/4, hard all
            if SKILL_FLAGS[k % 4]:
                p["spawnflags"] = SKILL_FLAGS[k % 4]
        elif e.kind == "key":
            p = {"classname": Q2_KEY_ITEMS[e.props["color"]] if q2 else KEY_ITEMS[e.props["color"]][0],
                 "origin": origin}
        elif e.kind == "weapon":
            p = {"classname": (Q2_WEAPONS if q2 else WEAPONS).get(e.props.get("type"), "weapon_supershotgun"),
                 "origin": origin}
        elif e.kind in ITEMS:
            p = {"classname": (Q2_ITEMS if q2 else ITEMS)[e.kind], "origin": origin}
            if e.kind == "megahealth" and not q2:
                p["spawnflags"] = 2
        elif e.kind == "door" and q2:
            # Quake 2: a door with a name opens only when triggered; a trigger round it fires a
            # trigger_key, which opens it if the player carries the key (and takes the key, so the
            # door stays open: wait -1)
            lo, hi = box(e.bounds)
            n_door += 1
            name = f"bl_door{n_door}"
            out.append(_entity({"classname": "func_door", "angle": -1, "wait": -1, "targetname": name,
                                "_badlevel_key": e.props["key"]}, [brush_text(lo, hi, *(["bl_door"] * 3))]))
            tlo = (lo[0] - 32, lo[1] - 32, lo[2])
            thi = (hi[0] + 32, hi[1] + 32, hi[2])
            out.append(_entity({"classname": "trigger_multiple", "target": name + "_key"},
                               [brush_text(tlo, thi, *(["trigger"] * 3))]))
            out.append(_entity({"classname": "trigger_key", "targetname": name + "_key", "target": name,
                                "item": Q2_KEY_ITEMS[e.props["key"]], "origin": origin}))
            continue
        elif e.kind == "door":
            lo, hi = box(e.bounds)
            p = {"classname": "func_door", "angle": -1, "spawnflags": KEY_ITEMS[e.props["key"]][1],
                 "_badlevel_key": e.props["key"]}
            out.append(_entity(p, [brush_text(lo, hi, *(["bl_door"] * 3))]))
            continue
        elif e.kind == "secret_door":
            lo, hi = box(e.bounds)
            p = {"classname": "func_door_secret", "spawnflags": 0}
            out.append(_entity(p, [brush_text(lo, hi, *(["bl_wall"] * 3))]))
            continue
        elif e.kind == "lift":
            # func_plat is drawn in its raised position and travels `height` units down
            lo, hi = box(e.bounds)
            p = {"classname": "func_plat", "height": e.props["travel"], "speed": 150,
                 "_badlevel_low": e.props["low"], "_badlevel_high": e.props["high"]}
            out.append(_entity(p, [brush_text(lo, hi, *(["bl_lift"] * 3))]))
            continue
        elif e.kind == "ladder" and q2:
            # Quake 2: a solid brush with CONTENTS_LADDER is climbable (a group, merged into the world);
            # it ends flush with the landing, so the player steps off the top onto it
            lo, hi = box(e.bounds)
            hi = (hi[0], hi[1], int(e.props["high"]))
            out.append(_entity({"classname": "func_group", "_badlevel_kind": "ladder",
                                "_badlevel_low": e.props["low"], "_badlevel_high": e.props["high"]},
                               [brush_text(lo, hi, *(["bl_ladder"] * 3))]))
            continue
        elif e.kind == "ladder":
            # Quake 1 has no ladders: a func_ladder volume (Quake 2 / Half-Life style) for importers
            lo, hi = box(e.bounds)
            p = {"classname": "func_ladder", "_badlevel_low": e.props["low"], "_badlevel_high": e.props["high"]}
            out.append(_entity(p, [brush_text(lo, hi, *(["bl_ladder"] * 3))]))
            continue
        elif e.kind == "window":
            if not e.props.get("glazed"):
                continue  # a slit is just a hole in the wall
            lo, hi = box(e.bounds)
            # glass: a func_wall pane; "alpha" makes it see-through in modern Quake engines (Quake 2:
            # the glass texture's own flags, window contents and 33% translucent)
            p = {"classname": "func_wall", "_badlevel_kind": "window_glass"}
            if not q2:
                p["alpha"] = "0.35"
            out.append(_entity(p, [brush_text(lo, hi, *(["bl_glass"] * 3))]))
            continue
        elif e.kind == "exit" and q2:
            lo, hi = box(e.bounds)
            out.append(_entity({"classname": "trigger_multiple", "target": "bl_exit"},
                               [brush_text(lo, hi, *(["trigger"] * 3))]))
            out.append(_entity({"classname": "target_changelevel", "targetname": "bl_exit", "map": "base1",
                                "origin": origin}))
            continue
        elif e.kind == "exit":
            lo, hi = box(e.bounds)
            p = {"classname": "trigger_changelevel", "map": "start"}
            out.append(_entity(p, [brush_text(lo, hi, *(["trigger"] * 3))]))
            continue
        else:
            p = {"classname": "info_null", "origin": origin, "_badlevel_kind": e.kind}
        for k, v in e.props.items():
            p.setdefault(f"_badlevel_{k}", v)
        out.append(_entity(p))
    # Quake counts a secret when the player walks into a trigger_secret: one over each secret
    # room's floor (the end-of-level screen showed "Secrets 0/0" without it)
    s = level.cell
    for r in level.rooms:
        if r.kind != "secret":
            continue
        cells = [c for c in r.cells() if c in level.spans and c not in level.diag]
        if not cells:
            continue
        lo = (min(c[0] for c in cells) * s, min(c[1] for c in cells) * s, r.floor)
        hi = ((max(c[0] for c in cells) + 1) * s, (max(c[1] for c in cells) + 1) * s, r.floor + 64)
        if q2:                                     # Quake 2: a trigger_once fires a target_secret
            name = f"bl_secret{r.id}"
            out.append(_entity({"classname": "trigger_once", "target": name, "_badlevel_room": r.id},
                               [brush_text(*box((lo, hi)), *(["trigger"] * 3))]))
            mid = box((lo, hi))
            out.append(_entity({"classname": "target_secret", "targetname": name, "origin": " ".join(
                _fmt((a + b) / 2) for a, b in zip(*mid))}))
            continue
        out.append(_entity({"classname": "trigger_secret", "_badlevel_room": r.id},
                           [brush_text(*box((lo, hi)), *(["trigger"] * 3))]))
    anchor = _hull_anchor(level)
    if anchor:
        out.append(_entity({"classname": "info_null", "origin": " ".join(_fmt(v) for v in at(anchor)),
                            "_badlevel": "hull fill anchor (removed at spawn)"}))
    text = "\n".join(out) + "\n"
    return _q2_faces(text) if q2 else text


def _hull_anchor(level: Level):
    """A point in wide open space, far from walls, floor and ceiling: qbsp fills the outside of
    each collision hull from an entity standing in that hull's empty space, and the level's own
    entities hug floors, walls and ceilings (lights, pickups, the start). The game removes an
    info_null at spawn."""
    s = level.cell
    rooms = sorted(level.rooms, key=lambda r: -(r.w * r.h))
    for room in rooms[:6]:
        if room.ceil - room.floor < 192:
            continue
        z = room.floor + 72
        cx, cy = room.center()
        for c in sorted(room.cells(), key=lambda c: (c[0] + 0.5 - cx) ** 2 + (c[1] + 0.5 - cy) ** 2):
            if all(any(f <= z - 48 and top >= z + 96 for f, top in level.spans.get((c[0] + dx, c[1] + dy), []))
                   for dx in range(-2, 3) for dy in range(-2, 3)):
                return ((c[0] + 0.5) * s, (c[1] + 0.5) * s, z)
    return None
