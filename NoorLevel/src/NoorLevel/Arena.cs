using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>Arena mode: deathmatch levels (arena.py; the Quake 3 / Unreal Tournament tradition).
    ///
    /// A campaign level is a route: start, keys, exit. A deathmatch level is a circuit that players run
    /// round and round, so it is judged by different things:
    /// <list type="bullet">
    /// <item>No dead ends, no traps. Every spot must lead back to every other (the validator's arena
    /// check). The graph has no locks, start or exit; dead-end rooms get a second way out.</item>
    /// <item>Spawns spread out, in the outer rooms, one per room first, facing into the room.</item>
    /// <item>Where the items would go. BadLevel places power-ups, weapons, ammo, health and armour;
    /// NoorLevel places none of them (the game fills the space) but keeps the plan they come from,
    /// because the spawns are spread by it: the power-up rooms (the most central room first, the others
    /// as far from it as the central half allows) and the weapon spots (spread so every spawn is about
    /// as far from its nearest gun as every other; the strongest in the most central rooms). Both are
    /// in <c>level.Arena</c> (<c>power_rooms</c>, <c>weapon_spots</c>) for the game to fill.</item>
    /// <item>High ground by jump pad (Decorate.FTower, cfg.JumpPads): every arena gets one tower.</item>
    /// </list>
    /// Distances are walking distances (cells, by the validator's own moves, drops and links), room anchor
    /// to room anchor. The result is in <c>level.Arena</c>: how even the spread is (<c>fairness</c>: the
    /// nearest-weapon distance of the worst-placed spawn over the best-placed; 1.0 is perfectly even).</summary>
    public static class Arena
    {
        // generic weapon roles, weakest first; exporters map them to each game's guns
        public static readonly string[] Weapons = { "shotgun", "rapid", "grenade_launcher", "rocket_launcher", "heavy_rapid", "beam", "rail" };
        public static readonly Dictionary<string, int> Power = new Dictionary<string, int>
            { ["shotgun"] = 1, ["rapid"] = 1, ["grenade_launcher"] = 2, ["heavy_rapid"] = 2, ["rocket_launcher"] = 3, ["beam"] = 3, ["rail"] = 3 };
        public static readonly HashSet<string> Cleared = new HashSet<string>
            { "enemy", "health", "ammo", "armor", "megahealth", "weapon", "player_start" };
        public const int Far = 1000000;

        /// <summary>Velocity for a jump pad throwing a player from pad onto target (game axes), peaking clear
        /// units above the target. Returns (speed, pitch, yaw) as Quake's trigger_push wants them: velocity =
        /// speed * 10 along (pitch, yaw), pitch negative up. (The map exporter has its own copy.)</summary>
        public static (double Speed, double Pitch, double Yaw) JumpLaunch(Vec3 pad, Vec3 target, double gravity = 800.0, double clear = 64.0)
        {
            double dx = target.X - pad.X, dy = target.Y - pad.Y;
            double rise = target.Z - pad.Z;
            double d = Math.Sqrt(dx * dx + dy * dy);
            double apex = Math.Max(rise, 0) + clear;
            double vz = Math.Sqrt(2 * gravity * apex);
            double t = vz / gravity + Math.Sqrt(2 * (apex - rise) / gravity);
            double vx = d / t;
            double speed = Math.Sqrt(vx * vx + vz * vz) / 10.0;
            double pitch = -Math.Atan2(vz, vx) * 180 / Math.PI;
            double yaw = Tower.PyMod(Math.Atan2(dy, dx) * 180 / Math.PI, 360);
            return (Math.Round(speed, 1), Math.Round(pitch, 1), Math.Round(yaw, 1));
        }

        public static bool Flat(Level level, Room room, Cell c)
        {
            var spans = level.RoomSpans(c, room);
            return spans.Count == 1 && spans[0].Floor == room.Floor && !level.Diag.ContainsKey(c);
        }

        public static Cell? Anchor(Level level, Room room)
        {
            double cx = room.X + room.W / 2.0, cy = room.Y + room.H / 2.0;
            var cands = room.Cells().Where(c => Flat(level, room, c)).ToList();
            if (cands.Count == 0)
                cands = room.Cells().Where(c => level.RoomSpans(c, room).Count > 0 && !level.Diag.ContainsKey(c)).ToList();
            if (cands.Count == 0) return null;
            return MinBy(cands, c => Math.Pow(c.X + 0.5 - cx, 2) + Math.Pow(c.Y + 0.5 - cy, 2));
        }

        static T MinBy<T>(IEnumerable<T> xs, Func<T, double> key)
        {
            bool first = true; T best = default; double bk = 0;
            foreach (var x in xs) { double k = key(x); if (first || k < bk) { best = x; bk = k; first = false; } }
            return best;
        }

        static T MaxBy<T>(IEnumerable<T> xs, Func<T, double> key)
        {
            bool first = true; T best = default; double bk = 0;
            foreach (var x in xs) { double k = key(x); if (first || k > bk) { best = x; bk = k; first = false; } }
            return best;
        }

        /// <summary>Walking steps between room anchors (directed: drops are one way).</summary>
        public static Dictionary<int, Dictionary<int, int>> Distances(Level level, List<Room> rooms, Dictionary<int, Cell> anchors)
        {
            var links = Validator.LinkEdges(level);
            var nodes = new Dictionary<int, (int, int, int)>();
            foreach (var r in rooms)
            {
                var c = anchors[r.Id];
                int f = level.RoomSpans(c, r)[0].Floor;
                var n = Validator.NodeAt(level, new Vec3((c.X + 0.5) * level.CellSize, (c.Y + 0.5) * level.CellSize, f + 24));
                if (n != null) nodes[r.Id] = n.Value;
            }
            var where = new Dictionary<(int, int, int), int>();
            foreach (var kv in nodes) where[kv.Value] = kv.Key;
            var D = new Dictionary<int, Dictionary<int, int>>();
            var none = new HashSet<Cell>();
            foreach (var r in rooms)
            {
                var found = new Dictionary<int, int>();
                if (nodes.TryGetValue(r.Id, out var start))
                {
                    var dist = new Dictionary<(int, int, int), int> { [start] = 0 };
                    var frontier = new List<(int, int, int)> { start };
                    found[r.Id] = 0;
                    while (frontier.Count > 0 && found.Count < rooms.Count)
                    {
                        var nxt = new List<(int, int, int)>();
                        foreach (var n in frontier)
                            foreach (var m in Validator.Successors(level, n, none, links))
                            {
                                if (dist.ContainsKey(m)) continue;
                                dist[m] = dist[n] + 1;
                                nxt.Add(m);
                                if (where.TryGetValue(m, out var rid) && !found.ContainsKey(rid)) found[rid] = dist[m];
                            }
                        frontier = nxt;
                    }
                }
                D[r.Id] = rooms.ToDictionary(q => q.Id, q => found.TryGetValue(q.Id, out var d) ? d : Far);
                D[r.Id][r.Id] = (r.W + r.H) / 4;              // across its own room: not nothing
            }
            return D;
        }

        public static int SpanIndex(Level level, Room room, Cell c)
        {
            var sp = level.RoomSpans(c, room)[0];
            return level.Spans[c].IndexOf(sp);
        }

        /// <summary>Flat floor cells of the room off the doorways (where Decorate.PickFree would pick).</summary>
        public static List<Cell> FreeCells(Level level, RoomCtx ctx, HashSet<Cell> taken)
        {
            var r = ctx.Room;
            var outp = new List<Cell>();
            foreach (var c in r.Cells())
            {
                var spans = level.RoomSpans(c, r);
                if (spans.Count == 1 && spans[0].Floor == r.Floor && !taken.Contains(c) && !ctx.Clear.Contains(c) && !level.Diag.ContainsKey(c))
                    outp.Add(c);
            }
            return outp;
        }

        /// <summary>For every spot: the walk (steps) to the nearest target, moving the way a player moves
        /// (a reverse search over the validator's moves, drops and links).</summary>
        public static Dictionary<(int, int, int), int> Field(Level level, IEnumerable<Vec3> targets)
        {
            var links = Validator.LinkEdges(level);
            var none = new HashSet<Cell>();
            var rev = new Dictionary<(int, int, int), List<(int, int, int)>>();
            foreach (var kv in level.Spans)
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    var n = (kv.Key.X, kv.Key.Y, i);
                    foreach (var m in Validator.Successors(level, n, none, links))
                    {
                        if (!rev.TryGetValue(m, out var l)) rev[m] = l = new List<(int, int, int)>();
                        l.Add(n);
                    }
                }
            var dist = new Dictionary<(int, int, int), int>();
            var frontier = new List<(int, int, int)>();
            foreach (var t in targets)
            {
                var n = Validator.NodeAt(level, t);
                if (n == null) continue;
                dist[n.Value] = 0;
                frontier.Add(n.Value);
            }
            while (frontier.Count > 0)
            {
                var nxt = new List<(int, int, int)>();
                foreach (var n in frontier)
                    if (rev.TryGetValue(n, out var ms))
                        foreach (var m in ms)
                            if (!dist.ContainsKey(m)) { dist[m] = dist[n] + 1; nxt.Add(m); }
                frontier = nxt;
            }
            return dist;
        }

        sealed class Cand
        {
            public int D;
            public (string Kind, int Id) Place;
            public int Cap;
            public Cell C;
            public Vec3 Pos;
            public (double X, double Y) Face;
        }

        /// <summary>The distance band [lo, 1.5 lo] (lo at least 6 steps) holding candidates in the most places.</summary>
        static (double Lo, double Hi) BestBand(List<Cand> cands, int want)
        {
            (double, double) best = (6, 9);
            (int, int, int) bestKey = (-1, 0, 0);
            bool any = false;
            foreach (var lo in cands.Select(x => Math.Max(6, x.D)).Distinct().OrderBy(x => x))
            {
                double hi = lo * 1.5;
                var places = new HashSet<(string, int)>(cands.Where(x => lo <= x.D && x.D <= hi).Select(x => x.Place));
                int rooms = places.Count(p => p.Item1 == "room");
                var key = (Math.Min(places.Count, want), rooms, -lo);   // enough places, as many rooms as can be, a short walk
                if (!any || key.CompareTo(bestKey) > 0) { best = (lo, hi); bestKey = key; any = true; }
            }
            return best;
        }

        public static void Populate(Level level, Dictionary<int, RoomCtx> ctxs, Rng rng)
        {
            var cfg = level.Config;
            level.Entities.RemoveAll(e => Cleared.Contains(e.Kind));
            var rooms = level.Rooms.Where(r => !r.IsConnector && r.Kind != "overlook" && r.Kind != "secret").ToList();
            var anchorsN = rooms.ToDictionary(r => r.Id, r => Anchor(level, r));
            rooms = rooms.Where(r => anchorsN[r.Id] != null).ToList();
            if (rooms.Count == 0) return;
            var anchors = rooms.ToDictionary(r => r.Id, r => anchorsN[r.Id].Value);
            if (cfg.JumpPads > 0 && !level.Entities.Any(e => e.Kind == "jumppad"))
            {
                // every arena gets its high ground: a tower in the biggest room that takes one
                foreach (var r in rooms.OrderBy(r => -r.W * r.H))
                    if ((r.Kind == "combat" || r.Kind == "arena" || r.Kind == "hub") && Decorate.FTower(ctxs[r.Id], rng))
                        break;
            }
            var D = Distances(level, rooms, anchors);
            int n = rooms.Count;
            var cen = rooms.ToDictionary(r => r.Id, r => rooms.Sum(q => (double)Math.Min(D[r.Id][q.Id], Far) + Math.Min(D[q.Id][r.Id], Far)) / n);
            var taken = rooms.ToDictionary(r => r.Id, r => new HashSet<Cell>(ctxs[r.Id].Pads));
            var ents = level.Entities;

            List<Cell> High(Room r)
            {
                var ctx = ctxs[r.Id];
                return ctx.TopCells.Concat(ctx.DeckCells)
                    .Where(c => !taken[r.Id].Contains(c) && level.RoomSpans(c, r).Count > 0 && !level.Diag.ContainsKey(c)).ToList();
            }

            // a free cell in the room (on its high ground if up and it has some): its position
            (Cell? C, Vec3? Pos) Spot(Room r, bool up = false, double z = 0.0)
            {
                if (up)
                {
                    var cells = High(r);
                    if (cells.Count > 0)
                    {
                        var hc = rng.Choice(cells);
                        taken[r.Id].Add(hc);
                        return (hc, Decorate.At(level, r, hc, top: true, zOff: z));
                    }
                }
                var c = Decorate.PickFree(ctxs[r.Id], rng, taken[r.Id]);
                if (c == null) return (null, null);
                taken[r.Id].Add(c.Value);
                return (c, Decorate.At(level, r, c.Value, zOff: z));
            }

            // --- power-ups: the centre first, then spread over the central half ------------------------
            // (no pickups in NoorLevel: the rooms and spots are kept as facts, and the spawns keep off them)
            int nPower = n < 5 ? 1 : n < 9 ? 2 : 3;
            var kinds = nPower == 3 ? new[] { "quad", "armor_heavy", "megahealth" } : new[] { "armor_heavy", "megahealth" }.Take(nPower).ToArray();
            var order = rooms.OrderBy(r => cen[r.Id] * (High(r).Count > 0 ? 0.85 : 1.0)).ToList();
            var central = order.Take(Math.Max(1, (n + 1) / 2)).ToList();
            var powerRooms = new List<Room> { order[0] };
            while (powerRooms.Count < nPower)
            {
                var rest = central.Where(r => !powerRooms.Contains(r)).ToList();
                if (rest.Count == 0) rest = rooms.Where(r => !powerRooms.Contains(r)).ToList();
                if (rest.Count == 0) break;
                powerRooms.Add(MaxBy(rest, r => powerRooms.Min(p => D[p.Id][r.Id] + D[r.Id][p.Id])));
            }
            var powerups = new List<Dictionary<string, object>>();
            for (int i = 0; i < Math.Min(kinds.Length, powerRooms.Count); i++)
            {
                var r = powerRooms[i];
                var (c, pos) = Spot(r, up: true);
                if (pos != null)
                    powerups.Add(new Dictionary<string, object>
                    {
                        ["kind"] = kinds[i], ["room"] = r.Id, ["high"] = ctxs[r.Id].TopCells.Contains(c.Value),
                        ["pos"] = new List<double> { pos.Value.X, pos.Value.Y, pos.Value.Z },
                    });
            }

            // --- weapons: spread over the map, the strongest in the most central rooms ----------------
            // (the spots only: the spawns are spread by their distance to them)
            int nWeap = Math.Max(3, Math.Min(Weapons.Length, n / 2 + 1));
            var guns = Weapons.Take(nWeap).OrderBy(w => Power[w]).ToList();
            var pool = rooms.Where(r => !powerRooms.Contains(r)).ToList();
            if (pool.Count == 0) pool = rooms;
            var farOut = MaxBy(pool, r => cen[r.Id]);
            var chosen = new List<Room> { farOut };
            while (chosen.Count < Math.Min(nWeap, pool.Count))
            {
                var rest = pool.Where(r => !chosen.Contains(r)).ToList();
                chosen.Add(MaxBy(rest, r => chosen.Min(c => D[c.Id][r.Id] + D[r.Id][c.Id]) + rng.Random() * 4));
            }
            chosen = chosen.OrderBy(r => -cen[r.Id]).ToList();          // outer first: the weak guns there
            while (chosen.Count < guns.Count) chosen.Add(rng.Choice(rooms));
            var weaponSpots = new List<Dictionary<string, object>>();
            var gunPos = new List<Vec3>();
            for (int i = 0; i < Math.Min(guns.Count, chosen.Count); i++)
            {
                string gun = guns[i];
                var r = chosen[i];
                var (_, pos) = Spot(r, up: Power[gun] == 3 && rng.Random() < 0.5);
                if (pos == null) continue;
                gunPos.Add(pos.Value);
                weaponSpots.Add(new Dictionary<string, object>
                {
                    ["type"] = gun, ["room"] = r.Id, ["pos"] = new List<double> { pos.Value.X, pos.Value.Y, pos.Value.Z },
                });
                // its ammo: in the nearest other room, or beside it (a pickup: left out; the cell stays taken)
                var others = rooms.Where(q => q != r).ToList();
                var qr = others.Count > 0 ? MinBy(others, q => D[r.Id][q.Id]) : r;
                Spot(qr);
            }

            // --- spawns: every one about as far from its nearest gun as the others ----------------------
            // places: rooms (a small room holds one spawn, a hall a few) and the straight, level stretches
            // of corridors (one each); each candidate keeps its walk to the nearest gun
            var toGun = Field(level, gunPos);
            var cands = new List<Cand>();
            foreach (var r in rooms)
            {
                if (powerRooms.Contains(r) && rooms.Count > powerRooms.Count + 2)
                    continue;                                  // not on top of the power-ups
                double cx = r.X + r.W / 2.0, cy = r.Y + r.H / 2.0;
                int cap = 1 + (r.W * r.H) / 250;
                foreach (var c in FreeCells(level, ctxs[r.Id], taken[r.Id]))
                {
                    if (!toGun.TryGetValue((c.X, c.Y, SpanIndex(level, r, c)), out var d) || d >= Far) continue;
                    var face = (X: cx - (c.X + 0.5), Y: cy - (c.Y + 0.5));
                    if (Math.Abs(face.X) + Math.Abs(face.Y) < 2 && ctxs[r.Id].Doors.Count > 0)   // in the middle: face a way out
                    {
                        var dd = ctxs[r.Id].Doors[0];
                        face = (dd.X - c.X, dd.Y - c.Y);
                    }
                    cands.Add(new Cand { D = d, Place = ("room", r.Id), Cap = cap, C = c, Pos = Decorate.At(level, r, c, zOff: 32.0), Face = face });
                }
            }
            foreach (var corr in level.Corridors)
            {
                var path = corr.Path;
                var floors = corr.Floors;
                for (int i = 4; i < path.Count - 4; i++)
                {
                    var c = path[i];
                    level.Spans.TryGetValue(c, out var spans);
                    if (level.Diag.ContainsKey(c) || level.FloorSlope.ContainsKey(c) || spans == null || spans.Count != 1 || spans[0].Floor != floors[i]
                        || floors[i - 1] != floors[i] || floors[i + 1] != floors[i])
                        continue;
                    if (level.OwnerOf(c, spans[0]) != $"corridor:{corr.Id}" || corr.Radius < 1) continue;
                    if (!toGun.TryGetValue((c.X, c.Y, 0), out var d) || d >= Far) continue;
                    var face = ((double)(path[i + 1].X - c.X), (double)(path[i + 1].Y - c.Y));
                    var pos = new Vec3((c.X + 0.5) * level.CellSize, (c.Y + 0.5) * level.CellSize, floors[i] + 32.0);
                    cands.Add(new Cand { D = d, Place = ("corr", corr.Id), Cap = 1, C = c, Pos = pos, Face = face });
                }
            }
            int nSpawn = Math.Max(4, Math.Min(12, n));
            var band = BestBand(cands, nSpawn);
            var spawns = new List<Cand>();
            foreach (var (lo, hi) in new[] { band, (band.Lo / 1.5, band.Hi * 1.4), (0.0, (double)Far) })
            {
                var pick = cands.Where(x => lo <= x.D && x.D <= hi).ToList();
                while (spawns.Count < nSpawn && pick.Count > 0)
                {
                    var count = new Dictionary<(string, int), int>();
                    foreach (var sp in spawns) count[sp.Place] = count.TryGetValue(sp.Place, out var k) ? k + 1 : 1;
                    // an empty place first, a room first, far from the others
                    (int, int, int) Score(Cand x)
                    {
                        int dOther = spawns.Count == 0 ? 99 : spawns.Min(sp => Math.Abs(x.C.X - sp.C.X) + Math.Abs(x.C.Y - sp.C.Y));
                        return (count.ContainsKey(x.Place) ? 0 : 1, x.Place.Kind == "room" ? 1 : 0, Math.Min(dOther, 30));
                    }
                    int bi = 0;
                    var bs = Score(pick[0]);
                    for (int i = 1; i < pick.Count; i++)
                    {
                        var s = Score(pick[i]);
                        if (s.CompareTo(bs) > 0) { bs = s; bi = i; }
                    }
                    var xx = pick[bi];
                    pick.RemoveAt(bi);
                    if (spawns.Any(sp => Math.Abs(xx.C.X - sp.C.X) + Math.Abs(xx.C.Y - sp.C.Y) < 4)) continue;
                    if ((count.TryGetValue(xx.Place, out var cnt) ? cnt : 0) >= xx.Cap && lo > 0) continue;
                    if (xx.Place.Kind == "room") taken[xx.Place.Id].Add(xx.C);
                    spawns.Add(xx);
                }
                if (spawns.Count >= nSpawn) break;
            }
            int firstAngle = 0;
            for (int i = 0; i < spawns.Count; i++)
            {
                var sp = spawns[i];
                int angle = (int)Math.Round(Math.Atan2(sp.Face.Y, sp.Face.X) * 180 / Math.PI / 45.0) * 45;
                angle = ((angle % 360) + 360) % 360;
                if (i == 0) firstAngle = angle;
                ents.Add(new Entity("spawn", sp.Pos).With("angle", angle).With(sp.Place.Kind == "room" ? "room" : "corridor", sp.Place.Id));
            }
            if (spawns.Count > 0)
                ents.Add(new Entity("player_start", spawns[0].Pos).With("angle", firstAngle));

            // --- health everywhere, armour shards between: pickups, left out --------------------------

            int dead = rooms.Count(r => level.Graph.Neighbors(r.Node).Count() <= 1);
            // the spread as built: each spawn's walk to its nearest gun spot, in cells
            var near = spawns.Select(s => s.D).ToList();
            double fair = near.Count > 0 ? Math.Round((double)near.Max() / Math.Max(6, near.Min()), 2) : 1.0;
            level.Arena["spawns"] = spawns.Count;
            level.Arena["weapon_spots"] = weaponSpots;
            level.Arena["power_rooms"] = powerups;
            level.Arena["fairness"] = fair;
            level.Arena["dead_ends"] = dead;
            level.Arena["jump_pads"] = ents.Count(e => e.Kind == "jumppad");
            level.Arena["nearest_weapon"] = near;
            level.Notes.Add($"arena: {spawns.Count} spawns, {weaponSpots.Count} weapon spots, " +
                            $"{powerups.Count} power-up spots, fairness {fair.ToString(System.Globalization.CultureInfo.InvariantCulture)}, dead ends {dead}");
        }
    }
}
