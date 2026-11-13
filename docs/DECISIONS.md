# BadLevel: architectural decisions

Why BadLevel is built the way it is. Each entry: the decision, why, and what it
costs. Newest last. This is the reasoning to carry into NoorLevel later; the code
itself is the prototype.

---

## D1. The world is a grid of columns with stacked open spans

**Decision.** A level is a 2D grid of 32-unit cells. Each cell holds a sorted list of
*open spans* `(floor, ceiling)`; everything else is solid. 45-degree walls are
half-solid cells (`level.diag`).

**Why.** It makes the hard guarantees cheap: brushes are the solid complement of the
spans, so output is **sealed by construction**; walkability is a graph over spans;
line of sight is a voxel lookup. Stacking spans gives room-over-room, storeys,
balconies, galleries and windows without a different model.

**Cost.** No true slopes (height changes are steps), no curves beyond 45 degrees, and
32 units is coarse for detailed terrain. Fine for blockouts, which artists rework.

## D2. Python prototype now, NoorLevel (C#) later; no exact replication

**Decision.** BadLevel proves the concepts. The NoorMD bench will be a native C# port
written from a design document, not a line-by-line translation, and it does not need
to reproduce Python's levels seed for seed.

**Why.** NoorMD's convention is native, deterministic C#. Matching levels across
languages would have required a hand-rolled RNG and frozen algorithms for little
benefit. The JSON IR is the contract worth keeping.

## D3. Every level is validated, failures are re-rolled

**Decision.** The generator retries until the validator passes: sealed, every room
reachable, keys gate the exit, no softlocks.

**Why.** Procedural output that is *usually* fine is not usable. Retries are cheap
(about 1 in 4 levels needs a second attempt).

## D4. Movement is directed; softlocks are checked

**Decision.** Step up at most 18 units, drop down up to 288; lifts and ladders are
explicit links with semantics (direction, interaction, accessibility). From every
reachable spot the next goal must still be reachable.

**Why.** One-way drops and lifts make a connected room graph meaningless as a test of
completability.

## D5. Analysis is optional, separate and machine-readable

**Decision.** `badlevel.analysis` (numpy) produces plain JSON. The HTML preview only
draws it. The generator core has no dependencies.

**Why.** Exporters, engine plugins and the generator itself (candidate gate) can all
reuse the same results. Visualisation choices never leak into the data.

## D6. The candidate gate rejects, it does not optimise

**Decision.** `--candidates N` keeps the *first* variant without `problem` flags.

**Why.** Optimising every level toward target numbers converges on sameness. Variety,
pacing and deliberate vulnerability are worth more than a score.

## D7. Seeing is not walking

**Decision.** Firing slits are 48 units tall (a player is 56), so they are visual-only
by construction. Glass is a set of cells that is solid for movement and gunfire and
open for visual connectivity. The analysis keeps a visibility graph next to the
walking graph.

**Why.** Windows, overlooks and landmarks are only safe if they can never become
unintended paths (bypassing a locked door, for example).

## D8. Storeys are planning guides; connectors are rooms with ports

**Decision.** Rooms get a storey (256 units apart) but may span more. Stairwells, lifts
and ladders are connector rooms with a lower and an upper *port* where corridors
attach.

**Why.** It keeps vertical connections as first-class, editable spaces with their own
geometry and semantics, instead of special cases inside corridors.

## D9. Long corridors: fix the cause, then add beats

**Decision.** Loops are chosen *after* placement (close in space, apart in the graph),
level loops may cross other corridors at the same height (junctions), and long
corridors get beats (chambers, alcoves, vaults). A quarter of loops may be long on
purpose.

**Why.** Long corridors came from loops picked before rooms had positions. Removing
long corridors altogether would remove a legitimate design tool (flank runs, sniper
alleys); monotony is the problem, not length.

## D10. Merged halls keep their rooms as regions

**Decision.** Rooms merged into one hall stay separate rooms in the data
(`room.meta["hall"]`, `level.halls`).

**Why.** Encounters, pacing, diagnostics and editors need "the arena part" and "the
antechamber" even when they read as one space. Mega-rooms will build on this.

## D11. Recipes: design dials compile into settings; overrides pin identity

**Decision.** A recipe is eight dials (0..1: scale, openness, verticality,
claustrophobia, combat, exploration, loopiness, regularity) plus raw overrides.
`compile_recipe` maps dials monotonically onto GenConfig; overrides always win. All
style constants that used to live in code (size mix, shapes, room features, crates,
enemy and pickup density, corridor widths, ceiling heights, footprints) became
config levers with the old values as the 1.0 baseline. The recipe is saved beside
every export.

**Why.** Users express intent in design terms, not 40 numbers. Overrides let a preset
fix what makes it recognisable (bunker: no glass, ladders and lifts; fortress:
stairwells and slits). Keeping the old values as the baseline means "no recipe" still
generates exactly what it did.

**Non-combat.** `combat = 0` means zero enemies. The analysis then reports
`purpose: exploration` and skips combat-only diagnostics.

**Cost.** The dial mapping is hand-tuned; it is documented in `recipes.py` and should
be revisited as new space types arrive.

## D12. Identity is measured, and the measurement exposed a limit

**Decision.** `fingerprint.py` describes a level as ~17 features (room size and
variation, ceiling height, corridor width, storeys, vertical spread, regularity,
halls, windows and glass, combat density, loops, connector mix). A test requires that
presets are recognisable from fingerprints alone (nearest-centroid, leave-one-out).

**Result.** 98% on the first measured batch. But side-by-side renders showed that from
far away every preset still has the **same silhouette**: a branching tree of rooms and
corridors. Recipes change proportions, scale, density, heights and materials of the
layout, not its macro shape, which comes from the layout algorithm itself.

**Consequence.** Instant recognisability needs the next steps: space-type modules,
open areas, mega-rooms and, above all, **composition** (macro structures such as
hub and spokes, central space with satellites, stacked tower, compound). The
fingerprint test stays as a guard; a silhouette-level measure should be added with
composition.

## D13. Space types are modules behind one interface

**Decision.** Every kind of space implements `SpaceType` (`badlevel/spaces/`):
`propose` (size, heights, meta), `assign_shape`, `build` (write spans), `attachment`
(ports), `decorate` (features), `populate` (contents) and `regions`. A registry maps
room kinds to space types. Rooms (all eight gameplay roles) and connectors
(stairwell, lift, ladder) are the first modules. Corridors (style, beats, contents)
and merged halls are relationships rather than room types and got their own
modules. Placement, carving, decoration and the ports lookup dispatch through the
registry; `Room.is_connector` asks it too.

**Why.** Open areas and mega-rooms must be *additions*, not more special cases in
the core. A test registers a new space type from outside the package, generates
with it and removes it again.

**How it was verified.** A pure refactor: full `.map` + `.json` output of 38 levels
(defaults, three storeys, six presets) hashed before and after, byte for byte
identical. Keeping the random sequence stable meant the base class still performs
the feature roll for spaces that never get features.

**Cost.** Some helpers (feature placement, entity spots) still live in `decorate.py`
and are imported by the room module; they can move when a second space type needs
them in a different form.

## D14. Open areas: a plug-in space type with a sky

**Decision.** Courtyards / plazas are `OpenAreaSpace` (`spaces/open_area.py`), the first
space type added through the D13 interface. A few hubs, arenas or fight rooms become
courtyards when `open_areas > 0` (recipes: openness; compound 2, fortress 1 as its
bailey, industrial 1 as a loading yard; never in a bunker). The column is open up to
sky height (nominally 10 m, clamped to 9-14 m whatever the recipe's ceiling scale)
and those cells are in `level.sky`: the solid above them gets a sky face (`sky1` in
the `.map`, `bl_sky` in the `.glb`, `"sky"` in the IR).

**Outdoors is not a big room.** The perimeter gets pilasters in a regular rhythm so the
walls read as building fronts. Contents are one weighted choice including *nothing*
(breathing room), a kiosk/monument, a colonnade, a sunken plaza, a raised terrace or
an arcade with a walkway. Indoor pillars run floor to ceiling; outdoor columns stop
at 5-7 m because there is no ceiling to hold up (the first render showed 20 m
columns, which read as towers). Lamps hang at most 7 m up instead of at the sky.

**Default levels unchanged.** With `open_areas = 0` the graph step is skipped; the
`.map` output of the 38 golden levels stayed byte-identical. The IR gained an empty
`"sky"` list.

**Cost / next.** The kiosk is a solid block; a building you can enter belongs to
mega-rooms (sub-generation). Yards don't yet shape the macro layout (a compound
should be *organised around* its yard): that is composition.

## D15. Mega-rooms run their own generation inside their footprint

**Decision.** `MegaRoomSpace` (`spaces/mega.py`) is a huge (24-32 cells, capped at 40),
tall (12-18 m) space whose `decorate` hook runs a small generator: sub-areas joined by
open transitions instead of rooms and corridors. Vocabulary: **platforms** (raised,
with stairs), **decks** (on stilts, walkable underneath), **catwalks** (bridges between
decks at one height, floor open below), **structures** (solid masses) and **tiers**
(terraces rising towards a wall, two stairs each). Every sub-area is a semantic region
(`room.meta["regions"]`, cells in `region_cells`).

**Characters, because a mega-room is not necessarily an arena.** *hangar* (sparse: a
few big structures, a loading dock), *works* (dense: decks on one shared level so
catwalks line up, platforms, clutter), *terraces* (an amphitheatre / great hall).
Recipes weight them (`mega_styles`): industrial -> works, compound -> hangar,
fortress -> terraces, explore -> terraces/works with no enemies at all.

**Reachability by construction.** Each piece is placed together with its stairs, and
undone if it would split the base floor into islands; stair landings are reserved so
later clutter cannot block them. A test requires every non-solid region to be
reachable from the player start.

**What the tests caught.** (1) Terraces were first built inverted (the lowest tier
against the wall, higher ones in front), which buried the lower tier's stairs and made
it unreachable; tiers now rise towards the wall and are built front to back. (2) The
first "works" halls almost never had catwalks, because decks rarely shared a height and
faced each other; one deck level per hall plus aligned placement fixed it (15 catwalks
in 20 halls instead of 1 in 30).

**Cost / next.** The interiors are functional and legible but not yet memorable; they
need iteration with an artist's eye. Mega-rooms are rectangular outlines; structures are
solid (no enterable buildings yet). Composition should decide *where* a mega-room sits
(central space with satellites, compression before it).

## D16. Composition: the macro structure, and pacing by contrast

**Decision.** `GenConfig.composition` picks the level's macro structure, and it acts on
**both** the mission graph and the placement (if only one of them changed, corridors
would just wrap around everything):

| composition | graph | placement |
|---|---|---|
| `tree` (default) | as before | as before (random sides) |
| `hub` | side rooms and key branches hang off a centre node; loops come back to it | the centre is placed first; its children go round its four sides in turn, further rooms keep heading outwards |
| `central` | as hub, plus two extra loops tying satellites back to the centre | as hub, with satellites in a tight ring around the big space |
| `linear` | side rooms attach only to the critical path | the critical path follows one axis with rooms centred on it (a spine), side rooms branch off perpendicular (ribs), merged halls follow the axis, no deliberately long loops |

Recipes choose it from what the level contains (big spaces -> `central`) unless a
preset pins it: compact and bunker are spines, industrial and explore hubs.

**Pacing by contrast.** With `pacing_contrast`, corridors into big spaces (mega-rooms,
courtyards, rooms 9 m+ tall) are 3 cells wide and 3.5 m high, so arriving is a
release. The analysis reports every such transition (`transitions`: corridor vs room
cross-section) and flags the strongest reveal.

**Measured.** Three silhouette features (elongation of the room layout, centrality of
the best-connected room, share of dead ends) recognise the composition from the
silhouette **alone** 70% of the time (chance: 25%); linear levels are clearly
elongated, hub/central clearly centralised. In side-by-side top-down renders hubs read
as crosses, central levels as a mass with satellites, linear ones as fishbones (5 of
6 seeds; one wraps around through ordinary loops). All presets recognisable: 100% on
the measured batch.

**This answers D12** in part: presets no longer share one silhouette. What remains:
hub and central look alike from far above (they differ in what sits at the centre, not
in shape), and compositions are still four hand-made strategies rather than a grammar.

**Bugs found on the way.** (1) Merged halls ignored the composition and broke spines.
(2) Recipes chose the composition before applying a preset's overrides, so a fortress
whose courtyard and mega-room come from overrides was composed as a plain tree; the
composition is now derived after overrides.

## D17. Distinctness is evaluated, published and guarded

**Decision.** `badlevel/evaluate.py` generates many seeds per preset (or composition),
fingerprints every level and reports: leave-one-out recognition and a confusion
matrix, the closest pairs (drift warning), **variety inside each label vs distance
between labels** (separation), a z-score profile per label ("what makes it itself"),
and space metrics answering the modularity note (biggest space, intimate vs expansive
rooms, side rooms, secrets, brush count). `badlevel evaluate presets|compositions`
writes `evaluation.json` and an HTML page; the live site publishes both on every push.

**Why variety matters as much as accuracy.** A preset that always made the same level
would be perfectly recognisable and still a failure. Tests require every preset to
have variety and the separation to stay above 1 (levels of different presets further
apart than levels of the same one).

**First results.** Presets: 100% recognised on 6 seeds each, separation 1.6x; the
closest pair is fortress <-> industrial (both tall, multi-storey, organised around a big
space), the next compact <-> classic. Compositions: 71% from the silhouette alone
(chance 25%) but only 50% from the full fingerprint, because compositions differ only
in macro shape and the other features are noise for that question; silhouette
separation is reported separately.

**Next.** Fortress and industrial need more distance (stairwell towers vs lift
shafts already differ; their footprints and halls should too). Hub vs central remains
the weakest composition pair.

## D18. Architecture starts with hall cross-sections

**Problem.** The architectural showcase (12 levels from the four themed presets, seeds
1-3) showed that BadLevel made big spaces and real verticality but not architecture:
every room was a box with a flat ceiling and sheer walls, so a fortress, a factory
and a compound differed by plan only. Gothic and underground weren't convincing at all.

**Decision.** `badlevel/architecture.py` gives roofed rooms a *cross-section*: a
profile of ceiling heights swept along the room's long axis, plus piers, columns and
pilasters standing in it. Profiles per style (D18 table in the README). It's an
additive pass, in two halves around a room's features: piers before (so features
avoid them), ceilings after (so they can respect platforms, decks and stairs).

**Why sections first.** The ceiling and the structure that carries it change the
silhouette of the biggest spaces most, with no new space types and no new routing. A
section is also easy to read and replace as greybox: a nave is a nave in any engine.

**Noor's constraints, and how they're met.**
1. *Variety within a style*: palettes, not templates; open variants (vault, clearspan)
   and asymmetric ones (single-aisle, shed); each use of a profile halves its weight
   for the rest of the level.
2. *A hierarchy across a level*: rooms ranked by volume into grand / major / minor
   tiers with their own palettes; small rooms get haunches or nothing.
3. *Proportions*: bay spacing grows with the height (clamped; at least three bays),
   piers thicken from 12 m, ribs and trusses deepen with the span; an undercroft is only
   offered to low rooms (in a 18 m hall it turned into a hypostyle, seen in a test render).
4. *Genuinely open space*: every grand palette keeps a pierless option.
5. *Framed reveals*: not in this step (step 3 of the plan).
6. *Useful as greybox*: plain blocks only, on the 32-unit grid and 16-unit steps;
   `room.meta["arch"]` records profile, bay, aisle widths, spring heights.

**Safety.** Ceilings are only lowered, never below the top walkable surface + 128
(a base corridor's height) or below an opening beside the cell; galleries and sky are
skipped. Piers keep 2 cells from doorway cells (features keep 3, but arcades with gaps
looked broken), stand on plain floor, keep a walkable ring, and are undone if the base
floor would split. Each room uses its own random stream, so unstyled levels are
byte-identical (golden check: 26 of 26 unchanged; only the 12 styled preset levels in
the set changed). The validator remains mandatory.

**Known limits.** Arches and vaults are stepped (1 cell x 16 units), "voxel arches";
smooth slopes would need sloped brushes in the exporter. Rooms on a multi-storey level
are often 6 m high, which leaves 64 units to shape: there the effect is subtle. Shaped
rooms (rings, octagons) take the profile of their bounding box.

## D19. Smooth vaults, and character for small rooms

**Smooth vaults.** Stepped vaults read as arches from across a hall but as an upside-down
staircase from below. Profiles that vary along one axis (vault, barrel, basilica,
gable, shed, multispan, sawtooth, haunches, tray) now also give a continuous surface;
at a cell's four corners it yields a plane, kept in `level.ceil_slope`. The column span
keeps the **lowest corner**, so validation, LOS and analysis stay conservative and
unchanged. Exporters draw the true surface:
- `.map`: the solid above a sloped cell is a box with a sloped bottom plane (convex, so
  qbsp-safe); cells with the same corners merge along the axis the slope doesn't change
  on, so the brush count barely moves.
- mesh / glTF / preview: a sloped ceiling quad, and side faces between cells whose
  bounds are lines along the edge: the edge is split where bounds cross and each piece
  becomes a quad. Checked with a magenta-background render (no pixel of background
  shows through from inside) and a unit test that the side faces exactly fill a gap.
- IR: `ceiling_slopes` and per-brush `slope`.
Ribs, trusses, drop panels, coffers and groin vaults stay crisp steps: they are
members, not surfaces. A slope is only used where nothing is stacked above the room in
that column. `arch_smooth: false` restores the strict 16-unit grid.

**Small rooms.** On multi-storey levels most rooms are 6 m high, and with 128 units of
headroom there was almost nothing left to shape: 28 of 64 fortress rooms and ~45 of 78
industrial rooms stayed plain boxes. Changes:
- HEAD is now 112, the height of the low pacing corridors the generator already makes,
  so no structure is lower than a corridor a player already walks through. Profile
  thresholds are relative to HEAD.
- With smooth surfaces a 48-64 unit vault or gable reads, so low vaults and gables are
  allowed; new low-room profiles **tray** (raised middle, coved edges) and **coffered**
  (a waffle slab in a bunker).
- "plain" is weighted lower in the minor tier and damped like any other profile.
Result over seeds 1-5: plain rooms (secrets excluded) fortress 6/64, industrial 13/78,
compound 10/63, bunker 21/54. The bunker's remaining plain rooms are 128 units high
(corridor height): nothing fits under them, and their character has to come from walls.

## D20. Structure along the walls

**Problem.** Noor's note on the showcase: "enormous uninterrupted wall surfaces". After
step 1 the ceilings had structure, the walls didn't: the longest flat wall per room
averaged 11-15 m, up to 38 m in the big halls.

**Decision.** `badlevel/walls.py` gives every eligible room of a styled level a rhythm
along its walls, aligned with its section's bays where it has them:
- *inward members* on the bay lines of all four walls (shafts, frame pilasters,
  counterforts 2 wide in bunkers), placed with the pier rules before the features;
  skipped where the section already stands members against the walls;
- *outward recesses* between the members: gothic lancet niches with pointed (sloped)
  heads, industrial service bays in every other bay, bunker shelters (96 high, so they
  fit 128-high rooms), compound window bays with a 32-unit sill;
- *one end feature* in grand and major halls: an apse niche 2 deep with a pointed head,
  a loading door, a blast door, a bay window;
- *crane corbels* along the long walls of tall industrial halls (forced stepped cells
  laid over the section).

**Why recesses go outward.** Cutting into the solid around a room changes the wall's
depth without taking floor from the room, and it is where a wall would have niches.
It is only safe with rules: the recess cell must be solid rock with no owner, inside
the border, and every 8-neighbour that isn't the room or the recess itself must be solid
too, so one cell of wall always remains between it and any other space. All or nothing
per recess, never at a doorway (2 cells). A recess belongs to its room (owner), so
windows found later may open from it, which looks right.

**Measured** (seeds 1-3, the same layouts with and without walls): mean longest flat
wall per room fortress 11.5 -> 5.9 m, industrial 13.0 -> 7.0 m, compound 14.5 -> 5.9 m,
bunker 7.1 -> 5.4 m; the worst wall per level in fortress, industrial and compound
19-38 m -> 8-26 m (bunker 9-13 -> 7-9 m). A test requires a clear
drop for every style. `arch_walls: false` turns the pass off; unstyled levels are
byte-identical.

**Known limits.** Members are full-height boxes; there are no stepped buttresses or
string courses yet. Rooms with shaped outlines (octagons, rings) get members only where
their bounding box meets a wall. Small gothic rooms can look busy with shafts every
3 cells; the minor tier only thins the recesses.

## D21. Portals and framed reveals

**Problem.** The showcase's reveal shots never changed: a low corridor ending in a plain
rectangular gap in a wall, and whatever happened to be in line with it. Noor: "the change
in scale exists, but the view into the larger space isn't particularly dramatic", and
"a corridor opening toward a recognizable architectural feature, but keep some
surprises".

**Decision, part 1: portals** (`badlevel/portals.py`). Every corridor opening of a styled
room (a run of room cells whose outside neighbour is a corridor span at the room's floor)
is framed:
- *jambs*: the room cells at both ends of the opening become solid members (two deep in a
  bunker: a blast frame), only if solid wall is behind them, nothing stands there, and the
  floor stays connected;
- *head* (grand and major rooms): the mouth rises above the corridor into the wall: a
  pointed arch (gothic), a round arch (compound), a tall opening under a heavy lintel
  (industrial). Arches are smooth (sloped mouth cells); the corridor behind stays low, so
  you look through a tall frame into the big space.
It runs as decorate's **last** step, after rooms and corridors are populated: it sees the
final ceilings, every entity and every door. A mouth holding a door is never raised (a
locked door must still seal its corridor; a test checks every door), and raising only cuts
rock with one solid cell kept around it, the same rule as wall recesses.

*Splay*: the first version raised only the mouth, and the renders showed it was invisible
from inside the corridor (the low corridor ceiling hides the frame until you stand in it).
So the corridor's last 2 cells now ramp their ceiling up into the portal (sloped, same
rock rules, never over a door): the opening grows as you approach.

**Decision, part 2: framed reveals** (in `walls.py`, before the room's features). The way
in to a big hall is the opening whose corridor comes from the room nearer the start (or the
only opening). With probability `arch_axial` (0.65) the hall's end feature (apse, loading
door, blast door, bay window) goes on the wall facing it, on its axis; it may slide up to
2 cells, or narrow to 3, to stay clear of other doorways, and it's placed before the wall
members so the focal point wins. Otherwise it stays on an end wall: an oblique, side-on
discovery. When the axis lands on another doorway instead, it's recorded as a
*through-view* (the next portal framed across the hall, the classic vista), separately
from *axial*, so the numbers stay honest.

**Measured** (seeds 1-5, grand and major halls): with a way in found, 31 axial features +
15 through-views of 87; 41 oblique (by choice or no room on the far wall); 17 halls had no
recognisable way in. Fortress is the weakest (6 of 20 axial or through): its far walls are
crowded with wide doorways.

**Known limits.** Jambs are plain boxes; arches have no voussoirs or moulding. The axis
is the mouth's centre line, not the line of sight from deeper in the corridor. Diagonal
corridor mouths and mouths spanning a corner get no head.

## D22. Underground mode, and the reveal camera follows the corridor

**Problem.** In every review since the first showcase, "underground facility" failed:
the bunker preset was a flat 2-storey chain that climbed *up* from a basement, with
nothing that said "below ground".

**Decision.** `underground` (set by the bunker preset):
- *Descent.* The start is on the top storey; the critical path steps **down** one storey
  at a time (positions chosen at random along the path, all before the big hall when it
  fits), so the exit is the deepest point and the path never climbs back. Branches keep
  their usual freedom.
- *Surface entrance.* The start room is open to the sky where nothing is stacked above it:
  a hatch or pit you came down into. The rest of the level is under ceilings and rock.
- *The deep chamber.* The bunker now has one big hall (hangar or works), and because the
  descent comes first it sits on the bottom storey. Its section is **cavern**: a rough
  ceiling from a coarse random lattice (interpolated, kept in 16-unit steps so it looks
  cut, not poured) with rock pillars left standing, and rough-cut walls (recesses of random
  width, depth and height, no rhythm). The contrast with the ribbed concrete above is the point.
- The preset goes to 3 storeys; low, narrow, orthogonal rooms, slits, ladders and lifts
  stay. Pacing contrast turns on (a big space now exists), so the way into the deep
  chamber is compressed.

**Bug found on the way.** A locked door took its height from the corridor's nominal
height; on a stair corridor whose carved space is taller, it left a gap above the door
(96 units in bunker seed 4). Doors now fill the whole opening they close (generator-wide;
a test checks every door in the styled presets).

**Camera.** The showcase's reveal camera now looks along the corridor's last stretch
from 4 cells before the hall (slightly upwards), as a player arrives, instead of at the
hall's centre, which was often off the axis and filled half the shot with corridor wall.

## D23. The deep chamber is seen from above (an overlook, visual only)

**Problem.** Tom and Noor: make the deep chamber visible on the way down, so the descent
becomes a reveal of its own. A gallery (a deck inside the hall reached from the storey
above) is the existing tool, but it never formed: the chamber sits behind the locked door
(lock zone 1) while the rooms above are in zone 0, and a gallery is a physical way in (you
could drop from the deck into the hall and skip the lock). The generator was right to refuse.

**Decision.** `add_overlook` (layout, underground only): an **observation booth**, a new
small space type `overlook` (no enemies, no pickups, no architecture), carved into the rock
beside the chamber at gallery height, with exactly one cell of rock between them. A corridor
reaches it from a room on the storey above (any zone, at or before the chamber's depth),
routed and carved like a gallery corridor; the router learns about the late room
(`Router.add_room`). In that one-cell wall goes a wide **glazed window** (glass blocks
movement and gunfire but not sight), or, where the level uses no glass, a long **observation
slit** at eye height, 48 units tall so no player fits: the bunker's identity is "firing slits,
never glass", and a preset test caught the first version breaking it. Either way the lock
logic is untouched and the booth is a dead end. The hall cells in front of the window are reserved, so no member, platform or crate
blocks the view. If no corridor can reach a candidate booth, it is taken back out.

**Result.** Bunker seeds 1-8: 8 of 8 chambers have an overlook 7-8 m above their floor;
every level validates; the visual-connectivity analysis counts the new through-glass views.
A showcase camera rule looks from the booth through the window into the chamber.

**Known limits.** One overlook per level, always a booth with a window (no balcony over
the void). The booth is a side branch; nothing pulls the player into it, it is found.

## D24. Compounds: buildings in the yards, a roofscape around them

**Problem.** The last finding from the first showcase that no step had touched: a yard
was a 9-14 m pit with sheer walls on every side and a few blocks inside. It read as a
roofless room, not as a compound of buildings.

**Decision** (`badlevel/compound.py`):
- *Buildings.* With `open_buildings` (compound preset: 0.9) a yard gets 1-3 free-standing
  buildings first (by its size): walls one cell thick, a roof slab, an interior of at
  least 2x2 at 128+ units, and a 2-cell doorway on the side facing the yard's middle. About
  two thirds get a ladder up an outside wall (the existing ladder link and entity), and
  then the rooftop is a vantage point: the room's populate step puts a sniper and a reward
  on it. Each building keeps a 2-cell walkable ring and stays clear of doorways. Buildings
  are also one weighted option among the yard features of other presets.
- *Roofscape.* With `open_roofscape` (compound) the rock around a yard is carved, up to 4
  cells out, into open air above stepped roofs: 5-cell blocks along each side with their
  own front height (176-256 units), rising 48 units per ring away from the yard, up to the
  sky plane, sky above. From the yard: building fronts and a skyline, not a cliff. Facade
  pilasters stop at the roof beside them.

**Safety.** Roofs are scenery: a cell is carved only if its column is empty from below
the roof to above the sky, and only if nothing other than the yard or more roof is within
one cell at those heights (so no room, corridor, gallery or other storey can walk in).
The lowest front is 176, out of step reach of a balcony deck along a yard wall (144). A
test checks that no roof span is reachable from the start, that every building interior
is, and that every ladder reaches its roof. Lights in a yard are never placed on a
building's roof cell.

**Measured** (compound seeds 1-8): 14 of 16 yards have buildings, 25 ladders; every yard
has a roofscape (188-509 cells).

**Known limits.** The roofscape is a stepped band, still a wall at its outer edge (96 units
under the sky plane); where the yard borders other spaces the rock stays full height
(it reads like a gatehouse or a tower, but it isn't designed as one). Buildings are boxes
with one doorway: no windows, no interior features, no second storey.

## D25. Room around the yards, and a little life in the buildings

**Room around the yards.** Step 6's roofscape barely showed from the ground. Measuring
it showed why, and it wasn't what I expected: 88% of the band around a yard was already
being carved. The problem was the roof shape. Rings rising 48 units outwards read from the
ground as one tall stepped wall, and compound yards opened up to 14 m, so even low roofs
left a tall face below the sky plane. Changes:
- roofs are mostly flat blocks of their own height (176-256), some rising 16-32 per ring,
  never closer than 64 units to the sky plane;
- `yard_height` (compound: 320, 10 m) fixes where a compound's sky starts: low buildings
  around a yard under a wide sky;
- `yard_setback` (compound: 4 cells, the roofscape's width) keeps other rooms that much
  further from an open area at layout time, so denser levels still leave rock to carve.
  It changed little in the current seeds (median carved share 88% -> 91%), but it's what
  makes the band possible when rooms crowd a yard.

**Interiors.** Each building now gets window openings in its side walls (48 units, the
slit height: you see and shoot through, nobody climbs through; the window's owner entry
goes first so the validator recognises it), a light, a counter against the back wall in
rooms of 3x3+ inside, and a health or ammo pickup near the middle: a reason to go in.
Tests: windows too low for a player, every building reward reachable.

**Measured** (compound seeds 1-8): 16 of 16 yards have buildings, 112 window openings.

## D26. Houses, not towers; and a versatility check

**Buildings.** Step 7's yard buildings stood up to 7 m tall in a 10 m yard, as tall as
the roofs around them, and from some corners read as pillars. They're now 5-9 cells wide
and 5-5.5 m high (interior 4-4.5 m), below the roofscape: houses around a yard. The widest
sizes are tried first, smaller ones when a yard is full. Compound seeds 1-8: still 16 of 16
yards with buildings.

**Versatility check** (Tom: "is this tool still versatile enough to produce many different
kinds of levels?"). After eight steps of architecture work aimed at four categories:
- every preset with every architecture style (none, gothic, industrial, bunker,
  compound), seeds 1-2: 70 of 70 levels valid, mostly on the first or second attempt. The
  new systems are switches on a recipe, not welded to the presets that introduced them;
- the presets are still distinct: 100% recognised from their fingerprints, separation
  1.58 (1.6 before the architecture work);
- levels without a style are byte-identical to before all of it (golden 26/26).
What the generator still can't make is listed in the answer to Tom, and is the input for
deciding what comes next.

## D27. A blockout you can edit part by part

**Why.** Tom: BadLevel's output is "a blockout that I could leave as is, just texture it,
or change some elements (in Blender: make a nice curved arc and replace part of the
level)", with curves and organic shapes as an option, not the only way. Until now the
.glb was one object split only by material, and the .map one undivided worldspawn: you
couldn't click "the vault of the great hall" and swap in your own.

**Decision.** Every architectural element is tagged where it is made (`Level.part_solid`
for solid elements such as piers, members, jambs and building walls; `part_cells` for
spaces such as niches, building interiors and portal mouths; `part_ceiling` for vaults and
roofs). Exporters file every face and brush under a part:
- **.glb**: `level_geometry` has one object per room and corridor (`room_5_megaroom`,
  `corridor_3`), each with children: `.structure` and one per element (`.vault`,
  `.piers`, `.members`, `.niches`, `.end feature`, `.portals`, `.building 2`,
  `.roofscape`...). Extras carry the area, kind, storey and the section's parameters.
  In Blender: select `room_5_megaroom.vault`, delete it, drop in your curved vault.
- **.map**: each element's brushes are a TrenchBroom group (`func_group` with
  `_tb_type`/`_tb_name`/`_tb_id`; compilers merge func_group into the world, so it compiles
  the same). Plain structure stays in worldspawn.

**Costs.** Faces and brushes no longer merge across parts: +0.5% triangles, the .glb ~15%
bigger from per-object overhead, and about 40% more brushes in a styled fortress (1,400 ->
2,000). Levels without architecture have no tags: their .map is byte-identical (golden
26/26) and their .glb only gains the per-room objects.

## D28. Three more vocabularies: egyptian, sci-fi, castle

**Why.** Tom and Noor: "let's go with all of them". A new style is now mostly data: a palette
of profiles per tier (architecture.py), a wall rhythm, members, recesses and an end feature
(walls.py), and a portal head (portals.py). Nine new profiles were needed; everything else
reuses the machinery of D18-D27 (piers, smooth surfaces, forced steps, recesses, parts).

| Style | Grand halls | Other rooms | Walls | End feature | Portals |
|---|---|---|---|---|---|
| egyptian | **hypostyle** (a forest of thick columns, a taller processional aisle lit by a clerestory), **corbelled**, **battered** | **lintel** (stone beams close together), corbelled, battered | false-door niches | a sanctuary niche, 2 deep | flat, under a massive lintel; pylon-thick jambs (2 deep) |
| scifi | **hull** (chamfered section with frames), **chamfered**, **lightwell** (a lit spine) | chamfered, lightwell, haunched | thin frames, panel bays with a console sill | a bulkhead door | a frame with its corners cut |
| castle | **trussed** (a steep open roof with tie-beams at the eaves), barrel, undercroft | **timber** (joists), barrel, trussed | buttresses (2 wide), window embrasures 2 deep | a fireplace | round arches |

Corbelling is stepped on purpose (courses are steps); battered walls and chamfers are
smooth; hull frames and tie-beams are forced steps laid over a smooth section. Corbelling
springs from at least half the room's height, otherwise a big hall turns into a ziggurat.

**Any recipe, any style.** `--style` on the command line (and `arch_style` in a recipe)
applies any vocabulary to any preset: `badlevel generate --recipe classic --style castle`.
A styles showcase renders one layout (fortress, seed 3) in all eight variants.

**Checked.** Egyptian, sci-fi and castle on fortress, industrial, compound and classic,
seeds 1-3: all valid, each style uses its whole palette; tests per style (grand halls get a
grand profile, walls get members and recesses). Unstyled levels unchanged.

**Bug found on the way: architecture could change the layout.** The styles showcase was
meant to show one layout in eight styles, but bunker, egyptian and castle produced
different room counts. Their first attempt had failed validation ("span too low (48)"): a
jamb or a wall member turns a room cell solid, the cell keeps the room's owner entry, and
when the window pass later cuts a firing slit through it, the slit's owner came *after*
the room's, so the validator saw a 48-unit room span instead of a window. The generator
then retried with a new layout. Windows now put their owner entry first. Checked over every
preset x every style x seeds 1-3 (147 levels): styled and unstyled levels now take the same
attempt and have the same layout; a test keeps it so. Unstyled levels unchanged.

## D29. One deliberate landmark per level

**Problem.** The first architectural review: "landmarks" were only the most visible rooms,
measured. Nothing in a level was a thing you'd point at ("meet me by the obelisk").

**Decision** (`badlevel/landmark.py`). A styled level's biggest grand space gets one
landmark, shaped by the style: gothic spire (stepped, corner pinnacles), castle keep
(crenellated), egyptian obelisk (on a plinth), industrial stack (a chimney through the roof),
sci-fi core (a column floor to ceiling with floating rings), compound water tower (a tank on a
shaft, open underneath), bunker monolith. Where the room is too low for the style's shape, a
great column on a plinth (floor to ceiling) takes its place.

It is as big as the room allows: the shape is scaled 3x / 2x / 1x with the room's size
(a first version at 1x was lost in a 40 m hall full of decks; in the review renders it read as
one more block), smaller when it doesn't fit. It is placed after the room's piers and walls
and before its features (they avoid it), as
near the middle as fits: a 2-cell walkable ring, 3 cells from any doorway, undone if the
floor would split. A free-standing top stays HEAD + 16 under the lowest ceiling of the
section around it, so a vault is never broken; full-height shapes are solid floor to
ceiling. It is a named part (`room:N/landmark`, a TrenchBroom group and a .glb object).
`arch_landmark: false` turns it off.

**Measured.** Every style x fortress, industrial, classic, bunker, compound, seeds 1-2: 66
of 70 levels have a landmark (the misses are halls whose floor is already a forest of
columns or piers: hypostyle, mushroom, multispan). The four themed presets with their own
styles, seeds 1-5: 19 of 20 (12 at 2x, 7 at 1x), and all 19 can be seen from a way into their room (a test
checks the line of sight to the landmark's near face). A showcase camera rule frames it
from the far side of its room.

## D30. Compiled with ericw-tools: sealed, no leaks

**What was run.** With Tom's permission, the official ericw-tools v0.18.1 Linux binaries
(qbsp, vis, light) on BadLevel maps: the 12 step-8 showcase levels, the 8 styles-showcase
levels, a plain level, then every preset x every style (and none) x seeds 1-2
(`scripts/compile_check.py`, 112 maps). TrenchBroom is a desktop GUI with no batch mode, so it
wasn't run; the .map format and its groups follow TrenchBroom's conventions, but opening one
in the editor is still for a person to do.

**Result.** Every map compiles; **no leaks** (the column model seals levels by construction,
and that holds through smooth sloped brushes, TrenchBroom groups, recesses, roofscapes,
overlooks); vis and light run cleanly (visibility data and lightmaps). The only warnings left
are expected: missing textures (BadLevel names textures like `bl_wall` and ships no Quake
wad), no wad key, and no deathmatch start in a single-player map.

**Two fixes the compiler asked for.** qbsp fills the outside of each collision hull (point,
player, shambler) from an entity standing in that hull's empty space, and warned "no
entities in empty space, no filling performed" on some maps: BadLevel's entities hug floors
and ceilings (pickups on the floor, lights 24 under the ceiling) and the player start stood
exactly 24 up, on the player hull's boundary. Now the start is 32 up (the player drops 8
units at spawn), and the .map gets an `info_null` (removed by the game at spawn) at the most
open point of the biggest room, 2 cells from any wall, 48 above the floor and 96 under the
ceiling. Both are in a unit test. These change every level's .map slightly (the start's
height and one extra entity); the golden baseline is reset after this commit.

## D31. Real Quake engines: textures in the .bsp, centred maps

**What Tom found.** The compiled fortress .bsp in the **Quake rerelease**: the player floating in
an empty brown void with monsters far off. In the original **WinQuake**: the game stopped with
"Bad surface extents".

**One root cause.** Reading the .bsp: the player start and all 239 entities were in open space
(inside the level). But all 6 textures had names and no pixels, because BadLevel shipped no
wad (the "texture not found" warnings, which D30 called expected). The rerelease draws such
surfaces as nothing: invisible walls. WinQuake swaps in its notexture with flags 0, so the
sky loses its TEX_SPECIAL flag; sky faces aren't subdivided by qbsp, and a 768-texel sky over
a yard then fails the 256-texel check. Emulating WinQuake's CalcSurfaceExtents (with the
notexture rule) on the .bsp reproduces the crash exactly.

**Fixes.**
- `badlevel/export/wad.py` makes `badlevel.wad`, a WAD2 of grey grid textures (Quake's
  palette starts with a 16-step grey ramp, so greys look the same in any Quake): one line per
  32-unit cell like the preview, 4 mip levels, a two-layer 256x128 sky, and every name the
  map uses (bl_floor, bl_wall, bl_ceiling, bl_door, bl_glass, bl_lift, bl_ladder, trigger,
  sky1). The .map's worldspawn names it (`wad`), and it is written next to every exported
  .map, so qbsp embeds real textures and TrenchBroom shows them.
- **Centred maps.** Original Quake's network protocol only carries positions within +-4096;
  BadLevel built levels from (0, 0) outwards (the fortress ran to x = 6240). The .map is now
  shifted to centre the level, in steps of 64 (the texture grid), recorded in worldspawn as
  `_badlevel_offset` (the IR and .glb keep level coordinates).
- `scripts/compile_check.py` now also checks each .bsp the way the original engine would:
  textures without pixels and surface extents over 256 (failures: they break modern engines
  too), and geometry beyond +-4096 (a note: only the original protocol is limited). Over
  every preset x style x seeds 1-2: all 112 compile with no leaks, real textures and no bad
  extents; 16 of them are bigger than 8192 units, which only original Quake can't carry.
  Tom: original-Quake support is welcome but not a must (BadLevel is mostly for other engines).

**Known limit: Quake 1 has no ladders.** Ladders are exported as `func_ladder` (Quake 2 /
Half-Life), which Quake 1 ignores, so a level whose critical path uses a ladder connector
(bunker, industrial) can't be finished in Quake 1, and yard rooftops can't be reached. A
Quake 1 target would replace ladders (connectors with lifts or stairs, rooftops without).

## D32. Room options: spaciousness, variety, round rooms, domes

**Why.** Tom, playing a compiled map: "sometimes the rooms feel a little bit cramped... I don't
want to lose it, what we have", then "it feels like a Quake 1 map, so I love it. But maybe some
features that allow you to also make bigger rooms, different sizes and shapes", "hexagonal,
more rounded rooms, dome ceilings". So the compact feel stays the default and the rest is opt-in.

**Two new dials, both 0 by default** (0 changes nothing: every preset, styled or not, is byte
for byte the same map; checked on 38 golden levels and tested).
- `spaciousness` (`--space`): rooms up to 1.5x wider and 1.25x taller, storeys 1.25x, corridors
  up to 1.5x higher and biased to 2-3 cells wide (narrow ones rare), compressed corridors less
  pinched, fewer crates, and architecture keeps more headroom over the floor (112 -> 192
  units, `arch_headroom`) so vaults and trusses don't eat the extra height. Measured over 5
  presets x 2 seeds: average room side 13.8 -> 19.7 cells, average headroom 6.2 -> 7.8 m,
  corridors 3.3 x 128 -> 4.3 x 196. Bigger rooms are harder to fit, so layout can take more
  attempts (up to 12 seen); every level still validates.
- `variety` (`--variety`): ordinary rooms are scaled by 2^u, u in [-0.6v, 1.0v] (cells 6-40,
  height by the square root), so small closets and big rooms sit side by side (room areas 49-406
  -> 32-957 cells); extra footprints join the preset's own: round, hex, diamond, Z, H; and a
  `dome` profile joins the palette of grand and major rooms in any style, three times as likely
  over centred plans (round, hex, octagon, diamond).

**Round rooms.** An ellipse tested at cell centres; each step of its outline becomes a 45-degree
half cell, so the wall reads as a polygon of 8+ sides, not a staircase. Grid-sound like every
shape (connected, no thin walls; tested at five sizes).

**Domes.** Rising from a springing line to a crown over the middle, `spring + rise * sqrt(1 - r^2)`.
A dome curves both ways, which planar ceiling corners can't follow, so it is built in
courses (3 to 7 rings, each about 1.5 cells wide and a multiple of 16 high): a corbelled or
beehive dome, which reads as a dome in a blockout and is easy to keep or
replace (it's the room's named ceiling part). Needs height to spare (rise 48 units or more above
headroom) and a room at least 7 cells across. A smooth dome is for curved patches (Quake 3) or a
mesh, noted for later.
A first version used 16-unit courses everywhere; on a 32-unit grid they broke up into a scatter
of cubes, so the rings are now fewer and wider.

## D33. Playtest fixes: the player is a box, fewer monsters, readable light

Tom played the roomy fortress (D32) in the Quake rerelease: stuck in passes too narrow to get
through, "are you trying to kill me? So many monsters", and too dark to judge the geometry.

**Clearance (`badlevel/clearance.py`).** The validator walked a point from cell to cell, but
Quake's player is a 32x32 box, and the engine expands walls by half that, so a gap exactly 32
units (one cell) wide closes to nothing. Door jambs that narrowed a 3-cell mouth to 1, jambs set
diagonally (corner to corner, the same 32-unit squeeze), a column one cell from a wall, the end
of a stair beside a wall, the arm of an L. Measured before the fix: 3 of 7 presets had rooms the
box couldn't reach (bunker seed 1: 13 of 15 rooms, the exit and the key).
- `box_reach` floods where a 2x2-cell box (64 units, the smallest grid gap a 32-unit hull passes)
  can stand and move: steps, drops, lifts, ladders as in the validator; doors count as open.
- `widen_pinches` runs after generation: where the point goes on and the box can't, it opens the
  solid cell beside the squeeze (a jamb, column, member or notch; its named part goes). It never
  joins two spaces that weren't already joined (not counting doors), never opens a cell beside a
  door, glass, a 45-degree wall or sky, and a cell outside a room's footprint goes to the
  corridor, not the room. About 8 cells per level; 0.1-0.3 s.
- The validator now also requires every room, key and exit to be reachable by the box. Over all
  presets x styles (roomy and plain) the repair fixed every case: the check never forced a retry.
- Levels without pinches are unchanged; the others change only where a cell opened.

**Monsters.** Rooms got one enemy per 28 cells (combat), so a 30x30 room under `--space` and
`--variety` held 21, and the roomy fortress 132 (28 ogres).
- Past 8 enemies a room's count grows with the square root (its side, not its floor):
  a huge room isn't a monster pit. Normal-sized rooms are unchanged.
- `--space` lowers the density with the floor area it adds.
- Quake skill levels: of every 4 monsters, all are in hard, 3 in normal, 2 in easy
  (spawnflags 256 / 512, which every Quake engine reads).
- `--monsters X` scales the count (0 = none, 0.5 = half).

**Light.** Lights hung 24 units under the ceiling, so in a 500-unit hall the floor got nothing,
and big rooms had only 4. Measured on the compiled roomy fortress (lightmap samples, 0-255): floor
median 15, 68% of floor samples under 24.
- Lights now hang at most 192 units over the floor, one every ~9 cells in big rooms, plus
  one under the vault of a tall room.
- Quake light keys: `"wait" "0.5"` on every light (fades over twice the distance; supported by
  id's light and ericw-tools), and worldspawn `"light" "24"` (ericw-tools' minimum light).
- Result: floor median 131, walls 85, 5% of floor samples under 24. Bounce lighting
  (`_bounce`) on top washed everything out to white, so it's left off.
