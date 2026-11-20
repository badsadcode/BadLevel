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

  noorlevel generate [--preset classic] [--seed 1] [--dial name=0.7 ...] [--set main_rooms=8 ...]
                     [--format glb|quake|quake2|valve|map|obj|stl|json|svg ...] [--out folder]
  noorlevel serve [--port 5173] [--www path/to/wwwroot]     the previewer in your browser
  noorlevel presets                                          list presets and dials
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
            var p = kv.Split('=');
            sets[p[0]] = p[1] == "true" || p[1] == "false" ? (object)(p[1] == "true") : double.Parse(p[1], CultureInfo.InvariantCulture);
        }
        req["dials"] = dials; req["settings"] = sets;
        var cfg = PreviewApi.ConfigFrom(req);
        var sw = Stopwatch.StartNew();
        var level = Generator.Generate(cfg);
        Console.WriteLine($"{cfg.Recipe} seed {cfg.Seed}: {level.Rooms.Count} rooms, {level.Corridors.Count} corridors, {level.Spans.Count} cells in {sw.ElapsedMilliseconds} ms");
        var outDir = One(o, "out", ".");
        Directory.CreateDirectory(outDir);
        var formats = o.TryGetValue("format", out var fl) && fl.Count > 0 ? fl.SelectMany(f => f.Split(',')).ToList() : new List<string> { "glb" };
        foreach (var f in formats)
        {
            var data = Exporters.Export(level, f, out var name);
            var path = Path.Combine(outDir, name);
            File.WriteAllBytes(path, data);
            Console.WriteLine($"  {f,-7} -> {path} ({data.Length / 1024.0:0.#} KB)");
        }
        return 0;
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
                foreach (var cand in new[] { "wwwroot", Path.Combine("src", "NoorLevel.Previewer", "wwwroot"), Path.Combine("NoorLevel", "src", "NoorLevel.Previewer", "wwwroot") })
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
