using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>A room footprint: local open cells inside the bounding box plus 45-degree half cells
    /// (stored with the corner their solid triangle points at).</summary>
    public sealed class Shape
    {
        public int W, H;
        public string Name;
        public HashSet<Cell> Cells;
        public Dictionary<Cell, Corner> Diag = new Dictionary<Cell, Corner>();

        public static readonly Corner[] AllCorners = { Corner.SW, Corner.SE, Corner.NW, Corner.NE };

        /// <summary>The two sides of a half cell that are solid (its triangle's legs).</summary>
        public static (int, int)[] SolidSides(Corner c)
        {
            switch (c)
            {
                case Corner.SW: return new[] { (0, -1), (-1, 0) };
                case Corner.SE: return new[] { (0, -1), (1, 0) };
                case Corner.NW: return new[] { (0, 1), (-1, 0) };
                default: return new[] { (0, 1), (1, 0) };
            }
        }

        /// <summary>Unit-square vertices: (solid triangle, open triangle).</summary>
        public static ((double, double)[] Solid, (double, double)[] Open) CornerVertices(Corner c)
        {
            var sw = (0.0, 0.0); var se = (1.0, 0.0); var nw = (0.0, 1.0); var ne = (1.0, 1.0);
            switch (c)
            {
                case Corner.SW: return (new[] { sw, se, nw }, new[] { ne, nw, se });
                case Corner.SE: return (new[] { se, ne, sw }, new[] { nw, sw, ne });
                case Corner.NW: return (new[] { nw, sw, ne }, new[] { se, ne, sw });
                default: return (new[] { ne, nw, se }, new[] { sw, se, nw });
            }
        }

        public Shape(int w, int h, string name = "rect")
        {
            W = w; H = h; Name = name;
            Cells = new HashSet<Cell>();
            for (int x = 0; x < w; x++) for (int y = 0; y < h; y++) Cells.Add(new Cell(x, y));
        }

        public void RemoveRect(int x0, int y0, int x1, int y1)
        {
            for (int x = x0; x < x1; x++)
                for (int y = y0; y < y1; y++) { var c = new Cell(x, y); Cells.Remove(c); Diag.Remove(c); }
        }

        /// <summary>Cut a 45-degree corner off the bounding box, k cells long.</summary>
        public void Chamfer(Corner corner, int k)
        {
            if (k <= 0) return;
            bool west = corner == Corner.SW || corner == Corner.NW, south = corner == Corner.SW || corner == Corner.SE;
            for (int i = 0; i < k; i++)
                for (int j = 0; j < k - i; j++)
                {
                    var c = new Cell(west ? i : W - 1 - i, south ? j : H - 1 - j);
                    if (!Cells.Contains(c)) continue;
                    if (i + j <= k - 2) { Cells.Remove(c); Diag.Remove(c); }
                    else if (i + j == k - 1) Diag[c] = corner;
                }
        }

        /// <summary>Connected, and every full cell in some 2x2 block of full cells (no 1-cell slivers).</summary>
        public bool Ok()
        {
            var full = new HashSet<Cell>(Cells.Where(c => !Diag.ContainsKey(c)));
            if (full.Count == 0) return false;
            var start = full.First();
            var seen = new HashSet<Cell> { start };
            var stack = new Stack<Cell>(); stack.Push(start);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                foreach (var n in new[] { c.Offset(1, 0), c.Offset(-1, 0), c.Offset(0, 1), c.Offset(0, -1) })
                    if (full.Contains(n) && seen.Add(n)) stack.Push(n);
            }
            if (seen.Count != full.Count) return false;
            var covered = new HashSet<Cell>();
            foreach (var c in full)
            {
                var block = new[] { c, c.Offset(1, 0), c.Offset(0, 1), c.Offset(1, 1) };
                if (block.All(full.Contains)) foreach (var b in block) covered.Add(b);
            }
            return full.All(covered.Contains);
        }

        static void ChamferCorners(Shape s, Rng rng, int kmax)
        {
            foreach (var corner in AllCorners)
                if (rng.Random() < 0.75) s.Chamfer(corner, rng.Int(1, kmax));
        }

        public static Shape Make(int w, int h, string name, Rng rng)
        {
            var s = new Shape(w, h, name);
            int m = Math.Min(w, h);
            switch (name)
            {
                case "chamfer": ChamferCorners(s, rng, Math.Max(1, Math.Min(3, m / 4))); break;
                case "octagon": foreach (var c in AllCorners) s.Chamfer(c, Math.Max(2, m / 3)); break;
                case "L":
                {
                    int nw = rng.Int(w / 3, w / 2), nh = rng.Int(h / 3, h / 2);
                    var corner = rng.Choice(AllCorners);
                    int x0 = corner == Corner.SW || corner == Corner.NW ? 0 : w - nw;
                    int y0 = corner == Corner.SW || corner == Corner.SE ? 0 : h - nh;
                    s.RemoveRect(x0, y0, x0 + nw, y0 + nh);
                    var opposite = corner == Corner.SW ? Corner.NE : corner == Corner.NE ? Corner.SW : corner == Corner.SE ? Corner.NW : Corner.SE;
                    s.Chamfer(opposite, rng.Int(1, Math.Max(1, m / 4)));
                    break;
                }
                case "T":
                {
                    char side = rng.Choice("nsew".ToCharArray());
                    if (side == 'n' || side == 's')
                    {
                        int cw = (w - rng.Int(4, Math.Max(4, w / 2))) / 2, ch = rng.Int(h / 3, h / 2);
                        int y0 = side == 's' ? 0 : h - ch;
                        s.RemoveRect(0, y0, cw, y0 + ch); s.RemoveRect(w - cw, y0, w, y0 + ch);
                    }
                    else
                    {
                        int ch = (h - rng.Int(4, Math.Max(4, h / 2))) / 2, cw = rng.Int(w / 3, w / 2);
                        int x0 = side == 'w' ? 0 : w - cw;
                        s.RemoveRect(x0, 0, x0 + cw, ch); s.RemoveRect(x0, h - ch, x0 + cw, h);
                    }
                    break;
                }
                case "U":
                {
                    char side = rng.Choice("nsew".ToCharArray());
                    int arm = rng.Int(4, Math.Max(4, m / 3));
                    if (side == 'n' || side == 's')
                    {
                        int depth = rng.Int(h / 3, h / 2), y0 = side == 's' ? 0 : h - depth;
                        s.RemoveRect(arm, y0, w - arm, y0 + depth);
                    }
                    else
                    {
                        int depth = rng.Int(w / 3, w / 2), x0 = side == 'w' ? 0 : w - depth;
                        s.RemoveRect(x0, arm, x0 + depth, h - arm);
                    }
                    ChamferCorners(s, rng, 2);
                    break;
                }
                case "plus":
                {
                    int cw = rng.Int(Math.Max(2, w / 4), Math.Max(2, (w - 4) / 2 - 1));
                    int ch = rng.Int(Math.Max(2, h / 4), Math.Max(2, (h - 4) / 2 - 1));
                    foreach (var (x0, y0) in new[] { (0, 0), (w - cw, 0), (0, h - ch), (w - cw, h - ch) })
                        s.RemoveRect(x0, y0, x0 + cw, y0 + ch);
                    break;
                }
                case "ring":
                {
                    int ring = rng.Int(3, Math.Max(3, (m - 3) / 3));
                    int bx0 = ring, by0 = ring, bx1 = w - ring, by1 = h - ring;
                    s.RemoveRect(bx0, by0, bx1, by1);
                    if (bx1 - bx0 >= 4 && by1 - by0 >= 4)
                        foreach (var (x, y, c) in new[] { (bx0, by0, Corner.NE), (bx1 - 1, by0, Corner.NW), (bx0, by1 - 1, Corner.SE), (bx1 - 1, by1 - 1, Corner.SW) })
                        { s.Cells.Add(new Cell(x, y)); s.Diag[new Cell(x, y)] = c; }
                    ChamferCorners(s, rng, Math.Max(1, Math.Min(3, ring - 1)));
                    break;
                }
                case "round":
                {
                    double cx = w / 2.0, cy = h / 2.0, rx = w / 2.0, ry = h / 2.0;
                    bool Inside(int x, int y) => Math.Pow((x + 0.5 - cx) / rx, 2) + Math.Pow((y + 0.5 - cy) / ry, 2) <= 1.0;
                    var cells = new HashSet<Cell>();
                    for (int x = 0; x < w; x++) for (int y = 0; y < h; y++) if (Inside(x, y)) cells.Add(new Cell(x, y));
                    var diag = new Dictionary<Cell, Corner>();
                    for (int x = 0; x < w; x++)
                        for (int y = 0; y < h; y++)
                        {
                            var c = new Cell(x, y);
                            if (cells.Contains(c)) continue;
                            int hx = x + (x + 0.5 < cx ? 1 : -1), vy = y + (y + 0.5 < cy ? 1 : -1);
                            if (cells.Contains(new Cell(hx, y)) && cells.Contains(new Cell(x, vy)))
                            {
                                bool north = y + 0.5 > cy, east = x + 0.5 > cx;
                                diag[c] = north ? (east ? Corner.NE : Corner.NW) : (east ? Corner.SE : Corner.SW);
                            }
                        }
                    s.Cells = new HashSet<Cell>(cells.Concat(diag.Keys));
                    s.Diag = diag;
                    break;
                }
                case "hex": foreach (var c in AllCorners) s.Chamfer(c, Math.Max(2, Math.Min(m / 2, Math.Max(w, h) / 3))); break;
                case "diamond": foreach (var c in AllCorners) s.Chamfer(c, Math.Max(2, m / 2)); break;
                case "Z":
                {
                    int nw = rng.Int(w / 3, w / 2), nh = rng.Int(h / 3, Math.Max(h / 3, h / 2 - 1));
                    bool flip = rng.Random() < 0.5;
                    s.RemoveRect(0, flip ? 0 : h - nh, nw, flip ? nh : h);
                    s.RemoveRect(w - nw, flip ? h - nh : 0, w, flip ? h : nh);
                    break;
                }
                case "H":
                    if (w >= h)
                    {
                        int gap = rng.Int(Math.Max(2, w / 4), Math.Max(2, w / 3)), depth = rng.Int(h / 4, Math.Max(h / 4, h / 3));
                        int x0 = (w - gap) / 2;
                        s.RemoveRect(x0, 0, x0 + gap, depth); s.RemoveRect(x0, h - depth, x0 + gap, h);
                    }
                    else
                    {
                        int gap = rng.Int(Math.Max(2, h / 4), Math.Max(2, h / 3)), depth = rng.Int(w / 4, Math.Max(w / 4, w / 3));
                        int y0 = (h - gap) / 2;
                        s.RemoveRect(0, y0, depth, y0 + gap); s.RemoveRect(w - depth, y0, w, y0 + gap);
                    }
                    break;
            }
            return s;
        }

        // offered only when a recipe asks for them (the variety dial): existing levels stay as they were
        public static readonly string[] ExtraShapes = { "round", "hex", "diamond", "Z", "H" };
        public static readonly Corner[] Corners = AllCorners;
        public static readonly Dictionary<string, string[]> ShapesBySize = new Dictionary<string, string[]>
        {
            ["small"] = new[] { "rect", "rect", "chamfer", "chamfer", "octagon" },
            ["medium"] = new[] { "rect", "chamfer", "chamfer", "octagon", "L", "L", "T", "U" },
            ["large"] = new[] { "chamfer", "octagon", "octagon", "L", "T", "U", "plus", "ring", "ring" },
            ["yard"] = new[] { "rect", "rect", "chamfer", "chamfer", "L", "U", "plus", "octagon" },
        };
        public static readonly Dictionary<string, string[]> ShapesByKind = new Dictionary<string, string[]>
        {
            ["secret"] = new[] { "rect", "chamfer" },
            ["arena"] = new[] { "octagon", "ring", "plus", "chamfer" },
            ["key"] = new[] { "octagon", "chamfer", "rect" },
        };

        public static Shape Pick(int w, int h, string size, string kind, Rng rng, Dictionary<string, double> weights)
        {
            var options = (ShapesByKind.TryGetValue(kind, out var k) ? k : ShapesBySize.TryGetValue(size, out var s) ? s : new[] { "rect" }).ToList();
            if (weights != null && weights.Count > 0 && weights.ContainsKey("rect") && !options.Contains("rect")) options.Add("rect");
            if (weights != null && weights.Count > 0 && kind != "secret")
                options.AddRange(ExtraShapes.Where(n => weights.TryGetValue(n, out var v) && v > 0 && !options.Contains(n)));
            for (int i = 0; i < 8; i++)
            {
                var shape = Make(w, h, rng.WeightedBias(options, weights), rng);
                if (shape.Ok()) return shape;
            }
            return new Shape(w, h);
        }
    }
}
