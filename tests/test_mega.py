"""Mega-rooms: levels within the level, with characters and reachable regions."""
import pytest

from badlevel import GenConfig, generate
from badlevel.recipes import PRESETS, compile_recipe
from badlevel.validate import _node_at, flood, validate

STYLES = ("hangar", "works", "terraces")


def only(style):
    return {s: (1.0 if s == style else 0.0) for s in STYLES}


@pytest.fixture(scope="module")
def megas():
    out = {}
    for style in STYLES:
        out[style] = [generate(GenConfig(seed=s, mega_rooms=1, mega_styles=only(style))) for s in range(8)]
    out["tall"] = [generate(GenConfig(seed=s, mega_rooms=1, storeys=3)) for s in range(4)]
    return out


def mega_of(lv):
    return next(r for r in lv.rooms if r.kind == "megaroom")


def test_off_by_default():
    assert not any(r.kind == "megaroom" for r in generate(seed=6).rooms)


def test_mega_rooms_are_valid_and_big(megas):
    for group in megas.values():
        for lv in group:
            assert validate(lv)["ok"], validate(lv)["errors"]
            m = mega_of(lv)
            assert m.w >= 20 and m.h >= 20 and m.ceil - m.floor >= 384
            assert m.meta["regions"][0]["kind"] == "floor"


def test_every_region_is_reachable(megas):
    for group in megas.values():
        for lv in group:
            start = _node_at(lv, next(e for e in lv.entities if e.kind == "player_start").pos)
            reach = flood(lv, start, set())
            m = mega_of(lv)
            for name, cells in m.meta.get("region_cells", {}).items():
                kind = next(r["kind"] for r in m.meta["regions"] if r["name"] == name)
                if kind == "structure":
                    continue                 # solid masses are landmarks / cover, not floors
                tops = [tuple(c) for c in cells]
                assert any((c[0], c[1], i) in reach for c in tops for i in range(len(lv.spans.get(c, [])))), \
                    f"{name} in a {m.meta['style']} mega-room can't be reached"


def test_characters_differ(megas):
    kinds = {style: [rg["kind"] for lv in megas[style] for rg in mega_of(lv).meta["regions"]] for style in STYLES}
    assert "catwalk" in kinds["works"] and kinds["works"].count("deck") >= 8
    assert "tier" in kinds["terraces"] and "deck" not in kinds["terraces"]
    assert "structure" in kinds["hangar"] and "deck" not in kinds["hangar"]
    # hangars are the sparse ones: fewest regions per room
    per_room = {s: len(kinds[s]) / len(megas[s]) for s in STYLES}
    assert per_room["hangar"] < per_room["works"]


def test_decks_can_be_walked_under(megas):
    for lv in megas["works"]:
        m = mega_of(lv)
        for name, cells in m.meta["region_cells"].items():
            if name.startswith("deck"):
                c = tuple(cells[0])
                spans = lv.room_spans(c, m)
                assert len(spans) == 2 and spans[0][0] == m.floor, "open floor under a deck"
                return
    pytest.fail("no deck found")


def test_recipes_use_mega_rooms():
    assert compile_recipe(PRESETS["industrial"]).mega_rooms == 1
    assert compile_recipe(PRESETS["compact"]).mega_rooms == 0
    assert compile_recipe(PRESETS["bunker"]).mega_rooms == 0
    lv = generate(compile_recipe(PRESETS["explore"], 3))
    m = mega_of(lv)
    assert not any(e.kind == "enemy" for e in lv.entities), "a mega-room is not necessarily an arena"
