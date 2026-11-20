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

        public void Append(MeshGroup o)
        {
            int b = VertexCount;
            Pos.AddRange(o.Pos); Nrm.AddRange(o.Nrm); Uv.AddRange(o.Uv);
            foreach (var i in o.Idx) Idx.Add(i + b);
        }
    }

    /// <summary>Triangles grouped by part (a room, a corridor, an element like "room:5/vault") and material.</summary>
    public sealed class MeshBuilder
    {
        public const double UvScale = 64.0;
        public readonly Dictionary<string, Dictionary<string, MeshGroup>> Parts = new Dictionary<string, Dictionary<string, MeshGroup>>();
        public string Part = "level";

        /// <summary>All parts merged by material.</summary>
        public Dictionary<string, MeshGroup> Groups
        {
            get
            {
                var outp = new Dictionary<string, MeshGroup>();
                foreach (var p in Parts.OrderBy(k => k.Key, StringComparer.Ordinal))
                    foreach (var kv in p.Value)
                    {
                        if (!outp.TryGetValue(kv.Key, out var g)) outp[kv.Key] = g = new MeshGroup { Material = kv.Key };
                        g.Append(kv.Value);
                    }
                return outp;
            }
        }

        MeshGroup G(string mat)
        {
            if (!Parts.TryGetValue(Part, out var p)) Parts[Part] = p = new Dictionary<string, MeshGroup>();
            if (!p.TryGetValue(mat, out var g)) p[mat] = g = new MeshGroup { Material = mat };
            return g;
        }

        static (double, double, double) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c)
        {
            double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
            return (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
        }

        /// <summary>A convex polygon (3 or 4 corners), wound counter-clockwise seen from the normal side.</summary>
        public void Poly(string mat, IList<(double X, double Y, double Z)> pts, (double X, double Y, double Z) n, IList<(double U, double V)> uvs)
        {
            var g = G(mat);
            var cr = Cross(pts[0], pts[1], pts[2]);
            if (Math.Abs(cr.Item1) + Math.Abs(cr.Item2) + Math.Abs(cr.Item3) < 1e-9 && pts.Count > 3) cr = Cross(pts[0], pts[2], pts[3]);
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

    /// <summary>Level -> render mesh: only faces that border open space, merged where they line up,
    /// grouped by named part (port of export/mesh.py).</summary>
    public static class LevelMesh
    {
        static readonly (int, int)[] Dirs = { (1, 0), (-1, 0), (0, 1), (0, -1) };
        static readonly (double, double, double)[] Normals = { (-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0) };

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

        /// <summary>The room / corridor a span belongs to ("room:5", "corridor:3"), for naming parts.</summary>
        public static string AreaOf(Level level, Cell c, Span sp)
        {
            var o = level.OwnerOf(c, sp) ?? "";
            if (o.StartsWith("roof:")) return $"room:{o.Substring(5)}/roofscape";
            if (o.StartsWith("window:")) return "windows";
            return o.Length > 0 ? o : "level";
        }

        public static string PartOf(Level level, Cell c, Span sp, string face, Cell? n = null)
        {
            if (face == "wall" && n != null && level.PartSolid.TryGetValue(n.Value, out var ps)) return ps;
            if (face == "ceiling" && level.PartCeiling.TryGetValue(c, out var pc) && level.Spans[c][level.Spans[c].Count - 1].Equals(sp)) return pc;
            return level.PartCells.TryGetValue(c, out var pcl) ? pcl : AreaOf(level, c, sp);
        }

        public static string FloorMaterial(Level level, Cell c) => level.Hazard.TryGetValue(c, out var h) ? "hazard_" + h : "floor";

        public static MeshBuilder Build(Level level, bool entities = true)
        {
            int s = level.CellSize;
            var mb = new MeshBuilder();
            var flats = new Dictionary<(string Part, string Mat, int Z), List<Cell>>();
            var walls = new Dictionary<(string Part, int D, int Line, int A, int B, string Mat), List<int>>();
            foreach (var kv in level.Spans.OrderBy(k => k.Key))
            {
                var c = kv.Key; var spans = kv.Value;
                bool diag = level.Diag.TryGetValue(c, out var corner);
                if (diag) { mb.Part = PartOf(level, c, spans[0], "floor"); DiagFaces(mb, level, c, corner, spans[0]); }
                if (level.CeilSlope.ContainsKey(c)) { mb.Part = PartOf(level, c, spans[spans.Count - 1], "ceiling"); SlopedCeiling(mb, level, c); }
                if (level.FloorSlope.ContainsKey(c) && !diag) { mb.Part = PartOf(level, c, spans[0], "floor"); SlopedFloor(mb, level, c); }
                if (level.Water.TryGetValue(c, out var S) && S < spans[0].Ceil) Add(flats, ("water", "water", S), c);
                for (int si = 0; si < spans.Count; si++)
                {
                    var sp = spans[si];
                    if (!diag)
                    {
                        if (!(level.FloorSlope.ContainsKey(c) && si == 0))
                        {
                            string mat = level.Hazard.ContainsKey(c) && si == 0 ? "hazard_" + level.Hazard[c] : "floor";
                            Add(flats, (PartOf(level, c, sp, "floor"), mat, sp.Floor), c);
                        }
                        bool top = level.Sky.Contains(c) && si == spans.Count - 1;
                        if (!(level.CeilSlope.ContainsKey(c) && si == spans.Count - 1))
                            Add(flats, (PartOf(level, c, sp, "ceiling"), top ? "sky" : "ceiling", sp.Ceil), c);
                    }
                    for (int d = 0; d < 4; d++)
                    {
                        var (dx, dy) = Dirs[d];
                        if (diag && Shape.SolidSides(corner).Contains((dx, dy))) continue;
                        var n = c.Offset(dx, dy);
                        var nspans = level.Spans.TryGetValue(n, out var nl) ? nl : new List<Span>();
                        if (level.Diag.TryGetValue(n, out var nc) && Shape.SolidSides(nc).Contains((-dx, -dy))) nspans = new List<Span>();
                        if (level.CeilSlope.ContainsKey(c) || level.CeilSlope.ContainsKey(n) || level.FloorSlope.ContainsKey(c) || level.FloorSlope.ContainsKey(n))
                        {
                            mb.Part = PartOf(level, c, sp, "wall", n);
                            SlopedEdge(mb, level, c, sp, spans, d, nspans);
                            continue;
                        }
                        foreach (var (a, b) in Subtract(sp.Floor, sp.Ceil, nspans.Select(x => (x.Floor, x.Ceil))))
                        {
                            int line = dx != 0 ? c.X + (dx > 0 ? 1 : 0) : c.Y + (dy > 0 ? 1 : 0);
                            int along = dx != 0 ? c.Y : c.X;
                            bool isCeil = b == sp.Ceil && nspans.Any(x => x.Ceil <= a && x.Floor < a);
                            string part = PartOf(level, c, sp, isCeil ? "ceiling" : "wall", n);
                            string mat = level.SkyWalls.Contains(n) ? "sky" : isCeil ? "ceiling" : "wall";
                            var key = (part, d, line, a, b, mat);
                            if (!walls.TryGetValue(key, out var l)) walls[key] = l = new List<int>();
                            l.Add(along);
                        }
                    }
                }
            }
            foreach (var kv in flats)
            {
                mb.Part = kv.Key.Part;
                var mat = kv.Key.Mat;
                int up = mat == "floor" || mat == "water" || mat.StartsWith("hazard") ? 1 : -1;
                foreach (var (x0, y0, x1, y1) in Brushes.GreedyRects(kv.Value))
                    mb.HQuad(mat, x0 * s, y0 * s, x1 * s, y1 * s, kv.Key.Z, up);
            }
            foreach (var kv in walls)
            {
                var (part, d, line, a, b, mat) = kv.Key;
                mb.Part = part;
                var al = kv.Value; al.Sort();
                int i = 0;
                while (i < al.Count)
                {
                    int p0 = al[i], p1 = p0 + 1; i++;
                    while (i < al.Count && al[i] == p1) { p1++; i++; }
                    if (d < 2) mb.VQuad(mat, line * s, p0 * s, line * s, p1 * s, a, b, Normals[d]);
                    else mb.VQuad(mat, p0 * s, line * s, p1 * s, line * s, a, b, Normals[d]);
                }
            }
            if (entities) { mb.Part = "entities"; AddEntities(level, mb); }
            return mb;
        }

        static void Add<K>(Dictionary<K, List<Cell>> d, K k, Cell c) { if (!d.TryGetValue(k, out var l)) d[k] = l = new List<Cell>(); l.Add(c); }

        static void SlopedCeiling(MeshBuilder mb, Level level, Cell c)
        {
            int s = level.CellSize; double u = MeshBuilder.UvScale;
            var (z00, z10, z11, z01) = level.CeilSlope[c];
            double X0 = c.X * s, Y0 = c.Y * s, X1 = (c.X + 1) * s, Y1 = (c.Y + 1) * s;
            var pts = new[] { (X0, Y0, (double)z00), (X1, Y0, (double)z10), (X1, Y1, (double)z11), (X0, Y1, (double)z01) };
            double ax = X1 - X0, az = z10 - z00, by = Y1 - Y0, bz = z01 - z00;
            double nx = -az * by, ny = -ax * bz, nz = ax * by;
            if (nz > 0) { nx = -nx; ny = -ny; nz = -nz; }
            double ln = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            mb.Poly("ceiling", pts, (nx / ln, ny / ln, nz / ln), pts.Select(p => (p.Item1 / u, p.Item2 / u)).ToArray());
        }

        static void SlopedFloor(MeshBuilder mb, Level level, Cell c)
        {
            int s = level.CellSize; double u = MeshBuilder.UvScale;
            var (z00, z10, z11, z01) = level.FloorSlope[c];
            double X0 = c.X * s, Y0 = c.Y * s, X1 = (c.X + 1) * s, Y1 = (c.Y + 1) * s;
            var pts = new[] { (X0, Y0, (double)z00), (X1, Y0, (double)z10), (X1, Y1, (double)z11), (X0, Y1, (double)z01) };
            double ax = X1 - X0, az = z10 - z00, by = Y1 - Y0, bz = z01 - z00;
            double nx = -az * by, ny = -ax * bz, nz = ax * by;
            if (nz < 0) { nx = -nx; ny = -ny; nz = -nz; }
            double ln = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            mb.Poly(FloorMaterial(level, c), pts, (nx / ln, ny / ln, nz / ln), pts.Select(p => (p.Item1 / u, p.Item2 / u)).ToArray());
        }

        static (double, double) FloorLine(Level level, Cell c, Span span, List<Span> spans, int d)
        {
            if (!level.FloorSlope.TryGetValue(c, out var sl) || spans.Count == 0 || !span.Equals(spans[0]) || level.Diag.ContainsKey(c)) return (span.Floor, span.Floor);
            var (z00, z10, z11, z01) = sl;
            switch (d) { case 0: return (z10, z11); case 1: return (z00, z01); case 2: return (z01, z11); default: return (z00, z10); }
        }

        static (double, double) EdgeLine(Level level, Cell c, Span span, List<Span> spans, int d)
        {
            if (!level.CeilSlope.TryGetValue(c, out var sl) || spans.Count == 0 || !span.Equals(spans[spans.Count - 1])) return (span.Ceil, span.Ceil);
            var (z00, z10, z11, z01) = sl;
            switch (d) { case 0: return (z10, z11); case 1: return (z00, z01); case 2: return (z01, z11); default: return (z00, z10); }
        }

        /// <summary>Side faces of one span towards a neighbour where either has a sloped floor or ceiling:
        /// every bound is linear along the edge; split where bounds cross, and in each piece the open
        /// part of c minus the open parts of n becomes quads.</summary>
        static void SlopedEdge(MeshBuilder mb, Level level, Cell c, Span span, List<Span> spans, int d, List<Span> nspans)
        {
            var (dx, dy) = Dirs[d];
            var n = c.Offset(dx, dy);
            string wallMat = level.SkyWalls.Contains(n) ? "sky" : "wall";
            int s = level.CellSize; double uvs = MeshBuilder.UvScale;
            int nd = new[] { 1, 0, 3, 2 }[d];
            var loC = FloorLine(level, c, span, spans, d);
            var hiC = EdgeLine(level, c, span, spans, d);
            var others = nspans.Select(x => (Lo: FloorLine(level, n, x, nspans, nd), Hi: EdgeLine(level, n, x, nspans, nd))).ToList();
            var lines = new List<(double, double)> { loC, hiC };
            foreach (var o in others) { lines.Add(o.Lo); lines.Add(o.Hi); }
            double At((double, double) l, double t) => l.Item1 + (l.Item2 - l.Item1) * t;
            var ts = new SortedSet<double> { 0.0, 1.0 };
            for (int i = 0; i < lines.Count; i++)
                for (int j = i + 1; j < lines.Count; j++)
                {
                    var a = lines[i]; var b = lines[j];
                    double den = (a.Item1 - b.Item1) - (a.Item2 - b.Item2);
                    if (Math.Abs(den) > 1e-12) { double tt = (a.Item1 - b.Item1) / den; if (tt > 0 && tt < 1) ts.Add(tt); }
                }
            var tl = ts.ToList();
            int line = dx != 0 ? c.X + (dx > 0 ? 1 : 0) : c.Y + (dy > 0 ? 1 : 0);
            (double, double) Pos(double t) => dx != 0 ? ((double)line * s, (c.Y + t) * s) : ((c.X + t) * s, (double)line * s);
            for (int k = 0; k + 1 < tl.Count; k++)
            {
                double t0 = tl[k], t1 = tl[k + 1], tm = (t0 + t1) / 2;
                var pieces = new List<((double, double) Lo, (double, double) Hi)> { (loC, hiC) };
                foreach (var o in others.OrderBy(o => At(o.Lo, tm)))
                {
                    var outp = new List<((double, double), (double, double))>();
                    foreach (var (plo, phi) in pieces)
                    {
                        double a = At(plo, tm), b = At(phi, tm), na = At(o.Lo, tm), nb = At(o.Hi, tm);
                        if (nb <= a || na >= b) { outp.Add((plo, phi)); continue; }
                        if (na > a) outp.Add((plo, o.Lo));
                        if (nb < b) outp.Add((o.Hi, phi));
                    }
                    pieces = outp;
                }
                foreach (var (plo, phi) in pieces)
                {
                    var z = new[] { At(plo, t0), At(plo, t1), At(phi, t1), At(phi, t0) };
                    if (z[3] - z[0] < 0.01 && z[2] - z[1] < 0.01) continue;
                    bool ceil = phi.Equals(hiC) && others.Any(o => At(o.Hi, tm) <= At(plo, tm) + 0.01 && At(o.Lo, tm) < At(plo, tm));
                    var p0 = Pos(t0); var p1 = Pos(t1);
                    double a0 = dx != 0 ? p0.Item2 : p0.Item1, a1 = dx != 0 ? p1.Item2 : p1.Item1;
                    mb.Poly(ceil ? "ceiling" : wallMat,
                        new[] { (p0.Item1, p0.Item2, z[0]), (p1.Item1, p1.Item2, z[1]), (p1.Item1, p1.Item2, z[2]), (p0.Item1, p0.Item2, z[3]) },
                        Normals[d], new[] { (a0 / uvs, z[0] / uvs), (a1 / uvs, z[1] / uvs), (a1 / uvs, z[2] / uvs), (a0 / uvs, z[3] / uvs) });
                }
            }
        }

        static void DiagFaces(MeshBuilder mb, Level level, Cell c, Corner corner, Span sp)
        {
            int s = level.CellSize; double u = MeshBuilder.UvScale;
            var (solid, open) = Shape.CornerVertices(corner);
            var o = open.Select(p => ((c.X + p.Item1) * s, (c.Y + p.Item2) * s)).ToArray();
            var uv = o.Select(p => (p.Item1 / u, p.Item2 / u)).ToArray();
            mb.Poly(FloorMaterial(level, c), o.Select(p => (p.Item1, p.Item2, (double)sp.Floor)).ToArray(), (0, 0, 1), uv);
            mb.Poly(level.Sky.Contains(c) ? "sky" : "ceiling", o.Select(p => (p.Item1, p.Item2, (double)sp.Ceil)).ToArray(), (0, 0, -1), uv);
            var h0 = solid[1]; var h1 = solid[2];
            double nx = open[0].Item1 - solid[0].Item1, ny = open[0].Item2 - solid[0].Item2, nl = Math.Sqrt(nx * nx + ny * ny);
            mb.VQuad("wall", (c.X + h0.Item1) * s, (c.Y + h0.Item2) * s, (c.X + h1.Item1) * s, (c.Y + h1.Item2) * s, sp.Floor, sp.Ceil, (nx / nl, ny / nl, 0));
        }

        /// <summary>Doors, lifts, ladders, glass, jump pads and the exit as boxes (static: the previewer animates its own).</summary>
        public static void AddEntities(Level level, MeshBuilder mb)
        {
            foreach (var e in level.Entities)
            {
                if (e.Bounds == null) continue;
                var (lo, hi) = e.Bounds.Value;
                switch (e.Kind)
                {
                    case "door": mb.Box(e.Props.TryGetValue("key", out var k) && (string)k == "blue" ? "door_blue" : "door", lo, hi); break;
                    case "secret_door": mb.Box("secret", lo, hi); break;
                    case "lift": mb.Box("lift", lo, hi); break;
                    case "ladder": mb.Box("ladder", lo, hi); break;
                    case "exit": mb.Box("trigger", lo, hi); break;
                    case "jumppad": mb.Box("lift", lo, new Vec3(hi.X, hi.Y, lo.Z + 4)); break;
                    case "window": if (e.Props.TryGetValue("glazed", out var g) && g is bool gb && gb) mb.Box("glass", lo, hi); break;
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
            ["hazard_lava"] = new[] { 0.95f, 0.35f, 0.08f, 1f }, ["hazard_spikes"] = new[] { 0.45f, 0.12f, 0.10f, 1f },
            ["water"] = new[] { 0.22f, 0.45f, 0.70f, 0.5f },
        };

        public static float[] ColorOf(string mat) => Colors.TryGetValue(mat, out var c) ? c : new[] { 0.7f, 0.7f, 0.7f, 1f };
    }
}
