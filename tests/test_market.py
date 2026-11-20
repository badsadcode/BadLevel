"""The goblin market (market.py): yards in streets of stalls, a merchant in each."""
import pytest

from badlevel import generate
from badlevel.market import GOODS, ROOF, SLAB
from badlevel.recipes import PRESETS, compile_recipe


@pytest.fixture(scope="module")
def levels():
    return {seed: generate(compile_recipe(PRESETS["market"], seed)) for seed in (1, 2)}


def test_markets_have_stalls_and_merchants(levels):
    for lv in levels.values():
        assert lv.report["ok"]
        markets = [r for r in lv.rooms if r.meta.get("stalls")]
        assert markets
        merchants = [e for e in lv.entities if e.kind == "merchant"]
        assert len(merchants) == sum(len(r.meta["stalls"]) for r in markets) >= 10
        assert {e.props["sells"] for e in merchants} <= set(GOODS)
        assert "teeth (no explanation)" in GOODS                 # Noor's list, kept


def test_a_stall_is_counter_booth_roof(levels):
    lv = levels[1]
    m = next(r for r in lv.rooms if r.meta.get("stalls"))
    f = m.floor
    shapes = {tuple(lv.spans.get(c, [])) for c, p in lv.part_cells.items() if p.startswith(f"room:{m.id}/stall ")}
    assert ((f + 40, f + ROOF), (f + ROOF + SLAB, m.ceil)) in shapes       # the counter under the awning
    assert ((f, f + ROOF), (f + ROOF + SLAB, m.ceil)) in shapes            # the booth under the roof
    for (a, b) in shapes:
        assert a[1] - a[0] >= lv.config.player_height                      # headroom everywhere


def test_merchants_stand_in_their_booths(levels):
    for lv in levels.values():
        for e in (e for e in lv.entities if e.kind == "merchant"):
            c = (int(e.pos[0] // lv.cell), int(e.pos[1] // lv.cell))
            assert lv.part_cells.get(c, "").endswith(f"/stall {e.props['stall']}")


def test_no_market_elsewhere():
    lv = generate(compile_recipe(PRESETS["compound"], 1))
    assert not any(e.kind == "merchant" for e in lv.entities)
