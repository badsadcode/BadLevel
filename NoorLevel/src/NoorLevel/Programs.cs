using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>Room programs: what a room is *for* (programs.py; facility levels, the Half-Life /
    /// Abiotic Factor tradition).
    ///
    /// A campaign level's rooms have gameplay roles (start, fight, key, arena...). A facility's rooms also
    /// have a job: an office, a lab, a server room, a warehouse, a reactor hall. The job decides what fills
    /// the room, as blockout furniture an artist can find by name and swap for real props
    /// (<c>room:5/desks</c>, <c>room:9/server racks</c>):
    /// <list type="bullet">
    /// <item>Sectors. Each lock zone of the mission is a sector of the complex (admin, research, storage,
    /// maintenance, reactor): the keycard doors are the checkpoints between them.</item>
    /// <item>Programs. Each room gets a program from its sector and size: the start is the lobby, the key
    /// room a security office, the exit the transit station, the sector's biggest room its hall, the rest
    /// the sector's everyday rooms that fit their size.</item>
    /// <item>Furniture. Rows and grids of blocks, never closer than 2 cells apart, with a 2-cell walkway
    /// round the walls and breaks every few cells; doorways keep their clear zone. Low pieces (32-48 units)
    /// are cover you can see over; tall ones are solid to the ceiling.</item>
    /// </list>
    /// cfg.Programs turns it on (the facility presets); off, nothing changes.</summary>
    public static class Programs
    {
        public static readonly string[] Sectors = { "admin", "research", "storage", "maintenance", "reactor" };

        static Dictionary<string, (string[] Everyday, string Hall)> T(params (string, string[], string)[] xs) =>
            xs.ToDictionary(x => x.Item1, x => (x.Item2, x.Item3));

        public static readonly Dictionary<string, (string[] Everyday, string Hall)> SectorPrograms = T(   // everyday rooms (weighted by repetition), the big hall
            ("admin", new[] { "office", "office", "conference", "cafeteria" }, "atrium"),
            ("research", new[] { "lab", "lab", "server", "office" }, "test_chamber"),
            ("storage", new[] { "storage", "storage", "loading", "office" }, "warehouse"),
            ("maintenance", new[] { "pump", "workshop", "server" }, "boiler"),
            ("reactor", new[] { "control", "pump", "lab" }, "reactor"));

        public static readonly Dictionary<string, int> MinSide = new Dictionary<string, int>
        {
            ["conference"] = 9, ["cafeteria"] = 10, ["storage"] = 9, ["loading"] = 10, ["server"] = 8, ["lab"] = 8,
            ["workshop"] = 8, ["pump"] = 9, ["control"] = 8, ["office"] = 7,
            ["scriptorium"] = 8, ["alchemy lab"] = 8, ["summoning chamber"] = 9, ["dormitory"] = 8, ["study"] = 6,
            ["library"] = 8, ["armory"] = 8, ["cells"] = 9, ["crypt"] = 8, ["storeroom"] = 7, ["guardroom"] = 7, ["shrine"] = 8,
            ["quarters"] = 7, ["galley"] = 8, ["medbay"] = 8, ["hydroponics"] = 8, ["moon_pool"] = 10, ["dive_locker"] = 7,
            ["sonar_room"] = 7, ["generator"] = 9,
        };

        public static readonly string[] RoomKinds = { "start", "combat", "arena", "hub", "supply", "key", "secret", "exit" };

        // dungeons (cfg.ProgramSet = "dungeon"): the keep above, the prison, the catacombs, the deep
        public static readonly Dictionary<string, (string[] Everyday, string Hall)> DungeonPrograms = T(
            ("keep", new[] { "guardroom", "armory", "library", "storeroom" }, "great hall"),
            ("prison", new[] { "cells", "cells", "guardroom", "storeroom" }, "pit hall"),
            ("catacombs", new[] { "crypt", "crypt", "shrine", "storeroom" }, "catacomb hall"),
            ("deep", new[] { "shrine", "crypt", "cells" }, "throne room"));

        // the wizard's tower (cfg.ProgramSet = "wizard"): the apprentices below, the study, the sanctum
        public static readonly Dictionary<string, (string[] Everyday, string Hall)> WizardPrograms = T(
            ("apprentice", new[] { "library", "storeroom", "alchemy lab", "dormitory" }, "lecture hall"),
            ("study", new[] { "library", "alchemy lab", "scriptorium", "study" }, "great library"),
            ("sanctum", new[] { "summoning chamber", "study", "alchemy lab" }, "summoning hall"));

        // deep-sea stations (cfg.ProgramSet = "station", Solitude): the habitat by the dock, the work sectors,
        // engineering at the bottom. Room programs: quarters, galley, medbay, hydroponics, moon_pool,
        // dive_locker, sonar_room, generator (plus the facility's lab, control, server, storage, workshop...)
        public static readonly Dictionary<string, (string[] Everyday, string Hall)> StationPrograms = T(
            ("habitat", new[] { "quarters", "quarters", "galley", "medbay", "hydroponics" }, "galley"),
            ("research", new[] { "lab", "hydroponics", "sonar_room", "medbay" }, "moon_pool"),
            ("dive", new[] { "dive_locker", "storage", "moon_pool", "workshop" }, "moon_pool"),
            ("operations", new[] { "sonar_room", "control", "server", "office" }, "control"),
            ("engineering", new[] { "generator", "pump", "workshop", "storage" }, "generator"));

        public static readonly Dictionary<string, string> StationFixed = new Dictionary<string, string>
            { ["start"] = "dive_locker", ["exit"] = "reactor", ["key"] = "security", ["secret"] = "closet" };

        public static readonly Dictionary<string, string> WizardFixed = new Dictionary<string, string>
            { ["start"] = "entrance hall", ["exit"] = "observatory", ["key"] = "reliquary", ["secret"] = "hidden study" };
        public static readonly Dictionary<string, string> DungeonFixed = new Dictionary<string, string>
            { ["start"] = "gatehouse", ["exit"] = "stair down", ["key"] = "vault", ["secret"] = "hidden cache" };
        public static readonly Dictionary<string, string> Fixed = new Dictionary<string, string>
            { ["start"] = "lobby", ["exit"] = "transit", ["key"] = "security", ["secret"] = "closet" };

        public static List<string> SectorsFor(int zones, int seed)
        {
            var rng = Rng.FromLabel($"{seed}/sectors");
            var middle = new List<string> { "research", "storage", "maintenance" };
            rng.Shuffle(middle);
            if (zones <= 1) return new List<string> { "research" };
            if (zones == 2) return new List<string> { "admin", rng.Choice(new[] { "research", "reactor" }) };
            return new[] { "admin" }.Concat(middle).Take(zones - 1).Concat(new[] { "reactor" }).ToList();
        }

        public static List<string> StationSectors(int zones, int seed)
        {
            var rng = Rng.FromLabel($"{seed}/sectors");
            var middle = new List<string> { "research", "dive", "operations" };
            rng.Shuffle(middle);
            if (zones <= 1) return new List<string> { "habitat" };
            if (zones == 2) return new List<string> { "habitat", rng.Choice(new[] { "research", "engineering" }) };
            return new[] { "habitat" }.Concat(middle).Take(zones - 1).Concat(new[] { "engineering" }).ToList();
        }

        public static List<string> DungeonSectors(int zones, int seed)
        {
            if (zones <= 1) return new List<string> { "catacombs" };
            if (zones == 2) return new List<string> { "keep", Rng.FromLabel($"{seed}/sectors").Choice(new[] { "prison", "catacombs" }) };
            return new[] { "keep", "prison", "catacombs" }.Take(zones - 1).Concat(new[] { "deep" }).ToList();
        }

        public static List<string> WizardSectors(int zones, int seed)
        {
            if (zones <= 3) return new[] { "apprentice", "study", "sanctum" }.Take(Math.Max(1, zones)).ToList();
            return new[] { "apprentice" }.Concat(Enumerable.Repeat("study", zones - 2)).Concat(new[] { "sanctum" }).ToList();
        }

        /// <summary>Every ordinary room gets a sector (from its lock zone) and a program.</summary>
        public static void Assign(Level level)
        {
            var cfg = level.Config;
            var g = level.Graph;
            int zones = g.Nodes.Max(n => n.Zone) + 1;
            string pset = cfg.ProgramSet;
            bool dungeon = pset == "dungeon" || pset == "wizard";      // (their own fallbacks, not the facility's)
            var (table, fixedKinds) = pset == "dungeon" ? (DungeonPrograms, DungeonFixed)
                : pset == "wizard" ? (WizardPrograms, WizardFixed)
                : pset == "station" ? (StationPrograms, StationFixed) : (SectorPrograms, Fixed);
            var names = pset == "dungeon" ? DungeonSectors(zones, cfg.Seed)
                : pset == "wizard" ? WizardSectors(zones, cfg.Seed)
                : pset == "station" ? StationSectors(zones, cfg.Seed) : SectorsFor(zones, cfg.Seed);
            var rng = Rng.FromLabel($"{cfg.Seed}/{cfg.Variant}/programs");
            var halls = new HashSet<string>();                     // one big hall per sector, the biggest room first
            foreach (var room in level.Rooms.OrderBy(r => r.Kind != "arena").ThenBy(r => -r.W * r.H))
            {
                if (room.IsConnector || !(room.Kind == "arena" || room.Kind == "hub" || room.Kind == "combat" || room.Kind == "supply"))
                    continue;
                string sector = names[Math.Min(g.Nodes[room.Node].Zone, names.Count - 1)];
                if (!halls.Contains(sector) && (room.Kind == "arena" || room.W * room.H >= 196))
                {
                    halls.Add(sector);
                    room.Meta["sector_hall"] = true;
                }
            }
            foreach (var room in level.Rooms)
            {
                var node = g.Nodes[room.Node];
                string sector = names[Math.Min(node.Zone, names.Count - 1)];
                room.Meta["sector"] = sector;
                if (room.IsConnector || Array.IndexOf(RoomKinds, room.Kind) < 0) continue;
                int side = Math.Min(room.W, room.H);
                string prog;
                bool hall = room.Meta.TryGetValue("sector_hall", out var sh) && sh is bool b && b;
                room.Meta.Remove("sector_hall");
                if (fixedKinds.TryGetValue(room.Kind, out var fx)) prog = fx;
                else if (hall) prog = table[sector].Hall;
                else
                {
                    var every = table[sector].Everyday;
                    var opts = every.Where(p => side >= (MinSide.TryGetValue(p, out var ms) ? ms : 6)).ToList();
                    if (opts.Count == 0) opts.Add(dungeon ? every[every.Length - 1] : pset == "station" ? "quarters" : "office");
                    prog = rng.Choice(opts);
                }
                // a station's character rooms (Station.cs) do the job they were built for
                switch (room.Get<string>("station_room"))
                {
                    case "moon_pool": if (pset == "station") prog = "moon_pool"; break;
                    case "hydroponics": if (pset == "station") prog = "hydroponics"; break;
                    case "dome_lab": prog = "lab"; break;
                }
                room.Meta["program"] = prog;
            }
        }

        // ------------------------------------------------------------------------------------------
        // furniture
        // ------------------------------------------------------------------------------------------

        /// <summary>Raise cells by height (a block you can see over) or make them solid (height null).</summary>
        public static int Place(RoomCtx ctx, IEnumerable<Cell> cells, string name, int? height = null)
        {
            var level = ctx.Level;
            var r = ctx.Room;
            int n = 0;
            foreach (var c in cells)
            {
                if (height == null)
                {
                    ctx.MakeSolid(c);
                    level.PartSolid[c] = $"room:{r.Id}/{name}";
                }
                else
                {
                    ctx.SetFloor(c, r.Floor + height.Value);
                    level.PartCells[c] = $"room:{r.Id}/{name}";
                }
                n++;
            }
            return n;
        }

        /// <summary>A cell furniture may take: in the room, off the doorway zones, nothing there yet.</summary>
        public static bool Ok(RoomCtx ctx, Cell c) =>
            ctx.Room.Contains(c) && !Clear(ctx).Contains(c) && !ctx.Used.Contains(c) && !ctx.Level.Diag.ContainsKey(c);

        /// <summary>Doorway zones for furniture: 2 cells round each door cell (a player box's width in front
        /// of every way in), smaller than the features' 3, so a small office still gets its desks.</summary>
        public static HashSet<Cell> Clear(RoomCtx ctx)
        {
            if (ctx.ProgClear == null)
            {
                ctx.ProgClear = new HashSet<Cell>();
                foreach (var d in ctx.Doors)
                    for (int dx = -2; dx <= 2; dx++)
                        for (int dy = -2; dy <= 2; dy++) ctx.ProgClear.Add(d.Offset(dx, dy));
            }
            return ctx.ProgClear;
        }

        /// <summary>Nothing built hard against the piece (a pier, a wall member, other furniture): no slits.
        /// A doorway's clear zone may touch it: that zone stays open anyway.</summary>
        public static bool RingOk(RoomCtx ctx, IEnumerable<Cell> cells)
        {
            var cs = new HashSet<Cell>(cells);
            foreach (var c in cs)
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        var n = c.Offset(dx, dy);
                        if (cs.Contains(n)) continue;
                        if (!ctx.Room.Contains(n) || ctx.Used.Contains(n) || ctx.Level.Diag.ContainsKey(n)) return false;
                    }
            return true;
        }

        public static int Put(RoomCtx ctx, List<Cell> cells, string name, int? height)
        {
            if (cells.Count == 0 || !RingOk(ctx, cells)) return 0;
            int n = Place(ctx, cells, name, height);
            if (ctx.Level.Config.Dressing)                         // Dressing marks each block with a prop marker
                FurnishBlocks(ctx.Room).Add((name, new List<Cell>(cells), height));
            foreach (var c in cells)
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++) ctx.Used.Add(c.Offset(dx, dy));
            return n;
        }

        /// <summary>The furniture blocks placed in a room (name, cells, height or null for solid), recorded
        /// only when cfg.Dressing is on: Dressing gives each one a prop marker tagged "blockout".</summary>
        public static List<(string Name, List<Cell> Cells, int? Height)> FurnishBlocks(Room room)
        {
            if (!(room.Meta.TryGetValue("furnish_blocks", out var v) && v is List<(string, List<Cell>, int?)> l))
                room.Meta["furnish_blocks"] = l = new List<(string, List<Cell>, int?)>();
            return l;
        }

        /// <summary>Rows along the room's long axis: thick cells deep, gap between rows, cut into pieces seg
        /// long with brk cells between them. Where a piece meets a doorway zone or other structure it is cut
        /// back to its longest free run (at least 2 cells, or 1 if thick).</summary>
        public static int Rows(RoomCtx ctx, Rng rng, string name, int thick, int gap, int seg, int brk = 2,
                               int? height = null, int margin = 2)
        {
            var r = ctx.Room;
            bool alongX = r.W >= r.H;
            int L = alongX ? r.W : r.H, D = alongX ? r.H : r.W;
            Func<int, int, Cell> toW = alongX ? (a, b) => new Cell(r.X + a, r.Y + b) : (Func<int, int, Cell>)((a, b) => new Cell(r.X + b, r.Y + a));
            int placed = 0;
            int v = margin;
            while (v + thick <= D - margin)
            {
                int u = margin + rng.Int(0, 1);
                while (u + 1 <= L - margin)
                {
                    int end = Math.Min(u + seg, L - margin);
                    // the longest run of columns whose whole depth is free
                    var best = new List<int>();
                    var run = new List<int>();
                    for (int a = u; a < end; a++)
                    {
                        bool free = true;
                        for (int j = 0; j < thick && free; j++) free = Ok(ctx, toW(a, v + j));
                        if (free)
                        {
                            run.Add(a);
                            if (run.Count > best.Count) best = new List<int>(run);
                        }
                        else run = new List<int>();
                    }
                    if (best.Count >= (thick > 1 ? 1 : 2))
                    {
                        var cells = new List<Cell>();
                        foreach (var a in best) for (int j = 0; j < thick; j++) cells.Add(toW(a, v + j));
                        placed += Put(ctx, cells, name, height);
                    }
                    u += seg + brk;
                }
                v += thick + gap;
            }
            return placed;
        }

        /// <summary>One piece near the middle of the room (a reception desk, a conference table, a core):
        /// the free spot nearest the centre.</summary>
        public static int Block(RoomCtx ctx, string name, int w, int h, int? height = null)
        {
            var r = ctx.Room;
            double cx = r.X + (r.W - w) / 2.0, cy = r.Y + (r.H - h) / 2.0;
            var spots = new List<Cell>();
            for (int x = r.X + 2; x < r.X + r.W - 1 - w; x++)
                for (int y = r.Y + 2; y < r.Y + r.H - 1 - h; y++) spots.Add(new Cell(x, y));
            foreach (var p in spots.OrderBy(p => (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)).Take(40))
            {
                var cells = new List<Cell>();
                for (int x = p.X; x < p.X + w; x++) for (int y = p.Y; y < p.Y + h; y++) cells.Add(new Cell(x, y));
                if (cells.All(c => Ok(ctx, c)))
                {
                    int n = Put(ctx, cells, name, height);
                    if (n > 0) return n;
                }
            }
            return 0;
        }

        public static readonly Dictionary<string, (string Name, int? Height)> Fallback = new Dictionary<string, (string, int?)>
        {
            ["office"] = ("desks", 32), ["lab"] = ("benches", 40), ["control"] = ("consoles", 40), ["workshop"] = ("workbenches", 40),
            ["server"] = ("server racks", null), ["storage"] = ("shelving", null), ["pump"] = ("tanks", null),
            ["security"] = ("counter", 48), ["cafeteria"] = ("tables", 32),
            ["crypt"] = ("sarcophagi", 40), ["library"] = ("bookshelves", null), ["armory"] = ("weapon racks", 64),
            ["storeroom"] = ("barrels", 48), ["cells"] = ("cell walls", null),
            ["alchemy lab"] = ("workbenches", 40), ["scriptorium"] = ("writing desks", 32), ["study"] = ("bookshelves", null),
            ["quarters"] = ("bunks", 48), ["galley"] = ("tables", 32), ["medbay"] = ("beds", 24), ["hydroponics"] = ("planters", 40),
            ["sonar_room"] = ("consoles", 40),
        };

        public static bool Chasm(RoomCtx ctx, Rng rng) => Decorate.FChasm(ctx, rng);

        public static void Furnish(RoomCtx ctx, string prog, Rng rng)
        {
            var r = ctx.Room;
            int long_ = Math.Max(r.W, r.H);
            bool wide = r.W >= r.H;
            if (r.Kind == "key")
                Decorate.FPedestal(ctx, rng);                      // the keycard on its stand
            switch (prog)
            {
                case "lobby":
                    if (!Decorate.FBalcony(ctx, rng)) Decorate.FPillars(ctx, rng);
                    Block(ctx, "reception desk", wide ? 4 : 1, wide ? 1 : 4, 40);
                    break;
                case "atrium":
                    Decorate.FBalcony(ctx, rng);
                    Decorate.FPillars(ctx, rng);
                    break;
                case "office":
                    Rows(ctx, rng, "desks", 1, 2, 2, 2, height: 32);
                    break;
                case "conference":
                    Block(ctx, "conference table", wide ? Math.Max(2, r.W - 6) : 2, wide ? 2 : Math.Max(2, r.H - 6), 32);
                    break;
                case "cafeteria":
                    Rows(ctx, rng, "tables", 2, 2, 2, 2, height: 32);
                    break;
                case "lab":
                case "control":
                    Rows(ctx, rng, prog == "lab" ? "benches" : "consoles", 1, 3, Math.Min(5, long_ - 6), 2, height: 40);
                    break;
                case "workshop":
                    Rows(ctx, rng, "workbenches", 1, 3, 3, 2, height: 40);
                    Decorate.FCrates(ctx, rng);
                    break;
                case "server":
                    Rows(ctx, rng, "server racks", 1, 2, 5, 2);
                    break;
                case "storage":
                    Rows(ctx, rng, "shelving", 2, 3, 6, 2);
                    break;
                case "warehouse":
                    Rows(ctx, rng, "shelving", 2, 4, 8, 3);
                    Decorate.FCrates(ctx, rng);
                    break;
                case "loading":
                    Decorate.FPlatform(ctx, rng);                  // the dock
                    Decorate.FCrates(ctx, rng);
                    break;
                case "pump":
                    Rows(ctx, rng, "tanks", 2, 3, 2, 3);
                    break;
                case "boiler":
                    Decorate.FPlatform(ctx, rng);
                    Rows(ctx, rng, "boilers", 3, 4, 3, 4);
                    break;
                case "reactor":
                    int core = Math.Min(r.W, r.H) >= 18 ? 6 : 4;
                    Block(ctx, "reactor core", core, core);
                    Decorate.FBalcony(ctx, rng);
                    break;
                case "test_chamber":
                    Decorate.FPit(ctx, rng);
                    Decorate.FPlatform(ctx, rng);
                    break;
                case "security":
                    Rows(ctx, rng, "counter", 1, 4, 3, 2, height: 48);
                    break;
                case "transit":
                    Decorate.FPillars(ctx, rng);
                    break;
                // --- deep-sea stations (program_set "station") ---------------------------------------
                case "quarters":
                    Rows(ctx, rng, "bunks", 1, 2, 2, 2, height: 48);
                    break;
                case "galley":
                    Rows(ctx, rng, "tables", 2, 2, 2, 2, height: 32);
                    break;
                case "medbay":
                    Rows(ctx, rng, "beds", 1, 3, 2, 2, height: 24);
                    break;
                case "hydroponics":
                    Rows(ctx, rng, "planters", 1, 2, Math.Max(2, Math.Min(6, long_ - 6)), 2, height: 40);
                    break;
                case "moon_pool":
                    if (r.Get<string>("station_room") == "moon_pool") break;   // the station layout carved its pool already
                    if (!MoonPool(ctx, rng))
                    {
                        prog = "dive_locker";                      // no room for the pool: the suits and lockers then
                        r.Meta["program"] = prog;
                    }
                    break;
                case "dive_locker":                                // the dock: kept open (suits and lockers are props)
                    break;
                case "sonar_room":
                    Rows(ctx, rng, "consoles", 1, 3, Math.Max(2, Math.Min(4, long_ - 6)), 2, height: 40);
                    break;
                case "generator":
                    Block(ctx, "generator", wide ? 4 : 3, wide ? 3 : 4);
                    break;
                default:
                    FurnishDungeonOrWizard(ctx, prog, rng);
                    break;
            }
            // closet, hidden cache, stair down, hidden study: empty
            if (Fallback.TryGetValue(prog, out var small))
            {
                string pre = $"room:{r.Id}/{small.Name}";
                if (!ctx.Level.PartCells.Values.Any(v => v.StartsWith(pre)) && !ctx.Level.PartSolid.Values.Any(v => v.StartsWith(pre)))
                    Rows(ctx, rng, small.Name, 1, 2, 2, 2, height: small.Height);   // nothing fitted: smaller pieces, closer together
            }
            r.Features.Add(prog);
        }

        /// <summary>The moon pool: a pit in the floor, three steps deep with a stair down one side (like
        /// Decorate.FPit, but from 3x3 cells up, so smaller rooms get one too), flooded to just under the deck.
        /// The game treats its bottom as open to the sea; here it stays shallow enough to walk out of (the
        /// validator's player can't swim up a shaft). The cells are part "room:N/moon pool".</summary>
        public static bool MoonPool(RoomCtx ctx, Rng rng)
        {
            var r = ctx.Room;
            var level = ctx.Level;
            int step = level.Config.StepHeight;
            if (Math.Min(r.W, r.H) < 7) return false;
            List<Cell> pit = null;
            for (int t = 0; t < 60 && pit == null; t++)
            {
                int pw = rng.Int(3, Math.Max(3, Math.Min(8, r.W - 4))), ph = rng.Int(3, Math.Max(3, Math.Min(8, r.H - 4)));
                if (r.X + 2 > r.X + r.W - 2 - pw || r.Y + 2 > r.Y + r.H - 2 - ph) continue;
                int px = rng.Int(r.X + 2, r.X + r.W - 2 - pw), py = rng.Int(r.Y + 2, r.Y + r.H - 2 - ph);
                var border = new List<Cell>();
                for (int y = py - 1; y < py + ph + 1; y++) for (int x = px - 1; x < px + pw + 1; x++) border.Add(new Cell(x, y));
                if (!border.All(c => Ok(ctx, c) && level.RoomSpans(c, r).Count == 1 && level.RoomSpans(c, r)[0].Floor == r.Floor)) continue;
                char side = rng.Choice(new[] { 'n', 's', 'e', 'w' });
                pit = new List<Cell>();
                for (int y = py; y < py + ph; y++)
                    for (int x = px; x < px + pw; x++)
                    {
                        int d = side == 'w' ? x - px : side == 'e' ? px + pw - 1 - x : side == 's' ? y - py : py + ph - 1 - y;
                        int along = side == 'w' || side == 'e' ? y - py : x - px;
                        int centre = (side == 'w' || side == 'e' ? ph : pw) / 2;
                        int lvl = d >= 2 || Math.Abs(along - centre) > 1 ? 3 : d + 1;
                        var c = new Cell(x, y);
                        ctx.SetFloor(c, r.Floor - lvl * step);
                        pit.Add(c);
                    }
                foreach (var c in border) ctx.Used.Add(c);
            }
            if (pit == null) return false;
            int surface = r.Floor - 8;
            int n = 0;
            foreach (var c in pit)
            {
                level.Water[c] = surface;
                level.PartCells[c] = $"room:{r.Id}/moon pool";
                n++;
            }
            if (n > 0) r.Meta["moon_pool"] = new Dictionary<string, object> { ["surface"] = surface, ["cells"] = n };
            return n > 0;
        }

        static void FurnishDungeonOrWizard(RoomCtx ctx, string prog, Rng rng)
        {
            var r = ctx.Room;
            bool wide = r.W >= r.H;
            // --- dungeons ---------------------------------------------------------------------------
            if ((prog == "catacomb hall" || prog == "throne room" || prog == "crypt" || prog == "guardroom") && Math.Min(r.W, r.H) >= 12
                && rng.Random() < ctx.Level.Config.Chasms * 0.25 && Chasm(ctx, rng))
                Decorate.FPillars(ctx, rng);                       // a chasm through it, and a bridge
            else if (prog == "gatehouse")
                Decorate.FPillars(ctx, rng);
            else if (prog == "guardroom")
            {
                Block(ctx, "table", 2, 2, 32);
                Rows(ctx, rng, "weapon racks", 1, 4, 3, 3, height: 64);
            }
            else if (prog == "armory")
                Rows(ctx, rng, "weapon racks", 1, 3, 4, 2, height: 64);
            else if (prog == "library")
                Rows(ctx, rng, "bookshelves", 1, 2, 5, 2);
            else if (prog == "storeroom")
            {
                Rows(ctx, rng, "barrels", 2, 2, 2, 2, height: 48);
                Decorate.FCrates(ctx, rng);
            }
            else if (prog == "cells")
                Rows(ctx, rng, "cell walls", 1, 3, 3, 2);          // stalls between solid walls: the cells
            else if (prog == "crypt")
                Rows(ctx, rng, "sarcophagi", 1, 2, 2, 2, height: 40);
            else if (prog == "shrine")
            {
                Block(ctx, "altar", wide ? 3 : 2, wide ? 2 : 3, 48);
                Decorate.FPillars(ctx, rng);
            }
            else if (prog == "vault")
                Rows(ctx, rng, "chests", 1, 3, 2, 3, height: 32);
            else if (prog == "great hall")
            {
                Decorate.FBalcony(ctx, rng);                       // goblins on the balcony
                Decorate.FPillars(ctx, rng);
                Block(ctx, "long table", wide ? Math.Max(2, r.W - 8) : 2, wide ? 2 : Math.Max(2, r.H - 8), 32);
            }
            else if (prog == "pit hall")
            {
                if (!Decorate.FChasm(ctx, rng)) Decorate.FPit(ctx, rng);   // the bridge over the spikes
                Decorate.FBalcony(ctx, rng);
            }
            else if (prog == "catacomb hall")
            {
                Decorate.FPillars(ctx, rng);
                Rows(ctx, rng, "sarcophagi", 1, 3, 2, 3, height: 40);
            }
            else if (prog == "throne room")
            {
                Decorate.FPlatform(ctx, rng);                      // the dais
                Decorate.FPillars(ctx, rng);
            }
            // --- the wizard's tower ----------------------------------------------------------------
            else if (prog == "entrance hall")
                Decorate.FPillars(ctx, rng);
            else if (prog == "alchemy lab")
            {
                Rows(ctx, rng, "workbenches", 1, 3, 4, 2, height: 40);
                Block(ctx, "cauldron", 2, 2, 32);
            }
            else if (prog == "scriptorium")
                Rows(ctx, rng, "writing desks", 1, 2, 2, 2, height: 32);
            else if (prog == "study")
            {
                Block(ctx, "desk", wide ? 3 : 2, wide ? 2 : 3, 32);
                Rows(ctx, rng, "bookshelves", 1, 4, 4, 2, margin: 2);
            }
            else if (prog == "dormitory")
                Rows(ctx, rng, "beds", 2, 2, 1, 2, height: 24);
            else if (prog == "lecture hall" || prog == "great library")
            {
                Decorate.FBalcony(ctx, rng);
                Rows(ctx, rng, prog == "great library" ? "bookshelves" : "benches", 1, 3, 5, 2,
                     height: prog == "great library" ? (int?)null : 32);
            }
            else if (prog == "summoning chamber" || prog == "summoning hall")
            {
                Block(ctx, "summoning circle", 4, 4, 8);           // a raised ring to stand on (8: a step)
                Decorate.FPillars(ctx, rng);
            }
            else if (prog == "reliquary")
                Rows(ctx, rng, "reliquaries", 1, 3, 2, 3, height: 48);
            else if (prog == "observatory")
                Block(ctx, "telescope", 2, 2, null);
        }
    }
}
