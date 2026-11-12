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
    keys: int = 1                # 0..2 key/locked-door pairs
    secret: bool = True          # add a secret room

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

    # free-form room size table: kind -> (min_cells, max_cells, min_h, max_h)
    room_sizes: dict = field(default_factory=lambda: {
        "small": (6, 8, 128, 160),
        "medium": (9, 12, 160, 224),
        "large": (14, 18, 256, 320),
    })

    def to_dict(self) -> dict:
        return asdict(self)

    @classmethod
    def from_dict(cls, data: dict) -> "GenConfig":
        known = {k: v for k, v in data.items() if k in cls.__dataclass_fields__}
        return cls(**known)
