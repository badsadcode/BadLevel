"""Quake 1 .map export (Standard format).

Opens directly in TrenchBroom (Quake game config), compiles with qbsp/ericw-tools,
and imports into Godot (func_godot / Qodot), Unity (e.g. Qonverter-style .map
importers) and Unreal (.map / BSP importers).

Generic entity kinds are mapped to Quake classnames; textures are named after
their role so artists can bulk-replace them.
"""
from __future__ import annotations

from typing import Dict, List

from ..brushes import Brush, build_brushes
from ..shapes import corner_vertices
from ..level import Level

TEXTURES = {"floor": "bl_floor", "wall": "bl_wall", "ceiling": "bl_ceiling", "sky": "sky1",
            "door": "bl_door", "trigger": "trigger", "secret": "bl_wall"}

KEY_ITEMS = {"red": ("item_key2", 8), "blue": ("item_key1", 16)}  # gold / silver
ENEMIES = {1: "monster_army", 2: "monster_knight", 3: "monster_ogre"}
ITEMS = {
    "health": "item_health", "megahealth": "item_health", "ammo": "item_shells",
    "armor": "item_armor1",
}
WEAPONS = {"shotgun": "weapon_supershotgun", "rocket_launcher": "weapon_rocketlauncher"}


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


def to_map(level: Level) -> str:
    brushes: List[Brush] = build_brushes(level)
    def text(b):
        return (prism_text(b, TEXTURES[b.side]) if b.corner else
                sloped_text(b, TEXTURES[b.top], TEXTURES[b.bottom], TEXTURES[b.side]) if b.slope else
                brush_text(b.mins, b.maxs, TEXTURES[b.top], TEXTURES[b.bottom], TEXTURES[b.side]))
    world = [text(b) for b in brushes if not b.part]
    # named elements (a vault, piers, a portal, a building) as TrenchBroom groups: func_group, which
    # compilers merge into the world, so the map compiles the same; in the editor each is one click
    parts: Dict[str, List[str]] = {}
    for b in brushes:
        if b.part:
            parts.setdefault(b.part, []).append(text(b))
    out = [
        "// Game: Quake",
        "// Format: Standard",
        f"// Generated by BadLevel (seed {level.seed})",
        _entity({"classname": "worldspawn", "message": f"BadLevel seed {level.seed}",
                 "_badlevel_seed": level.seed}, world),
    ]
    for gid, (part, texts) in enumerate(sorted(parts.items()), start=1):
        area, _, element = part.partition("/")
        kind, _, n = area.partition(":")
        label = (f"{kind} {n} {level.rooms[int(n)].kind}" if kind == "room" and n.isdigit()
                 and int(n) < len(level.rooms) else area.replace(":", " "))
        out.append(_entity({"classname": "func_group", "_tb_type": "_tb_group", "_tb_name": f"{label}: {element}",
                            "_tb_id": gid, "_badlevel_part": part}, texts))
    for e in level.entities:
        p = {}
        origin = " ".join(_fmt(v) for v in e.pos)
        if e.kind == "player_start":
            p = {"classname": "info_player_start", "origin": origin, "angle": e.props.get("angle", 0)}
        elif e.kind == "light":
            p = {"classname": "light", "origin": origin, "light": e.props.get("intensity", 300)}
        elif e.kind == "enemy":
            p = {"classname": ENEMIES.get(e.props.get("tier", 1), "monster_army"), "origin": origin}
        elif e.kind == "key":
            p = {"classname": KEY_ITEMS[e.props["color"]][0], "origin": origin}
        elif e.kind == "weapon":
            p = {"classname": WEAPONS.get(e.props.get("type"), "weapon_supershotgun"), "origin": origin}
        elif e.kind in ITEMS:
            p = {"classname": ITEMS[e.kind], "origin": origin}
            if e.kind == "megahealth":
                p["spawnflags"] = 2
        elif e.kind == "door":
            lo, hi = _ibox(e.bounds)
            p = {"classname": "func_door", "angle": -1, "spawnflags": KEY_ITEMS[e.props["key"]][1],
                 "_badlevel_key": e.props["key"]}
            out.append(_entity(p, [brush_text(lo, hi, *(["bl_door"] * 3))]))
            continue
        elif e.kind == "secret_door":
            lo, hi = _ibox(e.bounds)
            p = {"classname": "func_door_secret", "spawnflags": 0}
            out.append(_entity(p, [brush_text(lo, hi, *(["bl_wall"] * 3))]))
            continue
        elif e.kind == "lift":
            # func_plat is drawn in its raised position and travels `height` units down
            lo, hi = _ibox(e.bounds)
            p = {"classname": "func_plat", "height": e.props["travel"], "speed": 150,
                 "_badlevel_low": e.props["low"], "_badlevel_high": e.props["high"]}
            out.append(_entity(p, [brush_text(lo, hi, *(["bl_lift"] * 3))]))
            continue
        elif e.kind == "ladder":
            # Quake 1 has no ladders: a func_ladder volume (Quake 2 / Half-Life style) for importers
            lo, hi = _ibox(e.bounds)
            p = {"classname": "func_ladder", "_badlevel_low": e.props["low"], "_badlevel_high": e.props["high"]}
            out.append(_entity(p, [brush_text(lo, hi, *(["bl_ladder"] * 3))]))
            continue
        elif e.kind == "window":
            if not e.props.get("glazed"):
                continue  # a slit is just a hole in the wall
            lo, hi = _ibox(e.bounds)
            # glass: a func_wall pane; "alpha" makes it see-through in modern Quake engines
            p = {"classname": "func_wall", "alpha": "0.35", "_badlevel_kind": "window_glass"}
            out.append(_entity(p, [brush_text(lo, hi, *(["bl_glass"] * 3))]))
            continue
        elif e.kind == "exit":
            lo, hi = _ibox(e.bounds)
            p = {"classname": "trigger_changelevel", "map": "start"}
            out.append(_entity(p, [brush_text(lo, hi, *(["trigger"] * 3))]))
            continue
        else:
            p = {"classname": "info_null", "origin": origin, "_badlevel_kind": e.kind}
        for k, v in e.props.items():
            p.setdefault(f"_badlevel_{k}", v)
        out.append(_entity(p))
    anchor = _hull_anchor(level)
    if anchor:
        out.append(_entity({"classname": "info_null", "origin": " ".join(_fmt(v) for v in anchor),
                            "_badlevel": "hull fill anchor (removed at spawn)"}))
    return "\n".join(out) + "\n"


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
