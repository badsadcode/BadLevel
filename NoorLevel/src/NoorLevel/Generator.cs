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
