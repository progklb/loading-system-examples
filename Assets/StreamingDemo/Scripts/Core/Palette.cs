using UnityEngine;

namespace StreamingDemo
{
    /// <summary>
    /// One colour language for every mode, so the audience learns it once:
    ///   grey = not resident · yellow = queued · orange = loading ·
    ///   green = resident · red = evicting · blue = cached · purple = generating.
    /// </summary>
    public static class Palette
    {
        public static readonly Color Unloaded   = Hex("#3a4560");
        public static readonly Color Queued     = Hex("#ffd23f");
        public static readonly Color Loading    = Hex("#ff8c42");
        public static readonly Color Loaded     = Hex("#3ddc97");
        public static readonly Color Unloading  = Hex("#ff4f6d");
        public static readonly Color Cached     = Hex("#4ea8ff");
        public static readonly Color Generating = Hex("#b57bff");
        public static readonly Color Lod        = Hex("#8a94a8");

        public static readonly Color Player     = Hex("#5ef1ff");
        public static readonly Color Accent     = Hex("#5ef1ff");
        public static readonly Color Warning    = Hex("#ff4f6d");

        public static readonly Color Background = Hex("#070a12");
        public static readonly Color Panel      = Hex("#0d1322");
        public static readonly Color PanelEdge  = Hex("#24314d");
        public static readonly Color Text       = Hex("#e6ecf7");
        public static readonly Color TextDim    = Hex("#8b98b3");

        public static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            return c;
        }

        public static Color WithAlpha(this Color c, float a) { c.a = a; return c; }

        /// <summary>Vertex colour with "glow" stored in alpha (see VertexLit.shader).</summary>
        public static Color Glow(this Color c, float glow) { c.a = glow; return c; }

        public static Color Mul(this Color c, float k) => new Color(c.r * k, c.g * k, c.b * k, c.a);

        public static Color Jitter(this Color c, System.Random rng, float amount)
        {
            float k = 1f + ((float)rng.NextDouble() * 2f - 1f) * amount;
            return new Color(c.r * k, c.g * k, c.b * k, c.a);
        }
    }
}
