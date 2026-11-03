using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StreamingDemo
{
    /// <summary>
    /// Shared materials + helpers for spawning generated meshes.
    /// Everything is created at runtime from three shaders in Resources/Shaders,
    /// so the demo has no hand-authored assets to keep in sync.
    /// </summary>
    public sealed class DemoAssets
    {
        public readonly Shader LitShader;
        public readonly Shader GlowShader;
        public readonly Shader GridShader;

        /// <summary>The one material used by all vertex-coloured content.</summary>
        public readonly Material Lit;

        readonly Dictionary<long, Material> _glowCache = new Dictionary<long, Material>();
        public static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        public static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        public DemoAssets()
        {
            LitShader = Resources.Load<Shader>("Shaders/VertexLit");
            GlowShader = Resources.Load<Shader>("Shaders/Glow");
            GridShader = Resources.Load<Shader>("Shaders/GridFloor");
            if (LitShader == null || GlowShader == null || GridShader == null)
                Debug.LogError("[StreamingDemo] Could not load shaders from Resources/Shaders.");

            Lit = new Material(LitShader) { name = "SD_Lit", enableInstancing = false };

            Shader.SetGlobalColor("_SD_AmbientSky", Palette.Hex("#2c3d63"));
            Shader.SetGlobalColor("_SD_AmbientGround", Palette.Hex("#120e1a"));
        }

        /// <summary>Cached additive hologram material.</summary>
        public Material Glow(Color c, float rim = 0f, float scan = 0f, float pulse = 0f, bool additive = true)
        {
            Color32 c32 = c;
            long key = ((long)c32.r << 56) | ((long)c32.g << 48) | ((long)c32.b << 40) | ((long)c32.a << 32)
                       | ((long)(rim * 10) << 24) | ((long)(scan * 10) << 16) | ((long)(pulse * 10) << 8) | (additive ? 1L : 0L);
            if (_glowCache.TryGetValue(key, out var m) && m != null) return m;

            m = new Material(GlowShader) { name = "SD_Glow" };
            m.SetColor(BaseColorId, c);
            m.SetFloat("_Rim", rim);
            m.SetFloat("_Scan", scan);
            m.SetFloat("_Pulse", pulse);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", additive ? (float)BlendMode.One : (float)BlendMode.OneMinusSrcAlpha);
            _glowCache[key] = m;
            return m;
        }

        /// <summary>A fresh (uncached) glow material, for things whose colour animates.</summary>
        public Material GlowInstance(Color c, float rim = 0f, float scan = 0f)
        {
            var m = new Material(GlowShader) { name = "SD_GlowInstance" };
            m.SetColor(BaseColorId, c);
            m.SetFloat("_Rim", rim);
            m.SetFloat("_Scan", scan);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.One);
            return m;
        }

        public Material Grid(float gridSize, float majorEvery, Color floor, Color line)
        {
            var m = new Material(GridShader) { name = "SD_Grid" };
            m.SetFloat("_GridSize", gridSize);
            m.SetFloat("_MajorEvery", majorEvery);
            m.SetColor(BaseColorId, floor);
            m.SetColor("_LineColor", line);
            return m;
        }

        /// <summary>Create a GameObject that renders <paramref name="mesh"/> with <paramref name="mat"/>.</summary>
        public static MeshRenderer Spawn(string name, Transform parent, Mesh mesh, Material mat, bool castShadows = true)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            mr.receiveShadows = castShadows;
            return mr;
        }

        /// <summary>Destroy a GameObject together with its generated mesh (avoids leaking GPU buffers).</summary>
        public static void DestroyWithMesh(GameObject go)
        {
            if (go == null) return;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                if (mf.sharedMesh != null && mf.sharedMesh.name != "SD_Shared") Object.Destroy(mf.sharedMesh);
            Object.Destroy(go);
        }

        static Mesh _unitCube, _unitQuad;

        /// <summary>Shared unit cube (centre at origin). Name "SD_Shared" protects it from DestroyWithMesh.</summary>
        public static Mesh UnitCube
        {
            get
            {
                if (_unitCube != null) return _unitCube;
                var mb = new MeshBuilder();
                mb.Box(Vector3.zero, Vector3.one, Color.white.Glow(0), 0, true);
                _unitCube = mb.ToMesh("SD_Shared");
                return _unitCube;
            }
        }

        /// <summary>Shared 1x1 upward-facing quad in XZ centred on origin.</summary>
        public static Mesh UnitQuad
        {
            get
            {
                if (_unitQuad != null) return _unitQuad;
                var mb = new MeshBuilder();
                mb.FlatQuad(new Rect(-0.5f, -0.5f, 1, 1), 0, Color.white);
                _unitQuad = mb.ToMesh("SD_Shared");
                return _unitQuad;
            }
        }
    }
}
