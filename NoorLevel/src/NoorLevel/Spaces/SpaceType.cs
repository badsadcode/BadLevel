using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>A space type knows how to be one kind of place (spaces/__init__.py). The core pipeline
    /// (mission graph -> placement -> routing -> carving -> validation) asks the registry for the type of
    /// each room and calls these hooks in order:
    /// <list type="bullet">
    /// <item>Propose(node, floor, ...) -> a Room (size, heights, meta) or null if it can't exist here</item>
    /// <item>AssignShape(room, node, ...) -> footprint mask / 45-degree cells after placement</item>
    /// <item>Build(level, room) -> write its open spans into the grid</item>
    /// <item>Attachment(room, other) -> (port cells or null, floor) where a corridor to other attaches</item>
    /// <item>Decorate(level, room, ctx) -> internal features (pillars, platforms, balconies...)</item>
    /// <item>Populate(level, room, ctx) -> gameplay entities (lights, start, keys...)</item>
    /// <item>Regions(room) -> semantic regions it exposes (default: itself)</item>
    /// </list>
    /// A new kind of space is one class registered in <see cref="Spaces"/>; the core does not change.
    /// Corridors and merged halls are relationships between rooms, so they have their own modules
    /// (<see cref="CorridorSpace"/>, <see cref="HallSpace"/>).
    /// The ctx hooks are <c>internal</c> while <see cref="RoomCtx"/> is internal to the assembly.</summary>
    public abstract class SpaceType
    {
        public string Name = "space";
        public string[] Kinds = Array.Empty<string>();
        public bool Vertical;            // joins storeys (a connector with a low and a high port)

        // --- planning / placement ---------------------------------------------------------------
        public virtual Room Propose(Node node, int floor, GenConfig cfg, Rng rng, bool multi) =>
            throw new NotImplementedException();

        public virtual void AssignShape(Room room, Node node, GenConfig cfg, Rng rng, bool merged) { }

        // --- geometry ---------------------------------------------------------------------------
        public virtual void Build(Level level, Room room, GenConfig cfg)
        {
            foreach (var c in room.Cells())
                level.AddSpan(c, new Span(room.Floor, room.Ceil), $"room:{room.Id}", room.Slot);
            foreach (var kv in room.WorldDiag())
            {
                level.Diag[kv.Key] = kv.Value;
                level.DiagSpan[kv.Key] = new Span(room.Floor, room.Ceil);
            }
        }

        public virtual (HashSet<Cell> Port, int Floor) Attachment(Room room, Room other) => (null, room.Floor);

        // --- content ----------------------------------------------------------------------------
        internal virtual List<List<Func<RoomCtx, Rng, bool>>> FeatureOptions(Room room) =>
            new List<List<Func<RoomCtx, Rng, bool>>> { new List<Func<RoomCtx, Rng, bool>>() };

        public virtual double CrateChance(Room room) => 0.0;

        /// <summary>"f_pillars" -> "pillars": a feature's key in feature_weights (FPillars -> pillars).</summary>
        internal static string FeatureName(Func<RoomCtx, Rng, bool> f)
        {
            var known = DecorateStubs.NameOf(f);                  // a decorate feature carried as a Func: "f_tower"
            if (known != null) return known.StartsWith("f_") ? known.Substring(2) : known;
            string n = f.Method.Name;
            if (n.Length > 1 && n[0] == 'F' && char.IsUpper(n[1])) n = n.Substring(1);
            return n.ToLowerInvariant();
        }

        /// <summary>Pick one weighted option of internal features, then maybe crates.</summary>
        internal virtual void Decorate(Level level, Room room, RoomCtx ctx, Rng rng)
        {
            var cfg = level.Config;
            var prog = room.Get<string>("program");
            if (!string.IsNullOrEmpty(prog))                     // a facility room: furnished for its job
            {
                Programs.Furnish(ctx, prog, rng);
                return;
            }
            var options = FeatureOptions(room).ToList();
            var fw = cfg.FeatureWeights;
            string k = room.Kind;
            if (cfg.Hazards > 0 && (k == "combat" || k == "arena" || k == "hub" || k == "megaroom"))
                options.Add(new List<Func<RoomCtx, Rng, bool>> { DecorateStubs.FHazard });        // a slime or lava pool
            if (cfg.Chasms > 0 && (k == "combat" || k == "arena" || k == "hub"))
                options.Add(new List<Func<RoomCtx, Rng, bool>> { DecorateStubs.FChasm });         // a spiked chasm and a bridge
            double pads = cfg.JumpPads;
            if (pads > 0 && (k == "combat" || k == "arena" || k == "hub"))
            {                                                                                  // high ground by jump pad
                options.Add(new List<Func<RoomCtx, Rng, bool>> { DecorateStubs.FTower });
                options.Add(new List<Func<RoomCtx, Rng, bool>> { DecorateStubs.FTower, DecorateStubs.FPillars });
            }
            double OptionWeight(List<Func<RoomCtx, Rng, bool>> opt)
            {
                if (opt.Count == 0) return fw.TryGetValue("none", out var nw) ? nw : 1.0;
                double w = 1.0;
                foreach (var f in opt)
                {
                    string name = FeatureName(f);
                    if (!cfg.Pillars && (name == "pillars" || name == "colonnade")) return 0.0;   // pillars off
                    double def = name == "tower" ? pads : name == "chasm" ? cfg.Chasms : 1.0;
                    w *= fw.TryGetValue(name, out var x) ? x : def;
                }
                return w;
            }
            var ws = options.Select(OptionWeight).ToList();
            var chosen = ws.Sum() > 0 ? rng.Choices(options, ws) : new List<Func<RoomCtx, Rng, bool>>();
            foreach (var feat in chosen) feat(ctx, rng);
            if (rng.Random() < Math.Min(1.0, CrateChance(room) * cfg.CrateScale))
                DecorateStubs.FCrates(ctx, rng);
        }

        internal virtual void Populate(Level level, Room room, RoomCtx ctx, Rng rng, Dictionary<string, object> info) =>
            Spaces.PlaceLights(level, room);

        public virtual List<int> Regions(Room room) => new List<int> { room.Id };
    }

    /// <summary>The registry of space types (by room kind), and the lights every space gets.</summary>
    public static class Spaces
    {
        static readonly Dictionary<string, SpaceType> Registry = new Dictionary<string, SpaceType>();
        static readonly List<string> Order = new List<string>();      // registration order (Python dict order)

        public const int LightOverFloor = 192;

        static Spaces()
        {
            // the built-in space types (Python: registered on import)
            Register(new RoomSpace());
            Register(new OverlookSpace());
            foreach (var kind in new[] { "stairwell", "lift", "ladder", "spiral" }) Register(new ConnectorSpace(kind));
            Register(new OpenAreaSpace());
            Register(new MegaSpace());
            Register(new TowerSpace());
        }

        public static SpaceType Register(SpaceType space)
        {
            foreach (var k in space.Kinds)
            {
                if (!Registry.ContainsKey(k)) Order.Add(k);
                Registry[k] = space;
            }
            return space;
        }

        public static SpaceType SpaceFor(string kind)
        {
            if (kind != null && Registry.TryGetValue(kind, out var s)) return s;
            throw new KeyNotFoundException($"no space type registered for room kind '{kind}'");
        }

        public static bool Has(string kind) => kind != null && Registry.ContainsKey(kind);

        public static string[] VerticalKinds() => Order.Where(k => Registry[k].Vertical).ToArray();

        /// <summary>Lights over the floor, one every ~9 cells in big rooms, and one up in a tall vault.</summary>
        public static void PlaceLights(Level level, Room room, int? maxHeight = null, int? intensity = null)
        {
            var cfg = level.Config;
            if (cfg.Torches && !room.IsConnector && room.Kind != "courtyard")
            {
                if (Dungeon.PlaceTorches(level, room) > 0)     // dungeons: torches on the walls instead
                    return;
            }
            int area = room.W * room.H;
            double lv = cfg.LightVariety;
            if (lv > 0)            // sector-style light: some rooms dim, some bright (one draw per room)
            {
                double kk = Rng.FromLabel($"{cfg.Seed}/{cfg.Variant}/light/{room.Id}").Uniform(1 - 0.5 * lv, 1 + 0.3 * lv);
                intensity = (int)Math.Round((intensity ?? 300) * kk);
            }
            var spots = new List<(double, double)>();
            if (area >= 140)               // a light every ~9 cells (Quake light fades over ~300 units)
            {
                int nx = Math.Max(2, (int)Math.Round(room.W / 9.0)), ny = Math.Max(2, (int)Math.Round(room.H / 9.0));
                for (int i = 0; i < nx; i++)
                    for (int j = 0; j < ny; j++) spots.Add(((i + 0.5) / nx, (j + 0.5) / ny));
            }
            else spots.Add((0.5, 0.5));
            bool single = room.Kind == "courtyard";           // (not on a yard building's roof)
            var lit = new HashSet<Cell>();
            int s = level.CellSize;
            foreach (var (fx, fy) in spots)
            {
                // TODO(wave 1b): DecorateStubs.NearestOpen -> Decorate.NearestOpen
                var c = DecorateStubs.NearestOpen(level, room, room.X + room.W * fx, room.Y + room.H * fy, single);
                if (!lit.Add(c)) continue;
                var sp = level.RoomSpans(c, room).Last();
                int z = Math.Min(sp.Ceil - 24, Math.Max(sp.Floor, room.Floor) + LightOverFloor);   // over the floor, not lost in a vault
                if (maxHeight.HasValue) z = Math.Min(z, room.Floor + maxHeight.Value);
                level.Entities.Add(new Entity("light", new Vec3((c.X + 0.5) * s, (c.Y + 0.5) * s, z))
                    .With("intensity", intensity ?? (spots.Count > 1 ? 250 : 300)));
            }
            if (maxHeight == null && room.Ceil - room.Floor > 2 * LightOverFloor)     // and the vault itself
            {
                var c = DecorateStubs.NearestOpen(level, room, room.X + room.W / 2.0, room.Y + room.H / 2.0, single);
                int top = level.RoomSpans(c, room).Last().Ceil;
                if (top - room.Floor > 2 * LightOverFloor)
                    level.Entities.Add(new Entity("light", new Vec3((c.X + 0.5) * s, (c.Y + 0.5) * s, top - 48)).With("intensity", 350));
            }
        }
    }
}
