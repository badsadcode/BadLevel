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
        # everything but crate tops (jump-only) is reachable on foot
        assert report["reachable_fraction"] >= 0.97, seed


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
    shapes = {r.shape for lv in levels.values() for r in lv.rooms}
    assert {"octagon", "L", "ring", "plus"} <= shapes, shapes
    assert sum(len(lv.diag) for lv in levels.values()) > 100, "45-degree walls expected"


@pytest.fixture(scope="module")
def tall_levels():
    return {s: generate(GenConfig(seed=s, storeys=3)) for s in range(12)}


@pytest.mark.parametrize("which", ["single", "stacked"])
def test_brushes_exactly_fill_the_solid_space(levels, tall_levels, which):
    """Brush columns + empty spans tile [zmin, zmax] with no gaps or overlaps -> sealed."""
    pool = levels if which == "single" else tall_levels
    level = max(pool.values(), key=lambda lv: len(lv.diag) + lv.stats()["multi_span_cells"])
    assert level.diag
    zmin, zmax = level.z_range()
    s = level.cell
    per_cell = {}
    prisms = {}
    for b in build_brushes(level):
        if b.corner:
            prisms[(b.mins[0] // s, b.mins[1] // s)] = b
            continue
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
    # every 45-degree cell has exactly one prism, filling its open span height
    assert set(prisms) == set(level.diag)
    for c, b in prisms.items():
        assert (b.mins[2], b.maxs[2]) == level.spans[c][0] and b.corner == level.diag[c]


def _qbsp_normal(p0, p1, p2):
    t1 = [p0[i] - p1[i] for i in range(3)]
    t2 = [p2[i] - p1[i] for i in range(3)]
    return [t1[1] * t2[2] - t1[2] * t2[1], t1[2] * t2[0] - t1[0] * t2[2], t1[0] * t2[1] - t1[1] * t2[0]]


def test_map_file_is_well_formed(levels):
    text = to_map(max(levels.values(), key=lambda lv: len(lv.diag)))
    assert text.count("{\n(") > 50
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
        assert len(planes) in (5, 6)  # boxes and 45-degree prisms
        verts = [p for p, _ in planes]
        for point, n in planes:
            # convex + outward normals: every brush vertex lies behind (or on) every plane
            assert any(n)
            for q in verts:
                assert sum(n[i] * (q[i] - point[i]) for i in range(3)) <= 0


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


def test_corridor_widths_and_diagonals(levels):
    corridors = [c for lv in levels.values() for c in lv.corridors]
    assert {c.radius for c in corridors} >= {1, 2, 3}, "3, 5 and 7 wide corridors"
    assert any(c.diagonal for c in corridors), "some corridors run at 45 degrees"
    for lv in levels.values():
        for c in lv.corridors:
            if c.kind == "secret":
                assert c.radius == 1, "secret passages stay tight"
        # every corridor half cell has its two open sides on flat cells of the same corridor
        for cell, corner in lv.diag.items():
            owner = lv.owner_of(cell, lv.spans[cell][0])
            if owner and owner.startswith("corridor:"):
                from badlevel.shapes import SOLID_SIDES
                for dx, dy in [(1, 0), (-1, 0), (0, 1), (0, -1)]:
                    n = (cell[0] + dx, cell[1] + dy)
                    if (dx, dy) in SOLID_SIDES[corner]:
                        # solid side: faces solid, another half cell, or a window cut later
                        assert n not in lv.spans or n in lv.diag or all(
                            (lv.owner_of(n, sp) or "").startswith("window:") for sp in lv.spans[n])
                    else:
                        assert lv.owner_of(n, lv.spans[n][0]) == owner
                        assert abs(lv.spans[n][0][0] - lv.spans[cell][0][0]) <= lv.config.step_height


def test_bigger_levels():
    level = generate(GenConfig(seed=99, main_rooms=10, side_rooms=5, loops=3, keys=2))
    assert len(level.rooms) >= 15
    assert validate(level)["ok"]


def test_cli(tmp_path):
    assert cli_main(["gallery", "--seeds", "1-2", "--out", str(tmp_path)]) == 0
    for ext in ("map", "glb", "json", "html"):
        assert (tmp_path / f"badlevel_1.{ext}").exists()
    assert (tmp_path / "index.html").exists()


# ---------------------------------------------------------------- storeys
def test_single_storey_stays_classic(levels):
    for lv in levels.values():
        st = lv.stats()
        assert st["storeys"] == 1 and not lv.links and not any(st["connectors"].values())


def test_multi_storey_levels(tall_levels):
    kinds, drops, stacked_rooms = set(), 0, 0
    for seed, lv in tall_levels.items():
        assert validate(lv)["ok"], (seed, validate(lv)["errors"])
        st = lv.stats()
        assert st["storeys"] >= 2
        kinds |= {k for k, v in st["connectors"].items() if v}
        drops += st["drops"]
        rooms = [r for r in lv.rooms if not r.is_connector]
        for a in rooms:
            for b in rooms:
                if a.id < b.id and a.storey != b.storey and set(a.cells()) & set(b.cells()):
                    stacked_rooms += 1
    assert kinds == {"stairwell", "lift", "ladder"}
    assert drops > 0, "some loops become one-way drops"
    assert stacked_rooms > 0, "rooms on different storeys stack over each other"


def test_link_semantics(tall_levels):
    links = [l for lv in tall_levels.values() for l in lv.links]
    lifts = [l for l in links if l["kind"] == "lift"]
    ladders = [l for l in links if l["kind"] == "ladder"]
    assert lifts and ladders
    assert all(l["two_way"] and l["interaction"] == "ride" and l["accessible"] for l in lifts)
    assert all(l["two_way"] and l["interaction"] == "climb" and not l["accessible"] for l in ladders)
    text = to_map(next(lv for lv in tall_levels.values() if any(l["kind"] == "lift" for l in lv.links)))
    assert '"classname" "func_plat"' in text


def test_drops_are_one_way(tall_levels):
    from badlevel.validate import _link_edges
    checked = 0
    for lv in tall_levels.values():
        for c in lv.corridors:
            if c.connector != "drop":
                continue
            own = f"corridor:{c.id}"
            # walk only inside this corridor: top end reaches the bottom end, never the reverse
            outside = {cell for cell in lv.spans
                       if not any(lv.owner_of(cell, sp) == own for sp in lv.spans[cell])}
            top = (c.path[0][0], c.path[0][1], 0)
            bottom = (c.path[-1][0], c.path[-1][1], 0)
            top = next((x, y, i) for (x, y) in [c.path[0]] for i, sp in enumerate(lv.spans[(x, y)])
                       if lv.owner_of((x, y), sp) == own)
            bottom = next((x, y, i) for (x, y) in [c.path[-1]] for i, sp in enumerate(lv.spans[(x, y)])
                          if lv.owner_of((x, y), sp) == own)
            assert bottom in flood(lv, top, outside, {})
            assert top not in flood(lv, bottom, outside, {})
            checked += 1
    assert checked


def test_validator_catches_a_softlock():
    """A ledge you can drop off into a closed pit: the exit is still reachable from the start,
    but a player who drops in is stuck. The validator must say so."""
    from badlevel.graph import MissionGraph
    from badlevel.level import Entity, Level
    lv = Level(seed=0, cell=32, width=14, height=8, config=GenConfig(), graph=MissionGraph())
    for x in range(1, 13):
        for y in range(1, 4):
            lv.spans[(x, y)] = [(128, 320)]          # upper walkway
    for x in range(5, 9):
        for y in range(4, 7):
            lv.spans[(x, y)] = [(0, 320)]            # pit below, open to the walkway
    lv.entities.append(Entity("player_start", (2.5 * 32, 2.5 * 32, 152)))
    lv.entities.append(Entity("exit", (11.5 * 32, 2.5 * 32, 160), {}, ((11 * 32, 2 * 32, 128), (12 * 32, 3 * 32, 192))))
    report = validate(lv)
    assert not report["ok"] and any("softlock" in e for e in report["errors"])
    # give the pit a staircase back up (16-unit steps) and the softlock is gone
    for cell, f in (((9, 6), 16), ((10, 6), 32), ((11, 6), 48), ((12, 6), 64),
                    ((12, 5), 80), ((11, 5), 96), ((10, 5), 112), ((10, 4), 128)):
        lv.spans[cell] = [(f, 320)]
    assert validate(lv)["ok"], validate(lv)["errors"]
