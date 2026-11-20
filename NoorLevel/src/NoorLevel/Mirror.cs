using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NoorLevel
{
    /// <summary>Mirrored team maps (capture the flag): one half generated, the other its mirror image (mirror.py).
    ///
    /// Team maps are judged on one thing above all: neither side may have an advantage. The surest way is
    /// the one the classic CTF maps use: both bases are the same base, mirrored. So:
    /// <list type="number">
    /// <item>A half. The generator builds an ordinary arena-mode level, validated as one circuit.</item>
    /// <item>Lanes. From the rooms on the half's open (east) side, 2-3 straight lanes, 5 wide, run out to
    /// the seam (a vertical line a few cells past everything), spread apart, each from a flat stretch of
    /// its room's wall through rock only. On the middle lane a midfield chamber at the seam, taller.</item>
    /// <item>The base. The room farthest (walking) from the lanes holds the team's flag; team spawns at
    /// the back of the base.</item>
    /// <item>The mirror. Every cell, span, slope, 45-degree corner, hazard, part name, room, corridor,
    /// entity and link is reflected across the seam (x -> 2A - 1 - x) and renumbered; facing angles turn
    /// round (180 - a), the flag and spawns become team 2. The lanes meet their mirror images at the seam.</item>
    /// </list>
    /// BadLevel puts one quad on the seam (and takes the half's out); NoorLevel places no power-ups, so
    /// only the midfield chamber is there. The validator then checks the whole map as one arena.</summary>
    public static class Mirror
    {
        public static readonly Dictionary<Corner, Corner> Flip = new Dictionary<Corner, Corner>
            { [Corner.SW] = Corner.SE, [Corner.SE] = Corner.SW, [Corner.NW] = Corner.NE, [Corner.NE] = Corner.NW };
        public static readonly int LaneR = 2;     // 5 wide
        public static readonly int Gap = 7;       // rock between the half's easternmost cell and the seam (room for the midfield)

        public sealed class Lane
        {
            public Room Room;
            public int Y, X0, Floor, Height;
            public List<Cell> Cut = new List<Cell>();
            public double Score;
            public int Corridor = -1;
            public bool Chamber;
        }

        public static (int, int, int, int) FlipSlope((int, int, int, int) t) => (t.Item2, t.Item1, t.Item4, t.Item3);   // corners mirrored in x

        public static int LaneHeight(Level level) => Math.Max(160, level.Config.CorridorHeight);

        public static bool Free(Level level, Cell c, int f, int top) => level.FreeAt(c, f, top, 16) && !level.Diag.ContainsKey(c);

        /// <summary>Straight lanes from east-facing room walls to the seam column A-1.</summary>
        public static List<Lane> CutLanes(Level level, int A)
        {
            int h0 = LaneHeight(level);
            var cands = new List<Lane>();
            foreach (var r in level.Rooms)
            {
                if (r.IsConnector || r.Kind == "secret" || r.Kind == "overlook") continue;
                int hmin = level.Config.PlayerHeight + 32;
                string mine = $"room:{r.Id}/";
                for (int y = r.Y + LaneR + 1; y < r.Y + r.H - LaneR - 1; y++)
                {
                    var band = Enumerable.Range(y - LaneR, 2 * LaneR + 1).ToList();
                    // the room's east edge on these rows: flat (shaped rooms have cut corners)
                    var edges = new HashSet<int?>();
                    foreach (var yy in band)
                    {
                        int? e = null;
                        for (int x = r.X; x < r.X + r.W; x++) if (r.Contains(x, yy)) e = x;
                        edges.Add(e);
                    }
                    if (edges.Count != 1 || edges.Contains(null)) continue;
                    int ex = edges.First().Value + 1;          // the first cell east of the room: its wall
                    // the room side: the edge column and the one inside it open at the room's floor, or a
                    // piece of the room's own dressing (a pilaster, its base, a pier) the lane may cut through
                    int h = Math.Min(h0, r.Ceil - r.Floor);
                    bool edge = true;
                    var cut = new List<Cell>();
                    foreach (var k in new[] { 1, 2 })
                    {
                        foreach (var yy in band)
                        {
                            var c = new Cell(ex - k, yy);
                            if (!r.Contains(c) || level.Diag.ContainsKey(c)) { edge = false; break; }
                            var sp = level.Spans.TryGetValue(c, out var ss) ? ss.Where(s => s.Floor == r.Floor).ToList() : new List<Span>();
                            string part = level.PartSolid.TryGetValue(c, out var ps) && !string.IsNullOrEmpty(ps) ? ps
                                : level.PartCells.TryGetValue(c, out var pc) ? pc : "";
                            if (sp.Count > 0) h = Math.Min(h, sp[0].Ceil - r.Floor);
                            else if (part.StartsWith(mine)) cut.Add(c);
                            else { edge = false; break; }
                        }
                        if (!edge) break;
                    }
                    if (!edge || h < hmin) continue;
                    bool ok = true;
                    for (int x = ex; x < A && ok; x++)
                    {
                        var ring = x > ex ? Enumerable.Range(y - LaneR - 1, 2 * LaneR + 3) : band;
                        if (!ring.All(yy => Free(level, new Cell(x, yy), r.Floor, r.Floor + h))) ok = false;
                    }
                    if (ok)
                        cands.Add(new Lane { Room = r, Y = y, X0 = ex, Floor = r.Floor, Height = h, Cut = cut,
                                             Score = (A - ex) + Math.Abs(y - (r.Y + r.H / 2.0)) * 0.5 });
                }
            }
            cands = cands.OrderBy(c => c.Score).ToList();
            var lanes = new List<Lane>();
            foreach (var c in cands)
            {
                if (lanes.Any(l => Math.Abs(c.Y - l.Y) < 2 * LaneR + 8)) continue;
                if (lanes.Any(l => c.Room == l.Room)) continue;
                lanes.Add(c);
                if (lanes.Count == 3) break;
            }
            if (lanes.Count < 2) throw new LayoutException($"team map: only {lanes.Count} lane(s) reach the seam");
            return lanes.OrderBy(l => l.Y).ToList();
        }

        /// <summary>Carve the lanes as corridors; a midfield chamber on the middle one. Returns that lane.</summary>
        public static Lane CarveLanes(Level level, List<Lane> lanes, int A)
        {
            var mid = lanes[lanes.Count / 2];
            foreach (var lane in lanes)
            {
                int cid = level.Corridors.Count;
                int y = lane.Y, f = lane.Floor, h = lane.Height;
                var path = Enumerable.Range(lane.X0, A - lane.X0).Select(x => new Cell(x, y)).ToList();
                var corr = new Corridor { Id = cid, Edge = -1, A = lane.Room.Id, B = lane.Room.Id, Path = path,
                                          Floors = Enumerable.Repeat(f, path.Count).ToList(), Radius = LaneR, Height = h };
                string roomTag = $"room:{lane.Room.Id}";
                foreach (var c in lane.Cut)                    // through a pilaster (or its base) in the room's wall
                {
                    var gone = level.SpansIn(c, f, f + h).ToList();
                    int top = new[] { f + h }.Concat(gone.Select(s => s.Ceil)).Max();
                    level.ReplaceIn(c, f, f + h, new[] { new Span(f, top) });
                    if (!level.Owners.TryGetValue(c, out var os)) level.Owners[c] = os = new List<Owner>();
                    if (!os.Any(o => o.Name == roomTag)) os.Add(new Owner(f, top, roomTag));
                    level.PartSolid.Remove(c);
                    level.PartCells.Remove(c);
                }
                foreach (var p in path)
                    for (int yy = y - LaneR; yy <= y + LaneR; yy++)
                    {
                        var c = new Cell(p.X, yy);
                        level.AddSpan(c, new Span(f, f + h), $"corridor:{cid}");
                        level.PartCells[c] = $"corridor:{cid}/lane";
                    }
                level.Corridors.Add(corr);
                lane.Corridor = cid;
                if (lane == mid)
                {
                    // the midfield: 5 x 9 cells on this side of the seam (10 x 9 once mirrored), 64 higher
                    int top = f + h + 64;
                    var cells = new List<Cell>();
                    for (int x = A - 5; x < A; x++) for (int yy = y - 4; yy < y + 5; yy++) cells.Add(new Cell(x, yy));
                    var ring = new List<Cell>();
                    for (int x = A - 6; x < A; x++) for (int yy = y - 5; yy < y + 6; yy++) ring.Add(new Cell(x, yy));
                    string ctag = $"corridor:{cid}";
                    if (ring.All(c => (level.Spans.TryGetValue(c, out var ss) && level.OwnerOf(c, ss[0]) == ctag) || Free(level, c, f, top)))
                    {
                        foreach (var c in cells)
                        {
                            if (level.Spans.ContainsKey(c))     // a lane cell: raise its ceiling
                            {
                                level.ReplaceIn(c, f, f + h, new[] { new Span(f, top) });
                                if (level.Owners.TryGetValue(c, out var os))
                                    level.Owners[c] = os.Select(o => new Owner(o.Lo, o.Name == ctag ? top : o.Hi, o.Name)).ToList();
                            }
                            else level.AddSpan(c, new Span(f, top), ctag);
                            level.PartCells[c] = $"{ctag}/midfield";
                        }
                        lane.Chamber = true;
                    }
                }
            }
            return mid;
        }

        static readonly Regex OwnerRe = new Regex(@"^(room|corridor):(\d+)");

        public static string Rename(string name, int R, int C)
        {
            name = OwnerRe.Replace(name, m =>
            {
                int k = int.Parse(m.Groups[2].Value);
                return $"{m.Groups[1].Value}:{k + (m.Groups[1].Value == "room" ? R : C)}";
            }, 1);
            if (name.StartsWith("window:")) name = "window:m" + name.Substring(7);
            return name;
        }

        /// <summary>A deep copy of a meta value (dicts and lists copied, everything else shared: values,
        /// strings and cells are immutable).</summary>
        public static object DeepCopy(object v)
        {
            if (v == null) return null;
            var t = v.GetType();
            if (v is IDictionary d && t.IsGenericType)
            {
                var nd = (IDictionary)Activator.CreateInstance(t);
                foreach (DictionaryEntry kv in d) nd[kv.Key] = DeepCopy(kv.Value);
                return nd;
            }
            if (v is IList l && t.IsGenericType)
            {
                var nl = (IList)Activator.CreateInstance(t);
                foreach (var x in l) nl.Add(DeepCopy(x));
                return nl;
            }
            if (v is Array a) return a.Clone();
            return v;
        }

        static Dictionary<string, object> CopyMeta(Dictionary<string, object> meta) =>
            meta.ToDictionary(kv => kv.Key, kv => DeepCopy(kv.Value));

        static int ToInt(object o) => Convert.ToInt32(o);

        /// <summary>The level plus its mirror image across the seam between columns A-1 and A.</summary>
        public static Level DoMirror(Level level, int A)
        {
            int s = level.CellSize;
            int R = level.Rooms.Count, C = level.Corridors.Count;
            var g = level.Graph;
            int N = g.Nodes.Count, E = g.Edges.Count;
            int Mx(int x) => 2 * A - 1 - x;
            double MX(double X) => 2.0 * A * s - X;
            Cell Mc(Cell c) => new Cell(Mx(c.X), c.Y);
            Vec3 Mpos(Vec3 p) => new Vec3(MX(p.X), p.Y, p.Z);

            foreach (var kv in level.Spans.ToList()) level.Spans[Mc(kv.Key)] = new List<Span>(kv.Value);
            foreach (var kv in level.Owners.ToList())
                level.Owners[Mc(kv.Key)] = kv.Value.Select(o => new Owner(o.Lo, o.Hi, Rename(o.Name, R, C))).ToList();
            foreach (var kv in level.Diag.ToList()) level.Diag[Mc(kv.Key)] = Flip[kv.Value];
            foreach (var kv in level.DiagSpan.ToList()) level.DiagSpan[Mc(kv.Key)] = kv.Value;
            foreach (var st in new[] { level.Glass, level.Sky, level.SkyWalls })   // (sky walls: added in NoorLevel)
                foreach (var c in st.ToList()) st.Add(Mc(c));
            foreach (var d in new[] { level.CeilSlope, level.FloorSlope })
                foreach (var kv in d.ToList()) d[Mc(kv.Key)] = FlipSlope(kv.Value);
            foreach (var kv in level.Hazard.ToList()) level.Hazard[Mc(kv.Key)] = kv.Value;
            foreach (var kv in level.Water.ToList()) level.Water[Mc(kv.Key)] = kv.Value;   // (water: added in NoorLevel)
            foreach (var d in new[] { level.PartSolid, level.PartCells, level.PartCeiling })
                foreach (var kv in d.ToList()) d[Mc(kv.Key)] = Rename(kv.Value, R, C);

            // the mission graph: a twin of every node and edge
            foreach (var n in g.Nodes.ToList())
                g.Nodes.Add(new Node { Id = n.Id + N, Kind = n.Kind, Size = n.Size, Zone = n.Zone, Depth = n.Depth, Key = n.Key,
                                       Storey = n.Storey, Top = n.Top, Main = n.Main, Style = n.Style });
            foreach (var e in g.Edges.ToList())
                g.Edges.Add(new Edge { Id = e.Id + E, A = e.A + N, B = e.B + N, Kind = e.Kind, Key = e.Key, Tree = e.Tree,
                                       Connector = e.Connector, Merged = e.Merged });

            foreach (var r in level.Rooms.ToList())
            {
                var m = new Room
                {
                    Id = r.Id + R, Node = r.Node + N, Kind = r.Kind, X = 2 * A - r.X - r.W, Y = r.Y, W = r.W, H = r.H,
                    Floor = r.Floor, Ceil = r.Ceil, Storey = r.Storey, TopFloor = r.TopFloor, Shape = r.Shape,
                    Features = new List<string>(r.Features), Meta = CopyMeta(r.Meta),
                    Mask = r.Mask == null ? null : new HashSet<Cell>(r.Mask.Select(c => new Cell(r.W - 1 - c.X, c.Y))),
                    Diag = r.Diag.ToDictionary(kv => new Cell(r.W - 1 - kv.Key.X, kv.Key.Y), kv => Flip[kv.Value]),
                };
                m.Meta["mirror_of"] = r.Id;
                level.Rooms.Add(m);
            }
            foreach (var c in level.Corridors.ToList())
            {
                var m = new Corridor
                {
                    Id = c.Id + C, Edge = c.Edge >= 0 ? c.Edge + E : -1, A = c.A + R, B = c.B + R,
                    Path = c.Path.Select(Mc).ToList(), Floors = new List<int>(c.Floors), Kind = c.Kind, Key = c.Key,
                    Radius = c.Radius, Connector = c.Connector, Height = c.Height, Diagonal = c.Diagonal, Vent = c.Vent,
                    Junctions = c.Junctions.Select(j => j + C).ToList(),
                };
                foreach (var b in c.Beats)
                {
                    var nb = CopyMeta(b);
                    if (b.TryGetValue("cells", out var cells) && cells is IEnumerable seq)
                    {
                        var mc = new List<List<int>>();
                        foreach (var o in seq)
                            switch (o)
                            {
                                case Cell cc: mc.Add(new List<int> { Mx(cc.X), cc.Y }); break;
                                case ValueTuple<int, int> tt: mc.Add(new List<int> { Mx(tt.Item1), tt.Item2 }); break;
                                case IList li when li.Count >= 2: mc.Add(new List<int> { Mx(ToInt(li[0])), ToInt(li[1]) }); break;
                            }
                        nb["cells"] = mc;
                    }
                    if (b.TryGetValue("pos", out var pos))
                    {
                        if (pos is Vec3 v) nb["pos"] = Mpos(v);
                        else if (pos is IList pl && pl.Count > 0)
                        {
                            var np = new List<double> { MX(Convert.ToDouble(pl[0])) };
                            for (int i = 1; i < pl.Count; i++) np.Add(Convert.ToDouble(pl[i]));
                            nb["pos"] = np;
                        }
                    }
                    m.Beats.Add(nb);
                }
                level.Corridors.Add(m);
            }
            level.Halls.AddRange(level.Halls.ToList().Select(h => h.Select(i => i + R).ToList()).ToList());

            foreach (var e in level.Entities.ToList())
            {
                if (e.Kind == "player_start") continue;          // one start, on team 1's side
                var props = CopyMeta(e.Props);
                if (props.TryGetValue("room", out var rm) && rm != null) props["room"] = ToInt(rm) + R;
                if (props.TryGetValue("corridor", out var cr) && cr != null) props["corridor"] = ToInt(cr) + C;
                if (props.TryGetValue("angle", out var an) && an != null) props["angle"] = (((180 - ToInt(an)) % 360) + 360) % 360;
                if (props.TryGetValue("target", out var tg) && tg is Vec3 tv) props["target"] = Mpos(tv);
                if (props.ContainsKey("team")) props["team"] = 2;
                var ne = new Entity(e.Kind, Mpos(e.Pos)) { Props = props };
                if (e.Bounds != null)
                {
                    var (lo, hi) = e.Bounds.Value;
                    ne.Bounds = (new Vec3(MX(hi.X), lo.Y, lo.Z), new Vec3(MX(lo.X), hi.Y, hi.Z));
                }
                level.Entities.Add(ne);
            }
            foreach (var L in level.Links.ToList())
                level.Links.Add(new Link { Kind = L.Kind, A = Mpos(L.A), B = Mpos(L.B), TwoWay = L.TwoWay, Interaction = L.Interaction,
                                           Accessible = L.Accessible, Room = L.Room + R });
            level.Width = 2 * A + 1;
            return level;
        }

        /// <summary>Team spawns at the back of the base: deep rooms first (farthest walk from the lanes), one per
        /// room, then more in the big ones; never in the front 40% of the base, off doorways, facing in.</summary>
        public static void TeamSpawns(Level level, List<Room> rooms, Dictionary<(int, int, int), int> toLane, HashSet<Cell> used,
                                      int flagD, int want = 8)
        {
            int s = level.CellSize;
            var old = level.Entities.Where(e => e.Kind == "spawn").ToList();
            level.Entities.RemoveAll(e => e.Kind == "spawn");
            var cands = new List<(int D, Room R, Cell C, double Ang)>();
            foreach (var r in rooms)
            {
                double cx = r.X + r.W / 2.0, cy = r.Y + r.H / 2.0;
                foreach (var c in r.Cells())
                {
                    var sp = level.RoomSpans(c, r);
                    if (sp.Count != 1 || sp[0].Floor != r.Floor || used.Contains(c) || level.Diag.ContainsKey(c)) continue;
                    // off doorways: the 5x5 round the cell is this room (or solid)
                    bool door = false;
                    for (int dx = -2; dx <= 2 && !door; dx++)
                        for (int dy = -2; dy <= 2 && !door; dy++)
                        {
                            var n = c.Offset(dx, dy);
                            if (level.Spans.TryGetValue(n, out var ns) && ns.Count > 0 && !r.Contains(n)) door = true;
                        }
                    if (door) continue;
                    if (!toLane.TryGetValue((c.X, c.Y, level.Spans[c].IndexOf(sp[0])), out var d) || d < 0.4 * flagD) continue;
                    cands.Add((d, r, c, Math.Atan2(cy - (c.Y + 0.5), cx - (c.X + 0.5)) * 180 / Math.PI));
                }
            }
            cands = cands.OrderBy(t => -t.D).ToList();
            var picked = new List<(int D, Room R, Cell C, double Ang)>();
            for (int rnd = 0; rnd < 3; rnd++)                    // one per room, then two, then three
                foreach (var t in cands)
                {
                    if (picked.Count >= want) break;
                    if (picked.Count(p => p.R == t.R) > rnd) continue;
                    if (picked.Any(p => Math.Abs(t.C.X - p.C.X) + Math.Abs(t.C.Y - p.C.Y) < 5)) continue;
                    picked.Add(t);
                }
            if (picked.Count < 4)                                 // a tiny base: keep the arena's spawns
            {
                level.Entities.AddRange(old);
                foreach (var e in old) e.Props["team"] = 1;
                return;
            }
            Entity last = null;
            foreach (var (d, r, c, ang) in picked)
            {
                int a = (int)Math.Round(ang / 45.0) * 45;
                last = new Entity("spawn", new Vec3((c.X + 0.5) * s, (c.Y + 0.5) * s, r.Floor + 32.0))
                    .With("angle", ((a % 360) + 360) % 360).With("room", r.Id).With("team", 1);
                level.Entities.Add(last);
            }
            foreach (var e in level.Entities)
                if (e.Kind == "player_start")
                {
                    e.Pos = last.Pos;
                    e.Props["angle"] = last.Props["angle"];
                }
        }

        /// <summary>A finished arena half -> a mirrored two-team map (see the class summary).</summary>
        public static Level MakeTeamMap(Level level)
        {
            int s = level.CellSize;
            int A = level.Spans.Keys.Max(c => c.X) + 1 + Gap;
            var lanes = CutLanes(level, A);
            var mid = CarveLanes(level, lanes, A);

            // the base: the room farthest from the lanes holds the flag
            var entries = lanes.Select(l => new Vec3((l.X0 + 0.5) * s, (l.Y + 0.5) * s, l.Floor + 24)).ToList();
            var toLane = Arena.Field(level, entries);
            var rooms = level.Rooms.Where(r => !r.IsConnector && r.Kind != "secret" && r.Kind != "overlook").ToList();
            var used = new HashSet<Cell>(level.Entities.Select(e => new Cell((int)Math.Floor(e.Pos.X / s), (int)Math.Floor(e.Pos.Y / s))));
            (int D, Room R, Cell A)? best = null;
            foreach (var r in rooms)
            {
                var a = Arena.Anchor(level, r);
                if (a == null) continue;
                var sp = level.RoomSpans(a.Value, r);
                int d = toLane.TryGetValue((a.Value.X, a.Value.Y, level.Spans[a.Value].IndexOf(sp[0])), out var dd) ? dd : -1;
                if (best == null || d > best.Value.D) best = (d, r, a.Value);
            }
            if (best == null || best.Value.D < 0) throw new LayoutException("team map: no base room");
            var (flagD, bas, anc) = best.Value;
            var cells = bas.Cells()
                .Where(c => !used.Contains(c) && !level.Diag.ContainsKey(c) && level.RoomSpans(c, bas).Count > 0
                            && level.RoomSpans(c, bas)[0].Floor == bas.Floor)
                .OrderBy(c => (c.X - anc.X) * (c.X - anc.X) + (c.Y - anc.Y) * (c.Y - anc.Y)).ToList();
            if (cells.Count == 0) throw new LayoutException("team map: no spot for the flag");
            var fc = cells[0];
            level.Entities.Add(new Entity("flag", new Vec3((fc.X + 0.5) * s, (fc.Y + 0.5) * s, bas.Floor)).With("team", 1).With("room", bas.Id));
            TeamSpawns(level, rooms, toLane, used, flagD);
            level.Entities.RemoveAll(e => e.Kind == "quad");     // (none in NoorLevel: no power-ups)
            int halfRooms = level.Rooms.Count;

            DoMirror(level, A);
            // BadLevel: one quad on the seam, in the midfield chamber (a power-up: left out)
            level.Arena["team"] = true;
            level.Arena["lanes"] = lanes.Count;
            level.Arena["seam"] = A;
            level.Arena["base_room"] = bas.Id;
            level.Arena["base_room_2"] = bas.Id + halfRooms;
            level.Arena["flag_distance"] = flagD;
            level.Arena["midfield"] = mid.Chamber;
            level.Notes.Add($"team map: {lanes.Count} lanes, seam at x={A}, flag {flagD} steps from the lanes");
            return level;
        }
    }
}
