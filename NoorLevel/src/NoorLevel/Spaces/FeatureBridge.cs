using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>The decorate.py helpers as the space types call them (Func-typed features), forwarding
    /// to <see cref="Decorate"/>.</summary>
    static class DecorateStubs
    {
        public static bool FHazard(RoomCtx ctx, Rng rng) => Decorate.FHazard(ctx, rng);
        public static bool FChasm(RoomCtx ctx, Rng rng) => Decorate.FChasm(ctx, rng);
        public static bool FTower(RoomCtx ctx, Rng rng) => Decorate.FTower(ctx, rng);
        public static bool FPillars(RoomCtx ctx, Rng rng) => Decorate.FPillars(ctx, rng);
        public static bool FCrates(RoomCtx ctx, Rng rng) => Decorate.FCrates(ctx, rng);

        // keep the feature's name (the weight key) when it travels as a Func
        static readonly Dictionary<Func<RoomCtx, Rng, bool>, string> Names = new Dictionary<Func<RoomCtx, Rng, bool>, string>();
        public static readonly Dictionary<string, List<List<Func<RoomCtx, Rng, bool>>>> FeaturesByKind =
            Decorate.FeaturesByKind.ToDictionary(kv => kv.Key,
                kv => kv.Value.Select(opt => opt.Select(f => Wrap(f)).ToList()).ToList());
        public static readonly Dictionary<string, double> CrateChance = Decorate.CrateChance;

        static Func<RoomCtx, Rng, bool> Wrap(Decorate.Feature f)
        {
            Func<RoomCtx, Rng, bool> g = (c, r) => f(c, r);
            Names[g] = Decorate.FeatureName(f);
            return g;
        }
        public static string NameOf(Func<RoomCtx, Rng, bool> f) => Names.TryGetValue(f, out var n) ? n : null;

        public static List<Cell> Pedestal(RoomCtx ctx) => ctx.Pedestal;
        public static Entity DoorEntity(Level level, Corridor corr, string kind) => Decorate.DoorEntity(level, corr, kind);
        public static Cell NearestOpen(Level level, Room room, double fx, double fy, bool single = false) => Decorate.NearestOpen(level, room, fx, fy, single);
        public static bool FlatCell(Level level, Room room, Cell c) => Decorate.FlatCell(level, room, c);
        public static List<Cell> FlatBlock(RoomCtx ctx, double fx, double fy, ICollection<Cell> avoid = null) => Decorate.FlatBlock(ctx, fx, fy, avoid);
        public static Cell? PickFree(RoomCtx ctx, Rng rng, HashSet<Cell> taken, bool floorOnly = true) => Decorate.PickFree(ctx, rng, taken, floorOnly);
        public static Vec3 At(Level level, Room room, Cell c, bool top = false, double zOff = 24.0) => Decorate.At(level, room, c, top, zOff);
    }
}
