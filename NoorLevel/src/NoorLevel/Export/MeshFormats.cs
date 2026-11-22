using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace NoorLevel.Export
{
    /// <summary>Binary glTF 2.0 (.glb), no dependencies. Y-up, metres: one cell (32 map units) = 1 m.
    /// One mesh "level_geometry" with a primitive per material; every entity is an empty node named
    /// kind_n whose extras carry its properties, for engines to swap for their own prefabs. The station
    /// markers (<see cref="Markers.Kinds"/>: props, loot, spawns...) also carry a yaw rotation (their forward
    /// is the node's +Z) and a Noor Interchange block, extras.noor; other nodes are as they always were.</summary>
    public static class Gltf
    {
        public const double Scale = 1.0 / 32.0;

        public static byte[] ToGlb(Level level, bool entities = true) => ToGlb(level, entities, out _);

        /// <summary>The .glb, and the name of each mesh in it (mesh i is part names[i]: a room, a corridor, an element).</summary>
        public static byte[] ToGlb(Level level, bool entities, out List<string> meshNames)
        {
            meshNames = new List<string>();
            var mb = LevelMesh.Build(level, entities);
            var bin = new MemoryStream(); var bw = new BinaryWriter(bin);
            var views = new List<object>(); var accessors = new List<object>(); var mats = new List<object>();
            var matIndex = new Dictionary<string, int>();
            var meshes = new List<object>();
            var partNodes = new List<object>();
            void Align() { while (bin.Length % 4 != 0) bw.Write((byte)0); }
            int View(int offset, int length, int? target)
            {
                var v = new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = length };
                if (target != null) v["target"] = target.Value;
                views.Add(v); return views.Count - 1;
            }
            int Mat(string name)
            {
                if (matIndex.TryGetValue(name, out var i)) return i;
                var col = LevelMesh.ColorOf(name);
                var mat = new Dictionary<string, object>
                {
                    ["name"] = "nl_" + name,
                    ["pbrMetallicRoughness"] = new Dictionary<string, object> { ["baseColorFactor"] = col, ["metallicFactor"] = 0.0, ["roughnessFactor"] = 0.9 },
                };
                if (col[3] < 1) { mat["alphaMode"] = "BLEND"; mat["doubleSided"] = true; }
                mats.Add(mat);
                return matIndex[name] = mats.Count - 1;
            }
            // one mesh per named part (a room, a corridor, an element like "room:5/vault"), a primitive per material
            foreach (var part in mb.Parts.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var prims = new List<object>();
                foreach (var g in part.Value.Values.Where(g => g.Idx.Count > 0).OrderBy(g => g.Material, StringComparer.Ordinal))
                {
                    int n = g.VertexCount;
                    float[] mn = { float.MaxValue, float.MaxValue, float.MaxValue }, mx = { float.MinValue, float.MinValue, float.MinValue };
                    Align(); int off = (int)bin.Length;
                    for (int i = 0; i < n; i++)
                    {
                        float x = (float)(g.Pos[3 * i] * Scale), y = (float)(g.Pos[3 * i + 2] * Scale), z = (float)(-g.Pos[3 * i + 1] * Scale);
                        bw.Write(x); bw.Write(y); bw.Write(z);
                        mn[0] = Math.Min(mn[0], x); mn[1] = Math.Min(mn[1], y); mn[2] = Math.Min(mn[2], z);
                        mx[0] = Math.Max(mx[0], x); mx[1] = Math.Max(mx[1], y); mx[2] = Math.Max(mx[2], z);
                    }
                    accessors.Add(new Dictionary<string, object> { ["bufferView"] = View(off, n * 12, 34962), ["componentType"] = 5126, ["count"] = n, ["type"] = "VEC3", ["min"] = mn, ["max"] = mx });
                    int pa = accessors.Count - 1;
                    off = (int)bin.Length;
                    for (int i = 0; i < n; i++) { bw.Write(g.Nrm[3 * i]); bw.Write(g.Nrm[3 * i + 2]); bw.Write(-g.Nrm[3 * i + 1]); }
                    accessors.Add(new Dictionary<string, object> { ["bufferView"] = View(off, n * 12, 34962), ["componentType"] = 5126, ["count"] = n, ["type"] = "VEC3" });
                    int na = accessors.Count - 1;
                    off = (int)bin.Length;
                    for (int i = 0; i < n; i++) { bw.Write(g.Uv[2 * i]); bw.Write(-g.Uv[2 * i + 1]); }
                    accessors.Add(new Dictionary<string, object> { ["bufferView"] = View(off, n * 8, 34962), ["componentType"] = 5126, ["count"] = n, ["type"] = "VEC2" });
                    int ua = accessors.Count - 1;
                    off = (int)bin.Length;
                    foreach (var ix in g.Idx) bw.Write((uint)ix);
                    accessors.Add(new Dictionary<string, object> { ["bufferView"] = View(off, g.Idx.Count * 4, 34963), ["componentType"] = 5125, ["count"] = g.Idx.Count, ["type"] = "SCALAR" });
                    int ia = accessors.Count - 1;
                    prims.Add(new Dictionary<string, object>
                    {
                        ["attributes"] = new Dictionary<string, object> { ["POSITION"] = pa, ["NORMAL"] = na, ["TEXCOORD_0"] = ua },
                        ["indices"] = ia, ["material"] = Mat(g.Material),
                    });
                }
                if (prims.Count == 0) continue;
                meshes.Add(new Dictionary<string, object> { ["name"] = part.Key, ["primitives"] = prims });
                meshNames.Add(part.Key);
                partNodes.Add(new Dictionary<string, object> { ["name"] = part.Key, ["mesh"] = meshes.Count - 1, ["extras"] = new Dictionary<string, object> { ["part"] = part.Key } });
            }
            Align();
            // the level: a root node "level_geometry" whose children are the parts
            var nodes = new List<object> { new Dictionary<string, object> { ["name"] = "level_geometry", ["children"] = Enumerable.Range(1, partNodes.Count).ToArray() } };
            nodes.AddRange(partNodes);
            if (entities)
            {
                var counts = new Dictionary<string, int>();
                foreach (var e in level.Entities)
                {
                    counts[e.Kind] = counts.TryGetValue(e.Kind, out var k) ? k + 1 : 1;
                    var extras = new Dictionary<string, object> { ["kind"] = e.Kind };
                    foreach (var kv in e.Props) extras[kv.Key] = kv.Value;
                    string name = $"{e.Kind}_{counts[e.Kind]}";
                    var node = new Dictionary<string, object>
                    {
                        ["name"] = name,
                        ["translation"] = new[] { e.Pos.X * Scale, e.Pos.Z * Scale, -e.Pos.Y * Scale },
                        ["extras"] = extras,
                    };
                    if (Markers.IsMarker(e.Kind))
                    {
                        if (e.Props.TryGetValue("angle", out var ang)) node["rotation"] = Markers.Rotation(Convert.ToInt32(ang));
                        extras["noor"] = Markers.NoorBlock(e, name);
                    }
                    nodes.Add(node);
                }
            }
            var root = new Dictionary<string, object>
            {
                ["asset"] = new Dictionary<string, object> { ["version"] = "2.0", ["generator"] = "NoorLevel" },
                ["scene"] = 0,
                ["scenes"] = new[] { new Dictionary<string, object> { ["name"] = $"noorlevel_{level.Seed}", ["nodes"] = new[] { 0 }.Concat(Enumerable.Range(1 + partNodes.Count, nodes.Count - 1 - partNodes.Count)).ToArray() } },
                ["nodes"] = nodes,
                ["meshes"] = meshes,
                ["materials"] = mats,
                ["accessors"] = accessors,
                ["bufferViews"] = views,
                ["buffers"] = new[] { new Dictionary<string, object> { ["byteLength"] = bin.Length } },
            };
            var json = Encoding.UTF8.GetBytes(Json.Write(root));
            int jlen = (json.Length + 3) / 4 * 4;
            var outp = new MemoryStream(); var ow = new BinaryWriter(outp);
            ow.Write(0x46546C67u); ow.Write(2u); ow.Write((uint)(12 + 8 + jlen + 8 + bin.Length));
            ow.Write((uint)jlen); ow.Write(0x4E4F534Au); ow.Write(json); for (int i = json.Length; i < jlen; i++) ow.Write((byte)' ');
            ow.Write((uint)bin.Length); ow.Write(0x004E4942u); ow.Write(bin.ToArray());
            return outp.ToArray();
        }
    }

    /// <summary>Wavefront .obj + .mtl (Y-up metres like the .glb), one group per material.</summary>
    public static class Obj
    {
        public static (string Obj, string Mtl) ToObj(Level level, string mtlName = "level.mtl")
        {
            var mb = LevelMesh.Build(level, true);
            var o = new StringBuilder(); var m = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            o.Append($"# NoorLevel seed {level.Seed}\nmtllib {mtlName}\n");
            int vbase = 1;
            foreach (var g in mb.Groups.Values.OrderBy(g => g.Material, StringComparer.Ordinal))
            {
                int n = g.VertexCount;
                o.Append($"o {g.Material}\nusemtl nl_{g.Material}\n");
                for (int i = 0; i < n; i++)
                    o.Append(string.Format(ci, "v {0:0.####} {1:0.####} {2:0.####}\n", g.Pos[3 * i] * Gltf.Scale, g.Pos[3 * i + 2] * Gltf.Scale, -g.Pos[3 * i + 1] * Gltf.Scale));
                for (int i = 0; i < n; i++) o.Append(string.Format(ci, "vt {0:0.####} {1:0.####}\n", g.Uv[2 * i], g.Uv[2 * i + 1]));
                for (int i = 0; i < n; i++) o.Append(string.Format(ci, "vn {0:0.###} {1:0.###} {2:0.###}\n", g.Nrm[3 * i], g.Nrm[3 * i + 2], -g.Nrm[3 * i + 1]));
                for (int i = 0; i < g.Idx.Count; i += 3)
                {
                    int a = g.Idx[i] + vbase, b = g.Idx[i + 1] + vbase, c = g.Idx[i + 2] + vbase;
                    o.Append($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}\n");
                }
                vbase += n;
                var col = LevelMesh.ColorOf(g.Material);
                m.Append(string.Format(ci, "newmtl nl_{0}\nKd {1:0.###} {2:0.###} {3:0.###}\nd {4:0.##}\n\n", g.Material, col[0], col[1], col[2], col[3]));
            }
            return (o.ToString(), m.ToString());
        }
    }

    /// <summary>Binary STL (for 3D printing a level, or any mesh tool). Z-up, map units scaled to millimetres (1 unit = 0.5 mm).</summary>
    public static class Stl
    {
        public static byte[] ToStl(Level level)
        {
            var mb = LevelMesh.Build(level, false);
            var tris = mb.Groups.Values.Sum(g => g.Idx.Count / 3);
            var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
            var head = new byte[80]; var h = Encoding.ASCII.GetBytes($"NoorLevel seed {level.Seed}"); Array.Copy(h, head, Math.Min(80, h.Length));
            bw.Write(head); bw.Write((uint)tris);
            foreach (var g in mb.Groups.Values)
                for (int i = 0; i < g.Idx.Count; i += 3)
                {
                    int a = g.Idx[i];
                    bw.Write(g.Nrm[3 * a]); bw.Write(g.Nrm[3 * a + 1]); bw.Write(g.Nrm[3 * a + 2]);
                    for (int k = 0; k < 3; k++) { int v = g.Idx[i + k]; bw.Write(g.Pos[3 * v] * 0.5f); bw.Write(g.Pos[3 * v + 1] * 0.5f); bw.Write(g.Pos[3 * v + 2] * 0.5f); }
                    bw.Write((ushort)0);
                }
            return ms.ToArray();
        }
    }
}
