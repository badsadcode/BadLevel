using System;
using System.Collections.Generic;
using System.Linq;
using static NoorLevel.Architecture;

// walls.py: structure along a room's walls, so long walls stop being flat cliffs.
//
// Every eligible room of a styled level gets a rhythm along its walls, aligned with its bays when
// its cross-section has them:
//   inward   pilasters / shafts / counterforts on the bay lines (placed before the features, like piers)
//   outward  recesses cut into the solid wall between the members, and on the end wall of a big
//            hall one deep feature (an apse niche, a loading door, a blast door, a bay window)
//   above    a crane corbel along the long walls of tall industrial halls
//   dress    bases and capitals on the members, one order per room
// Safety: a recess is carved only into solid rock with one solid cell kept between it and any
// other space (8-neighbourhood), never at a doorway, never through the level border; all or
// nothing per recess. Members keep the pier rules. The validator still checks everything.
namespace NoorLevel
{
    /// <summary>One wall of the room's bounding rectangle, in frame coordinates.</summary>
    public sealed class Side
    {
        public readonly Frame Fr;
        public readonly string Which;
        public readonly bool Long;
        public readonly int Length;

        public Side(Frame fr, string which)
        {
            Fr = fr;
            Which = which;
            Long = which == "v0" || which == "v1";
            Length = Long ? fr.L : fr.W;
        }

        /// <summary>(u, v) of the room cell at position a along this wall.</summary>
        public (int, int) Inner(int a) => Which switch
        {
            "v0" => (a, 0),
            "v1" => (a, Fr.W - 1),
            "u0" => (0, a),
            _ => (Fr.L - 1, a),
        };

        /// <summary>World direction pointing out of the room through this wall.</summary>
        public (int X, int Y) OutDir()
        {
            var (du, dv) = Which switch { "v0" => (0, -1), "v1" => (0, 1), "u0" => (-1, 0), _ => (1, 0) };
            if (Fr.Flip) dv = -dv;
            return Fr.AlongX ? (du, dv) : (dv, du);
        }
    }

    public static partial class Walls
    {
        /// <summary>style -> (bay factor, bay min, bay max)</summary>
        public static readonly Dictionary<string, (double K, int Lo, int Hi)> Rhythm = new Dictionary<string, (double, int, int)>
        {
            ["gothic"] = (0.42, 3, 8), ["industrial"] = (0.7, 4, 8), ["bunker"] = (0.4, 3, 4), ["compound"] = (0.6, 4, 7),
            ["egyptian"] = (0.5, 3, 6), ["scifi"] = (0.35, 3, 4), ["castle"] = (0.5, 4, 7),
        };

        static IEnumerable<Cell> Neighbours8(Cell c)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0) yield return c.Offset(dx, dy);
        }

        static readonly (int, int)[] Quad = { (0, 0), (1, 0), (1, 1), (0, 1) };

        /// <summary>Cut a recess depth cells into the wall behind the room cells (in a row), open from
        /// room.Floor + z0 to room.Floor + z1. head(along) -> z (absolute) gives the recess a sloped top.
        /// All or nothing.</summary>
        public static bool CarveRecess(Level level, Room room, RoomCtx ctx, List<Cell> cells, (int X, int Y) d, int depth,
                                       int z0, int z1, Func<double, double> head = null, HashSet<Cell> nearDoor = null,
                                       string label = "niches")
        {
            nearDoor ??= new HashSet<Cell>();
            var outCells = new List<Cell>();
            foreach (var r in cells)
                for (int k = 1; k <= depth; k++) outCells.Add(r.Offset(d.X * k, d.Y * k));
            var planned = new HashSet<Cell>(outCells);
            foreach (var r in cells)
            {
                // (Used is not checked on roomy levels: it holds the rings kept free round the members)
                if (!room.Contains(r) || room.IsDiag(r.X, r.Y) || nearDoor.Contains(r) || !IsPlain(level, room, r))
                    return false;
                if (ctx.Used.Contains(r) && level.Config.Spaciousness == 0.0)
                    return false;                               // (the original rule, kept for --space 0)
            }
            foreach (var o in outCells)
            {
                if ((level.Spans.TryGetValue(o, out var sp) && sp.Count > 0) || level.Diag.ContainsKey(o) || level.Glass.Contains(o)
                    || (level.Owners.TryGetValue(o, out var ow) && ow.Count > 0))
                    return false;
                if (!(1 < o.X && o.X < level.Width - 2 && 1 < o.Y && o.Y < level.Height - 2))
                    return false;
                foreach (var n in Neighbours8(o))      // one solid cell stays between the recess and anything else
                {
                    if (planned.Contains(n) || room.Contains(n)) continue;
                    if ((level.Spans.TryGetValue(n, out var ns) && ns.Count > 0) || level.Glass.Contains(n))
                        return false;
                }
            }
            int lo = room.Floor + z0, hi = room.Floor + z1;
            foreach (var o in outCells)
            {
                level.PartCells[o] = $"room:{room.Id}/{label}";
                if (head == null)
                {
                    level.AddSpan(o, new Span(lo, hi), $"room:{room.Id}", room.Slot);
                    continue;
                }
                var zs = Quad.Select(q => (int)Math.Round(head(d.Y != 0 ? o.X + q.Item1 : o.Y + q.Item2))).ToArray();
                int top = FloorDiv(zs.Min(), 16) * 16;          // the span keeps the lowest point, the slope the rest
                level.AddSpan(o, new Span(lo, top), $"room:{room.Id}", room.Slot);
                if (zs.Max() > top) level.CeilSlope[o] = (zs[0], zs[1], zs[2], zs[3]);
            }
            return true;
        }

        static int LocalCeiling(Section sec, Frame fr, (int, int) uv) =>
            sec != null && sec.Ceil.TryGetValue(uv, out int h) ? h : fr.H;

        sealed class EndSpec { public double FracW; public int Depth; public double? FracH; public int Top; public int Z0; public bool Pointed; }
        sealed class Spec { public int Width; public int Z0; public double? Frac; public int Top; public int Every; public int Depth; public bool Pointed; }

        /// <summary>Members and recesses along the walls of one room. Returns counts for room.Meta.</summary>
        public static Dictionary<string, object> Articulate(Level level, Room room, RoomCtx ctx, Section sec, string tier)
        {
            var cfg = level.Config;
            string style = cfg.ArchStyle ?? "";
            var counts = new Dictionary<string, object>();
            if (!Rhythm.ContainsKey(style) || !cfg.ArchWalls) return counts;
            var rng = Rng.FromLabel($"{cfg.Seed}/{cfg.Variant}/walls/{room.Id}");
            bool smooth = cfg.ArchSmooth;                     // pointed heads are slopes: a strict grid keeps them flat
            var fr = sec != null ? sec.Frame : new Frame(room);
            int H = fr.H;
            var (k, lo, hi) = Rhythm[style];
            int bay = sec != null && sec.Info.TryGetValue("bay", out var bo) && bo is int bi && bi != 0 ? bi : BaySize(H, fr.L, k, lo, hi);
            var nearDoor = Around(ctx.Doors, 2);
            counts["members"] = 0;
            counts["recesses"] = 0;
            counts["end"] = 0;
            void Inc(string key, int by = 1) => counts[key] = (int)counts[key] + by;

            // --- a cavern's walls are rough-cut: recesses of random width, depth and height, no rhythm
            if (sec != null && sec.Name == "cavern")
            {
                foreach (var sideName in new[] { "v0", "v1", "u0", "u1" })
                {
                    var side = new Side(fr, sideName);
                    int a = rng.Int(1, 3);
                    while (a < side.Length - 2)
                    {
                        int w = rng.Int(1, 3);
                        if (a + w > side.Length - 1) break;
                        var cells = Enumerable.Range(a, w).Select(x => { var t = side.Inner(x); return fr.Cell(t.Item1, t.Item2); }).ToList();
                        int ceilHere = Enumerable.Range(a, w).Min(x => LocalCeiling(sec, fr, side.Inner(x)));
                        int top = Math.Min(Q(rng.Uniform(96, H * 0.7)), ceilHere - 16);
                        if (top >= 96 && CarveRecess(level, room, ctx, cells, side.OutDir(), rng.Choice(new[] { 1, 1, 2 }), 0,
                                                     top, null, nearDoor, "rough walls"))
                            Inc("recesses");
                        a += w + rng.Int(1, 4);
                    }
                }
                counts["rough"] = 1;
                return counts;
            }

            // --- the end walls of big halls: one strong feature, opposite or beside the entrance
            if ((tier == "grand" || tier == "major") && fr.W >= 7)
            {
                var end = style switch
                {
                    "gothic" => new EndSpec { FracW = 0.34, Depth = 2, FracH = 0.7, Z0 = 0, Pointed = true },
                    "industrial" => new EndSpec { FracW = 0.3, Depth = 1, FracH = null, Top = 176, Z0 = 0 },
                    "bunker" => new EndSpec { FracW = 0.3, Depth = 1, FracH = null, Top = 112, Z0 = 0 },
                    "compound" => new EndSpec { FracW = 0.3, Depth = 1, FracH = 0.6, Z0 = 32 },
                    // a sanctuary niche at the end of the axis; a bulkhead door; a fireplace
                    "egyptian" => new EndSpec { FracW = 0.25, Depth = 2, FracH = 0.6, Z0 = 0 },
                    "scifi" => new EndSpec { FracW = 0.3, Depth = 1, FracH = null, Top = 128, Z0 = 0 },
                    _ => new EndSpec { FracW = 0.25, Depth = 1, FracH = null, Top = 96, Z0 = 0 },     // castle
                };
                int w = Math.Max(3, Math.Min(7, (int)Math.Round(fr.W * end.FracW)));
                var tries = rng.Sample(new[] { "u0", "u1" }, 2).Select(s => (Side: s, A0: (fr.W - w) / 2, Axial: false, W: w)).ToList();
                var focus = IncomingAxis(level, room, fr, ctx);
                if (focus != null) counts["focus"] = 1;
                if (focus != null && rng.Random() < cfg.ArchAxial)
                {
                    var (far, aMid) = focus.Value;               // a framed reveal: the feature faces the way in
                    int length = far == "v0" || far == "v1" ? fr.L : fr.W;
                    int a0 = Math.Max(1, Math.Min(length - 1 - w, (int)Math.Round(aMid - w / 2.0)));
                    var narrow = new List<(string Side, int A0, bool Axial, int W)>();
                    foreach (int shift in new[] { 2, -2, 1, -1, 0 })    // it may slide up to 2 cells off a doorway,
                    {
                        tries.Insert(0, (far, a0 + shift, true, w));
                        if (w > 3)                                       // or narrow down to 3 between wide doorways
                            narrow.Insert(0, (far, (int)Math.Round(aMid - 1.5) + shift, true, 3));
                    }
                    tries.InsertRange(Math.Min(5, tries.Count), narrow);
                }
                foreach (var (sideName, a0, axial, ww) in tries)
                {
                    var side = new Side(fr, sideName);
                    if (a0 < 1 || a0 + ww > side.Length - 1) continue;
                    var cells = Enumerable.Range(a0, ww).Select(a => { var t = side.Inner(a); return fr.Cell(t.Item1, t.Item2); }).ToList();
                    int ceilHere = Enumerable.Range(a0, ww).Min(a => LocalCeiling(sec, fr, side.Inner(a)));
                    int top = end.FracH != null ? Q(H * end.FracH.Value) : end.Top;
                    top = Math.Min(top, ceilHere - 16);
                    if (top - end.Z0 < 96) continue;
                    Func<double, double> head = null;
                    if (end.Pointed && smooth)
                    {
                        int rise = Math.Min(Q(ww * 12), ceilHere - 16 - top);
                        if (rise >= 16)
                        {
                            var d = side.OutDir();
                            var xs = cells.Select(c => d.Y != 0 ? c.X : c.Y).ToList();
                            double centre = (xs.Min() + xs.Max() + 1) / 2.0;
                            double half = ww / 2.0;
                            int bas = room.Floor + top;
                            head = A => bas + rise * Math.Max(0.0, 1 - Math.Abs(A - centre) / half);
                        }
                    }
                    int depth = end.Depth;
                    if (CarveRecess(level, room, ctx, cells, side.OutDir(), depth, end.Z0, top, head, nearDoor, "end feature")
                        || (depth > 1 && CarveRecess(level, room, ctx, cells, side.OutDir(), 1, end.Z0, top, head, nearDoor, "end feature")))
                    {
                        Inc("end");
                        counts["axial"] = axial ? 1 : 0;
                        ctx.Used.UnionWith(cells);                       // its opening stays clear of members
                        break;                                           // one per hall
                    }
                }
                if (focus != null && !(counts.TryGetValue("axial", out var ax) && (int)ax != 0))
                {
                    // the way in looks straight at another doorway: a through-view, framed by its portal
                    var (far, aMid) = focus.Value;
                    foreach (var op in Portals.Openings(level, room))
                    {
                        var uvs = op.Cells.Select(c => fr.UV(c)).ToList();
                        bool onFar = uvs.All(t => far == "v0" ? t.V == 0 : far == "v1" ? t.V == fr.W - 1 :
                                                  far == "u0" ? t.U == 0 : t.U == fr.L - 1);
                        if (onFar)
                        {
                            var pos = uvs.Select(t => far == "v0" || far == "v1" ? t.U : t.V).ToList();
                            if (pos.Min() - 2 <= aMid && aMid <= pos.Max() + 3)
                            {
                                counts["through"] = 1;
                                break;
                            }
                        }
                    }
                }
            }

            // --- inward: members on the bay lines of the long walls (unless the section already has them)
            bool OnLongWall(List<(int, int)> g) => g.All(t => t.Item2 == 0 || t.Item2 == fr.W - 1);
            bool hasMembers = sec != null && sec.Piers.Any(OnLongWall);
            int memberW = (style == "bunker" || style == "castle") && fr.W >= 9 ? 2 : 1;   // counterforts, buttresses
            var placed = new List<List<(int, int)>>();
            if (hasMembers)                                 // the section's own wall piers get dressed too
                placed.AddRange(sec.Placed.Where(OnLongWall));
            var lines = BayLines(fr.L, bay, memberW);
            if (!hasMembers && fr.W >= 5 && fr.L >= 6)
            {
                var groups = new List<List<(int, int)>>();
                foreach (int u in lines)
                    foreach (int v in new[] { 0, fr.W - 1 })
                        groups.Add(Enumerable.Range(0, memberW).Select(i => (u + i, v)).ToList());
                var tmp = new Section("members", fr) { Piers = groups };
                counts["members"] = PlacePiers(level, room, tmp, ctx, "members");
                placed.AddRange(tmp.Placed);
            }

            // --- outward: recesses between the members, on the long walls
            var spec = style switch
            {
                "gothic" => new Spec { Width = bay >= 5 ? 2 : 1, Z0 = 0, Frac = 0.5, Every = 1, Depth = 1, Pointed = true },
                "industrial" => new Spec { Width = bay >= 6 ? 3 : 2, Z0 = 0, Frac = null, Top = 128, Every = 2, Depth = 1 },
                "bunker" => new Spec { Width = 2, Z0 = 0, Frac = null, Top = 96, Every = 1, Depth = 1 },
                "compound" => new Spec { Width = bay >= 6 ? 3 : 2, Z0 = 32, Frac = 0.55, Every = 1, Depth = 1 },
                // false doors; panel bays with a console sill; deep window embrasures
                "egyptian" => new Spec { Width = 1, Z0 = 0, Frac = 0.5, Every = 1, Depth = 1 },
                "scifi" => new Spec { Width = 2, Z0 = 16, Frac = null, Top = 112, Every = 1, Depth = 1 },
                _ => new Spec { Width = 1, Z0 = 0, Frac = null, Top = 128, Every = 1, Depth = 2 },      // castle
            };
            if (tier == "minor" && (style == "gothic" || style == "compound"))
                spec.Every = 2;                                      // quieter in small rooms
            var mids = new List<int>();
            for (int i = 0; i + 1 < lines.Count; i++) mids.Add((lines[i] + lines[i + 1] + memberW) / 2);
            if (mids.Count == 0 && fr.L >= 8) mids.Add(fr.L / 2);    // a short wall without bays: one in the middle
            foreach (var sideName in new[] { "v0", "v1" })
            {
                var side = new Side(fr, sideName);
                for (int i = 0; i < mids.Count; i++)
                {
                    if (i % spec.Every != 0) continue;
                    int w = spec.Width;
                    int a0 = mids[i] - w / 2;
                    if (a0 < 1 || a0 + w > side.Length - 1) continue;
                    var cells = Enumerable.Range(a0, w).Select(a => { var t = side.Inner(a); return fr.Cell(t.Item1, t.Item2); }).ToList();
                    int ceilHere = Enumerable.Range(a0, w).Min(a => LocalCeiling(sec, fr, side.Inner(a)));
                    int top = spec.Frac != null ? Q(H * spec.Frac.Value) : spec.Top;
                    top = Math.Min(top, ceilHere - 16);
                    if (top - spec.Z0 < 80) continue;
                    Func<double, double> head = null;
                    if (spec.Pointed && w == 2 && smooth)            // a lancet: two sloped halves meeting in a point
                    {
                        int rise = Math.Min(48, ceilHere - 16 - top);
                        if (rise >= 16)
                        {
                            var d = side.OutDir();
                            int centre = cells.Min(c => d.Y != 0 ? c.X : c.Y) + 1;   // the edge between the two cells
                            int bas = room.Floor + top;
                            head = A => bas + rise * Math.Max(0.0, 1 - Math.Abs(A - centre));
                        }
                    }
                    if (CarveRecess(level, room, ctx, cells, side.OutDir(), spec.Depth, spec.Z0, top, head, nearDoor))
                        Inc("recesses");
                }
            }

            // --- the end walls get the rhythm too (a 35 m wide hall has 35 m end walls)
            if (fr.W >= 9 && !hasMembers)
            {
                int ebay = BaySize(H, fr.W, k, lo, hi);
                var groups = new List<List<(int, int)>>();
                foreach (int v in BayLines(fr.W, ebay, memberW))
                    foreach (int u in new[] { 0, fr.L - 1 })
                        groups.Add(Enumerable.Range(0, memberW).Select(i => (u, v + i)).ToList());
                var tmp = new Section("members", fr) { Piers = groups };
                Inc("members", PlacePiers(level, room, tmp, ctx, "members"));
                placed.AddRange(tmp.Placed);
            }

            // --- bases and capitals on the members
            if (placed.Count > 0 && cfg.ArchDress)
            {
                var dressed = DressMembers(level, room, fr, sec, placed, style, nearDoor, rng);
                if (dressed.Count > 0) counts["dress"] = dressed;
            }

            // --- above: crane corbels along the long walls of a tall industrial hall
            if (style == "industrial" && sec != null && H >= 352 && (tier == "grand" || tier == "major"))
            {
                int rail = Q(H * 0.62);
                for (int u = 0; u < fr.L; u++)
                    foreach (int v in new[] { 0, fr.W - 1 })
                        if (sec.Ceil.TryGetValue((u, v), out int cz) && cz > rail)
                        {
                            sec.Ceil[(u, v)] = rail;
                            sec.Forced.Add((u, v));
                        }
                counts["corbel"] = rail;
            }
            return counts;
        }

        /// <summary>style -> [(base, capital, weight)]: one order per room. base: null | "front" | "pedestal"
        /// (front and both sides) | "block" (the front cell, 48 high: walked round); capital: null |
        /// (cells, drop) with cells "front" | "flared" | "stepped" | "splay" | "splay-flared" | "corbel".</summary>
        public static readonly Dictionary<string, List<(string Base, (string Cells, int Drop)? Cap, double W)>> Orders =
            new Dictionary<string, List<(string, (string, int)?, double)>>
            {
                ["gothic"] = new List<(string, (string, int)?, double)> { ("pedestal", ("front", 48), 2), ("front", ("flared", 32), 1), ("pedestal", null, 1),
                    ("pedestal", ("stepped", 64), 1), ("pedestal", ("splay", 48), 2), ("block", ("splay-flared", 48), 1) },
                ["castle"] = new List<(string, (string, int)?, double)> { ("front", ("front", 64), 2), ("pedestal", ("front", 48), 1), (null, ("flared", 32), 1),
                    ("front", ("stepped", 64), 1), ("block", ("corbel", 48), 2), ("front", ("splay", 64), 1) },
                ["egyptian"] = new List<(string, (string, int)?, double)> { ("pedestal", ("flared", 32), 2), ("pedestal", ("front", 64), 1), ("pedestal", ("stepped", 64), 1),
                    ("block", ("splay", 64), 2) },
                ["compound"] = new List<(string, (string, int)?, double)> { ("front", ("front", 32), 2), (null, ("front", 48), 1), ("pedestal", null, 1),
                    ("front", ("splay", 32), 1) },
                ["industrial"] = new List<(string, (string, int)?, double)> { ("front", null, 2), (null, ("front", 32), 1), ("front", ("front", 48), 1),
                    ("block", ("corbel", 48), 1) },
                ["bunker"] = new List<(string, (string, int)?, double)> { ("pedestal", null, 2), ("front", ("flared", 32), 1), ("block", null, 1) },
                ["scifi"] = new List<(string, (string, int)?, double)> { ("front", ("front", 32), 2), ("pedestal", ("flared", 32), 1), (null, ("front", 48), 1),
                    ("front", ("splay-flared", 32), 1) },
            };
        public const int BaseRise = 16;      // one step: walkable
        public const int BlockRise = 48;     // a block base: too tall to step on, walked round

        static (int, int)? Inward(Frame fr, List<(int, int)> group)
        {
            if (group.All(t => t.Item2 == 0)) return (0, 1);
            if (group.All(t => t.Item2 == fr.W - 1)) return (0, -1);
            if (group.All(t => t.Item1 == 0)) return (1, 0);
            if (group.All(t => t.Item1 == fr.L - 1)) return (-1, 0);
            return null;
        }

        /// <summary>A base and a capital on every wall member of the room, the same order for all of them.</summary>
        public static Dictionary<string, object> DressMembers(Level level, Room room, Frame fr, Section sec, List<List<(int, int)>> groups,
                                                              string style, HashSet<Cell> nearDoor, Rng rng)
        {
            var empty = new Dictionary<string, object>();
            if (!Orders.TryGetValue(style, out var opts)) return empty;
            var (bas, cap, _) = rng.Choices(opts, opts.Select(o => o.W).ToList());
            if (bas == null && (cap == null || sec == null)) return empty;
            int bases = 0, caps = 0;
            bool InFrame((int, int) t) => 0 <= t.Item1 && t.Item1 < fr.L && 0 <= t.Item2 && t.Item2 < fr.W;
            foreach (var g in groups)
            {
                var dd = Inward(fr, g);
                if (dd == null) continue;
                var d = dd.Value;
                var front = g.Select(t => (t.Item1 + d.Item1, t.Item2 + d.Item2)).ToList();
                var along = d.Item2 != 0 ? (1, 0) : (0, 1);
                var lo = g.OrderBy(t => t.Item1 * along.Item1 + t.Item2 * along.Item2).First();
                var hi = g.OrderByDescending(t => t.Item1 * along.Item1 + t.Item2 * along.Item2).First();
                var sides = new List<(int, int)> { (lo.Item1 - along.Item1, lo.Item2 - along.Item2), (hi.Item1 + along.Item1, hi.Item2 + along.Item2) };
                // a base: the plain floor in front (and beside) the member, one step up
                if (bas != null)
                {
                    var bcells = new List<(int, int)>(front);
                    if (bas == "pedestal") bcells.AddRange(sides);
                    var world = bcells.Where(InFrame).Select(t => fr.Cell(t.Item1, t.Item2)).ToList();
                    int rise = bas == "block" ? BlockRise : BaseRise;
                    var clear = new List<Cell>();
                    if (bas == "block")            // a path two cells wide stays in front of the block
                    {
                        int deep = d.Item2 != 0 ? fr.W : fr.L;
                        foreach (var t in g)
                            foreach (int kk in new[] { 2, 3 })
                                clear.Add(fr.Cell(t.Item1 + d.Item1 * kk, t.Item2 + d.Item2 * kk));
                        if (deep < 8 || !clear.All(c => InFrame(fr.UV(c)))) world.Clear();
                    }
                    if (world.Count > 0 && world.Concat(clear).All(c => room.Contains(c) && !room.IsDiag(c.X, c.Y) && !nearDoor.Contains(c)
                                                                        && IsPlain(level, room, c)))
                    {
                        foreach (var c in world)
                        {
                            level.ReplaceIn(c, room.Slot.Lo, room.Slot.Hi, new[] { new Span(room.Floor + rise, room.Ceil) });
                            level.PartCells[c] = $"room:{room.Id}/bases";
                        }
                        if (bas == "block" && !BaseConnected(level, room))
                        {
                            foreach (var c in world)
                            {
                                level.ReplaceIn(c, room.Slot.Lo, room.Slot.Hi, new[] { new Span(room.Floor, room.Ceil) });
                                level.PartCells.Remove(c);
                            }
                        }
                        else bases++;
                    }
                }
                // a capital: the ceiling in front (and beside) the member, dropped under the member's top
                if (cap != null && sec != null)
                {
                    var (which, drop) = cap.Value;
                    var ccells = front.Select(uv => (uv, drop)).ToList();
                    if (which == "flared" || which == "stepped" || which == "splay-flared")
                        ccells.AddRange(sides.Select(uv => (uv, which != "stepped" ? drop : drop / 2)));
                    bool slope = which == "splay" || which == "splay-flared";
                    if (which == "corbel")          // the second cell out slopes back up: an angled underside
                        ccells.AddRange(front.Select(t => ((t.Item1 + d.Item1, t.Item2 + d.Item2), drop)));
                    ccells = ccells.Where(t => InFrame(t.Item1)).ToList();
                    var lows = new List<((int, int) Uv, int Low)>();
                    foreach (var (uv, dr) in ccells)
                    {
                        int here = sec.Ceil.TryGetValue(uv, out int hz) ? hz : fr.H;
                        int low = here - dr;
                        if (low < Math.Max(Head, level.Config.ArchHeadroom) + 32)
                        {
                            lows.Clear();
                            break;
                        }
                        lows.Add((uv, low));
                    }
                    var w0 = fr.Cell(0, 0);
                    var w1 = fr.Cell(-d.Item1, -d.Item2);           // the world direction towards the wall
                    var toWall = (w1.X - w0.X, w1.Y - w0.Y);
                    var outer = which == "corbel" ? new HashSet<(int, int)>(front.Select(t => (t.Item1 + d.Item1, t.Item2 + d.Item2)))
                                                  : new HashSet<(int, int)>();
                    foreach (var (uv, low) in lows)
                    {
                        sec.Ceil[uv] = low;
                        sec.Forced.Add(uv);
                        level.PartCeiling[fr.Cell(uv.Item1, uv.Item2)] = $"room:{room.Id}/capitals";
                        if (slope || outer.Contains(uv))           // sloped after ApplyCeilings, low at the wall side
                            sec.Splays.Add((fr.Cell(uv.Item1, uv.Item2), toWall, low, low + drop));
                    }
                    if (lows.Count > 0) caps++;
                }
            }
            if (bases == 0 && caps == 0) return empty;
            return new Dictionary<string, object>
            {
                ["base"] = bas, ["capital"] = cap != null ? $"{cap.Value.Cells} {cap.Value.Drop}" : null,
                ["bases"] = bases, ["capitals"] = caps,
            };
        }

        /// <summary>After ApplyCeilings: the sloped capitals. A cell's ceiling stays at its low point; the
        /// slope rises from the wall side to the room's ceiling, as a sloped brush.</summary>
        public static void ApplySplays(Level level, Room room, Section sec)
        {
            var splays = sec.Splays.ToList();
            sec.Splays.Clear();
            if (!level.Config.ArchSmooth) return;              // a strict grid: the capitals stay flat blocks
            foreach (var (c, toWall, low, high) in splays)
            {
                var rs = level.RoomSpans(c, room);
                if (rs.Count == 0 || level.CeilSlope.ContainsKey(c) || level.Diag.ContainsKey(c)) continue;
                var last = rs[rs.Count - 1];
                int f = last.Floor, top = last.Ceil;
                var all = level.Spans[c];
                if (top != room.Floor + low || !all[all.Count - 1].Equals(last)) continue;   // a feature or a storey changed it
                int hiZ = room.Floor + high;
                var zs = new int[4];
                for (int i = 0; i < 4; i++)
                {
                    var (dx, dy) = Quad[i];
                    bool nearWall = (toWall.X == 1 && dx == 1) || (toWall.X == -1 && dx == 0) ||
                                    (toWall.Y == 1 && dy == 1) || (toWall.Y == -1 && dy == 0);
                    zs[i] = nearWall ? top : hiZ;
                }
                level.CeilSlope[c] = (zs[0], zs[1], zs[2], zs[3]);
            }
        }

        /// <summary>After the room's features (and again after the portals): a base that a feature built
        /// over is no longer a base, and a capital whose cell was filled is no longer a capital.</summary>
        public static void TidyDress(Level level, Room room)
        {
            string tag = $"room:{room.Id}/bases";
            foreach (var c in level.PartCells.Where(kv => kv.Value == tag).Select(kv => kv.Key).ToList())
            {
                var rs = level.RoomSpans(c, room);
                if (rs.Count == 0 || (rs[0].Floor - room.Floor != BaseRise && rs[0].Floor - room.Floor != BlockRise))
                    level.PartCells.Remove(c);
            }
            // a base that a later feature walled in is a pocket: you could drop onto it and never climb
            // out. Such a base (and the bases it joins) is filled: the member deepens
            var bases = new HashSet<Cell>(level.PartCells.Where(kv => kv.Value == tag).Select(kv => kv.Key));
            int step = level.Config.MaxStep;
            var seen = new HashSet<Cell>();
            foreach (var c0 in bases.OrderBy(c => c))
            {
                if (seen.Contains(c0)) continue;
                var group = new HashSet<Cell> { c0 };
                var stack = new Stack<Cell>();
                stack.Push(c0);
                while (stack.Count > 0)
                {
                    var a = stack.Pop();
                    foreach (var (dx, dy) in Decorate.D4)
                    {
                        var n = a.Offset(dx, dy);
                        if (bases.Contains(n) && group.Add(n)) stack.Push(n);
                    }
                }
                seen.UnionWith(group);
                bool way = false;
                foreach (var a in group)
                {
                    int fa = level.RoomSpans(a, room)[0].Floor;
                    foreach (var (dx, dy) in Decorate.D4)
                    {
                        var n = a.Offset(dx, dy);
                        if (group.Contains(n) || level.Diag.ContainsKey(n)) continue;
                        if (level.RoomSpans(n, room).Any(sp => sp.Floor <= fa + step && sp.Ceil - Math.Max(sp.Floor, fa) >= level.Config.PlayerHeight))
                            way = true;                         // a way off it: level, a step up, or down
                    }
                }
                if (!way)
                    foreach (var a in group)
                    {
                        level.ReplaceIn(a, room.Slot.Lo, room.Slot.Hi, new Span[0]);
                        level.PartCells.Remove(a);
                        level.PartSolid[a] = $"room:{room.Id}/members";
                    }
            }
            tag = $"room:{room.Id}/capitals";
            foreach (var c in level.PartCeiling.Where(kv => kv.Value == tag).Select(kv => kv.Key).ToList())
                if (level.RoomSpans(c, room).Count == 0)          // a feature filled the cell
                {
                    level.PartCeiling.Remove(c);
                    level.CeilSlope.Remove(c);
                }
        }

        /// <summary>The wall facing the way in, and where along it the way in points: (side, position).
        /// The way in is the corridor from the room nearer the start (lower mission-graph depth).</summary>
        public static (string Side, double Pos)? IncomingAxis(Level level, Room room, Frame fr, RoomCtx ctx)
        {
            var g = level.Graph;
            int depth = g.Nodes[room.Node].Depth;
            Portals.Opening best = null;
            var ops = Portals.Openings(level, room);
            if (ops.Count == 1) best = ops[0];                 // the only way in is the way in
            if (best == null)
                foreach (var op in ops)
                {
                    var corr = op.Corridor < level.Corridors.Count ? level.Corridors[op.Corridor] : null;
                    if (corr == null) continue;
                    var other = level.Rooms[corr.B == room.Id ? corr.A : corr.B];
                    int od = g.Nodes[other.Node].Depth;
                    if (od < depth && (best == null || op.Cells.Count > best.Cells.Count)) best = op;
                }
            if (best == null) return null;
            var uvs = best.Cells.Select(c => fr.UV(c)).ToList();
            var us = uvs.Select(t => t.U).ToList();
            var vs = uvs.Select(t => t.V).ToList();
            if (vs.All(v => v == 0)) return ("v1", us.Sum() / (double)us.Count + 0.5);
            if (vs.All(v => v == fr.W - 1)) return ("v0", us.Sum() / (double)us.Count + 0.5);
            if (us.All(u => u == 0)) return ("u1", vs.Sum() / (double)vs.Count + 0.5);
            if (us.All(u => u == fr.L - 1)) return ("u0", vs.Sum() / (double)vs.Count + 0.5);
            return null;
        }

        /// <summary>The longest straight run (cells) of wall where nothing changes: open room cell on one
        /// side, solid rock on the other. Members, recesses, doors and windows all break a run.</summary>
        public static int FlatWallRuns(Level level, Room room)
        {
            var runs = new Dictionary<((int, int), int), List<int>>();
            foreach (var c in room.Cells())
            {
                if (room.IsDiag(c.X, c.Y) || level.RoomSpans(c, room).Count == 0) continue;
                foreach (var d in Decorate.D4)
                {
                    var n = c.Offset(d.Item1, d.Item2);
                    if (room.Contains(n) || (level.Spans.TryGetValue(n, out var s) && s.Count > 0) || level.Diag.ContainsKey(n) || level.Glass.Contains(n))
                        continue;
                    var line = (d, d.Item1 != 0 ? n.X : n.Y);
                    if (!runs.TryGetValue(line, out var l)) runs[line] = l = new List<int>();
                    l.Add(d.Item1 != 0 ? c.Y : c.X);
                }
            }
            int best = 0;
            foreach (var alongs in runs.Values)
            {
                alongs.Sort();
                int cur = 1;
                for (int i = 0; i + 1 < alongs.Count; i++)
                {
                    cur = alongs[i + 1] == alongs[i] + 1 ? cur + 1 : 1;
                    best = Math.Max(best, cur);
                }
                best = Math.Max(best, alongs.Count > 0 ? 1 : 0);
            }
            return best;
        }
    }
}
