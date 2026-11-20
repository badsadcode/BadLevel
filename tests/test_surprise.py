"""--recipe surprise: a preset drawn by the seed, and a roll of extras on top."""
from badlevel import GenConfig, generate
from badlevel.recipes import PRESETS, compile_recipe, surprise


def test_surprise_is_deterministic_and_valid():
    for seed in range(1, 30):
        recipe, extras, label = surprise(seed)
        assert surprise(seed)[2] == label                       # same seed, same surprise
        assert recipe.name in PRESETS and label.startswith(f"surprise: {recipe.name}")
        assert all(k in GenConfig.__dataclass_fields__ for k in extras)


def test_surprises_generate(tmp_path):
    import json

    from badlevel.cli import main as cli_main
    for seed in (5, 7):                                         # a gothic facility deathmatch; a flooded classic
        assert cli_main(["generate", "--recipe", "surprise", "--seed", str(seed), "--format", "json",
                         "--out", str(tmp_path)]) in (0, None)
        d = json.load(open(tmp_path / f"badlevel_{seed}.json"))
        assert d["config"]["recipe"] == surprise(seed)[2]
