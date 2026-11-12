"""Binary glTF 2.0 (.glb) export, no dependencies.

* Y-up, metres: 1 grid cell (32 map units) = 1 m.
* One mesh "level_geometry" with a primitive per material
  (bl_floor / bl_wall / bl_ceiling), world-aligned UVs.
* Doors and the exit trigger are separate box meshes.
* Every gameplay entity is an empty node named ``<kind>_<n>`` whose
  ``extras`` carry the kind and properties -- engine importers can swap
  them for native prefabs/scenes/actors (Godot, Unity and Unreal all keep
  node names, and Godot/Unity expose glTF extras).
"""
from __future__ import annotations

import json
import struct
from typing import List

from ..level import Level
from .mesh import MeshBuilder, build_mesh

SCALE = 1.0 / 32.0

MATERIALS = {
    "floor": ("bl_floor", [0.55, 0.50, 0.44, 1.0]),
    "wall": ("bl_wall", [0.72, 0.72, 0.74, 1.0]),
    "ceiling": ("bl_ceiling", [0.38, 0.40, 0.45, 1.0]),
    "door": ("bl_door", [0.80, 0.25, 0.20, 1.0]),
    "door_blue": ("bl_door_blue", [0.20, 0.35, 0.85, 1.0]),
    "secret": ("bl_secret", [0.60, 0.45, 0.80, 1.0]),
    "trigger": ("bl_trigger", [0.20, 0.90, 0.40, 0.45]),
    "lift": ("bl_lift", [0.95, 0.75, 0.15, 1.0]),
    "glass": ("bl_glass", [0.55, 0.80, 0.95, 0.30]),
    "sky": ("bl_sky", [0.45, 0.62, 0.90, 1.0]),
    "ladder": ("bl_ladder", [0.55, 0.35, 0.20, 1.0]),
}


def to_yup(p):
    x, y, z = p
    return (x * SCALE, z * SCALE, -y * SCALE)


def nrm_yup(n):
    x, y, z = n
    return (x, z, -y)


class _Glb:
    def __init__(self):
        self.bin = bytearray()
        self.views: List[dict] = []
        self.accessors: List[dict] = []

    def _view(self, data: bytes, target=None) -> int:
        while len(self.bin) % 4:
            self.bin.append(0)
        v = {"buffer": 0, "byteOffset": len(self.bin), "byteLength": len(data)}
        if target:
            v["target"] = target
        self.bin.extend(data)
        self.views.append(v)
        return len(self.views) - 1

    def floats(self, rows, kind: str, minmax=False) -> int:
        flat = [c for r in rows for c in r]
        view = self._view(struct.pack(f"<{len(flat)}f", *flat), 34962)
        acc = {"bufferView": view, "componentType": 5126, "count": len(rows), "type": kind}
        if minmax:
            n = len(rows[0])
            acc["min"] = [min(r[i] for r in rows) for i in range(n)]
            acc["max"] = [max(r[i] for r in rows) for i in range(n)]
        self.accessors.append(acc)
        return len(self.accessors) - 1

    def indices(self, idx) -> int:
        view = self._view(struct.pack(f"<{len(idx)}I", *idx), 34963)
        self.accessors.append({"bufferView": view, "componentType": 5125, "count": len(idx), "type": "SCALAR"})
        return len(self.accessors) - 1


def _primitives(glb: _Glb, mb: MeshBuilder, mat_index) -> list:
    prims = []
    for mat, g in sorted(mb.groups.items()):
        if not g["idx"]:
            continue
        pos = [to_yup(p) for p in g["pos"]]
        nrm = [nrm_yup(n) for n in g["nrm"]]
        prims.append({
            "attributes": {
                "POSITION": glb.floats(pos, "VEC3", minmax=True),
                "NORMAL": glb.floats(nrm, "VEC3"),
                "TEXCOORD_0": glb.floats(g["uv"], "VEC2"),
            },
            "indices": glb.indices(g["idx"]),
            "material": mat_index[mat],
        })
    return prims


def to_glb(level: Level) -> bytes:
    glb = _Glb()
    materials, mat_index = [], {}
    for key, (name, color) in MATERIALS.items():
        m = {"name": name, "pbrMetallicRoughness": {"baseColorFactor": color, "metallicFactor": 0.0,
                                                    "roughnessFactor": 0.9}}
        if color[3] < 1:
            m["alphaMode"] = "BLEND"
            m["doubleSided"] = True
        mat_index[key] = len(materials)
        materials.append(m)

    meshes = [{"name": "level_geometry", "primitives": _primitives(glb, build_mesh(level), mat_index)}]
    nodes = [{"name": "level_geometry", "mesh": 0}]
    children = [0]
    counts = {}
    for e in level.entities:
        n = counts.get(e.kind, 0)
        counts[e.kind] = n + 1
        node = {"name": f"{e.kind}_{n}", "translation": list(to_yup(e.pos)),
                "extras": {"kind": e.kind, **{k: v for k, v in e.props.items()}}}
        if e.kind == "window" and not e.props.get("glazed"):
            node["extras"]["bounds"] = [list(e.bounds[0]), list(e.bounds[1])]
            nodes.append(node)
            children.append(len(nodes) - 1)
            continue
        if e.bounds:
            mat = {"window": "glass","door": "door_blue" if e.props.get("key") == "blue" else "door",
                   "secret_door": "secret", "exit": "trigger", "lift": "lift", "ladder": "ladder"}.get(e.kind, "door")
            mb = MeshBuilder()
            lo, hi = e.bounds
            c = e.pos
            mb.box(mat, tuple(a - b for a, b in zip(lo, c)), tuple(a - b for a, b in zip(hi, c)))
            meshes.append({"name": node["name"], "primitives": _primitives(glb, mb, mat_index)})
            node["mesh"] = len(meshes) - 1
            node["extras"]["bounds"] = [list(lo), list(hi)]
        nodes.append(node)
        children.append(len(nodes) - 1)
    nodes.append({"name": f"BadLevel_{level.seed}", "children": children,
                  "extras": {"generator": "BadLevel", "seed": level.seed}})

    doc = {
        "asset": {"version": "2.0", "generator": "BadLevel"},
        "scene": 0,
        "scenes": [{"nodes": [len(nodes) - 1]}],
        "nodes": nodes,
        "meshes": meshes,
        "materials": materials,
        "buffers": [{"byteLength": len(glb.bin)}],
        "bufferViews": glb.views,
        "accessors": glb.accessors,
    }
    js = json.dumps(doc, separators=(",", ":")).encode()
    js += b" " * ((4 - len(js) % 4) % 4)
    binc = bytes(glb.bin) + b"\0" * ((4 - len(glb.bin) % 4) % 4)
    total = 12 + 8 + len(js) + 8 + len(binc)
    return (struct.pack("<III", 0x46546C67, 2, total)
            + struct.pack("<II", len(js), 0x4E4F534A) + js
            + struct.pack("<II", len(binc), 0x004E4942) + binc)
