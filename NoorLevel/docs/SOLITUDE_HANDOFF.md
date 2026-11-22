# NoorLevel stations: ready for Solitude

From Sage on the NoorLevel side, 2026-11-22. This answers `docs/STATIONS_BRIEF.md`. Paste it into the
Solitude session.

## Where it is

- Repo `badsadcode/BadLevel`, branch `claude/kind-hamilton-cb66z6`, C# library in `NoorLevel/`.
- All of the brief is done: Task A (dressing markers), Task B (gameplay markers), Task C (the station
  layout and corridor profiles), Task D (part metadata), and the runtime CLI items.

## The promise held

- `tests/NoorLevel.Tests/GoldenTests.cs` checks 64 outputs: facility, bunker, classic and dungeon, seeds
  1–8, glb and IR JSON.
- With every new flag off, they are byte-identical to before the work. The existing 12-station bank
  regenerates unchanged.
- 345 tests pass.

## Making a station

```bash
noorlevel-cli generate --preset station --seed 1234 --out user/stations/st_1234.glb --json-result --max-ms 5000
# {"ok":true,"file":"user/stations/st_1234.glb","ms":412,"seed":1234,"recipe":"station","rooms":8,"errors":[]}
```

Settings, each set with `--set name=value`:

| Setting | Values |
|---|---|
| `spine` | `line`, `ring`, `tree`, `auto` |
| `station_style` | `research`, `mining`, `military`, `derelict` |
| `map_size` | metres |
| `corridor_profile` | `box`, `chamfer`, `round` |
| `character_rooms` | |
| `tube_height` | |

The markers also work on old presets, so you can dress the existing facility and bunker style with:
`--set dressing=true --set station_entities=true --set program_set=station --set programs=true --set viewports=true --set part_meta=true`.

## The CLI

- Build it with `dotnet publish src/NoorLevel.Cli -p:PublishProfile=win-x64`. It writes `dist/cli-win-x64/`, one self-contained file of about 16 MB, precompiled.
- One run, including startup, takes 0.4–0.65 s. Medians over 5 seeds:

  | map_size | Time |
  |---|---|
  | 48 | 638 ms |
  | 56 | 419 ms |
  | 64 | 421 ms |
  | 72 | 468 ms |

- Exit codes:

  | Code | Meaning |
  |---|---|
  | 0 | ok |
  | 1 | no valid level (try another seed) |
  | 2 | bad parameter |
  | 3 | `--max-ms` passed |
  | 4 | could not write |

## What the importer reads

The importer reads the **consumer contract**: `NoorLevel/README.md`, section "Consumer contract: markers in the .glb".

- **Axes.** +Y up, metres.
- **Parts.** Parts are children of `level_geometry`. With `part_meta` on, each part node carries `room`, `kind`, `program`, `sector`, `style` and `profile`.
- **Station root.** The root node's extras hold `noor.station = {style, spine, seed}`.
- **Markers.** Markers are nodes named `<kind>_<n>`.
  - Each has a yaw quaternion, and its forward is local +Z.
  - Each has `extras.noor = {v: 1, marker, id, ...}`.
  - A footprint is `[w, d, h]` in metres.
  - The origin depends on the mount: floor, wall or ceiling.
- **Marker kinds and their Interchange names:**

  | Marker kind | Interchange name |
  |---|---|
  | `prop` | `prop` |
  | `loot` | `pickup` |
  | `log_terminal` | `interact` |
  | `creature_spawn` | `spawn.enemy` |
  | `hiding_spot` | `hide` |
  | `power_junction` | `interact` |
  | `hazard_zone` | `trigger` |
  | `airlock` | `entrance` |
  | `viewport` | `viewport` |
  | `bulkhead` | `door` (`opens: "slide"`) |

- **Interchange proposal.** Two marker names are new to Interchange: `hide` and `viewport`. They are proposed in `docs/INTERCHANGE_PROPOSAL.md` and not yet in the spec.

## Examples

`NoorLevel/examples/station/` holds 8 GLBs with plans, and `index.html` lists the prop and marker counts
for each.

## Where it differs from the brief

These are recorded in `docs/DECISIONS.md`, D8–D12.

- **Tubes are 3 cells wide, not 2.** The round profile needs that width for the player box.
- **Small maps run large.** A `map_size` of 48 can come out bigger, because a station needs its pods.
- **The airlock is on the wall behind the player start,** not the wall the player faces.
- **Viewports are markers only.** Nothing is cut in the wall; Solitude opens them.
- **Bulkheads are door markers.** Solitude builds the door.

## Next on the Solitude side

The importer is unblocked:

1. Read the markers and spawn props from `class`, `variant` and `footprint`.
2. Swap the pre-baked bank for runtime generation, or regenerate a bigger bank with the station preset.
