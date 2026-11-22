using System;
using System.Collections.Generic;

namespace NoorLevel
{
    public sealed class GenerationException : Exception
    {
        public GenerationException(string msg) : base(msg) { }
    }

    /// <summary>Top-level pipeline (generator.py): seed + config -> validated Level. A master stream from
    /// the seed (and the variant: alternative layouts for the same seed) gives each attempt its own
    /// stream, so a seed always gives the same level.</summary>
    public static class Generator
    {
        public static Level Generate(GenConfig cfg = null)
        {
            cfg = cfg ?? new GenConfig();
            return cfg.MapSize > 0 ? GenerateSized(cfg) : GenerateOnce(cfg);
        }

        /// <summary>The level's footprint: the longer side of the open cells' bounding box, in cells (metres).</summary>
        public static int Footprint(Level level)
        {
            if (level.Spans.Count == 0) return 0;
            int x0 = int.MaxValue, x1 = int.MinValue, y0 = int.MaxValue, y1 = int.MinValue;
            foreach (var c in level.Spans.Keys) { x0 = Math.Min(x0, c.X); x1 = Math.Max(x1, c.X); y0 = Math.Min(y0, c.Y); y1 = Math.Max(y1, c.Y); }
            return Math.Max(x1 - x0, y1 - y0) + 1;
        }

        /// <summary>Fit the level to cfg.MapSize: generate, measure, scale the room counts by the area that's
        /// off (and the room sizes when the counts hit their limits), and try again, a few times. Same seed,
        /// same answer. Returns the closest level that isn't too big (or the smallest one made).</summary>
        static Level GenerateSized(GenConfig cfg)
        {
            int target = cfg.MapSize;
            var c = cfg.Clone();
            Level best = null; double bestScore = double.MaxValue;
            for (int round = 0; round < 7; round++)
            {
                Level level;
                try { level = GenerateOnce(c); }
                catch (GenerationException) when (best != null) { break; }
                int size = Footprint(level);
                double ratio = (double)size / target;
                double score = ratio > 1.15 ? 10 + ratio : Math.Abs(1 - ratio);      // too big is worse than a bit small
                if (best == null || score < bestScore) { best = level; bestScore = score; }
                level.Notes.Add($"map size: {size} m for {target} m (round {round + 1})");
                if (ratio <= 1.15 && ratio >= 0.85) break;
                double area = 1 / (ratio * ratio);                                   // how much more (or less) level we want
                int main = (int)Math.Round(c.MainRooms * Math.Sqrt(area) * (area > 1 ? 1.0 : 1.0));
                int newMain = Math.Max(3, Math.Min(30, main));
                c.SideRooms = Math.Max(0, Math.Min(20, (int)Math.Round(c.SideRooms * Math.Sqrt(area))));
                c.Loops = Math.Max(0, Math.Min(8, c.Loops));
                bool clamped = newMain == c.MainRooms || newMain == 3 || newMain == 30;
                c.MainRooms = newMain;
                if (clamped || area < 0.5 || area > 2)                                // the rooms themselves change size too
                    c.RoomScale = Math.Max(0.55, Math.Min(2.2, c.RoomScale * Math.Pow(area, 0.25)));
                if (area < 0.75 && c.MainRooms <= 4)
                {
                    // at the fewest rooms and still too big: the big spaces go (a level-in-a-level, then the yards)
                    if (c.MegaRooms > 0) c.MegaRooms = 0;
                    else if (c.OpenAreas > 0) c.OpenAreas--;
                    c.SideRooms = Math.Min(c.SideRooms, 1);
                }
            }
            return best;
        }

        static Level GenerateOnce(GenConfig cfg)
        {
            var master = Rng.FromLabel(cfg.Variant == 0 ? $"{cfg.Seed}" : $"{cfg.Seed}/{cfg.Variant}");
            string last = null;
            for (int attempt = 0; attempt < cfg.MaxAttempts; attempt++)
            {
                var rng = master.Derive("attempt");
                Level level;
                try
                {
                    var graph = MissionGraph.Build(cfg, rng);
                    level = Layout.BuildLayout(graph, cfg, rng, cfg.Seed);
                    Decorate.Run(level, rng);
                    Layout.CleanupDiagonals(level);
                    Openings.AddWindows(level, rng, cfg.Windows, cfg.GlazedChance);
                    Vistas.AddVistas(level);               // grand halls: a wall of glass onto a landscape (cfg.Vistas)
                    Clearance.Widen(level);                // the player is a box: no 32-unit squeezes
                    Ramps.MakeRamps(level);                // stair corridors as smooth ramps (cfg.Ramps)
                    Floods.AddFloods(level);               // flooded crypts and sumps (cfg.Floods, cfg.Sumps)
                    if (cfg.Mirror)
                        level = Mirror.MakeTeamMap(level); // team map: this half, lanes, and its mirror image
                    level.Sky.RemoveWhere(c => !level.Spans.ContainsKey(c));   // (a neighbour's wall member filled a yard's edge cell)
                    // station markers, last: they only append entities (own label streams, off by default)
                    if (cfg.Dressing) Dressing.Run(level);                   // prop markers
                    if (cfg.StationEntities || cfg.Viewports) StationEntities.Run(level);   // gameplay markers, viewports
                }
                catch (LayoutException e)
                {
                    last = e.Message;
                    continue;
                }
                var report = Validator.Run(level);
                if (report.Ok)
                {
                    level.Notes.Add($"attempts: {attempt + 1}");
                    level.Report = report;
                    return level;
                }
                last = string.Join("; ", report.Errors);
            }
            throw new GenerationException($"seed {cfg.Seed}: no valid level after {cfg.MaxAttempts} attempts ({last})");
        }

        public static Level Generate(string preset, int seed, Action<Recipe> tweak = null)
        {
            var r = Recipes.Presets.TryGetValue(preset ?? "classic", out var p) ? p.Copy() : Recipes.Presets["classic"].Copy();
            tweak?.Invoke(r);
            return Generate(Recipes.Compile(r, seed));
        }
    }
}
