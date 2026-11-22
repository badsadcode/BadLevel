"""Generate NoorLevel/src/NoorLevel/ConfigDocs.cs: every GenConfig parameter with its group, type, range,
choices and description (from BadLevel's commented config.py, plus NoorLevel's own fields).
    python NoorLevel/tools/make_config_docs.py
The previewer's "All parameters" panel, the CLI (--set name=value, noorlevel-cli params) and
docs/USING_THE_DLL.md all read this one table."""
import os, re
ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
src = open(os.path.join(ROOT, "badlevel", "config.py"), encoding="utf-8").read()

# (min, max, step) for the numbers; choices for the strings. Names not here and not numbers are skipped.
RANGES = {
    "seed": None, "variant": (0, 20, 1),
    "main_rooms": (2, 30, 1), "side_rooms": (0, 20, 1), "loops": (0, 10, 1), "long_loop_chance": (0, 1, .05),
    "beat_spacing": (8, 40, 1), "beat_shapes": (0, 1, .05), "keys": (0, 2, 1), "jump_pads": (0, 3, .1), "vistas": (0, 6, 1),
    "market": (0, 1, .05), "chasms": (0, 3, .1), "skylights": (0, 2, .05), "floods": (0, 1, .05), "sumps": (0, 1, .05),
    "traps": (0, 1, .05), "vents": (0, 1, .05), "vent_height": (56, 128, 8),
    "storeys": (1, 6, 1), "storey_height": (192, 448, 32), "storey_change": (0, 1, .05), "drop_chance": (0, 1, .05),
    "max_drop": (64, 384, 16), "tall_room_chance": (0, 1, .05),
    "open_areas": (0, 4, 1), "open_buildings": (0, 1, .05), "yard_height": (0, 640, 32), "yard_setback": (0, 8, 1),
    "mega_rooms": (0, 2, 1),
    "hazards": (0, 3, .1), "light_variety": (0, 1, .05), "ramps": (0, 1, .05), "arch_axial": (0, 1, .05),
    "arch_headroom": (64, 256, 16), "spaciousness": (0, 1, .05),
    "merge_chance": (0, 1, .05), "max_hall_rooms": (2, 6, 1),
    "windows": (0, 20, 1), "glazed_chance": (0, 1, .05),
    "cell": None, "step_height": (8, 18, 1), "level_step": (16, 128, 16), "max_level": (0, 8, 1),
    "corridor_radius": (1, 3, 1), "corridor_height": (96, 256, 16), "max_corridor_radius": (1, 4, 1),
    "diagonal_chance": (0, 1, .05), "packing": (0, 1, .05), "diagonal_pull": (0, 1, .05), "room_gap": (2, 10, 1),
    "player_height": (48, 72, 1), "max_step": (16, 24, 1), "max_attempts": (10, 200, 10),
    "crate_scale": (0, 3, .1), "enemy_density": None, "pickup_density": None, "height_scale": (0.5, 2, .05),
    "size_variety": (0, 1, .05), "room_scale": (0.5, 2.5, .05),
}
CHOICES = {
    "mode": ["campaign", "arena"], "composition": ["tree", "hub", "central", "linear"],
    "arch_style": ["", "gothic", "industrial", "bunker", "compound", "castle", "egyptian", "scifi"],
    "hazard_kind": ["slime", "lava"], "program_set": ["facility", "dungeon", "wizard", "station"],
    "monster_set": None, "recipe": None,
}
WEIGHTS = {  # dictionaries shown as one slider per key
    "connector_weights": ["stairs", "stairwell", "lift", "ladder", "spiral"],
    "feature_weights": ["pillars", "platform", "pit", "balcony", "hazard", "chasm", "tower", "none"],
    "shape_weights": ["rect", "chamfer", "octagon", "ring", "plus", "L", "T", "U", "round", "hex", "diamond", "Z", "H"],
    "size_bias": ["small", "medium", "large"],
    "mega_styles": ["hangar", "works", "terraces"],
}
NOORLEVEL = [  # NoorLevel's own parameters (not in BadLevel)
    ("station", "part_meta", "bool", None, "Room facts on the .glb's part nodes (extras: room, kind, program, sector, style, profile), so a game can pick materials per room. Adds keys only; no names change."),
    ("noorlevel", "map_size", "int", (0, 400, 8), "The level's footprint: the longer side in metres (cells); 0 = whatever the recipe gives. The generator fits the level to it by changing room counts and sizes."),
    ("storeys", "stacking", "float", (0, 1, .05), "Rooms over rooms: 0 = now and then a room on another storey sits over or under a neighbour (as BadLevel), up to 1 = every such room tries that first. Needs more than one storey."),
    ("noorlevel", "pillars", "bool", None, "Free-standing columns anywhere: room pillars, yard colonnades, piers under vaults, corridor colonnades, the landmark's column. Off: none of them."),
    ("noorlevel", "check_clearance", "bool", None, "Check that a player-sized box (not just a point) fits everywhere it has to go, and widen squeezes."),
    ("station", "dressing", "bool", None, "Prop markers (Dressing): empty nodes saying 'a console, this big, facing this way, belongs here' for the game to fill with real assets, by room program; corridors get cable and pipe runs. Each furniture block also gets one (tag blockout). Adds no geometry."),
    ("station", "dressing_density", "float", (0, 1, .05), "How many props dressing places, 0..1 (scaled per program: storage is dense, an atrium sparse)."),
    ("station", "station_entities", "bool", None, "Gameplay markers (StationEntities): loot, log terminals, creature spawns (nest, ambush, patrol), hiding spots, power junctions, hazard zones, the airlock. Loot and spawns are always reachable."),
    ("station", "viewports", "bool", None, "Viewport markers: a small porthole on an outer wall of some rooms (nothing open behind it), for the game to cut and show the sea through."),
    ("geometry", "corridor_profile", "choice", ["box", "chamfer", "round"], "The corridors' cross-section: box (flat, square corners), chamfer (the ceiling's edges cut at 45 degrees) or round (the ceiling curves down to the walls, the floor's edges rise: a tube). Sloped ceiling corners, so every exporter has it; it never changes what's reachable. Vents stay boxes."),
    ("station", "layout_style", "choice", ["", "station"], "The layout: \"\" (rooms and corridors as BadLevel) or station (a deep-sea station: round and octagonal pods on a spine of tubes, the airlock on the top deck, the core at the bottom, stairs only)."),
    ("station", "spine", "choice", ["auto", "line", "ring", "tree"], "Station: the spine the pods hang on: one long line, a ring round a central hub, or a branching tree; auto picks one from the seed."),
    ("station", "station_style", "choice", ["research", "mining", "military", "derelict"], "Station: its character. Picks the architecture when arch_style is empty (research: scifi, mining: industrial, military: bunker, derelict: industrial with dark rooms) and which character rooms it gets."),
    ("station", "tube_height", "int", (80, 128, 8), "Station: the tubes' headroom at the crown, in units (32 = 1 m; 96 = 3 m)."),
    ("station", "character_rooms", "int", (0, 3, 1), "Station: how many character rooms (a moon pool, a domed lab, a flooded wing, a collapsed tube, a hydroponics bay)."),
    ("station", "character_weights", "weights", ["moon_pool", "dome_lab", "flooded_wing", "collapsed", "hydroponics"], "Station: weights for the character rooms (empty: by station_style)."),
]
GROUP_NAMES = {"mission graph": "mission", "storeys": "storeys", "composition": "composition", "open areas": "open areas",
               "architecture": "architecture", "merged halls": "halls", "openings": "openings", "geometry": "geometry",
               "player capsule used for validation": "player", "style levers (set by recipes; 1.0 / empty = the built-in defaults)": "style"}

rows, group = [], "general"
for line in src.splitlines():
    m = re.match(r"\s*# --- (.*?) -+$", line)
    if m:
        group = GROUP_NAMES.get(m.group(1).strip(), m.group(1).strip())
        continue
    m = re.match(r"\s{4}(\w+): (\w+) = (.*?)(?:\s+#\s*(.*))?$", line)
    if not m or m.group(1) in ("room_sizes",):
        continue
    name, typ, _, doc = m.groups()
    doc = (doc or "").strip()
    if typ == "dict":
        if name in WEIGHTS:
            rows.append((group, name, "weights", WEIGHTS[name], doc or "Weights (1 = the built-in default)"))
        continue
    if name in CHOICES:
        if CHOICES[name]:
            rows.append((group, name, "choice", CHOICES[name], doc))
        continue
    if typ in ("int", "float"):
        r = RANGES.get(name, "missing")
        if r is None:
            continue
        if r == "missing":
            raise SystemExit(f"no range for {name}")
        rows.append((group, name, typ, r, doc))
    elif typ == "bool":
        rows.append((group, name, "bool", None, doc))
    elif typ == "str":
        pass
for g, n, t, r, d in NOORLEVEL:
    rows.append((g, n, t, r, d))

def cs(s):
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'

out = ["// Generated by NoorLevel/tools/make_config_docs.py from BadLevel's config.py: don't edit by hand.",
       "using System.Collections.Generic;", "", "namespace NoorLevel", "{",
       "    /// <summary>One GenConfig parameter: its name (Python spelling, as recipes and --set use it), group, kind",
       "    /// (int, float, bool, choice, weights), range or choices, and what it does.</summary>",
       "    public sealed class ConfigParam",
       "    {",
       "        public string Group, Name, Kind, Doc;",
       "        public double Min, Max, Step;",
       "        public string[] Options;   // choice: the values; weights: the keys",
       "    }", "",
       "    public static class ConfigDocs", "    {",
       "        public static readonly ConfigParam[] All =", "        {"]
for g, n, t, r, d in rows:
    if t in ("int", "float"):
        out.append(f"            new ConfigParam {{ Group = {cs(g)}, Name = {cs(n)}, Kind = {cs(t)}, Min = {r[0]}, Max = {r[1]}, Step = {r[2]}, Doc = {cs(d)} }},")
    elif t in ("choice", "weights"):
        opts = ", ".join(cs(o) for o in r)
        out.append(f"            new ConfigParam {{ Group = {cs(g)}, Name = {cs(n)}, Kind = {cs(t)}, Options = new[] {{ {opts} }}, Min = 0, Max = 4, Step = 0.05, Doc = {cs(d)} }},")
    else:
        out.append(f"            new ConfigParam {{ Group = {cs(g)}, Name = {cs(n)}, Kind = \"bool\", Doc = {cs(d)} }},")
out += ["        };", "    }", "}", ""]
open(os.path.join(ROOT, "NoorLevel", "src", "NoorLevel", "ConfigDocs.cs"), "w", encoding="utf-8").write("\n".join(out))
print(len(rows), "parameters")

# ---- docs/PARAMETERS.md: the same table for people
def pascal(n):
    return "".join(p[:1].upper() + p[1:] for p in n.split("_") if p)

md = ["# NoorLevel parameters", "",
      "Generated by `tools/make_config_docs.py` (from BadLevel's commented `config.py` and NoorLevel's own fields):",
      "every setting of `GenConfig`. A preset and its ten dials fill them all in (`Recipes.Compile`); then set any",
      "of them yourself: in C# by field (`cfg.MapSize = 120`), by name (`Recipes.SetFields(cfg, new Dictionary<string, object> { [\"map_size\"] = 120 })`),",
      "on the command line (`--set map_size=120`, weights as `--set feature_weights.pillars=0`), or in the previewer",
      "(**All parameters**, with a search box). `noorlevel-cli params` prints this list too.", ""]
GROUP_TITLES = {"general": "General", "mission": "Mission (rooms, keys, loops, modes, extras)", "storeys": "Storeys",
                "composition": "Composition", "open areas": "Open areas and mega-rooms", "architecture": "Architecture",
                "halls": "Merged halls", "openings": "Openings", "geometry": "Geometry", "player": "The player (validation)",
                "style": "Style levers", "noorlevel": "NoorLevel's own", "station": "Stations (NoorLevel's own)"}
order = []
for g, *_ in rows:
    if g not in order:
        order.append(g)
for g in order:
    md += [f"## {GROUP_TITLES.get(g, g)}", "", "| Name | C# field | Values | What it does |", "|---|---|---|---|"]
    for gg, n, t, r, d in rows:
        if gg != g:
            continue
        if t in ("int", "float"):
            vals = f"{r[0]} – {r[1]}"
        elif t == "bool":
            vals = "true / false"
        elif t == "choice":
            vals = " / ".join(f"`{o}`" if o else '`""`' for o in r)
        else:
            vals = "weights for " + ", ".join(f"`{o}`" for o in r)
        md.append(f"| `{n}` | `{pascal(n)}` | {vals} | {d.replace('|', '/')} |")
    md.append("")
open(os.path.join(ROOT, "NoorLevel", "docs", "PARAMETERS.md"), "w", encoding="utf-8").write("\n".join(md))
print("docs/PARAMETERS.md written")
