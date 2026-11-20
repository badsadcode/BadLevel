using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>The goblin market (Noor's idea): an open yard laid out in streets of stalls (market.py).
    ///
    /// A market is a courtyard (open to the sky) filled with stalls in double rows, back to back, each
    /// facing a street; streets 4 cells wide (two player boxes), gaps of 2 between some stalls so the rows
    /// can be crossed, a plaza in the middle with a well, a street round the walls. A stall:
    /// <list type="bullet">
    /// <item>a counter at the front, 40 high: cover you can see over (and hop onto);</item>
    /// <item>the booth behind it, under a roof at 112, where the merchant stands;</item>
    /// <item>side and back walls up to the roof, the roof a 16-unit slab, open sky above.</item>
    /// </list>
    /// Every stall is a named part (<c>room:N/stall 3</c>) with a <c>merchant</c> marker behind its counter
    /// and what it sells, so the artist's pass knows what goes where. Lanterns hang at the stall fronts.
    /// cfg.Market: the chance a yard is one (called by the open-area space type).</summary>
    public static class Market
    {
        public static readonly string[] Goods =
        {
            "suspiciously twitching treasure chests", "meat of no known species", "mushrooms (some escape)",
            "teeth (no explanation)", "swords that scream when swung", "potions of uncertain colour",
            "rats on sticks", "maps to places that aren't there", "second-hand hats", "jars of eyes",
            "slightly cursed rings", "live eels", "goblin poetry", "boots, one of each",
        };
        public static readonly int Depth = 3;      // a stall: counter, booth, back wall
        public static readonly int Street = 4;     // between rows of stalls: two player boxes
        public static readonly int Roof = 112, Slab = 16;   // the booth's headroom and its roof

        public static void Stall(RoomCtx ctx, List<List<Cell>> cellsByRow, int k, string sells, (int X, int Y) frontDir)
        {
            var level = ctx.Level;
            var room = ctx.Room;
            int f = room.Floor;
            var (lo, hi) = room.Slot;
            string name = $"room:{room.Id}/stall {k}";
            List<Cell> counter = cellsByRow[0], booth = cellsByRow[1], back = cellsByRow[2];
            var sides = new HashSet<Cell> { booth[0], booth[booth.Count - 1], counter[0], counter[counter.Count - 1] };
            foreach (var c in back.Concat(new[] { booth[0], booth[booth.Count - 1] }))   // the walls, up to the roof
            {
                level.ReplaceIn(c, lo, hi, new[] { new Span(f + Roof + Slab, room.Ceil) });
                level.PartSolid[c] = name;
            }
            foreach (var c in counter)
            {
                if (sides.Contains(c))
                {
                    level.ReplaceIn(c, lo, hi, new[] { new Span(f + Roof + Slab, room.Ceil) });
                    level.PartSolid[c] = name;
                }
                else                                       // the counter, under the awning
                {
                    level.ReplaceIn(c, lo, hi, new[] { new Span(f + 40, f + Roof), new Span(f + Roof + Slab, room.Ceil) });
                    level.PartCells[c] = name;
                }
            }
            for (int i = 1; i < booth.Count - 1; i++)       // the booth, under the roof
            {
                var c = booth[i];
                level.ReplaceIn(c, lo, hi, new[] { new Span(f, f + Roof), new Span(f + Roof + Slab, room.Ceil) });
                level.PartCells[c] = name;
            }
            foreach (var c in counter.Concat(booth).Concat(back)) ctx.Used.Add(c);
            int s = level.CellSize;
            var mid = booth[booth.Count / 2];
            level.Entities.Add(new Entity("merchant", new Vec3((mid.X + 0.5) * s, (mid.Y + 0.5) * s, f + 24))
                .With("stall", k).With("sells", sells).With("room", room.Id));
            var front = counter[counter.Count / 2];
            double lx = (front.X + 0.5 + frontDir.X * 0.8) * s, ly = (front.Y + 0.5 + frontDir.Y * 0.8) * s;
            level.Entities.Add(new Entity("light", new Vec3(lx, ly, f + Roof - 8)).With("intensity", 160).With("lantern", true));
            if (!(room.Meta.TryGetValue("stalls", out var st) && st is List<Dictionary<string, object>> stalls))
                room.Meta["stalls"] = stalls = new List<Dictionary<string, object>>();
            stalls.Add(new Dictionary<string, object> { ["id"] = k, ["sells"] = sells, ["cells"] = counter.Count + booth.Count + back.Count });
        }

        public static bool FMarket(RoomCtx ctx, Rng rng)
        {
            var room = ctx.Room;
            var level = ctx.Level;
            if (Math.Min(room.W, room.H) < 14) return false;
            bool alongX = room.W >= room.H;
            int L = alongX ? room.W : room.H, D = alongX ? room.H : room.W;
            Func<int, int, Cell> toW = alongX
                ? (a, b) => new Cell(room.X + a, room.Y + b)
                : (Func<int, int, Cell>)((a, b) => new Cell(room.X + b, room.Y + a));
            var goods = new List<string>(Goods);
            rng.Shuffle(goods);
            // the plaza: the middle third along, kept open (a well in its centre)
            var plaza = (Lo: L / 2 - Math.Max(3, L / 6), Hi: L / 2 + Math.Max(3, L / 6));
            int k = 0;
            int v = 3;                                         // a street round the walls
            while (v + 2 * Depth <= D - 3)
            {
                // a double row: stalls facing -v (rows v..v+2) and +v (rows v+3..v+5), back to back
                int u = 3 + rng.Int(0, 1);
                while (u + 3 <= L - 3)
                {
                    int w = rng.Choice(new[] { 3, 4 });
                    if (u + w > L - 3) break;
                    if (!(u + w <= plaza.Lo || u >= plaza.Hi))
                    {
                        u = plaza.Hi;
                        continue;
                    }
                    foreach (var (facing, rows) in new[] { (-1, new[] { v, v + 1, v + 2 }), (1, new[] { v + 5, v + 4, v + 3 }) })
                    {
                        var cells = rows.Select(r => Enumerable.Range(u, w).Select(a => toW(a, r)).ToList()).ToList();
                        var flat = cells.SelectMany(row => row);
                        if (!flat.All(c => room.Contains(c) && !ctx.Clear.Contains(c) && !ctx.Used.Contains(c) && !level.Diag.ContainsKey(c)
                                           && IsWhole(level.RoomSpans(c, room), room)))
                            continue;
                        var d = alongX ? (0, facing) : (facing, 0);
                        Stall(ctx, cells, k, goods[k % goods.Count], d);
                        k++;
                    }
                    u += w + (rng.Random() < 0.4 ? 2 : 0);   // now and then an alley through the row
                }
                v += 2 * Depth + Street;
            }
            if (k == 0) return false;
            // the well in the plaza
            int cu = (plaza.Lo + plaza.Hi) / 2, cv = D / 2;
            var well = new List<Cell>();
            foreach (var i in new[] { -1, 0 }) foreach (var j in new[] { -1, 0 }) well.Add(toW(cu + i, cv + j));
            if (well.All(c => room.Contains(c) && !ctx.Used.Contains(c) && !ctx.Clear.Contains(c)))
                foreach (var c in well)
                {
                    ctx.SetFloor(c, room.Floor + 48);
                    level.PartCells[c] = $"room:{room.Id}/well";
                }
            room.Features.Add("market");
            return true;
        }

        static bool IsWhole(List<Span> spans, Room room) =>
            spans.Count == 1 && spans[0].Floor == room.Floor && spans[0].Ceil == room.Ceil;
    }
}
