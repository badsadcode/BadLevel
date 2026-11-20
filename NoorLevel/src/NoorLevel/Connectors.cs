using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>Vertical connector rooms between two floor heights: switchback stairwells, spiral
    /// stairs round a newel, lift shafts and ladder shafts. Built in a local frame (u along, v across);
    /// corridors attach at ports, the low one at the bottom landing, the high one at the top.</summary>
    public static class Connectors
    {
        const int LiftW = 5, LiftL = 6, LadderW = 4, LadderL = 5;
        const int SpiralSize = 11;
        const double SpiralCore = 2.0, SpiralLanding = 0.15;
        const int SpiralHeadroom = 80;

        public static (int Cycles, int Flight)? StairwellPlan(int rise, int step = 16)
        {
            if (rise <= 0 || rise % (2 * step) != 0) return null;
            int m = rise / (2 * step);
            foreach (var (lo, hi) in new[] { (5, 9), (10, 13) })
                for (int cycles = 1; cycles <= m; cycles++)
                {
                    if (m % cycles != 0) continue;
                    int f = m / cycles;
                    if (lo <= f && f <= hi) return (cycles, f);
                }
            return null;
        }

        static Dictionary<(int U, int V), double> SpiralCells(int S)
        {
            double c = S / 2.0;
            var outp = new Dictionary<(int, int), double>();
            for (int u = 0; u < S; u++)
                for (int v = 0; v < S; v++)
                {
                    double du = u + 0.5 - c, dv = v + 0.5 - c, r = Math.Sqrt(du * du + dv * dv);
                    if (SpiralCore <= r && r <= c)
                        outp[(u, v)] = ((Math.Atan2(dv, du) / (2 * Math.PI)) % 1.0 + 1.0) % 1.0;
                }
            return outp;
        }

        static Dictionary<(int U, int V), List<int>> SpiralFloors(int S, int rise, int turns, double start, bool cw, int step = 16)
        {
            double a = SpiralLanding;
            double span = turns - a;
            if (span <= 0) return null;
            var cells = SpiralCells(S);
            var floors = new Dictionary<(int, int), List<int>>();
            foreach (var kv in cells)
            {
                double ang = kv.Value;
                double phi = ((cw ? (start - ang) : (ang - start)) % 1.0 + 1.0) % 1.0;
                phi = (phi + a / 2) % 1.0;
                var fl = new SortedSet<int>();
                for (int k = 0; k <= turns; k++)
                {
                    double t = k + phi;
                    if (t > turns + a) break;
                    double h = t <= a ? 0.0 : t >= turns ? rise : (t - a) / span * rise;
                    fl.Add((int)Math.Round(h / step) * step);
                }
                floors[kv.Key] = fl.ToList();
            }
            foreach (var kv in floors)
            {
                var fl = kv.Value;
                for (int i = 0; i + 1 < fl.Count; i++)
                    if (fl[i + 1] - fl[i] < SpiralHeadroom + 16) return null;
                var (u, v) = kv.Key;
                foreach (var n in new[] { (u + 1, v), (u, v + 1) })
                {
                    if (!floors.TryGetValue(n, out var nf)) continue;
                    foreach (var f in fl)
                    {
                        if (f == rise) continue;
                        int near = nf.OrderBy(g => Math.Abs(g - f)).First();
                        if (Math.Abs(near - f) > step && near != rise) return null;
                    }
                }
            }
            return floors;
        }

        static (int Turns, double Start, bool Cw)? SpiralPlan(int rise, Rng rng)
        {
            if (rise < 128 || rise % 16 != 0) return null;
            var sides = new List<(double, bool)>();
            foreach (var s in new[] { 0.0, 0.25, 0.5, 0.75 }) foreach (var cw in new[] { false, true }) sides.Add((s, cw));
            rng.Shuffle(sides);
            for (int turns = 1; turns < 8; turns++)
                foreach (var (start, cw) in sides)
                    if (SpiralFloors(SpiralSize, rise, turns, start, cw) != null) return (turns, start, cw);
            return null;
        }

        /// <summary>(w, h) of the footprint, with its plan in meta, or null if the rise can't be built.</summary>
        public static (int W, int H)? Size(string kind, int rise, Rng rng, bool wide, Dictionary<string, object> meta)
        {
            string axis = rng.Choice(new[] { "x", "y" });
            int fw = wide ? 5 : 3;
            int L, W;
            switch (kind)
            {
                case "stairwell":
                {
                    var plan = StairwellPlan(rise);
                    if (plan == null) return null;
                    var (cycles, F) = plan.Value;
                    L = F + 2 * fw; W = 2 * fw + 1;
                    meta["cycles"] = cycles; meta["flight"] = F;
                    break;
                }
                case "spiral":
                {
                    var plan = SpiralPlan(rise, rng);
                    if (plan == null) return null;
                    meta["turns"] = plan.Value.Turns; meta["start"] = plan.Value.Start; meta["cw"] = plan.Value.Cw;
                    meta["axis"] = "x"; meta["L"] = SpiralSize; meta["W"] = SpiralSize; meta["fw"] = 3;
                    return (SpiralSize, SpiralSize);
                }
                case "lift": L = LiftL; W = LiftW + (wide ? 2 : 0); break;
                default: L = LadderL; W = LadderW + (wide ? 3 : 0); break;
            }
            meta["axis"] = axis; meta["L"] = L; meta["W"] = W; meta["fw"] = fw;
            return axis == "x" ? (L, W) : (W, L);
        }

        static Cell ToCell(Room room, int u, int v) =>
            (string)room.Meta["axis"] == "x" ? new Cell(room.X + u, room.Y + v) : new Cell(room.X + v, room.Y + u);

        public static void ApplyShape(Room room)
        {
            if (room.Kind != "spiral") return;
            room.Mask = new HashSet<Cell>(SpiralCells(room.W).Keys.Select(k => new Cell(k.U, k.V)));
            room.Shape = "round";
        }

        /// <summary>Perimeter cells where corridors may attach: low and high (cells, floor).</summary>
        public static (HashSet<Cell> Cells, int Floor)[] Ports(Room room)
        {
            int L = (int)room.Meta["L"], W = (int)room.Meta["W"];
            int top = room.TopFloor ?? room.Floor;
            if (room.Kind == "spiral")
            {
                var ring = SpiralCells(L);
                double c = L / 2.0, ang = (double)room.Meta["start"] * 2 * Math.PI;
                double sx = Math.Cos(ang), sy = Math.Sin(ang);
                double best = ring.Keys.Max(k => (k.U + 0.5 - c) * sx + (k.V + 0.5 - c) * sy);
                var door = new HashSet<Cell>(ring.Keys.Where(k => (k.U + 0.5 - c) * sx + (k.V + 0.5 - c) * sy > best - 0.5)
                                                   .Select(k => ToCell(room, k.U, k.V)));
                return new[] { (door, room.Floor), (door, top) };
            }
            if (room.Kind == "stairwell")
            {
                int h = ((int)room.Meta["fw"]) / 2;
                var low = new HashSet<Cell> { ToCell(room, L - 1, h), ToCell(room, L - 1 - h, 0) };
                var high = new HashSet<Cell> { ToCell(room, L - 1, W - 1 - h), ToCell(room, L - 1 - h, W - 1) };
                return new[] { (low, room.Floor), (high, top) };
            }
            var mid = W % 2 == 1 ? new[] { W / 2 } : new[] { W / 2 - 1, W / 2 };
            return new[] { (new HashSet<Cell>(mid.Select(v => ToCell(room, L - 1, v))), room.Floor),
                           (new HashSet<Cell>(mid.Select(v => ToCell(room, 0, v))), top) };
        }

        static void Column(Level level, Room room, Cell c, IEnumerable<int> floorsIn)
        {
            var floors = floorsIn.OrderBy(f => f).ToList();
            for (int i = 0; i < floors.Count; i++)
                level.AddSpan(c, new Span(floors[i], i + 1 < floors.Count ? floors[i + 1] - 16 : room.Ceil));
            if (!level.Owners.TryGetValue(c, out var os)) level.Owners[c] = os = new List<Owner>();
            os.Add(new Owner(room.Slot.Lo, room.Ceil, "room:" + room.Id));
        }

        public static void Build(Level level, Room room, int step = 16)
        {
            int L = (int)room.Meta["L"], W = (int)room.Meta["W"];
            int low = room.Floor, top = room.TopFloor ?? room.Floor;
            int s = level.CellSize;
            if (room.Kind == "spiral")
            {
                var floors = SpiralFloors(L, top - low, (int)room.Meta["turns"], (double)room.Meta["start"], (bool)room.Meta["cw"], step);
                foreach (var kv in floors) Column(level, room, ToCell(room, kv.Key.U, kv.Key.V), kv.Value.Select(f => low + f));
                for (int u = 0; u < L; u++)
                    for (int v = 0; v < W; v++)
                        if (!floors.ContainsKey((u, v)) && Math.Sqrt(Math.Pow(u + 0.5 - L / 2.0, 2) + Math.Pow(v + 0.5 - W / 2.0, 2)) < SpiralCore)
                            level.PartSolid[ToCell(room, u, v)] = $"room:{room.Id}/newel";
                return;
            }
            if (room.Kind == "stairwell")
            {
                int cycles = (int)room.Meta["cycles"], F = (int)room.Meta["flight"], fw = (int)room.Meta["fw"];
                int riseC = 2 * F * step;
                for (int u = 0; u < L; u++)
                    for (int v = 0; v < W; v++)
                    {
                        if (v == W / 2 && fw <= u && u <= L - fw - 1) continue;       // the spine between the flights
                        IEnumerable<int> fl;
                        if (u >= L - fw) fl = Enumerable.Range(0, cycles + 1).Select(k => low + k * riseC);
                        else if (u <= fw - 1) fl = Enumerable.Range(0, cycles).Select(k => low + k * riseC + F * step);
                        else if (v < W / 2) { int j = (L - fw - 1) - u; fl = Enumerable.Range(0, cycles).Select(k => low + k * riseC + (j + 1) * step); }
                        else { int j = u - fw; fl = Enumerable.Range(0, cycles).Select(k => low + k * riseC + F * step + (j + 1) * step); }
                        Column(level, room, ToCell(room, u, v), fl);
                    }
                return;
            }
            // lift / ladder: a top landing (rows u = 0, 1) over open space, the shaft below
            for (int u = 0; u < L; u++)
                for (int v = 0; v < W; v++)
                    Column(level, room, ToCell(room, u, v), u <= 1 ? new[] { low, top } : new[] { low });
            Vec3 World(int u, int v, double z) { var c = ToCell(room, u, v); return new Vec3((c.X + 0.5) * s, (c.Y + 0.5) * s, z); }
            int vmid = W / 2;
            if (room.Kind == "lift")
            {
                var cells = (from u in new[] { 2, 3, 4 } from v in new[] { vmid - 1, vmid, vmid + 1 } select ToCell(room, u, v)).ToList();
                var lo = new Vec3(cells.Min(c => c.X) * s, cells.Min(c => c.Y) * s, top - 16);
                var hi = new Vec3((cells.Max(c => c.X) + 1) * s, (cells.Max(c => c.Y) + 1) * s, top);
                level.Entities.Add(new Entity("lift", new Vec3((lo.X + hi.X) / 2, (lo.Y + hi.Y) / 2, (lo.Z + hi.Z) / 2)) { Bounds = (lo, hi) }
                    .With("low", low).With("high", top).With("travel", top - low).With("room", room.Id));
                level.Links.Add(new Link { Kind = "lift", A = World(3, vmid, low + 24), B = World(1, vmid, top + 24), Interaction = "ride", Accessible = true, Room = room.Id });
            }
            else
            {
                var c0 = ToCell(room, 2, vmid - 1); var c1 = ToCell(room, 2, vmid);
                Vec3 lo, hi;
                if ((string)room.Meta["axis"] == "x")
                { lo = new Vec3(c0.X * s, Math.Min(c0.Y, c1.Y) * s, low); hi = new Vec3(c0.X * s + 8, (Math.Max(c0.Y, c1.Y) + 1) * s, top + 32); }
                else
                { lo = new Vec3(Math.Min(c0.X, c1.X) * s, c0.Y * s, low); hi = new Vec3((Math.Max(c0.X, c1.X) + 1) * s, c0.Y * s + 8, top + 32); }
                level.Entities.Add(new Entity("ladder", new Vec3((lo.X + hi.X) / 2, (lo.Y + hi.Y) / 2, (lo.Z + hi.Z) / 2)) { Bounds = (lo, hi) }
                    .With("low", low).With("high", top).With("room", room.Id));
                level.Links.Add(new Link { Kind = "ladder", A = World(2, vmid, low + 24), B = World(1, vmid, top + 24), Interaction = "climb", Accessible = false, Room = room.Id });
            }
        }
    }
}
