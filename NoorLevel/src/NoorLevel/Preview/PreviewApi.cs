using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NoorLevel.Export;

namespace NoorLevel.Preview
{
    public sealed class ApiResponse
    {
        public int Status = 200;
        public string ContentType = "application/json";
        public byte[] Body;
        public string FileName;    // set for downloads

        public static ApiResponse JsonOf(object o, int status = 200) => new ApiResponse { Status = status, Body = Encoding.UTF8.GetBytes(Json.Write(o)) };
    }

    /// <summary>The previewer's backend, shared by the WebView2 app and `noorlevel serve`: the same
    /// web page talks to it over /api/... in both. It keeps the last few levels by id.</summary>
    public sealed class PreviewApi
    {
        readonly Dictionary<int, Level> levels = new Dictionary<int, Level>();
        readonly Queue<int> order = new Queue<int>();
        int nextId = 1;
        readonly object gate = new object();

        /// <summary>Numeric settings the page can override (null = what the preset and dials give).</summary>
        public static readonly (string Name, string Label, double Min, double Max, double Step)[] Settings =
        {
            ("main_rooms", "Main rooms", 2, 20, 1), ("side_rooms", "Side rooms", 0, 12, 1), ("loops", "Loops", 0, 8, 1),
            ("keys", "Keys", 0, 2, 1), ("storeys", "Storeys", 1, 4, 1), ("diagonal_chance", "45° corridors", 0, 1, 0.05),
            ("drop_chance", "One-way drops", 0, 1, 0.05), ("corridor_height", "Corridor height", 96, 256, 16),
        };

        public Level Get(int id) { lock (gate) return levels.TryGetValue(id, out var l) ? l : null; }

        public ApiResponse Handle(string method, string path, IDictionary<string, string> query, string body)
        {
            try
            {
                switch (path)
                {
                    case "/api/info": return ApiResponse.JsonOf(Info());
                    case "/api/generate": return Generate(body);
                    case "/api/level.glb":
                    {
                        var lvl = Get(int.Parse(query["id"]));
                        if (lvl == null) return ApiResponse.JsonOf(new Dictionary<string, object> { ["error"] = "no such level" }, 404);
                        return new ApiResponse { ContentType = "model/gltf-binary", Body = Gltf.ToGlb(lvl, false) };
                    }
                    case "/api/export":
                    {
                        var lvl = Get(int.Parse(query["id"]));
                        if (lvl == null) return ApiResponse.JsonOf(new Dictionary<string, object> { ["error"] = "no such level" }, 404);
                        var data = Exporters.Export(lvl, query["format"], out var name);
                        return new ApiResponse { ContentType = "application/octet-stream", Body = data, FileName = name };
                    }
                }
                return ApiResponse.JsonOf(new Dictionary<string, object> { ["error"] = "not found: " + path }, 404);
            }
            catch (Exception e)
            {
                return ApiResponse.JsonOf(new Dictionary<string, object> { ["error"] = e.Message }, 500);
            }
        }

        public static Dictionary<string, object> Info() => new Dictionary<string, object>
        {
            ["version"] = typeof(PreviewApi).Assembly.GetName().Version.ToString(),
            ["dials"] = Recipe.Dials,
            ["presets"] = Recipes.Presets.Values.Select(r => new Dictionary<string, object>
            {
                ["name"] = r.Name, ["description"] = r.Description,
                ["dials"] = Recipe.Dials.ToDictionary(d => d, d => (object)r.Get(d)),
            }).Append(new Dictionary<string, object>
            {
                ["name"] = "surprise", ["description"] = "A preset drawn by the seed, with a roll for extras on top (another architecture style, water, torches, vistas, a deathmatch). Same seed, same surprise. The dials don't apply.",
                ["dials"] = Recipe.Dials.ToDictionary(d => d, d => (object)Recipes.Presets["classic"].Get(d)),
            }).ToList(),
            ["settings"] = Settings.Select(s => new Dictionary<string, object> { ["name"] = s.Name, ["label"] = s.Label, ["min"] = s.Min, ["max"] = s.Max, ["step"] = s.Step }).ToList(),
            ["formats"] = Exporters.Formats.Select(f => new Dictionary<string, object> { ["id"] = f.Id, ["label"] = f.Label, ["ext"] = f.Extension, ["description"] = f.Description }).ToList(),
        };

        static double? Num(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v is double x ? x : (double?)null;

        public static GenConfig ConfigFrom(Dictionary<string, object> req)
        {
            int seed = (int)(Num(req, "seed") ?? 1);
            GenConfig cfg;
            if (req.TryGetValue("preset", out var sp) && sp as string == "surprise")
            {
                // a preset drawn by the seed and extras on top (recipes.surprise); the dials don't apply
                var (rec, extras, label) = Recipes.Surprise(seed);
                cfg = Recipes.Compile(rec, seed);
                Recipes.SetFields(cfg, extras);
                cfg.Recipe = label;
            }
            else
            {
                string preset = req.TryGetValue("preset", out var p) && p is string ps && Recipes.Presets.ContainsKey(ps) ? ps : "classic";
                var recipe = Recipes.Presets[preset].Copy();
                if (req.TryGetValue("dials", out var dv) && dv is Dictionary<string, object> dials)
                    foreach (var d in Recipe.Dials) { var v = Num(dials, d); if (v != null) recipe.Set(d, v.Value); }
                cfg = Recipes.Compile(recipe, seed);
            }
            var st = req.TryGetValue("settings", out var sv) ? sv as Dictionary<string, object> : null;
            if (Num(st, "main_rooms") is double mr) cfg.MainRooms = (int)mr;
            if (Num(st, "side_rooms") is double sr) cfg.SideRooms = (int)sr;
            if (Num(st, "loops") is double lp) cfg.Loops = (int)lp;
            if (Num(st, "keys") is double k) cfg.Keys = (int)k;
            if (Num(st, "storeys") is double s) cfg.Storeys = (int)s;
            if (Num(st, "diagonal_chance") is double dc) cfg.DiagonalChance = dc;
            if (Num(st, "drop_chance") is double dr) cfg.DropChance = dr;
            if (Num(st, "corridor_height") is double ch) cfg.CorridorHeight = (int)ch;
            if (st != null && st.TryGetValue("secret", out var sec) && sec is bool b) cfg.Secret = b;
            return cfg;
        }

        public static Dictionary<string, object> SettingsOf(GenConfig c) => new Dictionary<string, object>
        {
            ["main_rooms"] = c.MainRooms, ["side_rooms"] = c.SideRooms, ["loops"] = c.Loops, ["keys"] = c.Keys, ["storeys"] = c.Storeys,
            ["diagonal_chance"] = c.DiagonalChance, ["drop_chance"] = c.DropChance, ["corridor_height"] = c.CorridorHeight, ["secret"] = c.Secret,
        };

        ApiResponse Generate(string body)
        {
            var req = (string.IsNullOrWhiteSpace(body) ? new Dictionary<string, object>() : Json.Parse(body) as Dictionary<string, object>) ?? new Dictionary<string, object>();
            var cfg = ConfigFrom(req);
            var sw = Stopwatch.StartNew();
            Level level;
            try { level = Generator.Generate(cfg); }
            catch (GenerationException e)
            {
                return ApiResponse.JsonOf(new Dictionary<string, object> { ["error"] = "No valid level for these settings — try another seed. " + e.Message.Split('\n')[0] }, 422);
            }
            long ms = sw.ElapsedMilliseconds;
            int id;
            lock (gate)
            {
                id = nextId++;
                levels[id] = level; order.Enqueue(id);
                while (order.Count > 6) levels.Remove(order.Dequeue());
            }
            var v = LevelJson.ToViewer(level);
            v["id"] = id;
            v["ms"] = ms;
            v["settings"] = SettingsOf(cfg);
            v["recipe"] = cfg.Recipe;
            return ApiResponse.JsonOf(v);
        }

        /// <summary>Static files for the page (wwwroot), with a content type.</summary>
        public static ApiResponse StaticFile(string root, string path)
        {
            if (path == "/" || path == "") path = "/index.html";
            var full = Path.GetFullPath(Path.Combine(root, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal) || !File.Exists(full))
                return new ApiResponse { Status = 404, ContentType = "text/plain", Body = Encoding.UTF8.GetBytes("not found") };
            return new ApiResponse { ContentType = MimeOf(full), Body = File.ReadAllBytes(full) };
        }

        public static string MimeOf(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": case ".mjs": return "text/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".json": return "application/json";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".ico": return "image/x-icon";
                case ".glb": return "model/gltf-binary";
                default: return "application/octet-stream";
            }
        }

        public static Dictionary<string, string> ParseQuery(string q)
        {
            var d = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(q)) return d;
            foreach (var part in q.TrimStart('?').Split('&'))
            {
                if (part.Length == 0) continue;
                int i = part.IndexOf('=');
                var k = Uri.UnescapeDataString(i < 0 ? part : part.Substring(0, i));
                d[k] = i < 0 ? "" : Uri.UnescapeDataString(part.Substring(i + 1).Replace('+', ' '));
            }
            return d;
        }
    }
}
