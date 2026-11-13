"""Opt-in room options: spaciousness (bigger rooms, wider corridors, more headroom) and variety
(more spread of sizes, extra footprints like round and hex, domes). Both default to 0: nothing
changes, so the compact Quake-like levels stay exactly as they were."""
import collections
import math
import random
import statistics

import pytest

from badlevel import generate
from badlevel.export import to_map
from badlevel.recipes import PRESETS, compile_recipe, roomier, varied
from badlevel.shapes import EXTRA_SHAPES, make_shape
from badlevel.validate import validate


def test_dials_default_to_nothing():
    for name in ("classic", "fortress"):
        a = compile_recipe(PRESETS[name], 4)
        b = compile_recipe(PRESETS[name], 4)
        roomier(b, 0.0)
        varied(b, 0.0)
        assert to_map(generate(a)) == to_map(generate(b))
        assert a.spaciousness == 0 and a.size_variety == 0 and a.arch_headroom == 112


def _levels(space=0.0, variety=0.0, presets=("classic", "fortress", "compound"), seeds=(1, 2)):
    out = []
    for name in presets:
        for s in seeds:
            cfg = compile_recipe(PRESETS[name], s)
            roomier(cfg, space)
            varied(cfg, variety)
            lv = generate(cfg)
            assert validate(lv)["ok"], (name, s, space, variety)
            out.append(lv)
    return out


def test_spaciousness_grows_rooms():
    small, big = _levels(), _levels(space=1.0)

    def side(lvs):
        return statistics.mean((r.w * r.h) ** 0.5 for lv in lvs for r in lv.rooms)
    assert side(big) > side(small) * 1.2


def test_variety_spreads_sizes_and_shapes():
    plain, wide = _levels(), _levels(variety=1.0, seeds=(1, 2, 3))
    shapes = collections.Counter(r.shape for lv in wide for r in lv.rooms)
    assert sum(shapes[s] for s in EXTRA_SHAPES) >= 5
    assert not any(r.shape in EXTRA_SHAPES for lv in plain for r in lv.rooms
                   if r.shape in ("round", "diamond"))
    def spread(lvs):                                       # how much ordinary rooms differ in size
        return statistics.pstdev(math.log(r.w * r.h) for lv in lvs for r in lv.rooms if r.w * r.h < 600)
    assert spread(wide) > spread(plain)


def test_variety_brings_domes():
    lvs = _levels(variety=1.0, presets=("fortress", "compound", "industrial"), seeds=(1, 2, 3))
    domes = [r for lv in lvs for r in lv.rooms if r.meta.get("arch", {}).get("profile") == "dome"]
    assert domes


@pytest.mark.parametrize("name", EXTRA_SHAPES)
def test_extra_shapes_are_sound(name):
    for w, h in [(8, 8), (10, 12), (14, 14), (18, 22), (30, 30)]:
        for k in range(6):
            assert make_shape(w, h, name, random.Random(k)).ok(), (name, w, h, k)


def test_room_options_from_the_command_line(tmp_path):
    from badlevel.cli import main as cli
    assert cli(["generate", "--seed", "2", "--recipe", "classic", "--space", "0.8", "--variety", "0.8",
                "--out", str(tmp_path), "--formats", "map", "--no-analysis"]) == 0
    assert list(tmp_path.glob("*.map"))
