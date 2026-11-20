using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    public sealed class ValidationReport
    {
        public bool Ok => Errors.Count == 0;
        public List<string> Errors = new List<string>();
        public double ReachableFraction;
        public int SoftlockSpots;
    }

    /// <summary>Is the level completable? Movement is directed: a player steps up at most MaxStep
    /// and drops at most MaxDrop (one way); lifts and ladders are links; every move needs headroom.
    /// Checks: the border is sealed, spans are tall enough, entities are in open space, keys come in
    /// lock order and the exit stays shut until then, no reachable spot is a dead end (softlock),
    /// every room is reached, and a player-sized box fits (Clearance).</summary>
    public static class Validator
    {
        static readonly (int, int)[] Dirs = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        public static (int X, int Y, int I)? NodeAt(Level level, Vec3 pos)
        {
            var c = level.CellOf(pos);
            if (!level.Spans.TryGetValue(c, out var spans)) return null;
            for (int i = 0; i < spans.Count; i++)
                if (spans[i].Floor <= pos.Z && pos.Z < spans[i].Ceil) return (c.X, c.Y, i);
            return null;
        }

        public static Dictionary<(int, int, int), List<(int, int, int)>> LinkEdges(Level level)
        {
            var outp = new Dictionary<(int, int, int), List<(int, int, int)>>();
            foreach (var link in level.Links)
            {
                var a = NodeAt(level, link.A); var b = NodeAt(level, link.B);
                if (a == null || b == null) continue;
                Add(outp, a.Value, b.Value);
                if (link.TwoWay) Add(outp, b.Value, a.Value);
            }
            return outp;
        }

        static void Add(Dictionary<(int, int, int), List<(int, int, int)>> d, (int, int, int) a, (int, int, int) b)
        {
            if (!d.TryGetValue(a, out var l)) d[a] = l = new List<(int, int, int)>();
            l.Add(b);
        }

        static bool CanMove(GenConfig cfg, Span s0, Span s1)
        {
            if (s1.Floor - s0.Floor > cfg.MaxStep) return false;
            if (s0.Floor - s1.Floor > cfg.MaxDrop) return false;
            return Math.Min(s0.Ceil, s1.Ceil) - Math.Max(s0.Floor, s1.Floor) >= cfg.PlayerHeight;
        }

        public static IEnumerable<(int X, int Y, int I)> Successors(Level level, (int X, int Y, int I) node, HashSet<Cell> blocked,
                                                                   Dictionary<(int, int, int), List<(int, int, int)>> links)
        {
            var here = level.Spans[new Cell(node.X, node.Y)][node.I];
            foreach (var (dx, dy) in Dirs)
            {
                var n = new Cell(node.X + dx, node.Y + dy);
                if (blocked.Contains(n) || !level.Spans.TryGetValue(n, out var ns) || level.Diag.ContainsKey(n) || level.Glass.Contains(n)) continue;
                for (int j = 0; j < ns.Count; j++)
                    if (CanMove(level.Config, here, ns[j])) yield return (n.X, n.Y, j);
            }
            if (links != null && links.TryGetValue(node, out var ls))
                foreach (var m in ls)
                    if (!blocked.Contains(new Cell(m.Item1, m.Item2))) yield return m;
        }

        public static HashSet<(int, int, int)> Flood(Level level, (int, int, int) start, HashSet<Cell> blocked,
                                                    Dictionary<(int, int, int), List<(int, int, int)>> links)
        {
            var seen = new HashSet<(int, int, int)> { start };
            var stack = new Stack<(int, int, int)>(); stack.Push(start);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                foreach (var m in Successors(level, n, blocked, links))
                    if (seen.Add(m)) stack.Push(m);
            }
            return seen;
        }

        /// <summary>Every node from which a target can be reached (a reverse search).</summary>
        static HashSet<(int, int, int)> CanReach(Level level, HashSet<(int, int, int)> targets, HashSet<Cell> blocked,
                                                 Dictionary<(int, int, int), List<(int, int, int)>> links)
        {
            var rev = new Dictionary<(int, int, int), List<(int, int, int)>>();
            foreach (var kv in links) foreach (var b in kv.Value) Add(rev, b, kv.Key);
            var seen = new HashSet<(int, int, int)>(targets);
            var stack = new Stack<(int, int, int)>(targets);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                var here = level.Spans[new Cell(node.Item1, node.Item2)][node.Item3];
                foreach (var (dx, dy) in Dirs)
                {
                    var p = new Cell(node.Item1 + dx, node.Item2 + dy);
                    if (blocked.Contains(p) || !level.Spans.TryGetValue(p, out var ps) || level.Diag.ContainsKey(p) || level.Glass.Contains(p)) continue;
                    for (int j = 0; j < ps.Count; j++)
                    {
                        var m = (p.X, p.Y, j);
                        if (!seen.Contains(m) && CanMove(level.Config, ps[j], here)) { seen.Add(m); stack.Push(m); }
                    }
                }
                if (rev.TryGetValue(node, out var rs))
                    foreach (var p in rs)
                        if (!blocked.Contains(new Cell(p.Item1, p.Item2)) && seen.Add(p)) stack.Push(p);
            }
            return seen;
        }

        static HashSet<Cell> DoorCells(Level level, Entity e)
        {
            var (lo, hi) = e.Bounds.Value; int s = level.CellSize;
            var cells = new HashSet<Cell>();
            for (int x = (int)(lo.X / s); x < (int)(hi.X / s); x++)
                for (int y = (int)(lo.Y / s); y < (int)(hi.Y / s); y++) cells.Add(new Cell(x, y));
            return cells;
        }

        public static ValidationReport Run(Level level)
        {
            var rep = new ValidationReport();
            var cfg = level.Config;
            foreach (var c in level.Spans.Keys)
                if (c.X <= 0 || c.Y <= 0 || c.X >= level.Width - 1 || c.Y >= level.Height - 1) { rep.Errors.Add($"open cell {c} touches the border"); break; }
            foreach (var kv in level.Spans)
            {
                foreach (var sp in kv.Value)
                    if (sp.Height < cfg.PlayerHeight && !(level.OwnerAt(kv.Key, sp.Floor) ?? "").StartsWith("window:"))
                        rep.Errors.Add($"span at {kv.Key} too low ({sp.Height})");
                for (int i = 0; i + 1 < kv.Value.Count; i++)
                    if (kv.Value[i + 1].Floor <= kv.Value[i].Ceil) rep.Errors.Add($"overlapping spans at {kv.Key}");
            }
            var starts = level.Entities.Where(e => e.Kind == "player_start").ToList();
            if (starts.Count != 1) { rep.Errors.Add($"expected 1 player_start, got {starts.Count}"); return rep; }
            foreach (var e in level.Entities.Where(e => e.Bounds == null))
            {
                var n = NodeAt(level, e.Pos);
                if (n == null || level.Diag.ContainsKey(level.CellOf(e.Pos))) rep.Errors.Add($"{e.Kind} at {e.Pos} is inside solid");
            }
            var links = LinkEdges(level);
            var start = NodeAt(level, starts[0].Pos).Value;
            var doorCells = level.Entities.Where(e => e.Kind == "door").ToDictionary(e => (string)e.Props["key"], e => DoorCells(level, e));
            var keys = level.Entities.Where(e => e.Kind == "key").ToDictionary(e => (string)e.Props["color"], e => NodeAt(level, e.Pos));
            var exits = level.Entities.Where(e => e.Kind == "exit").ToList();
            if (exits.Count == 0) { rep.Errors.Add("no exit"); return rep; }
            var exitNode = NodeAt(level, exits[0].Pos);
            var lockedOrder = level.Graph.Edges.Where(e => e.Kind == "locked").Select(e => e.Key).ToList();
            var have = new HashSet<string>();
            var stages = lockedOrder.Select(k => (Name: k, Goal: keys.TryGetValue(k, out var g) ? g : null)).ToList();
            stages.Add(("exit", exitNode));
            HashSet<(int, int, int)> reach = null;
            foreach (var (name, goal) in stages)
            {
                var blocked = new HashSet<Cell>();
                foreach (var kv in doorCells) if (!have.Contains(kv.Key)) blocked.UnionWith(kv.Value);
                reach = Flood(level, start, blocked, links);
                if (goal == null) { rep.Errors.Add($"missing {name} key"); break; }
                if (!reach.Contains(goal.Value)) { rep.Errors.Add($"{name} unreachable" + (name == "exit" ? "" : " before its door")); break; }
                if (name != "exit" && exitNode != null && reach.Contains(exitNode.Value)) rep.Errors.Add($"exit reachable without the {name} key");
                var back = CanReach(level, new HashSet<(int, int, int)> { goal.Value }, blocked, links);
                var stuck = reach.Where(n => !back.Contains(n)).ToList();
                if (stuck.Count > 0)
                {
                    rep.SoftlockSpots += stuck.Count;
                    var s = stuck.OrderBy(t => t).First();
                    rep.Errors.Add($"softlock: {stuck.Count} reachable spot(s) can't get to the {name} (e.g. {s.Item1},{s.Item2})");
                }
                if (name != "exit") have.Add(name);
            }
            if (reach != null)
            {
                var unreached = level.Rooms.Where(room => !room.Cells().Any(c =>
                    level.Spans.TryGetValue(c, out var sps) && Enumerable.Range(0, sps.Count).Any(i => reach.Contains((c.X, c.Y, i)) && level.OwnerOf(c, sps[i]) == "room:" + room.Id)))
                    .Select(r => r.Id).ToList();
                if (unreached.Count > 0) rep.Errors.Add("rooms unreachable: " + string.Join(",", unreached));
                int open = level.Spans.Where(kv => !level.Diag.ContainsKey(kv.Key)).Sum(kv => kv.Value.Count);
                rep.ReachableFraction = Math.Round(reach.Count / (double)Math.Max(1, open), 3);
            }
            if (rep.Ok && cfg.CheckClearance) rep.Errors.AddRange(Clearance.Report(level));
            return rep;
        }
    }

    /// <summary>The player is a box, not a point: a 2x2-cell box (64 units, the smallest grid gap a
    /// 32-unit hull fits through). <see cref="Widen"/> opens solid cells beside squeezes the box can't
    /// pass (never joining spaces that weren't joined, never beside a door); what can't be widened
    /// fails validation.</summary>
    public static class Clearance
    {
        static readonly (int, int)[] D4 = { (1, 0), (-1, 0), (0, 1), (0, -1) };
        const int Slab = 16;

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

        public static HashSet<(int X, int Y, int F)> BoxReach(Level level)
        {
            var cfg = level.Config;
            int H = cfg.PlayerHeight, step = cfg.MaxStep, drop = cfg.MaxDrop, s = level.CellSize;
            var corners = new HashSet<Cell>();
            foreach (var c in level.Spans.Keys) for (int dx = 0; dx <= 1; dx++) for (int dy = 0; dy <= 1; dy++) corners.Add(c.Offset(dx, dy));
            var by = new Dictionary<Cell, List<int>>();
            foreach (var c in corners) { var fs = BoxesAt(level, c.X, c.Y, H); if (fs.Count > 0) by[c] = fs; }
            var startE = level.Entities.FirstOrDefault(e => e.Kind == "player_start");
            var seen = new HashSet<(int, int, int)>();
            if (startE == null) return seen;
            var sp0 = startE.Pos;
            (int, int, int)? best = null;
            for (int r = 0; r < 3 && best == null; r++)
                for (int dx = -r; dx <= r && best == null; dx++)
                    for (int dy = -r; dy <= r && best == null; dy++)
                    {
                        var c = new Cell((int)Math.Round(sp0.X / s) + dx, (int)Math.Round(sp0.Y / s) + dy);
                        if (by.TryGetValue(c, out var fs))
                            foreach (var F in fs) if (F <= sp0.Z && sp0.Z <= F + H + 32) { best = (c.X, c.Y, F); break; }
                    }
            if (best == null) return seen;
            var links = new Dictionary<Cell, List<Vec3>>();
            foreach (var L in level.Links)
            {
                var ca = level.CellOf(L.A);
                if (!links.TryGetValue(ca, out var la)) links[ca] = la = new List<Vec3>();
                la.Add(L.B);
                if (L.TwoWay)
                {
                    var cb = level.CellOf(L.B);
                    if (!links.TryGetValue(cb, out var lb)) links[cb] = lb = new List<Vec3>();
                    lb.Add(L.A);
                }
            }
            seen.Add(best.Value);
            var stack = new Stack<(int, int, int)>(); stack.Push(best.Value);
            while (stack.Count > 0)
            {
                var (x, y, F) = stack.Pop();
                foreach (var (dx, dy) in D4)
                    if (by.TryGetValue(new Cell(x + dx, y + dy), out var gs))
                        foreach (var G in gs)
                        {
                            if (G - F > step || F - G > drop) continue;
                            // dropping: the space it drops into must have room for the box at the height it leaves from
                            if (F > G && !Quad(x + dx, y + dy).All(c => SpanAt(level, c, G, F - G + H) != null)) continue;
                            var m = (x + dx, y + dy, G);
                            if (seen.Add(m)) stack.Push(m);
                        }
                foreach (var c in Quad(x, y))
                    if (links.TryGetValue(c, out var ps))
                        foreach (var p in ps)
                        {
                            var pc = level.CellOf(p);
                            foreach (var cx in new[] { pc.X, pc.X + 1 })
                                foreach (var cy in new[] { pc.Y, pc.Y + 1 })
                                    if (by.TryGetValue(new Cell(cx, cy), out var gs))
                                        foreach (var G in gs)
                                            if (Math.Abs(G - p.Z) <= 80) { var m = (cx, cy, G); if (seen.Add(m)) stack.Push(m); }
                        }
            }
            return seen;
        }

        static HashSet<(int, int, int)> Covered(Level level, HashSet<(int X, int Y, int F)> seen)
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

        public static List<string> Report(Level level)
        {
            var cells = new HashSet<Cell>(Covered(level, BoxReach(level)).Select(t => new Cell(t.Item1, t.Item2)));
            var errors = new List<string>();
            foreach (var r in level.Rooms)
                if (!r.Cells().Any(cells.Contains)) errors.Add($"clearance: room {r.Id} ({r.Kind}) too narrow to reach for the player");
            foreach (var e in level.Entities.Where(e => e.Kind == "key" || e.Kind == "exit"))
                if (!cells.Contains(level.CellOf(e.Pos))) errors.Add($"clearance: {e.Kind} at {level.CellOf(e.Pos)} too narrow to reach");
            return errors;
        }

        static HashSet<Cell> DoorCells(Level level)
        {
            int s = level.CellSize;
            var outp = new HashSet<Cell>();
            foreach (var e in level.Entities.Where(e => (e.Kind == "door" || e.Kind == "secret_door") && e.Bounds != null))
            {
                var (lo, hi) = e.Bounds.Value;
                for (int x = (int)(lo.X / s); x < (int)(hi.X / s); x++)
                    for (int y = (int)(lo.Y / s); y < (int)(hi.Y / s); y++) outp.Add(new Cell(x, y));
            }
            return outp;
        }

        /// <summary>Point moves from where the box stands into where it can't, leading somewhere that matters.</summary>
        static List<((int, int, int) N, (int, int, int) M)> StuckEdges(Level level)
        {
            var cov = Covered(level, BoxReach(level));
            var start = level.Entities.First(e => e.Kind == "player_start");
            var links = Validator.LinkEdges(level);
            var pr = Validator.Flood(level, Validator.NodeAt(level, start.Pos).Value, new HashSet<Cell>(), links);
            var unc = new HashSet<(int, int, int)>(pr.Where(n => !cov.Contains(n)));
            var comp = new Dictionary<(int, int, int), int>(); var sizes = new List<int>();
            foreach (var n in unc.OrderBy(t => t))
            {
                if (comp.ContainsKey(n)) continue;
                int k = sizes.Count, size = 0;
                comp[n] = k;
                var stack = new Stack<(int, int, int)>(); stack.Push(n);
                while (stack.Count > 0)
                {
                    var a = stack.Pop(); size++;
                    foreach (var m in Validator.Successors(level, a, new HashSet<Cell>(), links))
                        if (unc.Contains(m) && !comp.ContainsKey(m)) { comp[m] = k; stack.Push(m); }
                }
                sizes.Add(size);
            }
            var goals = new HashSet<int>();
            foreach (var e in level.Entities.Where(e => e.Kind == "key" || e.Kind == "exit"))
            {
                var c = level.CellOf(e.Pos);
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
            outp.Remove("");
            return outp;
        }

        static HashSet<(string, string)> Joined(Level level, HashSet<Cell> doors)
        {
            var outp = new HashSet<(string, string)>();
            foreach (var kv in level.Spans)
            {
                if (doors.Contains(kv.Key)) continue;
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    var a = level.OwnerOf(kv.Key, kv.Value[i]);
                    foreach (var m in Validator.Successors(level, (kv.Key.X, kv.Key.Y, i), doors, null))
                    {
                        var mc = new Cell(m.Item1, m.Item2);
                        var b = level.OwnerOf(mc, level.Spans[mc][m.Item3]);
                        if (a != null && b != null && a != b) { outp.Add((a, b)); outp.Add((b, a)); }
                    }
                }
            }
            return outp;
        }

        static bool TryOpen(Level level, Cell b, int F, int ceil, string owner, HashSet<(string, string)> allowed, HashSet<Cell> doors)
        {
            int H = level.Config.PlayerHeight;
            if (level.Spans.ContainsKey(b) && SpanAt(level, b, F, H) != null) return false;
            if (level.Glass.Contains(b) || level.Diag.ContainsKey(b) || level.Sky.Contains(b)) return false;
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) if (doors.Contains(b.Offset(dx, dy))) return false;
            if (!(1 <= b.X && b.X < level.Width - 1 && 1 <= b.Y && b.Y < level.Height - 1)) return false;
            if (level.Spans.TryGetValue(b, out var spans) && spans.Any(sp => sp.Floor < ceil + Slab && F - Slab < sp.Ceil)) return false;
            var near = OwnersNear(level, b, F, F + H);
            if (near.Contains("glass")) return false;
            foreach (var o1 in near) foreach (var o2 in near) if (o1 != o2 && !allowed.Contains((o1, o2))) return false;
            var inside = new HashSet<string>(level.Rooms.Where(r => r.Contains(b)).Select(r => "room:" + r.Id));
            var pick = near.Where(inside.Contains).ToList();
            if (pick.Count == 0) pick = near.Where(o => !o.StartsWith("room:")).ToList();
            if (pick.Count == 0) pick.Add(owner);
            level.AddSpan(b, new Span(F, ceil), pick.OrderBy(x => x, StringComparer.Ordinal).First());
            level.PartSolid.Remove(b);
            return true;
        }

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
                    var mc = new Cell(m.Item1, m.Item2); var nc = new Cell(n.Item1, n.Item2);
                    if (done.Contains(mc)) continue;
                    var na = level.Spans[nc][n.Item3]; var ma = level.Spans[mc][m.Item3];
                    int F = Math.Max(na.Floor, ma.Floor);
                    string owner = level.OwnerOf(mc, ma) ?? level.OwnerOf(nc, na);
                    var pair = new HashSet<Cell> { nc, mc };
                    var cornersL = new List<Cell>();
                    foreach (var x in new[] { mc.X, mc.X + 1 }) foreach (var y in new[] { mc.Y, mc.Y + 1 }) cornersL.Add(new Cell(x, y));
                    foreach (var x in new[] { nc.X, nc.X + 1 }) foreach (var y in new[] { nc.Y, nc.Y + 1 }) if (!cornersL.Contains(new Cell(x, y))) cornersL.Add(new Cell(x, y));
                    var cands = new List<(int Both, int Count, Cell Corner, List<Cell> Block, int Ceil)>();
                    foreach (var corner in cornersL)
                    {
                        var q = Quad(corner.X, corner.Y);
                        var block = q.Where(c => SpanAt(level, c, F, H) == null).ToList();
                        var openSp = q.Select(c => SpanAt(level, c, F, H)).Where(s => s != null).Select(s => s.Value).ToList();
                        if (openSp.Count == 0 || block.Count == 0 || block.Count > 2) continue;
                        cands.Add((pair.IsSubsetOf(q) ? 0 : 1, block.Count, corner, block, openSp.Min(s => s.Ceil)));
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
                        if (trial.Count == cand.Block.Count) { opened += trial.Count; progress = true; done.Add(mc); break; }
                        foreach (var (c, part) in trial)
                        {
                            level.ReplaceIn(c, F, cand.Ceil, Array.Empty<Span>());
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
    }
}
