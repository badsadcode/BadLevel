using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace NoorLevel.Export
{
    public sealed class ExportFormat
    {
        public string Id, Label, Extension, Description;
    }

    /// <summary>Every export format behind one call. Formats that are more than one file (a .map with
    /// its textures, an .obj with its .mtl) come as a .zip.</summary>
    public static class Exporters
    {
        public static readonly ExportFormat[] Formats =
        {
            new ExportFormat { Id = "quake", Label = "Quake 1 .map + wad", Extension = ".zip", Description = "TrenchBroom / ericw-tools (Standard format) with noorlevel.wad" },
            new ExportFormat { Id = "quake2", Label = "Quake 2 .map + wal", Extension = ".zip", Description = "Quake 2 format with textures/noorlevel/*.wal" },
            new ExportFormat { Id = "valve", Label = "Valve 220 .map + wad3", Extension = ".zip", Description = "Half-Life / J.A.C.K. / func_godot; WAD3 textures" },
            new ExportFormat { Id = "map", Label = "Quake .map only", Extension = ".map", Description = "Just the .map text" },
            new ExportFormat { Id = "glb", Label = "glTF binary (.glb)", Extension = ".glb", Description = "Godot, Unity, Unreal, Blender; Y-up metres, entity nodes with extras" },
            new ExportFormat { Id = "obj", Label = "Wavefront .obj + .mtl", Extension = ".zip", Description = "Any 3D tool" },
            new ExportFormat { Id = "stl", Label = "STL (binary)", Extension = ".stl", Description = "3D printing / mesh tools" },
            new ExportFormat { Id = "json", Label = "NoorLevel JSON", Extension = ".json", Description = "The full level: columns, rooms, corridors, entities, links, graph" },
            new ExportFormat { Id = "svg", Label = "Top-down map (.svg)", Extension = ".svg", Description = "A floor plan, coloured by height" },
        };

        public static byte[] Export(Level level, string format, out string fileName)
        {
            string stem = $"noorlevel_{level.Seed}";
            switch (format)
            {
                case "quake":
                    fileName = stem + "_quake.zip";
                    return Zip((stem + ".map", Encoding.UTF8.GetBytes(MapExport.ToMap(level, MapGame.Quake))), (Textures.WadName, Textures.MakeWad()));
                case "quake2":
                    fileName = stem + "_quake2.zip";
                    return Zip(new[] { ("maps/" + stem + ".map", Encoding.UTF8.GetBytes(MapExport.ToMap(level, MapGame.Quake2))) }.Concat(Textures.Wals()).ToArray());
                case "valve":
                    fileName = stem + "_valve220.zip";
                    return Zip((stem + ".map", Encoding.UTF8.GetBytes(MapExport.ToMap(level, MapGame.Valve220))), (Textures.Wad3Name, Textures.MakeWad3()));
                case "map":
                    fileName = stem + ".map";
                    return Encoding.UTF8.GetBytes(MapExport.ToMap(level, MapGame.Quake));
                case "glb":
                    fileName = stem + ".glb";
                    return Gltf.ToGlb(level);
                case "obj":
                    fileName = stem + "_obj.zip";
                    var (obj, mtl) = Obj.ToObj(level, stem + ".mtl");
                    return Zip((stem + ".obj", Encoding.UTF8.GetBytes(obj)), (stem + ".mtl", Encoding.UTF8.GetBytes(mtl)));
                case "stl":
                    fileName = stem + ".stl";
                    return Stl.ToStl(level);
                case "json":
                    fileName = stem + ".json";
                    return Encoding.UTF8.GetBytes(Json.Write(LevelJson.ToIr(level)));
                case "svg":
                    fileName = stem + ".svg";
                    return Encoding.UTF8.GetBytes(Svg.ToSvg(level));
                default:
                    throw new ArgumentException($"unknown format '{format}' (one of: {string.Join(", ", Formats.Select(f => f.Id))})");
            }
        }

        public static byte[] Zip(params (string Name, byte[] Data)[] files)
        {
            var ms = new MemoryStream();
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                foreach (var (name, data) in files)
                {
                    var e = z.CreateEntry(name, CompressionLevel.Optimal);
                    using (var s = e.Open()) s.Write(data, 0, data.Length);
                }
            return ms.ToArray();
        }
    }

    /// <summary>The level as JSON: the intermediate representation (IR) other tools can read, and the
    /// small payload the previewer walks on (columns + moving parts).</summary>
    public static class LevelJson
    {
        public static Dictionary<string, object> ToIr(Level level)
        {
            var cols = level.Spans.OrderBy(k => k.Key).Select(kv => new Dictionary<string, object>
            {
                ["x"] = kv.Key.X, ["y"] = kv.Key.Y,
                ["spans"] = kv.Value.Select(s => new Dictionary<string, object> { ["floor"] = s.Floor, ["ceil"] = s.Ceil, ["owner"] = level.OwnerOf(kv.Key, s) }).ToList(),
                ["diag"] = level.Diag.TryGetValue(kv.Key, out var d) ? d.ToString().ToLowerInvariant() : null,
            }).ToList();
            return new Dictionary<string, object>
            {
                ["format"] = "noorlevel-ir", ["version"] = 1, ["seed"] = level.Seed, ["cell"] = level.CellSize,
                ["width"] = level.Width, ["height"] = level.Height,
                ["config"] = ConfigInfo(level.Config),
                ["columns"] = cols,
                ["rooms"] = level.Rooms.Select(r => new Dictionary<string, object>
                {
                    ["id"] = r.Id, ["kind"] = r.Kind, ["x"] = r.X, ["y"] = r.Y, ["w"] = r.W, ["h"] = r.H, ["floor"] = r.Floor, ["ceil"] = r.Ceil,
                    ["storey"] = r.Storey, ["shape"] = r.Shape, ["features"] = r.Features,
                }).ToList(),
                ["corridors"] = level.Corridors.Select(c => new Dictionary<string, object>
                {
                    ["id"] = c.Id, ["a"] = c.A, ["b"] = c.B, ["kind"] = c.Kind, ["key"] = c.Key, ["radius"] = c.Radius,
                    ["height"] = c.Height, ["path"] = c.Path.Select(p => new[] { p.X, p.Y }).ToList(), ["floors"] = c.Floors,
                }).ToList(),
                ["entities"] = level.Entities.Select(EntityInfo).ToList(),
                ["links"] = level.Links.Select(l => new Dictionary<string, object> { ["kind"] = l.Kind, ["a"] = l.A, ["b"] = l.B, ["two_way"] = l.TwoWay, ["interaction"] = l.Interaction }).ToList(),
                ["graph"] = new Dictionary<string, object>
                {
                    ["nodes"] = level.Graph.Nodes.Select(n => new Dictionary<string, object> { ["id"] = n.Id, ["kind"] = n.Kind, ["zone"] = n.Zone, ["storey"] = n.Storey }).ToList(),
                    ["edges"] = level.Graph.Edges.Select(e => new Dictionary<string, object> { ["a"] = e.A, ["b"] = e.B, ["kind"] = e.Kind, ["key"] = e.Key, ["tree"] = e.Tree, ["connector"] = e.Connector }).ToList(),
                },
                ["notes"] = level.Notes,
            };
        }

        public static Dictionary<string, object> EntityInfo(Entity e)
        {
            var d = new Dictionary<string, object> { ["kind"] = e.Kind, ["pos"] = e.Pos };
            if (e.Bounds != null) { d["lo"] = e.Bounds.Value.Lo; d["hi"] = e.Bounds.Value.Hi; }
            var props = new Dictionary<string, object>();
            foreach (var kv in e.Props) props[kv.Key] = kv.Value;
            d["props"] = props;
            return d;
        }

        public static Dictionary<string, object> ConfigInfo(GenConfig c) => new Dictionary<string, object>
        {
            ["seed"] = c.Seed, ["recipe"] = c.Recipe, ["main_rooms"] = c.MainRooms, ["side_rooms"] = c.SideRooms, ["loops"] = c.Loops,
            ["keys"] = c.Keys, ["secret"] = c.Secret, ["storeys"] = c.Storeys, ["cell"] = c.Cell, ["player_height"] = c.PlayerHeight,
            ["max_step"] = c.MaxStep, ["max_drop"] = c.MaxDrop,
        };

        /// <summary>What the previewer walks on: per column [x, y, diag(-1 none, 0 sw, 1 se, 2 nw, 3 ne), f0, c0, f1, c1, ...]
        /// in map units (Z up), plus the entities.</summary>
        public static Dictionary<string, object> ToViewer(Level level)
        {
            var cols = new List<int[]>();
            foreach (var kv in level.Spans.OrderBy(k => k.Key))
            {
                var a = new List<int> { kv.Key.X, kv.Key.Y, level.Diag.TryGetValue(kv.Key, out var d) ? (int)d : -1 };
                foreach (var s in kv.Value) { a.Add(s.Floor); a.Add(s.Ceil); }
                cols.Add(a.ToArray());
            }
            var rooms = level.Rooms.Select(r => new Dictionary<string, object> { ["id"] = r.Id, ["kind"] = r.Kind, ["x"] = r.X, ["y"] = r.Y, ["w"] = r.W, ["h"] = r.H, ["floor"] = r.Floor }).ToList();
            return new Dictionary<string, object>
            {
                ["seed"] = level.Seed, ["cell"] = level.CellSize, ["width"] = level.Width, ["height"] = level.Height,
                ["player"] = new Dictionary<string, object> { ["height"] = level.Config.PlayerHeight, ["step"] = level.Config.MaxStep },
                ["columns"] = cols, ["entities"] = level.Entities.Select(EntityInfo).ToList(), ["rooms"] = rooms,
                ["links"] = level.Links.Select(l => new Dictionary<string, object> { ["kind"] = l.Kind, ["a"] = l.A, ["b"] = l.B }).ToList(),
                ["stats"] = Stats(level),
            };
        }

        public static Dictionary<string, object> Stats(Level level)
        {
            var (zlo, zhi) = level.ZRange();
            return new Dictionary<string, object>
            {
                ["rooms"] = level.Rooms.Count(r => !r.IsConnector), ["connectors"] = level.Rooms.Count(r => r.IsConnector),
                ["corridors"] = level.Corridors.Count, ["cells"] = level.Spans.Count,
                ["keys"] = level.Entities.Count(e => e.Kind == "key"), ["doors"] = level.Entities.Count(e => e.Kind == "door"),
                ["secrets"] = level.Entities.Count(e => e.Kind == "secret_door"), ["lights"] = level.Entities.Count(e => e.Kind == "light"),
                ["size_m"] = $"{level.Width}x{level.Height}x{(zhi - zlo) / level.CellSize}",
                ["reachable"] = level.Report?.ReachableFraction ?? 0,
                ["notes"] = level.Notes,
            };
        }
    }

    /// <summary>A floor plan: each open cell coloured by its top floor's height, rooms outlined, entities marked.</summary>
    public static class Svg
    {
        public static string ToSvg(Level level, int px = 6)
        {
            var ci = CultureInfo.InvariantCulture;
            int minX = level.Spans.Keys.Min(c => c.X) - 2, minY = level.Spans.Keys.Min(c => c.Y) - 2;
            int maxX = level.Spans.Keys.Max(c => c.X) + 3, maxY = level.Spans.Keys.Max(c => c.Y) + 3;
            int W = (maxX - minX) * px, H = (maxY - minY) * px;
            var floors = level.Spans.Values.SelectMany(s => s).Select(s => s.Floor).ToList();
            double lo = floors.Min(), hi = Math.Max(lo + 1, floors.Max());
            double X(double cx) => (cx - minX) * px;
            double Y(double cy) => (maxY - cy) * px;      // north up
            var sb = new StringBuilder();
            sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{W}\" height=\"{H}\" viewBox=\"0 0 {W} {H}\">\n");
            sb.Append($"<rect width=\"{W}\" height=\"{H}\" fill=\"#1d1b22\"/>\n");
            foreach (var kv in level.Spans)
            {
                double t = (kv.Value[kv.Value.Count - 1].Floor - lo) / (hi - lo);
                int r = (int)(70 + 150 * t), g = (int)(90 + 110 * t), b = (int)(140 - 40 * t);
                string fill = $"rgb({r},{g},{b})";
                if (level.Diag.TryGetValue(kv.Key, out var corner))
                {
                    var open = Shape.CornerVertices(corner).Open;
                    sb.Append("<polygon points=\"").Append(string.Join(" ", open.Select(p => string.Format(ci, "{0:0.#},{1:0.#}", X(kv.Key.X + p.Item1), Y(kv.Key.Y + p.Item2)))))
                      .Append($"\" fill=\"{fill}\"/>\n");
                }
                else sb.Append(string.Format(ci, "<rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{2}\" fill=\"{3}\"/>\n", X(kv.Key.X), Y(kv.Key.Y + 1), px, fill));
            }
            foreach (var r in level.Rooms.Where(r => !r.IsConnector))
                sb.Append(string.Format(ci, "<rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" fill=\"none\" stroke=\"#ffffff55\" stroke-width=\"1\"/>\n", X(r.X), Y(r.Y + r.H), r.W * px, r.H * px));
            var marks = new Dictionary<string, string> { ["player_start"] = "#4cff7a", ["exit"] = "#ffd84c", ["key"] = "#ff5a5a", ["door"] = "#ff8a3c", ["secret_door"] = "#b48cff", ["lift"] = "#ffcc33", ["ladder"] = "#c08850" };
            foreach (var e in level.Entities)
                if (marks.TryGetValue(e.Kind, out var col))
                {
                    string c2 = e.Kind == "key" && (string)e.Props["color"] == "blue" ? "#5a8aff" : col;
                    sb.Append(string.Format(ci, "<circle cx=\"{0:0.#}\" cy=\"{1:0.#}\" r=\"{2}\" fill=\"{3}\" stroke=\"#000\" stroke-width=\"1\"/>\n", X(e.Pos.X / level.CellSize), Y(e.Pos.Y / level.CellSize), px * 0.9, c2));
                }
            sb.Append($"<text x=\"8\" y=\"{H - 8}\" fill=\"#ffffffaa\" font-family=\"sans-serif\" font-size=\"12\">NoorLevel seed {level.Seed}</text>\n");
            return sb.Append("</svg>\n").ToString();
        }
    }
}
