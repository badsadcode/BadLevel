"""Flooded crypts and sumps (floods.py): wading water to a natural shore, swims on one breath."""
import pytest

from badlevel import generate
from badlevel.export import to_map
from badlevel.floods import MAX_SWIM, WADE, breath_report
from badlevel.recipes import PRESETS, compile_recipe


@pytest.fixture(scope="module")
def levels():
    return {seed: generate(compile_recipe(PRESETS["dungeon"], seed)) for seed in (2, 5, 6)}


def test_wading_water(levels):
    floods = [(lv, r) for lv in levels.values() for r in lv.rooms if "flood" in r.meta]
    assert floods
    for lv, r in floods:
        S = r.meta["flood"]["surface"]
        assert S == r.floor + WADE
        assert WADE < lv.config.player_height - 10        # under the eyes: nobody drowns wading
        for c, s_ in lv.water.items():
            if s_ == S and lv.spans[c][0][0] >= r.floor:
                assert lv.spans[c][0][0] < S              # water only where the floor is under it


def test_water_has_a_shore(levels):
    """Water never stands against open air: every open neighbour of a wet cell (at the water's
    height) is wet too, or its floor is above the surface."""
    for lv in levels.values():
        for c, S in lv.water.items():
            f0, c0 = lv.spans[c][0]
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                n = (c[0] + dx, c[1] + dy)
                if n in lv.water or n not in lv.spans or n in lv.glass:
                    continue
                f1, c1 = lv.spans[n][0]
                assert f1 >= S or min(c0, c1) <= max(f0, f1), (c, n)


def test_sumps_are_swims_on_one_breath(levels):
    sumps = [(lv, c) for lv in levels.values() for c in lv.corridors if getattr(c, "sump", False)]
    assert sumps
    for lv, corr in sumps:
        cells = [c for c, p in lv.part_cells.items() if p == f"corridor:{corr.id}/sump"]
        noair = [c for c in cells if lv.spans[c][0][1] - lv.water[c] < 12]
        assert noair                                      # a real swim: no air at the bottom
        assert all(abs(a - b) <= lv.config.step_height for a, b in zip(corr.floors, corr.floors[1:]))
    for lv in levels.values():
        assert not breath_report(lv) and lv.report["ok"]


def test_breath_check_catches_a_long_swim(levels):
    lv = levels[5]
    corr = next(c for c in lv.corridors if getattr(c, "sump", False))
    x0, y = corr.path[0]
    saved = dict(lv.water)
    # pretend a long flooded stretch with no air
    fake = [(x0 + 1000 + i, y) for i in range(MAX_SWIM + 3)]
    for c in fake:
        lv.spans[c] = [(0, 64)]
        lv.water[c] = 64
    try:
        assert breath_report(lv)
    finally:
        for c in fake:
            del lv.spans[c]
        lv.water.clear()
        lv.water.update(saved)


def test_water_export(levels):
    lv = levels[5]
    q1, q2 = to_map(lv, "quake"), to_map(lv, "quake2")
    assert "*water0" in q1 and "badlevel/water" in q2


def test_dry_elsewhere():
    lv = generate(compile_recipe(PRESETS["classic"], 2))
    assert not lv.water


def test_water_in_any_recipe(tmp_path):
    """Tom: "it fits any game". The flags work on every recipe, not only dungeons."""
    import json

    from badlevel.cli import main as cli_main
    for name in ("runekeep", "facility"):
        out = tmp_path / name
        assert cli_main(["generate", "--recipe", name, "--seed", "3", "--floods", "0.6", "--sumps", "0.6",
                         "--format", "json", "--out", str(out)]) in (0, None)
        level = json.load(open(out / "badlevel_3.json"))
        recipe = json.load(open(out / "badlevel_3.recipe.json"))
        assert level["water"]
        assert recipe["config"]["floods"] == 0.6 if "config" in recipe else True
