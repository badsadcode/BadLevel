using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel.Export
{
    /// <summary>A convex brush: an axis-aligned box, or (Corner set) a triangular prism filling the
    /// solid half of a 45-degree cell. Face roles (floor / wall / ceiling / sky) become textures.</summary>
    public sealed class Brush
    {
        public int X0, Y0, Z0, X1, Y1, Z1;
        public string Top = "wall", Bottom = "wall", Side = "wall";
        public Corner? Corner;
        /// <summary>A sloped bottom (vaults, roofs): heights of the bottom face at (x0y0, x1y0, x1y1, x0y1); Z0 is their lowest.</summary>
        public (int, int, int, int)? Slope;
        /// <summary>A sloped top (ramps): heights of the top face at the corners; Z1 is their highest.</summary>
        public (int, int, int, int)? TopSlope;
        public string Part;
        public Brush(int x0, int y0, int z0, int x1, int y1, int z1) { X0 = x0; Y0 = y0; Z0 = z0; X1 = x1; Y1 = y1; Z1 = z1; }
        public Brush Shift(int dx, int dy) => new Brush(X0 + dx, Y0 + dy, Z0, X1 + dx, Y1 + dy, Z1) { Top = Top, Bottom = Bottom, Side = Side, Corner = Corner, Slope = Slope, TopSlope = TopSlope, Part = Part };
    }

    /// <summary>Column spans -> brushes. Every open cell and every cell touching one gets a solid
    /// column minus its open spans; equal intervals on neighbouring cells merge into rectangles
    /// (greedy meshing). Sealed by construction, so qbsp-style compilers find no leaks.</summary>
    public static class Brushes
    {
        public static List<(int Z0, int Z1, string Top, string Bottom)> SolidIntervals(IList<Span> spans, int zmin, int zmax)
        {
            var outp = new List<(int, int, string, string)>();
            int z = zmin;
            for (int i = 0; i < spans.Count; i++)
            {
                if (spans[i].Floor > z) outp.Add((z, spans[i].Floor, "floor", i > 0 ? "ceiling" : "wall"));
                z = spans[i].Ceil;
            }
            if (z < zmax) outp.Add((z, zmax, "wall", "ceiling"));
            return outp;
        }

        public static List<Brush> Build(Level level)
        {
            var (zmin, zmax) = level.ZRange();
            var cols = new HashSet<Cell>();
            foreach (var c in level.Spans.Keys)
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) cols.Add(c.Offset(dx, dy));
            var groups = new Dictionary<(int, int, string, string, string, string), HashSet<Cell>>();
            var sloped = new Dictionary<((int, int, int, int), int, string, string), HashSet<Cell>>();
            var floors = new Dictionary<((int, int, int, int), int, string, string), HashSet<Cell>>();
            var empty = new List<Span>();
            foreach (var c in cols.OrderBy(c => c))
            {
                var spans = level.Spans.TryGetValue(c, out var sp) ? sp : empty;
                bool sky = spans.Count > 0 && level.Sky.Contains(c);
                (int, int, int, int)? slope = spans.Count > 0 && level.CeilSlope.TryGetValue(c, out var sl) ? sl : ((int, int, int, int)?)null;
                (int, int, int, int)? fslope = spans.Count > 0 && level.FloorSlope.TryGetValue(c, out var fl) ? fl : ((int, int, int, int)?)null;
                foreach (var (z0, z1, top, bottom0) in SolidIntervals(spans, zmin, zmax))
                {
                    string bottom = sky && z0 == spans[spans.Count - 1].Ceil ? "sky" : bottom0;
                    string part = PartOf(level, c, spans, z0, z1);
                    if (slope != null && z0 == spans[spans.Count - 1].Ceil && Min4(slope.Value) >= z0)
                    {
                        Add(sloped, (slope.Value, z1, top, part ?? ""), c);
                        continue;
                    }
                    if (fslope != null && z1 == spans[0].Floor && Min4(fslope.Value) > z0)
                    {
                        Add(floors, (fslope.Value, z0, bottom, part ?? ""), c);
                        continue;
                    }
                    Add(groups, (z0, z1, top, bottom, part ?? "", level.SkyWalls.Contains(c) ? "sky" : "wall"), c);
                }
            }
            int s = level.CellSize;
            var brushes = new List<Brush>();
            foreach (var kv in groups.OrderBy(k => k.Key.Item1).ThenBy(k => k.Key.Item2).ThenBy(k => k.Key.Item3, StringComparer.Ordinal)
                                     .ThenBy(k => k.Key.Item4, StringComparer.Ordinal).ThenBy(k => k.Key.Item5, StringComparer.Ordinal).ThenBy(k => k.Key.Item6, StringComparer.Ordinal))
                foreach (var (x0, y0, x1, y1) in GreedyRects(kv.Value))
                    brushes.Add(new Brush(x0 * s, y0 * s, kv.Key.Item1, x1 * s, y1 * s, kv.Key.Item2)
                    { Top = kv.Key.Item3, Bottom = kv.Key.Item4, Part = kv.Key.Item5 == "" ? null : kv.Key.Item5, Side = kv.Key.Item6 });
            // sloped roofs: cells with the same corners merge along the axis the slope doesn't change on
            foreach (var kv in sloped.OrderBy(k => k.Key.Item1).ThenBy(k => k.Key.Item2).ThenBy(k => k.Key.Item4, StringComparer.Ordinal))
            {
                var (z00, z10, z11, z01) = kv.Key.Item1;
                foreach (var (x0, y0, x1, y1) in Runs(kv.Value, z00 == z10 && z01 == z11, z00 == z01 && z10 == z11))
                    brushes.Add(new Brush(x0 * s, y0 * s, Min4(kv.Key.Item1), x1 * s, y1 * s, kv.Key.Item2)
                    { Top = kv.Key.Item3, Bottom = "ceiling", Slope = kv.Key.Item1, Part = kv.Key.Item4 == "" ? null : kv.Key.Item4 });
            }
            foreach (var kv in floors.OrderBy(k => k.Key.Item1).ThenBy(k => k.Key.Item2).ThenBy(k => k.Key.Item4, StringComparer.Ordinal))
            {
                var (z00, z10, z11, z01) = kv.Key.Item1;
                foreach (var (x0, y0, x1, y1) in Runs(kv.Value, z00 == z10 && z01 == z11, z00 == z01 && z10 == z11))
                    brushes.Add(new Brush(x0 * s, y0 * s, kv.Key.Item2, x1 * s, y1 * s, Max4(kv.Key.Item1))
                    { Top = "floor", Bottom = kv.Key.Item3, TopSlope = kv.Key.Item1, Part = kv.Key.Item4 == "" ? null : kv.Key.Item4 });
            }
            // half-solid diagonal cells: one triangular prism filling the solid half of the open span
            foreach (var kv in level.Diag.OrderBy(k => k.Key))
            {
                var c = kv.Key; var sp = level.Spans[c][0];
                brushes.Add(new Brush(c.X * s, c.Y * s, sp.Floor, (c.X + 1) * s, (c.Y + 1) * s, sp.Ceil) { Corner = kv.Value });
            }
            return brushes;
        }

        static int Min4((int, int, int, int) t) => Math.Min(Math.Min(t.Item1, t.Item2), Math.Min(t.Item3, t.Item4));
        static int Max4((int, int, int, int) t) => Math.Max(Math.Max(t.Item1, t.Item2), Math.Max(t.Item3, t.Item4));
        static void Add<K>(Dictionary<K, HashSet<Cell>> d, K k, Cell c) { if (!d.TryGetValue(k, out var set)) d[k] = set = new HashSet<Cell>(); set.Add(c); }

        /// <summary>Which named part a solid interval of a column belongs to (see Level.Part*).</summary>
        static string PartOf(Level level, Cell c, List<Span> spans, int z0, int z1)
        {
            if (level.PartSolid.Count == 0 && level.PartCells.Count == 0 && level.PartCeiling.Count == 0) return null;
            string Get(Dictionary<Cell, string> d) => d.TryGetValue(c, out var v) ? v : null;
            if (spans.Count == 0) return Get(level.PartSolid);
            if (z0 == spans[spans.Count - 1].Ceil) return Get(level.PartCeiling) ?? Get(level.PartCells);
            if (z1 == spans[0].Floor) return Get(level.PartSolid) ?? Get(level.PartCells);
            return Get(level.PartCells);
        }

        /// <summary>Merge cells into 1-wide runs along an axis where the slope is constant (or keep them single).</summary>
        static List<(int, int, int, int)> Runs(HashSet<Cell> cells, bool alongX, bool alongY)
        {
            var left = new HashSet<Cell>(cells);
            var outp = new List<(int, int, int, int)>();
            var order = alongX ? cells.OrderBy(c => c.Y).ThenBy(c => c.X) : cells.OrderBy(c => c.X).ThenBy(c => c.Y);
            foreach (var c in order.ToList())
            {
                if (!left.Remove(c)) continue;
                int x1 = c.X + 1, y1 = c.Y + 1;
                if (alongX) while (left.Remove(new Cell(x1, c.Y))) x1++;
                else if (alongY) while (left.Remove(new Cell(c.X, y1))) y1++;
                outp.Add((c.X, c.Y, x1, y1));
            }
            return outp;
        }

        /// <summary>Cover a set of cells with non-overlapping rectangles (x0, y0, x1, y1), max exclusive.</summary>
        public static List<(int, int, int, int)> GreedyRects(IEnumerable<Cell> cells)
        {
            var left = new HashSet<Cell>(cells);
            var rects = new List<(int, int, int, int)>();
            foreach (var c in left.OrderBy(c => c.Y).ThenBy(c => c.X).ToList())
            {
                if (!left.Contains(c)) continue;
                int x1 = c.X + 1;
                while (left.Contains(new Cell(x1, c.Y))) x1++;
                int y1 = c.Y + 1;
                while (Enumerable.Range(c.X, x1 - c.X).All(x => left.Contains(new Cell(x, y1)))) y1++;
                for (int y = c.Y; y < y1; y++) for (int x = c.X; x < x1; x++) left.Remove(new Cell(x, y));
                rects.Add((c.X, c.Y, x1, y1));
            }
            return rects;
        }
    }
}
