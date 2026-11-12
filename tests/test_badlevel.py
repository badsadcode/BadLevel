import json
import re
import struct

import pytest

from badlevel import GenConfig, generate
from badlevel.brushes import build_brushes
from badlevel.cli import main as cli_main
from badlevel.export import to_glb, to_html, to_json, to_map
from badlevel.validate import flood, validate

SEEDS = list(range(25))


@pytest.fixture(scope="module")
def levels():
    return {s: generate(seed=s) for s in SEEDS}


def test_every_seed_generates_a_valid_level(levels):
    for seed, level in levels.items():
        report = validate(level)
        assert report["ok"], (seed, report["errors"])
        assert report["reachable_fraction"] == 1.0, seed


def test_same_seed_same_level():
    assert to_map(generate(seed=1234)) == to_map(generate(seed=1234))
    assert to_map(generate(seed=1)) != to_map(generate(seed=2))


def test_level_has_quake_like_ingredients(levels):
    features, heights, over_under = set(), set(), 0
    for level in levels.values():
        features.update(f for r in level.rooms for f in r.features)
        heights.update(r.floor for r in level.rooms)
        over_under += level.stats()["multi_span_cells"]
        kinds = {e.kind for e in level.entities}
        assert {"player_start", "exit", "enemy", "light"} <= kinds
    assert {"pillars", "platform", "pit", "balcony"} <= features
    assert len(heights) >= 4, "rooms should sit on several floor levels"
    assert over_under > 0, "some levels should have room-over-room spaces"


def test_brushes_exactly_fill_the_solid_space(levels):
    """Brush columns + empty spans tile [zmin, zmax] with no gaps or overlaps -> sealed."""
    level = levels[3]
    zmin, zmax = level.z_range()
    s = level.cell
    per_cell = {}
    for b in build_brushes(level):
        for x in range(b.mins[0] // s, b.maxs[0] // s):
            for y in range(b.mins[1] // s, b.maxs[1] // s):
                per_cell.setdefault((x, y), []).append((b.mins[2], b.maxs[2]))
    for (x, y), spans in level.spans.items():
        for c in [(x, y), (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)]:
            intervals = sorted(per_cell.get(c, []) + level.spans.get(c, []))
            z = zmin
            for (a, b) in intervals:
                assert a == z, f"gap/overlap at {c} z={z}"
                z = b
            assert z == zmax, c


def _qbsp_normal(p0, p1, p2):
    t1 = [p0[i] - p1[i] for i in range(3)]
    t2 = [p2[i] - p1[i] for i in range(3)]
    return [t1[1] * t2[2] - t1[2] * t2[1], t1[2] * t2[0] - t1[0] * t2[2], t1[0] * t2[1] - t1[1] * t2[0]]


def test_map_file_is_well_formed(levels):
    text = to_map(levels[5])
    assert text.count("{") == text.count("}")
    assert '"classname" "worldspawn"' in text
    assert text.count('"classname" "info_player_start"') == 1
    brushes = re.findall(r"\{\n(\(.*?)\n\}", text, re.S)
    assert len(brushes) > 50
    for body in brushes:
        planes = []
        for line in body.splitlines():
            nums = [int(v) for v in re.findall(r"-?\d+", line.split(")")[0] + line.split(")")[1] + line.split(")")[2])]
            pts = [nums[0:3], nums[3:6], nums[6:9]]
            planes.append((pts[0], _qbsp_normal(*pts)))
        assert len(planes) == 6
        xs = [p[0][0] for p in planes]
        ys = [p[0][1] for p in planes]
        zs = [p[0][2] for p in planes]
        centre = [(min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2, (min(zs) + max(zs)) / 2]
        for point, n in planes:
            # every face normal must point away from the brush centre
            assert sum(n[i] * (point[i] - centre[i]) for i in range(3)) > 0


def test_glb_is_valid(levels):
    data = to_glb(levels[7])
    magic, version, length = struct.unpack_from("<III", data, 0)
    assert magic == 0x46546C67 and version == 2 and length == len(data)
    jlen, jtype = struct.unpack_from("<II", data, 12)
    assert jtype == 0x4E4F534A
    doc = json.loads(data[20:20 + jlen])
    blen, btype = struct.unpack_from("<II", data, 20 + jlen)
    assert btype == 0x004E4942
    binary = data[28 + jlen: 28 + jlen + blen]
    assert doc["buffers"][0]["byteLength"] <= len(binary)
    for v in doc["bufferViews"]:
        assert v["byteOffset"] + v["byteLength"] <= doc["buffers"][0]["byteLength"]
    names = {n["name"] for n in doc["nodes"]}
    assert "level_geometry" in names and "player_start_0" in names
    for mesh in doc["meshes"]:
        for prim in mesh["primitives"]:
            n_verts = doc["accessors"][prim["attributes"]["POSITION"]]["count"]
            acc = doc["accessors"][prim["indices"]]
            view = doc["bufferViews"][acc["bufferView"]]
            idx = struct.unpack_from(f"<{acc['count']}I", binary, view["byteOffset"])
            assert max(idx) < n_verts


def test_json_and_html_exports(levels):
    level = levels[2]
    d = json.loads(to_json(level))
    assert d["format"] == "badlevel" and d["seed"] == 2
    assert len(d["rooms"]) == len(level.rooms)
    assert d["brushes"] and d["spans"]
    page = to_html(level)
    assert "<title>BadLevel Preview</title>" in page and "three@0.160.0" in page


def test_keys_actually_gate_the_exit():
    level = generate(GenConfig(seed=11, keys=2))
    doors = [e for e in level.entities if e.kind == "door"]
    assert {d.props["key"] for d in doors} == {"red", "blue"}
    assert validate(level)["ok"]

    from badlevel.validate import _door_cells, _node_at
    start = _node_at(level, next(e for e in level.entities if e.kind == "player_start").pos)
    exit_node = _node_at(level, next(e for e in level.entities if e.kind == "exit").pos)
    blocked = set().union(*(_door_cells(level, d) for d in doors))
    assert exit_node not in flood(level, start, blocked)
    assert exit_node in flood(level, start, set())


def test_no_keys_no_doors():
    level = generate(GenConfig(seed=4, keys=0, secret=False))
    kinds = [e.kind for e in level.entities]
    assert "door" not in kinds and "key" not in kinds and "secret_door" not in kinds


def test_bigger_levels():
    level = generate(GenConfig(seed=99, main_rooms=10, side_rooms=5, loops=3, keys=2))
    assert len(level.rooms) >= 15
    assert validate(level)["ok"]


def test_cli(tmp_path):
    assert cli_main(["gallery", "--seeds", "1-2", "--out", str(tmp_path)]) == 0
    for ext in ("map", "glb", "json", "html"):
        assert (tmp_path / f"badlevel_1.{ext}").exists()
    assert (tmp_path / "index.html").exists()
