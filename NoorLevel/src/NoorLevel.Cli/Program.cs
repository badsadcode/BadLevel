using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using NoorLevel;
using NoorLevel.Export;
using NoorLevel.Preview;

static class Program
{
    static readonly string Usage = @"noorlevel — procedural FPS level blockouts

  noorlevel generate [--preset classic|surprise] [--seed 1] [--dial name=0.7 ...] [--set main_rooms=8 ...]
                     [--off pillars,crates,wall_structure,portal_frames,landmark,windows,secret]
                     [--coth path/to/NoorEngine/samples/Hillside/Assets/Sprites]
                     [--out folder | --out exact/file.glb] [--json-result] [--max-ms 5000]
                     [--format glb|noorengine|quake|quake2|valve|map|obj|stl|json|svg ...] [--out folder]
  noorlevel serve [--port 5173] [--www path/to/wwwroot]     the previewer in your browser
  noorlevel presets                                          list presets and dials
  noorlevel params                                           list every parameter (--set name=value)
  noorlevel formats                                          list export formats
  noorlevel sweep [seeds]                                    generate every preset, report

Dials: " + string.Join(", ", Recipe.Dials);

    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] == "-h" || args[0] == "--help") { Console.WriteLine(Usage); return 0; }
        try
        {
            switch (args[0])
            {
                case "generate": return GenerateCmd(args.Skip(1).ToArray());
                case "serve": return Serve(args.Skip(1).ToArray());
                case "sweep": return Sweep(args.Length > 1 ? int.Parse(args[1]) : 5);
                case "presets":
                    foreach (var r in Recipes.Presets.Values)
                        Console.WriteLine($"{r.Name,-11} {r.Description}\n            " + string.Join("  ", Recipe.Dials.Select(d => $"{d}={r.Get(d):0.##}")));
                    return 0;
                case "params":
                    foreach (var g in ConfigDocs.All.GroupBy(x => x.Group))
                    {
                        Console.WriteLine($"[{g.Key}]");
                        foreach (var x in g)
                        {
                            string range = x.Kind == "bool" ? "true|false" : x.Kind == "choice" ? string.Join("|", x.Options.Select(o => o == "" ? "\"\"" : o))
                                : x.Kind == "weights" ? string.Join(",", x.Options.Select(o => $"{x.Name}.{o}")) : $"{x.Min}..{x.Max}";
                            Console.WriteLine($"  {x.Name,-22} {range}");
                            if (!string.IsNullOrEmpty(x.Doc)) Console.WriteLine($"  {"",-22} {x.Doc}");
                        }
                    }
                    return 0;
                case "formats":
                    foreach (var f in Exporters.Formats) Console.WriteLine($"{f.Id,-7} {f.Label,-24} {f.Description}");
                    return 0;
            }
            Console.Error.WriteLine("unknown command " + args[0] + "\n\n" + Usage);
            return 2;
        }
        catch (Exception e) when (!(e is OutOfMemoryException))
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 1;
        }
    }

    static Dictionary<string, List<string>> Opts(string[] a)
    {
        var d = new Dictionary<string, List<string>>();
        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--")) throw new ArgumentException("unexpected " + a[i]);
            var k = a[i].Substring(2);
            if (!d.TryGetValue(k, out var l)) d[k] = l = new List<string>();
            if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) l.Add(a[++i]);
        }
        return d;
    }

    static string One(Dictionary<string, List<string>> o, string k, string def) => o.TryGetValue(k, out var l) && l.Count > 0 ? l[l.Count - 1] : def;

    static int GenerateCmd(string[] a)
    {
        var o = Opts(a);
        var req = new Dictionary<string, object>
        {
            ["preset"] = One(o, "preset", "classic"),
            ["seed"] = double.Parse(One(o, "seed", "1"), CultureInfo.InvariantCulture),
        };
        var dials = new Dictionary<string, object>(); var sets = new Dictionary<string, object>();
        foreach (var kv in o.TryGetValue("dial", out var dl) ? dl : new List<string>()) { var p = kv.Split('='); dials[p[0]] = double.Parse(p[1], CultureInfo.InvariantCulture); }
        foreach (var kv in o.TryGetValue("set", out var sl) ? sl : new List<string>())
        {
            // name=value; a weight as dict.key=value (feature_weights.pillars=0); a choice as text (arch_style=gothic)
            int eq = kv.IndexOf('=');
            string k = kv.Substring(0, eq), v = kv.Substring(eq + 1);
            object val = v == "true" || v == "false" ? (object)(v == "true")
                : double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv) ? dv : v;
            int dot = k.IndexOf('.');
            if (dot > 0)
            {
                string dk = k.Substring(0, dot);
                if (!(sets.TryGetValue(dk, out var sub) && sub is Dictionary<string, object> sd)) sets[dk] = sd = new Dictionary<string, object>();
                sd[k.Substring(dot + 1)] = val;
            }
            else sets[k] = val;
        }
        req["dials"] = dials; req["settings"] = sets;
        // --off pillars,crates,wall_structure,portal_frames,landmark,windows,secret
        var offs = new Dictionary<string, object>();
        foreach (var t in (o.TryGetValue("off", out var ol) ? ol : new List<string>()).SelectMany(x => x.Split(','))) offs[t.Trim()] = false;
        req["toggles"] = offs;
        if (One(o, "coth", null) is string cothDir) Exporters.CothDir = cothDir;   // Coth's sprite files, for --format noorengine
        bool jsonResult = o.ContainsKey("json-result");
        int maxMs = int.Parse(One(o, "max-ms", "0"), CultureInfo.InvariantCulture);
        var formats = o.TryGetValue("format", out var fl) && fl.Count > 0 ? fl.SelectMany(f => f.Split(',')).ToList() : new List<string> { "glb" };
        string outArg = One(o, "out", ".");
        // --out is a folder (names like noorlevel_7.glb), or, with one format, an exact file name ending in its extension
        var fmt0 = Exporters.Formats.FirstOrDefault(f => f.Id == formats[0]);
        bool outIsFile = formats.Count == 1 && fmt0 != null && outArg.EndsWith(fmt0.Extension, StringComparison.OrdinalIgnoreCase);
        var sw = Stopwatch.StartNew();
        GenConfig cfg = null;

        int Result(bool ok, string file, Level lvl, List<string> errors, int code)
        {
            if (jsonResult)
                Console.WriteLine(Json.Write(new Dictionary<string, object>
                {
                    ["ok"] = ok, ["file"] = file, ["ms"] = sw.ElapsedMilliseconds, ["seed"] = cfg?.Seed ?? 0,
                    ["recipe"] = cfg?.Recipe, ["rooms"] = lvl?.Rooms.Count ?? 0,
                    ["errors"] = errors.Select(e => e.Length > 400 ? e.Substring(0, 400) + "..." : e).ToList(),
                }));
            else foreach (var e in errors) Console.Error.WriteLine("error: " + e);
            return code;
        }

        Level level;
        try
        {
            cfg = PreviewApi.ConfigFrom(req);
            var job = System.Threading.Tasks.Task.Run(() => Generator.Generate(cfg));
            if (maxMs > 0 && !job.Wait(maxMs))
            {
                // a guaranteed upper bound: give up cleanly (the process ends, the worker with it)
                Environment.Exit(Result(false, null, null, new List<string> { $"no level within --max-ms {maxMs}" }, 3));
            }
            level = job.GetAwaiter().GetResult();
        }
        catch (GenerationException e) { return Result(false, null, null, new List<string> { e.Message.Split('\n')[0] }, 1); }
        catch (ArgumentException e) { return Result(false, null, null, new List<string> { e.Message }, 2); }
        if (!jsonResult)
            Console.WriteLine($"{cfg.Recipe} seed {cfg.Seed}: {level.Rooms.Count} rooms, {level.Corridors.Count} corridors, {level.Spans.Count} cells in {sw.ElapsedMilliseconds} ms");
        string written = null;
        try
        {
            foreach (var f in formats)
            {
                var data = Exporters.Export(level, f, out var name);
                string path = outIsFile ? outArg : Path.Combine(outArg, name);
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, data);
                written ??= path;
                if (!jsonResult) Console.WriteLine($"  {f,-7} -> {path} ({data.Length / 1024.0:0.#} KB)");
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
        {
            return Result(false, null, level, new List<string> { e.Message }, 4);
        }
        return jsonResult ? Result(true, written, level, new List<string>(), 0) : 0;
    }

    static int Sweep(int seeds)
    {
        int ok = 0, n = 0;
        foreach (var preset in Recipes.Presets.Keys)
            for (int seed = 1; seed <= seeds; seed++)
            {
                n++;
                var sw = Stopwatch.StartNew();
                try
                {
                    var lvl = Generator.Generate(preset, seed);
                    foreach (var f in Exporters.Formats) Exporters.Export(lvl, f.Id, out _);
                    ok++;
                    Console.WriteLine($"{preset,-10} {seed,3} ok  rooms={lvl.Rooms.Count} cells={lvl.Spans.Count} {sw.ElapsedMilliseconds}ms");
                }
                catch (Exception e) { Console.WriteLine($"{preset,-10} {seed,3} FAIL {e.GetType().Name}: {e.Message.Split('\n').Last()}\n{(e is GenerationException ? "" : e.StackTrace)}"); }
            }
        Console.WriteLine($"{ok}/{n}");
        return ok == n ? 0 : 1;
    }

    static string FindWww(string given)
    {
        if (given != null) return given;
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var d = new DirectoryInfo(start);
            for (; d != null; d = d.Parent)
            {
                foreach (var cand in new[] { "wwwroot", Path.Combine("Previewer", "wwwroot"), Path.Combine("src", "NoorLevel.Previewer", "wwwroot"), Path.Combine("NoorLevel", "src", "NoorLevel.Previewer", "wwwroot") })
                    if (File.Exists(Path.Combine(d.FullName, cand, "index.html"))) return Path.Combine(d.FullName, cand);
            }
        }
        throw new DirectoryNotFoundException("wwwroot not found; pass --www");
    }

    static int Serve(string[] a)
    {
        var o = Opts(a);
        int port = int.Parse(One(o, "port", "5173"));
        string www = FindWww(One(o, "www", null));
        var api = new PreviewApi();
        var http = new HttpListener();
        http.Prefixes.Add($"http://localhost:{port}/");
        http.Start();
        Console.WriteLine($"NoorLevel previewer: http://localhost:{port}/   (serving {www}; Ctrl+C to stop)");
        while (true)
        {
            var ctx = http.GetContext();
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var req = ctx.Request; var path = req.Url.AbsolutePath;
                    string body = null;
                    if (req.HasEntityBody) using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
                    var resp = path.StartsWith("/api/")
                        ? api.Handle(req.HttpMethod, path, PreviewApi.ParseQuery(req.Url.Query), body)
                        : PreviewApi.StaticFile(www, path);
                    ctx.Response.StatusCode = resp.Status;
                    ctx.Response.ContentType = resp.ContentType;
                    ctx.Response.Headers["Cache-Control"] = "no-store";
                    if (resp.FileName != null) ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{resp.FileName}\"";
                    ctx.Response.ContentLength64 = resp.Body.Length;
                    ctx.Response.OutputStream.Write(resp.Body, 0, resp.Body.Length);
                    ctx.Response.Close();
                    if (path.StartsWith("/api/")) Console.WriteLine($"{req.HttpMethod} {req.Url.PathAndQuery} -> {resp.Status} ({resp.Body.Length} bytes)");
                }
                catch (Exception e) { Console.Error.WriteLine(e.Message); try { ctx.Response.Abort(); } catch { } }
            });
        }
    }
}
