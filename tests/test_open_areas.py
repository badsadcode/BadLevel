"""Open areas: courtyards under the sky, as a plug-in space type."""
import json
import struct

import pytest

from badlevel import GenConfig, generate
from badlevel.brushes import SKY, build_brushes
from badlevel.export import to_glb, to_json, to_map
from badlevel.recipes import PRESETS, compile_recipe
from badlevel.validate import validate


@pytest.fixture(scope="module")
def yards():
    return [generate(GenConfig(seed=s, open_areas=2)) for s in range(10)] + \
           [generate(GenConfig(seed=s, open_areas=1, storeys=3)) for s in range(6)]


def test_off_by_default():
    lv = generate(seed=4)
    assert not lv.sky and not any(r.kind == "courtyard" for r in lv.rooms)


def test_courtyards_are_valid_and_open_to_the_sky(yards):
    for lv in yards:
        assert validate(lv)["ok"], validate(lv)["errors"]
        courts = [r for r in lv.rooms if r.kind == "courtyard"]
        assert courts
        for r in courts:
            open_cells = {c for c in r.cells() if c in lv.spans}
            assert open_cells and open_cells <= lv.sky
            assert all(lv.spans[c][-1][1] == r.ceil for c in open_cells), "the top of a yard is the sky"
        assert lv.sky <= set(lv.spans), "sky only over open cells"


def test_sky_reaches_every_export(yards):
    lv = yards[0]
    assert any(b.bottom == SKY for b in build_brushes(lv))
    assert " sky1 " in to_map(lv), "Quake sky texture on the faces above the yard"
    data = to_glb(lv)
    jlen = struct.unpack_from("<I", data, 12)[0]
    doc = json.loads(data[20:20 + jlen])
    assert "bl_sky" in {m["name"] for m in doc["materials"]}
    assert json.loads(to_json(lv))["sky"]


def test_yards_have_facades_and_varied_contents(yards):
    feats = [set(r.features) for lv in yards for r in lv.rooms if r.kind == "courtyard"]
    seen = set().union(*feats)
    assert "facade" in seen
    assert {"kiosk", "colonnade"} & seen
    interior = {"kiosk", "colonnade", "pit", "platform", "balcony", "gallery"}
    assert any(not (f & interior) for f in feats), "some yards are left open: breathing room"


def test_recipes_use_open_areas():
    assert compile_recipe(PRESETS["compound"]).open_areas >= 2
    assert compile_recipe(PRESETS["fortress"]).open_areas == 1
    assert compile_recipe(PRESETS["classic"]).open_areas == 0
    assert compile_recipe(PRESETS["bunker"]).open_areas == 0, "no sky in a bunker"
