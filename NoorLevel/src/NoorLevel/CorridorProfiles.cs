using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>Corridor cross-sections (NoorLevel's own): a corridor need not be a box. Each corridor has a
    /// <see cref="Corridor.Profile"/> (set from <c>corridor_profile</c> when it is carved; vents stay boxes):
    /// <list type="bullet">
    /// <item><c>box</c>: flat floor and ceiling, square corners (the default: nothing changes).</item>
    /// <item><c>chamfer</c>: the ceiling's edges cut at 45 degrees, one cell wide (an octagonal section).</item>
    /// <item><c>round</c>: the ceiling curves down to the walls along a circle (over several cells in a wide
    /// corridor) and the floor's edges rise a little: a tube.</item>
    /// </list>
    /// It runs last, after everything that carves (<see cref="Generator"/>: after the floods), and only writes
    /// what every exporter already reads: <see cref="Level.CeilSlope"/> / <see cref="Level.FloorSlope"/> corner
    /// heights, the span keeping the lowest ceiling point and the highest floor point. A cell's corner heights
    /// come from how far each corner is from the corridor's walls (Chebyshev, in cells), so bends, junctions,
    /// stairs and doorways follow by themselves; a cell whose corners don't lie in one plane is lowered flat
    /// instead (the .map compiler needs planar faces). The depth of the cut is chosen per corridor as the
    /// deepest that keeps every move the player (a point, and the 2x2 box of <see cref="Clearance"/>) could
    /// make before possible after: it never changes what's reachable.</summary>
    public static class CorridorProfiles
    {
        public static readonly string[] Names = { "box", "chamfer", "round" };
        public const int Fillet = 8;                 // round: how far a floor edge rises

        public static bool Profiled(Corridor c) => !string.IsNullOrEmpty(c.Profile) && c.Profile != "box";

        /// <summary>The profile a new corridor gets (Layout.BuildLayout): the config's, but vents stay ducts.</summary>
        public static string For(GenConfig cfg, bool vent) => vent ? "box" : Names.Contains(cfg.CorridorProfile) ? cfg.CorridorProfile : "box";

        /// <summary>How far the ceiling comes down at a corner d cells from the wall (0: on the wall), in a
        /// corridor of half-width w (cells), with the full cut D at the wall.</summary>
        public static int Drop(string profile, int d, double w, int D)
        {
            if (D <= 0) return 0;
            if (profile == "chamfer") return d == 0 ? D : 0;
            if (profile == "round")
            {
                double x = Math.Max(0.0, w - d) / w;                  // 1 at the wall, 0 on the axis
                double drop = D * (1 - Math.Sqrt(Math.Max(0.0, 1 - x * x)));
                return (int)Math.Round(drop / 4.0) * 4;
            }
            return 0;
        }

        /// <summary>The cut a profile asks for at the wall, before the headroom limits it.</summary>
        public static int Wanted(string profile, double w) => profile == "chamfer" ? 32 : profile == "round" ? (int)Math.Min(64, Math.Round(w * 32)) : 0;

        public static int Apply(Level level)
        {
            if (!level.Corridors.Any(Profiled)) return 0;
            var byOwner = new Dictionary<string, List<Cell>>();
            foreach (var kv in level.Owners)
                foreach (var o in kv.Value)
                    if (o.Name.StartsWith("corridor:"))
                    {
                        if (!byOwner.TryGetValue(o.Name, out var l)) byOwner[o.Name] = l = new List<Cell>();
                        l.Add(kv.Key);
                    }
            int total = 0;
            foreach (var corr in level.Corridors)
                if (Profiled(corr) && byOwner.TryGetValue($"corridor:{corr.Id}", out var cells))
                    total += ApplyOne(level, corr, cells.Distinct().OrderBy(c => c).ToList());
            if (total > 0) level.Notes.Add($"corridor profiles: {total} cells");
            return total;
        }

        /// <summary>A neighbour's span that meets the span s (the most overlap), or null: solid there.</summary>
        static Span? Meets(Level level, Cell c, Span s)
        {
            if (!level.Spans.TryGetValue(c, out var spans) || level.Diag.ContainsKey(c) || level.Glass.Contains(c)) return null;
            Span? best = null; int bo = 0;
            foreach (var x in spans)
            {
                int o = Math.Min(x.Ceil, s.Ceil) - Math.Max(x.Floor, s.Floor);
                if (o > bo) { bo = o; best = x; }
            }
            return best;
        }

        static readonly (int, int)[] CornerOffsets = { (0, 0), (1, 0), (1, 1), (0, 1) };   // CeilSlope order: x0y0, x1y0, x1y1, x0y1

        static int ApplyOne(Level level, Corridor corr, List<Cell> cells)
        {
            var cfg = level.Config;
            string own = $"corridor:{corr.Id}";
            int PH = cfg.PlayerHeight;
            double w = corr.Radius + 0.5;
            // the corridor's own span per cell (skip half cells, the sky, and spans under something stacked)
            var mine = new Dictionary<Cell, Span>();
            foreach (var c in cells)
            {
                if (level.Diag.ContainsKey(c) || level.Sky.Contains(c) || !level.Spans.TryGetValue(c, out var spans)) continue;
                foreach (var sp in spans)
                    if (level.OwnerOf(c, sp) == own) { mine[c] = sp; break; }
            }
            if (mine.Count == 0) return 0;
            // how far each corner is from a wall: the smallest k for which the (2k+2)^2 cells round the corner
            // hold a cell the corridor's space doesn't continue into
            int K = corr.Radius + 1;
            var dist = new Dictionary<Cell, int[]>();
            foreach (var kv in mine)
            {
                var c = kv.Key; var s = kv.Value;
                var ds = new int[4];
                for (int i = 0; i < 4; i++)
                {
                    int vx = c.X + CornerOffsets[i].Item1, vy = c.Y + CornerOffsets[i].Item2;
                    int d = K;
                    for (int k = 0; k < K && d == K; k++)
                        for (int x = vx - 1 - k; x <= vx + k && d == K; x++)
                            for (int y = vy - 1 - k; y <= vy + k; y++)
                            {
                                var m = Meets(level, new Cell(x, y), s);
                                if (m == null || Math.Min(m.Value.Ceil, s.Ceil) - Math.Max(m.Value.Floor, s.Floor) < PH) { d = k; break; }
                            }
                    ds[i] = d;
                }
                dist[kv.Key] = ds;
            }

            // the deepest cut (and the floor fillet, round only) that keeps every move possible
            int want = Wanted(corr.Profile, w);
            int fillet = corr.Profile == "round" ? Fillet : 0;
            Dictionary<Cell, (Span S, (int, int, int, int)? Ceil, (int, int, int, int)? Floor)> best = null;
            for (int D = want; D >= 4 && best == null; D -= 4)
                foreach (var F in fillet > 0 ? new[] { fillet, 0 } : new[] { 0 })
                {
                    var cand = Shape(level, corr, mine, dist, w, D, F);
                    if (Keeps(level, mine, cand)) { best = cand; break; }
                }
            if (best == null) return 0;
            int n = 0;
            foreach (var kv in best.OrderBy(k => k.Key))
            {
                var c = kv.Key; var old = mine[c]; var (s, ceil, floor) = kv.Value;
                if (s.Equals(old) && ceil == null && floor == null) continue;
                level.ReplaceIn(c, old.Floor, old.Ceil, new[] { s });
                if (ceil != null) level.CeilSlope[c] = ceil.Value;
                if (floor != null) level.FloorSlope[c] = floor.Value;
                // what hangs from the old ceiling comes down with it (lights); what stands on the floor stays on it
                foreach (var e in level.Entities)
                    if (e.Bounds == null && level.CellOf(e.Pos) == c && e.Pos.Z >= old.Floor && e.Pos.Z < old.Ceil)
                    {
                        double z = Math.Min(e.Pos.Z, s.Ceil - 16);
                        z = Math.Max(z, s.Floor + (e.Pos.Z - old.Floor));
                        z = Math.Min(z, s.Ceil - 1);
                        if (z != e.Pos.Z) e.Pos = new Vec3(e.Pos.X, e.Pos.Y, z);
                    }
                n++;
            }
            return n;
        }

        static bool Planar(int[] z) => z[0] + z[2] == z[1] + z[3];

        /// <summary>The new span and corner heights of every cell for a cut D at the wall and a fillet F.</summary>
        static Dictionary<Cell, (Span S, (int, int, int, int)? Ceil, (int, int, int, int)? Floor)> Shape(
            Level level, Corridor corr, Dictionary<Cell, Span> mine, Dictionary<Cell, int[]> dist, double w, int D, int F)
        {
            var outp = new Dictionary<Cell, (Span, (int, int, int, int)?, (int, int, int, int)?)>();
            foreach (var kv in mine)
            {
                var c = kv.Key; var s = kv.Value; var ds = dist[c];
                var spans = level.Spans[c];
                bool top = spans[spans.Count - 1].Equals(s), bottom = spans[0].Equals(s);
                int newCeil = s.Ceil, newFloor = s.Floor;
                (int, int, int, int)? ceil = null, floor = null;
                if (top)
                {
                    var bz = level.CeilSlope.TryGetValue(c, out var sl) ? new[] { sl.Item1, sl.Item2, sl.Item3, sl.Item4 } : new[] { s.Ceil, s.Ceil, s.Ceil, s.Ceil };
                    var z = new int[4];
                    for (int i = 0; i < 4; i++) z[i] = bz[i] - Drop(corr.Profile, ds[i], w, D);
                    if (!z.SequenceEqual(bz))
                    {
                        if (!Planar(z)) { int lo = z.Min(); z = new[] { lo, lo, lo, lo }; }
                        newCeil = Math.Min(s.Ceil, z.Min());
                        if (z.Distinct().Count() > 1 || level.CeilSlope.ContainsKey(c)) ceil = (z[0], z[1], z[2], z[3]);
                    }
                }
                // the floor's edges (round): only on a level stretch, never under water or on a ramp
                if (F > 0 && bottom && !level.FloorSlope.ContainsKey(c) && !level.Water.ContainsKey(c) && !level.Hazard.ContainsKey(c)
                    && ds.Any(d => d == 0) && Layout.Dirs.All(t => { var n = c.Offset(t.Item1, t.Item2); return !mine.TryGetValue(n, out var ns) || ns.Floor == s.Floor; }))
                {
                    var f = ds.Select(d => d == 0 ? s.Floor + F : s.Floor).ToArray();
                    if (Planar(f) && f.Distinct().Count() > 1)
                    {
                        newFloor = f.Max();
                        floor = (f[0], f[1], f[2], f[3]);
                    }
                }
                outp[c] = (new Span(newFloor, newCeil), ceil, floor);
            }
            return outp;
        }

        /// <summary>Does the new shape keep every span tall enough, every step between neighbours that was
        /// possible, and every 2x2 stand of the player box that was possible?</summary>
        static bool Keeps(Level level, Dictionary<Cell, Span> mine, Dictionary<Cell, (Span S, (int, int, int, int)? Ceil, (int, int, int, int)? Floor)> cand)
        {
            var cfg = level.Config;
            int PH = cfg.PlayerHeight;
            foreach (var kv in cand)
                if (kv.Value.S.Height < PH) return false;
            Span? Old(Cell c, Span refS) => mine.TryGetValue(c, out var s) ? s : Meets(level, c, refS);
            Span? New(Cell c, Span refS) => cand.TryGetValue(c, out var s) ? s.S : Meets(level, c, refS);
            bool Move(Span a, Span b) => b.Floor - a.Floor <= cfg.MaxStep && a.Floor - b.Floor <= cfg.MaxDrop
                                         && Math.Min(a.Ceil, b.Ceil) - Math.Max(a.Floor, b.Floor) >= PH;
            foreach (var kv in mine)
            {
                var c = kv.Key; var s = kv.Value;
                foreach (var (dx, dy) in Layout.Dirs)
                {
                    var n = c.Offset(dx, dy);
                    var o0 = Old(n, s); var o1 = New(n, cand[c].S);
                    if (o0 == null) continue;
                    if (o1 == null) return false;
                    if (Move(s, o0.Value) && !Move(cand[c].S, o1.Value)) return false;
                    if (Move(o0.Value, s) && !Move(o1.Value, cand[c].S)) return false;
                }
                // the 2x2 blocks this cell is part of
                for (int bx = -1; bx <= 0; bx++)
                    for (int by = -1; by <= 0; by++)
                    {
                        var blk = new[] { c.Offset(bx, by), c.Offset(bx + 1, by), c.Offset(bx, by + 1), c.Offset(bx + 1, by + 1) };
                        var before = blk.Select(b => Old(b, s)).ToList();
                        if (before.Any(x => x == null)) continue;
                        if (before.Max(x => x.Value.Floor) + PH > before.Min(x => x.Value.Ceil)) continue;   // the box didn't stand here
                        var after = blk.Select(b => New(b, cand[c].S)).ToList();
                        if (after.Any(x => x == null) || after.Max(x => x.Value.Floor) + PH > after.Min(x => x.Value.Ceil)) return false;
                    }
            }
            return true;
        }
    }
}
