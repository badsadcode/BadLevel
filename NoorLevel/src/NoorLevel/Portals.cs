using System;
using System.Collections.Generic;
using System.Linq;
using static NoorLevel.Architecture;

// portals.py: how a corridor opens into a room, framed so the arrival means something.
//
// Where a corridor meets a room of a styled level:
//   jambs   the room cells at both ends of the opening become solid members, so the opening reads
//           as a doorway in a wall rather than a gap (bunkers: two deep, a blast frame)
//   head    in grand and major rooms the mouth (the corridor's last cell before the room) rises
//           above the corridor into the wall: a pointed arch (gothic), a round arch (compound), a
//           tall opening under a heavy lintel (industrial)
// This runs after every room and corridor is populated (decorate's last step): a mouth that holds a
// door is never raised, and a jamb never lands on an entity or a raised feature. Raising only cuts
// into solid rock with one solid cell kept around it. The validator still checks the result.
namespace NoorLevel
{
    public static partial class Portals
    {
        static readonly (int, int)[] Dirs = { (1, 0), (-1, 0), (0, 1), (0, -1) };
        static readonly (int, int)[] Quad = { (0, 0), (1, 0), (1, 1), (0, 1) };

        /// <summary>A run of room cells along one wall where a corridor meets the room at its floor.</summary>
        public sealed class Opening
        {
            public List<Cell> Cells;
            public (int X, int Y) Out;
            public int Corridor;
            public Span Span;
        }

        /// <summary>Where corridors meet the room at its floor: runs of room cells along one wall.</summary>
        public static List<Opening> Openings(Level level, Room room)
        {
            var keys = new List<((int, int), int)>();
            var groups = new Dictionary<((int, int), int), List<Cell>>();
            var spansOf = new Dictionary<((int, int), int), Span>();
            foreach (var c in room.Cells())
            {
                if (room.IsDiag(c.X, c.Y)) continue;
                foreach (var d in Dirs)
                {
                    var n = c.Offset(d.Item1, d.Item2);
                    if (room.Contains(n)) continue;
                    foreach (var sp in level.SpansIn(n, room.Floor, room.Ceil))
                    {
                        string owner = level.OwnerOf(n, sp) ?? "";
                        if (owner.StartsWith("corridor:") && sp.Floor == room.Floor)
                        {
                            var key = (d, int.Parse(owner.Substring(9)));
                            if (!groups.TryGetValue(key, out var l)) { groups[key] = l = new List<Cell>(); keys.Add(key); }
                            l.Add(c);
                            spansOf[key] = sp;
                        }
                    }
                }
            }
            var o = new List<Opening>();
            foreach (var key in keys)
            {
                var (d, cid) = key;
                Func<Cell, int> along = d.Item1 != 0 ? (c => c.Y) : (c => c.X);
                Func<Cell, int> line = d.Item1 != 0 ? (c => c.X) : (c => c.Y);
                var cells = groups[key].Distinct().OrderBy(line).ThenBy(along).ToList();
                var run = new List<Cell> { cells[0] };
                foreach (var c in cells.Skip(1))
                {
                    var last = run[run.Count - 1];
                    if (line(c) == line(last) && along(c) == along(last) + 1) run.Add(c);
                    else
                    {
                        o.Add(new Opening { Cells = run, Out = d, Corridor = cid, Span = spansOf[key] });
                        run = new List<Cell> { c };
                    }
                }
                o.Add(new Opening { Cells = run, Out = d, Corridor = cid, Span = spansOf[key] });
            }
            return o;
        }

        static void BoundsCells(Level level, (Vec3 Lo, Vec3 Hi) b, HashSet<Cell> into)
        {
            int s = level.CellSize;
            int x0 = (int)Math.Floor(b.Lo.X / s), x1 = (int)Math.Floor((b.Hi.X - 1) / s);
            int y0 = (int)Math.Floor(b.Lo.Y / s), y1 = (int)Math.Floor((b.Hi.Y - 1) / s);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++) into.Add(new Cell(x, y));
        }

        public static HashSet<Cell> EntityCells(Level level)
        {
            int s = level.CellSize;
            var cells = new HashSet<Cell>();
            foreach (var e in level.Entities)
            {
                cells.Add(new Cell((int)Math.Floor(e.Pos.X / s), (int)Math.Floor(e.Pos.Y / s)));
                if (e.Bounds != null) BoundsCells(level, e.Bounds.Value, cells);
            }
            return cells;
        }

        public static HashSet<Cell> DoorCells(Level level)
        {
            var cells = new HashSet<Cell>();
            foreach (var e in level.Entities)
                if ((e.Kind == "door" || e.Kind == "secret_door") && e.Bounds != null)
                    BoundsCells(level, e.Bounds.Value, cells);
            return cells;
        }

        /// <summary>style -> (shape, rise per cell of width, least rise, most rise)</summary>
        public static readonly Dictionary<string, (string Shape, int Per, int Least, int Most)> Heads =
            new Dictionary<string, (string, int, int, int)>
            {
                ["gothic"] = ("pointed", 28, 48, 192),
                ["compound"] = ("round", 16, 32, 128),
                ["industrial"] = ("flat", 0, 64, 112),
                ["egyptian"] = ("flat", 0, 48, 96),          // a massive lintel over a tall doorway
                ["scifi"] = ("chamfer", 16, 32, 64),         // a door frame with its corners cut
                ["castle"] = ("round", 16, 32, 128),
            };

        /// <summary>Frame every corridor opening of every eligible room. Returns the number of portals.</summary>
        public static int Build(Level level, Dictionary<int, string> tiers)
        {
            var cfg = level.Config;
            string style = cfg.ArchStyle ?? "";
            if (style == "" || !cfg.ArchPortals) return 0;
            bool smooth = cfg.ArchSmooth;
            var taken = EntityCells(level);
            var doors = DoorCells(level);
            int made = 0;
            foreach (var room in level.Rooms)
            {
                if (!tiers.TryGetValue(room.Id, out var tier)) continue;
                var rng = Rng.FromLabel($"{cfg.Seed}/{cfg.Variant}/portal/{room.Id}");
                var vents = new HashSet<int>(level.Corridors.Where(c => c.Vent).Select(c => c.Id));
                var opens = Openings(level, room).Where(o => !vents.Contains(o.Corridor)).ToList();   // a duct gets no frame
                var mouthCells = new HashSet<Cell>(opens.SelectMany(o => o.Cells));
                foreach (var op in opens)
                {
                    int jambs = Jambs(level, room, op, style, taken, mouthCells);
                    int head = 0;
                    if ((tier == "grand" || tier == "major") && Heads.ContainsKey(style) && op.Cells.Count >= 2)
                        head = Head_(level, room, op, style, doors, smooth, rng);
                    if (jambs != 0 || head != 0)
                    {
                        made++;
                        if (!(room.Meta.TryGetValue("portals", out var pl) && pl is List<Dictionary<string, object>> list))
                            room.Meta["portals"] = list = new List<Dictionary<string, object>>();
                        list.Add(new Dictionary<string, object>
                            { ["corridor"] = op.Corridor, ["width"] = op.Cells.Count, ["jambs"] = jambs, ["head"] = head });
                    }
                }
            }
            return made;
        }

        static int Jambs(Level level, Room room, Opening op, string style, HashSet<Cell> taken, HashSet<Cell> mouths)
        {
            var d = op.Out;
            var t = d.X != 0 ? (0, 1) : (1, 0);              // along the wall
            Cell first = op.Cells[0], last = op.Cells[op.Cells.Count - 1];
            var ends = new[] { first.Offset(-t.Item1, -t.Item2), last.Offset(t.Item1, t.Item2) };
            int deep = style == "bunker" || style == "egyptian" ? 2 : 1;   // a blast frame; a pylon gate
            int placed = 0;
            foreach (var e in ends)
            {
                var cells = Enumerable.Range(0, deep).Select(k => e.Offset(-d.X * k, -d.Y * k)).ToList();
                bool ok = true;
                foreach (var c in cells)
                {
                    var sp = level.RoomSpans(c, room);
                    if (!room.Contains(c) || room.IsDiag(c.X, c.Y) || taken.Contains(c) || mouths.Contains(c) || level.CeilSlope.ContainsKey(c)
                        || sp.Count != 1 || sp[0].Floor != room.Floor)
                    {
                        ok = false;
                        break;
                    }
                }
                // the jamb's back must be wall: solid behind it, or it would plug something else
                var back = e.Offset(d.X, d.Y);
                if (!ok || (level.Spans.TryGetValue(back, out var bs) && bs.Count > 0)) continue;
                var snap = Snap(level, cells);
                foreach (var c in cells) level.ReplaceIn(c, room.Slot.Lo, room.Slot.Hi, new Span[0]);
                if (!BaseConnected(level, room))
                {
                    Restore(level, snap);
                    continue;
                }
                foreach (var c in cells) level.PartSolid[c] = $"room:{room.Id}/portals";
                placed++;
            }
            return placed;
        }

        static bool TopIs(Level level, Cell c, Span s) =>
            level.Spans.TryGetValue(c, out var l) && l.Count > 0 && l[l.Count - 1].Equals(s);

        static void WidenOwner(Level level, Cell c, int f, int to)
        {
            if (!level.Owners.TryGetValue(c, out var owners)) return;
            for (int i = 0; i < owners.Count; i++)
                if (owners[i].Lo == f && owners[i].Name.StartsWith("corridor:"))
                    owners[i] = new Owner(owners[i].Lo, Math.Max(owners[i].Hi, to), owners[i].Name);
        }

        /// <summary>Raise the mouth above the corridor into the wall: an arch or a lintelled opening.</summary>
        public static int Head_(Level level, Room room, Opening op, string style, HashSet<Cell> doors, bool smooth, Rng rng)
        {
            var d = op.Out;
            int f = op.Span.Floor, top = op.Span.Ceil;
            var mouth = op.Cells.Select(c => c.Offset(d.X, d.Y)).ToList();
            if (mouth.Any(m => doors.Contains(m) || level.Diag.ContainsKey(m) || level.CeilSlope.ContainsKey(m)))
                return 0;
            if (mouth.Any(m => !TopIs(level, m, op.Span)))
                return 0;                                       // something stacked above, or not the plain corridor
            if (op.Cells.Any(c => level.RoomSpans(c, room).Count == 0)) return 0;   // (guard: a filled room cell)
            int roomTop = op.Cells.Min(c => { var rs = level.RoomSpans(c, room); return rs[rs.Count - 1].Ceil; });
            var (shape, per, least, most) = Heads[style];
            int w = mouth.Count;
            int rise = Math.Min(most, per != 0 ? Math.Max(least, Q(per * w)) : least + 16 * rng.Int(0, 3));
            rise = Math.Min(rise, Q(roomTop - 16 - top));
            if (rise < 32) return 0;
            int peak = top + rise;
            var planned = new HashSet<Cell>(mouth);
            foreach (var m in mouth)                            // rock above the mouth, one solid cell around it
            {
                if (level.SpansIn(m, top + 1, peak + 16).Any()) return 0;
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        var n = m.Offset(dx, dy);
                        if (planned.Contains(n) || room.Contains(n)) continue;
                        if (level.SpansIn(n, top, peak + 16).Any() || level.Glass.Contains(n)) return 0;
                    }
            }
            Func<int, int, int> along = d.X != 0 ? ((X, Y) => Y) : ((X, Y) => X);
            var xs = mouth.Select(m => along(m.X, m.Y)).ToList();
            double centre = (xs.Min() + xs.Max() + 1) / 2.0, half = w / 2.0;
            Func<double, double> arch = shape switch
            {
                "pointed" => t => Pointed(t),
                "round" => RoundArch,
                "chamfer" => t => Math.Min(1.0, (1.0 - t) * 3.0),
                _ => null,
            };
            double Z(double A) => arch == null || !smooth ? peak : top + rise * arch(Math.Min(1.0, Math.Abs(A - centre) / half));
            foreach (var m in mouth)
            {
                var zs = Quad.Select(q => (int)Math.Round(Z(along(m.X + q.Item1, m.Y + q.Item2)))).ToArray();
                int flat = Math.Max(top, FloorDiv(zs.Min(), 16) * 16);
                level.ReplaceIn(m, f, top, new[] { new Span(f, flat) });
                WidenOwner(level, m, f, peak);
                if (zs.Max() > flat) level.CeilSlope[m] = (zs[0], zs[1], zs[2], zs[3]);
                level.PartCells[m] = $"corridor:{op.Corridor}/portal";
            }
            Splay(level, room, mouth, d, f, top, arch != null ? Q(top + rise / 2) : peak, doors, smooth);
            return rise;
        }

        /// <summary>The corridor's last cells before the mouth: the ceiling ramps up towards the portal, so
        /// the opening grows as you walk up to it. Tries 2 layers, then 1.</summary>
        public static int Splay(Level level, Room room, List<Cell> mouth, (int X, int Y) d, int f, int top, int hS,
                                HashSet<Cell> doors, bool smooth, int layers = 2)
        {
            if (!smooth || hS <= top) return 0;
            var plainTop = new Span(f, top);
            for (int n = layers; n >= 1; n--)
            {
                var cells = new List<Cell>();
                for (int k = 1; k <= n; k++)
                    foreach (var m in mouth) cells.Add(m.Offset(d.X * k, d.Y * k));
                if (cells.Any(c => doors.Contains(c) || level.Diag.ContainsKey(c) || level.CeilSlope.ContainsKey(c) || !TopIs(level, c, plainTop)))
                    continue;
                var planned = new HashSet<Cell>(cells);
                planned.UnionWith(mouth);
                bool ok = true;
                foreach (var c in cells)
                {
                    if (level.SpansIn(c, top + 1, hS + 16).Any()) ok = false;
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            var nb = c.Offset(dx, dy);
                            if (planned.Contains(nb) || room.Contains(nb)) continue;
                            foreach (var sp in level.SpansIn(nb, top, hS + 16))
                                if (!sp.Equals(plainTop)) ok = false;     // the corridor itself, further back, is fine
                            if (level.Glass.Contains(nb)) ok = false;
                        }
                }
                if (!ok) continue;
                // axial coordinate of the mouth's back edge, and which way the corridor runs from it
                var m0 = mouth[0];
                int sign = d.X != 0 ? d.X : d.Y;
                int a0 = (d.X != 0 ? m0.X : m0.Y) + (sign > 0 ? 1 : 0);
                int nn = n;
                double Z(int X, int Y)
                {
                    int a = d.X != 0 ? X : Y;
                    double dist = Math.Max(0.0, Math.Min(nn, (double)(a - a0) * sign));
                    return hS - (hS - top) * dist / nn;
                }
                foreach (var c in cells)
                {
                    var zs = Quad.Select(q => (int)Math.Round(Z(c.X + q.Item1, c.Y + q.Item2))).ToArray();
                    int flat = Math.Max(top, FloorDiv(zs.Min(), 16) * 16);
                    level.ReplaceIn(c, f, top, new[] { new Span(f, flat) });
                    WidenOwner(level, c, f, hS);
                    if (zs.Max() > flat) level.CeilSlope[c] = (zs[0], zs[1], zs[2], zs[3]);
                    level.PartCells[c] = level.PartCells.TryGetValue(mouth[0], out var pn) && !string.IsNullOrEmpty(pn) ? pn : "corridor/portal";
                }
                return n;
            }
            return 0;
        }
    }
}
