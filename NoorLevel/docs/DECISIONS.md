# NoorLevel decisions

Short records of choices that aren't obvious from the code: what was decided, why, and what it costs.
Newest last.

## D1. Station markers are a post-pass on label streams (2026-11-22)

**Context.** Solitude wants its deep-sea stations dressed (props) and wants gameplay markers (loot,
creature spawns, the airlock...) from NoorLevel ([STATIONS_BRIEF.md](STATIONS_BRIEF.md), tasks A and B).
Existing output must not change.

**Decision.**
- Three flags, all off by default: `dressing` (with `dressing_density`, default 0.6), `station_entities`,
  `viewports`. Plus a new program set, `program_set = "station"`; the existing sets are untouched.
- `Dressing.Run` and `StationEntities.Run` run at the end of each generation attempt, after Floods (and
  Mirror), just before the validator. They only append to `Level.Entities`; they never edit `Spans`.
  New kinds come after every existing entity, so existing node names (`light_3`...) stay the same.
- Every random choice comes from a label stream (`{seed}/{variant}/dressing/{room}`,
  `.../station/<what>/<id>`, `.../viewport/{room}`), never the shared one.
- To mark the furniture blocks, `Programs.Put` records each block in `room.Meta["furnish_blocks"]`, only
  when `dressing` is on (Meta isn't exported).
- The golden-hash test (64 outputs) proves the flags-off output is byte-identical; a unit test proves that
  turning the flags on only appends markers (same columns, rooms, corridors, links, same entities before
  them).

**Cost.** About 10-40% more generation time with the flags on (the doorway-path check after each floor
prop); the passes run once per attempt, and placement guarantees the validator's new rule, so they never
cause a retry.

## D2. The marker format (2026-11-22)

**Decision.** Brief 2.1, checked against Noor Interchange v0.4:
- Each marker is an empty glTF node `<kind>_<n>` (numbered per kind, as every NoorLevel entity node).
- `extras` keeps NoorLevel's flat legacy form (`kind` plus every property) and gains `extras.noor`
  (spec 3.3): `v: 1`, `marker`, `id` (= the node name, stable for a seed), then the fields.
- Facing: the spec says a marker's forward is the node's **+Z**. The node gets a yaw-only `rotation`
  (written only for the new kinds); `extras.angle` keeps the map yaw in degrees (0 = map +X = glTF +X,
  90 = map +Y = glTF −Z, the player_start convention). Angles are multiples of 90, so the quaternions are
  exact constants (no trigonometry, spec 5.3).
- `footprint` `[w, d, h]` in metres: w across, d along the forward, h up.
- Origin by `mount`: `floor` = the centre of the footprint on the floor (the spec's bottom-centre pivot);
  `wall` = on the wall surface, the bottom centre of the back face; `ceiling` = on the ceiling, the top
  centre. The brief said "the point on that surface" for wall and ceiling props; this pins which point,
  so an asset's origin can be authored to match (brief section 8).
- In the IR JSON and the viewer payload the markers are ordinary entities (map units, Z up): `pos` is the
  origin, `lo`/`hi` the axis-aligned box the piece takes, `props` the fields.
- Marker types: where the spec has one it's used (`loot` → `pickup`, `creature_spawn` → `spawn.enemy`,
  `hazard_zone` → `trigger`, `airlock` → `entrance` of kind `airlock`); where it hasn't, NoorLevel
  proposes one (`prop`, `interact` for log terminals and power junctions, `hide`, `viewport`) in
  [INTERCHANGE_PROPOSAL.md](INTERCHANGE_PROPOSAL.md) instead of inventing it silently. The entity kinds
  (and node names, and flat `kind`) keep the brief's names, which Solitude reads.
- `room`, `program`, `sector` sit at the top of the `noor` block as the brief asked; the proposal offers
  them as optional common fields for level markers (or to move them into `props`).

## D3. Where props may go (2026-11-22)

**Decision.**
- Floor props never stand in a doorway's clear zone (`RoomCtx.Clear`, 3 cells round each door cell), on
  stairs, ramps or the cell at their foot, in water or hazards, on raised or solid furniture, or on the
  start, key or exit. Wall and ceiling props may hang over a doorway zone only with 2 m (64 units) of
  headroom under them (signs, pipe runs, cables).
- Doorways that were joined by a path 2 cells wide (2x2 squares of free floor, steps the validator
  allows) before dressing are still joined after it; a prop that would cut one isn't placed.
- Against-wall props need a solid, non-glass wall behind them over their whole height and a free cell in
  front of them. Free-standing pieces get a free cell all round.
- No two props overlap, nor a prop and another entity (3D boxes).
- A room whose floor is all doorway zone (a small room with doors on every side) gets only wall and
  ceiling props: the doorways win. A program's signature piece that finds no wall tries the open floor
  (and the other way round) before giving up.
- Blockout props mark `Programs.Furnish` blocks exactly where they are; those follow the programs' own
  2-cell doorway rule, as they always have.

## D4. The prop vocabulary (2026-11-22)

**Decision.** The brief's classes (2.2), plus five for the station programs: `bed` (2.0 × 0.9 × 0.6),
`cabinet` (0.8 × 0.5 × 1.8), `grow_rack` (1.8 × 0.6 × 2.0), `water_tank` (1.0 × 1.0 × 1.8), `dive_suit`
(0.7 × 0.5 × 2.0). Furniture blocks map to classes where one exists (`desks` → `desk`, `server racks` →
`server_rack`, `shelving` → `shelf`, `planters` → `grow_rack`...); others keep their block name with
underscores (`reactor_core`, `reception_desk`, `tank`, `boiler`, `generator`, `counter`). Rooms without a
program (levels without `programs`, like the bunker preset) get props by their kind (start: lockers and a
console; key: a console and a desk; supply: shelves and crates; the rest: crates, barrels, lockers,
shelves, a desk or console, pipes). Tags: `broken` (15%), `blockout`, `flat` (stains and floor grates:
walk over them), `vent` (a grate over a duct mouth: the duct is walkable, never make it solid).

## D5. The station program set (2026-11-22)

**Decision.** `program_set = "station"` (with `programs` on): sectors habitat (first), research, dive,
operations (shuffled), engineering (last, the deep end); programs quarters, galley, medbay, hydroponics,
moon_pool, dive_locker, sonar_room, generator, plus the facility's lab, control, server, office, storage,
workshop, pump. The start is the dive locker (the dock), the exit the reactor (the core), the key room
security. The moon pool is a 3x3-to-8x8 pit, three 16-unit steps deep with a stair down one side, its water
just under the deck: shallow enough that the validator's player can walk out (it can't swim up a shaft).
The game treats its bottom as open to the sea. A room too cramped for the pool becomes a dive locker.

## D6. Gameplay markers (2026-11-22)

**Decision.**
- Loot sits on props with a surface (desk, table, workbench, lab bench, shelf, crate, barrel, cabinet) or
  on the floor by a wall. "Reachable" for loot means grabbable: a reachable floor in its cell or next to
  it, at most 2 m below it (loot on a block you can't step onto). Creature spawns must be stood on.
  Placement only picks reachable spots; the validator (with `station_entities` on) checks it again.
- Nests: the 1-2 dead-end rooms farthest from the start by graph distance. Ambushes: corridor corners
  and vent mouths. A patrol per sector, in its biggest room.
- Power junctions: one per sector, preferring generator, pump, boiler, reactor, workshop, control, server
  rooms. Without programs a sector is a lock zone.
- Hazard zones: each flooded area (not a moon pool) as `flooded`, and `electrified` round some broken
  consoles, racks, pumps and tanks. The node is at the bottom centre of the box; `size` is in glTF axes
  [x, y up, z] metres, as the spec's trigger.
- **Deviation: the airlock is on the wall behind the player, not "the wall the player faces".** The
  player_start already faces the start room's doorways (the way in), so "the wall it faces" is the
  doorway wall. The airlock is where the player came in from, so it's behind them, and its forward
  points the way the player faces. If the brief meant the other wall, it's one line (`behind`).

## D7. Viewports are markers, not holes (2026-11-22)

**Decision.** `viewport` markers (0.6 × 0.1 × 0.6 m, 1.3 m up) on outer walls (nothing open for 3 m
behind the wall), in about 4 rooms in 10. NoorLevel doesn't cut them: the level has no outside volume to
show through glass, and the post-pass never edits `Spans`. The game cuts the porthole and draws the sea.

## D8. The station layout is its own branch (2026-11-22)

**Context.** Brief Task C: a station is pods on a spine of tubes, the airlock on top, the core deepest.
**Decision.** `layout_style = "station"` branches `MissionGraph.Build` and `Layout.PlaceRooms` the way the
wizard's tower and packed plans do (`Station.cs`); with the default `""` no code path or rng draw changes
(the golden test proves it). Decks are 96 units (3 m) apart: a stair tube is 6 steps. Pods are never stacked.
**Deviations.** Tubes are 3 cells wide, not 2: corridors here have odd widths, and a 2x2 player box has to
fit; the round profile makes them read narrower. Small maps run large (a deck change costs a 12-cell stair
tube): at map_size 48 a station comes out 1.0-1.7x, at 64-72 within 15%. `station_style` is its own field
(it sets `arch_style` only when empty). Pressure doors are `bulkhead` markers, not keyless `door` entities,
because the validator indexes doors by key; in the .glb they are Interchange `door` markers (opens slide).

## D9. Corridor cross-sections (2026-11-22)

**Context.** Tom: corridors needn't be rectangular. The station's tubes are the first user.
**Decision.** `corridor_profile` = box (default, no change) | chamfer | round, applied after all carving by
writing only `CeilSlope` / `FloorSlope` per cell corner (the span keeps the lowest ceiling and highest floor
point), so every exporter, the .map compiler and the previewer already handle it. Each corridor is cut only
as deep as keeps every move the player point and the 2x2 player box could make; vents stay boxes.

## D10. The CLI for runtime generation (2026-11-22)

**Context.** Brief section 6 (B): Solitude may run the CLI next to the game while the sub docks.
**Decision.** `--out` with the format's extension is an exact file (a folder works as before);
`--json-result` prints one line, exit codes 1 no level / 2 bad parameter / 3 `--max-ms` / 4 can't write;
`--max-ms` gives up cleanly (the process ends with the worker). Publish profiles win-x64 / linux-x64: one
self-contained file, trimmed (NoorLevel kept whole: parameters are set by name through reflection) and
ReadyToRun (a fresh process otherwise JIT-compiles the generator: 1.0-1.3 s per station; precompiled
0.4-0.65 s). 15.8 / 19.9 MB. Output byte-identical to the normal build.

## D11. Room facts on part nodes (2026-11-22)

**Decision.** `part_meta` (off by default) adds `room`, `kind`, `program`, `sector`, `style`, `profile` to the
part nodes' extras; names and materials stay `nl_*` (Solitude picks material sets per program from extras).

## D12. Markers in Quake .map exports (2026-11-22)

**Decision.** Entity values that are lists or vectors are written space-separated (dictionaries as
key=value). Markers whose point is inside solid (a blockout prop standing on its own furniture block) are
left out of .map files: qbsp reads a point entity in solid as a leak. Dressed stations compile leak-free.

## Known gaps

- (Fixed, D12: `.map` array values and markers in solid.)
