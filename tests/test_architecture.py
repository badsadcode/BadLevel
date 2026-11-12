"""Architecture: hall cross-sections give spaces structure without breaking gameplay."""
import random

import pytest

from badlevel import GenConfig, generate
from badlevel import architecture as A
from badlevel.export import to_map
from badlevel.recipes import PRESETS, compile_recipe
from badlevel.validate import validate

PRESET_STYLE = {"fortress": "gothic", "industrial": "industrial", "bunker": "bunker", "compound": "compound"}


@pytest.fixture(scope="module")
def styled():
    return {name: [generate(compile_recipe(PRESETS[name], s)) for s in range(1, 6)] for name in PRESET_STYLE}


def shaped(lv):
    return [r for r in lv.rooms if "arch" in r.meta]


def test_off_by_default():
    lv = generate(seed=3)
    assert not shaped(lv)
    # an empty style is the same level, byte for byte
    assert to_map(generate(GenConfig(seed=3, arch_style=""))) == to_map(lv)


def test_presets_carry_their_style(styled):
    for name, style in PRESET_STYLE.items():
        assert compile_recipe(PRESETS[name], 1).arch_style == style
        for lv in styled[name]:
            assert validate(lv)["ok"]
            rooms = shaped(lv)
            assert rooms, f"{name} seed {lv.seed}: no room got a cross-section"
            assert all(r.meta["arch"]["style"] == style for r in rooms)
            # the largest space is always treated as the grand one
            assert sum(1 for r in rooms if r.meta["arch"]["tier"] == "grand") >= 1


def test_headroom_and_openings(styled):
    """Ceilings are only ever lowered, never below a walkable surface plus HEAD."""
    for levels in styled.values():
        for lv in levels:
            for r in shaped(lv):
                for c in r.cells():
                    spans = lv.room_spans(c, r)
                    if spans and spans[-1][1] < r.ceil and c not in lv.sky:
                        f, top = spans[-1]
                        assert top - f >= A.HEAD, (lv.seed, r.id, c, spans)


def test_skips_open_areas_and_connectors(styled):
    for levels in styled.values():
        for lv in levels:
            for r in lv.rooms:
                if r.kind == "courtyard" or r.is_connector:
                    assert "arch" not in r.meta


def test_variety_within_a_style(styled):
    """A palette, not a template: several profiles per style, and not one profile per level."""
    for name, levels in styled.items():
        profiles = [r.meta["arch"]["profile"] for lv in levels for r in shaped(lv)]
        assert len(set(profiles)) >= 3, (name, set(profiles))
        for lv in levels:
            ps = [r.meta["arch"]["profile"] for r in shaped(lv)]
            if len(ps) >= 4:
                assert len(set(ps)) >= 2, (name, lv.seed, ps)


def test_grand_halls_get_structure(styled):
    """Mega-rooms in the gothic and industrial presets get a real section: lowered ceilings, and
    across the seeds at least some piers (arcades, columns, pilasters)."""
    for name in ("fortress", "industrial"):
        piers = 0
        for lv in styled[name]:
            mega = next(r for r in lv.rooms if r.kind == "megaroom")
            a = mega.meta["arch"]
            assert a["tier"] == "grand" and a["shaped_cells"] > 50
            piers += a["piers"]
        assert piers > 0


def test_proportions_drive_the_structure():
    """Taller halls get wider bays and thicker piers; every profile keeps its headroom."""
    assert A.bay_size(576, 60, 0.5, 3, 12) > A.bay_size(256, 60, 0.5, 3, 12)
    assert A.pier_size(512) > A.pier_size(256)
    for name, fn in A.PROFILES.items():
        for (w, h, height) in [(9, 12, 192), (14, 20, 320), (30, 38, 512)]:
            from badlevel.level import Room
            room = Room(0, 0, "arena", 0, 0, w, h, 0, height)
            sec = fn(A.Frame(room), random.Random(7))
            if sec is None:
                continue
            assert min(sec.ceil.values()) >= A.HEAD, (name, w, h, height)
            assert max(sec.ceil.values()) <= height, (name, w, h, height)
            for group in sec.piers:
                assert all(0 <= u < sec.frame.L and 0 <= v < sec.frame.W for u, v in group), name


def test_undercroft_stays_low():
    from badlevel.level import Room
    tall = Room(0, 0, "megaroom", 0, 0, 30, 30, 0, 512)
    assert A.undercroft(A.Frame(tall), random.Random(1)) is None


def test_weights_steer_the_palette():
    cfg = compile_recipe(PRESETS["fortress"], 2)
    cfg.arch_weights = {"vault": 0.0, "single-aisle": 0.0, "undercroft": 0.0}
    lv = generate(cfg)
    mega = next(r for r in lv.rooms if r.kind == "megaroom")
    assert mega.meta["arch"]["profile"] == "basilica"
    assert not any(r.meta["arch"]["profile"] == "vault" for r in shaped(lv))
