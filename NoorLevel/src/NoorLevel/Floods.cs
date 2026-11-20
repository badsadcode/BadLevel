using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>Flooded crypts (floods.py): water to wade through, and corridors you must swim.
    /// <list type="bullet">
    /// <item>Wading water (<c>cfg.Floods</c>, a chance per crypt): a room fills to <see cref="Wade"/> units (32: knee
    /// to waist on a 56-unit player, well under the eyes at 46). The water spreads through doorways and along
    /// level corridors, at one height, and stops where the floor rises above it. It doesn't change a single
    /// floor. If it would spread over more than MaxFlood cells, or reach a pit, a chasm or a lava pool, that
    /// room stays dry.</item>
    /// <item>Swimming (<c>cfg.Sumps</c>, a chance per corridor): a straight, level corridor dips: down a few steps,
    /// along a stretch whose ceiling is under the water (no air), and up again. <see cref="BreathReport"/> (part
    /// of validation) fails the level if a stretch without air is longer than MaxSwim cells.</item>
    /// </list>
    /// <c>level.Water</c>: cell -> the water's surface height over that cell's lowest span.</summary>
    public static partial class Floods
    {
        public static readonly int Wade = 32;              // wading depth: under a Quake player's eyes (46 above the feet)
        public static readonly int MaxFlood = 900;         // cells: a flood bigger than this is a lake; the room stays dry
        public static readonly int SumpDrop = 5;           // steps down (16 each): at the bottom the duct is all under water
        public static readonly int SumpHeight = 80;        // the duct: 64 of headroom between one step and the next
        public static readonly int MaxSwim = 14;           // cells without air on one breath

        static readonly (int, int)[] D4 = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        /// <summary>Cells the water reaches from the start cells at surface height S, or null if it spills
        /// into a pit or grows past MaxFlood.</summary>
        public static HashSet<Cell> Flood(Level level, IEnumerable<Cell> startCells, int S)
        {
            var seen = new HashSet<Cell>();
            var stack = new List<Cell>();
            foreach (var c in startCells)
                if (level.Spans.TryGetValue(c, out var sp) && sp.Count > 0 && sp[0].Floor < S && seen.Add(c))
                    stack.Add(c);
            while (stack.Count > 0)
            {
                var c = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                var s0 = level.Spans[c][0];
                foreach (var (dx, dy) in D4)
                {
                    var n = c.Offset(dx, dy);
                    if (seen.Contains(n) || !level.Spans.TryGetValue(n, out var ns) || level.Glass.Contains(n)) continue;
                    var s1 = ns[0];
                    if (s1.Floor >= S || Math.Min(s0.Ceil, s1.Ceil) <= Math.Max(s0.Floor, s1.Floor))
                        continue;                                  // dry ground, or no opening between them
                    if (level.Hazard.ContainsKey(n) || s1.Floor < S - 160)
                        return null;                               // a pit, a chasm, a pool: not a crypt flood
                    seen.Add(n);
                    stack.Add(n);
                    if (seen.Count > MaxFlood) return null;
                }
            }
            return seen;
        }

        public static int AddFloods(Level level)
        {
            var cfg = level.Config;
            double chance = cfg.Floods;
            if (chance <= 0)
            {
                MakeSumps(level);
                return 0;
            }
            int made = 0;
            foreach (var room in level.Rooms)
            {
                if (room.IsConnector || room.Kind == "courtyard" || room.Kind == "overlook" || room.Kind == "megaroom") continue;
                string prog = room.Get<string>("program", "") ?? "";
                if (cfg.ProgramSet == "dungeon" && prog != "" && prog != "crypt" && prog != "catacomb hall" && prog != "shrine" && prog != "cells")
                    continue;                                      // dungeons flood their crypts; elsewhere any room
                if (room.Cells().Any(c => level.Water.ContainsKey(c))) continue;
                var rng = Rng.FromLabel($"{cfg.Seed}/{cfg.Variant}/flood/{room.Id}");
                if (rng.Random() >= chance) continue;
                int S = room.Floor + Wade;
                var starts = room.Cells().Where(c => level.SpansIn(c, room.Slot.Lo, room.Slot.Hi).Any()
                                                     && level.Spans[c][0].Floor == room.Floor).ToList();
                var cells = Flood(level, starts, S);
                if (cells == null || cells.Count < 20) continue;
                foreach (var c in cells) level.Water[c] = S;
                room.Features.Add("flooded");
                room.Meta["flood"] = new Dictionary<string, object> { ["surface"] = S, ["cells"] = cells.Count };
                made++;
            }
            MakeSumps(level);
            if (made > 0) level.Notes.Add($"floods: {made}");
            return made;
        }

        /// <summary>A straight, level stretch of corridor dips under water: a sump (a U-bend). Down SumpDrop
        /// steps, along a flat bottom, up again; the dip holds water flush with the corridor's floor (or the
        /// flood's, if it's in one), and its duct is 80 high, so at the bottom there's no air: a swim.</summary>
        public static int MakeSumps(Level level)
        {
            var cfg = level.Config;
            double chance = cfg.Sumps;
            if (chance <= 0) return 0;
            int made = 0;
            foreach (var corr in level.Corridors)
            {
                if (corr.Vent || !string.IsNullOrEmpty(corr.Connector) || corr.Kind != "open") continue;
                var rng = Rng.FromLabel($"{cfg.Seed}/{cfg.Variant}/sump/{corr.Id}");
                if (rng.Random() >= chance) continue;
                var path = corr.Path; var floors = corr.Floors;
                string tag = $"corridor:{corr.Id}";
                int nFlat = rng.Int(2, 4);
                var profRel = new List<int>();
                for (int k = 0; k < SumpDrop; k++) profRel.Add(-16 * (k + 1));
                for (int k = 0; k < nFlat; k++) profRel.Add(-16 * SumpDrop);
                for (int k = 0; k < SumpDrop; k++) profRel.Add(-16 * (SumpDrop - k));
                int L = profRel.Count;
                // a window of the path: straight and level, with 2 cells of margin each side
                var starts = new List<int>();
                for (int i = 2; i < path.Count - L - 2; i++)
                {
                    bool straight = true;
                    for (int j = i - 1; j < i + L + 1 && straight; j++)
                        if (!Layout.Straight(path, j, corr.Radius, axisOnly: true)) straight = false;
                    if (!straight) continue;
                    bool level1 = true;
                    for (int j = i - 2; j < i + L + 2; j++) if (floors[j] != floors[i - 2]) { level1 = false; break; }
                    if (level1) starts.Add(i);
                }
                if (starts.Count == 0) continue;
                int i0 = starts[starts.Count / 2];
                int f = floors[i0];
                var mine = level.Owners.Where(kv => kv.Value.Any(o => o.Name == tag)).Select(kv => kv.Key).OrderBy(c => c).ToList();
                var cells = new Dictionary<Cell, int>();
                var cellOrder = new List<Cell>();
                foreach (var c in mine)
                {
                    int best = 0, bd = int.MaxValue;
                    for (int j = 0; j < path.Count; j++)
                    {
                        int dd = Math.Abs(path[j].X - c.X) + Math.Abs(path[j].Y - c.Y);
                        if (dd < bd) { bd = dd; best = j; }
                    }
                    if (i0 <= best && best < i0 + L) { cells[c] = f + profRel[best - i0]; cellOrder.Add(c); }
                }
                int lowest = f - 16 * SumpDrop;
                bool ok = cells.Count > 0;
                foreach (var c in cellOrder)
                {
                    var sp = level.Spans.TryGetValue(c, out var l) ? l : new List<Span>();
                    if (sp.Count != 1 || sp[0].Floor != f || level.FloorSlope.ContainsKey(c) || level.Diag.ContainsKey(c) || level.Hazard.ContainsKey(c))
                    {
                        ok = false;
                        break;
                    }
                    for (int dx = -1; dx <= 1; dx++)               // nothing open down there, beside or below
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            var n = c.Offset(dx, dy);
                            if (!cells.ContainsKey(n) && level.SpansIn(n, lowest - 16, f).Any()) ok = false;
                        }
                }
                if (!ok) continue;
                foreach (var c in cellOrder)
                {
                    int nf = cells[c];
                    int top = Math.Min(level.Spans[c][0].Ceil, nf + SumpHeight);
                    level.ReplaceIn(c, f, level.Spans[c][0].Ceil, new[] { new Span(nf, top) });
                    level.Owners[c] = level.Owners[c].Select(o => new Owner(o.Name == tag ? Math.Min(o.Lo, nf) : o.Lo, o.Hi, o.Name)).ToList();
                    level.CeilSlope.Remove(c);
                    level.PartCells[c] = $"{tag}/sump";
                    level.Water[c] = Math.Max(level.Water.TryGetValue(c, out var w) ? w : f, f);
                }
                int s_ = level.CellSize;
                foreach (var e in level.Entities)                  // whatever stood here goes down with the floor
                {
                    var c = new Cell((int)Math.Floor(e.Pos.X / s_), (int)Math.Floor(e.Pos.Y / s_));
                    if (cells.TryGetValue(c, out int nf) && e.Bounds == null)
                    {
                        int top = level.Spans[c][0].Ceil;
                        double z = e.Pos.Z - (f - nf);
                        e.Pos = new Vec3(e.Pos.X, e.Pos.Y, Math.Min(Math.Max(z, nf + 8), top - 8));
                    }
                }
                for (int i = i0; i < i0 + L; i++) corr.Floors[i] = f + profRel[i - i0];
                corr.Sump = true;
                made++;
            }
            if (made > 0) level.Notes.Add($"sumps: {made}");
            return made;
        }

        /// <summary>Every stretch without air (the ceiling under the water) must be swum on one breath.</summary>
        public static List<string> BreathReport(Level level)
        {
            var errors = new List<string>();
            if (level.Water.Count == 0) return errors;
            // no air: less than 12 units between the water and the ceiling (no room for a head)
            var noair = new HashSet<Cell>(level.Water.Where(kv => level.Spans.TryGetValue(kv.Key, out var sp) && sp.Count > 0
                                                                  && sp[0].Ceil - kv.Value < 12).Select(kv => kv.Key));
            var seen = new HashSet<Cell>();
            foreach (var c in noair.OrderBy(x => x))
            {
                if (seen.Contains(c)) continue;
                var comp = new List<Cell> { c };
                var stack = new List<Cell> { c };
                seen.Add(c);
                while (stack.Count > 0)
                {
                    var a = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    foreach (var (dx, dy) in D4)
                    {
                        var n = a.Offset(dx, dy);
                        if (noair.Contains(n) && seen.Add(n))
                        {
                            comp.Add(n);
                            stack.Add(n);
                        }
                    }
                }
                int span = Math.Max(comp.Max(p => p.X) - comp.Min(p => p.X), comp.Max(p => p.Y) - comp.Min(p => p.Y)) + 1;
                if (span > MaxSwim)
                {
                    var m = comp.Min();
                    errors.Add($"breath: {span} cells under water without air at ({m.X}, {m.Y}) (one breath: {MaxSwim})");
                }
            }
            return errors;
        }
    }
}
