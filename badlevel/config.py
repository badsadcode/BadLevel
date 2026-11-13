"""Generation parameters.

All distances are in Quake-style map units unless the name says ``cells``.
One grid cell is ``cell`` units wide (32 by default, the width of a player).
"""
from __future__ import annotations

from dataclasses import asdict, dataclass, field


@dataclass
class GenConfig:
    seed: int = 1
    variant: int = 0             # alternative layouts for the same seed (candidate selection)

    # --- mission graph -------------------------------------------------
    main_rooms: int = 6          # rooms on the critical path, incl. start and exit
    side_rooms: int = 3          # optional rooms hanging off the path
    loops: int = 2               # extra connections that create circuits
    long_loop_chance: float = 0.25  # a loop may deliberately be long (a sniper alley, a flank run)
    corridor_beats: bool = True  # break long corridors up with chambers, alcoves, vaults
    beat_spacing: int = 16       # cells between beats along a long corridor
    keys: int = 1                # 0..2 key/locked-door pairs
    secret: bool = True          # add a secret room

    # --- storeys -----------------------------------------------------------
    storeys: int = 1             # 1 = classic single-storey level; 2-5 = stacked floors
    storey_height: int = 256     # planning grid between storeys (rooms may span more)
    underground: bool = False    # in from the surface and down: start on top, open to the sky; exit deepest
    storey_change: float = 0.5   # chance the critical path changes storey at each room
    # how a change of storey is made: stair corridor, switchback stairwell, lift, ladder
    connector_weights: dict = field(default_factory=lambda: {
        "stairs": 0.3, "stairwell": 0.35, "lift": 0.2, "ladder": 0.15})
    drop_chance: float = 0.6     # loops between storeys become one-way drops when they fit
    max_drop: int = 288          # highest intentional drop (units)
    tall_room_chance: float = 0.4  # arenas / hubs reaching up into the next storey

    # --- composition ------------------------------------------------------------
    composition: str = "tree"    # tree | hub | central | linear: the macro structure of the level
    pacing_contrast: bool = False  # compression before release: low, narrow corridors into big spaces

    # --- open areas ------------------------------------------------------------
    open_areas: int = 0          # courtyards / plazas open to the sky (0 = none)
    open_buildings: float = 0.0   # chance a yard gets free-standing buildings first (compound: most yards)
    yard_height: int = 0          # fixed height of open areas (where the sky starts; 0: from the size table)
    yard_setback: int = 0         # extra cells between open areas and other rooms (room for the roofscape)
    open_roofscape: bool = False  # carve the rock around yards into stepped roofs: a skyline, not a pit
    mega_rooms: int = 0          # levels within the level (0 = none)
    mega_styles: dict = field(default_factory=dict)   # {"hangar": 1, "works": 2, "terraces": 0}

    # --- architecture ----------------------------------------------------------
    arch_style: str = ""         # "" (plain boxes) | gothic | industrial | bunker | compound: hall cross-sections
    arch_weights: dict = field(default_factory=dict)  # bias the profiles: {"basilica": 2.0, "sawtooth": 0.0}
    arch_walls: bool = True      # structure along the walls: members, recesses, end features (walls.py)
    arch_portals: bool = True    # framed corridor openings: jambs, arched / lintelled heads (portals.py)
    arch_axial: float = 0.65     # share of big halls whose end feature faces the way in (the rest: oblique)
    arch_smooth: bool = True     # vaults and roofs as sloped planes (False: 16-unit steps, a strict grid)

    # --- merged halls -------------------------------------------------------
    merge_chance: float = 0.3    # neighbouring rooms joined into one hall instead of a corridor
    max_hall_rooms: int = 3      # rooms (semantic regions) per merged hall

    # --- openings ----------------------------------------------------------
    windows: int = 5             # most windows per level (0 = none)
    glazed_chance: float = 0.4   # tall glass panes vs firing slits
    galleries: bool = True       # tall rooms get an upper-storey gallery (multi-storey only)

    # --- geometry --------------------------------------------------------
    cell: int = 32               # grid cell size in units
    step_height: int = 16        # stair step rise (Quake max step is 18)
    level_step: int = 64         # floor height quantum between rooms
    max_level: int = 4           # floors range over 0 .. max_level * level_step
    corridor_radius: int = 1     # base corridor half width in cells (1 -> 3 cells wide)
    corridor_height: int = 128   # height of a base-width corridor
    max_corridor_radius: int = 3 # widest corridors: 7 cells
    diagonal_chance: float = 0.5 # share of corridors allowed to run at 45 degrees
    room_gap: int = 4            # minimum empty cells between room rectangles

    # --- player capsule used for validation ----------------------------
    player_height: int = 56
    max_step: int = 18

    max_attempts: int = 60

    # --- style levers (set by recipes; 1.0 / empty = the built-in defaults) ----------
    size_bias: dict = field(default_factory=dict)        # {"small": 2.0, "large": 0.5}
    shape_weights: dict = field(default_factory=dict)    # {"rect": 3.0, "ring": 0.0}
    feature_weights: dict = field(default_factory=dict)  # {"pillars": 2.0, "balcony": 0.0, "none": 1.0}
    corridor_width_bias: dict = field(default_factory=dict)  # {1: 3.0, 3: 0.0} by half width
    crate_scale: float = 1.0     # crates / low walls (cover, clutter)
    enemy_density: float = 1.0   # 0 = no combat at all (exploration, architecture, survival...)
    pickup_density: float = 1.0
    height_scale: float = 1.0    # room ceiling heights (low = claustrophobic, high = monumental)
    room_scale: float = 1.0      # room footprints (cells): small and tight vs vast halls
    recipe: str = ""             # the recipe this config was compiled from (for the record)

    # free-form room size table: kind -> (min_cells, max_cells, min_h, max_h)
    room_sizes: dict = field(default_factory=lambda: {
        "small": (6, 8, 128, 160),
        "medium": (9, 12, 160, 224),
        "large": (14, 18, 256, 320),
        "yard": (16, 22, 320, 320),      # open areas: the height is where the sky starts
        "mega": (24, 32, 448, 512),      # mega-rooms
    })

    def to_dict(self) -> dict:
        return asdict(self)

    @classmethod
    def from_dict(cls, data: dict) -> "GenConfig":
        known = {k: v for k, v in data.items() if k in cls.__dataclass_fields__}
        return cls(**known)
