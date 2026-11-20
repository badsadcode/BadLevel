using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NoorLevel.Export
{
    /// <summary>A tiny JSON writer and reader (no dependencies, so the DLL runs on netstandard2.1).</summary>
    public static class Json
    {
        public static string Write(object o) { var sb = new StringBuilder(); W(sb, o); return sb.ToString(); }

        static void W(StringBuilder sb, object o)
        {
            switch (o)
            {
                case null: sb.Append("null"); break;
                case string s: Str(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case float f: sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "0" : f.ToString("R", CultureInfo.InvariantCulture)); break;   // exact (glTF accessor min/max must match the data)
                case double d: sb.Append(Num(d)); break;
                case int or long or short or byte or uint or ulong: sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture)); break;
                case Vec3 v: sb.Append('[').Append(Num(v.X)).Append(',').Append(Num(v.Y)).Append(',').Append(Num(v.Z)).Append(']'); break;
                case IDictionary dict:
                    sb.Append('{'); bool first = true;
                    foreach (DictionaryEntry kv in dict)
                    {
                        if (!first) sb.Append(','); first = false;
                        Str(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture)); sb.Append(':'); W(sb, kv.Value);
                    }
                    sb.Append('}'); break;
                case IEnumerable list:
                    sb.Append('['); bool f1 = true;
                    foreach (var x in list) { if (!f1) sb.Append(','); f1 = false; W(sb, x); }
                    sb.Append(']'); break;
                default: Str(sb, o.ToString()); break;
            }
        }

        static string Num(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
            if (Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < 1e15) return ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture);
            return d.ToString("0.####", CultureInfo.InvariantCulture);
        }

        static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4")); else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>Parse JSON into Dictionary&lt;string, object&gt; / List&lt;object&gt; / string / double / bool / null.</summary>
        public static object Parse(string text) { int i = 0; var v = P(text, ref i); return v; }

        static void Ws(string t, ref int i) { while (i < t.Length && char.IsWhiteSpace(t[i])) i++; }

        static object P(string t, ref int i)
        {
            Ws(t, ref i);
            if (i >= t.Length) throw new FormatException("unexpected end of JSON");
            char c = t[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>(); i++; Ws(t, ref i);
                if (t[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(t, ref i); var k = (string)P(t, ref i); Ws(t, ref i);
                    if (t[i] != ':') throw new FormatException("expected :"); i++;
                    d[k] = P(t, ref i); Ws(t, ref i);
                    if (t[i] == ',') { i++; continue; }
                    if (t[i] == '}') { i++; return d; }
                    throw new FormatException("expected , or }");
                }
            }
            if (c == '[')
            {
                var l = new List<object>(); i++; Ws(t, ref i);
                if (t[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(P(t, ref i)); Ws(t, ref i);
                    if (t[i] == ',') { i++; continue; }
                    if (t[i] == ']') { i++; return l; }
                    throw new FormatException("expected , or ]");
                }
            }
            if (c == '"')
            {
                var sb = new StringBuilder(); i++;
                while (t[i] != '"')
                {
                    if (t[i] == '\\')
                    {
                        i++;
                        switch (t[i])
                        {
                            case 'n': sb.Append('\n'); break;
                            case 't': sb.Append('\t'); break;
                            case 'r': sb.Append('\r'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'u': sb.Append((char)Convert.ToInt32(t.Substring(i + 1, 4), 16)); i += 4; break;
                            default: sb.Append(t[i]); break;
                        }
                        i++;
                    }
                    else sb.Append(t[i++]);
                }
                i++; return sb.ToString();
            }
            if (t.Length - i >= 4 && string.CompareOrdinal(t, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (t.Length - i >= 5 && string.CompareOrdinal(t, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (t.Length - i >= 4 && string.CompareOrdinal(t, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st = i;
            while (i < t.Length && "+-0123456789.eE".IndexOf(t[i]) >= 0) i++;
            if (st == i) throw new FormatException($"bad JSON at {i}");
            return double.Parse(t.Substring(st, i - st), CultureInfo.InvariantCulture);
        }
    }
}
