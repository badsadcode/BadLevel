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
        public int OneWayDrops;
        /// <summary>The level's links (kind, two_way, interaction, accessible, room).</summary>
        public List<Dictionary<string, object>> Links = new List<Dictionary<string, object>>();
    }

    /// <summary>Level validation (validate.py): is the generated level actually completable?
    ///
    /// Movement is directed: a player can step up at most MaxStep units, but can drop down up to
    /// MaxDrop (a one-way move). Lifts, ladders and jump pads are explicit links (level.Links).
    /// Every move also needs headroom.
    ///
    /// Checks: bounds (nothing open touches the grid border), spans (headroom, no overlaps),
    /// entities (none inside solid or a 45-degree half cell), gating (keys in lock order; the exit
    /// stays out of reach until every locked door is open), softlock (from every reachable spot the
    /// next goal is still reachable), coverage (every room reached), clearance (by a player-sized
    /// box too, see <see cref="Clearance"/>). Arena levels: no goal, but no trap either (one circuit).</summary>
    public static class Validator
    {
        public static readonly (int, int)[] Dirs = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        public static HashSet<Cell> DoorCells(Level level, Entity e)
        {
            var (lo, hi) = e.Bounds.Value;
            int s = level.CellSize;
            var cells = new HashSet<Cell>();
            for (int x = (int)Math.Floor(lo.X / s); x < (int)Math.Floor(hi.X / s); x++)
                for (int y = (int)Math.Floor(lo.Y / s); y < (int)Math.Floor(hi.Y / s); y++) cells.Add(new Cell(x, y));
            return cells;
        }

        public static Cell CellOf(Level level, Vec3 pos) => level.CellOf(pos);

        /// <summary>The (x, y, span index) node holding a position, or null where Python raises
        /// (no column there, or the position is not inside open space).</summary>
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
                var a = NodeAt(level, link.A);
                var b = NodeAt(level, link.B);
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

        public static bool CanMove(Level level, Span s0, Span s1)
        {
            var cfg = level.Config;
            if (s1.Floor - s0.Floor > cfg.MaxStep) return false;   // too high to step up
            if (s0.Floor - s1.Floor > cfg.MaxDrop) return false;   // too far to fall
            return Math.Min(s0.Ceil, s1.Ceil) - Math.Max(s0.Floor, s1.Floor) >= cfg.PlayerHeight;
        }

        public static IEnumerable<(int X, int Y, int I)> Successors(Level level, (int X, int Y, int I) node, HashSet<Cell> blocked,
                                                                   Dictionary<(int, int, int), List<(int, int, int)>> links = null)
        {
            var here = level.Spans[new Cell(node.X, node.Y)][node.I];
            foreach (var (dx, dy) in Dirs)
            {
                var n = new Cell(node.X + dx, node.Y + dy);
                if (blocked.Contains(n) || !level.Spans.TryGetValue(n, out var ns) || level.Diag.ContainsKey(n) || level.Glass.Contains(n)) continue;
                for (int j = 0; j < ns.Count; j++)
                    if (CanMove(level, here, ns[j])) yield return (n.X, n.Y, j);
            }
            if (links != null && links.TryGetValue(node, out var ls))
                foreach (var m in ls)
                    if (!blocked.Contains(new Cell(m.Item1, m.Item2))) yield return m;
        }

        /// <summary>Every node reachable from start (directed: drops are one-way).</summary>
        public static HashSet<(int, int, int)> Flood(Level level, (int, int, int) start, HashSet<Cell> blocked,
                                                    Dictionary<(int, int, int), List<(int, int, int)>> links = null)
        {
            links ??= LinkEdges(level);
            var seen = new HashSet<(int, int, int)> { start };
            var stack = new Stack<(int, int, int)>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                foreach (var m in Successors(level, n, blocked, links))
                    if (seen.Add(m)) stack.Push(m);
            }
            return seen;
        }

        /// <summary>Every node from which some target can be reached (a reverse search).</summary>
        public static HashSet<(int, int, int)> CanReach(Level level, HashSet<(int, int, int)> targets, HashSet<Cell> blocked,
                                                       Dictionary<(int, int, int), List<(int, int, int)>> links = null)
        {
            links ??= LinkEdges(level);
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
                        if (!seen.Contains(m) && CanMove(level, ps[j], here)) { seen.Add(m); stack.Push(m); }
                    }
                }
                if (rev.TryGetValue(node, out var rs))
                    foreach (var p in rs)
                        if (!seen.Contains(p) && !blocked.Contains(new Cell(p.Item1, p.Item2))) { seen.Add(p); stack.Push(p); }
            }
            return seen;
        }

        public static List<int> RoomsUnreached(Level level, HashSet<(int, int, int)> reach)
        {
            var outp = new List<int>();
            foreach (var room in level.Rooms)
            {
                string tag = "room:" + room.Id;
                bool hit = room.Cells().Any(c => level.Spans.TryGetValue(c, out var sps)
                    && Enumerable.Range(0, sps.Count).Any(i => reach.Contains((c.X, c.Y, i)) && level.OwnerOf(c, sps[i]) == tag));
                if (!hit) outp.Add(room.Id);
            }
            return outp;
        }

        static readonly string[] ArenaGoalKinds = { "spawn", "weapon", "quad", "armor_heavy", "megahealth", "flag" };

        static void Finish(Level level, ValidationReport rep, HashSet<(int, int, int)> reach)
        {
            int open = level.Spans.Where(kv => !level.Diag.ContainsKey(kv.Key)).Sum(kv => kv.Value.Count);
            rep.ReachableFraction = Math.Round(reach.Count / (double)Math.Max(1, open), 3);
            rep.OneWayDrops = level.Corridors.Count(c => c.Connector == "drop");
            rep.Links = level.Links.Select(l => new Dictionary<string, object>
            {
                ["kind"] = l.Kind, ["two_way"] = l.TwoWay, ["interaction"] = l.Interaction, ["accessible"] = l.Accessible, ["room"] = l.Room,
            }).ToList();
        }

        /// <summary>Deathmatch: no goal, but no trap either. Everything reachable from the first spawn must
        /// be able to get back to it (one circuit); every room and every spawn, weapon and power-up must be on it.</summary>
        static ValidationReport ValidateArena(Level level, (int, int, int) start, Dictionary<(int, int, int), List<(int, int, int)>> links,
                                              Dictionary<string, List<Entity>> byKind, ValidationReport rep)
        {
            var errors = rep.Errors;
            var reach = Flood(level, start, new HashSet<Cell>(), links);
            var back = CanReach(level, new HashSet<(int, int, int)> { start }, new HashSet<Cell>(), links);
            var stuck = reach.Where(n => !back.Contains(n)).ToList();
            if (stuck.Count > 0)
            {
                var s = stuck.OrderBy(t => t).First();
                errors.Add($"softlock: {stuck.Count} reachable spot(s) can't get back (e.g. {s.Item1},{s.Item2})");
            }
            var spawns = byKind.TryGetValue("spawn", out var sl) ? sl : new List<Entity>();
            if (spawns.Count < 2) errors.Add($"arena needs spawns, got {spawns.Count}");
            foreach (var kind in ArenaGoalKinds)
                if (byKind.TryGetValue(kind, out var es))
                    foreach (var e in es)
                    {
                        var n = NodeAt(level, e.Pos);
                        if (n == null || !reach.Contains(n.Value)) errors.Add($"{kind} at {e.Pos} unreachable");
                    }
            var unreached = RoomsUnreached(level, reach);
            if (unreached.Count > 0) errors.Add($"rooms unreachable: [{string.Join(", ", unreached)}]");
            if (level.Water.Count > 0) errors.AddRange(Floods.BreathReport(level));   // every swim on one breath
            if (errors.Count == 0 && level.Config.CheckClearance) errors.AddRange(Clearance.Report(level));
            rep.SoftlockSpots = stuck.Count;
            Finish(level, rep, reach);
            return rep;
        }

        public static ValidationReport Run(Level level)
        {
            var rep = new ValidationReport();
            var errors = rep.Errors;
            var cfg = level.Config;
            foreach (var c in level.Spans.Keys)
                if (c.X <= 0 || c.Y <= 0 || c.X >= level.Width - 1 || c.Y >= level.Height - 1)
                {
                    errors.Add($"open cell {c.X},{c.Y} touches the border");
                    break;
                }
            foreach (var kv in level.Spans)
            {
                foreach (var sp in kv.Value)
                    if (sp.Height < cfg.PlayerHeight && !(level.OwnerAt(kv.Key, sp.Floor) ?? "").StartsWith("window:"))
                        errors.Add($"span at {kv.Key} too low ({sp.Height})");
                for (int i = 0; i + 1 < kv.Value.Count; i++)
                    if (kv.Value[i + 1].Floor <= kv.Value[i].Ceil) errors.Add($"overlapping spans at {kv.Key}");
            }

            var byKind = new Dictionary<string, List<Entity>>();
            foreach (var e in level.Entities)
            {
                if (!byKind.TryGetValue(e.Kind, out var l)) byKind[e.Kind] = l = new List<Entity>();
                l.Add(e);
            }
            var starts = byKind.TryGetValue("player_start", out var st) ? st : new List<Entity>();
            if (starts.Count != 1)
            {
                errors.Add($"expected 1 player_start, got {starts.Count}");
                return rep;
            }
            foreach (var e in level.Entities)
                if (e.Bounds == null && (NodeAt(level, e.Pos) == null || level.Diag.ContainsKey(level.CellOf(e.Pos))))
                    errors.Add($"{e.Kind} at {e.Pos} is inside solid");

            var links = LinkEdges(level);
            if (links.Count < level.Links.Count)
                errors.Add("a lift or ladder link does not touch open space");
            var startNode = NodeAt(level, starts[0].Pos);
            if (startNode == null) return rep;                     // Python raises here; the error is already reported
            var start = startNode.Value;
            var doorCells = new Dictionary<string, HashSet<Cell>>();
            foreach (var d in byKind.TryGetValue("door", out var dl) ? dl : new List<Entity>())
                doorCells[(string)d.Props["key"]] = DoorCells(level, d);
            var keys = new Dictionary<string, (int, int, int)?>();
            foreach (var k in byKind.TryGetValue("key", out var kl) ? kl : new List<Entity>())
                keys[(string)k.Props["color"]] = NodeAt(level, k.Pos);
            if (cfg.Mode == "arena") return ValidateArena(level, start, links, byKind, rep);
            var exits = byKind.TryGetValue("exit", out var xl) ? xl : new List<Entity>();
            if (exits.Count == 0)
            {
                errors.Add("no exit");
                return rep;
            }
            var exitNode = NodeAt(level, exits[0].Pos);

            // progression: keys in lock order, then the exit; at every stage nothing reachable may be a dead end
            var lockedOrder = level.Graph.Edges.Where(e => e.Kind == "locked").Select(e => e.Key).ToList();
            var have = new HashSet<string>();
            var stages = lockedOrder.Select(k => (Name: k, Goal: keys.TryGetValue(k, out var g) ? g : null)).ToList();
            stages.Add(("exit", exitNode));
            int softlocks = 0;
            var reach = new HashSet<(int, int, int)>();
            foreach (var (name, goal) in stages)
            {
                var blocked = new HashSet<Cell>();
                if (doorCells.Count > have.Count)
                    foreach (var kv in doorCells) if (!have.Contains(kv.Key)) blocked.UnionWith(kv.Value);
                reach = Flood(level, start, blocked, links);
                if (goal == null)
                {
                    errors.Add($"missing {name} key");
                    break;
                }
                if (!reach.Contains(goal.Value))
                {
                    errors.Add($"{name} unreachable" + (name == "exit" ? "" : " before its door"));
                    break;
                }
                if (name != "exit" && exitNode != null && reach.Contains(exitNode.Value))
                    errors.Add($"exit reachable without the {name} key");
                var back = CanReach(level, new HashSet<(int, int, int)> { goal.Value }, blocked, links);
                var stuck = reach.Where(n => !back.Contains(n)).ToList();
                if (stuck.Count > 0)
                {
                    softlocks += stuck.Count;
                    var s = stuck.OrderBy(t => t).First();
                    errors.Add($"softlock: {stuck.Count} reachable spot(s) can't get to the {name} (e.g. {s.Item1},{s.Item2})");
                }
                if (name != "exit") have.Add(name);
            }

            var unreached = RoomsUnreached(level, reach);
            if (unreached.Count > 0) errors.Add($"rooms unreachable: [{string.Join(", ", unreached)}]");
            if (level.Water.Count > 0) errors.AddRange(Floods.BreathReport(level));   // every swim on one breath
            if (errors.Count == 0 && cfg.CheckClearance) errors.AddRange(Clearance.Report(level));   // the player is a box, not a point
            rep.SoftlockSpots = softlocks;
            Finish(level, rep, reach);
            return rep;
        }
    }
}
