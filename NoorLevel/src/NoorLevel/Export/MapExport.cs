using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace NoorLevel.Export
{
    public enum MapGame { Quake, Quake2, Valve220 }

    /// <summary>.map export: Quake 1 (Standard), Quake 2, and Valve 220 (Half-Life; also what
    /// TrenchBroom, J.A.C.K., Godot's func_godot and many importers read best). Opens in TrenchBroom,
    /// compiles with ericw-tools / ZHLT. The level is centred on the origin in steps of 64 (Quake's
    /// protocol only carries +-4096).</summary>
    public static class MapExport
    {
        static readonly Dictionary<string, string> Tex = new Dictionary<string, string>
        {
            ["floor"] = "nl_floor", ["wall"] = "nl_wall", ["ceiling"] = "nl_ceiling", ["sky"] = "sky1",
        };
        static readonly Dictionary<string, (string Name, int Contents, int Flags)> Q2Tex = new Dictionary<string, (string, int, int)>
        {
            ["nl_floor"] = ("noorlevel/floor", 0, 0), ["nl_wall"] = ("noorlevel/wall", 0, 0), ["nl_ceiling"] = ("noorlevel/ceiling", 0, 0),
            ["sky1"] = ("noorlevel/sky", 0, 4), ["nl_door"] = ("noorlevel/door", 0, 0), ["trigger"] = ("noorlevel/trigger", 0, 128),
            ["nl_glass"] = ("noorlevel/glass", 2, 16), ["nl_lift"] = ("noorlevel/lift", 0, 0), ["nl_ladder"] = ("noorlevel/ladder", 1 | 0x20000000, 0),
        };
        static readonly Dictionary<string, (string Q1, int Flag, string Q2)> Keys = new Dictionary<string, (string, int, string)>
        {
            ["red"] = ("item_key2", 8, "key_red_key"), ["blue"] = ("item_key1", 16, "key_blue_key"),
        };

        struct P { public double X, Y, Z; public P(double x, double y, double z) { X = x; Y = y; Z = z; } }

        static string F(double v) => Math.Abs(v - Math.Round(v)) < 1e-9 ? ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture);

        sealed class Writer
        {
            public MapGame Game;
            public string FaceLine(P p0, P p1, P p2, string tex, P inside)
            {
                // qbsp: normal = (p0 - p1) x (p2 - p1) must point out of the brush
                double ax = p0.X - p1.X, ay = p0.Y - p1.Y, az = p0.Z - p1.Z, bx = p2.X - p1.X, by = p2.Y - p1.Y, bz = p2.Z - p1.Z;
                double nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx;
                if (nx * (p0.X - inside.X) + ny * (p0.Y - inside.Y) + nz * (p0.Z - inside.Z) < 0) { var t = p1; p1 = p2; p2 = t; nx = -nx; ny = -ny; nz = -nz; }
                string pts = $"( {F(p0.X)} {F(p0.Y)} {F(p0.Z)} ) ( {F(p1.X)} {F(p1.Y)} {F(p1.Z)} ) ( {F(p2.X)} {F(p2.Y)} {F(p2.Z)} )";
                switch (Game)
                {
                    case MapGame.Quake2:
                        var q = Q2Tex.TryGetValue(tex, out var v) ? v : (tex, 0, 0);
                        return $"{pts} {q.Item1} 0 0 0 1 1 {q.Item2} {q.Item3} 0";
                    case MapGame.Valve220:
                        double anx = Math.Abs(nx), any = Math.Abs(ny), anz = Math.Abs(nz);
                        string uv = anz >= anx && anz >= any ? "[ 1 0 0 0 ] [ 0 -1 0 0 ]"
                                  : anx >= any ? "[ 0 1 0 0 ] [ 0 0 -1 0 ]" : "[ 1 0 0 0 ] [ 0 0 -1 0 ]";
                        return $"{pts} {tex} {uv} 0 1 1";
                    default:
                        return $"{pts} {tex} 0 0 0 1 1";
                }
            }

            public string Box(P lo, P hi, string top, string bottom, string side)
            {
                double x1 = lo.X, y1 = lo.Y, z1 = lo.Z, x2 = hi.X, y2 = hi.Y, z2 = hi.Z;
                var inside = new P((x1 + x2) / 2, (y1 + y2) / 2, (z1 + z2) / 2);
                var sb = new StringBuilder("{\n");
                sb.Append(FaceLine(new P(x1, y1, z1), new P(x1, y1 + 1, z1), new P(x1, y1, z1 + 1), side, inside)).Append('\n');
                sb.Append(FaceLine(new P(x1, y1, z1), new P(x1, y1, z1 + 1), new P(x1 + 1, y1, z1), side, inside)).Append('\n');
                sb.Append(FaceLine(new P(x1, y1, z1), new P(x1 + 1, y1, z1), new P(x1, y1 + 1, z1), bottom, inside)).Append('\n');
                sb.Append(FaceLine(new P(x2, y2, z2), new P(x2, y2 + 1, z2), new P(x2 + 1, y2, z2), top, inside)).Append('\n');
                sb.Append(FaceLine(new P(x2, y2, z2), new P(x2 + 1, y2, z2), new P(x2, y2, z2 + 1), side, inside)).Append('\n');
                sb.Append(FaceLine(new P(x2, y2, z2), new P(x2, y2, z2 + 1), new P(x2, y2 + 1, z2), side, inside)).Append('\n');
                return sb.Append('}').ToString();
            }

            public string Box(Vec3 lo, Vec3 hi, string tex) => Box(new P(lo.X, lo.Y, lo.Z), new P(hi.X, hi.Y, hi.Z), tex, tex, tex);

            public string Prism(Brush b, string tex)
            {
                double s = b.X1 - b.X0;
                var tri = Shape.CornerVertices(b.Corner.Value).Solid.Select(uv => (X: b.X0 + uv.Item1 * s, Y: b.Y0 + uv.Item2 * s)).ToArray();
                var inside = new P(tri.Average(t => t.X), tri.Average(t => t.Y), (b.Z0 + b.Z1) / 2.0);
                var sb = new StringBuilder("{\n");
                sb.Append(FaceLine(new P(tri[0].X, tri[0].Y, b.Z0), new P(tri[1].X, tri[1].Y, b.Z0), new P(tri[2].X, tri[2].Y, b.Z0), tex, inside)).Append('\n');
                sb.Append(FaceLine(new P(tri[0].X, tri[0].Y, b.Z1), new P(tri[1].X, tri[1].Y, b.Z1), new P(tri[2].X, tri[2].Y, b.Z1), tex, inside)).Append('\n');
                for (int i = 0; i < 3; i++)
                {
                    var a = tri[i]; var c = tri[(i + 1) % 3];
                    sb.Append(FaceLine(new P(a.X, a.Y, b.Z0), new P(c.X, c.Y, b.Z0), new P(a.X, a.Y, b.Z1), tex, inside)).Append('\n');
                }
                return sb.Append('}').ToString();
            }

            public string Text(Brush b) => b.Corner != null ? Prism(b, Tex[b.Side])
                : Box(new P(b.X0, b.Y0, b.Z0), new P(b.X1, b.Y1, b.Z1), Tex[b.Top], Tex[b.Bottom], Tex[b.Side]);
        }

        static string Ent(IEnumerable<(string K, object V)> props, IEnumerable<string> brushes = null)
        {
            var sb = new StringBuilder("{\n");
            foreach (var (k, v) in props) sb.Append('"').Append(k).Append("\" \"").Append(v is double d ? F(d) : Convert.ToString(v, CultureInfo.InvariantCulture)).Append("\"\n");
            if (brushes != null) foreach (var b in brushes) sb.Append(b).Append('\n');
            return sb.Append('}').ToString();
        }

        public static (int X, int Y) Centre(List<Brush> brushes)
        {
            if (brushes.Count == 0) return (0, 0);
            int x0 = brushes.Min(b => b.X0), x1 = brushes.Max(b => b.X1), y0 = brushes.Min(b => b.Y0), y1 = brushes.Max(b => b.Y1);
            return (-(int)Math.Round((x0 + x1) / 2.0 / 64) * 64, -(int)Math.Round((y0 + y1) / 2.0 / 64) * 64);
        }

        public static string ToMap(Level level, MapGame game = MapGame.Quake)
        {
            bool q2 = game == MapGame.Quake2, hl = game == MapGame.Valve220;
            var w = new Writer { Game = game };
            var raw = Brushes.Build(level);
            var (ox, oy) = Centre(raw);
            var brushes = raw.Select(b => b.Shift(ox, oy)).ToList();
            Vec3 At(Vec3 p) => new Vec3(p.X + ox, p.Y + oy, p.Z);
            (Vec3, Vec3) Bx(Entity e) => (At(e.Bounds.Value.Lo), At(e.Bounds.Value.Hi));
            string Origin(Vec3 p) { var a = At(p); return $"{F(a.X)} {F(a.Y)} {F(a.Z)}"; }

            var world = new List<(string, object)> { ("classname", "worldspawn"), ("message", $"NoorLevel seed {level.Seed}"), ("_noorlevel_seed", level.Seed) };
            if (game == MapGame.Quake) { world.Add(("wad", Textures.WadName)); world.Add(("light", 24)); }
            if (hl) { world.Add(("mapversion", 220)); world.Add(("wad", Textures.Wad3Name)); world.Add(("skyname", "desert")); }
            if (q2) world.Add(("sky", "unit1_"));
            world.Add(("_noorlevel_offset", $"{ox} {oy} 0"));
            var outp = new List<string>
            {
                q2 ? "// Game: Quake 2" : hl ? "// Game: Half-Life" : "// Game: Quake",
                q2 ? "// Format: Quake2" : hl ? "// Format: Valve" : "// Format: Standard",
                $"// Generated by NoorLevel (seed {level.Seed})",
                Ent(world, brushes.Where(b => b.Part == null).Select(w.Text)),
            };
            int gid = 0;
            foreach (var grp in brushes.Where(b => b.Part != null).GroupBy(b => b.Part).OrderBy(g => g.Key, StringComparer.Ordinal))
                outp.Add(Ent(new (string, object)[] { ("classname", "func_group"), ("_tb_type", "_tb_group"), ("_tb_name", grp.Key), ("_tb_id", ++gid) }, grp.Select(w.Text)));

            int nDoor = 0;
            double fade = q2 ? 0.85 : 0.5;
            foreach (var e in level.Entities)
            {
                switch (e.Kind)
                {
                    case "player_start":
                        outp.Add(Ent(new (string, object)[] { ("classname", "info_player_start"), ("origin", Origin(e.Pos)), ("angle", e.Props.TryGetValue("angle", out var a) ? a : 0) }));
                        break;
                    case "light":
                        var lp = new List<(string, object)> { ("classname", "light"), ("origin", Origin(e.Pos)), ("light", e.Props.TryGetValue("intensity", out var li) ? li : 300) };
                        if (!hl) lp.Add(("wait", fade)); else lp[2] = ("_light", $"255 240 220 {(e.Props.TryGetValue("intensity", out var lj) ? lj : 300)}");
                        outp.Add(Ent(lp));
                        break;
                    case "key":
                        var color = (string)e.Props["color"];
                        outp.Add(Ent(new (string, object)[] { ("classname", hl ? "info_target" : q2 ? Keys[color].Q2 : Keys[color].Q1), ("origin", Origin(e.Pos)), ("_noorlevel_key", color) }));
                        break;
                    case "door":
                    {
                        var (lo, hi) = Bx(e); var key = (string)e.Props["key"];
                        if (q2)
                        {
                            string name = $"nl_door{++nDoor}";
                            outp.Add(Ent(new (string, object)[] { ("classname", "func_door"), ("angle", -1), ("wait", -1), ("targetname", name), ("_noorlevel_key", key) }, new[] { w.Box(lo, hi, "nl_door") }));
                            outp.Add(Ent(new (string, object)[] { ("classname", "trigger_multiple"), ("target", name + "_key") },
                                new[] { w.Box(new Vec3(lo.X - 32, lo.Y - 32, lo.Z), new Vec3(hi.X + 32, hi.Y + 32, hi.Z), "trigger") }));
                            outp.Add(Ent(new (string, object)[] { ("classname", "trigger_key"), ("targetname", name + "_key"), ("target", name), ("item", Keys[key].Q2), ("origin", Origin(e.Pos)) }));
                        }
                        else if (hl)
                            outp.Add(Ent(new (string, object)[] { ("classname", "func_door"), ("angles", "-90 0 0"), ("speed", 100), ("wait", -1), ("_noorlevel_key", key) }, new[] { w.Box(lo, hi, "nl_door") }));
                        else
                            outp.Add(Ent(new (string, object)[] { ("classname", "func_door"), ("angle", -1), ("spawnflags", Keys[key].Flag), ("_noorlevel_key", key) }, new[] { w.Box(lo, hi, "nl_door") }));
                        break;
                    }
                    case "secret_door":
                    {
                        var (lo, hi) = Bx(e);
                        outp.Add(Ent(new (string, object)[] { ("classname", hl ? "func_door" : "func_door_secret"), ("spawnflags", 0) }, new[] { w.Box(lo, hi, "nl_wall") }));
                        break;
                    }
                    case "lift":
                    {
                        var (lo, hi) = Bx(e);
                        outp.Add(Ent(new (string, object)[] { ("classname", "func_plat"), ("height", e.Props["travel"]), ("speed", 150), ("_noorlevel_low", e.Props["low"]), ("_noorlevel_high", e.Props["high"]) }, new[] { w.Box(lo, hi, "nl_lift") }));
                        break;
                    }
                    case "ladder":
                    {
                        var (lo, hi) = Bx(e);
                        if (q2) outp.Add(Ent(new (string, object)[] { ("classname", "func_group"), ("_noorlevel_kind", "ladder") }, new[] { w.Box(lo, new Vec3(hi.X, hi.Y, Convert.ToDouble(e.Props["high"])), "nl_ladder") }));
                        else outp.Add(Ent(new (string, object)[] { ("classname", "func_ladder") }, new[] { w.Box(lo, hi, "nl_ladder") }));
                        break;
                    }
                    case "exit":
                    {
                        var (lo, hi) = Bx(e);
                        if (q2)
                        {
                            outp.Add(Ent(new (string, object)[] { ("classname", "trigger_multiple"), ("target", "nl_exit") }, new[] { w.Box(lo, hi, "trigger") }));
                            outp.Add(Ent(new (string, object)[] { ("classname", "target_changelevel"), ("targetname", "nl_exit"), ("map", "base1"), ("origin", Origin(e.Pos)) }));
                        }
                        else outp.Add(Ent(new (string, object)[] { ("classname", "trigger_changelevel"), ("map", hl ? "c1a0" : "start") }, new[] { w.Box(lo, hi, "trigger") }));
                        break;
                    }
                    default:
                        var props = new List<(string, object)> { ("classname", "info_null"), ("origin", Origin(e.Pos)), ("_noorlevel_kind", e.Kind) };
                        foreach (var kv in e.Props) props.Add(("_noorlevel_" + kv.Key, kv.Value));
                        outp.Add(Ent(props));
                        break;
                }
            }
            return string.Join("\n", outp) + "\n";
        }
    }
}
