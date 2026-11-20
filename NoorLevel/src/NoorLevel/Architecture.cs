using System;
using System.Collections.Generic;
using System.Linq;

// architecture.py: hall cross-sections that give a space a recognisable structure.
//
// A cross-section treats a roofed space as a section profile swept along its long axis: ceiling
// heights that vary across (and sometimes along) the room, plus structural members that stand in
// it (piers, columns, pilasters). Each style offers a palette of profiles (not one template):
//
//   gothic      basilica, single-aisle, vault, undercroft, gable
//   industrial  portal, sawtooth, multispan, clearspan
//   bunker      ribbed, barrel, mushroom
//   compound    gable, shed, portal, multispan, clearspan
//   egyptian    hypostyle, lintel, corbelled, battered
//   scifi       hull, chamfered, lightwell
//   castle      trussed, timber, barrel, undercroft
//
// Rooms are ranked by volume (grand / major / minor), proportions drive the structure, every
// palette keeps an open option, and each use of a profile halves its weight for the rest of the
// level. Safety: piers go in before a room's features (undone if they cut the floor), ceilings
// are applied after them and only ever lowered (never below a walkable surface plus HEAD, never
// below an opening next to the cell, never over a gallery or the sky). A separate random stream
// per room: levels without a style are unchanged.
namespace NoorLevel
{
    /// <summary>A room in hall coordinates: u along the long axis (0..L), v across it (0..W).</summary>
    public sealed class Frame
    {
        public readonly Room Room;
        public readonly bool AlongX;
        public readonly int L, W, H;
        public readonly bool Flip;

        public Frame(Room room, bool flipV = false)
        {
            Room = room;
            AlongX = room.W >= room.H;
            (L, W) = AlongX ? (room.W, room.H) : (room.H, room.W);
            H = room.Ceil - room.Floor;
            Flip = flipV;
        }

        public Cell Cell(int u, int v)
        {
            if (Flip) v = W - 1 - v;
            var r = Room;
            return AlongX ? new Cell(r.X + u, r.Y + v) : new Cell(r.X + v, r.Y + u);
        }

        public (int U, int V) UV(Cell c)
        {
            var r = Room;
            var (u, v) = AlongX ? (c.X - r.X, c.Y - r.Y) : (c.Y - r.Y, c.X - r.X);
            return Flip ? (u, W - 1 - v) : (u, v);
        }
    }

    /// <summary>A profile's result: ceiling height above the floor per (u, v), and pier groups.</summary>
    public sealed class Section
    {
        public string Name;
        public readonly Frame Frame;
        public Dictionary<(int, int), int> Ceil = new Dictionary<(int, int), int>();
        public List<List<(int, int)>> Piers = new List<List<(int, int)>>();
        public Dictionary<string, object> Info = new Dictionary<string, object>();
        /// <summary>Continuous ceiling surface h(u, v, cell_u, cell_v) for smooth vaults and roofs:
        /// evaluated at a cell's corners it gives a sloped plane (null: stepped by nature).</summary>
        public Func<double, double, int, int, double> Surf;
        public HashSet<(int, int)> Forced = new HashSet<(int, int)>();   // cells that stay stepped

        // walls.py bookkeeping (Python keeps these in sec.info): placed pier groups, sloped capitals
        public List<List<(int, int)>> Placed = new List<List<(int, int)>>();
        public List<(Cell C, (int X, int Y) ToWall, int Low, int High)> Splays = new List<(Cell, (int, int), int, int)>();

        public Section(string name, Frame frame) { Name = name; Frame = frame; }

        public void Set(int u, int v, double h) => Ceil[(u, v)] = Architecture.Q(h);

        /// <summary>Set every cell from a surface function (sampled at the cell centre).</summary>
        public void Fill(Func<double, double, int, int, double> h, bool smooth = true)
        {
            for (int u = 0; u < Frame.L; u++)
                for (int v = 0; v < Frame.W; v++)
                    Set(u, v, h(u + 0.5, v + 0.5, u, v));
            if (smooth) Surf = h;
        }
    }

    public static partial class Architecture
    {
        public static readonly string[] Styles = { "gothic", "industrial", "bunker", "compound", "egyptian", "scifi", "castle" };
        public const int Head = 112;     // least headroom under any structure: the lowest corridor the generator makes
        public static readonly string[] SkipKinds = { "courtyard", "secret", "overlook" };

        /// <summary>Snap a height to the 16-unit step grid.</summary>
        public static int Q(double z) => (int)Math.Round(z / 16.0) * 16;

        static double Clamp(double x, double lo, double hi) => Math.Max(lo, Math.Min(hi, x));
        static int Clamp(int x, int lo, int hi) => Math.Max(lo, Math.Min(hi, x));
        static int R(double x) => (int)Math.Round(x);          // Python int(round(x)) (banker's)

        // ------------------------------------------------------------------ proportions
        /// <summary>Start positions of structural lines along a length, centred, never at the very ends.</summary>
        public static List<int> BayLines(int L, int bay, int width = 1)
        {
            var o = new List<int>();
            if (L < bay + 2) return o;
            int n = FloorDiv(L - 2 - width, bay) + 1;
            int span = (n - 1) * bay + width;
            int start = FloorDiv(L - span, 2);
            for (int k = 0; k < n; k++)
            {
                int p = start + k * bay;
                if (1 <= p && p <= L - 1 - width) o.Add(p);
            }
            return o;
        }

        internal static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

        /// <summary>Bays scale with the height, and a hall gets at least three of them.</summary>
        public static int BaySize(int H, int L, double k, int lo, int hi)
        {
            int b = R(H / 32.0 * k);
            b = Clamp(b, lo, hi);
            return Math.Max(lo, Math.Min(b, Math.Max(lo, FloorDiv(L - 2, 3))));
        }

        public static int PierSize(int H) => H >= 384 ? 2 : 1;

        public static int RibDepth(int H, int W) => Q(Clamp(H * 0.08 + W * 1.5, 32, 96));

        /// <summary>Two-centred (pointed) arch: 1 at the apex (t = 0), 0 at the springing (t = 1).</summary>
        public static double Pointed(double t, double e = 0.5)
        {
            t = Clamp(t, 0.0, 1.0);
            double x = (t + e) / (1 + e);
            return Math.Sqrt(Math.Max(0.0, 1 - x * x)) / Math.Sqrt(1 - Math.Pow(e / (1 + e), 2));
        }

        public static double RoundArch(double t) => Math.Sqrt(Math.Max(0.0, 1 - Math.Pow(Clamp(t, 0.0, 1.0), 2)));

        /// <summary>0 in the middle of the band [v0, v1), 1 at its edges (continuous position).</summary>
        static double AcrossF(double vf, int v0, int v1)
        {
            double half = (v1 - v0) / 2.0;
            return half > 0 ? Clamp(Math.Abs(vf - (v0 + half)) / half, 0.0, 1.0) : 1.0;
        }

        static List<(int, int)> Pier(int u0, int v0, int p, int? pu = null)
        {
            int n = pu ?? p;
            var o = new List<(int, int)>();
            for (int u = u0; u < u0 + n; u++)
                for (int v = v0; v < v0 + p; v++) o.Add((u, v));
            return o;
        }

        // ------------------------------------------------------------------ profiles
        public static Section Basilica(Frame fr, Rng rng, int aisles = 2)
        {
            int L = fr.L, W = fr.W, H = fr.H;
            var s = new Section(aisles == 2 ? "basilica" : "single-aisle", fr);
            int aw = Clamp(R(W * rng.Uniform(0.2, 0.28)), 3, W / 3);
            var (nave0, nave1) = aisles == 2 ? (aw, W - aw) : (aw, W);
            if (nave1 - nave0 < 5 || H < 288) return null;
            int aisleH = Q(Math.Max(Head + 48, H * rng.Uniform(0.38, 0.52)));
            int spring = Q(aisleH + (H - aisleH) * rng.Uniform(0.3, 0.5));
            int p = PierSize(H);
            int bay = BaySize(H, L, rng.Uniform(0.38, 0.5), 3 + p, 9);
            var lines = BayLines(L, bay, p);
            int rib = RibDepth(H, nave1 - nave0);
            var onLine = new HashSet<int>(lines.SelectMany(u => Enumerable.Range(u, p)));
            s.Fill((uf, vf, u, v) =>
            {
                if (nave0 <= v && v < nave1)
                {
                    double z = spring + (H - spring) * Pointed(AcrossF(vf, nave0, nave1));
                    return onLine.Contains(u) ? Math.Max(spring, z - rib) : z;   // transverse ribs between the bays
                }
                return aisleH - (onLine.Contains(u) ? 32 : 0);                  // the aisle vaults spring from the piers
            });
            foreach (int u in lines)                    // the arcade: piers on the aisle side of the nave
            {
                s.Piers.Add(Pier(u, nave0 - p, p));
                if (aisles == 2) s.Piers.Add(Pier(u, nave1, p));
            }
            s.Info = new Dictionary<string, object> { ["bay"] = bay, ["aisle_width"] = aw, ["aisle_height"] = aisleH, ["spring"] = spring, ["pier"] = p };
            return s;
        }

        public static Section SingleAisle(Frame fr, Rng rng) => Basilica(fr, rng, 1);

        /// <summary>One vault over the whole width. No piers: an uninterrupted volume, with ribs (and wall
        /// shafts in bigger halls) marking the bays.</summary>
        public static Section Vault(Frame fr, Rng rng, string shape = "pointed")
        {
            int L = fr.L, W = fr.W, H = fr.H;
            if (H < Head + 48) return null;            // smooth, even a low vault reads (6 m rooms)
            var s = new Section(shape == "pointed" ? "vault" : "barrel", fr);
            int rise = Math.Min(H - (Head + 16), Q(W * (shape == "pointed" ? 12 : 7)));
            if (rise < 32) return null;
            int spring = H - rise;
            Func<double, double> arch = shape == "pointed" ? (t => Pointed(t)) : RoundArch;
            int bay = BaySize(H, L, rng.Uniform(0.4, 0.6), 3, 10);
            var lines = BayLines(L, bay);
            bool ribbed = rng.Random() < 0.75;
            int rib = shape == "pointed" ? RibDepth(H, W) / 2 : 32;
            var onLine = ribbed ? new HashSet<int>(lines) : new HashSet<int>();
            s.Fill((uf, vf, u, v) =>
            {
                double z = spring + rise * arch(AcrossF(vf, 0, W));
                return onLine.Contains(u) ? Math.Max(spring, z - rib) : z;
            });
            if (shape == "pointed" && W >= 10 && H >= 320 && rng.Random() < 0.7)
                foreach (int u in lines)                // wall shafts carry the ribs down
                {
                    s.Piers.Add(new List<(int, int)> { (u, 0) });
                    s.Piers.Add(new List<(int, int)> { (u, W - 1) });
                }
            s.Info = new Dictionary<string, object> { ["bay"] = ribbed ? bay : (object)null, ["spring"] = spring, ["rise"] = rise };
            return s;
        }

        public static Section Barrel(Frame fr, Rng rng) => Vault(fr, rng, "round");

        /// <summary>A grid of piers under groin vaults: each bay's ceiling peaks in its middle.</summary>
        public static Section Undercroft(Frame fr, Rng rng)
        {
            int L = fr.L, W = fr.W, H = fr.H;
            if (W < 9 || H < Head + 64 || H > 352) return null;   // an undercroft is low
            var s = new Section("undercroft", fr);
            int p = H < 352 ? 1 : 2;
            int bay = Clamp(R(H / 32.0 * 0.45), 4, 6);
            List<int> us = BayLines(L, bay, p), vs = BayLines(W, bay, p);
            if (us.Count == 0 || vs.Count == 0) return null;
            int low = Q(Math.Max(Head + 32, H * 0.6));
            double Dist(int x, List<int> lines) => lines.Min(l => Math.Abs((x + 0.5) - (l + p / 2.0))) / (bay / 2.0);
            for (int u = 0; u < L; u++)
                for (int v = 0; v < W; v++)
                {
                    double f = Math.Min(1.0, Math.Min(Dist(u, us), Dist(v, vs)));
                    s.Set(u, v, low + (H - low) * Math.Sqrt(f));
                }
            foreach (int u in us)
                foreach (int v in vs) s.Piers.Add(Pier(u, v, p));
            s.Info = new Dictionary<string, object> { ["bay"] = bay, ["pier"] = p, ["spring"] = low };
            return s;
        }

        /// <summary>A pitched roof (ridge along the hall), or a mono-pitch shed roof.</summary>
        public static Section Gable(Frame fr, Rng rng, bool mono = false)
        {
            int W = fr.W, H = fr.H;
            if (H < Head + 48) return null;
            var s = new Section(mono ? "shed" : "gable", fr);
            double steep = !mono ? rng.Uniform(10, 18) : rng.Uniform(6, 10);
            int rise = Math.Min(H - (Head + 16), Q(W * steep / (mono ? 1 : 2)));
            if (rise < 32) return null;
            int eave = H - rise;
            s.Fill((uf, vf, u, v) => eave + rise * (mono ? vf / W : 1 - AcrossF(vf, 0, W)));
            s.Info = new Dictionary<string, object> { ["eave"] = eave, ["rise"] = rise };
            return s;
        }

        public static Section Shed(Frame fr, Rng rng) => Gable(fr, rng, true);

        /// <summary>Portal frames: pilasters on both long walls at every bay, deep trusses across the roof
        /// between them; maybe a raised monitor strip down the middle for light.</summary>
        public static Section Portal(Frame fr, Rng rng, bool? monitor = null, bool columns = true, string name = "portal")
        {
            int L = fr.L, W = fr.W, H = fr.H;
            if (H < Head + 48 || W < 6) return null;
            var s = new Section(name, fr);
            int truss = Q(Clamp(W * 4.5, 48, 128));
            if (H - truss < Head + 16) truss = Q(H - Head - 16);
            if (truss < 32) return null;
            bool mon = monitor ?? (W >= 12 && H >= 320 && rng.Random() < 0.5);
            var (m0, m1) = mon ? (W / 3, W - W / 3) : (0, 0);
            int roof = H - (mon ? 48 : 0);
            int bay = BaySize(H, L, rng.Uniform(0.6, 0.85), 4, 8);
            var lines = BayLines(L, bay);
            for (int u = 0; u < L; u++)
                for (int v = 0; v < W; v++)
                {
                    int h = m0 <= v && v < m1 ? H : roof;
                    if (lines.Contains(u)) h = Math.Min(h, roof) - truss;
                    s.Set(u, v, h);
                }
            if (columns)
            {
                int pw = H >= 384 && rng.Random() < 0.6 ? 2 : 1;
                foreach (int u in lines)
                {
                    s.Piers.Add(Pier(u, 0, 1, pw));
                    s.Piers.Add(Pier(u, W - 1, 1, pw));
                }
            }
            s.Info = new Dictionary<string, object> { ["bay"] = bay, ["truss"] = truss, ["monitor"] = mon };
            return s;
        }

        public static Section Clearspan(Frame fr, Rng rng) => Portal(fr, rng, false, false, "clearspan");

        /// <summary>North-light roof: along the hall each bay's ceiling ramps up, then drops.</summary>
        public static Section Sawtooth(Frame fr, Rng rng)
        {
            int L = fr.L, W = fr.W, H = fr.H;
            if (H < 256 || L < 12) return null;
            var s = new Section("sawtooth", fr);
            int tooth = Q(Clamp(H * 0.22, 48, 128));
            int bay = BaySize(H, L, rng.Uniform(0.45, 0.6), 4, 9);
            var lines = BayLines(L, bay);
            if (lines.Count < 2) return null;
            s.Fill((uf, vf, u, v) =>
            {
                int? prev = null, nxt = null;
                foreach (int l in lines)
                {
                    if (l <= u) prev = prev == null ? l : Math.Max(prev.Value, l);
                    if (l > u) nxt = nxt == null ? l : Math.Min(nxt.Value, l);
                }
                if (prev == null || nxt == null) return H - tooth;
                return H - tooth + tooth * (uf - prev.Value) / (nxt.Value - prev.Value);   // ramps up, then drops
            });
            foreach (int u in lines)
            {
                s.Piers.Add(new List<(int, int)> { (u, 0) });
                s.Piers.Add(new List<(int, int)> { (u, W - 1) });
            }
            s.Info = new Dictionary<string, object> { ["bay"] = bay, ["tooth"] = tooth };
            return s;
        }

        /// <summary>Two or three gabled spans side by side, on rows of columns.</summary>
        public static Section Multispan(Frame fr, Rng rng)
        {
            int L = fr.L, W = fr.W, H = fr.H;
            int k = W >= 21 && rng.Random() < 0.5 ? 3 : 2;
            if (W < 12 || H < 256) return null;
            var s = new Section("multispan", fr);
            var edges = Enumerable.Range(0, k + 1).Select(i => R((double)W * i / k)).ToList();
            int p = PierSize(H);
            int rise = Q(Clamp((double)W / k * rng.Uniform(7, 11), 48, 160));
            rise = Math.Min(rise, H - Head - 32);
            int eave = H - rise;
            s.Fill((uf, vf, u, v) =>
            {
                int i = Enumerable.Range(0, k).Where(j => edges[j] <= v).Max();
                return eave + rise * (1 - AcrossF(vf, edges[i], edges[i + 1]));
            });
            int bay = BaySize(H, L, rng.Uniform(0.6, 0.8), 4 + p, 9);
            for (int ei = 1; ei < edges.Count - 1; ei++)
                foreach (int u in BayLines(L, bay, p))
                    s.Piers.Add(Pier(u, edges[ei] - p / 2, p));
            s.Info = new Dictionary<string, object> { ["spans"] = k, ["bay"] = bay, ["pier"] = p, ["eave"] = eave };
            return s;
        }

        /// <summary>Heavy concrete: deep downstand ribs every few cells, haunches along the walls.</summary>
        public static Section Ribbed(Frame fr, Rng rng)
        {
            int L = fr.L, W = fr.W, H = fr.H;
            if (H < Head + 32) return null;
            var s = new Section("ribbed", fr);
            int rib = Q(Clamp(H * 0.2, 32, 64));
            if (H - rib < Head) rib = Q(H - Head);
            if (rib < 16) return null;
            int every = rng.Choice(new[] { 2, 3 });
            int haunch = H - 32 >= Head ? 32 : 0;
            for (int u = 0; u < L; u++)
                for (int v = 0; v < W; v++)
                {
                    int h = H;
                    if (u % every == 0) h -= rib;
                    if (v == 0 || v == W - 1) h = Math.Min(h, H - haunch);
                    s.Set(u, v, h);
                }
            s.Info = new Dictionary<string, object> { ["rib"] = rib, ["every"] = every };
            return s;
        }

        /// <summary>Squat piers in a grid, each spreading into a drop panel under a flat slab.</summary>
        public static Section Mushroom(Frame fr, Rng rng)
        {
            int L = fr.L, W = fr.W, H = fr.H;
            if (W < 8 || H < Head + 16) return null;
            var s = new Section("mushroom", fr);
            int p = H >= 320 || W >= 16 ? 2 : 1;
            int bay = Clamp(R(H / 32.0 * 0.5) + 2, 4, 7);
            List<int> us = BayLines(L, bay, p), vs = BayLines(W, bay, p);
            if (us.Count == 0 || vs.Count == 0) return null;
            int drop = H - 32 >= Head ? 32 : H - 16 >= Head ? 16 : 0;
            var near = new HashSet<(int, int)>();
            foreach (int u0 in us)
                foreach (int v0 in vs)
                {
                    s.Piers.Add(Pier(u0, v0, p));
                    for (int u = u0 - 1; u < u0 + p + 1; u++)
                        for (int v = v0 - 1; v < v0 + p + 1; v++) near.Add((u, v));
                }
            for (int u = 0; u < L; u++)
                for (int v = 0; v < W; v++)
                    s.Set(u, v, near.Contains((u, v)) ? H - drop : H);
            s.Info = new Dictionary<string, object> { ["bay"] = bay, ["pier"] = p };
            return s;
        }

        /// <summary>The least a room can be: ceilings chamfered down along the long walls.</summary>
        public static Section Haunched(Frame fr, Rng rng)
        {
            int W = fr.W, H = fr.H;
            if (H < Head + 32 || W < 5) return null;
            var s = new Section("haunched", fr);
            s.Fill((uf, vf, u, v) =>
            {
                if (v == 0) return H - 32 * (1 - vf);
                if (v == W - 1) return H - 32 * (vf - (W - 1));
                return H;
            });
            return s;
        }

        /// <summary>A tray ceiling: a raised middle, coved down to the walls all round. For low rooms.</summary>
        public static Section Tray(Frame fr, Rng rng)
        {
            int L = fr.L, W = fr.W, H = fr.H;
            if (H < Head + 48 || W < 6 || L < 6) return null;
            var s = new Section("tray", fr);
            int band = W < 10 ? 1 : 2;
            int drop = Math.Min(64, Q(H - Head - 16));
            int low = H - drop;
            s.Fill((uf, vf, u, v) =>
            {
                double e = Math.Min(Math.Min(uf, L - uf), Math.Min(vf, W - vf));
                return low + drop * Math.Min(1.0, e / band);
            });                         // the four corner cells aren't planar: they stay stepped
            s.Info = new Dictionary<string, object> { ["band"] = band, ["drop"] = drop };
            return s;
        }

        /// <summary>A grid of shallow recesses between beams both ways (a waffle slab).</summary>
        public static Section Coffered(Frame fr, Rng rng)
        {
            int L = fr.L, W = fr.W, H = fr.H;
            if (H < Head + 32 || W < 6) return null;
            var s = new Section("coffered", fr);
            int k = W >= 14 ? 4 : 3;
            int depth = Math.Min(48, Q(H - Head));
            int ou = (L % k) / 2, ov = (W % k) / 2;
            for (int u = 0; u < L; u++)
                for (int v = 0; v < W; v++)
                {
                    bool beam = Mod(u - ou, k) == 0 || Mod(v - ov, k) == 0 || u == 0 || u == L - 1 || v == 0 || v == W - 1;
                    s.Set(u, v, beam ? H - depth : H);
                }
            s.Info = new Dictionary<string, object> { ["grid"] = k, ["depth"] = depth };
            return s;
        }

        static int Mod(int a, int b) => ((a % b) + b) % b;     // Python's %

        /// <summary>Rough-hewn rock: an irregular ceiling (a coarse random lattice, interpolated, in
        /// 16-unit steps) and a few rock pillars. Underground only.</summary>
        public static Section Cavern(Frame fr, Rng rng)
        {
            int L = fr.L, W = fr.W, H = fr.H;
            if (H < Head + 64 || W < 8) return null;
            var s = new Section("cavern", fr);
            int step = rng.Choice(new[] { 3, 4, 5 });
            int low = Math.Max(Head + 32, (int)(H * 0.55));
            var lat = new Dictionary<(int, int), double>();
            for (int i = 0; i < L / step + 2; i++)
                for (int j = 0; j < W / step + 2; j++) lat[(i, j)] = rng.Uniform(low, H);
            for (int u = 0; u < L; u++)
                for (int v = 0; v < W; v++)
                {
                    double i = (double)u / step, j = (double)v / step;
                    int i0 = (int)i, j0 = (int)j;
                    double a = i - i0, b = j - j0;
                    double h = lat[(i0, j0)] * (1 - a) * (1 - b) + lat[(i0 + 1, j0)] * a * (1 - b)
                             + lat[(i0, j0 + 1)] * (1 - a) * b + lat[(i0 + 1, j0 + 1)] * a * b;
                    s.Set(u, v, h);
                }
            int nRock = W >= 12 ? rng.Int(1, 3) : 0;
            for (int t = 0; t < nRock; t++)            // rock left standing
            {
                int p = rng.Choice(new[] { 2, 3 });
                int u0 = rng.Int(3, Math.Max(3, L - 4 - p)), v0 = rng.Int(3, Math.Max(3, W - 4 - p));
                s.Piers.Add(Pier(u0, v0, p));
            }
            s.Info = new Dictionary<string, object> { ["lattice"] = step, ["low"] = low };
            return s;
        }

        // ------------------------------------------------------------------ egyptian
        /// <summary>A forest of thick columns under a flat stone roof, a taller processional aisle down the
        /// middle lit by a clerestory.</summary>
        public static Section Hypostyle(Frame fr, Rng rng)
        {
            int L = fr.L, W = fr.W, H = fr.H;
            if (W < 11 || L < 10 || H < Head + 96) return null;
            var s = new Section("hypostyle", fr);
            int p = W >= 14 ? 2 : 1;
            int band = W >= 16 ? 4 : 3;                       // the processional aisle
            int b0 = (W - band) / 2;
            int b1 = b0 + band;
            int sideH = Q(Math.Max(Head + 48, H * rng.Uniform(0.6, 0.72)));
            for (int u = 0; u < L; u++)
                for (int v = 0; v < W; v++)
                    s.Set(u, v, b0 - p <= v && v < b1 + p ? H : sideH);
            int bay = p + rng.Choice(new[] { 2, 3 });         // dense: the columns are the room
            var us = BayLines(L, bay, p);
            var vs = new List<int>();
            for (int v = 1; v < b0 - p; v += bay) vs.Add(v);
            for (int v = b1 + 1; v < W - p; v += bay) vs.Add(v);
            vs = vs.Where(v => v + p <= b0 - 1 || v >= b1 + 1).ToList();   // the aisle stays clear
            foreach (int u in us)
                foreach (int v in vs)
                    if (v + p <= W - 1) s.Piers.Add(Pier(u, v, p));
            s.Info = new Dictionary<string, object> { ["bay"] = bay, ["pier"] = p, ["aisle"] = band, ["side_height"] = sideH };
            return s;
        }

        /// <summary>A flat stone roof on heavy beams laid across the room, close together.</summary>
        public static Section Lintel(Frame fr, Rng rng)
        {
            int L = fr.L, W = fr.W, H = fr.H;
            if (H < Head + 48) return null;
            var s = new Section("lintel", fr);
            int depth = Math.Min(64, Q(H - Head - 16));
            int every = rng.Choice(new[] { 2, 3 });
            for (int u = 0; u < L; u++)
                for (int v = 0; v < W; v++)
                    s.Set(u, v, u % every == 1 ? H - depth : H);
            s.Info = new Dictionary<string, object> { ["every"] = every, ["depth"] = depth };
            return s;
        }

        /// <summary>A corbelled ceiling: courses of stone stepping inwards to a narrow top (stepped by nature).</summary>
        public static Section Corbelled(Frame fr, Rng rng)
        {
            int L = fr.L, W = fr.W, H = fr.H;
            if (H < Head + 64 || W < 5) return null;
            var s = new Section("corbelled", fr);
            int course = rng.Choice(new[] { 32, 48 });
            int rise = Math.Min(Math.Min(Q(H - Head - 16), course * (W / 2)), Q(H * 0.5));
            int eave = H - rise;
            for (int u = 0; u < L; u++)
                for (int v = 0; v < W; v++)
                {
                    int e = Math.Min(v, W - 1 - v);               // courses counted in from the walls
                    s.Set(u, v, Math.Min(H, eave + course * e));
                }
            s.Info = new Dictionary<string, object> { ["course"] = course, ["eave"] = eave };
            return s;
        }

        /// <summary>Ceilings sloping down to both long walls over band cells (battered walls, chamfers).</summary>
        public static Section EdgeSlope(Frame fr, string name, int band, int drop)
        {
            int W = fr.W, H = fr.H;
            drop = Math.Min(drop, Q(H - Head - 16));
            if (drop < 32 || W < 2 * band + 2) return null;
            var s = new Section(name, fr);
            s.Fill((uf, vf, u, v) =>
            {
                double e = Math.Min(vf, W - vf);
                return H - drop * Math.Max(0.0, 1 - e / band);
            });
            s.Info = new Dictionary<string, object> { ["band"] = band, ["drop"] = drop };
            return s;
        }

        /// <summary>Walls leaning in as they rise (the battered walls of a temple or a pylon).</summary>
        public static Section Battered(Frame fr, Rng rng) =>
            EdgeSlope(fr, "battered", fr.W >= 14 ? 3 : 2, Q(fr.H * rng.Uniform(0.25, 0.35)));

        // ------------------------------------------------------------------ sci-fi
        /// <summary>A station module: the upper corners cut at 45 degrees (a near-octagonal section).</summary>
        public static Section Chamfered(Frame fr, Rng rng)
        {
            int band = fr.W >= 10 ? 2 : 1;
            return EdgeSlope(fr, "chamfered", band, 32 * band);
        }

        /// <summary>A hull: chamfered section with frames (ribs) every few cells along it.</summary>
        public static Section RibbedHull(Frame fr, Rng rng)
        {
            var s = Chamfered(fr, rng);
            if (s == null) return null;
            s.Name = "hull";
            int L = fr.L, W = fr.W, H = fr.H;
            int every = rng.Choice(new[] { 3, 4 });
            int drop = (int)s.Info["drop"];
            int rib = Math.Min(32, Q(H - Head - 16 - drop));
            if (rib < 16) return s;
            for (int u = 0; u < L; u++)
                if (u % every == 0)
                    for (int v = 0; v < W; v++)
                    {
                        s.Ceil[(u, v)] = Math.Min(s.Ceil[(u, v)], Q(H - drop - rib));
                        s.Forced.Add((u, v));
                    }
            s.Info["frames"] = every;
            return s;
        }

        /// <summary>A flat panelled ceiling with a raised light strip down the middle.</summary>
        public static Section Lightwell(Frame fr, Rng rng)
        {
            int L = fr.L, W = fr.W, H = fr.H;
            if (H < Head + 48 || W < 6) return null;
            var s = new Section("lightwell", fr);
            int band = W < 12 ? 2 : 3;
            int b0 = (W - band) / 2;
            for (int u = 0; u < L; u++)
                for (int v = 0; v < W; v++)
                    s.Set(u, v, b0 <= v && v < b0 + band ? H : H - 32);
            s.Info = new Dictionary<string, object> { ["strip"] = band };
            return s;
        }

        // ------------------------------------------------------------------ castle
        /// <summary>A flat timber ceiling on joists close together.</summary>
        public static Section Timber(Frame fr, Rng rng)
        {
            int L = fr.L, W = fr.W, H = fr.H;
            if (H < Head + 32) return null;
            var s = new Section("timber", fr);
            for (int u = 0; u < L; u++)
                for (int v = 0; v < W; v++)
                    s.Set(u, v, u % 2 == 0 ? H - 32 : H);
            s.Info = new Dictionary<string, object> { ["joists"] = 2 };
            return s;
        }

        /// <summary>A great hall: a steep roof open to the ridge, with tie-beams across at the eaves.</summary>
        public static Section Trussed(Frame fr, Rng rng)
        {
            var s = Gable(fr, rng);
            if (s == null) return null;
            s.Name = "trussed";
            int L = fr.L, W = fr.W, H = fr.H;
            int eave = (int)s.Info["eave"];
            int bay = BaySize(H, L, rng.Uniform(0.4, 0.55), 3, 7);
            foreach (int u in BayLines(L, bay))
                for (int v = 0; v < W; v++)
                {
                    s.Ceil[(u, v)] = eave;
                    s.Forced.Add((u, v));
                }
            s.Info["bay"] = bay;
            return s;
        }

        /// <summary>A dome over the room, rising from the walls to a crown in the middle, built in 16-unit
        /// courses (a corbelled or beehive dome). Best over centred plans.</summary>
        public static Section Dome(Frame fr, Rng rng)
        {
            int L = fr.L, W = fr.W, H = fr.H;
            if (H < Head + 64 || Math.Min(L, W) < 7) return null;
            var s = new Section("dome", fr);
            int rise = Math.Min(Q(H - Head - 16), Q(Math.Min(L, W) * rng.Uniform(10, 14)));
            if (rise < 48) return null;
            // courses about 1.5 cells wide, each a multiple of 16 high: clean concentric rings
            int n = Math.Max(3, Math.Min(Math.Min(rise / 16, (int)(Math.Min(L, W) / 3.0)), 7));
            int step = 16 * (int)Math.Ceiling((double)rise / n / 16);
            n = Math.Max(1, rise / step);
            rise = n * step;
            int spring = H - rise;
            double cu = L / 2.0, cv = W / 2.0;
            for (int u = 0; u < L; u++)
                for (int v = 0; v < W; v++)
                {
                    double r2 = Math.Pow((u + 0.5 - cu) / cu, 2) + Math.Pow((v + 0.5 - cv) / cv, 2);
                    int k = r2 < 1 ? Math.Min(n, (int)(n * Math.Sqrt(Math.Max(0.0, 1.0 - r2)) + 0.5)) : 0;
                    s.Set(u, v, spring + k * step);
                }
            s.Info = new Dictionary<string, object> { ["spring"] = spring, ["rise"] = rise, ["courses"] = n };
            return s;
        }

        public static readonly Dictionary<string, Func<Frame, Rng, Section>> Profiles = new Dictionary<string, Func<Frame, Rng, Section>>
        {
            ["basilica"] = (f, r) => Basilica(f, r), ["single-aisle"] = SingleAisle, ["vault"] = (f, r) => Vault(f, r),
            ["undercroft"] = Undercroft, ["gable"] = (f, r) => Gable(f, r), ["shed"] = Shed, ["portal"] = (f, r) => Portal(f, r),
            ["clearspan"] = Clearspan, ["sawtooth"] = Sawtooth, ["multispan"] = Multispan, ["ribbed"] = Ribbed,
            ["barrel"] = Barrel, ["mushroom"] = Mushroom, ["haunched"] = Haunched, ["tray"] = Tray, ["coffered"] = Coffered,
            ["cavern"] = Cavern, ["dome"] = Dome, ["hypostyle"] = Hypostyle, ["lintel"] = Lintel, ["corbelled"] = Corbelled,
            ["battered"] = Battered, ["chamfered"] = Chamfered, ["hull"] = RibbedHull, ["lightwell"] = Lightwell,
            ["timber"] = Timber, ["trussed"] = Trussed, ["plain"] = (f, r) => null,
        };

        static List<(string, double)> P(params (string, double)[] xs) => xs.ToList();

        /// <summary>style -> tier -> [(profile, weight)] (ordered); every tier keeps an option without piers.</summary>
        public static readonly Dictionary<string, Dictionary<string, List<(string Name, double W)>>> Palettes =
            new Dictionary<string, Dictionary<string, List<(string, double)>>>
            {
                ["gothic"] = new Dictionary<string, List<(string, double)>>
                {
                    ["grand"] = P(("basilica", 4.0), ("single-aisle", 1.5), ("vault", 1.5), ("undercroft", 1.0)),
                    ["major"] = P(("vault", 2.0), ("basilica", 1.5), ("undercroft", 1.5), ("single-aisle", 1.0), ("gable", 1.0), ("coffered", 0.5)),
                    ["minor"] = P(("vault", 2.0), ("gable", 1.0), ("tray", 1.0), ("coffered", 0.7), ("haunched", 0.5), ("plain", 0.5)),
                },
                ["industrial"] = new Dictionary<string, List<(string, double)>>
                {
                    ["grand"] = P(("portal", 2.0), ("sawtooth", 1.5), ("multispan", 2.0), ("clearspan", 1.0)),
                    ["major"] = P(("portal", 2.0), ("sawtooth", 1.0), ("clearspan", 1.0), ("multispan", 0.7), ("shed", 0.7), ("haunched", 0.5), ("plain", 0.5)),
                    ["minor"] = P(("portal", 1.5), ("clearspan", 0.7), ("shed", 0.7), ("haunched", 0.5), ("plain", 0.5)),
                },
                ["bunker"] = new Dictionary<string, List<(string, double)>>
                {
                    ["grand"] = P(("mushroom", 2.0), ("barrel", 1.5), ("ribbed", 1.5)),
                    ["major"] = P(("ribbed", 2.0), ("mushroom", 1.0), ("barrel", 1.0), ("coffered", 1.0), ("plain", 0.5)),
                    ["minor"] = P(("ribbed", 1.5), ("mushroom", 1.0), ("coffered", 1.0), ("barrel", 0.7), ("haunched", 0.7), ("plain", 0.5)),
                },
                ["compound"] = new Dictionary<string, List<(string, double)>>
                {
                    ["grand"] = P(("multispan", 2.0), ("portal", 1.5), ("gable", 1.5), ("clearspan", 1.0)),
                    ["major"] = P(("gable", 2.0), ("shed", 1.0), ("portal", 1.0), ("plain", 0.5)),
                    ["minor"] = P(("gable", 1.0), ("shed", 1.0), ("tray", 1.0), ("haunched", 0.5), ("plain", 0.5)),
                },
                ["egyptian"] = new Dictionary<string, List<(string, double)>>
                {
                    ["grand"] = P(("hypostyle", 3.0), ("corbelled", 1.0), ("battered", 1.0)),
                    ["major"] = P(("lintel", 2.0), ("corbelled", 1.5), ("battered", 1.0), ("hypostyle", 1.0)),
                    ["minor"] = P(("lintel", 1.0), ("corbelled", 1.0), ("battered", 1.0), ("plain", 0.5)),
                },
                ["scifi"] = new Dictionary<string, List<(string, double)>>
                {
                    ["grand"] = P(("hull", 2.0), ("chamfered", 1.5), ("lightwell", 1.5)),
                    ["major"] = P(("chamfered", 2.0), ("hull", 1.0), ("lightwell", 1.0), ("plain", 0.3)),
                    ["minor"] = P(("chamfered", 1.0), ("lightwell", 1.0), ("haunched", 0.5), ("plain", 0.5)),
                },
                ["castle"] = new Dictionary<string, List<(string, double)>>
                {
                    ["grand"] = P(("trussed", 3.0), ("barrel", 1.0), ("undercroft", 1.0)),
                    ["major"] = P(("timber", 2.0), ("barrel", 1.0), ("trussed", 1.0), ("undercroft", 1.0)),
                    ["minor"] = P(("timber", 1.0), ("barrel", 1.0), ("haunched", 1.0), ("plain", 0.5)),
                },
            };

        // ------------------------------------------------------------------ planning
        static bool Eligible(Room room) => !room.IsConnector && !SkipKinds.Contains(room.Kind);

        static long Volume(Room r) => (long)r.Cells().Count() * (r.Ceil - r.Floor);

        /// <summary>grand / major / minor for every eligible room: the largest space (and any mega-room)
        /// is grand, the next third major, the rest (and small rooms) minor.</summary>
        public static Dictionary<int, string> RankTiers(Level level)
        {
            var rooms = level.Rooms.Where(Eligible).OrderBy(r => -Volume(r)).ToList();
            int nMajor = Math.Max(1, (int)Math.Ceiling(rooms.Count * 0.3));
            var o = new Dictionary<int, string>();
            for (int rank = 0; rank < rooms.Count; rank++)
            {
                var room = rooms[rank];
                string tier = rank == 0 || room.Kind == "megaroom" ? "grand" : rank <= nMajor ? "major" : "minor";
                if (tier != "grand" && room.W * room.H < 60) tier = "minor";
                o[room.Id] = tier;
            }
            return o;
        }

        static readonly string[] CentredShapes = { "round", "hex", "octagon", "diamond" };

        /// <summary>Choose a section for every eligible room (rooms ranked by volume into tiers).</summary>
        public static Dictionary<int, Section> Plan(Level level)
        {
            var cfg = level.Config;
            string style = cfg.ArchStyle ?? "";
            if (!Palettes.ContainsKey(style)) return new Dictionary<int, Section>();
            var tiers = RankTiers(level);
            var rooms = level.Rooms.Where(r => tiers.ContainsKey(r.Id)).OrderBy(r => -Volume(r)).ToList();
            var bias = cfg.ArchWeights ?? new Dictionary<string, double>();
            var o = new Dictionary<int, Section>();
            var used = new Dictionary<string, int>();    // each use of a profile halves its weight
            foreach (var room in rooms)
            {
                var rng = Rng.FromLabel($"{cfg.Seed}/{cfg.Variant}/arch/{room.Id}");
                string tier = tiers[room.Id];
                var palette = new List<(string Name, double W)>(Palettes[style][tier]);
                if (cfg.Underground && tier == "grand")
                    palette = P(("cavern", 4.0), ("barrel", 1.0));      // the deep chamber is cut from the rock
                if (tier == "grand" || tier == "major")                 // profiles a recipe adds (variety: "dome")
                    foreach (var kv in bias)
                        if (Profiles.ContainsKey(kv.Key) && !palette.Any(p => p.Name == kv.Key) && kv.Value > 0)
                            palette.Add((kv.Key, 1.0));
                int di = palette.FindIndex(p => p.Name == "dome");
                if (di >= 0 && CentredShapes.Contains(room.Shape))
                    palette[di] = ("dome", palette[di].W * 3.0);      // a dome belongs over a centred plan
                else if (di >= 0 && Math.Max(room.W, room.H) > 1.2 * Math.Min(room.W, room.H))
                    palette.RemoveAt(di);                              // over a long hall it reads as a vault
                var pal = palette
                    .Select(p => (p.Name, W: p.W * (bias.TryGetValue(p.Name, out var b) ? b : 1.0) * Math.Pow(0.5, used.TryGetValue(p.Name, out var n) ? n : 0)))
                    .Where(p => p.W > 0).ToList();
                var fr = new Frame(room, rng.Random() < 0.5);
                Section sec = null;
                while (pal.Count > 0 && sec == null)
                {
                    string name = rng.Choices(pal.Select(p => p.Name).ToList(), pal.Select(p => p.W).ToList());
                    pal.RemoveAll(p => p.Name == name);
                    if (name == "plain")
                    {
                        used["plain"] = (used.TryGetValue("plain", out var np) ? np : 0) + 1;   // leaving a room plain is damped too
                        break;
                    }
                    sec = Profiles[name](fr, rng);
                }
                if (sec != null)
                {
                    used[sec.Name] = (used.TryGetValue(sec.Name, out var ns) ? ns : 0) + 1;
                    sec.Info["tier"] = tier;
                    o[room.Id] = sec;
                }
            }
            return o;
        }

        // ------------------------------------------------------------------ applying it to the grid
        static readonly (int, int)[] D4 = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        /// <summary>The room's floor-level cells stay one connected piece.</summary>
        public static bool BaseConnected(Level level, Room room)
        {
            var bs = new HashSet<Cell>();
            Cell? first = null;
            foreach (var c in room.Cells())
                if (level.RoomSpans(c, room).Any(sp => sp.Floor == room.Floor))
                {
                    bs.Add(c);
                    first ??= c;
                }
            if (first == null) return false;
            var seen = new HashSet<Cell> { first.Value };
            var stack = new Stack<Cell>();
            stack.Push(first.Value);
            while (stack.Count > 0)
            {
                var a = stack.Pop();
                foreach (var (dx, dy) in D4)
                {
                    var n = a.Offset(dx, dy);
                    if (bs.Contains(n) && seen.Add(n)) stack.Push(n);
                }
            }
            return seen.Count == bs.Count;
        }

        /// <summary>level.room_spans(c, room) == [(room.floor, room.ceil)].</summary>
        internal static bool IsPlain(Level level, Room room, Cell c)
        {
            var rs = level.RoomSpans(c, room);
            return rs.Count == 1 && rs[0].Floor == room.Floor && rs[0].Ceil == room.Ceil;
        }

        internal static HashSet<Cell> Around(IEnumerable<Cell> cells, int r)
        {
            var o = new HashSet<Cell>();
            foreach (var c in cells)
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++) o.Add(c.Offset(dx, dy));
            return o;
        }

        internal static void Restore(Level level, Dictionary<Cell, List<Span>> snap)
        {
            foreach (var kv in snap)
                if (kv.Value.Count > 0) level.Spans[kv.Key] = new List<Span>(kv.Value); else level.Spans.Remove(kv.Key);
        }

        internal static Dictionary<Cell, List<Span>> Snap(Level level, IEnumerable<Cell> cells) =>
            cells.Distinct().ToDictionary(c => c, c => level.Spans.TryGetValue(c, out var s) ? new List<Span>(s) : new List<Span>());

        /// <summary>Before the room's features: piers become solid, with a free ring kept around each.</summary>
        public static int PlacePiers(Level level, Room room, Section sec, RoomCtx ctx, string tag = "piers")
        {
            var fr = sec.Frame;
            int placed = 0;
            var nearDoor = Around(ctx.Doors, 2);
            foreach (var group in sec.Piers)
            {
                var cells = group.Select(g => fr.Cell(g.Item1, g.Item2)).ToList();
                var cellSet = new HashSet<Cell>(cells);
                var ring = Around(cells, 1);
                ring.ExceptWith(cellSet);
                ring.RemoveWhere(c => !room.Contains(c));
                // a pier keeps 2 cells from any doorway cell, stands on the plain floor, and the ring
                // around it stays walkable
                bool ok = cells.All(c => room.Contains(c) && !room.IsDiag(c.X, c.Y) && !ctx.Used.Contains(c)
                                         && !nearDoor.Contains(c) && IsPlain(level, room, c));
                ok = ok && ring.All(c => level.RoomSpans(c, room).Count > 0 && !room.IsDiag(c.X, c.Y));
                if (!ok) continue;
                var snap = Snap(level, cells);
                foreach (var c in cells) level.ReplaceIn(c, room.Slot.Lo, room.Slot.Hi, new Span[0]);
                if (!BaseConnected(level, room))
                {
                    Restore(level, snap);
                    continue;
                }
                ctx.Used.UnionWith(cells);
                ctx.Used.UnionWith(ring);
                foreach (var c in cells) level.PartSolid[c] = $"room:{room.Id}/{tag}";
                placed++;
                sec.Placed.Add(group);
            }
            return placed;
        }

        /// <summary>The surface at a cell's corners (x0y0, x1y0, x1y1, x0y1), above the room's floor.</summary>
        static double[] Corners(Frame fr, Section sec, Cell c)
        {
            var r = fr.Room;
            var (cu, cv) = fr.UV(c);
            var o = new double[4];
            var ds = new[] { (0, 0), (1, 0), (1, 1), (0, 1) };
            for (int i = 0; i < 4; i++)
            {
                int X = c.X - r.X + ds[i].Item1, Y = c.Y - r.Y + ds[i].Item2;
                double uf = fr.AlongX ? X : Y, vf = fr.AlongX ? Y : X;
                if (fr.Flip) vf = fr.W - vf;
                o[i] = sec.Surf(uf, vf, cu, cv);
            }
            return o;
        }

        /// <summary>After the features: lower ceilings to the profile, never below what must stay open.
        /// With a smooth surface (and ArchSmooth), a cell whose corners lie in one plane gets a sloped
        /// ceiling: the span keeps its lowest point, and level.CeilSlope holds the corners.</summary>
        public static int ApplyCeilings(Level level, Room room, Section sec)
        {
            var fr = sec.Frame;
            bool smooth = sec.Surf != null && level.Config.ArchSmooth;
            var gallery = new HashSet<Cell>(Decorate.MetaCells(room, "gallery"));
            int lowered = 0;
            foreach (var c in room.Cells())
            {
                if (gallery.Contains(c) || level.Sky.Contains(c)) continue;
                var spans = level.RoomSpans(c, room);
                if (spans.Count == 0 || spans[spans.Count - 1].Ceil != room.Ceil) continue;
                var uv = fr.UV(c);
                if (!sec.Ceil.TryGetValue(uv, out int h)) continue;
                var topSpan = spans[spans.Count - 1];
                int fTop = topSpan.Floor;
                int need = fTop + Math.Max(Head, level.Config.ArchHeadroom);
                foreach (var (dx, dy) in D4)
                {
                    var n = c.Offset(dx, dy);
                    if (room.Contains(n)) continue;
                    foreach (var sp in level.SpansIn(n, fTop, room.Ceil))   // an opening beside this cell
                        need = Math.Max(need, sp.Ceil);
                }
                var all = level.Spans[c];
                if (smooth && !level.Diag.ContainsKey(c) && all[all.Count - 1].Equals(topSpan) && !sec.Forced.Contains(uv))
                {
                    var zs = Corners(fr, sec, c).Select(z => room.Floor + z).ToArray();
                    bool planar = Math.Abs((zs[0] + zs[2]) - (zs[1] + zs[3])) <= 1.0;
                    if (planar && zs.Min() >= need && zs.Max() <= room.Ceil && zs.Min() < room.Ceil)
                    {
                        var zi = zs.Select(z => (int)Math.Round(z)).ToArray();
                        int flat = Math.Max(need, FloorDiv(zi.Min(), 16) * 16);
                        level.ReplaceIn(c, fTop, room.Ceil, new[] { new Span(fTop, flat) });
                        level.CeilSlope[c] = (zi[0], zi[1], zi[2], zi[3]);
                        level.PartCeiling.TryAdd(c, $"room:{room.Id}/{sec.Name}");
                        lowered++;
                        continue;
                    }
                }
                if (room.Floor + h >= room.Ceil) continue;     // (the apex of a smooth vault is handled above)
                int fresh = Math.Min(room.Ceil, Math.Max(room.Floor + h, need));
                if (fresh >= topSpan.Ceil) continue;
                level.ReplaceIn(c, fTop, room.Ceil, new[] { new Span(fTop, fresh) });
                if (level.DiagSpan.TryGetValue(c, out var ds) && ds.Ceil == room.Ceil)
                    level.DiagSpan[c] = new Span(ds.Floor, fresh);
                level.PartCeiling.TryAdd(c, $"room:{room.Id}/{sec.Name}");     // (capitals keep their name)
                lowered++;
            }
            return lowered;
        }
    }
}
