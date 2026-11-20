using System;
using System.Collections.Generic;
using System.Linq;

// TEMPORARY SHIM: a minimal stand-in for the space registry (badlevel/spaces/__init__.py, room.py,
// connector.py, corridor.py populate) so Decorate.cs builds and runs against the old Layout.cs.
// DELETE THIS FILE (the whole Shim/ folder) when the layout agent's Spaces/ port is merged: it
// defines Spaces, SpaceType, RoomSpace, ConnectorSpace and CorridorSpace, which that port replaces.
namespace NoorLevel
{
    public class SpaceType
    {
        public virtual List<List<Decorate.Feature>> FeatureOptions(Room room) => new List<List<Decorate.Feature>> { new List<Decorate.Feature>() };

        public virtual double CrateChance(Room room) => 0.0;

        /// <summary>Pick one weighted option of internal features, then maybe crates.</summary>
        public virtual void Decorate(Level level, Room room, RoomCtx ctx, Rng rng)
        {
            var cfg = level.Config;
            var options = FeatureOptions(room).ToList();
            var fw = cfg.FeatureWeights;
            if (cfg.Hazards > 0 && new[] { "combat", "arena", "hub", "megaroom" }.Contains(room.Kind))
                options.Add(new List<Decorate.Feature> { NoorLevel.Decorate.FHazard });
            if (cfg.Chasms > 0 && new[] { "combat", "arena", "hub" }.Contains(room.Kind))
                options.Add(new List<Decorate.Feature> { NoorLevel.Decorate.FChasm });
            double pads = cfg.JumpPads;
            if (pads > 0 && new[] { "combat", "arena", "hub" }.Contains(room.Kind))
            {
                options.Add(new List<Decorate.Feature> { NoorLevel.Decorate.FTower });
                options.Add(new List<Decorate.Feature> { NoorLevel.Decorate.FTower, NoorLevel.Decorate.FPillars });
            }
            double OptionWeight(List<Decorate.Feature> opt)
            {
                if (opt.Count == 0) return fw.TryGetValue("none", out var nw) ? nw : 1.0;
                double w = 1.0;
                foreach (var f in opt)
                {
                    var name = NoorLevel.Decorate.FeatureName(f);
                    double dflt = name == "f_tower" ? pads : name == "f_chasm" ? cfg.Chasms : 1.0;
                    w *= fw.TryGetValue(name.Substring(2), out var x) ? x : dflt;
                }
                return w;
            }
            var ws = options.Select(OptionWeight).ToList();
            var chosen = ws.Sum() > 0 ? rng.Choices(options, ws) : new List<Decorate.Feature>();
            foreach (var feat in chosen) feat(ctx, rng);
            if (rng.Random() < Math.Min(1.0, CrateChance(room) * cfg.CrateScale))
                NoorLevel.Decorate.FCrates(ctx, rng);
        }

        public virtual void Populate(Level level, Room room, RoomCtx ctx, Rng rng, Dictionary<string, object> info) =>
            Spaces.PlaceLights(level, room);
    }

    public sealed class RoomSpace : SpaceType
    {
        public override List<List<Decorate.Feature>> FeatureOptions(Room room) =>
            NoorLevel.Decorate.FeaturesByKind.TryGetValue(room.Kind, out var o) ? o : base.FeatureOptions(room);

        public override double CrateChance(Room room) =>
            NoorLevel.Decorate.CrateChance.TryGetValue(room.Kind, out var c) ? c : 0.0;

        public override void Populate(Level level, Room room, RoomCtx ctx, Rng rng, Dictionary<string, object> info)
        {
            var node = level.Graph.Nodes[room.Node];
            var taken = new HashSet<Cell>();
            double cx = room.X + room.W / 2.0, cy = room.Y + room.H / 2.0;
            Spaces.PlaceLights(level, room);
            if (room.Kind == "start")
            {
                Cell? best = null; double bd = 0;
                foreach (var c in room.Cells())
                {
                    if (!NoorLevel.Decorate.FlatCell(level, room, c)) continue;
                    double d = Math.Pow(c.X + 0.5 - cx, 2) + Math.Pow(c.Y + 0.5 - cy, 2);
                    if (best == null || d < bd) { best = c; bd = d; }
                }
                if (best == null) throw new LayoutException("no spot for the start");
                var s0 = best.Value;
                taken.Add(s0);
                int angle = 0;
                if (ctx.Doors.Count > 0)
                {
                    double dx = ctx.Doors.Sum(d => d.X) / (double)ctx.Doors.Count + 0.5 - (s0.X + 0.5);
                    double dy = ctx.Doors.Sum(d => d.Y) / (double)ctx.Doors.Count + 0.5 - (s0.Y + 0.5);
                    angle = (((int)Math.Round(Math.Atan2(dy, dx) * 180 / Math.PI / 45.0) * 45) % 360 + 360) % 360;
                }
                level.Entities.Add(new Entity("player_start", NoorLevel.Decorate.At(level, room, s0, zOff: 32.0)).With("angle", angle));
            }
            else if (room.Kind == "exit")
            {
                var block = NoorLevel.Decorate.FlatBlock(ctx, cx, cy);
                if (block == null) throw new LayoutException("no spot for the exit");
                int ex = block[0].X, ey = block[0].Y, s = level.CellSize;
                var lo = new Vec3(ex * s, ey * s, room.Floor);
                var hi = new Vec3((ex + 2) * s, (ey + 2) * s, room.Floor + 64);
                level.Entities.Add(new Entity("exit", new Vec3((ex + 1) * s, (ey + 1) * s, room.Floor + 32)) { Bounds = (lo, hi) });
            }
            else if (room.Kind == "key")
            {
                var ped = ctx.Pedestal;
                Cell? c = ped != null && ped.Count > 0 ? ped[0] : NoorLevel.Decorate.PickFree(ctx, rng, taken);
                if (c == null) throw new LayoutException("no spot for key");
                level.Entities.Add(new Entity("key", NoorLevel.Decorate.At(level, room, c.Value, zOff: 24)).With("color", node.Key));
            }
        }
    }

    public sealed class ConnectorSpace : SpaceType
    {
    }

    public static class Spaces
    {
        static readonly SpaceType Rooms = new RoomSpace();
        static readonly SpaceType Connectors = new ConnectorSpace();

        public static SpaceType SpaceFor(string kind) => Array.IndexOf(Room.ConnectorKinds, kind) >= 0 ? Connectors : Rooms;

        public static readonly int LightOverFloor = 192;

        public static void PlaceLights(Level level, Room room, int? maxHeight = null, int? intensity = null)
        {
            var cfg = level.Config;
            int area = room.W * room.H;
            if (cfg.LightVariety > 0)
            {
                double k = Rng.FromLabel($"{cfg.Seed}/{cfg.Variant}/light/{room.Id}").Uniform(1 - 0.5 * cfg.LightVariety, 1 + 0.3 * cfg.LightVariety);
                intensity = (int)Math.Round((intensity ?? 300) * k);
            }
            var spots = new List<(double, double)>();
            if (area >= 140)
            {
                int nx = Math.Max(2, (int)Math.Round(room.W / 9.0)), ny = Math.Max(2, (int)Math.Round(room.H / 9.0));
                for (int i = 0; i < nx; i++) for (int j = 0; j < ny; j++) spots.Add(((i + 0.5) / nx, (j + 0.5) / ny));
            }
            else spots.Add((0.5, 0.5));
            var lit = new HashSet<Cell>();
            foreach (var (fx, fy) in spots)
            {
                var c = NoorLevel.Decorate.NearestOpen(level, room, room.X + room.W * fx, room.Y + room.H * fy, room.Kind == "courtyard");
                if (!lit.Add(c)) continue;
                var sp = level.RoomSpans(c, room).Last();
                int z = Math.Min(sp.Ceil - 24, Math.Max(sp.Floor, room.Floor) + LightOverFloor);
                if (maxHeight != null) z = Math.Min(z, room.Floor + maxHeight.Value);
                level.Entities.Add(new Entity("light", new Vec3((c.X + 0.5) * level.CellSize, (c.Y + 0.5) * level.CellSize, z))
                    .With("intensity", intensity ?? (spots.Count > 1 ? 250 : 300)));
            }
            if (maxHeight == null && room.Ceil - room.Floor > 2 * LightOverFloor)
            {
                var c = NoorLevel.Decorate.NearestOpen(level, room, room.X + room.W / 2.0, room.Y + room.H / 2.0, room.Kind == "courtyard");
                int top = level.RoomSpans(c, room).Last().Ceil;
                if (top - room.Floor > 2 * LightOverFloor)
                    level.Entities.Add(new Entity("light", new Vec3((c.X + 0.5) * level.CellSize, (c.Y + 0.5) * level.CellSize, top - 48)).With("intensity", 350));
            }
        }
    }

    public static class CorridorSpace
    {
        public static void Populate(Level level, Corridor corr, Rng rng, Dictionary<string, object> info)
        {
            for (int i = 3; i < corr.Path.Count - 3; i += 7)
            {
                var c = corr.Path[i];
                level.Entities.Add(new Entity("light", new Vec3((c.X + 0.5) * level.CellSize, (c.Y + 0.5) * level.CellSize, corr.Floors[i] + corr.Height - 16))
                    .With("intensity", 200));
            }
            if (corr.Kind == "locked") level.Entities.Add(NoorLevel.Decorate.DoorEntity(level, corr, "door"));
            else if (corr.Kind == "secret") level.Entities.Add(NoorLevel.Decorate.DoorEntity(level, corr, "secret_door"));
        }
    }
}
