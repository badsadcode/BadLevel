# Roadmap and notes

Ideas agreed with Tom and Noor, for later. Not commitments, and not in any order.

## Moving into NoorMD as **NoorArchitect**

BadLevel will become a bench in NoorMD (github.com/badsadcode/NoorMD) under the name
**NoorArchitect**, once map generation is finished: there are more plans after that. Notes
for the move:
- Exact replication between the Python generator and the NoorMD version is not required.
- A design doc for NoorArchitect is to be written when BadLevel is mature.
- What should carry over: the engine-neutral IR (JSON), named replaceable parts (D27),
  the analysis (exposure, sightlines, landmarks, diagnostics), recipes and styles as data,
  and the review practice (fixed seeds, rule-based cameras, before/after showcases).

## Export targets for older engines

BadLevel exports Quake `.map` (+ `badlevel.wad`), `.glb` and JSON IR. Worth adding later, for
people using older engines:
- **Source** (`.vmf`): brushes map well (convex solids, displacements not needed); entities,
  ladders (`func_ladder` exists), named groups as visgroups.
- **Other id Tech `.map` dialects**: Quake 2 (surface flags, ladders as CONTENTS_LADDER),
  Quake 3 (patches could make real curved vaults and arches), Half-Life / GoldSrc (Valve 220
  texture axes, `func_ladder`).
- **A strict Quake 1 target**: Quake 1 has no ladders, so ladder connectors become lifts or
  stairs and yard buildings lose their ladders; levels kept under 8192 units across (original
  protocol +-4096); palette-correct textures. Today's .map already compiles and runs in the
  Quake rerelease, and in original WinQuake when the level is small enough (D30, D31).

## Generation, still open

- Arena / multiplayer mode: no keys or exit, symmetry, spawn points, item control.
- Styles reaching courtyards (open areas are not styled yet).
- Richer wall members: buttresses that step, capitals, string courses.
- Optional curved and organic shapes, alongside the grid (an artist can already replace any
  named part in Blender; Quake 3 patches would be a native route).
- Terrain stays outside BadLevel: a ground mesh from Blender laid over the map.
- Spiral stairs as thin wedges and curved stairs along a round wall (D34 builds whole-cell
  steps; finer steps need sub-cell geometry).

## Idea: a dungeon mode (Noor's concept art, later)

Abstract spaces that still make sense, creepy and readable: you see where to walk and what can
kill you. Built mostly from what exists, on the castle style:
- **Walkways over pits**: bridges and stepping stones over a drop (spikes, water, void). Bridges
  at least 2 cells wide (the player box, D33); a fall is lethal or recoverable, never a softlock
  (the validator checks both).
- **Traps as engine-neutral markers**: swinging blade, spike pit, crusher, dart wall; each
  exporter makes its own (Quake: a func_ trap or a trigger_hurt).
- **Galleries for ambushes**: enemies on balconies over the pit room (galleries exist).
- **Skylights**: a shaft of light through a grate in a vault (yards already open to the sky).
- **Portcullis gates**: portal heads with doors that slide up (portals exist).
- **Prop markers**: cages, braziers, banners, bones, placed where an artist drops real props.
Inspired by the feel of 90s dungeon games, not a copy of any of them.
