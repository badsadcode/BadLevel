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
        public string Part;
        public Brush(int x0, int y0, int z0, int x1, int y1, int z1) { X0 = x0; Y0 = y0; Z0 = z0; X1 = x1; Y1 = y1; Z1 = z1; }
        public Brush Shift(int dx, int dy) => new Brush(X0 + dx, Y0 + dy, Z0, X1 + dx, Y1 + dy, Z1) { Top = Top, Bottom = Bottom, Side = Side, Corner = Corner, Part = Part };
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
            var groups = new Dictionary<(int, int, string, string, string), HashSet<Cell>>();
            var empty = new List<Span>();
            foreach (var c in cols)
            {
                var spans = level.Spans.TryGetValue(c, out var sp) ? sp : empty;
                bool sky = spans.Count > 0 && level.Sky.Contains(c);
                foreach (var (z0, z1, top, bottom0) in SolidIntervals(spans, zmin, zmax))
                {
                    string bottom = sky && z0 == spans[spans.Count - 1].Ceil ? "sky" : bottom0;
                    string part = spans.Count == 0 && level.PartSolid.TryGetValue(c, out var p) ? p : null;
                    var key = (z0, z1, top, bottom, part);
                    if (!groups.TryGetValue(key, out var set)) groups[key] = set = new HashSet<Cell>();
                    set.Add(c);
                }
            }
            int s = level.CellSize;
            var brushes = new List<Brush>();
            foreach (var kv in groups.OrderBy(k => k.Key.Item1).ThenBy(k => k.Key.Item2).ThenBy(k => k.Key.Item3, StringComparer.Ordinal)
                                     .ThenBy(k => k.Key.Item4, StringComparer.Ordinal).ThenBy(k => k.Key.Item5 ?? "", StringComparer.Ordinal))
                foreach (var (x0, y0, x1, y1) in GreedyRects(kv.Value))
                    brushes.Add(new Brush(x0 * s, y0 * s, kv.Key.Item1, x1 * s, y1 * s, kv.Key.Item2)
                    { Top = kv.Key.Item3, Bottom = kv.Key.Item4, Part = kv.Key.Item5 });
            foreach (var kv in level.Diag.OrderBy(k => k.Key))
            {
                var c = kv.Key; var sp = level.Spans[c][0];
                brushes.Add(new Brush(c.X * s, c.Y * s, sp.Floor, (c.X + 1) * s, (c.Y + 1) * s, sp.Ceil) { Corner = kv.Value });
            }
            return brushes;
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
