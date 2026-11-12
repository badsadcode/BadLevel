"""Recipes: dials compile sensibly, presets generate, and presets are recognisably different."""
import json

import pytest

from badlevel import generate
from badlevel.cli import main as cli_main
from badlevel.fingerprint import fingerprint, nearest_centroid_accuracy, normalise
from badlevel.recipes import DIALS, PRESETS, Recipe, compile_recipe, get_recipe
from badlevel.validate import validate


@pytest.fixture(scope="module")
def preset_levels():
    return {name: [generate(compile_recipe(r, s)) for s in range(200, 205)] for name, r in PRESETS.items()}


def test_every_preset_generates_valid_levels(preset_levels):
    for name, levels in preset_levels.items():
        for lv in levels:
            assert validate(lv)["ok"], (name, validate(lv)["errors"])
            assert lv.config.recipe == name


def test_dials_move_settings_the_right_way():
    lo, hi = Recipe(**{d: 0.0 for d in DIALS}), Recipe(**{d: 1.0 for d in DIALS})
    a, b = compile_recipe(lo), compile_recipe(hi)
    assert a.main_rooms < b.main_rooms and a.storeys < b.storeys and a.loops < b.loops
    assert a.enemy_density == 0.0 < b.enemy_density, "combat 0 means no enemies at all"
    assert compile_recipe(Recipe(claustrophobia=1.0)).height_scale < compile_recipe(Recipe(claustrophobia=0.0)).height_scale
    assert compile_recipe(Recipe(regularity=1.0)).diagonal_chance == 0.0
    with pytest.raises(ValueError):
        compile_recipe(Recipe(overrides={"no_such_setting": 1}))


def test_presets_are_recognisable_from_their_fingerprints(preset_levels):
    labels, prints = [], []
    for name, levels in preset_levels.items():
        for lv in levels:
            labels.append(name)
            prints.append(fingerprint(lv))
    acc = nearest_centroid_accuracy(labels, normalise(prints))
    assert acc >= 0.85, f"presets blur into each other: {acc:.0%} recognised"


def test_preset_identities(preset_levels):
    f = {n: [fingerprint(lv) for lv in lvs] for n, lvs in preset_levels.items()}
    avg = lambda n, k: sum(p[k] for p in f[n]) / len(f[n])
    assert avg("compact", "rooms") <= 7, "small stays small"
    assert avg("bunker", "ceiling") < avg("classic", "ceiling") < avg("compound", "ceiling")
    assert avg("bunker", "room_area") < avg("classic", "room_area") < avg("industrial", "room_area")
    assert avg("bunker", "glass_share") == 0 and avg("fortress", "glass_share") == 0, "slits, no glass"
    assert avg("fortress", "storeys") > avg("bunker", "storeys") > avg("classic", "storeys")
    assert avg("compound", "hall_share") > avg("classic", "hall_share") > avg("bunker", "hall_share")
    assert avg("explore", "enemies_per_room") == 0, "exploration has no combat"
    assert avg("bunker", "lift_ladder_share") > avg("fortress", "lift_ladder_share")


def test_exploration_analysis_skips_combat_rules(preset_levels):
    from badlevel.analysis import analyze
    rep = analyze(preset_levels["explore"][0])
    assert rep["purpose"] == "exploration"
    combat_codes = {"single_approach", "no_repositioning", "overexposed", "dominant_high_ground", "sniper_lane"}
    assert not combat_codes & {fl["code"] for fl in rep["flags"]}


def test_recipe_files_round_trip(tmp_path):
    assert cli_main(["generate", "--seed", "3", "--recipe", "bunker", "--out", str(tmp_path),
                     "--formats", "map", "--no-analysis"]) == 0
    data = json.loads((tmp_path / "badlevel_3.recipe.json").read_text())
    assert data["name"] == "bunker" and data["compiled"]["storeys"] == 2
    again = get_recipe(str(tmp_path / "badlevel_3.recipe.json"))
    assert again.dials() == PRESETS["bunker"].dials() and again.overrides == PRESETS["bunker"].overrides
    assert cli_main(["compare", "--recipes", "compact,bunker", "--seeds", "1", "--out", str(tmp_path / "cmp"),
                     "--formats", "html", "--no-analysis"]) == 0
    assert (tmp_path / "cmp" / "index.html").exists()
