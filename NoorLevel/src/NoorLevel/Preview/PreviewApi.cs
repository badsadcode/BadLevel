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
            ("keys", "Keys", 0, 2, 1), ("storeys", "Storeys", 1, 6, 1), ("stacking", "Rooms over rooms", 0, 1, 0.05),
            ("map_size", "Map size (m, 0 = auto)", 0, 400, 8), ("diagonal_chance", "45° corridors", 0, 1, 0.05),
            ("drop_chance", "One-way drops", 0, 1, 0.05), ("corridor_height", "Corridor height", 96, 256, 16),
        };

        /// <summary>On/off switches for things people may not want in a level (all on by default).</summary>
        public static readonly (string Name, string Label, string Help)[] Toggles =
        {
            ("pillars", "Pillars", "Free-standing columns: room pillars, yard colonnades, piers under vaults, corridor colonnades"),
            ("wall_structure", "Wall structure", "Half-columns, recesses and end features along hall walls"),
            ("portal_frames", "Portal frames", "Jambs and arched / lintelled heads round corridor openings"),
            ("landmark", "Landmark", "One deliberate landmark in a styled level's biggest space"),
            ("crates", "Crates", "Crates and low cover walls"),
            ("windows", "Windows", "Slits and glass panes between spaces"),
            ("secret", "Secret room", "A secret room behind a hidden door"),
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
            ["params"] = ConfigDocs.All.Select(p => new Dictionary<string, object>
            {
                ["group"] = p.Group, ["name"] = p.Name, ["kind"] = p.Kind, ["doc"] = p.Doc, ["min"] = p.Min, ["max"] = p.Max, ["step"] = p.Step,
                ["options"] = p.Options,
            }).ToList(),
            ["toggles"] = Toggles.Select(t => new Dictionary<string, object> { ["name"] = t.Name, ["label"] = t.Label, ["help"] = t.Help }).ToList(),
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
            // any GenConfig parameter by name (the quick "settings" and the full "params" panel alike):
            // numbers, switches, choices, and weights (dictionaries merge into the recipe's own)
            foreach (var key in new[] { "settings", "params" })
                if (req.TryGetValue(key, out var pv) && pv is Dictionary<string, object> ps2 && ps2.Count > 0)
                    Recipes.SetFields(cfg, ps2, merge: true, what: "unknown parameter");
            var tg = req.TryGetValue("toggles", out var tv) ? tv as Dictionary<string, object> : null;
            bool Off(string k) => tg != null && tg.TryGetValue(k, out var v) && v is bool on && !on;
            if (Off("pillars")) cfg.Pillars = false;
            if (Off("wall_structure")) cfg.ArchWalls = false;
            if (Off("portal_frames")) cfg.ArchPortals = false;
            if (Off("landmark")) cfg.ArchLandmark = false;
            if (Off("crates")) cfg.CrateScale = 0;
            if (Off("windows")) { cfg.Windows = 0; cfg.Vistas = 0; }
            if (Off("secret")) cfg.Secret = false;
            return cfg;
        }

        public static Dictionary<string, object> SettingsOf(GenConfig c) =>
            Settings.ToDictionary(x => x.Name, x => Recipes.ConfigField(x.Name).GetValue(c));

        /// <summary>Every parameter's value in a compiled config (weights: every listed key, 1 when the recipe leaves it).</summary>
        public static Dictionary<string, object> ValuesOf(GenConfig c)
        {
            var outp = new Dictionary<string, object>();
            foreach (var p in ConfigDocs.All)
            {
                var v = Recipes.ConfigField(p.Name).GetValue(c);
                if (p.Kind == "weights")
                {
                    var d = (System.Collections.IDictionary)v;
                    outp[p.Name] = p.Options.ToDictionary(k => k, k => d != null && d.Contains(k) ? Convert.ToDouble(d[k], CultureInfo.InvariantCulture) : 1.0);
                }
                else outp[p.Name] = v;
            }
            return outp;
        }

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
            v["params"] = ValuesOf(cfg);
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
