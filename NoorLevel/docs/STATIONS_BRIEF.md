# Brief: deep-sea stations in NoorLevel (dressing markers, gameplay entities, a station layout)

> **How to use this.** Open a fresh Claude Code session **in the BadLevel repository**
> (`github.com/badsadcode/BadLevel`, C# library in `NoorLevel/`) and paste this file as the task,
> or point the session at it. It was written by Sage from the Solitude side on 2026-11-22, after
> reading NoorLevel's source. Line-level details may have drifted, so verify before editing.
> Work through the tasks in order. Each one is a separate, reviewable commit.

---

## 0. Who wants this and why

**Solitude** is a PS1-style submarine horror game in Godot 4.7 C# (repo `badsadcode/Solitude`).
Its deep-sea station interiors are NoorLevel levels: today a pre-baked bank of 12 GLBs made with the CLI
(`tools/generate_stations.ps1` in Solitude, `facility` and `bunker` presets, `underground`, no
lifts/ladders/spiral stairs, no windows). Solitude restyles them, cuts out doors, places loot,
logs, creatures and lights at runtime.

Tom's verdict: *the stations look creepy, but they're bare.* No old equipment, no clutter, no
sense that people lived and worked there. And he wants many more, and more varied, stations.

Studio rules that bind this work:

- **Projects connect through files, not each other's code.** Solitude reads NoorLevel's GLB. It
  does not link the DLL. (Section 6 discusses runtime generation within that rule.)
- **Noor Interchange** is the studio's asset contract: GLB plus `<name>.noor.json` manifest plus a
  recipe with the seed; right-handed, +Y up, metres; markers and metadata in glTF node
  `extras.noor`. Spec:
  https://github.com/badsadcode/NoorEngine/blob/main/docs/interchange/NOOR_INTERCHANGE.md.
  **Read it before touching the exporter.** If a marker you need isn't in the spec, write the
  proposal down (in `docs/DECISIONS.md` and in your report). Don't diverge silently.
- **Existing output must not change.** Every new feature is behind a config field that defaults to
  off. Section 1 says how to prove it.
- Code shall remain understandable to whichever AI inherits this mess.

## 1. Ground rules for this codebase (from reading it)

- **Pipeline:** `Generator.Generate` → `GenerateOnce`, then:
  - `MissionGraph.Build` (Graph.cs)
  - `Layout.BuildLayout` (Layout.cs: `PlaceRooms`, `SpaceType.Build`, halls, A* corridors, stairs)
  - `Decorate.Run` (Decorate.cs: programs, piers, `Walls.Articulate`, landmark, `SpaceType.Decorate`, then `Populate` for rooms and corridors)
  - Openings, Vistas, Clearance, Ramps, Floods
  - `Validator.Run`, which retries the attempt on failure

  Model: `Level` in Model.cs (cell grid of `Spans`, `Rooms`, `Corridors`, `Entities`, `PartSolid`/`PartCells`, `Water`, …). A `Room` has `Kind`, cell bounds, `Floor`/`Ceil` (units, 32 per metre), `Storey`, `Features`, and `Meta` (`program`, `sector`, `arch`).
- **Determinism.** One shared `rng` flows through graph → layout → decorate → openings. Any extra
  draw on it, including `rng.Derive(...)`, which consumes a draw, changes every seed's output.
  **Never touch the shared stream.** Use independent label streams like the existing features do:
  `Rng.FromLabel($"{cfg.Seed}/{cfg.Variant}/<feature>/{room.Id}")`.
- **Where to add things:** a **post-pass after the level validates** (or at the very end of
  `Decorate.Run`, guarded by your flag). It appends to `Level.Entities` and never edits `Spans`.
  New entity kinds appended at the end keep existing node names (`light_3`, …) stable, because
  the GLB writer numbers per kind in list order.
- **The validator rejects entities inside solid unless they have `Bounds`.** Wall- and
  ceiling-mounted markers sit at a surface: give them a small `Bounds`, or place them 2–4 units
  off the surface on the open side.
- **Regression proof (required).** There are no golden-hash tests yet. Before changing anything:
  1. Generate a fixed set of levels: presets `facility`, `bunker` and two others; seeds 1–8; `glb` and IR JSON.
  2. Record SHA-256 hashes.
  3. After each task, regenerate with every new flag **off** and prove the hashes are identical. Commit this as a test (e.g. `tests/GoldenTests.cs` with the hash list) so it stays proven.
- **Docs.** New `GenConfig` fields go in `ConfigDocs.cs`/`docs/PARAMETERS.md` (check whether a docs
  test enforces that), and a decision entry goes in `docs/DECISIONS.md`.

## 2. Task A: dressing markers (props), the main ask

**Goal:** a new pass, `Dressing.cs` (flag `dressing`, default off), that drops **prop markers**:
empty nodes saying "a thing of class X, about this big, facing this way, belongs here". The game
then puts real Blender assets there (Tom will model them over time) or, until then, a generated
placeholder from the footprint. No geometry is added by this pass.

### 2.1 Marker format (proposal; check it against the Interchange spec)

Each prop is an `Entity` with `Kind = "prop"`. In the GLB it becomes an empty node named
`prop_<n>`, carrying:

- **Node rotation** (a quaternion, yaw only). Forward is the direction the prop "faces": a
  console faces the room, a locker's door faces out. Use the spec's forward-axis convention. The
  current exporter writes no rotation at all (it only puts `angle` in extras). Add rotation for
  the new kinds only.
- **Node translation:** the prop's **floor contact point** (the centre of its footprint on the
  floor). For wall and ceiling mounts, the point on that surface.
- **`extras.noor`:**
  ```json
  { "marker": "prop", "class": "console", "mount": "floor|wall|ceiling",
    "footprint": [w, d, h], "room": 5, "program": "lab", "sector": "research",
    "variant": 3, "tags": ["broken"] }
  ```
  - `footprint` is in metres (width along the prop's right, depth along its forward, height).
  - `variant` is a small integer from the label stream, so the game can pick between asset variants deterministically.
- **Flat `extras.kind = "prop"` plus the same fields**, for consumers on today's legacy path.
  Solitude reads both forms.

If the spec has no `prop` marker, propose it there. It's the generic "dress this spot" marker any
studio tool can use.

### 2.2 Prop vocabulary (classes and rules)

Placement rules for every class:

- Never block a doorway's clear zone (`RoomCtx.Clear`), a corridor's walking line, stairs or ramps.
- Keep a walkable path of at least 2 cells between every door pair in the room.
- Floor props respect the ceiling. Wall props need a wall cell behind them.
- Reuse `RoomCtx.Used` so props don't overlap the existing furniture blocks.

| Class | Mount | Footprint (m) | Where |
|---|---|---|---|
| `console` | floor, against wall | 1.2 × 0.6 × 1.1 | control, lab, server, lobby, security |
| `desk`, `chair` | floor | 1.4 × 0.7 × 0.75 / 0.5 × 0.5 × 0.9 | office, conference, security |
| `bench_lab`, `specimen_tank` | floor | 2.0 × 0.8 × 0.95 / 0.9 × 0.9 × 2.0 | lab, test_chamber |
| `server_rack` | floor, rows | 0.6 × 1.0 × 2.0 | server |
| `locker` | floor, against wall | 0.5 × 0.5 × 1.9 | lobby, quarters, storage, security |
| `bunk` | floor, against wall | 2.0 × 0.9 × 1.6 | quarters (new program, see 2.3) |
| `crate`, `barrel`, `shelf` | floor | 0.8³ / 0.6 × 0.6 × 0.9 / 1.8 × 0.5 × 2.0 | storage, loading, warehouse, workshop, corridors' dead ends |
| `workbench`, `toolboard` | floor / wall | 2.0 × 0.8 × 0.95 / 1.6 × 0.1 × 1.0 | workshop, maintenance |
| `pump`, `valve_wheel`, `pipe_run` | floor / wall / wall, along a wall | 1.2 × 1.2 × 1.4 / 0.4 × 0.2 × 0.4 / length × 0.3 × 0.3 | pump, boiler, maintenance, corridors |
| `table`, `stool` | floor | 1.8 × 0.9 × 0.75 / 0.4 × 0.4 × 0.5 | cafeteria, galley |
| `cable_run` | ceiling or wall, along the corridor axis | length × 0.2 × 0.2 | corridors (most), tubes |
| `vent_grate` | wall or floor | 0.8 × 0.1 × 0.6 | where `vents` already open; corridors |
| `debris`, `bones`, `stain` | floor | 0.4–1.5 | anywhere open; more in dead ends, crawler nests |
| `light_fixture_dead` | ceiling | 0.6 × 0.3 × 0.1 | next to some `light` entities (Solitude kills some lights) |
| `sign` | wall | 0.6 × 0.05 × 0.3 | by doors; the `props.text` hint is the room program ("LAB 2", "PUMP ROOM") |

- **Density** is a config dial `dressing_density` (0..1, default 0.6), scaled per program: storage
  is dense, an atrium is sparse.
- **Corridors** get runs (pipes, cables), not furniture.
- **Bigger cases:**
  - Programs already have `Programs.Furnish` blockout geometry (`PartSolid` named `room:5/consoles`). **Also emit a prop marker for each furnish block** (same position and footprint, `tags: ["blockout"]`), so the game can hide the block and drop the real asset in its place.
  - Don't remove the geometry. It stays the collision and fallback.

### 2.3 Programs Solitude wants (station set)

Add a program set `station` (when `cfg.ProgramSet == "station"`), or extend `facility`:

- **quarters:** bunks, lockers, a table
- **galley:** tables, stools, crates
- **medbay:** beds, cabinets
- **hydroponics:** racks, tanks
- **moon_pool:** a room with a `Water` floor opening to the sea; great for the game
- **dive_locker:** suits, lockers
- **sonar_room:** consoles
- **generator:** pumps, valves, pipe runs

Each program also tags the room (`Meta["program"]` already does). The game uses it for loot
tables and log flavour.

## 3. Task B: gameplay entities (markers the game acts on)

The same marker format, appended after props, behind flag `station_entities` (default off).
Positions on label streams.

| Kind (`extras.noor.marker`) | Placement | Solitude uses it for |
|---|---|---|
| `loot` (`size`: small/medium/large) | on top of props (`desk`, `shelf`, `workbench`) or floor by walls; 1–4 per room by program | loot crates and pickups (today Solitude guesses from light positions) |
| `log_terminal` | on a `console` or `desk` | crew log readers |
| `creature_spawn` (`role`: patrol/ambush/nest) | `nest`: 1–2 dead-end rooms farthest from `player_start` (graph distance); `ambush`: corridor corners and vent exits | station creatures |
| `hiding_spot` | in front of `locker` props, under `desk`s | hide from things (planned) |
| `power_junction` | a wall in maintenance/generator rooms, one per sector | restore power to a sector (planned) |
| `hazard_zone` (`type`: flooded/electrified/gas, with `Bounds`) | Floods' `Water` cells, broken areas | flooded sections the player wades or swims through |
| `airlock` | the `player_start` room, at the wall the player faces | where Solitude's sub docks (today it uses `player_start`) |
| `viewport` (`Bounds`) | optional, flag `viewports`: a small porthole cut into an outer wall (`nl_glass`) | black water outside; something swims past |

Wall-mounted ones need `Bounds` (validator).

## 4. Task C: a station layout (new algorithm)

Today's stations are generic underground facilities. A deep-sea station is a **built object
under pressure**, and its shape language should say so. Add it **without** changing existing
presets: a new config flag `layout_style = "station"` (default `""`) branching in
`Layout.PlaceRooms` and `MissionGraph.Build` (the `Packing` / `Tower.BuildTowerGraph` patterns),
plus a preset `station` in `Recipes.Presets`.

What a station looks like (Tom and Sage's wishlist):

- **Modules on a spine.** Modules are pods: round or octagonal rooms (use `Diag` for the cut
  corners), 8–14 m across. Tubes are narrow corridors, 2 cells wide, with a lower ceiling
  (2.6–3 m) and sloped ceiling edges (`CeilSlope`) so they read as cylinders. The spine is
  either one long tube, a ring around a central hub, or a branching tree (a dial: `spine = line |
  ring | tree`).
- **Vertical:**
  - The **docking airlock is at the top** (`player_start`) and the **core is at the deepest point** (`exit`). This is how Solitude docks: from above.
  - 2–4 storeys connected by **stairs or ramps only**. No lifts, ladders or spirals until Solitude supports them; when they come, emit `nav.link` markers.
  - The 0.5 m stair rise works now: Solitude fixed its stepping on 2026-11-22.
- **Bulkheads.** Every tube-to-module joint is a doorway. Optional `door` entities without keys
  give "pressure doors" the game can open and close.
- **Character rooms (pick 1–3 per station):**
  - a moon pool (a `Water` floor opening)
  - a big domed lab
  - a flooded wing (a whole branch with `Water` up to 1 m; `hazard_zone`)
  - a collapsed section (a blocked tube, `debris`)
  - a hydroponics bay
- **Keys and secrets.** Keep NoorLevel's mission graph: a key before its door, one secret room.
- **Sizes.** Room count 8–30; `map_size` 40–72; ceiling 3–4 m in modules.
- **No skylights or yards.** Windows are allowed only as `viewport` markers (Task B).
- **Style per station** (a dial `arch_style`): research (clean, white), mining (heavy, industrial),
  military (bunker), derelict (more `debris`, `Floods`, broken `lights`). Write the style into the
  `.recipe.json` and into a root-node `extras.noor.station = { style, spine, seed }`.

Validation: everything reachable from the airlock; the core is the deepest point; no tube
shorter than 2 cells between modules.

## 5. Task D: material variants per room (small, optional)

Solitude already restyles surfaces by material name (`nl_floor`/`nl_wall`/`nl_ceiling`) with
tiling PS1 materials, so stations need **no UV texturing**. To vary the look per room without
breaking anything, behind flag `part_meta`:

- Put `program`, `sector` and `style` into the **part node extras** (`room:5` nodes already carry `{part}`). This adds keys and changes no names. Solitude picks a floor/wall/ceiling material set per program (lab = white tiles, pump room = rusty plate, quarters = carpet and wood).
- Material names stay `nl_*`. Don't add `nl_wall__lab` variants unless a consumer needs them;
  extras are enough.

## 6. Runtime generation: notes for the CLI (Tom asked "can they be generated during runtime?")

Sage's assessment for Tom, for context:

- **(A) A bigger baked bank** (60–100 layouts, ~200 KB each, ~20 MB total), plus Solitude
  dressing each visit differently from the station's seed (props, loot, power, flooding, creature
  nests). This works today and is zero-risk.
- **(B) True runtime generation without breaking the files-not-code rule.** Ship the CLI next to
  the game. When the player approaches a station, Solitude runs it in the background (docking
  takes ~5 s; generation takes 0.1–3 s per the docs) and loads the GLB from `user://`. That gives
  infinite stations.
- **(C) Linking `NoorLevel.dll`.** Technically trivial (netstandard2.1/net8, no deps, and the docs mention Godot), but it breaks the studio rule. Tom's call.

For (B), the CLI needs (please add, behind no flags since they're CLI-only):

- `--out <file>` writing exactly that file name, not `noorlevel_<seed>.glb`.
- `--json-result`: one JSON line on stdout with `{ ok, file, ms, seed, rooms, errors }`, and a non-zero exit code on failure.
- A guaranteed upper bound: `--max-ms` gives up cleanly instead of retrying 60 times.
- A self-contained single-file publish profile for win-x64 and linux-x64. Report its size.
- Measure and report generation time for `station` at `map_size` 48/56/64/72.

## 7. Deliverables and acceptance

1. Golden-hash test committed first. All existing outputs are byte-identical with new flags off.
2. Task A: the `Dressing.cs` pass plus a `dressing` flag. Props for every program in 2.2/2.3. A
   furnish-block marker per blockout block. Unit tests: no prop in door clear zones, no overlap,
   all props inside their room, deterministic per seed.
3. Task B: the entities in section 3 plus a flag. A validator rule: every `creature_spawn`/`loot`
   is reachable.
4. Task C: `layout_style = station`, the `station` preset, and 8 example outputs in
   `examples/station/` (GLB plus the preview HTML).
5. Task D (optional) and the section 6 CLI items.
6. Docs: `PARAMETERS.md`, `DECISIONS.md`, and an Interchange spec proposal for `prop` (and any
   other marker not in the spec), written up for review rather than applied to NoorEngine's
   repo.
7. **A short "consumer contract" section in NoorLevel's README:** marker names, fields, units,
   axes. Solitude's importer will be written against it.

Out of scope: anything in Solitude. Sage will write the Solitude importer (prop placeholders,
the Blender asset swap, entity hookups) in the Solitude repo once this lands.

## 8. Appendix: Blender asset convention (for Tom, so the swap is automatic)

So Solitude can swap a `prop` marker for Tom's model with no per-asset code:

- **One prop per GLB:** `assets/props/<class>_<variant>.glb`, e.g. `console_0.glb`,
  `console_1.glb`. When a marker's `variant` exceeds the count Tom made, the game wraps around.
  When none exist yet, it shows a generated placeholder box from `footprint`.
- **Origin:** at the floor contact point, centred on the footprint. Wall props have the origin on
  the wall surface. Ceiling props have it on the ceiling.
- **Facing:** the front faces −Y in Blender, so the Front view (numpad 1) looks straight at it.
  The glTF exporter turns that into +Z; the game does the rest. Keep the size close to the
  footprint table; the game won't rescale.
- **PS1 look:**
  - 64–128 px textures with nearest filtering. Low poly: 50–500 tris.
  - Solitude's shader treats **vertex alpha < 1 as emissive**, so screens and LEDs glow if painted with alpha.
  - An emission map wired into Principled BSDF **Emission Color** also works. (Blender only exports emission wired that way.)
- **Damage variants:** `<class>_<variant>_broken.glb` is optional. The game uses it when the
  marker has `tags: ["broken"]`.
