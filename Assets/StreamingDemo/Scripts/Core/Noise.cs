using UnityEngine;

namespace StreamingDemo
{
    /// <summary>
    /// Deterministic noise + hashing. Determinism matters for streaming:
    /// a chunk generated twice from the same seed is identical, which is what
    /// lets procedural games (Minecraft, No Man's Sky) avoid storing most of
    /// their world at all.
    /// </summary>
    public static class Noise
    {
        /// <summary>Fractal (fBm) Perlin noise in roughly [0,1].</summary>
        public static float Fbm(float x, float z, int octaves = 4, float lacunarity = 2f, float gain = 0.5f, float seed = 0f)
        {
            float sum = 0, amp = 1, norm = 0, freq = 1;
            for (int i = 0; i < octaves; i++)
            {
                sum += Mathf.PerlinNoise(x * freq + seed + i * 17.13f, z * freq - seed + i * 31.7f) * amp;
                norm += amp;
                amp *= gain;
                freq *= lacunarity;
            }
            return sum / norm;
        }

        /// <summary>Ridged noise: sharp mountain crests.</summary>
        public static float Ridged(float x, float z, int octaves = 4, float seed = 0f)
        {
            float sum = 0, amp = 0.5f, freq = 1, norm = 0;
            for (int i = 0; i < octaves; i++)
            {
                float n = Mathf.PerlinNoise(x * freq + seed + i * 7.7f, z * freq + seed * 0.5f - i * 3.3f);
                n = 1f - Mathf.Abs(n * 2f - 1f);
                sum += n * n * amp;
                norm += amp;
                amp *= 0.5f;
                freq *= 2.03f;
            }
            return sum / norm;
        }

        /// <summary>Integer hash -> stable seed.</summary>
        public static int Hash(int x, int y, int seed = 0)
        {
            unchecked
            {
                int h = seed * 374761393 + x * 668265263 + y * 2147483647;
                h = (h ^ (h >> 13)) * 1274126177;
                return h ^ (h >> 16);
            }
        }

        public static float Hash01(int x, int y, int seed = 0) => (Hash(x, y, seed) & 0xFFFFFF) / (float)0x1000000;

        public static System.Random Rng(int x, int y, int seed = 0) => new System.Random(Hash(x, y, seed));

        public static float Range(this System.Random r, float min, float max) => min + (float)r.NextDouble() * (max - min);
        public static bool Chance(this System.Random r, float p) => r.NextDouble() < p;
    }
}
