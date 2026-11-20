using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>Clearance (clearance.py): the player is a box, not a point.
    ///
    /// The validator walks cell to cell (32 units), but a Quake player is a 32x32 box, and a gap exactly
    /// 32 units wide doesn't let it through. Here the player is a 2x2-cell box (64 units, the smallest
    /// grid gap a 32-unit hull fits through). <see cref="BoxReach"/> floods where the box can stand and
    /// move (steps, drops, lifts and ladders as in the validator; doors count as open).
    /// <see cref="Widen"/> runs after generation: wherever the point can go on but the box can't, it opens
    /// a solid cell beside the squeeze, but only a cell whose open neighbours all belong to the spaces
    /// already meeting there, so it never cuts into another room, around a locked door or out of the
    /// level. What can't be widened is reported by <see cref="Report"/> and fails validation.</summary>
    public static class Clearance
    {
        static readonly (int, int)[] D4 = { (1, 0), (-1, 0), (0, 1), (0, -1) };
        public static readonly int Slab = 16;
        public static readonly string[] ArenaGoals = { "spawn", "weapon", "quad", "armor_heavy", "megahealth", "flag" };

        /// <summary>Entities the box must reach: keys and the exit; in an arena, spawns, guns and power-ups.</summary>
        static string[] Goals(Level level) => level.Config.Mode == "arena" ? ArenaGoals : new[] { "key", "exit" };

        static Cell[] Quad(int x, int y) => new[] { new Cell(x - 1, y - 1), new Cell(x, y - 1), new Cell(x - 1, y), new Cell(x, y) };

        static Span? SpanAt(Level level, Cell c, int F, int H)
        {
            if (level.Diag.ContainsKey(c) || level.Glass.Contains(c)) return null;
            if (level.Spans.TryGetValue(c, out var spans))
                foreach (var sp in spans) if (sp.Floor <= F && sp.Ceil >= F + H) return sp;
            return null;
        }

        static List<int> BoxesAt(Level level, int x, int y, int H)
        {
            var q = Quad(x, y);
            var fs = new SortedSet<int>();
            foreach (var c in q) if (level.Spans.TryGetValue(c, out var spans)) foreach (var sp in spans) fs.Add(sp.Floor);
            return fs.Where(F => q.All(c => SpanAt(level, c, F, H) != null)).ToList();
        }

        static Cell CellOfPos(Level level, Vec3 p) => new Cell((int)Math.Floor(p.X / level.CellSize), (int)Math.Floor(p.Y / level.CellSize));

        /// <summary>Every box position (x, y, F: the box over cells x-1..x, y-1..y standing at F) reachable
        /// from the player start (or <paramref name="startPos"/>).</summary>
        public static HashSet<(int X, int Y, int F)> BoxReach(Level level, Vec3? startPos = null)
        {
            var cfg = level.Config;
            int H = cfg.PlayerHeight, step = cfg.MaxStep, drop = cfg.MaxDrop, s = level.CellSize;
            var corners = new HashSet<Cell>();
            foreach (var c in level.Spans.Keys) for (int dx = 0; dx <= 1; dx++) for (int dy = 0; dy <= 1; dy++) corners.Add(c.Offset(dx, dy));
            var by = new Dictionary<Cell, List<int>>();
            foreach (var c in corners)
            {
                var fs = BoxesAt(level, c.X, c.Y, H);
                if (fs.Count > 0) by[c] = fs;
            }
            var seen = new HashSet<(int, int, int)>();
            if (startPos == null)
            {
                var startE = level.Entities.FirstOrDefault(e => e.Kind == "player_start");
                if (startE == null) return seen;
                startPos = startE.Pos;
            }
            var sp0 = startPos.Value;
            (int, int, int)? best = null;
            for (int r = 0; r < 3 && best == null; r++)
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                    {
                        var c = new Cell((int)Math.Round(sp0.X / s, MidpointRounding.ToEven) + dx, (int)Math.Round(sp0.Y / s, MidpointRounding.ToEven) + dy);
                        if (by.TryGetValue(c, out var fs))
                            foreach (var F in fs)
                                if (F <= sp0.Z && sp0.Z <= F + H + 32 && best == null) best = (c.X, c.Y, F);
                    }
            if (best == null) return seen;
            var links = new Dictionary<Cell, List<Vec3>>();
            foreach (var L in level.Links)
            {
                var ca = CellOfPos(level, L.A);
                if (!links.TryGetValue(ca, out var la)) links[ca] = la = new List<Vec3>();
                la.Add(L.B);
                if (L.TwoWay)
                {
                    var cb = CellOfPos(level, L.B);
                    if (!links.TryGetValue(cb, out var lb)) links[cb] = lb = new List<Vec3>();
                    lb.Add(L.A);
                }
            }
            seen.Add(best.Value);
            var stack = new Stack<(int, int, int)>();
            stack.Push(best.Value);
            while (stack.Count > 0)
            {
                var (x, y, F) = stack.Pop();
                foreach (var (dx, dy) in D4)
                    if (by.TryGetValue(new Cell(x + dx, y + dy), out var gs))
                        foreach (var G in gs)
                        {
                            if (G - F > step || F - G > drop) continue;
                            var m = (x + dx, y + dy, G);
                            if (seen.Add(m)) stack.Push(m);
                        }
                foreach (var c in Quad(x, y))
                    if (links.TryGetValue(c, out var ps))
                        foreach (var p in ps)
                        {
                            var pc = CellOfPos(level, p);
                            foreach (var cx in new[] { pc.X, pc.X + 1 })
                                foreach (var cy in new[] { pc.Y, pc.Y + 1 })
                                    if (by.TryGetValue(new Cell(cx, cy), out var gs))
                                        foreach (var G in gs)
                                            if (Math.Abs(G - p.Z) <= 80)
                                            {
                                                var m = (cx, cy, G);
                                                if (seen.Add(m)) stack.Push(m);
                                            }
                        }
            }
            return seen;
        }

        /// <summary>The (cell, span index) nodes some reachable box stands in.</summary>
        public static HashSet<(int, int, int)> Covered(Level level, HashSet<(int X, int Y, int F)> seen)
        {
            int H = level.Config.PlayerHeight;
            var outp = new HashSet<(int, int, int)>();
            foreach (var (x, y, F) in seen)
                foreach (var c in Quad(x, y))
                    if (level.Spans.TryGetValue(c, out var spans))
                        for (int i = 0; i < spans.Count; i++)
                            if (spans[i].Floor <= F && spans[i].Ceil >= F + H) outp.Add((c.X, c.Y, i));
            return outp;
        }

        static (HashSet<(int, int, int)> Reach, Dictionary<(int, int, int), List<(int, int, int)>> Links) PointReach(Level level)
        {
            var start = level.Entities.First(e => e.Kind == "player_start").Pos;
            var links = Validator.LinkEdges(level);
            var node = Validator.NodeAt(level, start) ?? throw new LayoutException($"position {start} is not inside open space");
            return (Validator.Flood(level, node, new HashSet<Cell>(), links), links);
        }

        static HashSet<Cell> DoorCells(Level level)
        {
            var outp = new HashSet<Cell>();
            foreach (var e in level.Entities)
                if ((e.Kind == "door" || e.Kind == "secret_door") && e.Bounds != null) outp.UnionWith(Validator.DoorCells(level, e));
            return outp;
        }

        /// <summary>Point moves from where the box stands into where it can't, leading somewhere that
        /// matters (more than a nook of a few cells).</summary>
        static List<((int, int, int) N, (int, int, int) M)> StuckEdges(Level level)
        {
            var cov = Covered(level, BoxReach(level));
            var (pr, links) = PointReach(level);
            var unc = new HashSet<(int, int, int)>(pr.Where(n => !cov.Contains(n)));
            var comp = new Dictionary<(int, int, int), int>();
            var sizes = new List<int>();
            foreach (var n in unc.OrderBy(t => t))
            {
                if (comp.ContainsKey(n)) continue;
                int k = sizes.Count, size = 0;
                comp[n] = k;
                var stack = new Stack<(int, int, int)>();
                stack.Push(n);
                while (stack.Count > 0)
                {
                    var a = stack.Pop();
                    size++;
                    foreach (var m in Validator.Successors(level, a, new HashSet<Cell>(), links))
                        if (unc.Contains(m) && !comp.ContainsKey(m)) { comp[m] = k; stack.Push(m); }
                }
                sizes.Add(size);
            }
            // a pocket matters if it is more than a nook of a few cells, or if a goal is in it
            var goalKinds = Goals(level);
            var goals = new HashSet<int>();
            foreach (var e in level.Entities.Where(e => goalKinds.Contains(e.Kind)))
            {
                var c = CellOfPos(level, e.Pos);
                foreach (var n in unc) if (n.Item1 == c.X && n.Item2 == c.Y) goals.Add(comp[n]);
            }
            var edges = new List<((int, int, int), (int, int, int))>();
            foreach (var n in cov.Where(pr.Contains).OrderBy(t => t))
                foreach (var m in Validator.Successors(level, n, new HashSet<Cell>(), links))
                    if (unc.Contains(m) && (sizes[comp[m]] > 3 || goals.Contains(comp[m]))) edges.Add((n, m));
            return edges;
        }

        static HashSet<string> OwnersNear(Level level, Cell c, int lo, int hi)
        {
            var outp = new HashSet<string>();
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var n = c.Offset(dx, dy);
                    foreach (var sp in level.SpansIn(n, lo, hi)) outp.Add(level.OwnerOf(n, sp) ?? "");
                    if (level.Glass.Contains(n) && level.SpansIn(n, lo, hi).Any()) outp.Add("glass");
                }
            return outp;
        }

        static bool TryOpen(Level level, Cell b, int F, int ceil, string owner, HashSet<(string, string)> allowed, HashSet<Cell> doors)
        {
            int H = level.Config.PlayerHeight;
            if (level.Spans.ContainsKey(b) && SpanAt(level, b, F, H) != null) return false;
            if (level.Glass.Contains(b) || level.Diag.ContainsKey(b) || level.Sky.Contains(b)) return false;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (doors.Contains(b.Offset(dx, dy))) return false;    // never beside a door: it could go round it
            if (!(1 <= b.X && b.X < level.Width - 1 && 1 <= b.Y && b.Y < level.Height - 1)) return false;
            if (level.Spans.TryGetValue(b, out var spans) && spans.Any(sp => sp.Floor < ceil + Slab && F - Slab < sp.Ceil))
                return false;                                              // something open at this height already (a step)
            var near = OwnersNear(level, b, F, F + H);
            if (near.Contains("glass")) return false;
            near.Remove("");
            foreach (var o1 in near)                                       // never join spaces that aren't joined already
                foreach (var o2 in near)
                    if (o1 != o2 && !allowed.Contains((o1, o2))) return false;
            var yard = level.Rooms.FirstOrDefault(r => r.Contains(b) && r.Kind == "courtyard");
            if (yard != null && ceil != yard.Ceil) return false;           // in a yard only at full height, open to the sky
            var inside = new HashSet<string>(level.Rooms.Where(r => r.Contains(b)).Select(r => "room:" + r.Id));
            var pick = near.Where(inside.Contains).ToList();
            if (pick.Count == 0) pick = near.Where(o => !o.StartsWith("room:")).ToList();
            if (pick.Count == 0) pick.Add(owner);
            owner = pick.OrderBy(x => x, StringComparer.Ordinal).First();  // a room's cell only inside its footprint
            level.AddSpan(b, new Span(F, ceil), owner);
            level.PartSolid.Remove(b);
            if (yard != null) level.Sky.Add(b);
            return true;
        }

        /// <summary>Pairs of spaces the player can already walk between, not through a door.</summary>
        static HashSet<(string, string)> Joined(Level level, HashSet<Cell> doors)
        {
            var outp = new HashSet<(string, string)>();
            foreach (var kv in level.Spans)
            {
                if (doors.Contains(kv.Key)) continue;
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    var a = level.OwnerOf(kv.Key, kv.Value[i]);
                    foreach (var m in Validator.Successors(level, (kv.Key.X, kv.Key.Y, i), doors))
                    {
                        var mc = new Cell(m.Item1, m.Item2);
                        var b = level.OwnerOf(mc, level.Spans[mc][m.Item3]);
                        if (!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && a != b) { outp.Add((a, b)); outp.Add((b, a)); }
                    }
                }
            }
            return outp;
        }

        /// <summary>widen_pinches: open solid cells beside squeezes the player box can't pass. Returns cells opened.</summary>
        public static int Widen(Level level, int maxRounds = 40)
        {
            int H = level.Config.PlayerHeight, opened = 0;
            var doors = DoorCells(level);
            for (int round = 0; round < maxRounds; round++)
            {
                var edges = StuckEdges(level);
                if (edges.Count == 0) break;
                bool progress = false;
                var done = new HashSet<Cell>();
                var joined = Joined(level, doors);
                foreach (var (n, m) in edges)
                {
                    var mc = new Cell(m.Item1, m.Item2);
                    var nc = new Cell(n.Item1, n.Item2);
                    if (done.Contains(mc)) continue;
                    var na = level.Spans[nc][n.Item3];
                    var ma = level.Spans[mc][m.Item3];
                    int F = Math.Max(na.Floor, ma.Floor);
                    string owner = level.OwnerOf(mc, ma) ?? level.OwnerOf(nc, na);
                    // the boxes that would hold both cells of the move: try to clear each, fewest cells first
                    var pair = new HashSet<Cell> { nc, mc };
                    var cornersL = new List<Cell>();
                    foreach (var x in new[] { mc.X, mc.X + 1 }) foreach (var y in new[] { mc.Y, mc.Y + 1 }) cornersL.Add(new Cell(x, y));
                    foreach (var x in new[] { nc.X, nc.X + 1 })
                        foreach (var y in new[] { nc.Y, nc.Y + 1 })
                            if (!cornersL.Contains(new Cell(x, y))) cornersL.Add(new Cell(x, y));
                    var cands = new List<(int Both, int Count, Cell Corner, List<Cell> Block, int Ceil)>();
                    foreach (var corner in cornersL)
                    {
                        var q = Quad(corner.X, corner.Y);
                        var block = q.Where(c => SpanAt(level, c, F, H) == null).ToList();
                        var openSp = q.Select(c => SpanAt(level, c, F, H)).Where(sp => sp != null).Select(sp => sp.Value).ToList();
                        if (openSp.Count == 0 || block.Count == 0 || block.Count > 2) continue;
                        bool both = pair.IsSubsetOf(q);                  // a box holding both cells of the move first
                        cands.Add((both ? 0 : 1, block.Count, corner, block, openSp.Min(sp => sp.Ceil)));
                    }
                    foreach (var cand in cands.OrderBy(t => t.Both).ThenBy(t => t.Count).ThenBy(t => t.Corner))
                    {
                        if (!cand.Block.All(c => !level.Spans.ContainsKey(c) || !level.SpansIn(c, F, cand.Ceil).Any())) continue;
                        var trial = new List<(Cell, string)>();
                        foreach (var c in cand.Block)
                        {
                            level.PartSolid.TryGetValue(c, out var part);
                            if (!TryOpen(level, c, F, cand.Ceil, owner, joined, doors)) break;
                            trial.Add((c, part));
                        }
                        if (trial.Count == cand.Block.Count)
                        {
                            opened += trial.Count;
                            progress = true;
                            done.Add(mc);
                            break;
                        }
                        foreach (var (c, part) in trial)                 // undo a half-cleared box
                        {
                            level.ReplaceIn(c, F, cand.Ceil, Array.Empty<Span>());
                            level.Sky.Remove(c);
                            if (level.Owners.TryGetValue(c, out var os))
                            {
                                os.RemoveAll(o => o.Lo == F && o.Hi == cand.Ceil);
                                if (os.Count == 0) level.Owners.Remove(c);
                            }
                            if (part != null) level.PartSolid[c] = part;
                        }
                    }
                }
                if (!progress) break;
            }
            if (opened > 0) level.Notes.Add($"clearance: opened {opened} cells");
            return opened;
        }

        /// <summary>clearance_report: rooms and goals the player box can't reach (empty when passable).</summary>
        public static List<string> Report(Level level)
        {
            var cells = new HashSet<Cell>(Covered(level, BoxReach(level)).Select(t => new Cell(t.Item1, t.Item2)));
            var errors = new List<string>();
            foreach (var r in level.Rooms)
                if (!r.Cells().Any(cells.Contains)) errors.Add($"clearance: room {r.Id} ({r.Kind}) too narrow to reach for the player");
            var goalKinds = Goals(level);
            foreach (var e in level.Entities.Where(e => goalKinds.Contains(e.Kind)))
            {
                var c = CellOfPos(level, e.Pos);
                if (!cells.Contains(c)) errors.Add($"clearance: {e.Kind} at ({c.X}, {c.Y}) too narrow to reach for the player");
            }
            return errors;
        }
    }
}
