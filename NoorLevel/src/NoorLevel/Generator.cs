using System;
using System.Collections.Generic;

namespace NoorLevel
{
    public sealed class GenerationException : Exception
    {
        public GenerationException(string msg) : base(msg) { }
    }

    /// <summary>The front door: config in, a valid level out. Each attempt rebuilds from the mission
    /// graph with its own derived random stream, so a seed always gives the same level.</summary>
    public static class Generator
    {
        public static Level Generate(GenConfig cfg)
        {
            var errors = new List<string>();
            for (int attempt = 0; attempt < cfg.MaxAttempts; attempt++)
            {
                var rng = Rng.FromLabel($"noorlevel:{cfg.Seed}:{attempt}");
                try
                {
                    var graph = MissionGraph.Build(cfg, rng.Derive("graph"));
                    var level = Layout.Build(graph, cfg, rng.Derive("layout"));
                    Decorate.Run(level, rng.Derive("decorate"));
                    Layout.CleanupDiagonals(level);
                    if (cfg.CheckClearance) Clearance.Widen(level);
                    var rep = Validator.Run(level);
                    level.Report = rep;
                    if (rep.Ok)
                    {
                        level.Notes.Add($"attempt {attempt + 1}");
                        return level;
                    }
                    errors.Add($"attempt {attempt}: {string.Join("; ", rep.Errors)}");
                }
                catch (LayoutException e) { errors.Add($"attempt {attempt}: {e.Message}"); }
            }
            throw new GenerationException($"no valid level in {cfg.MaxAttempts} attempts:\n" + string.Join("\n", errors));
        }

        public static Level Generate(string preset, int seed, Action<Recipe> tweak = null)
        {
            var r = Recipes.Presets.TryGetValue(preset ?? "classic", out var p) ? p.Copy() : Recipes.Presets["classic"].Copy();
            tweak?.Invoke(r);
            return Generate(Recipes.Compile(r, seed));
        }
    }
}
