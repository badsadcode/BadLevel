using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>Openings (openings.py): windows and galleries. Seeing is not walking.
    /// <para>Windows are cut through thin walls (1-2 cells) between two *different* areas:</para>
    /// <list type="bullet">
    /// <item>slit: 48 units tall: you can see and shoot through, but a player (56 tall) never fits, so it
    /// is visual-only by construction. Crouch below the sill and you are hidden.</item>
    /// <item>glazed: a tall pane: you can see through it, but not walk or shoot through it. Its cells are
    /// in <c>level.Glass</c>: walls for movement and for combat sight lines, open for visual connectivity.</item>
    /// </list>
    /// <para>Galleries (<see cref="AddGalleries"/>) are decks at the upper storey's height inside a tall room,
    /// reached by a corridor from an upper-storey room: an overlook onto the hall below. They connect
    /// physically, so they only join rooms in the same lock zone.</para></summary>
    public static class Openings
    {
        public static readonly int Slit = 48;

        /// <summary>One wall cross-section of a window site: (wall cells, open cell on side a, on side b, spans).</summary>
        public sealed class SiteCell
        {
            public List<Cell> Wall; public Cell C0, C1; public Span Sa, Sb;
        }

        public sealed class WindowSite
        {
            public (string, string) Areas;
            public (int, int) Dir;
            public List<SiteCell> Cells = new List<SiteCell>();
            public int Thick;
        }

        public static string Area(Level level, Cell c, Span span) => level.OwnerOf(c, span);

        /// <summary>From solid cell c, look along d for the first open cell within the wall thickness.
        /// Returns (wall cells, open cell) or null.</summary>
        public static (List<Cell> Wall, Cell Open)? Through(Level level, Cell c, (int, int) d, int maxThick = 2)
        {
            var wall = new List<Cell> { c };
            int x = c.X, y = c.Y;
            for (int i = 0; i < maxThick; i++)
            {
                x += d.Item1; y += d.Item2;
                var n = new Cell(x, y);
                if (level.Spans.ContainsKey(n)) return (wall, n);
                wall.Add(n);
            }
            return null;
        }

        /// <summary>Thin wall runs between two different areas, with the vertical overlap of both sides.</summary>
        public static List<WindowSite> FindWindowSites(Level level)
        {
            var sites = new Dictionary<(string, string, int, int, int), WindowSite>();
            var order = new List<WindowSite>();                 // (Python dict order)
            foreach (var c0 in level.Spans.Keys.OrderBy(c => c).ToList())
            {
                if (level.Diag.ContainsKey(c0)) continue;
                foreach (var d in new[] { (1, 0), (0, 1) })
                {
                    var w0 = c0.Offset(d.Item1, d.Item2);
                    if (level.Spans.ContainsKey(w0)) continue;
                    var hit = Through(level, w0, d);
                    if (hit == null) continue;
                    var (wall, c1) = hit.Value;
                    if (level.Diag.ContainsKey(c1) || wall.Any(w => level.Diag.ContainsKey(w))) continue;
                    // the side cells beside the wall must be solid too, so a window is a hole in a wall
                    foreach (var sa in level.Spans[c0])
                        foreach (var sb in level.Spans[c1])
                        {
                            string a1 = Area(level, c0, sa), a2 = Area(level, c1, sb);
                            if (a1 == null || a2 == null || a1 == a2) continue;
                            int lo = Math.Max(sa.Floor, sb.Floor), hi = Math.Min(sa.Ceil, sb.Ceil);
                            if (hi - lo < Slit + 40) continue;
                            var areas = string.CompareOrdinal(a1, a2) <= 0 ? (a1, a2) : (a2, a1);
                            var key = (areas.Item1, areas.Item2, d.Item1, d.Item2, wall.Count);
                            if (!sites.TryGetValue(key, out var site))
                            {
                                site = new WindowSite { Areas = areas, Dir = d, Thick = wall.Count };
                                sites[key] = site;
                                order.Add(site);
                            }
                            site.Cells.Add(new SiteCell { Wall = wall, C0 = c0, C1 = c1, Sa = sa, Sb = sb });
                        }
                }
            }
            return order;
        }

        public static void AddWindows(Level level, Rng rng, int count, double glazedChance)
        {
            if (count <= 0) return;
            var sites = FindWindowSites(level);
            rng.Shuffle(sites);
            // prefer views between different storeys / heights and between rooms
            int Score(WindowSite site)
            {
                var first = site.Cells[0];
                int rooms = (site.Areas.Item1.StartsWith("room:") ? 1 : 0) + (site.Areas.Item2.StartsWith("room:") ? 1 : 0);
                return -(Math.Abs(first.Sa.Floor - first.Sb.Floor) / 64 + rooms);
            }
            sites = sites.OrderBy(Score).ToList();               // stable, like Python's sort
            int made = 0;
            var usedPairs = new HashSet<(string, string)>();
            foreach (var site in sites)
            {
                if (made >= count) break;
                if (usedPairs.Contains(site.Areas)) continue;
                var runs = Runs(site).Where(r => r.Count >= 2).ToList();
                if (runs.Count == 0) continue;
                var run = runs[0];
                foreach (var r in runs) if (r.Count > run.Count) run = r;
                int n = Math.Min(run.Count, rng.Int(2, 4));
                int start = (run.Count - n) / 2;
                var chosen = run.GetRange(start, n);
                var sa = chosen[0].Sa; var sb = chosen[0].Sb;
                int floor = Math.Max(sa.Floor, sb.Floor);
                int top = Math.Min(sa.Ceil, sb.Ceil);
                bool glazed = rng.Random() < glazedChance && top - floor >= 128;
                int lo, hi;
                if (glazed) { lo = floor + 16; hi = top - 16; }
                else { lo = floor + 32; hi = floor + 32 + Slit; }
                if (hi > top - 8) continue;
                bool ok = true;
                foreach (var it in chosen)
                {
                    if (Math.Max(it.Sa.Floor, it.Sb.Floor) != floor || Math.Min(it.Sa.Ceil, it.Sb.Ceil) < hi + 8) ok = false;
                    foreach (var w in it.Wall)
                        if (level.Spans.ContainsKey(w)) ok = false;
                }
                if (!ok) continue;
                var cells = new List<Cell>();
                foreach (var it in chosen)
                    foreach (var w in it.Wall)
                    {
                        level.AddSpan(w, new Span(lo, hi));
                        // first: a wall cell that was a room cell (a jamb, a member) still has the room's entry
                        if (!level.Owners.TryGetValue(w, out var os)) level.Owners[w] = os = new List<Owner>();
                        os.Insert(0, new Owner(lo, hi, $"window:{made}"));
                        cells.Add(w);
                        if (glazed) level.Glass.Add(w);
                    }
                int s = level.CellSize;
                var bLo = new Vec3(cells.Min(c => c.X) * s, cells.Min(c => c.Y) * s, lo);
                var bHi = new Vec3((cells.Max(c => c.X) + 1) * s, (cells.Max(c => c.Y) + 1) * s, hi);
                level.Entities.Add(new Entity("window", Mid(bLo, bHi)) { Bounds = (bLo, bHi) }
                    .With("glazed", glazed).With("areas", new List<string> { site.Areas.Item1, site.Areas.Item2 }).With("id", made));
                usedPairs.Add(site.Areas);
                made++;
            }
        }

        public static Vec3 Mid(Vec3 a, Vec3 b) => new Vec3((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);

        /// <summary>Group a site's wall cells into straight consecutive runs along the wall.</summary>
        public static List<List<SiteCell>> Runs(WindowSite site)
        {
            var d = site.Dir;
            var along = d == (0, 1) ? (1, 0) : (0, 1);
            var items = site.Cells.OrderBy(it => it.Wall[0].X * along.Item1 + it.Wall[0].Y * along.Item2).ToList();
            var runs = new List<List<SiteCell>>();
            var cur = new List<SiteCell>();
            foreach (var it in items)
            {
                var w0 = it.Wall[0];
                if (cur.Count > 0)
                {
                    var last = cur[cur.Count - 1];
                    var p = last.Wall[0];
                    bool sameLine = (w0.X - p.X, w0.Y - p.Y) == along && it.Sa.Equals(last.Sa) && it.Sb.Equals(last.Sb);
                    if (!sameLine)
                    {
                        runs.Add(cur);
                        cur = new List<SiteCell>();
                    }
                }
                cur.Add(it);
            }
            if (cur.Count > 0) runs.Add(cur);
            return runs;
        }

        // ------------------------------------------------------------------
        // galleries
        // ------------------------------------------------------------------

        /// <summary>Longest straight wall stretch with a 3-deep deck: (deck cells, port cells) per side, best first.</summary>
        public static List<(List<Cell> Deck, HashSet<Cell> Ports)> GalleryDeck(Room room, Level level)
        {
            var outp = new List<(int Len, List<Cell> Deck, HashSet<Cell> Ports)>();
            foreach (char side in "nsew")
            {
                int ulen = side == 'n' || side == 's' ? room.W : room.H;
                Cell At(int u, int v)
                {
                    if (side == 's') return new Cell(room.X + u, room.Y + v);
                    if (side == 'n') return new Cell(room.X + u, room.Y + room.H - 1 - v);
                    if (side == 'w') return new Cell(room.X + v, room.Y + u);
                    return new Cell(room.X + room.W - 1 - v, room.Y + u);
                }
                var ok = new List<bool>();
                for (int u = 0; u < ulen; u++)
                {
                    bool good = true;
                    for (int v = 0; v < 3; v++)
                    {
                        var c = At(u, v);
                        if (!room.Contains(c) || room.IsDiag(c.X, c.Y)) { good = false; break; }
                    }
                    ok.Add(good);
                }
                ok.Add(false);
                (int A, int B) best = (0, 0);
                int? run = null;
                for (int u = 0; u < ok.Count; u++)
                {
                    bool good = ok[u];
                    if (good && run == null) run = u;
                    else if (!good && run != null)
                    {
                        if (u - run.Value > best.B - best.A) best = (run.Value, u);
                        run = null;
                    }
                }
                var (ua, ub) = best;
                if (ub - ua >= 5)
                {
                    var deck = new List<Cell>();
                    for (int u = ua; u < ub; u++)
                        for (int v = 0; v < 3; v++) deck.Add(At(u, v));
                    var ports = new HashSet<Cell>();
                    for (int u = ua + 1; u < ub - 1; u++) ports.Add(At(u, 0));
                    outp.Add((ub - ua, deck, ports));
                }
            }
            return outp.OrderBy(t => -t.Len).Select(t => (t.Deck, t.Ports)).ToList();
        }

        /// <summary>The gallery height of a tall room: the highest z (stepping down from a storey up) with
        /// 144 of headroom under the ceiling, or null.</summary>
        static int? GalleryHeight(Room room, int H)
        {
            for (int z = room.Floor + H; z > room.Floor + 159; z -= 32)
                if (room.Ceil >= z + 144) return z;
            return null;
        }

        /// <summary>Tall rooms get a deck at the upper storey's height, reached from an upper-storey room
        /// in the same lock zone: an overlook down into the hall, and a one-way way down.</summary>
        public static void AddGalleries(Level level, Router router, Rng rng)
        {
            var cfg = level.Config;
            var g = level.Graph;
            int H = cfg.StoreyHeight;
            foreach (var room in level.Rooms.ToList())
            {
                if (room.IsConnector || HasMeta(room, "gallery")) continue;
                int? Gq = GalleryHeight(room, H);
                if (Gq == null || Gq.Value - room.Floor > cfg.MaxDrop) continue;
                int G = Gq.Value;
                int zone = g.Nodes[room.Node].Zone;
                var linked = new HashSet<int>();
                foreach (var c in level.Corridors)
                    if (c.A == room.Id || c.B == room.Id) linked.Add(c.B == room.Id ? c.A : c.B);
                var rc = room.Center;
                var uppers = level.Rooms.Where(u => !u.IsConnector && u.Id != room.Id && !linked.Contains(u.Id)
                                                    && g.Nodes[u.Node].Zone == zone && Math.Abs(u.Floor - G) <= 96 && u.Kind != "secret")
                    .OrderBy(u => Math.Abs(u.Center.X - rc.X) + Math.Abs(u.Center.Y - rc.Y)).ToList();
                bool done = false;
                foreach (var (deck, port) in GalleryDeck(room, level))
                {
                    foreach (var u in uppers.Take(4))
                    {
                        var env = (Math.Min(u.Floor, G), Math.Max(u.Floor, G) + 128);
                        List<Cell> path;
                        try
                        {
                            path = router.Route(u, room, 1, false, 1, env, null, port);
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        while (path.Count > 1 && u.Contains(path[1])) path.RemoveAt(0);
                        var inner = path.Count >= 2 ? path.GetRange(1, path.Count - 2) : new List<Cell>();
                        var floors = Layout.PlanFloors(inner, u.Floor, G, cfg, 1);
                        if (floors == null || inner.Count < 3) continue;
                        foreach (var c in deck)
                            level.ReplaceIn(c, room.Slot.Lo, room.Slot.Hi, new[] { new Span(room.Floor, G - 16), new Span(G, room.Ceil) });
                        var edge = g.AddEdge(u.Node, room.Node, tree: false, connector: "gallery");
                        Layout.CarveCorridor(level, router, edge.Id, u, room, inner, floors, floors.Select(f => f + 128).ToList(),
                                             1, 1, 128, false, "open", null, "gallery", env);
                        room.Meta["gallery"] = deck.ToList();
                        room.Meta["gallery_floor"] = G;
                        room.Features.Add("gallery");
                        done = true;
                        break;
                    }
                    if (done) break;
                }
            }
        }

        static bool HasMeta(Room room, string key)
        {
            if (!room.Meta.TryGetValue(key, out var v) || v == null) return false;
            if (v is System.Collections.ICollection col) return col.Count > 0;
            return true;
        }

        // ------------------------------------------------------------------
        // overlooks: seeing a hall you can't (yet) enter
        // ------------------------------------------------------------------

        /// <summary>Underground levels: the deep chamber is seen from above before you reach it. A gallery
        /// would be a physical way in (and the chamber usually sits behind a locked door), so this is
        /// visual only: an observation booth carved beside the chamber at gallery height, reached by a
        /// corridor from a room on the storey above, looking in through a wide glazed window (glass
        /// stops movement and gunfire, not sight), or a long slit where the level has no glass (too low
        /// for a player). One cell of rock stays between booth and hall.</summary>
        public static bool AddOverlook(Level level, Router router, Rng rng)
        {
            var cfg = level.Config;
            var g = level.Graph;
            int H = cfg.StoreyHeight;
            var halls = level.Rooms.Where(r => r.Kind == "megaroom" && !HasMeta(r, "gallery")).ToList();
            if (halls.Count == 0) return false;
            var hall = halls[0];
            int? Gq = GalleryHeight(hall, H);
            if (Gq == null) return false;
            int G = Gq.Value;
            int depth = g.Nodes[hall.Node].Depth;
            var hc = hall.Center;
            var uppers = level.Rooms.Where(u => !u.IsConnector && u.Kind != "secret" && u.Kind != "courtyard" && u.Kind != "megaroom"
                                                && Math.Abs(u.Floor - G) <= 96 && g.Nodes[u.Node].Depth <= depth)
                .OrderBy(u => Math.Abs(u.Center.X - hc.X) + Math.Abs(u.Center.Y - hc.Y)).ToList();
            const int bw = 5, bd = 3;                           // booth: along the wall x out of it
            // a level without glass (the bunker: firing slits only) gets a long observation slit at eye
            // height instead of a pane: 48 units, so no player fits through it either
            bool glazed = cfg.GlazedChance > 0;
            int winLo = glazed ? G + 16 : G + 24, winHi = glazed ? G + 112 : G + 24 + Slit;

            bool Free(Cell c, int lo, int hi) =>
                2 < c.X && c.X < level.Width - 3 && 2 < c.Y && c.Y < level.Height - 3 && !level.SpansIn(c, lo, hi).Any()
                && !level.Diag.ContainsKey(c) && !level.Glass.Contains(c)
                && !(router.RoomAt.TryGetValue(c, out var ra) && ra.Count > 0);

            var sites = new List<((int, int) D, (int, int) Along, List<Cell> Inner)>();
            foreach (var d in new[] { (0, -1), (0, 1), (-1, 0), (1, 0) })
            {
                var along = d.Item2 != 0 ? (1, 0) : (0, 1);
                int length = d.Item2 != 0 ? hall.W : hall.H;
                var edge = new List<Cell>();
                for (int i = 0; i < length; i++)
                {
                    if (d == (0, -1)) edge.Add(new Cell(hall.X + i, hall.Y));
                    else if (d == (0, 1)) edge.Add(new Cell(hall.X + i, hall.Y + hall.H - 1));
                    else if (d == (-1, 0)) edge.Add(new Cell(hall.X, hall.Y + i));
                    else edge.Add(new Cell(hall.X + hall.W - 1, hall.Y + i));
                }
                int mid = length / 2;
                var offs = new List<int>();
                for (int o = 2; o < length - bw - 1; o++) offs.Add(o);
                foreach (int off in offs.OrderBy(o => Math.Abs(o + bw / 2 - mid)))
                    sites.Add((d, along, edge.GetRange(off, bw)));
            }
            foreach (var (d, along, inner) in sites)
            {
                if (inner.Any(c => !hall.Contains(c) || hall.IsDiag(c.X, c.Y))) continue;
                var wall = inner.Select(c => c.Offset(d.Item1, d.Item2)).ToList();
                var booth = new List<Cell>();
                foreach (var c in inner)
                    for (int k = 2; k < 2 + bd; k++) booth.Add(c.Offset(d.Item1 * k, d.Item2 * k));
                var ring = new HashSet<Cell>();
                foreach (var b in booth)
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++) ring.Add(b.Offset(dx, dy));
                ring.ExceptWith(booth);
                ring.ExceptWith(wall);
                if (!booth.All(c => Free(c, G - 48, G + 176)) || !ring.All(c => Free(c, G - 48, G + 176))
                    || !wall.All(c => Free(c, winLo, winHi)))
                    continue;
                int minX = booth.Min(c => c.X), minY = booth.Min(c => c.Y);
                var room = new Room
                {
                    Id = level.Rooms.Count, Node = -1, Kind = "overlook", X = minX, Y = minY,
                    W = booth.Max(c => c.X) - minX + 1, H = booth.Max(c => c.Y) - minY + 1,
                    Floor = G, Ceil = G + 128, Storey = hall.Storey + 1,
                };
                level.Rooms.Add(room);
                Spaces.SpaceFor("overlook").Build(level, room, cfg);
                router.AddRoom(room);
                foreach (var u in uppers.Take(5))
                {
                    var env = (Math.Min(u.Floor, G), Math.Max(u.Floor, G) + 128);
                    List<Cell> path;
                    try
                    {
                        path = router.Route(u, room, 1, false, 1, env);
                    }
                    catch (LayoutException)
                    {
                        continue;
                    }
                    while (path.Count > 1 && u.Contains(path[1])) path.RemoveAt(0);
                    var innerPath = path.Count >= 2 ? path.GetRange(1, path.Count - 2) : new List<Cell>();
                    var floors = Layout.PlanFloors(innerPath, u.Floor, G, cfg, 1);
                    if (floors == null || innerPath.Count < 2) continue;
                    var node = g.AddNode("overlook", "small", zone: g.Nodes[u.Node].Zone, depth: g.Nodes[u.Node].Depth, storey: room.Storey);
                    room.Node = node.Id;
                    var edge = g.AddEdge(u.Node, node.Id, tree: false, connector: "overlook");
                    Layout.CarveCorridor(level, router, edge.Id, u, room, innerPath, floors, floors.Select(f => f + 128).ToList(),
                                         1, 1, 128, false, "open", null, null, env);
                    // the window: one cell of wall, glazed, between booth and hall
                    var ws = wall.GetRange(1, wall.Count - 2);
                    foreach (var w in ws)
                    {
                        level.AddSpan(w, new Span(winLo, winHi), "window:overlook");
                        if (glazed) level.Glass.Add(w);
                    }
                    int s = level.CellSize;
                    var lo = new Vec3(ws.Min(c => c.X) * s, ws.Min(c => c.Y) * s, winLo);
                    var hi = new Vec3((ws.Max(c => c.X) + 1) * s, (ws.Max(c => c.Y) + 1) * s, winHi);
                    level.Entities.Add(new Entity("window", Mid(lo, hi)) { Bounds = (lo, hi) }
                        .With("glazed", glazed).With("areas", new List<string> { $"room:{room.Id}", $"room:{hall.Id}" }).With("overlook", true));
                    // keep the sightline clear: nothing stands right in front of the window
                    var front = new List<Cell>();
                    for (int i = 1; i < inner.Count - 1; i++)
                        for (int k = 0; k < 2; k++) front.Add(inner[i].Offset(-d.Item1 * k, -d.Item2 * k));
                    hall.Meta["overlook"] = front.Where(c => hall.Contains(c)).ToList();
                    room.Meta["overlooks"] = hall.Id;
                    return true;
                }
                // no way up to this booth: take it back out
                foreach (var c in room.Cells().ToList())
                {
                    level.Spans.Remove(c);
                    level.Owners.Remove(c);
                }
                router.RemoveRoom(room);
                if (level.Rooms.Count > 0 && level.Rooms[level.Rooms.Count - 1] == room)
                    level.Rooms.RemoveAt(level.Rooms.Count - 1);
            }
            return false;
        }
    }
}
