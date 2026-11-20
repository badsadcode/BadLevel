using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>Ramps (ramps.py): a stair corridor's steps become one smooth slope.
    /// <para>A corridor that changes height climbs one step (16 units) per cell. With <c>cfg.Ramps</c> (a chance
    /// per corridor, 0 by default: nothing changes), each such cell's floor slopes from the step below up to
    /// its own height, so the run reads as a ramp, not a stair. The cell keeps its own (higher) floor as its
    /// span: everything that walks the level (the validator, the player box of clearance) sees exactly what it
    /// saw before, and a 16-in-32 slope (27 degrees) is easy walking in Quake.</para>
    /// <para>Only plain cases are turned: the corridor's span is the bottom one of the cell, the cell behind is
    /// one step lower and the cell ahead level or one step higher (both the same corridor's), and the cells
    /// beside it are level with it or walls, so the whole width of the corridor rises together.</para></summary>
    public static class Ramps
    {
        public static readonly (int, int)[] D4 = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        public static int? CorridorFloor(Level level, Cell c, string owner)
        {
            if (level.Spans.TryGetValue(c, out var spans))
                foreach (var sp in spans)
                    if (level.OwnerOf(c, sp) == owner) return sp.Floor;
            return null;
        }

        /// <summary>Corner heights (x0y0, x1y0, x1y1, x0y1) of a cell rising in direction d from low to high.</summary>
        public static (int, int, int, int) Corners(Cell c, (int, int) d, int low, int high)
        {
            var outp = new int[4];
            int i = 0;
            foreach (var (dx, dy) in new[] { (0, 0), (1, 0), (1, 1), (0, 1) })
            {
                bool ahead = (d.Item1 == 1 && dx == 1) || (d.Item1 == -1 && dx == 0) ||
                             (d.Item2 == 1 && dy == 1) || (d.Item2 == -1 && dy == 0);
                outp[i++] = ahead ? high : low;
            }
            return (outp[0], outp[1], outp[2], outp[3]);
        }

        public static int MakeRamps(Level level)
        {
            var cfg = level.Config;
            double chance = cfg.Ramps;
            if (chance <= 0) return 0;
            int step = cfg.StepHeight;
            int made = 0;
            int s = level.CellSize;
            var ents = new Dictionary<Cell, List<Entity>>();
            foreach (var e in level.Entities)
                if (e.Bounds == null)
                {
                    var c = new Cell((int)Math.Floor(e.Pos.X / s), (int)Math.Floor(e.Pos.Y / s));
                    if (!ents.TryGetValue(c, out var l)) ents[c] = l = new List<Entity>();
                    l.Add(e);
                }
            var byOwner = new Dictionary<string, List<Cell>>();
            foreach (var kv in level.Owners)
                foreach (var o in kv.Value)
                {
                    if (!byOwner.TryGetValue(o.Name, out var l)) byOwner[o.Name] = l = new List<Cell>();
                    l.Add(kv.Key);
                }
            foreach (var corr in level.Corridors)
            {
                if (Rng.FromLabel($"{cfg.Seed}/{cfg.Variant}/ramp/{corr.Id}").Random() >= chance) continue;
                string owner = $"corridor:{corr.Id}";
                if (!byOwner.TryGetValue(owner, out var mine)) continue;
                foreach (var c in mine.OrderBy(x => x).ToList())
                {
                    if (level.Diag.ContainsKey(c) || level.FloorSlope.ContainsKey(c)) continue;
                    if (!level.Spans.TryGetValue(c, out var spans) || spans.Count == 0 || level.OwnerOf(c, spans[0]) != owner) continue;
                    int f = spans[0].Floor;
                    var dirs = new List<(int, int)>();
                    foreach (var d in D4)
                    {
                        int? back = CorridorFloor(level, c.Offset(-d.Item1, -d.Item2), owner);
                        int? ahead = CorridorFloor(level, c.Offset(d.Item1, d.Item2), owner);
                        if (back == f - step && (ahead == f || ahead == f + step)) dirs.Add(d);
                    }
                    if (dirs.Count != 1) continue;
                    var dd = dirs[0];
                    var side = new[] { c.Offset(dd.Item2, dd.Item1), c.Offset(-dd.Item2, -dd.Item1) };
                    if (side.Any(n => { var nf = CorridorFloor(level, n, owner); return nf != null && nf != f; })) continue;
                    level.FloorSlope[c] = Corners(c, dd, f - step, f);
                    made++;
                    // the ceiling follows, parallel, where nothing is stacked above (its span then keeps the
                    // lowest point, one step under where it was: still the corridor's headroom less 16)
                    if (spans.Count == 1 && !level.CeilSlope.ContainsKey(c) && !level.Sky.Contains(c))
                    {
                        int top = spans[0].Ceil;
                        if (top - step - f >= cfg.PlayerHeight + 32)
                        {
                            level.ReplaceIn(c, f, top, new[] { new Span(f, top - step) });
                            level.CeilSlope[c] = Corners(c, dd, top - step, top);
                            if (ents.TryGetValue(c, out var here))
                                foreach (var e in here)             // a light under the old ceiling comes down too
                                    if (e.Pos.Z > top - step - 16)
                                        e.Pos = new Vec3(e.Pos.X, e.Pos.Y, top - step - 16);
                        }
                    }
                }
            }
            if (made > 0) level.Notes.Add($"ramps: {made} cells");
            return made;
        }
    }
}
