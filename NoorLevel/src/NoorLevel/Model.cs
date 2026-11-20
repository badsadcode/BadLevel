using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>A grid cell (x, y). One cell is <see cref="GenConfig.Cell"/> units wide (32: a player).</summary>
    public readonly struct Cell : IEquatable<Cell>, IComparable<Cell>
    {
        public readonly int X, Y;
        public Cell(int x, int y) { X = x; Y = y; }
        public bool Equals(Cell o) => X == o.X && Y == o.Y;
        public override bool Equals(object obj) => obj is Cell c && Equals(c);
        public override int GetHashCode() => unchecked(X * 73856093 ^ Y * 19349663);
        public int CompareTo(Cell o) => X != o.X ? X.CompareTo(o.X) : Y.CompareTo(o.Y);
        public static bool operator ==(Cell a, Cell b) => a.Equals(b);
        public static bool operator !=(Cell a, Cell b) => !a.Equals(b);
        public Cell Offset(int dx, int dy) => new Cell(X + dx, Y + dy);
        public override string ToString() => $"({X},{Y})";
    }

    /// <summary>An empty vertical span of a column: open space from Floor up to Ceil (units).</summary>
    public readonly struct Span : IEquatable<Span>
    {
        public readonly int Floor, Ceil;
        public Span(int floor, int ceil) { Floor = floor; Ceil = ceil; }
        public int Height => Ceil - Floor;
        public bool Equals(Span o) => Floor == o.Floor && Ceil == o.Ceil;
        public override bool Equals(object obj) => obj is Span s && Equals(s);
        public override int GetHashCode() => Floor * 7919 + Ceil;
        public override string ToString() => $"[{Floor},{Ceil})";
    }

    /// <summary>The corner a 45-degree half cell's solid triangle points at (+Y is north).</summary>
    public enum Corner { SW, SE, NW, NE }

    public struct Vec3
    {
        public double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public override string ToString() => $"({X:0.#},{Y:0.#},{Z:0.#})";
    }

    /// <summary>Who owns a vertical slot of a column (a room, a corridor, a window...).</summary>
    public struct Owner
    {
        public int Lo, Hi;
        public string Name;
        public Owner(int lo, int hi, string name) { Lo = lo; Hi = hi; Name = name; }
    }

    public sealed class Room
    {
        public int Id, Node;
        public string Kind;
        public int X, Y, W, H;
        public int Floor, Ceil;
        public int Storey;
        public int? TopFloor;                 // vertical connectors: the upper landing's floor
        public string Shape = "rect";
        public List<string> Features = new List<string>();
        public Dictionary<string, object> Meta = new Dictionary<string, object>();
        public HashSet<Cell> Mask;            // local open cells (null = the whole rectangle)
        public Dictionary<Cell, Corner> Diag = new Dictionary<Cell, Corner>();   // local 45-degree half cells

        public static string[] ConnectorKinds => MissionGraph.ConnectorKinds;

        /// <summary>Joins storeys: its space type is vertical (stairwell, lift, ladder, spiral, tower).</summary>
        public bool IsConnector => Spaces.SpaceFor(Kind).Vertical;

        public (int Lo, int Hi) Slot => (Floor - 64, Ceil);

        public bool Contains(int cx, int cy)
        {
            if (cx < X || cx >= X + W || cy < Y || cy >= Y + H) return false;
            return Mask == null || Mask.Contains(new Cell(cx, cy - 0).Offset(-X, -Y));
        }

        public bool Contains(Cell c) => Contains(c.X, c.Y);

        public bool IsDiag(int cx, int cy) => Diag.ContainsKey(new Cell(cx - X, cy - Y));

        public Dictionary<Cell, Corner> WorldDiag() => Diag.ToDictionary(kv => kv.Key.Offset(X, Y), kv => kv.Value);

        public IEnumerable<Cell> Cells()
        {
            for (int cy = Y; cy < Y + H; cy++)
                for (int cx = X; cx < X + W; cx++)
                    if (Mask == null || Mask.Contains(new Cell(cx - X, cy - Y)))
                        yield return new Cell(cx, cy);
        }

        public (double X, double Y) Center => (X + W / 2.0, Y + H / 2.0);

        /// <summary>Chebyshev distance from a cell to this room's box (0 inside).</summary>
        public int Dist(int cx, int cy)
        {
            int dx = Math.Max(Math.Max(X - cx, 0), cx - (X + W - 1));
            int dy = Math.Max(Math.Max(Y - cy, 0), cy - (Y + H - 1));
            return Math.Max(dx, dy);
        }

        public T Get<T>(string key, T fallback = default) =>
            Meta.TryGetValue(key, out var v) && v is T t ? t : fallback;
    }

    public sealed class Corridor
    {
        public int Id, Edge, A, B;
        public List<Cell> Path = new List<Cell>();
        public List<int> Floors = new List<int>();
        public string Kind = "open";          // open | locked | secret
        public string Key;
        public int Radius = 1;                // half width in cells: 1 -> 3 wide
        public string Connector;              // "drop" for one-way drops
        public int Height = 128;
        public bool Diagonal;
        public bool Vent;                     // a low duct (facility levels)
        public bool Sump;                     // dips under water (floods.py make_sumps)
        public List<int> Junctions = new List<int>();
        public List<Dictionary<string, object>> Beats = new List<Dictionary<string, object>>();   // chambers, alcoves, vaults along it
    }

    public sealed class Entity
    {
        public string Kind;
        public Vec3 Pos;
        public Dictionary<string, object> Props = new Dictionary<string, object>();
        public (Vec3 Lo, Vec3 Hi)? Bounds;
        public Entity(string kind, Vec3 pos) { Kind = kind; Pos = pos; }
        public Entity With(string key, object value) { Props[key] = value; return this; }
    }

    /// <summary>A traversal that isn't plain walking: a lift, a ladder (two-way, with an interaction).</summary>
    public sealed class Link
    {
        public string Kind;
        public Vec3 A, B;
        public bool TwoWay = true;
        public string Interaction;
        public bool Accessible;
        public int Room;
    }

    /// <summary>The level: a 2D grid of columns, each a sorted list of open spans (everything else is
    /// solid), plus rooms, corridors, entities and links. Engine-neutral: every exporter reads this.</summary>
    public sealed class Level
    {
        public int Seed, CellSize, Width, Height;
        public Dictionary<Cell, List<Span>> Spans = new Dictionary<Cell, List<Span>>();
        public Dictionary<Cell, List<Owner>> Owners = new Dictionary<Cell, List<Owner>>();
        public List<Room> Rooms = new List<Room>();
        public List<Corridor> Corridors = new List<Corridor>();
        public List<Entity> Entities = new List<Entity>();
        public Dictionary<Cell, Corner> Diag = new Dictionary<Cell, Corner>();
        public Dictionary<Cell, Span> DiagSpan = new Dictionary<Cell, Span>();
        public HashSet<Cell> Glass = new HashSet<Cell>();
        public HashSet<Cell> Sky = new HashSet<Cell>();
        public HashSet<Cell> SkyWalls = new HashSet<Cell>();     // solid cells round a vista: sky faces
        /// <summary>Sloped ceilings: cell -> corner heights (x0y0, x1y0, x1y1, x0y1) of the top span's ceiling.
        /// The span keeps the lowest point, so gameplay stays conservative.</summary>
        public Dictionary<Cell, (int, int, int, int)> CeilSlope = new Dictionary<Cell, (int, int, int, int)>();
        /// <summary>Sloped floors (ramps): corner heights of the bottom span's floor; the span keeps the highest point.</summary>
        public Dictionary<Cell, (int, int, int, int)> FloorSlope = new Dictionary<Cell, (int, int, int, int)>();
        public Dictionary<Cell, string> Hazard = new Dictionary<Cell, string>();
        /// <summary>Water: cell -> the surface height over the cell's lowest span.</summary>
        public Dictionary<Cell, int> Water = new Dictionary<Cell, int>();
        public Dictionary<Cell, string> PartSolid = new Dictionary<Cell, string>();
        public Dictionary<Cell, string> PartCells = new Dictionary<Cell, string>();
        public Dictionary<Cell, string> PartCeiling = new Dictionary<Cell, string>();
        public List<Link> Links = new List<Link>();
        public List<List<int>> Halls = new List<List<int>>();    // merged halls: room ids per hall
        public Dictionary<string, object> Arena = new Dictionary<string, object>();   // arena mode facts
        public MissionGraph Graph;
        public GenConfig Config;
        public List<string> Notes = new List<string>();
        public ValidationReport Report;

        public void AddSpan(Cell c, Span span, string owner = null, (int Lo, int Hi)? slot = null)
        {
            if (!Spans.TryGetValue(c, out var spans)) Spans[c] = spans = new List<Span>();
            foreach (var s in spans)
                if (span.Floor < s.Ceil && s.Floor < span.Ceil)
                    throw new InvalidOperationException($"span {span} overlaps {s} at {c}");
            spans.Add(span);
            spans.Sort((a, b) => a.Floor.CompareTo(b.Floor));
            if (owner != null)
            {
                var (lo, hi) = slot ?? (span.Floor, span.Ceil);
                if (!Owners.TryGetValue(c, out var os)) Owners[c] = os = new List<Owner>();
                os.Add(new Owner(lo, hi, owner));
            }
        }

        public IEnumerable<Span> SpansIn(Cell c, int lo, int hi)
        {
            if (Spans.TryGetValue(c, out var spans))
                foreach (var s in spans)
                    if (s.Floor < hi && lo < s.Ceil) yield return s;
        }

        /// <summary>No open span within [lo - gap, hi + gap).</summary>
        public bool FreeAt(Cell c, int lo, int hi, int gap = 0) => !SpansIn(c, lo - gap, hi + gap).Any();

        /// <summary>Replace the spans overlapping [lo, hi) by the new ones (ownership is kept).</summary>
        public void ReplaceIn(Cell c, int lo, int hi, IEnumerable<Span> fresh)
        {
            var keep = Spans.TryGetValue(c, out var spans)
                ? spans.Where(s => !(s.Floor < hi && lo < s.Ceil)).ToList()
                : new List<Span>();
            keep.AddRange(fresh);
            keep.Sort((a, b) => a.Floor.CompareTo(b.Floor));
            if (keep.Count > 0) Spans[c] = keep; else Spans.Remove(c);
        }

        public string OwnerAt(Cell c, double z)
        {
            if (Owners.TryGetValue(c, out var os))
                foreach (var o in os)
                    if (o.Lo <= z && z < o.Hi) return o.Name;
            return null;
        }

        public string OwnerOf(Cell c, Span s) => OwnerAt(c, s.Floor);

        public List<Span> RoomSpans(Cell c, Room room)
        {
            string tag = "room:" + room.Id;
            return Spans.TryGetValue(c, out var spans) ? spans.Where(s => OwnerOf(c, s) == tag).ToList() : new List<Span>();
        }

        public (int Lo, int Hi) ZRange()
        {
            int lo = int.MaxValue, hi = int.MinValue;
            foreach (var spans in Spans.Values)
                foreach (var s in spans) { lo = Math.Min(lo, s.Floor); hi = Math.Max(hi, s.Ceil); }
            foreach (var sl in CeilSlope.Values) hi = Math.Max(hi, Math.Max(Math.Max(sl.Item1, sl.Item2), Math.Max(sl.Item3, sl.Item4)));   // a vault rises above its span
            foreach (var sl in FloorSlope.Values) lo = Math.Min(lo, Math.Min(Math.Min(sl.Item1, sl.Item2), Math.Min(sl.Item3, sl.Item4)));
            return lo == int.MaxValue ? (0, 256) : (lo - 32, hi + 32);
        }

        public Vec3 World(double cx, double cy, double z) => new Vec3(cx * CellSize, cy * CellSize, z);

        public int FloorAt(Cell c, bool top = false) { var s = Spans[c]; return top ? s[s.Count - 1].Floor : s[0].Floor; }

        public Room RoomByNode(int node)
        {
            foreach (var r in Rooms) if (r.Node == node) return r;
            throw new KeyNotFoundException("node " + node);
        }

        public Cell CellOf(Vec3 p) => new Cell((int)Math.Floor(p.X / CellSize), (int)Math.Floor(p.Y / CellSize));
    }
}
