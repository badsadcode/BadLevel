using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace NoorLevel
{
    /// <summary>What kind of place to generate, said in design terms (recipes.py): ten dials (0..1)
    /// plus optional <see cref="Overrides"/>. The dials describe intent; <see cref="Recipes.Compile"/>
    /// turns them into a <see cref="GenConfig"/>. Overrides are raw GenConfig fields, keyed by their
    /// Python names (snake_case, e.g. "arch_style", "corridor_width_bias"; PascalCase works too), and
    /// always win: a dictionary value merges into the field's existing dictionary.
    ///
    /// Dials: scale (room count and sizes), openness (merged halls, windows, glass, wide corridors),
    /// verticality (height variation, storeys, platforms / pits / balconies, tall rooms),
    /// claustrophobia (low ceilings, narrow corridors, few openings), combat (cover; 0 = none),
    /// exploration (side rooms, secrets, windows that preview what's ahead), loopiness (loops and
    /// alternative routes), regularity (rectangles and right angles vs octagons, rings, 45-degree
    /// corridors), spaciousness (bigger rooms, wider corridors, higher ceilings; 0 = the tight
    /// originals), variety (a wider spread of room sizes and more footprint shapes; 0 changes nothing).</summary>
    public sealed class Recipe
    {
        public string Name = "classic";
        public string Description = "";
        public double Scale = 0.5;
        public double Openness = 0.45;
        public double Verticality = 0.4;
        public double Claustrophobia = 0.3;
        public double Combat = 0.6;
        public double Exploration = 0.5;
        public double Loopiness = 0.5;
        public double Regularity = 0.35;
        public double Spaciousness = 0.5;   // Tom, after Quake 2: wider corridors and more spacious rooms
        public double Variety = 0.0;
        public Dictionary<string, object> Overrides = new Dictionary<string, object>();

        public static readonly string[] Dials =
            { "scale", "openness", "verticality", "claustrophobia", "combat", "exploration", "loopiness", "regularity", "spaciousness", "variety" };

        public double Get(string dial)
        {
            switch (dial)
            {
                case "scale": return Scale;
                case "openness": return Openness;
                case "verticality": return Verticality;
                case "claustrophobia": return Claustrophobia;
                case "combat": return Combat;
                case "exploration": return Exploration;
                case "loopiness": return Loopiness;
                case "regularity": return Regularity;
                case "spaciousness": return Spaciousness;
                case "variety": return Variety;
            }
            throw new ArgumentException("unknown dial " + dial);
        }

        public void Set(string dial, double v)
        {
            v = Math.Max(0, Math.Min(1, v));
            switch (dial)
            {
                case "scale": Scale = v; break;
                case "openness": Openness = v; break;
                case "verticality": Verticality = v; break;
                case "claustrophobia": Claustrophobia = v; break;
                case "combat": Combat = v; break;
                case "exploration": Exploration = v; break;
                case "loopiness": Loopiness = v; break;
                case "regularity": Regularity = v; break;
                case "spaciousness": Spaciousness = v; break;
                case "variety": Variety = v; break;
                default: throw new ArgumentException("unknown dial " + dial);
            }
        }

        /// <summary>Python's Recipe.dials(): dial name -> value.</summary>
        public Dictionary<string, double> DialValues() => Dials.ToDictionary(d => d, Get);

        public Recipe Copy()
        {
            var r = (Recipe)MemberwiseClone();
            r.Overrides = new Dictionary<string, object>(Overrides);
            return r;
        }

        /// <summary>Python's Recipe.from_dict: name, description, overrides, and dials either at the top
        /// level or under "dials".</summary>
        public static Recipe FromDict(IDictionary<string, object> d)
        {
            var r = new Recipe();
            if (d.TryGetValue("name", out var n) && n is string ns) r.Name = ns;
            if (d.TryGetValue("description", out var de) && de is string ds) r.Description = ds;
            foreach (var dial in Dials)
                if (d.TryGetValue(dial, out var v) && v != null) r.Set(dial, Convert.ToDouble(v));
            if (d.TryGetValue("dials", out var dl) && dl is IDictionary<string, object> dials)
                foreach (var kv in dials)
                    if (Array.IndexOf(Dials, kv.Key) >= 0) r.Set(kv.Key, Convert.ToDouble(kv.Value));
            if (d.TryGetValue("overrides", out var ov) && ov is IDictionary<string, object> ovs)
                r.Overrides = new Dictionary<string, object>(ovs);
            return r;
        }

        public Dictionary<string, object> ToDict()
        {
            var d = new Dictionary<string, object> { ["name"] = Name, ["description"] = Description };
            foreach (var dial in Dials) d[dial] = Get(dial);
            d["overrides"] = new Dictionary<string, object>(Overrides);
            return d;
        }
    }

    public static class Recipes
    {
        static double Lerp(double a, double b, double t) => a + (b - a) * t;

        static int Round(double v) => (int)Math.Round(v, MidpointRounding.ToEven);   // Python round()

        /// <summary>Make any compiled recipe a deathmatch map: no keys, secret or monsters; spawns (and,
        /// in BadLevel, weapons and power-ups) instead, more loops, jump-pad towers.</summary>
        public static void ArenaMode(GenConfig cfg)
        {
            cfg.Mode = "arena";
            cfg.Keys = 0;
            cfg.Secret = false;
            cfg.EnemyDensity = 0.0;
            cfg.PickupDensity = 0.0;
            cfg.Loops = Math.Max(cfg.Loops, 4);
            cfg.LongLoopChance = 0.0;           // short loops: a circuit, not a trek
            cfg.JumpPads = Math.Max(cfg.JumpPads, 1.0);
        }

        /// <summary>Spaciousness: s = 0 changes nothing; s = 1 makes rooms 1.5x bigger and 1.25x higher (and
        /// storeys 1.25x taller), corridors 1.5x higher and mostly 5 or 7 wide, keeps 192 units of headroom
        /// under shaped ceilings instead of 112, and thins the clutter.</summary>
        public static GenConfig Roomier(GenConfig cfg, double s)
        {
            s = Math.Max(0.0, Math.Min(1.0, s));
            if (s <= 0) return cfg;
            cfg.RoomScale *= Lerp(1.0, 1.5, s);
            cfg.HeightScale *= Lerp(1.0, 1.25, s);
            var b = cfg.CorridorWidthBias != null && cfg.CorridorWidthBias.Count > 0
                ? new Dictionary<int, double>(cfg.CorridorWidthBias)
                : new Dictionary<int, double> { [1] = 1.0, [2] = 1.0, [3] = 1.0 };
            double G(int k) => b.TryGetValue(k, out var v) ? v : 1.0;
            // 3-wide corridors fade out by s = 0.5 (secret passages and connector doors stay 3 wide)
            b[1] = G(1) * Math.Max(0.0, 1.0 - 2.0 * s);
            b[2] = G(2) * Lerp(1.0, 1.5, s);
            b[3] = G(3) * Lerp(1.0, 3.0, s);
            cfg.CorridorWidthBias = b;
            // on multi-storey levels the storey caps a room's height, whatever HeightScale says
            cfg.StoreyHeight = Round(cfg.StoreyHeight * Lerp(1.0, 1.25, s) / 32) * 32;
            cfg.CorridorHeight = Round(cfg.CorridorHeight * Lerp(1.0, 1.5, s) / 16) * 16;   // id maps: 128-192
            cfg.CrateScale *= Lerp(1.0, 0.6, s);
            cfg.ArchHeadroom = Round(Lerp(112, 192, s) / 16) * 16;
            // enemies come per square metre: the density falls with the floor area (same fight, more room)
            cfg.EnemyDensity /= Math.Pow(Lerp(1.0, 1.5, s), 2);
            cfg.Spaciousness = s;
            cfg.BeatShapes = Math.Max(cfg.BeatShapes, 1.0);        // rooms along long corridors
            cfg.BeatSpacing = Math.Min(cfg.BeatSpacing, 12);       // ... and more of them
            cfg.DiagonalPull = Math.Max(cfg.DiagonalPull, 1.0);    // diagonal corridors (Tom)
            return cfg;
        }

        /// <summary>Variety: v = 0 changes nothing; v = 1 spreads room sizes 0.66x - 2x per room, offers
        /// the extra footprints (round, hex, diamond, Z, H) alongside the preset's own, ramps for stairs,
        /// spiral stairs, and domes in big rooms of any style.</summary>
        public static GenConfig Varied(GenConfig cfg, double v)
        {
            v = Math.Max(0.0, Math.Min(1.0, v));
            if (v <= 0) return cfg;
            cfg.SizeVariety = v;
            cfg.ShapeWeights = cfg.ShapeWeights != null && cfg.ShapeWeights.Count > 0
                ? new Dictionary<string, double>(cfg.ShapeWeights) : new Dictionary<string, double> { ["rect"] = 1.0 };
            foreach (var (name, w) in new[] { ("round", 1.5), ("hex", 1.5), ("diamond", 1.0), ("Z", 1.2), ("H", 1.2) })
                cfg.ShapeWeights[name] = (cfg.ShapeWeights.TryGetValue(name, out var x) ? x : 0.0) + w * v;
            cfg.Ramps = Math.Max(cfg.Ramps, 0.6 * v);                                     // ramps for stairs
            cfg.ConnectorWeights = new Dictionary<string, double>(cfg.ConnectorWeights);
            cfg.ConnectorWeights["spiral"] = (cfg.ConnectorWeights.TryGetValue("spiral", out var sp) ? sp : 0.0) + 0.35 * v;   // spiral stairs
            cfg.ArchWeights = new Dictionary<string, double>(cfg.ArchWeights ?? new Dictionary<string, double>());
            cfg.ArchWeights["dome"] = (cfg.ArchWeights.TryGetValue("dome", out var dm) ? dm : 0.0) + 1.5 * v;      // domes in big rooms
            return cfg;
        }

        /// <summary>compile_recipe: dials -> GenConfig. Every mapping is monotonic.</summary>
        public static GenConfig Compile(Recipe r, int seed = 1)
        {
            double C(double v) => Math.Max(0, Math.Min(1, v));
            double sc = C(r.Scale), op = C(r.Openness), ve = C(r.Verticality), cl = C(r.Claustrophobia), co = C(r.Combat),
                   ex = C(r.Exploration), lo = C(r.Loopiness), rg = C(r.Regularity), sp = C(r.Spaciousness), va = C(r.Variety);
            var cfg = new GenConfig { Seed = seed, Recipe = r.Name };

            // scale
            cfg.MainRooms = Round(Lerp(3, 11, sc));
            cfg.SideRooms = Round(Lerp(0, 6, 0.6 * sc + 0.4 * ex));
            cfg.Keys = sc < 0.2 ? 0 : sc > 0.75 ? 2 : 1;
            cfg.SizeBias = new Dictionary<string, double> { ["small"] = Lerp(2.5, 0.4, sc), ["medium"] = 1.0, ["large"] = Lerp(0.3, 2.5, sc) };
            cfg.RoomScale = Lerp(0.85, 1.25, sc) * Lerp(1.1, 0.8, cl) * Lerp(0.95, 1.15, op);

            // verticality
            cfg.Storeys = ve < 0.5 ? 1 : ve < 0.7 ? 2 : ve < 0.9 ? 3 : 4;
            cfg.MaxLevel = Round(Lerp(0, 6, ve));
            cfg.TallRoomChance = Lerp(0.0, 0.7, ve);
            cfg.Galleries = ve >= 0.5;
            double vertFeatures = Lerp(0.2, 2.5, ve);

            // openness and claustrophobia (they pull against each other)
            cfg.MergeChance = Lerp(0.0, 0.6, op) * (1 - cl);
            cfg.Windows = Round(Lerp(0, 9, op) * (1 - 0.8 * cl) + 3 * ex * (1 - cl));
            cfg.GlazedChance = Lerp(0.1, 0.7, op) * (1 - cl);
            cfg.OpenAreas = op < 0.65 || cl > 0.6 ? 0 : op < 0.85 ? 1 : 2;
            cfg.MegaRooms = sc >= 0.75 && cl < 0.6 ? 1 : 0;
            cfg.HeightScale = Lerp(1.25, 0.72, cl);
            cfg.CorridorWidthBias = new Dictionary<int, double>
                { [1] = Lerp(3.0, 0.5, op) * Lerp(1, 3, cl), [2] = 1.0, [3] = Lerp(0.2, 3.0, op) * Lerp(1, 0.1, cl) };

            // combat (0 = none) and exploration
            cfg.EnemyDensity = Lerp(0.0, 1.6, co);
            cfg.CrateScale = Lerp(0.3, 1.8, co);
            cfg.PickupDensity = Lerp(0.6, 1.0, co) + 0.6 * ex;
            cfg.Secret = ex >= 0.25;

            // loops
            cfg.Loops = Round(Lerp(0, 5, lo));
            cfg.LongLoopChance = Lerp(0.1, 0.4, lo);

            // regularity
            cfg.ShapeWeights = new Dictionary<string, double>
            {
                ["rect"] = Lerp(0.5, 6.0, rg), ["chamfer"] = Lerp(1.5, 1.0, rg), ["octagon"] = Lerp(2.5, 0.15, rg),
                ["ring"] = Lerp(2.0, 0.1, rg), ["plus"] = Lerp(1.5, 0.6, rg), ["L"] = Lerp(1.2, 0.8, rg),
                ["T"] = Lerp(1.2, 0.6, rg), ["U"] = Lerp(1.2, 0.5, rg),
            };
            cfg.DiagonalChance = Lerp(0.8, 0.0, rg);

            cfg.FeatureWeights = new Dictionary<string, double>
            {
                ["platform"] = vertFeatures, ["pit"] = vertFeatures, ["balcony"] = vertFeatures,
                ["pillars"] = Lerp(0.6, 1.6, co), ["none"] = Lerp(1.5, 0.6, co),
            };

            SetFields(cfg, r.Overrides, merge: true, what: $"recipe {r.Name}: unknown override");
            Roomier(cfg, sp);                // after the overrides: it scales whatever the preset chose
            Varied(cfg, va);
            // decided last, from what the level ends up containing (overrides may add big spaces):
            // big spaces get a compressed approach, and the level is organised around them
            bool big = cfg.MegaRooms > 0 || cfg.OpenAreas > 0;
            if (!HasField(r.Overrides, "pacing_contrast")) cfg.PacingContrast = big;
            if (!HasField(r.Overrides, "composition")) cfg.Composition = big ? "central" : "tree";
            return cfg;
        }

        // ------------------------------------------------------------------
        // setting GenConfig fields by name
        // ------------------------------------------------------------------
        /// <summary>"arch_style" (or "ArchStyle") -> the GenConfig field.</summary>
        public static FieldInfo ConfigField(string name)
        {
            string pascal = string.Concat(name.Split('_').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1)));
            return typeof(GenConfig).GetField(pascal, BindingFlags.Public | BindingFlags.Instance)
                ?? typeof(GenConfig).GetField(name, BindingFlags.Public | BindingFlags.Instance);
        }

        static bool HasField(IDictionary<string, object> fields, string pyName)
        {
            var target = ConfigField(pyName);
            return fields.Keys.Any(k => ConfigField(k) == target);
        }

        /// <summary>Set GenConfig fields by name (Python's setattr loop). With <paramref name="merge"/>, a
        /// dictionary value merges into the field's existing dictionary (compile_recipe's overrides);
        /// without it, it replaces it (surprise's extras). Unknown names throw.</summary>
        public static void SetFields(GenConfig cfg, IDictionary<string, object> fields, bool merge = false, string what = "unknown config field")
        {
            if (fields == null) return;
            foreach (var kv in fields)
            {
                var f = ConfigField(kv.Key) ?? throw new ArgumentException($"{what} '{kv.Key}'");
                f.SetValue(cfg, ConvertTo(f.FieldType, kv.Value, merge ? f.GetValue(cfg) : null));
            }
        }

        static object ConvertTo(Type t, object v, object existing)
        {
            if (v == null) return null;
            if (t.IsInstanceOfType(v) && !(v is IDictionary && existing is IDictionary)) return CopyIfDict(v);
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>) && v is IDictionary src)
            {
                var args = t.GetGenericArguments();
                var outp = (IDictionary)Activator.CreateInstance(t);
                if (existing is IDictionary old) foreach (DictionaryEntry e in old) outp[e.Key] = e.Value;
                foreach (DictionaryEntry e in src) outp[ConvertTo(args[0], e.Key, null)] = ConvertTo(args[1], e.Value, null);
                return outp;
            }
            if (t == typeof((int, int, int, int)) && v is IList l && l.Count == 4)
                return (Convert.ToInt32(l[0]), Convert.ToInt32(l[1]), Convert.ToInt32(l[2]), Convert.ToInt32(l[3]));
            if (t == typeof(int) && v is string si) return int.Parse(si);
            if (t == typeof(bool) && !(v is bool)) return Convert.ToDouble(v) != 0;
            return Convert.ChangeType(v, t, System.Globalization.CultureInfo.InvariantCulture);
        }

        static object CopyIfDict(object v) =>
            v is IDictionary d && v.GetType().IsGenericType ? Activator.CreateInstance(v.GetType(), d) : v;

        // ------------------------------------------------------------------
        // presets: each must be recognisable at a glance
        // ------------------------------------------------------------------
        static Recipe R(string name, string desc, double scale, double openness, double verticality, double claustrophobia,
                        double combat, double exploration, double loopiness, double regularity, double spaciousness = 0.5,
                        double variety = 0.0, Dictionary<string, object> overrides = null) =>
            new Recipe
            {
                Name = name, Description = desc, Scale = scale, Openness = openness, Verticality = verticality,
                Claustrophobia = claustrophobia, Combat = combat, Exploration = exploration, Loopiness = loopiness,
                Regularity = regularity, Spaciousness = spaciousness, Variety = variety,
                Overrides = overrides ?? new Dictionary<string, object>(),
            };

        static Dictionary<string, double> D(params (string, double)[] kv) => kv.ToDictionary(p => p.Item1, p => p.Item2);
        static Dictionary<int, double> DI(params (int, double)[] kv) => kv.ToDictionary(p => p.Item1, p => p.Item2);


        public static readonly IReadOnlyDictionary<string, Recipe> Presets = new[]
        {
            R("compact", "A few small rooms and short corridors. Restrained, believable, quick to play.",
              scale: 0.1, openness: 0.3, verticality: 0.2, claustrophobia: 0.4, combat: 0.5, exploration: 0.2,
              loopiness: 0.2, regularity: 0.6, overrides: new Dictionary<string, object> { ["windows"] = 1, ["max_level"] = 1, ["composition"] = "linear" }),
            R("classic", "Quake-style: varied rooms, height changes, loops, a key and a secret.",
              scale: 0.5, openness: 0.45, verticality: 0.45, claustrophobia: 0.3, combat: 0.6, exploration: 0.5,
              loopiness: 0.5, regularity: 0.35),
            R("bunker", "Underground: in through a hatch from the surface and down, storey by storey. Low, " +
                        "narrow, orthogonal concrete rooms, firing slits, ladders and lifts, and at the bottom one " +
                        "deep chamber cut from the rock. Tense and close.",
              scale: 0.45, openness: 0.1, verticality: 0.55, claustrophobia: 0.9, combat: 0.8, exploration: 0.3,
              loopiness: 0.3, regularity: 0.95,
              overrides: new Dictionary<string, object>
              {
                  ["arch_style"] = "bunker", ["underground"] = true, ["storeys"] = 3, ["mega_rooms"] = 1,
                  ["mega_styles"] = D(("hangar", 2.0), ("works", 1.0), ("terraces", 0.0)), ["glazed_chance"] = 0.0, ["windows"] = 4,
                  ["tall_room_chance"] = 0.0, ["room_scale"] = 0.75,
                  ["size_bias"] = D(("small", 2.5), ("medium", 1.0), ("large", 0.15)),
                  ["connector_weights"] = D(("stairs", 0.15), ("stairwell", 0.25), ("lift", 0.3), ("ladder", 0.3)),
                  ["feature_weights"] = D(("balcony", 0.0)), ["merge_chance"] = 0.05, ["composition"] = "linear",
              }),
            R("fortress", "Tall, ordered, defensive. An inner courtyard, several storeys climbed by " +
                          "stairwells, great halls, galleries and arrow slits.",
              scale: 0.7, openness: 0.35, verticality: 0.92, claustrophobia: 0.25, combat: 0.8, exploration: 0.4,
              loopiness: 0.45, regularity: 0.9,
              overrides: new Dictionary<string, object>
              {
                  ["arch_style"] = "gothic", ["glazed_chance"] = 0.0, ["windows"] = 8, ["height_scale"] = 1.15, ["merge_chance"] = 0.4, ["open_areas"] = 1,
                  ["connector_weights"] = D(("stairs", 0.25), ("stairwell", 0.65), ("lift", 0.05), ("ladder", 0.05)),
                  ["feature_weights"] = D(("balcony", 2.5), ("pillars", 1.5)),
                  ["corridor_width_bias"] = DI((1, 0.5), (2, 2.0), (3, 1.5)),
                  ["mega_rooms"] = 1, ["mega_styles"] = D(("terraces", 3.0), ("hangar", 0.5), ("works", 0.0)),
              }),
            R("industrial", "Big tall halls with platforms and catwalk balconies, a loading yard, lifts " +
                            "and ladders, wide service corridors, control-room glass, lots of clutter.",
              scale: 0.8, openness: 0.6, verticality: 0.75, claustrophobia: 0.15, combat: 0.6, exploration: 0.5,
              loopiness: 0.6, regularity: 0.7,
              overrides: new Dictionary<string, object>
              {
                  ["arch_style"] = "industrial", ["height_scale"] = 1.35, ["glazed_chance"] = 0.65, ["windows"] = 8, ["crate_scale"] = 2.0, ["room_scale"] = 1.4,
                  ["open_areas"] = 1,
                  ["size_bias"] = D(("small", 0.5), ("medium", 1.0), ("large", 3.0)),
                  ["connector_weights"] = D(("stairs", 0.1), ("stairwell", 0.25), ("lift", 0.35), ("ladder", 0.3)),
                  ["feature_weights"] = D(("platform", 2.5), ("balcony", 2.5), ("pillars", 2.0)),
                  ["corridor_width_bias"] = DI((1, 0.4), (2, 1.0), (3, 2.5)), ["diagonal_chance"] = 0.2,
                  ["mega_rooms"] = 1, ["mega_styles"] = D(("works", 3.0), ("hangar", 1.0), ("terraces", 0.0)),
                  ["composition"] = "hub",
              }),
            R("compound", "Open and spread out: courtyards under the sky, large merged halls, wide " +
                          "connections, many windows, one level.",
              scale: 0.7, openness: 0.95, verticality: 0.2, claustrophobia: 0.0, combat: 0.6, exploration: 0.6,
              loopiness: 0.8, regularity: 0.55,
              overrides: new Dictionary<string, object>
              {
                  ["arch_style"] = "compound", ["open_roofscape"] = true, ["open_buildings"] = 0.9, ["yard_setback"] = 4, ["yard_height"] = 320,
                  ["merge_chance"] = 0.65, ["max_hall_rooms"] = 4, ["windows"] = 10, ["glazed_chance"] = 0.5,
                  ["height_scale"] = 1.4, ["crate_scale"] = 1.8, ["room_scale"] = 1.45,
                  ["size_bias"] = D(("small", 0.4), ("medium", 1.0), ("large", 2.5)),
                  ["corridor_width_bias"] = DI((1, 0.3), (2, 1.0), (3, 3.0)),
                  ["mega_rooms"] = 1, ["mega_styles"] = D(("hangar", 3.0), ("works", 0.5), ("terraces", 0.0)),
              }),
            R("techbase", "Doom-era techbase: one level, never a room over a room; height from steps, " +
                          "platforms and pits; angled walls and diagonal corridors; flat ceilings at many " +
                          "heights; outdoor yards, slime pools, keycards, bright and dim areas.",
              scale: 0.6, openness: 0.7, verticality: 0.45, claustrophobia: 0.15, combat: 0.85, exploration: 0.75,
              loopiness: 0.6, regularity: 0.1,
              overrides: new Dictionary<string, object>
              {
                  ["monster_set"] = "base", ["storeys"] = 1, ["max_level"] = 5, ["open_areas"] = 2, ["tall_room_chance"] = 0.45, ["packing"] = 1.0,
                  ["hazards"] = 1.0, ["hazard_kind"] = "slime", ["light_variety"] = 1.0, ["windows"] = 8,
                  ["glazed_chance"] = 0.3, ["mega_rooms"] = 0, ["keys"] = 2,
                  ["shape_weights"] = D(("octagon", 2.0), ("chamfer", 2.5), ("hex", 1.0), ("diamond", 1.0), ("ring", 0.5), ("rect", 1.5)),
                  ["feature_weights"] = D(("platform", 2.0), ("pit", 1.0), ("hazard", 2.0), ("balcony", 0.0)),
              }),
            R("runekeep", "Quake-1 flavour: a dark gothic keep with bits of base in it. Steep and vertical, " +
                          "packed tight, lava pits, secrets everywhere, narrow ledges over big halls, dim light.",
              scale: 0.65, openness: 0.4, verticality: 0.75, claustrophobia: 0.35, combat: 0.85, exploration: 0.8,
              loopiness: 0.55, regularity: 0.4,
              overrides: new Dictionary<string, object>
              {
                  ["monster_set"] = "keep", ["arch_style"] = "gothic", ["packing"] = 1.0, ["hazards"] = 1.0, ["hazard_kind"] = "lava",
                  ["light_variety"] = 0.8, ["windows"] = 4, ["glazed_chance"] = 0.0, ["keys"] = 2,
                  ["connector_weights"] = D(("stairs", 0.4), ("stairwell", 0.4), ("lift", 0.2), ("ladder", 0.0)),
                  ["feature_weights"] = D(("balcony", 2.0), ("pit", 1.5), ("platform", 1.5), ("hazard", 1.5)),
                  ["mega_rooms"] = 1, ["mega_styles"] = D(("terraces", 2.0), ("hangar", 0.5), ("works", 0.0)),
              }),
            R("outpost", "Quake-2 flavour: a military-industrial base. Several storeys climbed by lifts and " +
                         "ladders, wide service halls, catwalks, glass control rooms, crates, slime, a loading yard.",
              scale: 0.75, openness: 0.55, verticality: 0.7, claustrophobia: 0.2, combat: 0.85, exploration: 0.5,
              loopiness: 0.5, regularity: 0.75,
              overrides: new Dictionary<string, object>
              {
                  ["monster_set"] = "base", ["arch_style"] = "industrial", ["packing"] = 0.8, ["hazards"] = 0.8, ["hazard_kind"] = "slime",
                  ["glazed_chance"] = 0.7, ["windows"] = 8, ["crate_scale"] = 1.8, ["open_areas"] = 1,
                  ["connector_weights"] = D(("stairs", 0.15), ("stairwell", 0.2), ("lift", 0.4), ("ladder", 0.25)),
                  ["feature_weights"] = D(("platform", 2.0), ("balcony", 2.5), ("hazard", 1.0)),
                  ["mega_rooms"] = 1, ["mega_styles"] = D(("works", 3.0), ("hangar", 1.5), ("terraces", 0.0)),
              }),
            R("grand", "Unreal-like grandeur: huge halls and open yards under the sky, tall vaults, castle " +
                       "architecture, long views through big windows, landmarks to steer by, room to breathe.",
              scale: 0.85, openness: 0.95, verticality: 0.75, claustrophobia: 0.0, combat: 0.55, exploration: 0.85,
              loopiness: 0.6, regularity: 0.45, spaciousness: 1.0, variety: 0.8,
              overrides: new Dictionary<string, object>
              {
                  ["monster_set"] = "keep", ["arch_style"] = "castle", ["open_areas"] = 2, ["mega_rooms"] = 1, ["windows"] = 14,
                  ["glazed_chance"] = 0.6, ["tall_room_chance"] = 0.8, ["height_scale"] = 1.5, ["merge_chance"] = 0.5, ["vistas"] = 3,
                  ["mega_styles"] = D(("terraces", 2.5), ("hangar", 1.5), ("works", 0.0)),
                  ["size_bias"] = D(("small", 0.3), ("medium", 1.0), ("large", 3.0)),
              }),
            R("facility", "An underground research facility in the Half-Life / Abiotic Factor tradition: in " +
                          "from the surface and down, sectors behind keycard checkpoints (admin, research, " +
                          "storage, maintenance, reactor), rooms with a job (offices, labs, server rooms, " +
                          "warehouses), lifts and stairwells, vents as the other way round.",
              scale: 0.8, openness: 0.35, verticality: 0.75, claustrophobia: 0.4, combat: 0.55, exploration: 0.6,
              loopiness: 0.6, regularity: 0.85, spaciousness: 0.35,
              overrides: new Dictionary<string, object>
              {
                  ["monster_set"] = "base", ["underground"] = true, ["size_bias"] = D(("small", 2.0), ("medium", 1.5), ("large", 0.6)),
                  ["storeys"] = 3, ["keys"] = 2, ["programs"] = true, ["vents"] = 0.9,
                  ["arch_style"] = "industrial", ["open_areas"] = 0, ["mega_rooms"] = 0, ["windows"] = 10,
                  ["glazed_chance"] = 0.9, ["light_variety"] = 0.5, ["diagonal_chance"] = 0.15,
                  ["connector_weights"] = D(("stairs", 0.15), ("stairwell", 0.35), ("lift", 0.4), ("ladder", 0.1)),
              }),
            R("complex", "The facility above ground: office blocks, labs and warehouses round a loading yard, " +
                         "two storeys, keycard sectors, vents, glass everywhere.",
              scale: 0.7, openness: 0.55, verticality: 0.55, claustrophobia: 0.25, combat: 0.5, exploration: 0.6,
              loopiness: 0.6, regularity: 0.9, spaciousness: 0.35,
              overrides: new Dictionary<string, object>
              {
                  ["monster_set"] = "base", ["storeys"] = 2, ["keys"] = 2, ["programs"] = true,
                  ["size_bias"] = D(("small", 2.0), ("medium", 1.5), ("large", 0.6)), ["vents"] = 0.7, ["arch_style"] = "bunker",
                  ["open_areas"] = 1, ["open_buildings"] = 0.5, ["mega_rooms"] = 0, ["windows"] = 14, ["glazed_chance"] = 0.95,
                  ["diagonal_chance"] = 0.1,
                  ["connector_weights"] = D(("stairs", 0.3), ("stairwell", 0.35), ("lift", 0.35), ("ladder", 0.0)),
              }),
            R("dungeon", "A dungeon after Noor's concept art: down from a gatehouse through the keep, the " +
                         "prison, the catacombs and the deep; torch-lit stone, bridges over spiked chasms, light " +
                         "falling through grates, traps in the corridors, crypts, cells and a throne room.",
              scale: 0.7, openness: 0.35, verticality: 0.6, claustrophobia: 0.55, combat: 0.75, exploration: 0.85,
              loopiness: 0.45, regularity: 0.7, spaciousness: 0.3,
              overrides: new Dictionary<string, object>
              {
                  ["monster_set"] = "dungeon", ["underground"] = true, ["storeys"] = 2, ["keys"] = 2, ["programs"] = true, ["program_set"] = "dungeon",
                  ["arch_style"] = "castle", ["torches"] = true, ["chasms"] = 1.5, ["skylights"] = 0.3, ["traps"] = 0.5, ["floods"] = 0.5, ["sumps"] = 0.8,
                  ["windows"] = 2, ["glazed_chance"] = 0.0, ["open_areas"] = 0, ["mega_rooms"] = 0, ["diagonal_chance"] = 0.1,
                  ["light_variety"] = 0.4, ["crate_scale"] = 0.5, ["tall_room_chance"] = 0.5,
                  ["size_bias"] = D(("small", 1.5), ("medium", 1.5), ("large", 0.8)),
                  ["connector_weights"] = D(("stairs", 0.4), ("stairwell", 0.35), ("spiral", 0.25), ("lift", 0.0), ("ladder", 0.0)),
              }),
            R("wizard", "A wizard's tower after Noor's idea: one tall round hall with a stair spiralling up " +
                        "inside its wall round an open void, flights crossing the void at every height like an " +
                        "Escher drawing, the sky at the top; libraries, alchemy labs and a summoning chamber off " +
                        "its landings, the observatory at the top.",
              scale: 0.55, openness: 0.45, verticality: 0.9, claustrophobia: 0.3, combat: 0.6, exploration: 0.8,
              loopiness: 0.2, regularity: 0.5, spaciousness: 0.4,
              overrides: new Dictionary<string, object>
              {
                  ["monster_set"] = "wizard", ["tower"] = true, ["storeys"] = 4, ["keys"] = 2, ["composition"] = "central", ["programs"] = true,
                  ["program_set"] = "wizard", ["arch_style"] = "gothic", ["torches"] = true, ["skylights"] = 0.3,
                  ["open_areas"] = 0, ["mega_rooms"] = 0, ["windows"] = 4, ["glazed_chance"] = 0.5, ["merge_chance"] = 0.0,
                  ["shape_weights"] = D(("round", 2.0), ("octagon", 2.0), ("rect", 1.0)),
              }),
            R("market", "A goblin market after Noor's idea: open yards laid out in streets of stalls (counters, " +
                        "booths under roofs, a merchant in each and what they sell), a well in the plaza, halls and " +
                        "storerooms between the yards.",
              scale: 0.6, openness: 0.85, verticality: 0.3, claustrophobia: 0.1, combat: 0.5, exploration: 0.7,
              loopiness: 0.6, regularity: 0.6, spaciousness: 0.5,
              overrides: new Dictionary<string, object>
              {
                  ["monster_set"] = "keep", ["open_areas"] = 3, ["market"] = 1.0, ["storeys"] = 1, ["keys"] = 1, ["composition"] = "hub",
                  ["arch_style"] = "compound", ["open_roofscape"] = true, ["yard_setback"] = 3, ["mega_rooms"] = 0,
                  ["windows"] = 6, ["glazed_chance"] = 0.0, ["torches"] = true,
                  ["room_sizes"] = new Dictionary<string, (int, int, int, int)>
                  {
                      ["small"] = (6, 8, 128, 160), ["medium"] = (9, 12, 160, 224),
                      ["large"] = (14, 18, 256, 320), ["yard"] = (22, 30, 320, 320),
                      ["mega"] = (24, 32, 448, 512),
                  },
              }),
            R("arena", "Deathmatch in the arena-shooter tradition: a tight circuit of rooms round a central " +
                       "hall, loops everywhere and no dead ends, two storeys joined by stairs, lifts and drops, " +
                       "jump pads up to high ground, the power-ups where the fights should be.",
              scale: 0.45, openness: 0.6, verticality: 0.8, claustrophobia: 0.2, combat: 0.8, exploration: 0.3,
              loopiness: 1.0, regularity: 0.4, spaciousness: 0.6,
              overrides: new Dictionary<string, object>
              {
                  ["mode"] = "arena", ["keys"] = 0, ["secret"] = false, ["storeys"] = 2, ["composition"] = "central",
                  ["loops"] = 5, ["long_loop_chance"] = 0.0, ["jump_pads"] = 1.5, ["drop_chance"] = 1.0, ["tall_room_chance"] = 0.7, ["height_scale"] = 1.4,
                  ["enemy_density"] = 0.0, ["pickup_density"] = 0.0, ["windows"] = 6, ["open_areas"] = 0, ["mega_rooms"] = 0,
                  ["connector_weights"] = D(("stairs", 0.4), ("stairwell", 0.3), ("lift", 0.3), ("ladder", 0.0)),
                  ["feature_weights"] = D(("platform", 2.0), ("balcony", 2.0), ("pit", 0.5), ("pillars", 1.5), ("none", 0.3)),
              }),
            R("ctf", "Capture the flag: two bases, mirror images of each other, joined by two or three " +
                     "lanes with a midfield chamber on the seam; each team's flag in the room farthest from the " +
                     "lanes, team spawns round it, one quad in the middle.",
              scale: 0.35, openness: 0.6, verticality: 0.7, claustrophobia: 0.2, combat: 0.8, exploration: 0.3,
              loopiness: 1.0, regularity: 0.5, spaciousness: 0.6,
              overrides: new Dictionary<string, object>
              {
                  ["mode"] = "arena", ["mirror"] = true, ["keys"] = 0, ["secret"] = false, ["storeys"] = 2, ["composition"] = "central",
                  ["loops"] = 4, ["long_loop_chance"] = 0.0, ["jump_pads"] = 1.5, ["drop_chance"] = 1.0,
                  ["tall_room_chance"] = 0.7, ["height_scale"] = 1.4, ["enemy_density"] = 0.0, ["pickup_density"] = 0.0,
                  ["windows"] = 4, ["open_areas"] = 0, ["mega_rooms"] = 0,
                  ["connector_weights"] = D(("stairs", 0.4), ("stairwell", 0.3), ("lift", 0.3), ("ladder", 0.0)),
                  ["feature_weights"] = D(("platform", 2.0), ("balcony", 2.0), ("pit", 0.5), ("pillars", 1.5), ("none", 0.3)),
              }),
            R("duel", "One-on-one: a small deathmatch map, five or six rooms in a ring, one storey with " +
                      "platforms and a jump-pad tower, every room two ways out, one power-up to fight over.",
              scale: 0.25, openness: 0.5, verticality: 0.55, claustrophobia: 0.3, combat: 0.8, exploration: 0.2,
              loopiness: 1.0, regularity: 0.5, spaciousness: 0.5,
              overrides: new Dictionary<string, object>
              {
                  ["mode"] = "arena", ["keys"] = 0, ["secret"] = false, ["storeys"] = 1, ["composition"] = "central",
                  ["loops"] = 3, ["long_loop_chance"] = 0.0, ["jump_pads"] = 2.0, ["tall_room_chance"] = 0.8, ["height_scale"] = 1.3,
                  ["enemy_density"] = 0.0, ["pickup_density"] = 0.0, ["open_areas"] = 0, ["mega_rooms"] = 0,
                  ["feature_weights"] = D(("platform", 2.0), ("balcony", 1.0), ("pit", 0.5), ("none", 0.2)),
              }),
            R("explore", "No combat. Vertical, open, full of side rooms, secrets, views ahead and loops: " +
                         "for exploration, walking sims and architectural blockouts.",
              scale: 0.6, openness: 0.6, verticality: 0.7, claustrophobia: 0.2, combat: 0.0, exploration: 1.0,
              loopiness: 0.6, regularity: 0.35,
              overrides: new Dictionary<string, object>
              {
                  ["mega_rooms"] = 1, ["mega_styles"] = D(("terraces", 2.0), ("works", 1.0), ("hangar", 0.5)),
                  ["composition"] = "hub",
              }),
            // NoorLevel's own (not in BadLevel): for Solitude's deep-sea stations
            R("station", "A deep-sea station: round and octagonal pods on a spine of pressure tubes (one long line, " +
                         "a ring round a hub, or a branching tree), the docking airlock on the top deck and the " +
                         "core at the bottom, decks joined by stair tubes, a moon pool, a domed lab or a flooded wing.",
              scale: 0.55, openness: 0.2, verticality: 0.7, claustrophobia: 0.7, combat: 0.5, exploration: 0.6,
              loopiness: 0.3, regularity: 0.8, spaciousness: 0.3,
              overrides: new Dictionary<string, object>
              {
                  ["layout_style"] = "station", ["spine"] = "auto", ["station_style"] = "research", ["corridor_profile"] = "round",
                  ["tube_height"] = 96, ["character_rooms"] = 2, ["map_size"] = 56, ["storeys"] = 3, ["keys"] = 1, ["secret"] = true,
                  ["main_rooms"] = 7, ["side_rooms"] = 4, ["loops"] = 1, ["long_loop_chance"] = 0.0,
                  ["programs"] = true, ["program_set"] = "station", ["monster_set"] = "base",
                  // the station's own markers (Dressing, StationEntities) and room facts on the .glb's parts
                  ["dressing"] = true, ["station_entities"] = true, ["viewports"] = true, ["part_meta"] = true,
                  ["windows"] = 0, ["glazed_chance"] = 0.0, ["vistas"] = 0, ["open_areas"] = 0, ["mega_rooms"] = 0, ["merge_chance"] = 0.0,
                  ["galleries"] = false, ["corridor_beats"] = false, ["vents"] = 0.0, ["drop_chance"] = 0.0, ["ramps"] = 0.0,
                  ["diagonal_chance"] = 0.0, ["arch_landmark"] = false, ["pillars"] = false, ["light_variety"] = 0.4,
                  ["underground"] = false, ["tall_room_chance"] = 0.0, ["skylights"] = 0.0,
                  ["connector_weights"] = D(("stairs", 1.0), ("stairwell", 0.0), ("lift", 0.0), ("ladder", 0.0), ("spiral", 0.0)),
              }),
        }.ToDictionary(r => r.Name);

        /// <summary>Presets that came after <see cref="Surprise"/> was fixed: it never draws them, so a surprise
        /// seed keeps giving the level it always gave.</summary>
        public static readonly string[] NotInSurprise = { "station" };

        /// <summary><c>--recipe surprise</c>: a preset drawn by the seed, and a roll for extras on top
        /// (another architecture style, water, torches, vistas, a deathmatch). The extras are config fields
        /// to set after compiling (<see cref="SetFields"/>). Same seed, same surprise.</summary>
        public static (Recipe Recipe, Dictionary<string, object> Extras, string Label) Surprise(int seed)
        {
            var rng = Rng.FromLabel($"surprise/{seed}");
            var names = Presets.Keys.Where(n => !NotInSurprise.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            string name = rng.Choice(names);
            var extras = new Dictionary<string, object>();
            var bits = new List<string> { name };
            if (rng.Random() < 0.35)
            {
                extras["arch_style"] = rng.Choice(Architecture.Styles);
                bits.Add($"{extras["arch_style"]} style");
            }
            if (rng.Random() < 0.3)
            {
                extras["floods"] = 0.5;
                extras["sumps"] = 0.5;
                bits.Add("water");
            }
            if (rng.Random() < 0.25)
            {
                extras["torches"] = true;
                bits.Add("torches");
            }
            if (rng.Random() < 0.25)
            {
                extras["vistas"] = 2;
                bits.Add("vistas");
            }
            if (rng.Random() < 0.1 && !new[] { "arena", "duel", "ctf", "wizard" }.Contains(name))
            {
                extras["mode"] = "arena"; extras["keys"] = 0; extras["secret"] = false; extras["enemy_density"] = 0.0;
                extras["pickup_density"] = 0.0; extras["loops"] = 4; extras["long_loop_chance"] = 0.0; extras["jump_pads"] = 1.0;
                bits.Add("deathmatch");
            }
            return (Presets[name], extras, "surprise: " + string.Join(" + ", bits));
        }
    }
}
