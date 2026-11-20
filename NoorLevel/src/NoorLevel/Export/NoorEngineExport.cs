using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace NoorLevel.Export
{
    /// <summary>A NoorEngine project with the level in it, ready to open in NoorEditor and press Play:
    /// the level as a Noor Interchange asset (GLB + .noor.json manifest + recipe), a scene placing each
    /// of its parts with a static mesh collider, point lights for its lamps and torches, a first-person
    /// player, and Coth (The Uncounted's wanderer) walking a route through the rooms, getting lost.
    /// Coordinates: NoorEngine is +Y up, metres, right-handed, forward -Z; one NoorLevel cell (32
    /// units) is one metre, map (x, y, z) -> engine (x, z, -y) / 32 (the .glb already is).</summary>
    public static class NoorEngineExport
    {
        /// <summary>Coth's sprite sheet in NoorEngine's samples (samples/Hillside/Assets/Sprites/coth.noorsprite).</summary>
        public const string CothSheet = "5b417e00-0002-4000-8000-000000000001";
        const double S = 1.0 / 32.0;
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        /// <summary>A stable UUID from a label (same level, same ids: scenes keep pointing at it when regenerated).</summary>
        public static string Uuid(string label)
        {
            ulong a = Rng.Hash("noorlevel/a/" + label), b = Rng.Hash("noorlevel/b/" + label);
            var h = $"{a:x16}{b:x16}".ToCharArray();
            h[12] = '5';                                              // a name-based (v5-like) id
            h[16] = "89ab"[(int)(b >> 60) & 3];
            var s = new string(h);
            return $"{s.Substring(0, 8)}-{s.Substring(8, 4)}-{s.Substring(12, 4)}-{s.Substring(16, 4)}-{s.Substring(20, 12)}";
        }

        static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(data).Select(x => x.ToString("x2")));
        }

        static double[] Pos(Vec3 p) => new[] { Math.Round(p.X * S, 3), Math.Round(p.Z * S, 3), Math.Round(-p.Y * S, 3) };

        /// <summary>Rotation about +Y so the entity's forward (-Z) points along a map direction given as a Quake angle.</summary>
        static double[] YawQuat(double quakeAngleDeg)
        {
            double a = quakeAngleDeg * Math.PI / 180;
            double dx = Math.Cos(a), dz = -Math.Sin(a);                // the map direction in engine axes
            double theta = Math.Atan2(-dx, -dz);                      // forward(-Z) rotated by theta about +Y is (-sin, 0, -cos)
            return new[] { 0, Math.Round(Math.Sin(theta / 2), 6), 0, Math.Round(Math.Cos(theta / 2), 6) };
        }

        static Dictionary<string, object> Transform(double[] pos, double[] rot = null) => new Dictionary<string, object>
        {
            ["position"] = pos, ["rotation"] = rot ?? new double[] { 0, 0, 0, 1 }, ["scale"] = new double[] { 1, 1, 1 },
        };

        static Dictionary<string, object> Ent(string id, string name, Dictionary<string, object> transform, Dictionary<string, object> components, string parent = null) =>
            new Dictionary<string, object>
            {
                ["id"] = id, ["name"] = name, ["layer"] = "default", ["tags"] = new List<string>(), ["parent"] = parent,
                ["transform"] = transform, ["components"] = components,
            };

        /// <summary>The project's files as (relative path, bytes). Pass the folder holding Coth's sprite files
        /// (coth.png, coth.noorsprite and their .noormeta, e.g. NoorEngine/samples/Hillside/Assets/Sprites) to
        /// include him; without it the scene still names his sheet and the README says where to copy it from.</summary>
        public static List<(string Path, byte[] Data)> Project(Level level, string cothDir = null)
        {
            string recipe = string.IsNullOrEmpty(level.Config.Recipe) ? "level" : level.Config.Recipe.Replace("surprise: ", "surprise ").Replace(" + ", "_").Replace(' ', '_');
            string name = $"NoorLevel_{recipe}_{level.Seed}";
            string asset = name;
            var files = new List<(string, byte[])>();
            var glb = Gltf.ToGlb(level, false, out var meshNames);
            string assetId = Uuid($"{name}/asset");
            string recipeJson = Json.Write(new Dictionary<string, object>
            {
                ["format"] = "noorlevel-recipe", ["version"] = 1, ["recipe"] = level.Config.Recipe, ["seed"] = level.Seed,
                ["config"] = LevelJson.ConfigInfo(level.Config),
            });
            var recipeBytes = Encoding.UTF8.GetBytes(recipeJson);
            var (zlo, zhi) = level.ZRange();
            var manifest = new Dictionary<string, object>
            {
                ["noor_interchange"] = "0.4", ["id"] = assetId, ["name"] = asset, ["kind"] = "level",
                ["generator"] = new Dictionary<string, object> { ["tool"] = "NoorLevel", ["version"] = typeof(NoorEngineExport).Assembly.GetName().Version.ToString(3), ["determinism"] = "cross_platform" },
                ["recipe"] = new Dictionary<string, object> { ["file"] = asset + ".recipe.json", ["seed"] = (ulong)Math.Max(0, level.Seed), ["sha256"] = Sha256(recipeBytes) },
                ["created"] = "2026-11-20T00:00:00Z",
                ["space"] = new Dictionary<string, object> { ["up"] = "+Y", ["forward"] = "+Z", ["handedness"] = "right", ["units"] = "meters" },
                ["bounds"] = new Dictionary<string, object>
                {
                    ["min"] = new[] { 0.0, zlo * S, -level.Height * 1.0 },
                    ["max"] = new[] { level.Width * 1.0, zhi * S, 0.0 },
                },
                ["files"] = new List<object>
                {
                    new Dictionary<string, object> { ["path"] = asset + ".glb", ["role"] = "model", ["sha256"] = Sha256(glb) },
                    new Dictionary<string, object> { ["path"] = asset + ".recipe.json", ["role"] = "recipe", ["sha256"] = Sha256(recipeBytes) },
                },
                ["collision"] = "mesh",
                ["tags"] = new List<string> { "noorlevel", recipe },
            };
            files.Add(($"Assets/Levels/{asset}.glb", glb));
            files.Add(($"Assets/Levels/{asset}.glb.noormeta", Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string, object>
                { ["noor_meta"] = 1, ["guid"] = assetId, ["importer"] = "gltf", ["source_sha256"] = Sha256(glb), ["settings"] = new Dictionary<string, object>() }))));
            files.Add(($"Assets/Levels/{asset}.noor.json", Encoding.UTF8.GetBytes(Json.Write(manifest))));
            files.Add(($"Assets/Levels/{asset}.recipe.json", recipeBytes));

            // --- the scene
            var ents = new List<object>();
            int n = 0;
            string Id() => Uuid($"{name}/entity/{n++}");
            // ambient light: a faint sun with a dim sky colour (as NoorEngine's Lights test scene), so shadows aren't black
            ents.Add(Ent(Id(), "Ambient", Transform(new double[] { 0, 10, 0 }, new[] { -0.5, 0.2, 0.0, 0.84 }), new Dictionary<string, object>
            {
                ["light"] = new Dictionary<string, object> { ["type"] = "directional", ["color"] = "#B8C4E0", ["illuminance"] = level.Sky.Count > 0 ? 20000 : 300, ["sky_color"] = "#3A4058", ["sky_luminance"] = 40 },
            }));
            if (level.Sky.Count > 0)
                ents.Add(Ent(Id(), "Sky", Transform(new double[] { 0, 0, 0 }), new Dictionary<string, object>
                { ["environment"] = new Dictionary<string, object> { ["sky"] = "atmosphere", ["turbidity"] = 2, ["ground_albedo"] = 0.3 } }));
            // the level: one entity per named part (a room, a corridor, an element), each a static mesh collider
            string levelRoot = Id();
            ents.Add(Ent(levelRoot, asset, Transform(new double[] { 0, 0, 0 }), new Dictionary<string, object>
            {
                ["noor"] = new Dictionary<string, object> { ["generator"] = "NoorLevel", ["recipe"] = level.Config.Recipe, ["seed"] = level.Seed },
            }));
            for (int i = 0; i < meshNames.Count; i++)
            {
                var comps = new Dictionary<string, object> { ["mesh"] = new Dictionary<string, object> { ["asset"] = assetId, ["mesh_index"] = i } };
                if (meshNames[i] != "water") comps["collider"] = new Dictionary<string, object> { ["shape"] = "mesh" };   // (water: wade and swim through it)
                comps["noor"] = new Dictionary<string, object> { ["part"] = meshNames[i] };
                ents.Add(Ent(Id(), meshNames[i], Transform(new double[] { 0, 0, 0 }), comps, levelRoot));
            }
            // lights: ceiling lamps warm white, torches orange (lumens; NoorEngine's lights fade by their range)
            int lamps = 0;
            foreach (var e in level.Entities.Where(e => e.Kind == "light" || e.Kind == "torch"))
            {
                bool torch = e.Kind == "torch";
                double inten = e.Props.TryGetValue("intensity", out var iv) ? Convert.ToDouble(iv, CI) : 300;
                ents.Add(Ent(Id(), (torch ? "Torch " : "Lamp ") + (++lamps), Transform(Pos(e.Pos)), new Dictionary<string, object>
                {
                    ["light"] = new Dictionary<string, object>
                    {
                        ["type"] = "point", ["color"] = torch ? "#FF9A40" : "#FFE2BC",
                        ["intensity"] = Math.Round(inten * (torch ? 7 : 9)), ["range"] = torch ? 9 : 14,
                    },
                }));
            }
            // the exit: a green marker the player script looks for
            var exit = level.Entities.FirstOrDefault(e => e.Kind == "exit");
            if (exit != null)
            {
                var (lo, hi) = exit.Bounds.Value;
                ents.Add(Ent(Id(), "Exit", Transform(Pos(new Vec3((lo.X + hi.X) / 2, (lo.Y + hi.Y) / 2, lo.Z + 2))), new Dictionary<string, object>
                {
                    ["primitive"] = new Dictionary<string, object> { ["shape"] = "box", ["size"] = new[] { (hi.X - lo.X) * S, 0.06, (hi.Y - lo.Y) * S }, ["color"] = "#40E070" },
                }));
            }
            // the player: NoorEngine's character controller, 1.7 m (NoorLevel keeps 1.75 m of headroom everywhere it lets you go),
            // stepping up 0.6 m (NoorLevel's stairs rise 0.5 m), the camera at 1.55 m
            var start = level.Entities.First(e => e.Kind == "player_start");
            var startCell = level.CellOf(start.Pos);
            double startFloor = FloorUnder(level, start.Pos);
            string player = Id();
            double angle = start.Props.TryGetValue("angle", out var av) ? Convert.ToDouble(av, CI) : 0;
            ents.Add(Ent(player, "Player", Transform(Pos(new Vec3(start.Pos.X, start.Pos.Y, startFloor)), YawQuat(angle)), new Dictionary<string, object>
            {
                ["character"] = new Dictionary<string, object> { ["radius"] = 0.3, ["height"] = 1.7, ["step_height"] = 0.6, ["max_slope"] = 50 },
                ["script"] = new Dictionary<string, object> { ["file"] = "Scripts/NoorLevelPlayer.luau", ["params"] = new Dictionary<string, object> { ["speed"] = 5, ["sprint"] = 1.7, ["jump_speed"] = 5, ["look_sensitivity"] = 0.12 } },
            }));
            ents.Add(Ent(Id(), "Camera", Transform(new double[] { 0, 1.55, 0 }), new Dictionary<string, object>
            {
                ["camera"] = new Dictionary<string, object> { ["fov"] = 75, ["near"] = 0.05, ["ev100"] = "auto" },
                ["audio_listener"] = new Dictionary<string, object>(),
            }, player));
            // Coth: he starts somewhere in the middle of the level and wanders from room to room
            var route = CothRoute(level, Rng.FromLabel($"coth/{level.Seed}"));
            if (route.Count > 0)
            {
                var c0 = route[0];
                ents.Add(Ent(Id(), "Coth", Transform(new[] { c0.X, c0.Y, c0.Z }), new Dictionary<string, object>
                {
                    ["sprite"] = new Dictionary<string, object> { ["sheet"] = CothSheet, ["animation"] = "idle" },
                    ["character"] = new Dictionary<string, object> { ["radius"] = 0.3, ["height"] = 1.6, ["step_height"] = 0.6, ["max_slope"] = 50 },
                    ["script"] = new Dictionary<string, object> { ["file"] = "Scripts/coth_wander.luau", ["params"] = new Dictionary<string, object> { ["speed"] = 1.6, ["pause"] = 2.5 } },
                }));
            }
            var scene = new Dictionary<string, object>
            {
                ["noor_scene"] = 2, ["id"] = Uuid($"{name}/scene"), ["name"] = name, ["entities"] = ents,
            };
            files.Add(($"Scenes/{name}.noorscene", Encoding.UTF8.GetBytes(Json.Write(scene))));
            files.Add(($"{name}.noorproj", Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string, object>
                { ["noor_project"] = 1, ["id"] = Uuid($"{name}/project"), ["name"] = name, ["engine"] = "0.0.1", ["created"] = "2026-11-20T00:00:00Z" }))));
            files.Add(("Scripts/NoorLevelPlayer.luau", Encoding.UTF8.GetBytes(PlayerScript)));
            files.Add(("Scripts/coth_wander.luau", Encoding.UTF8.GetBytes(CothScript(route))));
            bool coth = false;
            if (!string.IsNullOrEmpty(cothDir) && Directory.Exists(cothDir))
                foreach (var f in new[] { "coth.png", "coth.png.noormeta", "coth.noorsprite", "coth.noorsprite.noormeta" })
                {
                    var p = Path.Combine(cothDir, f);
                    if (File.Exists(p)) { files.Add(($"Assets/Sprites/{f}", File.ReadAllBytes(p))); coth = true; }
                }
            files.Add(("README.md", Encoding.UTF8.GetBytes(Readme(name, level, coth, route.Count))));
            return files;
        }

        public static byte[] Zip(Level level, string cothDir = null, string folder = null)
        {
            var files = Project(level, cothDir);
            string root = folder ?? files.First(f => f.Path.EndsWith(".noorproj")).Path.Replace(".noorproj", "");
            return Exporters.Zip(files.Select(f => ($"{root}/{f.Path}", f.Data)).ToArray());
        }

        static double FloorUnder(Level level, Vec3 p)
        {
            if (!level.Spans.TryGetValue(level.CellOf(p), out var spans)) return p.Z;
            var best = spans.Where(s => s.Floor <= p.Z + 1).OrderByDescending(s => s.Floor).FirstOrDefault();
            return spans.Count == 0 ? p.Z : (best.Height > 0 ? best.Floor : spans[0].Floor);
        }

        public struct Waypoint { public double X, Y, Z; public bool Stop; }

        /// <summary>Coth's walk: a loop through rooms he can reach on foot (no lifts or ladders: he's a
        /// sprite of habit), as waypoints in engine metres. He starts in a room halfway along, then
        /// visits rooms at random, pausing in each to look around, and comes back round.</summary>
        public static List<Waypoint> CothRoute(Level level, Rng rng, int visits = 14)
        {
            var outp = new List<Waypoint>();
            var rooms = level.Rooms.Where(r => !r.IsConnector && r.Kind != "secret" && r.Kind != "overlook").ToList();
            if (rooms.Count == 0) return outp;
            (int, int, int)? Target(Room r)
            {
                Cell c;
                try { c = Decorate.NearestOpen(level, r, r.X + r.W / 2.0, r.Y + r.H / 2.0, true); }
                catch (InvalidOperationException) { return null; }
                if (level.Diag.ContainsKey(c) || !level.Spans.TryGetValue(c, out var sp)) return null;
                int i = sp.FindIndex(s => s.Floor >= r.Floor - 64 && s.Floor < r.Ceil);
                return i < 0 ? ((int, int, int)?)null : (c.X, c.Y, i);
            }
            var depthOrdered = rooms.OrderBy(r => level.Graph.Nodes[r.Node].Depth).ToList();
            var startRoom = depthOrdered[depthOrdered.Count / 2];
            var cur = Target(startRoom) ?? Target(rooms[0]);
            if (cur == null) return outp;
            var first = cur.Value;
            var recent = new List<int> { startRoom.Id };
            void Emit((int, int, int) node, bool stop)
            {
                var sp = level.Spans[new Cell(node.Item1, node.Item2)][node.Item3];
                outp.Add(new Waypoint { X = node.Item1 + 0.5, Y = Math.Round(sp.Floor * S, 3), Z = -(node.Item2 + 0.5), Stop = stop });
            }
            Emit(first, true);
            for (int v = 0; v <= visits; v++)
            {
                // everything Coth can walk to from here (doors open for him; no links)
                var prev = new Dictionary<(int, int, int), (int, int, int)> { [cur.Value] = cur.Value };
                var q = new Queue<(int, int, int)>(); q.Enqueue(cur.Value);
                while (q.Count > 0)
                {
                    var a = q.Dequeue();
                    foreach (var m in Validator.Successors(level, a, new HashSet<Cell>(), null))
                        if (!prev.ContainsKey(m)) { prev[m] = a; q.Enqueue(m); }
                }
                (int, int, int)? goal;
                if (v == visits) goal = prev.ContainsKey(first) ? first : ((int, int, int)?)null;     // back round to the start
                else
                {
                    var cands = rooms.Where(r => !recent.Contains(r.Id)).Select(r => (r, t: Target(r))).Where(x => x.t != null && prev.ContainsKey(x.t.Value)).ToList();
                    if (cands.Count == 0) break;
                    var pick = rng.Choice(cands);
                    recent.Add(pick.r.Id);
                    if (recent.Count > 4) recent.RemoveAt(0);
                    goal = pick.t;
                }
                if (goal == null) break;
                var path = new List<(int, int, int)>();
                for (var p = goal.Value; !p.Equals(cur.Value); p = prev[p]) path.Add(p);
                path.Reverse();
                // keep the corners: where the direction or the floor changes
                for (int i = 0; i < path.Count; i++)
                {
                    bool last = i == path.Count - 1;
                    if (!last && i > 0)
                    {
                        var a = path[i - 1]; var b = path[i]; var c = path[i + 1];
                        var fa = level.Spans[new Cell(a.Item1, a.Item2)][a.Item3].Floor;
                        var fb = level.Spans[new Cell(b.Item1, b.Item2)][b.Item3].Floor;
                        var fc = level.Spans[new Cell(c.Item1, c.Item2)][c.Item3].Floor;
                        bool straight = b.Item1 - a.Item1 == c.Item1 - b.Item1 && b.Item2 - a.Item2 == c.Item2 - b.Item2 && fa == fb && fb == fc;
                        if (straight) continue;
                    }
                    Emit(path[i], last);
                }
                cur = goal;
            }
            return outp;
        }

        static string F(double v) => v.ToString("0.###", CI);

        static string CothScript(List<Waypoint> route)
        {
            var sb = new StringBuilder();
            sb.Append(@"-- Coth (The Uncounted) is lost in a NoorLevel level. He walks a route through its rooms (worked out by
-- NoorLevel from the level itself: only places he can walk to), stops in each room to look around, and
-- goes on. When the player finds him he stops and looks at them for a moment... then wanders off again.
-- params: speed (m/s), pause (seconds he looks around in a room)
local Coth = {}

-- x, y (floor height), z, stop: the route in metres (generated)
local ROUTE = {
");
            foreach (var w in route) sb.Append($"    {{ {F(w.X)}, {F(w.Y)}, {F(w.Z)}, {(w.Stop ? "true" : "false")} }},\n");
            sb.Append(@"}

function Coth:on_start()
    self.i = 2
    self.wait = 0
    self.stuck = 0
    self.last = self.entity:world_position()
    self.found = 0
    self.yaw = 0
    self.entity:play_animation('walk')
end

local function face(self, dx, dz)
    -- forward is -Z: a yaw of atan2(-dx, -dz) points it along (dx, dz)
    self.yaw = math.deg(math.atan2(-dx, -dz))
    self.entity:set_euler(vector.create(0, self.yaw, 0))
end

function Coth:on_update(dt)
    if #ROUTE < 2 then return end
    local me = self.entity:world_position()
    -- found by the player? stop, look at them, then carry on
    local player = noor.find('Player')
    if player then
        local to = player:world_position() - me
        if vector.magnitude(to) < 2.2 and self.found <= 0 then
            self.found = 3
            self.entity:play_animation('idle')
            self.entity:set('found_by_player', { at = noor.time() })
        end
        if self.found > 0 then
            self.found -= dt
            face(self, to.x, to.z)
            self.entity:character_move(vector.create(0, 0, 0))
            if self.found <= 0 then self.entity:play_animation('walk') end
            return
        end
    end
    -- looking around in a room
    if self.wait > 0 then
        self.wait -= dt
        self.entity:set_euler(vector.create(0, self.yaw + 70 * math.sin(noor.time() * 1.3), 0))
        self.entity:character_move(vector.create(0, 0, 0))
        if self.wait <= 0 then self.entity:play_animation('walk') end
        return
    end
    local w = ROUTE[self.i]
    local d = vector.create(w[1] - me.x, 0, w[3] - me.z)
    local dist = vector.magnitude(d)
    if dist < 0.35 then
        if w[4] then
            self.wait = self.params.pause or 2.5
            self.entity:play_animation('idle')
        end
        self.i = self.i % #ROUTE + 1
        return
    end
    local v = vector.normalize(d) * (self.params.speed or 1.6)
    face(self, d.x, d.z)
    self.entity:character_move(v)
    -- stuck on something (another character, a ledge)? after a few seconds, try the next point
    if vector.magnitude(me - self.last) < 0.02 then
        self.stuck += dt
        if self.stuck > 3 then
            self.stuck = 0
            self.i = self.i % #ROUTE + 1
        end
    else
        self.stuck = 0
    end
    self.last = me
end

return Coth
");
            return sb.ToString();
        }

        const string PlayerScript = @"-- The player in a NoorLevel level: walk (WASD), sprint (Shift), jump (Space), look (mouse; click the game
-- view to capture it, Esc releases). Made from NoorEngine's FpsPlayer sample, without the shooting.
-- The HUD says how far Coth is, and what happens when you find him or the exit.
local Player = {}

function Player:on_start()
    self.yaw = self.entity:euler().y
    self.pitch = 0
    self.camera = self.entity:child('Camera')
    self.found = 0
    self.exit = false
    noor.input.capture_mouse(true)
end

function Player:on_update(dt)
    local p = self.params
    if noor.input.mouse_captured() then
        local look = p.look_sensitivity or 0.12
        self.yaw -= noor.input.axis('look_x') * look
        self.pitch = math.clamp(self.pitch - noor.input.axis('look_y') * look, -85, 85)
    end
    self.entity:set_euler(vector.create(0, self.yaw, 0))
    if self.camera then self.camera:set_euler(vector.create(self.pitch, 0, 0)) end

    local move = self.entity:forward() * noor.input.axis('move_y') + self.entity:right() * noor.input.axis('move_x')
    if vector.magnitude(move) > 1 then move = vector.normalize(move) end
    local speed = (p.speed or 5) * (if noor.input.down('sprint') then (p.sprint or 1.7) else 1)
    self.entity:character_move(move * speed)
    if noor.input.pressed('jump') then self.entity:character_jump(p.jump_speed or 5) end

    -- the HUD
    noor.ui.rect(0, 0, 4, 4, { color = '#FFFFFFA0', anchor = 'center' })
    local me = self.entity:world_position()
    local coth = noor.find('Coth')
    if coth then
        local d = vector.magnitude(coth:world_position() - me)
        if d < 2.2 then self.found = 4 end
        if self.found > 0 then
            self.found -= dt
            noor.ui.text('You found Coth! He was lost.\nHe is still lost.', 0, -140, { size = 32, anchor = 'center', color = '#FFD27A' })
        else
            noor.ui.text(string.format('Coth is %d m away', math.floor(d)), 20, 20, { size = 26 })
        end
    end
    local exit = noor.find('Exit')
    if exit and vector.magnitude(exit:world_position() - me) < 1.6 then self.exit = true end
    if self.exit then noor.ui.text('Exit reached!', -20, 20, { size = 26, anchor = 'top_right', color = '#7BE495' }) end
    noor.ui.text('WASD walk - mouse look - Shift sprint - Space jump - Esc frees the mouse', 0, -20, { size = 18, anchor = 'bottom', color = '#FFFFFFA0' })
end

return Player
";

        static string Readme(string name, Level level, bool coth, int waypoints) => $@"# {name}

A level made by **NoorLevel** (recipe `{level.Config.Recipe}`, seed {level.Seed}) for **NoorEngine**.

Open it: NoorEditor > Open project > this folder, then `Scenes/{name}.noorscene`, and press **Play**.
Click into the game view to look around with the mouse.

- `Assets/Levels/{name}.glb` + `.noor.json` + `.recipe.json`: the level as a Noor Interchange asset
  (kind `level`, generator NoorLevel). Every room, corridor and architectural element is its own mesh
  (named like `room:5/vault`), placed in the scene as its own entity with a static mesh collider, so
  you can select one and swap it for real art.
- Lights: each of the level's lamps and torches is a point light.
- **Player**: a character controller (1.7 m, steps up to 0.6 m) with `Scripts/NoorLevelPlayer.luau`
  (walk, sprint, jump, look) and a HUD that tells you how far Coth is.
- **Coth** (The Uncounted): a character with his 8-direction sprite and `Scripts/coth_wander.luau`,
  walking a route of {waypoints} points through the rooms that NoorLevel worked out from the level. He
  stops in each room to look around. Find him!
{(coth ? "" : @"
**Coth's sprite isn't included in this folder.** Copy `coth.png`, `coth.png.noormeta`, `coth.noorsprite`
and `coth.noorsprite.noormeta` from NoorEngine's `samples/Hillside/Assets/Sprites/` into
`Assets/Sprites/` (the scene already points at his sheet's id).
")}
Scale: one NoorLevel cell is one metre (Quake's 32 units); NoorLevel's player is 1.75 m, stairs rise 0.5 m.
Doors, lifts and keys are left open in this first export: walk anywhere.
";
    }
}
