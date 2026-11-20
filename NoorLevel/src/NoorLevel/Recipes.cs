using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>What kind of place to generate, said in design terms: ten dials (0..1). The dials
    /// describe intent; <see cref="Recipes.Compile"/> turns them into a <see cref="GenConfig"/>.</summary>
    public sealed class Recipe
    {
        public string Name = "classic";
        public string Description = "";
        public double Scale = 0.5;          // how much level: room count and sizes
        public double Openness = 0.45;      // windows and glass, wide corridors
        public double Verticality = 0.4;    // height variation, storeys, platforms / pits / balconies
        public double Claustrophobia = 0.3; // low ceilings, narrow corridors
        public double Combat = 0.6;         // cover (crates, pillars)
        public double Exploration = 0.5;    // side rooms, secrets
        public double Loopiness = 0.5;      // loops and alternative routes
        public double Regularity = 0.35;    // rectangles and right angles (1) vs octagons, rings, 45-degree corridors
        public double Spaciousness = 0.5;   // bigger rooms, wider corridors, more headroom
        public double Variety = 0.0;        // a wider spread of room sizes and more footprint shapes
        public Action<GenConfig> Overrides; // what makes a preset recognisable (applied after the dials)

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

        public Recipe Copy() => (Recipe)MemberwiseClone();
    }

    public static class Recipes
    {
        static double Lerp(double a, double b, double t) => a + (b - a) * t;

        /// <summary>Dials -> settings. Every mapping is monotonic.</summary>
        public static GenConfig Compile(Recipe r, int seed = 1)
        {
            double C(double v) => Math.Max(0, Math.Min(1, v));
            double sc = C(r.Scale), op = C(r.Openness), ve = C(r.Verticality), cl = C(r.Claustrophobia), co = C(r.Combat),
                   ex = C(r.Exploration), lo = C(r.Loopiness), rg = C(r.Regularity), sp = C(r.Spaciousness), va = C(r.Variety);
            var cfg = new GenConfig { Seed = seed, Recipe = r.Name };
            // scale
            cfg.MainRooms = (int)Math.Round(Lerp(3, 11, sc));
            cfg.SideRooms = (int)Math.Round(Lerp(0, 6, 0.6 * sc + 0.4 * ex));
            cfg.Keys = sc < 0.2 ? 0 : sc > 0.75 ? 2 : 1;
            cfg.SizeBias = new Dictionary<string, double> { ["small"] = Lerp(2.5, 0.4, sc), ["medium"] = 1.0, ["large"] = Lerp(0.3, 2.5, sc) };
            cfg.RoomScale = Lerp(0.85, 1.25, sc) * Lerp(1.1, 0.8, cl) * Lerp(0.95, 1.15, op);
            // verticality
            cfg.Storeys = ve < 0.5 ? 1 : ve < 0.7 ? 2 : ve < 0.9 ? 3 : 4;
            cfg.MaxLevel = (int)Math.Round(Lerp(0, 6, ve));
            cfg.TallRoomChance = Lerp(0.0, 0.7, ve);
            double vert = Lerp(0.2, 2.5, ve);
            // openness and claustrophobia
            cfg.Windows = (int)Math.Round(Lerp(0, 9, op) * (1 - 0.8 * cl) + 3 * ex * (1 - cl));
            cfg.HeightScale = Lerp(1.25, 0.72, cl);
            cfg.CorridorWidthBias = new Dictionary<int, double> { [1] = Lerp(3.0, 0.5, op) * Lerp(1, 3, cl), [2] = 1.0, [3] = Lerp(0.2, 3.0, op) * Lerp(1, 0.1, cl) };
            // combat and exploration
            cfg.CrateScale = Lerp(0.3, 1.8, co);
            cfg.Secret = ex >= 0.25;
            // loops
            cfg.Loops = (int)Math.Round(Lerp(0, 5, lo));
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
                { ["platform"] = vert, ["pit"] = vert, ["balcony"] = vert, ["pillars"] = Lerp(0.6, 1.6, co), ["none"] = Lerp(1.5, 0.6, co) };
            r.Overrides?.Invoke(cfg);
            Roomier(cfg, sp);
            Varied(cfg, va);
            return cfg;
        }

        /// <summary>Spaciousness: bigger, taller rooms, wider and higher corridors (0 changes nothing).</summary>
        public static void Roomier(GenConfig cfg, double s)
        {
            s = Math.Max(0, Math.Min(1, s));
            if (s <= 0) return;
            cfg.RoomScale *= Lerp(1.0, 1.5, s);
            cfg.HeightScale *= Lerp(1.0, 1.25, s);
            var b = cfg.CorridorWidthBias;
            double G(int k) => b.TryGetValue(k, out var v) ? v : 1.0;
            b[1] = G(1) * Math.Max(0.0, 1.0 - 2.0 * s);
            b[2] = G(2) * Lerp(1.0, 1.5, s);
            b[3] = G(3) * Lerp(1.0, 3.0, s);
            cfg.StoreyHeight = (int)Math.Round(cfg.StoreyHeight * Lerp(1.0, 1.25, s) / 32) * 32;
            cfg.CorridorHeight = (int)Math.Round(cfg.CorridorHeight * Lerp(1.0, 1.5, s) / 16) * 16;
            cfg.CrateScale *= Lerp(1.0, 0.6, s);
            cfg.Spaciousness = s;
        }

        /// <summary>Variety: room sizes from closets to halls, extra footprints (0 changes nothing).</summary>
        public static void Varied(GenConfig cfg, double v)
        {
            v = Math.Max(0, Math.Min(1, v));
            if (v <= 0) return;
            cfg.SizeVariety = v;
            foreach (var (name, w) in new[] { ("round", 1.5), ("hex", 1.5), ("diamond", 1.0), ("Z", 1.2), ("H", 1.2) })
                cfg.ShapeWeights[name] = (cfg.ShapeWeights.TryGetValue(name, out var x) ? x : 0) + w * v;
            cfg.ConnectorWeights["spiral"] = (cfg.ConnectorWeights.TryGetValue("spiral", out var sp) ? sp : 0) + 0.35 * v;
        }

        static Recipe R(string name, string desc, double sc, double op, double ve, double cl, double co, double ex,
                        double lo, double rg, double sp = 0.5, double va = 0.0, Action<GenConfig> ov = null) =>
            new Recipe { Name = name, Description = desc, Scale = sc, Openness = op, Verticality = ve, Claustrophobia = cl,
                         Combat = co, Exploration = ex, Loopiness = lo, Regularity = rg, Spaciousness = sp, Variety = va, Overrides = ov };

        /// <summary>The presets (the C# port starts with the core ones; more follow the Python set).</summary>
        public static readonly IReadOnlyDictionary<string, Recipe> Presets = new[]
        {
            R("compact", "A few small rooms and short corridors. Restrained, believable, quick to play.",
              0.1, 0.3, 0.2, 0.4, 0.5, 0.2, 0.2, 0.6, ov: c => { c.MaxLevel = 1; c.Composition = "linear"; }),
            R("classic", "Quake-style: varied rooms, height changes, loops, a key and a secret.",
              0.5, 0.45, 0.45, 0.3, 0.6, 0.5, 0.5, 0.35),
            R("bunker", "Low, narrow, orthogonal rooms; several storeys climbed by ladders and lifts. Tense and close.",
              0.45, 0.1, 0.55, 0.9, 0.8, 0.3, 0.3, 0.95, ov: c => {
                  c.Storeys = 3; c.TallRoomChance = 0; c.RoomScale = 0.75; c.Composition = "linear";
                  c.SizeBias = new Dictionary<string, double> { ["small"] = 2.5, ["medium"] = 1.0, ["large"] = 0.15 };
                  c.ConnectorWeights = new Dictionary<string, double> { ["stairs"] = 0.15, ["stairwell"] = 0.25, ["lift"] = 0.3, ["ladder"] = 0.3 };
                  c.FeatureWeights["balcony"] = 0; }),
            R("fortress", "Tall, ordered, defensive: several storeys climbed by stairwells, great halls, galleries.",
              0.7, 0.35, 0.92, 0.25, 0.8, 0.4, 0.6, 0.85, ov: c => {
                  c.ConnectorWeights = new Dictionary<string, double> { ["stairs"] = 0.3, ["stairwell"] = 0.6, ["lift"] = 0.0, ["ladder"] = 0.1 }; }),
            R("industrial", "Big, open, vertical: lifts and ladders, platforms, wide halls.",
              0.75, 0.75, 0.75, 0.2, 0.7, 0.4, 0.6, 0.6, ov: c => {
                  c.ConnectorWeights = new Dictionary<string, double> { ["stairs"] = 0.2, ["stairwell"] = 0.2, ["lift"] = 0.4, ["ladder"] = 0.2 }; }),
            R("explore", "No fights: vertical, open, full of side rooms, secrets and loops.",
              0.6, 0.75, 0.7, 0.1, 0.0, 1.0, 0.8, 0.3, va: 0.4),
            R("maze", "Many small rooms, narrow corridors, loops everywhere. Easy to get lost in.",
              0.8, 0.1, 0.2, 0.6, 0.6, 0.6, 1.0, 0.8, sp: 0.2),
            R("halls", "Big rooms of every shape, wide corridors, room to breathe.",
              0.6, 0.8, 0.5, 0.0, 0.5, 0.5, 0.5, 0.2, sp: 1.0, va: 1.0),
        }.ToDictionary(r => r.Name);
    }
}
