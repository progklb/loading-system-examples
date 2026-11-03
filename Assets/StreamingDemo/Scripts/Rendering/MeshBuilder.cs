using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StreamingDemo
{
    /// <summary>
    /// Tiny flat-shaded mesh builder. All world content in the demo (buildings,
    /// trees, terrain, rooms...) is generated with this, merged into one mesh per
    /// streamable unit. Merging is itself a real streaming technique: one unit of
    /// streaming = one buffer upload = one draw call.
    ///
    /// Vertex colour alpha is a glow mask (see VertexLit.shader).
    /// </summary>
    public sealed class MeshBuilder
    {
        readonly List<Vector3> _v = new List<Vector3>(1024);
        readonly List<Vector3> _n = new List<Vector3>(1024);
        readonly List<Color> _c = new List<Color>(1024);
        readonly List<int> _t = new List<int>(2048);

        public int VertexCount => _v.Count;

        public void Clear()
        {
            _v.Clear(); _n.Clear(); _c.Clear(); _t.Clear();
        }

        /// <summary>Triangle. Winding is fixed automatically so the face points along <paramref name="outward"/>.</summary>
        public void Tri(Vector3 a, Vector3 b, Vector3 c, Color col, Vector3 outward)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (Vector3.Dot(n, outward) < 0) { var tmp = b; b = c; c = tmp; n = -n; }
            n.Normalize();
            int i = _v.Count;
            _v.Add(a); _v.Add(b); _v.Add(c);
            _n.Add(n); _n.Add(n); _n.Add(n);
            _c.Add(col); _c.Add(col); _c.Add(col);
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
        }

        /// <summary>Quad from 4 corners in cyclic order. Winding fixed to face <paramref name="outward"/>.</summary>
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col, Vector3 outward)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (Vector3.Dot(n, outward) < 0) { var tmp = b; b = d; d = tmp; n = -n; }
            n.Normalize();
            int i = _v.Count;
            _v.Add(a); _v.Add(b); _v.Add(c); _v.Add(d);
            for (int k = 0; k < 4; k++) { _n.Add(n); _c.Add(col); }
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
            _t.Add(i); _t.Add(i + 2); _t.Add(i + 3);
        }

        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col)
            => Quad(a, b, c, d, col, Vector3.Cross(b - a, c - a));

        /// <summary>Axis-aligned (optionally yaw-rotated) box. <paramref name="center"/> is the box centre.</summary>
        public void Box(Vector3 center, Vector3 size, Color col, float yawDeg = 0f, bool bottom = false)
        {
            Vector3 h = size * 0.5f;
            Quaternion q = Quaternion.Euler(0, yawDeg, 0);
            Vector3 P(float x, float y, float z) => center + q * new Vector3(x * h.x, y * h.y, z * h.z);

            Vector3 p000 = P(-1, -1, -1), p100 = P(1, -1, -1), p010 = P(-1, 1, -1), p110 = P(1, 1, -1);
            Vector3 p001 = P(-1, -1, 1), p101 = P(1, -1, 1), p011 = P(-1, 1, 1), p111 = P(1, 1, 1);

            Quad(p010, p011, p111, p110, col, q * Vector3.up);          // top
            Quad(p100, p110, p111, p101, col, q * Vector3.right);       // +x
            Quad(p000, p001, p011, p010, col, q * Vector3.left);        // -x
            Quad(p001, p101, p111, p011, col, q * Vector3.forward);     // +z
            Quad(p000, p010, p110, p100, col, q * Vector3.back);        // -z
            if (bottom) Quad(p000, p100, p101, p001, col, q * Vector3.down);
        }

        /// <summary>Box defined by its bottom-centre (handy for buildings standing on the ground).</summary>
        public void BoxOnGround(Vector3 bottomCenter, Vector3 size, Color col, float yawDeg = 0f)
            => Box(bottomCenter + Vector3.up * (size.y * 0.5f), size, col, yawDeg);

        /// <summary>N-sided prism (cylinder) standing on <paramref name="baseCenter"/>.</summary>
        public void Prism(Vector3 baseCenter, float radius, float height, int sides, Color col, bool cap = true, float topRadius = -1f)
        {
            if (topRadius < 0) topRadius = radius;
            Vector3 top = baseCenter + Vector3.up * height;
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
                Vector3 d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0));
                Vector3 d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                Vector3 mid = (d0 + d1).normalized;
                Quad(baseCenter + d0 * radius, baseCenter + d1 * radius, top + d1 * topRadius, top + d0 * topRadius, col, mid);
                if (cap && topRadius > 0) Tri(top, top + d0 * topRadius, top + d1 * topRadius, col, Vector3.up);
            }
        }

        /// <summary>Cone standing on <paramref name="baseCenter"/>. Negative height = pointing down.</summary>
        public void Cone(Vector3 baseCenter, float radius, float height, int sides, Color col)
        {
            Vector3 tip = baseCenter + Vector3.up * height;
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
                Vector3 p0 = baseCenter + new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * radius;
                Vector3 p1 = baseCenter + new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * radius;
                Vector3 outward = ((p0 + p1) * 0.5f - baseCenter).normalized + Vector3.up * Mathf.Sign(height) * 0.5f;
                Tri(p0, p1, tip, col, outward);
            }
        }

        /// <summary>Double cone, a cheap "crystal" / gem shape.</summary>
        public void Crystal(Vector3 center, float radius, float height, int sides, Color col)
        {
            Cone(center, radius, height * 0.65f, sides, col);
            Cone(center, radius, -height * 0.35f, sides, col);
        }

        /// <summary>Low-poly UV sphere.</summary>
        public void Sphere(Vector3 center, float radius, Color col, int rings = 6, int segments = 10)
        {
            for (int r = 0; r < rings; r++)
            {
                float t0 = Mathf.PI * r / rings, t1 = Mathf.PI * (r + 1) / rings;
                for (int s = 0; s < segments; s++)
                {
                    float p0 = Mathf.PI * 2f * s / segments, p1 = Mathf.PI * 2f * (s + 1) / segments;
                    Vector3 a = SpherePoint(t0, p0), b = SpherePoint(t0, p1), c = SpherePoint(t1, p1), d = SpherePoint(t1, p0);
                    Vector3 outward = (a + b + c + d).normalized;
                    if (r == 0) Tri(center + a * radius, center + c * radius, center + d * radius, col, outward);
                    else if (r == rings - 1) Tri(center + a * radius, center + b * radius, center + c * radius, col, outward);
                    else Quad(center + a * radius, center + b * radius, center + c * radius, center + d * radius, col, outward);
                }
            }
        }

        static Vector3 SpherePoint(float theta, float phi)
            => new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta), Mathf.Sin(theta) * Mathf.Sin(phi));

        /// <summary>A stylised tree: trunk + stacked cones.</summary>
        public void Tree(Vector3 basePos, float height, Color leaves, Color trunk, System.Random rng = null)
        {
            Prism(basePos, height * 0.06f, height * 0.35f, 5, trunk, false);
            float r = height * 0.32f;
            Cone(basePos + Vector3.up * height * 0.25f, r, height * 0.55f, 6, leaves);
            Cone(basePos + Vector3.up * height * 0.5f, r * 0.75f, height * 0.5f, 6, leaves.Mul(1.1f));
        }

        /// <summary>A round "broadleaf" tree.</summary>
        public void RoundTree(Vector3 basePos, float height, Color leaves, Color trunk)
        {
            Prism(basePos, height * 0.07f, height * 0.5f, 5, trunk, false);
            Sphere(basePos + Vector3.up * height * 0.68f, height * 0.32f, leaves, 4, 7);
        }

        /// <summary>A flat rectangular frame (outline) lying in XZ at height y.</summary>
        public void RectFrame(Rect r, float y, float thickness, Color col)
        {
            float t = thickness;
            FlatQuad(new Rect(r.xMin, r.yMin, r.width, t), y, col);
            FlatQuad(new Rect(r.xMin, r.yMax - t, r.width, t), y, col);
            FlatQuad(new Rect(r.xMin, r.yMin + t, t, r.height - 2 * t), y, col);
            FlatQuad(new Rect(r.xMax - t, r.yMin + t, t, r.height - 2 * t), y, col);
        }

        /// <summary>Upward-facing quad covering an XZ rect (Rect.x = world x, Rect.y = world z).</summary>
        public void FlatQuad(Rect r, float y, Color col)
        {
            Quad(new Vector3(r.xMin, y, r.yMin), new Vector3(r.xMin, y, r.yMax),
                 new Vector3(r.xMax, y, r.yMax), new Vector3(r.xMax, y, r.yMin), col, Vector3.up);
        }

        /// <summary>Flat ring in XZ (for radius indicators).</summary>
        public void Ring(Vector3 center, float innerR, float outerR, int segments, Color col)
        {
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * Mathf.PI * 2f / segments, a1 = (i + 1) * Mathf.PI * 2f / segments;
                Vector3 d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0));
                Vector3 d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                Quad(center + d0 * innerR, center + d0 * outerR, center + d1 * outerR, center + d1 * innerR, col, Vector3.up);
            }
        }

        /// <summary>Vertical wireframe-ish box edges (12 thin bars). Good for "placeholder" holograms.</summary>
        public void WireBox(Vector3 center, Vector3 size, float thickness, Color col)
        {
            Vector3 h = size * 0.5f;
            float t = thickness;
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sz = -1; sz <= 1; sz += 2)
                Box(center + new Vector3(sx * h.x, 0, sz * h.z), new Vector3(t, size.y, t), col);
            for (int sy = -1; sy <= 1; sy += 2)
            {
                for (int sz = -1; sz <= 1; sz += 2)
                    Box(center + new Vector3(0, sy * h.y, sz * h.z), new Vector3(size.x, t, t), col);
                for (int sx = -1; sx <= 1; sx += 2)
                    Box(center + new Vector3(sx * h.x, sy * h.y, 0), new Vector3(t, t, size.z), col);
            }
        }

        public Mesh ToMesh(string name = "Generated")
        {
            var m = new Mesh { name = name };
            if (_v.Count > 65000) m.indexFormat = IndexFormat.UInt32;
            m.SetVertices(_v);
            m.SetNormals(_n);
            m.SetColors(_c);
            m.SetTriangles(_t, 0, true);
            return m;
        }
    }
}
