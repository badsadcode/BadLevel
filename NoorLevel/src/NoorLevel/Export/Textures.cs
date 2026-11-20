using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NoorLevel.Export
{
    /// <summary>The blockout textures, made here: grey grids with a line every 32 units (one cell).
    /// As a Quake WAD2 (Quake 1), Quake 2 .wal files, and a Half-Life WAD3 (own palette per texture).
    /// Indices 0-15 of the Quake and Quake 2 palettes are a grey ramp, so greys look the same in both.</summary>
    public static class Textures
    {
        public const string WadName = "noorlevel.wad";
        public const string Wad3Name = "noorlevel_hl.wad";

        // name -> (w, h, base grey, line grey, pattern)
        public static readonly (string Name, int W, int H, int Base, int Line, string Pattern)[] All =
        {
            ("nl_floor", 64, 64, 11, 7, "grid"),
            ("nl_wall", 64, 64, 9, 6, "grid"),
            ("nl_ceiling", 64, 64, 5, 3, "grid"),
            ("nl_door", 64, 64, 12, 4, "door"),
            ("nl_glass", 64, 64, 13, 10, "grid"),
            ("trigger", 64, 64, 14, 12, "grid"),
            ("nl_lift", 64, 64, 12, 5, "door"),
            ("nl_ladder", 64, 64, 7, 13, "grid"),
            ("sky1", 256, 128, 12, 10, "sky"),
            // liquids: a '*' name makes qbsp a liquid volume. Quake 1's palette: 48-63 greens, 240+ fullbright, 208-223 blues
            ("*slime0", 64, 64, 54, 50, "grid"),
            ("*lava1", 64, 64, 247, 244, "grid"),
            ("*water0", 64, 64, 214, 210, "grid"),
        };

        public static readonly Dictionary<string, string> Q2Names = new Dictionary<string, string>
        {
            ["nl_floor"] = "floor", ["nl_wall"] = "wall", ["nl_ceiling"] = "ceiling", ["nl_door"] = "door", ["nl_glass"] = "glass",
            ["trigger"] = "trigger", ["nl_lift"] = "lift", ["nl_ladder"] = "ladder", ["sky1"] = "sky",
            ["*slime0"] = "slime", ["*lava1"] = "lava", ["*water0"] = "water",
        };

        static byte[] Pixels(int w, int h, int b, int l, string pattern)
        {
            var px = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int v;
                    if (pattern == "sky") v = x < w / 2 ? 0 : ((x / 16 + y / 16) % 2 == 1 ? b : l);
                    else if (pattern == "door")
                    {
                        bool edge = x <= 1 || x >= w - 2 || y <= 1 || y >= h - 2;
                        bool cross = Math.Abs(x - y) <= 1 || Math.Abs(x - (h - 1 - y)) <= 1;
                        v = edge || cross ? l : b;
                    }
                    else v = x % 32 == 0 || y % 32 == 0 ? l : b;
                    px[y * w + x] = (byte)v;
                }
            return px;
        }

        static byte[] Mip(byte[] px, int w, int h, int f)
        {
            var o = new byte[(w / f) * (h / f)]; int i = 0;
            for (int y = 0; y < h; y += f) for (int x = 0; x < w; x += f) o[i++] = px[y * w + x];
            return o;
        }

        static void Name(BinaryWriter bw, string name, int len)
        {
            var b = new byte[len];
            var src = Encoding.ASCII.GetBytes(name);
            Array.Copy(src, b, Math.Min(src.Length, len - 1));
            bw.Write(b);
        }

        static byte[] Miptex((string Name, int W, int H, int Base, int Line, string Pattern) t, bool hl)
        {
            var px = Pixels(t.W, t.H, t.Base, t.Line, t.Pattern);
            var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
            Name(bw, t.Name, 16); bw.Write(t.W); bw.Write(t.H);
            int pos = 40;
            var mips = new List<byte[]>();
            foreach (var f in new[] { 1, 2, 4, 8 }) { var m = Mip(px, t.W, t.H, f); bw.Write(pos); pos += m.Length; mips.Add(m); }
            foreach (var m in mips) bw.Write(m);
            if (hl)
            {
                bw.Write((short)256);
                for (int i = 0; i < 256; i++)
                {
                    byte g = (byte)Math.Min(255, i * 17);            // the same grey ramp as Quake's first 16
                    if (t.Pattern == "sky" && i == 0) { bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)255); continue; }
                    bw.Write(g); bw.Write(g); bw.Write(g);
                }
                bw.Write((short)0);
            }
            return ms.ToArray();
        }

        static byte[] Wad(bool hl)
        {
            var lumps = new List<(string, byte[])>();
            foreach (var t in All) lumps.Add((t.Name, Miptex(t, hl)));
            var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
            bw.Write(Encoding.ASCII.GetBytes(hl ? "WAD3" : "WAD2")); bw.Write(lumps.Count);
            int dirPos = 12 + 0; foreach (var (_, l) in lumps) dirPos += l.Length;
            bw.Write(dirPos);
            int pos = 12;
            var entries = new List<(int, int, string)>();
            foreach (var (n, l) in lumps) { bw.Write(l); entries.Add((pos, l.Length, n)); pos += l.Length; }
            foreach (var (p, len, n) in entries)
            {
                bw.Write(p); bw.Write(len); bw.Write(len); bw.Write((byte)(hl ? 0x43 : 0x44)); bw.Write((byte)0); bw.Write((short)0);
                Name(bw, n, 16);
            }
            return ms.ToArray();
        }

        public static byte[] MakeWad() => Wad(false);
        public static byte[] MakeWad3() => Wad(true);

        /// <summary>One Quake 2 .wal: 100-byte header (name, size, 4 mip offsets, animname, flags, contents, value) + mips.</summary>
        public static byte[] MakeWal(string name)
        {
            foreach (var t in All)
            {
                if (t.Name != name) continue;
                int b0 = t.Base, l0 = t.Line;
                if (name.StartsWith("*")) (b0, l0) = name.Contains("slime") ? (4, 2) : name.Contains("water") ? (7, 5) : (14, 12);   // Quake 2's palette differs: stay grey
                var px = Pixels(t.W, t.H, b0, l0, t.Pattern);
                var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
                Name(bw, "noorlevel/" + Q2Names[name], 32); bw.Write(t.W); bw.Write(t.H);
                int pos = 100; var mips = new List<byte[]>();
                foreach (var f in new[] { 1, 2, 4, 8 }) { var m = Mip(px, t.W, t.H, f); bw.Write(pos); pos += m.Length; mips.Add(m); }
                Name(bw, "", 32); bw.Write(0); bw.Write(0); bw.Write(0);
                foreach (var m in mips) bw.Write(m);
                return ms.ToArray();
            }
            throw new ArgumentException(name);
        }

        /// <summary>textures/noorlevel/*.wal as (relative path, bytes).</summary>
        public static IEnumerable<(string Path, byte[] Data)> Wals()
        {
            foreach (var kv in Q2Names) yield return ($"textures/noorlevel/{kv.Value}.wal", MakeWal(kv.Key));
        }
    }
}
