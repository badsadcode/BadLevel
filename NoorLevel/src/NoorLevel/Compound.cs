using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>Compounds: open areas that read as places between buildings, not roofless rooms (compound.py).
    ///
    /// The architecture showcase kept finding the same thing: a courtyard was a 9-14 m pit with sheer walls
    /// on every side, a few blocks inside. Two changes make it a compound:
    /// <list type="bullet">
    /// <item><b>buildings</b>: free-standing blocks inside the yard, with walls, a roof and a doorway facing the
    /// yard's middle: you can walk in. Some get a ladder to the roof, and the rooftop becomes a vantage point.</item>
    /// <item><b>roofscape</b>: the rock around the yard is carved down to stepped roof heights, open to the sky:
    /// from the yard you see building fronts and a skyline rising away from you, not a cliff. The roofs are
    /// unreachable scenery; where something else is near (a corridor, a room, another storey) the rock stays,
    /// which reads as a gatehouse.</item>
    /// </list>
    /// Safety: a building keeps a 2-cell walkable ring and never touches a doorway; its rooftop is only
    /// reachable by its ladder. Roofscape cells are carved only where the column is empty from below the roof
    /// to above the sky, and only where no other space is within one cell at those heights, so nothing new can
    /// be walked into. The validator checks the rest.</summary>
    public static class Compound
    {
        public static readonly int Slab = 32;

        /// <summary>openings.SLIT: a window opening too low to climb through (kept local: openings.py is
        /// ported separately).</summary>
        const int Slit = 48;

        /// <summary>1-3 free-standing buildings in a yard, depending on its size.</summary>
        public static bool FBuildings(RoomCtx ctx, Rng rng)
        {
            var room = ctx.Room;
            var level = ctx.Level;
            int area = room.W * room.H;
            int want = area >= 320 ? 3 : area >= 200 ? 2 : 1;
            int made = 0;
            var (cx, cy) = room.Center;
            for (int t = 0; t < 40; t++)
            {
                if (made >= want) break;
                // houses, not towers: wide and low (the first tries are the widest; smaller if the yard is full)
                int big = t < 20 ? 9 : 6;
                int bw = rng.Int(5, big), bh = rng.Int(5, big);
                int x0 = rng.Int(room.X + 2, Math.Max(room.X + 2, room.X + room.W - 2 - bw));
                int y0 = rng.Int(room.Y + 2, Math.Max(room.Y + 2, room.Y + room.H - 2 - bh));
                var rect = new List<Cell>();
                for (int x = x0; x < x0 + bw; x++)
                    for (int y = y0; y < y0 + bh; y++) rect.Add(new Cell(x, y));
                var ring = new List<Cell>();
                for (int x = x0 - 2; x < x0 + bw + 2; x++)
                    for (int y = y0 - 2; y < y0 + bh + 2; y++) ring.Add(new Cell(x, y));
                if (!ctx.Free(ring)) continue;
                int top = room.Floor + rng.Choice(new[] { 160, 160, 176 });   // wall and roof top: lower than the roofs around
                int innerTop = top - Slab;                                      // the room inside: >= 128
                // the doorway: 2 cells in the middle of the side facing the yard's middle
                double bx = x0 + bw / 2.0, by = y0 + bh / 2.0;
                char side;
                if (Math.Abs(cx - bx) * bh > Math.Abs(cy - by) * bw)
                    side = cx > bx ? 'e' : 'w';
                else
                    side = cy > by ? 'n' : 's';
                List<Cell> door;
                if (side == 'n' || side == 's')
                {
                    int yy = side == 'n' ? y0 + bh - 1 : y0;
                    door = new List<Cell> { new Cell(x0 + bw / 2 - 1, yy), new Cell(x0 + bw / 2, yy) };
                }
                else
                {
                    int xx = side == 'e' ? x0 + bw - 1 : x0;
                    door = new List<Cell> { new Cell(xx, y0 + bh / 2 - 1), new Cell(xx, y0 + bh / 2) };
                }
                var walls = rect.Where(c => (c.X == x0 || c.X == x0 + bw - 1 || c.Y == y0 || c.Y == y0 + bh - 1)
                                            && !door.Contains(c)).ToList();
                var wallSet = new HashSet<Cell>(walls);
                string name = $"room:{room.Id}/building {made + 1}";
                foreach (var c in rect)
                {
                    if (wallSet.Contains(c))
                    {
                        level.ReplaceIn(c, room.Slot.Lo, room.Slot.Hi, new[] { new Span(top, room.Ceil) });
                        level.PartSolid[c] = name;
                    }
                    else
                        level.ReplaceIn(c, room.Slot.Lo, room.Slot.Hi, new[] { new Span(room.Floor, innerTop), new Span(top, room.Ceil) });
                    level.PartCells[c] = name;
                }
                ctx.Used.UnionWith(ring);
                made++;
                var info = new Dictionary<string, object>
                {
                    ["rect"] = new List<int> { x0, y0, bw, bh }, ["top"] = top, ["door"] = side.ToString(), ["ladder"] = false,
                };
                // a ladder up an outside wall (not the door side): the roof becomes a vantage point
                if (rng.Random() < 0.65)
                {
                    var sides = "nsew".Where(s => s != side).ToList();
                    rng.Shuffle(sides);
                    foreach (var s in sides)
                    {
                        Cell wallC, foot;
                        if (s == 'n' || s == 's')
                        {
                            wallC = new Cell(x0 + bw / 2, s == 'n' ? y0 + bh - 1 : y0);
                            foot = wallC.Offset(0, s == 'n' ? 1 : -1);
                        }
                        else
                        {
                            wallC = new Cell(s == 'e' ? x0 + bw - 1 : x0, y0 + bh / 2);
                            foot = wallC.Offset(s == 'e' ? 1 : -1, 0);
                        }
                        var fs = level.RoomSpans(foot, room);
                        if (!(fs.Count == 1 && fs[0].Equals(new Span(room.Floor, room.Ceil)))) continue;
                        int sz = level.CellSize;
                        double ax = (foot.X + 0.5) * sz, ay = (foot.Y + 0.5) * sz;
                        double bx_ = (wallC.X + 0.5) * sz, by_ = (wallC.Y + 0.5) * sz;
                        double mx = (ax + bx_) / 2, my = (ay + by_) / 2;
                        bool ew = s == 'e' || s == 'w', ns = !ew;
                        var lo = new Vec3(mx - (ew ? 4 : 16), my - (ns ? 4 : 16), room.Floor);
                        var hi = new Vec3(mx + (ew ? 4 : 16), my + (ns ? 4 : 16), top + 32);
                        level.Entities.Add(new Entity("ladder", new Vec3((lo.X + hi.X) / 2, (lo.Y + hi.Y) / 2, (room.Floor + top) / 2.0))
                        {
                            Bounds = (lo, hi),
                        }.With("low", room.Floor).With("high", top).With("room", room.Id).With("building", true));
                        level.Links.Add(new Link
                        {
                            Kind = "ladder", A = new Vec3(ax, ay, room.Floor + 24), B = new Vec3(bx_, by_, top + 24),
                            TwoWay = true, Interaction = "climb", Accessible = false, Room = room.Id,
                        });
                        ctx.TopCells.AddRange(rect.Where(c => !door.Contains(c)));
                        info["ladder"] = true;
                        info["ladder_cell"] = new List<int> { wallC.X, wallC.Y };
                        break;
                    }
                }
                Interior(level, room, ctx, rng, info, walls, door, innerTop);
                if (!(room.Meta.TryGetValue("buildings", out var bl) && bl is List<Dictionary<string, object>> list))
                    room.Meta["buildings"] = list = new List<Dictionary<string, object>>();
                list.Add(info);
            }
            if (made > 0) room.Features.Add("buildings");
            return made > 0;
        }

        /// <summary>A little life inside: window openings in the side walls (48 units, too low to climb
        /// through: you see and shoot through them), a light and a counter against the back wall. (The
        /// small reward for going in is a pickup: left to the game, see PORTING.md.)</summary>
        public static void Interior(Level level, Room room, RoomCtx ctx, Rng rng, Dictionary<string, object> info,
                                    List<Cell> walls, List<Cell> door, int innerTop)
        {
            var r = (List<int>)info["rect"];
            int x0 = r[0], y0 = r[1], bw = r[2], bh = r[3];
            int s = level.CellSize;
            Cell? ladder = info.TryGetValue("ladder_cell", out var lc) && lc is List<int> l
                ? new Cell(l[0], l[1]) : (Cell?)null;
            char doorSide = ((string)info["door"])[0];
            // windows: one per wall side except the door side, near the middle, not where the ladder is
            int lo = room.Floor + 40, hi = room.Floor + 40 + Slit;
            int wins = 0;
            foreach (var side in "nsew")
            {
                if (side == doorSide) continue;
                var cands = new List<Cell>();
                if (side == 'n' || side == 's')
                {
                    int yy = side == 'n' ? y0 + bh - 1 : y0;
                    for (int x = x0 + 1; x < x0 + bw - 1; x++) cands.Add(new Cell(x, yy));
                }
                else
                {
                    int xx = side == 'e' ? x0 + bw - 1 : x0;
                    for (int y = y0 + 1; y < y0 + bh - 1; y++) cands.Add(new Cell(xx, y));
                }
                cands = cands.Where(c => c != ladder && walls.Contains(c)).ToList();
                if (cands.Count < 2) continue;
                var cw = cands[cands.Count / 2 - 1];
                var topSpan = level.Spans[cw][level.Spans[cw].Count - 1];
                level.ReplaceIn(cw, room.Slot.Lo, room.Slot.Hi, new[] { new Span(lo, hi), topSpan });
                if (!level.Owners.TryGetValue(cw, out var os)) level.Owners[cw] = os = new List<Owner>();
                os.Insert(0, new Owner(lo, hi, $"window:building{room.Id}"));   // (before the yard's)
                wins++;
            }
            info["windows"] = wins;
            var inner = new List<Cell>();
            for (int x = x0 + 1; x < x0 + bw - 1; x++)
                for (int y = y0 + 1; y < y0 + bh - 1; y++) inner.Add(new Cell(x, y));
            double cx = x0 + bw / 2.0, cy = y0 + bh / 2.0;
            level.Entities.Add(new Entity("light", new Vec3(cx * s, cy * s, innerTop - 24)).With("intensity", 180).With("building", true));
            // a counter against the back wall, in rooms of at least 3x3 inside, never in front of the door
            char back = doorSide switch { 'n' => 's', 's' => 'n', 'e' => 'w', _ => 'e' };
            if (bw >= 5 && bh >= 5)
            {
                var counter = new List<Cell>();
                if (back == 'n' || back == 's')
                {
                    int yy = back == 's' ? y0 + 1 : y0 + bh - 2;
                    for (int x = x0 + 2; x < x0 + bw - 2; x++) counter.Add(new Cell(x, yy));
                }
                else
                {
                    int xx = back == 'w' ? x0 + 1 : x0 + bw - 2;
                    for (int y = y0 + 2; y < y0 + bh - 2; y++) counter.Add(new Cell(xx, y));
                }
                foreach (var c in counter)
                {
                    var spans = level.Spans[c];
                    level.ReplaceIn(c, room.Slot.Lo, room.Slot.Hi, new[] { new Span(room.Floor + 40, innerTop), spans[spans.Count - 1] });
                }
                info["counter"] = counter.Count;
                inner = inner.Where(c => !counter.Contains(c)).ToList();
            }
            // (a small reward for going in, in the cell nearest the middle: a pickup, left to the game)
        }

        static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);

        /// <summary>Carve the rock around a yard down to stepped roofs open to the sky.</summary>
        public static int Roofscape(Level level, Room room, Rng rng, int band = 4)
        {
            int skyTop = room.Ceil;
            int x0 = room.X, y0 = room.Y, x1 = room.X + room.W, y1 = room.Y + room.H;
            // blocks along the band: each block of 3-6 cells gets its own front height, and the roofs
            // rise away from the yard towards the sky plane
            var front = new Dictionary<(char, int), (int Base, int Rise)>();

            (int Base, int Rise) FrontHeight((char, int) key)
            {
                if (!front.TryGetValue(key, out var v))
                {
                    // never within a step of anything a player stands on along a yard wall (a balcony deck
                    // is 144 up): the roofs stay scenery. Mostly flat blocks of their own height, a few
                    // rising a little: low buildings with sky above them, not a stepped cliff
                    v = (room.Floor + rng.Choice(new[] { 176, 192, 208, 224, 256 }), rng.Choice(new[] { 0, 0, 16, 32 }));
                    front[key] = v;
                }
                return v;
            }
            var planned = new Dictionary<Cell, int>();
            for (int x = x0 - band; x < x1 + band; x++)
                for (int y = y0 - band; y < y1 + band; y++)
                {
                    if (room.Contains(x, y) || (x0 <= x && x < x1 && y0 <= y && y < y1)) continue;
                    int d = Math.Max(Math.Max(x0 - x, x - (x1 - 1)), Math.Max(y0 - y, y - (y1 - 1)));   // 1..band: rings out from the yard
                    bool ns = y < y0 || y >= y1;
                    int along = ns ? x : y;
                    char side = ns ? (y < y0 ? 's' : 'n') : (x < x0 ? 'w' : 'e');
                    var (bse, rise) = FrontHeight((side, FloorDiv(along, 5)));       // 5-cell blocks of building
                    int roof = Math.Min(skyTop - 64, bse + (d - 1) * rise);          // (at least 64 of air under the sky)
                    roof = FloorDiv(roof, 16) * 16;
                    if (roof - room.Floor < 112) continue;
                    planned[new Cell(x, y)] = roof;
                }
            int carved = 0;
            string tag = $"roof:{room.Id}";
            foreach (var kv in planned.OrderBy(kv => kv.Key))
            {
                var c = kv.Key;
                int roof = kv.Value;
                if (!(2 < c.X && c.X < level.Width - 3 && 2 < c.Y && c.Y < level.Height - 3)) continue;
                if (level.Diag.ContainsKey(c) || level.Glass.Contains(c) || level.CeilSlope.ContainsKey(c)
                    || level.SpansIn(c, roof - 64, skyTop + 32).Any())
                    continue;                                // (a sloped ceiling must stay the top of its column)
                bool ok = true;
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        var n = c.Offset(dx, dy);
                        if (n == c || room.Contains(n)) continue;
                        var near = level.SpansIn(n, roof - 64, skyTop + 32).ToList();
                        if (level.Glass.Contains(n) || near.Any(sp => level.OwnerOf(n, sp) != tag))
                            ok = false;                      // something else is right there: keep the rock
                    }
                if (!ok) continue;
                level.AddSpan(c, new Span(roof, skyTop), tag);
                level.Sky.Add(c);
                level.PartCells[c] = $"room:{room.Id}/roofscape";
                carved++;
            }
            if (carved > 0)
            {
                room.Meta["roofscape"] = carved;
                room.Features.Add("roofscape");
            }
            return carved;
        }
    }
}
