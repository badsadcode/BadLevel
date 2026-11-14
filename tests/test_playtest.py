"""Tom's playtest in the Quake rerelease: stuck in passes too narrow, too many monsters, too dark."""
import re

import pytest

from badlevel import clearance as C
from badlevel import generate
from badlevel import generator as G
from badlevel.export import to_map
from badlevel.recipes import PRESETS, compile_recipe, roomier, varied


@pytest.mark.parametrize("preset", ["bunker", "fortress", "industrial", "compound"])
def test_the_player_box_reaches_every_room(preset):
    for seed in (1, 2, 3):
        lv = generate(compile_recipe(PRESETS[preset], seed))
        assert C.clearance_report(lv) == [], (preset, seed)


def test_widening_never_joins_new_spaces(monkeypatch):
    seen = []
    real = G.widen_pinches

    def spy(level):
        doors = C._door_cells(level)
        before = C._joined(level, doors)
        n = real(level)
        seen.append((n, before, C._joined(level, doors)))
        return n
    monkeypatch.setattr(G, "widen_pinches", spy)
    for seed in (1, 2):
        generate(compile_recipe(PRESETS["bunker"], seed))
    assert any(n for n, _, _ in seen), "bunker levels have squeezes to open"
    for n, before, after in seen:
        assert after <= before


def test_without_widening_the_box_gets_stuck(monkeypatch):
    """The squeezes are real: switch the repair off and the bunker has rooms the box can't reach."""
    import dataclasses
    monkeypatch.setattr(G, "widen_pinches", lambda level: 0)
    cfg = compile_recipe(dataclasses.replace(PRESETS["bunker"], spaciousness=0.0), 1)   # the tight original
    cfg.check_clearance = False
    assert C.clearance_report(generate(cfg))


def test_monsters_thin_out():
    def count(cfg):
        return sum(e.kind == "enemy" for e in generate(cfg).entities)
    cfg = compile_recipe(PRESETS["fortress"], 6)
    roomier(cfg, 0.8)
    varied(cfg, 1.0)
    assert count(cfg) < 100                     # was 132 when Tom played it
    half = compile_recipe(PRESETS["fortress"], 6)
    half.enemy_density *= 0.5
    assert count(half) < count(compile_recipe(PRESETS["fortress"], 6))


def test_quake_skill_levels_and_light():
    m = to_map(generate(compile_recipe(PRESETS["fortress"], 2)))
    monsters = re.findall(r'"classname" "monster_\w+"', m)
    flags = re.findall(r'"spawnflags" "(256|768)"', m)
    assert 0.4 * len(monsters) < len(flags) < 0.6 * len(monsters)   # easy: 1/2, normal: 3/4
    assert '"wait" "0.5"' in m and '"light" "24"' in m


def test_secrets_count_in_quake():
    lv = generate(compile_recipe(PRESETS["classic"], 1))
    secrets = [r for r in lv.rooms if r.kind == "secret"]
    assert secrets
    assert to_map(lv).count('"classname" "trigger_secret"') == len(secrets)
