using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>Dungeon dressing (dungeon.py): torches, grates and traps.
    /// <list type="bullet">
    /// <item>Torches (<c>cfg.Torches</c>): instead of lights hanging in the middle of a room, torches on its
    /// walls, about every 6 cells round the room, 96 units up, warm and dim; never beside a doorway. A big
    /// room keeps one faint light in the middle so its floor isn't black.</item>
    /// <item>Grates (<c>cfg.Skylights</c>, a chance per room): a 2x2 or 3x3 opening in the ceiling to the sky,
    /// with a shaft of light under it, where nothing is stacked above the room. Done after the vaults are
    /// shaped, so the grate cuts through them (<c>room:N/grate</c>).</item>
    /// <item>Traps (<c>cfg.Traps</c>, a chance per corridor): markers on a straight, level stretch of corridor: a
    /// swinging blade, dart holes, a spike floor. Markers only (<c>trap</c> entities with a type and an axis):
    /// they never change the geometry, so they can't block a route.</item>
    /// </list></summary>
    public static partial class Dungeon
    {
        public static readonly int TorchUp = 96;
        public static readonly int TorchLight = 170;
        public static readonly string[] Traps = { "blade", "darts", "spikes" };

        /// <summary>level.room_spans as in level.py: the spans within the room's vertical slot.</summary>
        static List<Span> SlotSpans(Level level, Cell c, Room room) => level.SpansIn(c, room.Slot.Lo, room.Slot.Hi).ToList();

        public static int PlaceTorches(Level level, Room room)
        {
            int s = level.CellSize;
            double cx = room.X + room.W / 2.0, cy = room.Y + room.H / 2.0;
            var cands = new List<(double Ang, Cell C, (int, int) D)>();
            foreach (var c in room.Cells())
            {
                var sp = SlotSpans(level, c, room);
                if (sp.Count == 0 || level.Diag.ContainsKey(c) || sp[0].Floor > room.Floor + 16 || sp[0].Ceil < room.Floor + TorchUp + 40)
                    continue;
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var n = c.Offset(dx, dy);
                    if (level.SpansIn(n, room.Floor + TorchUp - 8, room.Floor + TorchUp + 8).Any() || level.Diag.ContainsKey(n))
                        continue;                                 // open (or half open) that way: not a wall
                    // never beside a doorway: no corridor within 2 cells
                    bool nearDoor = false;
                    for (int a = -2; a <= 2 && !nearDoor; a++)
                        for (int b = -2; b <= 2; b++)
                            if ((level.OwnerAt(c.Offset(a, b), room.Floor + 24) ?? "").StartsWith("corridor:")) { nearDoor = true; break; }
                    if (nearDoor) continue;
                    cands.Add((Math.Atan2(c.Y + 0.5 - cy, c.X + 0.5 - cx), c, (dx, dy)));
                    break;
                }
            }
            cands = cands.OrderBy(t => t.Ang).ThenBy(t => t.C).ToList();
            int perimeter = 2 * (room.W + room.H);
            int want = Math.Max(2, Math.Min(14, perimeter / 6));
            var picked = new List<(Cell C, (int, int) D)>();
            foreach (var (_, c, d) in cands)
            {
                if (picked.All(p => Math.Abs(c.X - p.C.X) + Math.Abs(c.Y - p.C.Y) >= 5)) picked.Add((c, d));
                if (picked.Count >= want) break;
            }
            foreach (var (c, (dx, dy)) in picked)
            {
                int z = Math.Min(room.Floor + TorchUp, SlotSpans(level, c, room)[0].Ceil - 32);
                var pos = new Vec3((c.X + 0.5 + dx * 0.3) * s, (c.Y + 0.5 + dy * 0.3) * s, z);
                level.Entities.Add(new Entity("torch", pos).With("intensity", TorchLight).With("wall", new List<int> { dx, dy }).With("room", room.Id));
            }
            if (room.W * room.H >= 160)                          // a big room: one faint light in the middle
            {
                var c = Decorate.NearestOpen(level, room, cx, cy);
                var last = SlotSpans(level, c, room).Last();
                int bottom = last.Floor, top = last.Ceil;
                level.Entities.Add(new Entity("light", new Vec3((c.X + 0.5) * s, (c.Y + 0.5) * s,
                                                                Math.Min(top - 24, Math.Max(bottom, room.Floor) + 160))).With("intensity", 140));
            }
            return picked.Count;
        }

        public static int AddSkylights(Level level)
        {
            var cfg = level.Config;
            double chance = cfg.Skylights;
            if (chance <= 0) return 0;
            int s = level.CellSize;
            int made = 0;
            foreach (var room in level.Rooms)
            {
                if (room.IsConnector || room.Kind == "courtyard" || room.Kind == "secret" || room.Kind == "overlook" || Math.Min(room.W, room.H) < 7)
                    continue;
                var rng = Rng.FromLabel($"{cfg.Seed}/{cfg.Variant}/grate/{room.Id}");
                if (rng.Random() >= chance) continue;
                int k = Math.Min(room.W, room.H) >= 12 ? 3 : 2;
                int cx = room.X + (room.W - k) / 2, cy = room.Y + (room.H - k) / 2;
                bool ok = false;
                int ox = 0, oy = 0;
                List<Cell> cells = null;
                foreach (var (ox_, oy_) in new[] { (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1), (2, 0), (-2, 0), (0, 2), (0, -2) })
                {
                    ox = ox_; oy = oy_;
                    cells = new List<Cell>();
                    for (int i = 0; i < k; i++)
                        for (int j = 0; j < k; j++) cells.Add(new Cell(cx + ox + i, cy + oy + j));
                    ok = true;
                    foreach (var c in cells)
                    {
                        var sp = SlotSpans(level, c, room);
                        // the room's own span must be the column's top one: nothing stacked above
                        if (!room.Contains(c) || level.Diag.ContainsKey(c) || level.Sky.Contains(c) || sp.Count == 0
                            || !level.Spans[c][level.Spans[c].Count - 1].Equals(sp[sp.Count - 1]))
                        {
                            ok = false;
                            break;
                        }
                    }
                    if (ok) break;
                }
                if (!ok) continue;
                string tag = $"room:{room.Id}";
                foreach (var c in cells)
                {
                    var last = level.Spans[c][level.Spans[c].Count - 1];
                    int f = last.Floor, top = last.Ceil;
                    if (top < room.Ceil)                         // a vault dips here: the grate cuts up through it
                    {
                        level.ReplaceIn(c, f, top, new[] { new Span(f, room.Ceil) });
                        level.Owners[c] = (level.Owners.TryGetValue(c, out var os) ? os : new List<Owner>())
                            .Select(o => new Owner(o.Lo, o.Name == tag ? room.Ceil : o.Hi, o.Name)).ToList();
                    }
                    level.CeilSlope.Remove(c);
                    level.Sky.Add(c);
                    level.PartCeiling[c] = $"room:{room.Id}/grate";
                }
                double mx = (cx + ox + k / 2.0) * s;
                double my = (cy + oy + k / 2.0) * s;
                level.Entities.Add(new Entity("light", new Vec3(mx, my, room.Ceil - 24)).With("intensity", 320).With("shaft", true));
                room.Features.Add("grate");
                made++;
            }
            if (made > 0) level.Notes.Add($"grates: {made}");
            return made;
        }

        public static int AddTraps(Level level)
        {
            var cfg = level.Config;
            double chance = cfg.Traps;
            if (chance <= 0) return 0;
            int s = level.CellSize;
            int made = 0;
            foreach (var corr in level.Corridors)
            {
                if (corr.Vent || corr.Path.Count < 10) continue;
                var rng = Rng.FromLabel($"{cfg.Seed}/{cfg.Variant}/trap/{corr.Id}");
                if (rng.Random() >= chance) continue;
                var path = corr.Path; var floors = corr.Floors;
                var spots = new List<int>();
                for (int i = 4; i < path.Count - 4; i++)
                    if (Layout.Straight(path, i, corr.Radius, axisOnly: true) && floors[i - 1] == floors[i] && floors[i] == floors[i + 1])
                        spots.Add(i);
                if (spots.Count == 0) continue;
                int at = rng.Choice(spots);
                var c = path[at]; int f = floors[at];
                string axis = path[at + 1].X != c.X ? "x" : "y";
                string kind = rng.Choice(Traps);
                level.Entities.Add(new Entity("trap", new Vec3((c.X + 0.5) * s, (c.Y + 0.5) * s, f + 24))
                    .With("type", kind).With("axis", axis).With("corridor", corr.Id).With("width", 2 * corr.Radius + 1));
                made++;
            }
            if (made > 0) level.Notes.Add($"traps: {made}");
            return made;
        }
    }
}
