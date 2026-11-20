using System;
using System.Collections.Generic;
using System.Linq;
using static NoorLevel.Architecture;

// landmark.py: one deliberate, unique, tall object per level, in its biggest space.
//
//   gothic spire, castle keep, egyptian obelisk, industrial stack (solid up through the roof),
//   scifi core (floor to ceiling, floating rings), compound water tower (open underneath),
//   bunker monolith; (any) column: where the room is too low for the style's own shape.
//
// Placed before the room's features (they avoid it), as near the room's middle as fits, with a
// 2-cell walkable ring, never at a doorway, and undone if it would cut the floor in two. A
// free-standing top stays at least HEAD below the room's ceiling profile. Tagged as a part
// ("room:N/landmark").
namespace NoorLevel
{
    public static partial class Landmark
    {
        public static readonly Dictionary<string, string> Shapes = new Dictionary<string, string>
        {
            ["gothic"] = "spire", ["castle"] = "keep", ["egyptian"] = "obelisk", ["industrial"] = "stack",
            ["scifi"] = "core", ["compound"] = "water tower", ["bunker"] = "monolith",
        };

        static IEnumerable<(int, int)> Block(int x0, int x1, int y0, int y1)
        {
            for (int x = x0; x < x1; x++)
                for (int y = y0; y < y1; y++) yield return (x, y);
        }

        static List<(int, int)> R(params (int, int)[] xs) => xs.ToList();

        /// <summary>Cell offset -> solid ranges (above the floor) for a shape; null if the room can't take it.
        /// topMax: the highest a free-standing top may reach; H the room's height.</summary>
        public static Dictionary<(int, int), List<(int Lo, int Hi)>> Shape(string name, int H, int topMax)
        {
            var o = new Dictionary<(int, int), List<(int, int)>>();
            if ((name == "spire" || name == "keep" || name == "obelisk" || name == "monolith" || name == "water tower") && topMax < 160)
                return null;
            switch (name)
            {
                case "spire":
                    foreach (var c in Block(-2, 2, -2, 2)) o[c] = R((0, Q(topMax * 0.3)));
                    foreach (var c in new[] { (-2, -2), (1, -2), (-2, 1), (1, 1) }) o[c] = R((0, Q(topMax * 0.45)));   // corner pinnacles
                    foreach (var c in Block(-1, 1, -1, 1)) o[c] = R((0, topMax));
                    break;
                case "keep":
                    foreach (var c in Block(-2, 2, -2, 2))
                    {
                        bool edge = c.Item1 == -2 || c.Item1 == 1 || c.Item2 == -2 || c.Item2 == 1;
                        bool merlon = edge && ((c.Item1 + c.Item2) % 2 + 2) % 2 == 0;
                        o[c] = R((0, Q(topMax * 0.8) + (merlon ? 32 : 0)));
                    }
                    break;
                case "obelisk":
                    foreach (var c in Block(-2, 2, -2, 2)) o[c] = R((0, 48));
                    foreach (var c in Block(-1, 1, -1, 1)) o[c] = R((0, topMax));
                    break;
                case "monolith":
                    foreach (var c in Block(-2, 2, -1, 1)) o[c] = R((0, 32));
                    foreach (var c in Block(-1, 1, -1, 1)) o[c] = R((0, Q(topMax * 0.8)));
                    break;
                case "stack":
                    foreach (var c in Block(-2, 3, -2, 3)) o[c] = R((0, 96));
                    foreach (var c in Block(-1, 2, -1, 2)) o[c] = R((0, H));               // through the roof
                    break;
                case "core":
                {
                    if (H < Head * 2 + 64) return null;
                    int r1 = Q(H * 0.35), r2 = Q(H * 0.65);
                    foreach (var c in Block(-2, 3, -2, 3))
                        if (r1 >= Head) o[c] = R((r1, r1 + 16), (r2, r2 + 16));           // rings floating around it
                    foreach (var c in Block(-1, 2, -1, 2)) o[c] = R((0, H));
                    break;
                }
                case "column":                                                             // the fallback: fits any height
                    foreach (var c in Block(-2, 2, -2, 2)) o[c] = R((0, 48));
                    foreach (var c in Block(-1, 1, -1, 1)) o[c] = R((0, H));
                    break;
                case "water tower":
                {
                    int tank0 = Q(topMax * 0.6), tank1 = Q(topMax * 0.85);
                    if (tank0 < Head) return null;
                    foreach (var c in Block(-2, 2, -2, 2)) o[c] = R((tank0, tank1));
                    foreach (var c in Block(-1, 1, -1, 1)) o[c] = R((0, tank1));
                    break;
                }
            }
            return o.Count > 0 ? o : null;
        }

        /// <summary>The style's landmark, or where the room is too low for it, a great column. As big as the
        /// room allows: scaled 3x / 2x / 1x with the room's size, smaller if it doesn't fit.</summary>
        public static Dictionary<string, object> Place(Level level, Room room, RoomCtx ctx, Section sec, string style, Rng rng)
        {
            if (style == null || !Shapes.TryGetValue(style, out var name)) return null;
            int big = Math.Min(room.W, room.H);
            foreach (int scale in new[] { 3, 2, 1 }.Where(k => big >= 14 * k || k == 1))
            {
                var got = Place_(level, room, ctx, sec, name, scale);
                if (got == null && scale == 1 && level.Config.Pillars) got = Place_(level, room, ctx, sec, "column", 1);   // (no column with pillars off)
                if (got != null) return got;
            }
            return null;
        }

        public static Dictionary<string, object> Place_(Level level, Room room, RoomCtx ctx, Section sec, string name, int scale = 1)
        {
            int H = room.Ceil - room.Floor;
            // the lowest ceiling over the room's middle (after the section) bounds a free-standing top
            var fr = sec?.Frame;
            var (cx, cy) = room.Center;
            int ox = (int)cx, oy = (int)cy;
            var cands = new List<Cell>();
            for (int x = ox - 6; x < ox + 7; x++)
                for (int y = oy - 6; y < oy + 7; y++) cands.Add(new Cell(x, y));
            var order = cands.OrderBy(c => Math.Pow(c.X + 0.5 - cx, 2) + Math.Pow(c.Y + 0.5 - cy, 2)).ToList();
            var nearDoor = Around(ctx.Doors, 3);
            foreach (var p in order)
            {
                int local = H;
                if (fr != null)
                    for (int dx = -3; dx <= 3; dx++)
                        for (int dy = -3; dy <= 3; dy++)
                            if (sec.Ceil.TryGetValue(fr.UV(p.Offset(dx, dy)), out int hz)) local = Math.Min(local, hz);
                var shape = Shape(name, H, Q(local - Head - 16));
                if (shape == null) continue;                    // (a lower spot of the profile may not fit it)
                // scaled with the room: a landmark has to dominate its space (2x in a mega-hall)
                int k = scale;
                var cells = new Dictionary<Cell, List<(int Lo, int Hi)>>();
                foreach (var kv in shape)
                    for (int i = 0; i < k; i++)
                        for (int j = 0; j < k; j++)
                            cells[new Cell(p.X + kv.Key.Item1 * k + i, p.Y + kv.Key.Item2 * k + j)] = kv.Value;
                var ring = Around(cells.Keys, 2);
                ring.ExceptWith(cells.Keys);
                if (!cells.Keys.All(c => room.Contains(c) && !room.IsDiag(c.X, c.Y) && !ctx.Used.Contains(c) && !nearDoor.Contains(c)
                                         && IsPlain(level, room, c)))
                    continue;
                if (!ring.All(c => room.Contains(c) && !ctx.Used.Contains(c) && level.RoomSpans(c, room).Count > 0))
                    continue;
                var snap = Snap(level, cells.Keys);
                foreach (var kv in cells)
                {
                    var open = new List<Span>();
                    int z = room.Floor;
                    foreach (var (lo, hi) in kv.Value.OrderBy(t => t.Lo).ThenBy(t => t.Hi))
                    {
                        if (room.Floor + lo > z) open.Add(new Span(z, room.Floor + lo));
                        z = room.Floor + hi;
                    }
                    if (z < room.Ceil) open.Add(new Span(z, room.Ceil));
                    level.ReplaceIn(kv.Key, room.Slot.Lo, room.Slot.Hi, open.Where(sp => sp.Ceil - sp.Floor >= 16));
                }
                if (!BaseConnected(level, room))
                {
                    Restore(level, snap);
                    continue;
                }
                string part = $"room:{room.Id}/landmark";
                foreach (var c in cells.Keys)
                {
                    level.PartSolid[c] = part;
                    if (level.Spans.TryGetValue(c, out var sl) && sl.Count > 0) level.PartCells[c] = part;
                }
                ctx.Used.UnionWith(cells.Keys);
                ctx.Used.UnionWith(ring);
                int top = shape.Values.SelectMany(r => r).Max(t => t.Hi);
                var info = new Dictionary<string, object>
                {
                    ["shape"] = name, ["centre"] = new List<double> { p.X + (scale - 1) / 2.0, p.Y + (scale - 1) / 2.0 },
                    ["top"] = room.Floor + top, ["cells"] = cells.Count, ["scale"] = scale,
                };
                room.Meta["landmark"] = info;
                room.Features.Add(name);
                return info;
            }
            return null;
        }
    }
}
