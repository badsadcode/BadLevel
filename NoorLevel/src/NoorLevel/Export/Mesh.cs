using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel.Export
{
    /// <summary>Triangles of one material: positions (Z-up map units), normals, UVs (1 per 64 units), indices.</summary>
    public sealed class MeshGroup
    {
        public string Material;
        public List<float> Pos = new List<float>(), Nrm = new List<float>(), Uv = new List<float>();
        public List<int> Idx = new List<int>();
        public int VertexCount => Pos.Count / 3;
    }

    public sealed class MeshBuilder
    {
        public const double UvScale = 64.0;
        public readonly Dictionary<string, MeshGroup> Groups = new Dictionary<string, MeshGroup>();

        MeshGroup G(string mat)
        {
            if (!Groups.TryGetValue(mat, out var g)) Groups[mat] = g = new MeshGroup { Material = mat };
            return g;
        }

        static (double, double, double) Cross((double, double, double) a, (double, double, double) b, (double, double, double) c)
        {
            double ux = b.Item1 - a.Item1, uy = b.Item2 - a.Item2, uz = b.Item3 - a.Item3, vx = c.Item1 - a.Item1, vy = c.Item2 - a.Item2, vz = c.Item3 - a.Item3;
            return (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
        }

        /// <summary>A convex polygon (3 or 4 corners) wound counter-clockwise seen from the normal side.</summary>
        public void Poly(string mat, IList<(double X, double Y, double Z)> pts, (double X, double Y, double Z) n, IList<(double U, double V)> uvs)
        {
            var g = G(mat);
            var cr = Cross(pts[0], pts[1], pts[2]);
            bool flip = cr.Item1 * n.X + cr.Item2 * n.Y + cr.Item3 * n.Z < 0;
            int b = g.VertexCount;
            for (int i = 0; i < pts.Count; i++)
            {
                int k = flip ? pts.Count - 1 - i : i;
                g.Pos.Add((float)pts[k].X); g.Pos.Add((float)pts[k].Y); g.Pos.Add((float)pts[k].Z);
                g.Nrm.Add((float)n.X); g.Nrm.Add((float)n.Y); g.Nrm.Add((float)n.Z);
                g.Uv.Add((float)uvs[k].U); g.Uv.Add((float)uvs[k].V);
            }
            for (int i = 1; i + 1 < pts.Count; i++) { g.Idx.Add(b); g.Idx.Add(b + i); g.Idx.Add(b + i + 1); }
        }

        public void HQuad(string mat, double x0, double y0, double x1, double y1, double z, int up)
        {
            double s = UvScale;
            Poly(mat, new[] { (x0, y0, z), (x1, y0, z), (x1, y1, z), (x0, y1, z) }, (0, 0, up),
                 new[] { (x0 / s, y0 / s), (x1 / s, y0 / s), (x1 / s, y1 / s), (x0 / s, y1 / s) });
        }

        /// <summary>A vertical wall quad from (ax, ay) to (bx, by), z0..z1, facing n.</summary>
        public void VQuad(string mat, double ax, double ay, double bx, double by, double z0, double z1, (double, double, double) n)
        {
            double s = UvScale;
            double ua = (ax + ay) / s, ub = (bx + by) / s;
            if (Math.Abs(ax - bx) < 1e-9) { ua = ay / s; ub = by / s; } else if (Math.Abs(ay - by) < 1e-9) { ua = ax / s; ub = bx / s; }
            Poly(mat, new[] { (ax, ay, z0), (bx, by, z0), (bx, by, z1), (ax, ay, z1) }, n,
                 new[] { (ua, z0 / s), (ub, z0 / s), (ub, z1 / s), (ua, z1 / s) });
        }

        public void Box(string mat, Vec3 lo, Vec3 hi)
        {
            HQuad(mat, lo.X, lo.Y, hi.X, hi.Y, hi.Z, 1);
            HQuad(mat, lo.X, lo.Y, hi.X, hi.Y, lo.Z, -1);
            VQuad(mat, lo.X, lo.Y, lo.X, hi.Y, lo.Z, hi.Z, (-1, 0, 0));
            VQuad(mat, hi.X, lo.Y, hi.X, hi.Y, lo.Z, hi.Z, (1, 0, 0));
            VQuad(mat, lo.X, lo.Y, hi.X, lo.Y, lo.Z, hi.Z, (0, -1, 0));
            VQuad(mat, lo.X, hi.Y, hi.X, hi.Y, lo.Z, hi.Z, (0, 1, 0));
        }
    }

    /// <summary>Level -> render mesh: only faces that border open space, merged where they line up.</summary>
    public static class LevelMesh
    {
        static readonly (int, int)[] Dirs = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        static List<(int, int)> Subtract(int a, int b, IEnumerable<(int, int)> others)
        {
            var outp = new List<(int, int)>();
            int z = a;
            foreach (var (f, c) in others.OrderBy(o => o.Item1))
            {
                if (c <= z || f >= b) continue;
                if (f > z) outp.Add((z, Math.Min(f, b)));
                z = Math.Max(z, c);
                if (z >= b) break;
            }
            if (z < b) outp.Add((z, b));
            return outp;
        }

        /// <summary>The open intervals of cell n along its side facing direction d (from n).</summary>
        static IEnumerable<(int, int)> OpenOnSide(Level level, Cell n, (int, int) d)
        {
            if (!level.Spans.TryGetValue(n, out var spans)) yield break;
            if (level.Diag.TryGetValue(n, out var corner) && Shape.SolidSides(corner).Contains(d)) yield break;
            foreach (var s in spans) yield return (s.Floor, s.Ceil);
        }

        public static string FloorMaterial(Level level, Cell c) => level.Hazard.TryGetValue(c, out var h) ? "hazard_" + h : "floor";

        public static MeshBuilder Build(Level level, bool entities = true)
        {
            var mb = new MeshBuilder();
            int s = level.CellSize;
            var floors = new Dictionary<(int, string), List<Cell>>();
            var ceils = new Dictionary<(int, string), List<Cell>>();
            // walls: (dir, line, z0, z1) -> positions along the line
            var walls = new Dictionary<(int, int, int, int, int), List<int>>();
            foreach (var kv in level.Spans)
            {
                var c = kv.Key;
                bool diag = level.Diag.TryGetValue(c, out var corner);
                foreach (var sp in kv.Value)
                {
                    if (diag)
                    {
                        var (solid, open) = Shape.CornerVertices(corner);
                        var o = open.Select(p => ((c.X + p.Item1) * s, (c.Y + p.Item2) * s)).ToArray();
                        var uv = o.Select(p => (p.Item1 / MeshBuilder.UvScale, p.Item2 / MeshBuilder.UvScale)).ToArray();
                        mb.Poly(FloorMaterial(level, c), o.Select(p => (p.Item1, p.Item2, (double)sp.Floor)).ToArray(), (0, 0, 1), uv);
                        mb.Poly(level.Sky.Contains(c) ? "sky" : "ceiling", o.Select(p => (p.Item1, p.Item2, (double)sp.Ceil)).ToArray(), (0, 0, -1), uv);
                        var h0 = solid[1]; var h1 = solid[2];
                        double nx = open[0].Item1 - solid[0].Item1, ny = open[0].Item2 - solid[0].Item2, nl = Math.Sqrt(nx * nx + ny * ny);
                        mb.VQuad("wall", (c.X + h0.Item1) * s, (c.Y + h0.Item2) * s, (c.X + h1.Item1) * s, (c.Y + h1.Item2) * s, sp.Floor, sp.Ceil, (nx / nl, ny / nl, 0));
                    }
                    else
                    {
                        Add(floors, (sp.Floor, FloorMaterial(level, c)), c);
                        Add(ceils, (sp.Ceil, level.Sky.Contains(c) ? "sky" : "ceiling"), c);
                    }
                    for (int di = 0; di < 4; di++)
                    {
                        var d = Dirs[di];
                        if (diag && Shape.SolidSides(corner).Contains(d)) continue;
                        var n = c.Offset(d.Item1, d.Item2);
                        foreach (var (z0, z1) in Subtract(sp.Floor, sp.Ceil, OpenOnSide(level, n, (-d.Item1, -d.Item2))))
                        {
                            int line = d.Item1 != 0 ? c.X + (d.Item1 > 0 ? 1 : 0) : c.Y + (d.Item2 > 0 ? 1 : 0);
                            int along = d.Item1 != 0 ? c.Y : c.X;
                            Add(walls, (di, line, z0, z1, 0), along);
                        }
                    }
                }
            }
            foreach (var kv in floors)
                foreach (var (x0, y0, x1, y1) in Brushes.GreedyRects(kv.Value))
                    mb.HQuad(kv.Key.Item2, x0 * s, y0 * s, x1 * s, y1 * s, kv.Key.Item1, 1);
            foreach (var kv in ceils)
                foreach (var (x0, y0, x1, y1) in Brushes.GreedyRects(kv.Value))
                    mb.HQuad(kv.Key.Item2, x0 * s, y0 * s, x1 * s, y1 * s, kv.Key.Item1, -1);
            foreach (var kv in walls)
            {
                var (di, line, z0, z1, _) = kv.Key;
                var d = Dirs[di];
                var pos = kv.Value; pos.Sort();
                int i = 0;
                while (i < pos.Count)
                {
                    int a = pos[i], b = a + 1; i++;
                    while (i < pos.Count && pos[i] == b) { b++; i++; }
                    var nrm = ((double)-d.Item1, (double)-d.Item2, 0.0);
                    if (d.Item1 != 0) mb.VQuad("wall", line * s, a * s, line * s, b * s, z0, z1, nrm);
                    else mb.VQuad("wall", a * s, line * s, b * s, line * s, z0, z1, nrm);
                }
            }
            if (entities) AddEntities(level, mb);
            return mb;
        }

        static void Add<K>(Dictionary<K, List<Cell>> d, K k, Cell c) { if (!d.TryGetValue(k, out var l)) d[k] = l = new List<Cell>(); l.Add(c); }
        static void Add<K>(Dictionary<K, List<int>> d, K k, int c) { if (!d.TryGetValue(k, out var l)) d[k] = l = new List<int>(); l.Add(c); }

        /// <summary>Doors, lifts, ladders and the exit as boxes (static: the previewer animates its own).</summary>
        public static void AddEntities(Level level, MeshBuilder mb)
        {
            foreach (var e in level.Entities)
            {
                if (e.Bounds == null) continue;
                var (lo, hi) = e.Bounds.Value;
                switch (e.Kind)
                {
                    case "door": mb.Box((string)e.Props["key"] == "blue" ? "door_blue" : "door", lo, hi); break;
                    case "secret_door": mb.Box("secret", lo, hi); break;
                    case "lift": mb.Box("lift", lo, hi); break;
                    case "ladder": mb.Box("ladder", lo, hi); break;
                    case "exit": mb.Box("trigger", lo, hi); break;
                }
            }
        }

        public static readonly Dictionary<string, float[]> Colors = new Dictionary<string, float[]>
        {
            ["floor"] = new[] { 0.55f, 0.50f, 0.44f, 1f }, ["wall"] = new[] { 0.72f, 0.72f, 0.74f, 1f },
            ["ceiling"] = new[] { 0.38f, 0.40f, 0.45f, 1f }, ["sky"] = new[] { 0.45f, 0.62f, 0.90f, 1f },
            ["door"] = new[] { 0.80f, 0.25f, 0.20f, 1f }, ["door_blue"] = new[] { 0.20f, 0.35f, 0.85f, 1f },
            ["secret"] = new[] { 0.60f, 0.45f, 0.80f, 1f }, ["trigger"] = new[] { 0.20f, 0.90f, 0.40f, 0.45f },
            ["lift"] = new[] { 0.95f, 0.75f, 0.15f, 1f }, ["ladder"] = new[] { 0.55f, 0.35f, 0.20f, 1f },
            ["glass"] = new[] { 0.55f, 0.80f, 0.95f, 0.3f }, ["hazard_slime"] = new[] { 0.35f, 0.85f, 0.20f, 1f },
            ["hazard_lava"] = new[] { 0.95f, 0.35f, 0.08f, 1f }, ["water"] = new[] { 0.22f, 0.45f, 0.70f, 0.5f },
        };

        public static float[] ColorOf(string mat) => Colors.TryGetValue(mat, out var c) ? c : new[] { 0.7f, 0.7f, 0.7f, 1f };
    }
}
