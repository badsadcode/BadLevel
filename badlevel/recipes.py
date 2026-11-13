"""Recipes: what kind of place to generate, said in design terms.

A recipe is a few *dials* (0..1) plus optional *overrides*. The dials describe
intent; ``compile_recipe`` translates them into the generator's low-level
settings (GenConfig). Overrides are raw GenConfig fields and always win, so a
preset can pin down what makes it recognisable (a bunker has firing slits and
no glass, a fortress climbs by stairwells, an industrial complex uses lifts).

Dials:
  scale           how much level: room count and room sizes
  openness        merged halls, windows and glass, wide corridors
  verticality     height variation, storeys, platforms / pits / balconies, tall rooms
  claustrophobia  low ceilings, narrow corridors, few openings
  combat          enemies and cover. 0 = no combat at all (exploration, architecture, survival)
  exploration     side rooms, secrets, windows that preview what's ahead, pickups
  loopiness       loops and alternative routes
  regularity      architectural order: rectangles and right angles (1) vs octagons,
                  rings and 45-degree corridors (0)
  spaciousness    room to move at fast-FPS speed: bigger rooms, wider corridors, higher
                  ceilings, more headroom kept under vaults and beams, less clutter.
                  0 (default) leaves every preset exactly as it was
  variety         a wider spread of room sizes (closets to halls in one level) and more
                  footprint shapes (round, hex, diamond, Z, H), and domes in big rooms.
                  0 (default) changes nothing

Recipes are JSON files (see ``save_recipe``), kept beside the exported level.
"""
from __future__ import annotations

import json
from dataclasses import asdict, dataclass, field
from typing import Dict

from .config import GenConfig

DIALS = ("scale", "openness", "verticality", "claustrophobia", "combat", "exploration", "loopiness", "regularity",
         "spaciousness", "variety")


@dataclass
class Recipe:
    name: str = "classic"
    description: str = ""
    scale: float = 0.5
    openness: float = 0.45
    verticality: float = 0.4
    claustrophobia: float = 0.3
    combat: float = 0.6
    exploration: float = 0.5
    loopiness: float = 0.5
    regularity: float = 0.35
    spaciousness: float = 0.0     # 0 = the presets as they are; up to 1 = roomier at fast-FPS speed
    variety: float = 0.0          # 0 = as they are; up to 1 = sizes from closets to halls, more shapes
    overrides: Dict[str, object] = field(default_factory=dict)

    def dials(self) -> Dict[str, float]:
        return {d: getattr(self, d) for d in DIALS}

    def to_dict(self) -> dict:
        return asdict(self)

    @classmethod
    def from_dict(cls, d: dict) -> "Recipe":
        dials = d.get("dials", {})
        kw = {k: v for k, v in d.items() if k in cls.__dataclass_fields__}
        kw.update({k: v for k, v in dials.items() if k in DIALS})
        return cls(**kw)


def _lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * t


def roomier(cfg: GenConfig, s: float) -> GenConfig:
    """Spaciousness: Tom found the rooms "a little bit cramped" in the Quake rerelease (fast
    movement, 320 units/s, in rooms of 384-448 units and 96-unit corridors). s = 0 changes
    nothing; s = 1 makes rooms 1.5x bigger and 1.25x higher (and storeys 1.25x taller, which cap
    room heights on multi-storey levels), corridors 1.5x higher and mostly 5 or 7 wide, keeps
    192 units of headroom under shaped ceilings instead of 112, and thins the clutter."""
    s = max(0.0, min(1.0, float(s)))
    if s <= 0:
        return cfg
    cfg.room_scale *= _lerp(1.0, 1.5, s)
    cfg.height_scale *= _lerp(1.0, 1.25, s)
    cfg.corridor_width_bias = dict(cfg.corridor_width_bias or {1: 1.0, 2: 1.0, 3: 1.0})
    cfg.corridor_width_bias[1] = cfg.corridor_width_bias.get(1, 1.0) * _lerp(1.0, 0.1, s)
    cfg.corridor_width_bias[2] = cfg.corridor_width_bias.get(2, 1.0) * _lerp(1.0, 1.5, s)
    cfg.corridor_width_bias[3] = cfg.corridor_width_bias.get(3, 1.0) * _lerp(1.0, 3.0, s)
    # on multi-storey levels the storey caps a room's height, whatever height_scale says
    cfg.storey_height = int(round(cfg.storey_height * _lerp(1.0, 1.25, s) / 32)) * 32
    cfg.corridor_height = int(round(cfg.corridor_height * _lerp(1.0, 1.5, s) / 16)) * 16   # id maps: 128-192
    cfg.crate_scale *= _lerp(1.0, 0.6, s)
    cfg.arch_headroom = int(round(_lerp(112, 192, s) / 16)) * 16
    cfg.spaciousness = s
    return cfg


def varied(cfg: GenConfig, v: float) -> GenConfig:
    """Variety: Tom wants "bigger rooms, different sizes and shapes" as options, keeping the
    Quake-1-tight default. v = 0 changes nothing; v = 1 spreads room sizes 0.66x - 2x per room
    and offers the extra footprints (round, hex, diamond, Z, H) alongside the preset's own, and
    domes in big rooms of any style (more likely over round, hex and octagonal plans)."""
    v = max(0.0, min(1.0, float(v)))
    if v <= 0:
        return cfg
    cfg.size_variety = v
    cfg.shape_weights = dict(cfg.shape_weights or {"rect": 1.0})
    for name, w in (("round", 1.5), ("hex", 1.5), ("diamond", 1.0), ("Z", 1.2), ("H", 1.2)):
        cfg.shape_weights[name] = cfg.shape_weights.get(name, 0.0) + w * v
    cfg.arch_weights = dict(cfg.arch_weights or {})
    cfg.arch_weights["dome"] = cfg.arch_weights.get("dome", 0.0) + 1.5 * v      # domes in big rooms
    return cfg


def compile_recipe(recipe: Recipe, seed: int = 1) -> GenConfig:
    """Dials -> GenConfig. Every mapping is monotonic and documented in docs/DECISIONS.md."""
    t = {d: max(0.0, min(1.0, float(getattr(recipe, d)))) for d in DIALS}
    sc, op, ve, cl, co, ex, lo, rg, sp, va = (t[d] for d in DIALS)
    cfg = GenConfig(seed=seed, recipe=recipe.name)

    # scale
    cfg.main_rooms = int(round(_lerp(3, 11, sc)))
    cfg.side_rooms = int(round(_lerp(0, 6, 0.6 * sc + 0.4 * ex)))
    cfg.keys = 0 if sc < 0.2 else (2 if sc > 0.75 else 1)
    cfg.size_bias = {"small": _lerp(2.5, 0.4, sc), "medium": 1.0, "large": _lerp(0.3, 2.5, sc)}
    cfg.room_scale = _lerp(0.85, 1.25, sc) * _lerp(1.1, 0.8, cl) * _lerp(0.95, 1.15, op)

    # verticality
    cfg.storeys = 1 if ve < 0.5 else 2 if ve < 0.7 else 3 if ve < 0.9 else 4
    cfg.max_level = int(round(_lerp(0, 6, ve)))
    cfg.tall_room_chance = _lerp(0.0, 0.7, ve)
    cfg.galleries = ve >= 0.5
    vert_features = _lerp(0.2, 2.5, ve)

    # openness and claustrophobia (they pull against each other)
    cfg.merge_chance = _lerp(0.0, 0.6, op) * (1 - cl)
    cfg.windows = int(round(_lerp(0, 9, op) * (1 - 0.8 * cl) + 3 * ex * (1 - cl)))
    cfg.glazed_chance = _lerp(0.1, 0.7, op) * (1 - cl)
    cfg.open_areas = 0 if op < 0.65 or cl > 0.6 else 1 if op < 0.85 else 2
    cfg.mega_rooms = 1 if sc >= 0.75 and cl < 0.6 else 0
    cfg.height_scale = _lerp(1.25, 0.72, cl)
    cfg.corridor_width_bias = {1: _lerp(3.0, 0.5, op) * _lerp(1, 3, cl), 2: 1.0,
                               3: _lerp(0.2, 3.0, op) * _lerp(1, 0.1, cl)}

    # combat (0 = none) and exploration
    cfg.enemy_density = _lerp(0.0, 1.6, co)
    cfg.crate_scale = _lerp(0.3, 1.8, co)
    cfg.pickup_density = _lerp(0.6, 1.0, co) + 0.6 * ex
    cfg.secret = ex >= 0.25

    # loops
    cfg.loops = int(round(_lerp(0, 5, lo)))
    cfg.long_loop_chance = _lerp(0.1, 0.4, lo)

    # regularity
    cfg.shape_weights = {"rect": _lerp(0.5, 6.0, rg), "chamfer": _lerp(1.5, 1.0, rg),
                         "octagon": _lerp(2.5, 0.15, rg), "ring": _lerp(2.0, 0.1, rg),
                         "plus": _lerp(1.5, 0.6, rg), "L": _lerp(1.2, 0.8, rg),
                         "T": _lerp(1.2, 0.6, rg), "U": _lerp(1.2, 0.5, rg)}
    cfg.diagonal_chance = _lerp(0.8, 0.0, rg)

    cfg.feature_weights = {"platform": vert_features, "pit": vert_features, "balcony": vert_features,
                           "pillars": _lerp(0.6, 1.6, co), "none": _lerp(1.5, 0.6, co)}

    for k, v in recipe.overrides.items():
        if k not in GenConfig.__dataclass_fields__:
            raise ValueError(f"recipe {recipe.name}: unknown override {k!r}")
        if isinstance(v, dict) and isinstance(getattr(cfg, k), dict):
            merged = dict(getattr(cfg, k))
            merged.update(v)
            v = merged
        setattr(cfg, k, v)
    roomier(cfg, sp)                 # after the overrides: it scales whatever the preset chose
    varied(cfg, va)
    # decided last, from what the level ends up containing (overrides may add big spaces):
    # big spaces get a compressed approach, and the level is organised around them
    big = cfg.mega_rooms > 0 or cfg.open_areas > 0
    if "pacing_contrast" not in recipe.overrides:
        cfg.pacing_contrast = big
    if "composition" not in recipe.overrides:
        cfg.composition = "central" if big else "tree"
    return cfg


# ----------------------------------------------------------------------
# presets: each must be recognisable at a glance (tests/test_recipes.py checks it)
# ----------------------------------------------------------------------
PRESETS: Dict[str, Recipe] = {r.name: r for r in [
    Recipe("compact", "A few small rooms and short corridors. Restrained, believable, quick to play.",
           scale=0.1, openness=0.3, verticality=0.2, claustrophobia=0.4, combat=0.5, exploration=0.2,
           loopiness=0.2, regularity=0.6, overrides={"windows": 1, "max_level": 1, "composition": "linear"}),
    Recipe("classic", "Quake-style: varied rooms, height changes, loops, a key and a secret.",
           scale=0.5, openness=0.45, verticality=0.45, claustrophobia=0.3, combat=0.6, exploration=0.5,
           loopiness=0.5, regularity=0.35),
    Recipe("bunker", "Underground: in through a hatch from the surface and down, storey by storey. Low, "
                     "narrow, orthogonal concrete rooms, firing slits, ladders and lifts, and at the bottom one "
                     "deep chamber cut from the rock. Tense and close.",
           scale=0.45, openness=0.1, verticality=0.55, claustrophobia=0.9, combat=0.8, exploration=0.3,
           loopiness=0.3, regularity=0.95,
           overrides={"arch_style": "bunker", "underground": True, "storeys": 3, "mega_rooms": 1,
                      "mega_styles": {"hangar": 2.0, "works": 1.0, "terraces": 0.0}, "glazed_chance": 0.0, "windows": 4, "tall_room_chance": 0.0, "room_scale": 0.75,
                      "size_bias": {"small": 2.5, "medium": 1.0, "large": 0.15},
                      "connector_weights": {"stairs": 0.15, "stairwell": 0.25, "lift": 0.3, "ladder": 0.3},
                      "feature_weights": {"balcony": 0.0}, "merge_chance": 0.05, "composition": "linear"}),
    Recipe("fortress", "Tall, ordered, defensive. An inner courtyard, several storeys climbed by "
                       "stairwells, great halls, galleries and arrow slits.",
           scale=0.7, openness=0.35, verticality=0.92, claustrophobia=0.25, combat=0.8, exploration=0.4,
           loopiness=0.45, regularity=0.9,
           overrides={"arch_style": "gothic", "glazed_chance": 0.0, "windows": 8, "height_scale": 1.15, "merge_chance": 0.4, "open_areas": 1,
                      "connector_weights": {"stairs": 0.25, "stairwell": 0.65, "lift": 0.05, "ladder": 0.05},
                      "feature_weights": {"balcony": 2.5, "pillars": 1.5},
                      "corridor_width_bias": {1: 0.5, 2: 2.0, 3: 1.5},
                      "mega_rooms": 1, "mega_styles": {"terraces": 3.0, "hangar": 0.5, "works": 0.0}}),
    Recipe("industrial", "Big tall halls with platforms and catwalk balconies, a loading yard, lifts "
                         "and ladders, wide service corridors, control-room glass, lots of clutter.",
           scale=0.8, openness=0.6, verticality=0.75, claustrophobia=0.15, combat=0.6, exploration=0.5,
           loopiness=0.6, regularity=0.7,
           overrides={"arch_style": "industrial", "height_scale": 1.35, "glazed_chance": 0.65, "windows": 8, "crate_scale": 2.0, "room_scale": 1.4,
                      "open_areas": 1,
                      "size_bias": {"small": 0.5, "medium": 1.0, "large": 3.0},
                      "connector_weights": {"stairs": 0.1, "stairwell": 0.25, "lift": 0.35, "ladder": 0.3},
                      "feature_weights": {"platform": 2.5, "balcony": 2.5, "pillars": 2.0},
                      "corridor_width_bias": {1: 0.4, 2: 1.0, 3: 2.5}, "diagonal_chance": 0.2,
                      "mega_rooms": 1, "mega_styles": {"works": 3.0, "hangar": 1.0, "terraces": 0.0},
                      "composition": "hub"}),
    Recipe("compound", "Open and spread out: courtyards under the sky, large merged halls, wide "
                       "connections, many windows, one level.",
           scale=0.7, openness=0.95, verticality=0.2, claustrophobia=0.0, combat=0.6, exploration=0.6,
           loopiness=0.8, regularity=0.55,
           overrides={"arch_style": "compound", "open_roofscape": True, "open_buildings": 0.9, "yard_setback": 4, "yard_height": 320, "merge_chance": 0.65, "max_hall_rooms": 4, "windows": 10, "glazed_chance": 0.5,
                      "height_scale": 1.4, "crate_scale": 1.8, "room_scale": 1.45,
                      "size_bias": {"small": 0.4, "medium": 1.0, "large": 2.5},
                      "corridor_width_bias": {1: 0.3, 2: 1.0, 3: 3.0},
                      "mega_rooms": 1, "mega_styles": {"hangar": 3.0, "works": 0.5, "terraces": 0.0}}),
    Recipe("explore", "No combat. Vertical, open, full of side rooms, secrets, views ahead and loops: "
                      "for exploration, walking sims and architectural blockouts.",
           scale=0.6, openness=0.6, verticality=0.7, claustrophobia=0.2, combat=0.0, exploration=1.0,
           loopiness=0.6, regularity=0.35,
           overrides={"mega_rooms": 1, "mega_styles": {"terraces": 2.0, "works": 1.0, "hangar": 0.5},
                      "composition": "hub"}),
]}


def get_recipe(name_or_path: str) -> Recipe:
    if name_or_path in PRESETS:
        return PRESETS[name_or_path]
    with open(name_or_path) as fh:
        return Recipe.from_dict(json.load(fh))


def save_recipe(recipe: Recipe, path: str, config: GenConfig = None):
    data = {"format": "badlevel-recipe", "version": 1, "name": recipe.name, "description": recipe.description,
            "dials": recipe.dials(), "overrides": recipe.overrides}
    if config is not None:
        data["compiled"] = config.to_dict()
    with open(path, "w") as fh:
        json.dump(data, fh, indent=1, default=str)
