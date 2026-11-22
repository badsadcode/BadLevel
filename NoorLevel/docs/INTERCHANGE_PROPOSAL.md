# Proposal: station markers in Noor Interchange (for v0.5)

**Status:** proposal for review, from NoorLevel (2026-11-22). Nothing here is applied to NoorEngine's
spec ([NOOR_INTERCHANGE.md](https://github.com/badsadcode/NoorEngine/blob/main/docs/interchange/NOOR_INTERCHANGE.md),
v0.4). NoorLevel already writes these markers behind flags that default to off (`dressing`,
`station_entities`, `viewports`); Solitude is the first consumer. The decisions behind it are in
[DECISIONS.md](DECISIONS.md).

Spec 3.4 says: "New marker types: add them to this table first (one PR), then use them. Never invent a
marker silently." This is that PR, written down. Every addition is optional, so it's a minor version
(spec section 6).

## What already fits the spec (used as is)

| NoorLevel kind | `extras.noor.marker` | Spec fields used | Fields this proposal adds |
|---|---|---|---|
| `loot` | `pickup` | `class` (= `"loot"`) | `size` (`small`, `medium`, `large`) |
| `creature_spawn` | `spawn.enemy` | `class` (= `"creature"`) | `role` (`nest`, `ambush`, `patrol`), as `spawn.npc` already has |
| `hazard_zone` | `trigger` | `size` [x, y, z] m, `event` (= `"hazard"`) | `type` (`flooded`, `electrified`, `gas`) |
| `airlock` | `entrance` | `kind` | the kind value `airlock` |

## New marker types

Add to the table in 3.4:

| `marker` | Meaning | Common fields |
|---|---|---|
| `prop` | A thing that belongs here and that the engine supplies: "a console, about this big, facing this way". The generic "dress this spot" marker for any tool: an engine puts a real asset there, or a placeholder box from `footprint`. Adds no geometry of its own | `class`, `mount`, `footprint`, `variant` |
| `interact` | Something the player uses where it stands (a terminal, a switch, a junction box) | `class`, `on` |
| `hide` | A place to hide from enemies (in a locker, under a desk) | `class` (what you hide in), `on` |
| `viewport` | A small window onto the outside that the engine cuts and renders (a porthole onto the sea) | `mount`, `footprint` |

### `prop` in detail

- `class`: what it is, a lower-case word (`console`, `desk`, `locker`, `crate`, `pipe_run`...). Each tool
  documents its vocabulary; an engine maps classes to assets (`assets/props/<class>_<variant>.glb`).
- `mount`: `floor`, `wall` or `ceiling`. It fixes where the node's origin is:

  | `mount` | origin | the piece occupies (node-local axes, metres) |
  |---|---|---|
  | `floor` | centre of the footprint, on the floor | x in ±w/2, z in ±d/2, y in 0..h |
  | `wall` | on the wall surface, bottom centre of the back face | x in ±w/2, z in 0..d (out from the wall), y in 0..h |
  | `ceiling` | on the ceiling, top centre | x in ±w/2, z in ±d/2, y in −h..0 |

  This keeps the spec's pivot rule (2: props at the bottom centre of the footprint) for floor props and
  extends it to the other two the natural way: the origin is always where the piece touches the
  building.
- `footprint`: `[w, d, h]` in metres. `w` across (node-local x), `d` along the node's forward (+Z),
  `h` up (+Y). Runs (pipes, cables) have `w` = their length.
- Orientation: the node's rotation, yaw only. Forward is +Z (spec 3.4), so a console's screen, a locker's
  door, a sign's face point along +Z.
- `variant`: a small integer (NoorLevel: 0..7) from the generator's seed, so the engine picks between
  asset variants deterministically and wraps around when it has fewer.
- `tags`: `broken` (use the damaged variant), `blockout` (the generator also built a block of geometry
  here, named in `props.block`: hide it when the real asset is placed; it stays the collision fallback),
  `flat` (a decal or grate you walk over; never collide), `vent` (a grate over a duct opening the player
  can crawl through: never make it solid).
- `props.text`: text to show (a sign's room name).

### Fields common to markers in levels (proposed, optional)

- `on`: the `id` of another marker this one sits on or belongs to (`loot` on a `prop`, a `hide` in front
  of a locker). The engine moves or drops the pair together.
- `room`, `program`, `sector`: the level region the marker is in (a room id, what the room is for, the
  part of the level it belongs to). A generator of levels knows these and an engine uses them for loot
  tables, logs and dressing. Today NoorLevel writes them at the top level of the `noor` block; if the
  review prefers, they move into `props` (tool data) unchanged.

## Manifest

No change: `prop` is already an asset `kind`; the manifest's `markers` counts take the new types as keys.
(NoorLevel's .glb export writes no manifest yet; that's separate work.)

## Example node

```json
{
  "name": "prop_17",
  "translation": [12.5, 0.0, -30.3],
  "rotation": [0, 0.70710677, 0, 0.70710677],
  "extras": {
    "kind": "prop", "angle": 0, "room": 5, "program": "lab", "sector": "research",
    "class": "console", "mount": "floor", "footprint": [1.2, 0.6, 1.1], "variant": 3, "tags": ["broken"],
    "noor": { "v": 1, "marker": "prop", "id": "prop_17", "class": "console", "mount": "floor",
              "footprint": [1.2, 0.6, 1.1], "variant": 3, "room": 5, "program": "lab", "sector": "research",
              "tags": ["broken"] }
  }
}
```

The flat fields (`extras.kind` plus the same values) are NoorLevel's legacy form, which every NoorLevel
entity node has always had; Noor readers use `extras.noor`.
