using System;
using System.Collections.Generic;

namespace NoorLevel
{
    /// <summary>A small, fast, deterministic random generator (xoshiro256**), the same on every
    /// platform and runtime, with the helpers the generator uses. <see cref="Derive"/> makes an
    /// independent stream from a label, so one feature's randomness never shifts another's.</summary>
    public sealed class Rng
    {
        ulong s0, s1, s2, s3;

        public Rng(ulong seed)
        {
            ulong x = seed;
            s0 = SplitMix(ref x); s1 = SplitMix(ref x); s2 = SplitMix(ref x); s3 = SplitMix(ref x);
        }

        public static Rng FromLabel(string label) => new Rng(Hash(label));

        public Rng Derive(string label) => new Rng(Hash(label) ^ NextULong());

        static ulong SplitMix(ref ulong x)
        {
            ulong z = (x += 0x9E3779B97F4A7C15UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>FNV-1a, 64 bit: a stable hash of a label (string.GetHashCode is per-process).</summary>
        public static ulong Hash(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
            return h;
        }

        static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

        public ulong NextULong()
        {
            ulong result = Rotl(s1 * 5, 7) * 9;
            ulong t = s1 << 17;
            s2 ^= s0; s3 ^= s1; s1 ^= s2; s0 ^= s3; s2 ^= t; s3 = Rotl(s3, 45);
            return result;
        }

        /// <summary>Uniform in [0, 1).</summary>
        public double Random() => (NextULong() >> 11) * (1.0 / (1UL << 53));

        /// <summary>Integer in [lo, hi] inclusive (like Python's randint).</summary>
        public int Int(int lo, int hi)
        {
            if (hi < lo) return lo;
            ulong span = (ulong)(hi - lo) + 1;
            return lo + (int)(NextULong() % span);
        }

        /// <summary>Integer in [lo, hi) stepping by step (like Python's randrange).</summary>
        public int Range(int lo, int hiExclusive, int step = 1)
        {
            int n = (hiExclusive - lo + step - 1) / step;
            if (n <= 0) return lo;
            return lo + step * Int(0, n - 1);
        }

        public double Uniform(double lo, double hi) => lo + (hi - lo) * Random();

        public T Choice<T>(IList<T> items) => items[Int(0, items.Count - 1)];

        public void Shuffle<T>(IList<T> items)
        {
            for (int i = items.Count - 1; i > 0; i--)
            {
                int j = Int(0, i);
                (items[i], items[j]) = (items[j], items[i]);
            }
        }

        /// <summary>Pick from options by weight (missing weights count 1; all zero: uniform).</summary>
        public T Weighted<T>(IList<T> options, IDictionary<T, double> weights)
        {
            double total = 0;
            var ws = new double[options.Count];
            for (int i = 0; i < options.Count; i++)
            {
                double w = 1.0;
                if (weights != null && weights.TryGetValue(options[i], out var v)) w = v;
                ws[i] = Math.Max(0, w);
                total += ws[i];
            }
            if (total <= 0) return Choice(options);
            double r = Random() * total;
            for (int i = 0; i < options.Count; i++)
            {
                r -= ws[i];
                if (r < 0) return options[i];
            }
            return options[options.Count - 1];
        }
    
        /// <summary>Python's rng.choices(options, weights)[0].</summary>
        public T Choices<T>(IList<T> options, IList<double> weights)
        {
            double total = 0;
            foreach (var w in weights) total += Math.Max(0, w);
            if (total <= 0) return Choice(options);
            double r = Random() * total;
            for (int i = 0; i < options.Count; i++)
            {
                r -= Math.Max(0, weights[i]);
                if (r < 0) return options[i];
            }
            return options[options.Count - 1];
        }

        /// <summary>Python's rng.sample(items, k): k distinct items in random order.</summary>
        public List<T> Sample<T>(IList<T> items, int k)
        {
            var pool = new List<T>(items);
            Shuffle(pool);
            return pool.GetRange(0, Math.Min(k, pool.Count));
        }

        public static Rng FromSeed(int seed) => new Rng(Hash("seed:" + seed));

        /// <summary>weights.weighted: pick from options (duplicates add base weight) scaled by bias[key] (default 1).</summary>
        public T WeightedBias<T, K>(IList<T> options, IDictionary<K, double> bias, Func<T, K> key)
        {
            var uniq = new List<T>(); var ws = new List<double>(); var seen = new Dictionary<K, int>();
            foreach (var o in options)
            {
                var k = key(o);
                if (seen.TryGetValue(k, out var i)) { ws[i] += 1; continue; }
                seen[k] = uniq.Count; uniq.Add(o); ws.Add(1);
            }
            for (int i = 0; i < uniq.Count; i++)
                if (bias != null && bias.TryGetValue(key(uniq[i]), out var b)) ws[i] *= b;
            double total = 0; foreach (var w in ws) total += w;
            if (total <= 0) return Choice(options);
            return Choices(uniq, ws);
        }

        public T WeightedBias<T>(IList<T> options, IDictionary<T, double> bias) => WeightedBias(options, bias, o => o);
}
}
