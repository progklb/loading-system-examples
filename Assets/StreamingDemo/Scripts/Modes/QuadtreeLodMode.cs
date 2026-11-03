using System.Collections.Generic;
using UnityEngine;

namespace StreamingDemo
{
    /// <summary>
    /// 06 · QUADTREE LOD  (Microsoft Flight Simulator, Google Earth, Cesium...)
    ///
    /// When the world is a whole planet, distance changes by orders of magnitude
    /// (orbit → runway). A fixed grid can't cope, so terrain is a QUADTREE of tiles:
    /// the root covers everything at low resolution, each child covers a quarter
    /// of its parent at the same vertex count = double the detail.
    ///
    /// Every frame: walk the tree from the root; split a tile if the camera is
    /// close relative to the tile's size. Two rules make it robust to slow
    /// networks:
    ///   * coarse first: low-depth tiles get the highest priority, so there is
    ///     always SOMETHING to draw;
    ///   * never show a hole: a parent keeps drawing until ALL FOUR children
    ///     have arrived, then swaps in one go.
    /// Skirts (little walls hanging under each tile edge) hide cracks between
    /// neighbouring tiles of different depth.
    /// </summary>
    public sealed class QuadtreeLodMode : DemoMode
    {
        public override string Number => "06";
        public override string Title => "Quadtree LOD";
        public override string ShortName => "Quadtree";
        public override string GameRef => "Microsoft Flight Simulator  ·  Google Earth";
        public override string Tagline => "A planet can't be a grid. Terrain is a quadtree: tiles split into four sharper children as the camera approaches, streamed coarse-first.";
        public override string[] HowItWorks => new[]
        {
            "The root tile covers the whole world. Each child covers a quarter of its parent with the same vertex count: twice the detail.",
            "Each frame, walk down from the root. Split a tile if  distance < tile size × split factor.",
            "Coarse first: shallow tiles have the highest priority, so the whole view is always covered by something.",
            "No holes: a parent keeps drawing until all four children are resident, then they swap in together.",
            "Skirts hang under tile edges to hide cracks between neighbours of different depth.",
            "Freeze LOD and fly away to see the tile structure the camera produced.",
        };

        public override (Color color, string label)[] Legend => new[]
        {
            (Palette.Hex("#ff5d73"), "Depth 0-2"),
            (Palette.Hex("#ffb547"), "3-4"),
            (Palette.Hex("#3ddc97"), "5-6"),
            (Palette.Hex("#4ea8ff"), "7+"),
            (Palette.Queued, "Tile requested"),
            (Palette.Loading, "Tile downloading"),
        };

        // ------------------------------------------------------------------ data

        const float WorldSize = 8192f;
        const int TileRes = 16;
        const float TileMB = 0.3f;

        enum TileState { Unloaded, Queued, Loaded }

        sealed class Node
        {
            public int Depth;
            public Vector2 Center;
            public float Size;
            public Node[] Children;
            public TileState State;
            public LoadRequest Request;
            public MeshRenderer Mesh;
            public MeshRenderer Outline;
            public float LastNeeded;
            public float AvgHeight;
            public Rect Rect => new Rect(Center.x - Size * 0.5f, Center.y - Size * 0.5f, Size, Size);
        }

        Node _root;
        readonly List<Node> _render = new List<Node>();
        readonly HashSet<Node> _renderSet = new HashSet<Node>();
        readonly HashSet<Node> _prevRender = new HashSet<Node>();
        readonly List<Node> _all = new List<Node>();
        readonly List<Node> _scratch = new List<Node>();
        readonly MeshBuilder _mb = new MeshBuilder();
        Material[] _depthMats;

        float _splitFactor = 2.2f;
        int _maxDepth = 8;
        bool _freeze;
        bool _tint = true;
        int _deepest;
        Vector3 _lodCamera;

        static readonly Color[] DepthColors =
        {
            Palette.Hex("#ff5d73"), Palette.Hex("#ff5d73"), Palette.Hex("#ff7a5c"), Palette.Hex("#ffb547"), Palette.Hex("#ffe066"),
            Palette.Hex("#3ddc97"), Palette.Hex("#2fd1c5"), Palette.Hex("#4ea8ff"), Palette.Hex("#8a7dff"), Palette.Hex("#c77dff"),
        };

        // =====================================================================================
        //  Setup
        // =====================================================================================

        protected override void OnEnter()
        {
            Ctx.Disk.Name = "Network (tile server)";
            Ctx.Disk.BandwidthMBps = 6f;
            Ctx.Disk.SeekMs = 120f;
            Ctx.Disk.MaxInFlight = 6;
            Ctx.Memory.BudgetMB = 256f;
            Ctx.SetFog(5000f, 16000f);
            Ctx.App.SetShadowDistance(900f);
            Ctx.Cam.farClipPlane = 30000f;

            _all.Clear(); _render.Clear(); _renderSet.Clear(); _prevRender.Clear();
            _freeze = false;
            _deepest = 0;

            _depthMats = new Material[DepthColors.Length];
            for (int i = 0; i < DepthColors.Length; i++)
            {
                _depthMats[i] = new Material(Ctx.Assets.Lit) { name = $"SD_Depth{i}" };
                _depthMats[i].SetColor(DemoAssets.BaseColorId, Color.Lerp(Color.white, DepthColors[i], 0.55f) * 1.1f);
            }

            var floor = DemoAssets.Spawn("Ocean", Root, DemoAssets.UnitQuad, Ctx.Assets.Grid(512, 8, Palette.Hex("#0a1630"), Palette.Hex("#17305a")), false);
            floor.transform.position = new Vector3(0, -2f, 0);
            floor.transform.localScale = new Vector3(60000, 1, 60000);

            _root = new Node { Depth = 0, Center = Vector2.zero, Size = WorldSize };
            _root.AvgHeight = Height(0, 0);
            _all.Add(_root);

            // A flight that dives low and climbs high, so the tree has to reshape itself constantly.
            var path = new List<Vector3>();
            float[] alts = { 90, 250, 900, 2600, 3800, 1600, 400, 70, 60, 200, 1200, 600 };
            for (int i = 0; i < alts.Length; i++)
            {
                float a = i / (float)alts.Length * Mathf.PI * 2f;
                float rad = 1700f + 500f * Mathf.Sin(a * 2f);
                var xz = new Vector3(Mathf.Cos(a) * rad, 0, Mathf.Sin(a) * rad);
                path.Add(new Vector3(xz.x, Height(xz.x, xz.z) + alts[i], xz.z));
            }

            var p = Ctx.Player;
            p.AllowVertical = true;
            p.Speed = 160f;
            p.SetPath(path, true, 1);
            p.Teleport(path[0]);
            p.SetVisualScale(3f, false);
            Ctx.Rig.SetView(52f, 750f, 0f);
            Ctx.Rig.MaxDistance = 20000f;
        }

        protected override void OnExit()
        {
            Ctx.Rig.MaxDistance = 6000f;
            foreach (var m in _depthMats) if (m != null) Object.Destroy(m);
        }

        // =====================================================================================
        //  CORE ALGORITHM - select, request, keep the parent until the children are in
        // =====================================================================================

        public override void Tick(float dt)
        {
            var p = Ctx.Player;
            // Keep the aircraft above the ground.
            var pos = p.transform.position;
            float ground = Height(pos.x, pos.z);
            if (pos.y < ground + 25f) { pos.y = ground + 25f; p.transform.position = pos; }

            if (_root.State == TileState.Unloaded) Request(_root);

            if (!_freeze)
            {
                _lodCamera = Ctx.Cam.transform.position;
                _render.Clear();
                _deepest = 0;
                if (_root.State == TileState.Loaded) Select(_root);
            }

            Present();
            Evict();
        }

        void Select(Node n)
        {
            n.LastNeeded = Time.time;
            if (n.Depth < _maxDepth && ShouldSplit(n))
            {
                EnsureChildren(n);
                bool allReady = true;
                foreach (var c in n.Children)
                {
                    c.LastNeeded = Time.time;
                    if (c.State != TileState.Loaded) { allReady = false; Request(c); }
                }
                // Never show a hole: only descend when ALL FOUR children are resident.
                if (allReady)
                {
                    foreach (var c in n.Children) Select(c);
                    return;
                }
            }
            _render.Add(n);
            _deepest = Mathf.Max(_deepest, n.Depth);
        }

        bool ShouldSplit(Node n)
        {
            // Distance from the camera to the tile's box (approximated at its average height).
            Rect r = n.Rect;
            float dx = Mathf.Max(r.xMin - _lodCamera.x, 0, _lodCamera.x - r.xMax);
            float dz = Mathf.Max(r.yMin - _lodCamera.z, 0, _lodCamera.z - r.yMax);
            float dy = _lodCamera.y - n.AvgHeight;
            float d = Mathf.Sqrt(dx * dx + dz * dz + dy * dy);
            return d < n.Size * _splitFactor;
        }

        void Request(Node n)
        {
            if (n.State != TileState.Unloaded) { if (n.Request != null && n.Request.State == RequestState.Queued) n.Request.Priority = Priority(n); return; }
            var captured = n;
            n.State = TileState.Queued;
            n.Request = Ctx.Disk.Enqueue($"tile L{n.Depth}", TileMB, Priority(n), _ => OnTileLoaded(captured), n);
        }

        float Priority(Node n)
        {
            // COARSE FIRST: depth dominates, distance breaks ties within a depth.
            float d = Vector2.Distance(new Vector2(_lodCamera.x, _lodCamera.z), n.Center);
            return n.Depth * 10f + d / n.Size;
        }

        void EnsureChildren(Node n)
        {
            if (n.Children != null) return;
            n.Children = new Node[4];
            float q = n.Size * 0.25f;
            for (int i = 0; i < 4; i++)
            {
                var c = new Node
                {
                    Depth = n.Depth + 1,
                    Center = n.Center + new Vector2((i & 1) == 0 ? -q : q, (i & 2) == 0 ? -q : q),
                    Size = n.Size * 0.5f,
                };
                c.AvgHeight = Height(c.Center.x, c.Center.y);
                n.Children[i] = c;
                _all.Add(c);
            }
        }

        void OnTileLoaded(Node n)
        {
            if (n.State != TileState.Queued) return;
            n.State = TileState.Loaded;
            Ctx.Memory.Allocate(TileMB);
            n.Mesh = BuildTile(n);
            n.Mesh.gameObject.SetActive(false);
        }

        void Present()
        {
            // Swap visibility only for tiles that changed.
            _renderSet.Clear();
            foreach (var n in _render) _renderSet.Add(n);
            foreach (var n in _prevRender)
                if (!_renderSet.Contains(n) && n.Mesh != null) n.Mesh.gameObject.SetActive(false);
            foreach (var n in _renderSet)
            {
                if (n.Mesh == null) continue;
                if (!_prevRender.Contains(n))
                {
                    n.Mesh.gameObject.SetActive(true);
                    Tweens.Flash(n.Mesh, DepthColors[Mathf.Min(n.Depth, DepthColors.Length - 1)] * 0.35f, 0.6f);
                }
                var mat = _tint ? _depthMats[Mathf.Min(n.Depth, _depthMats.Length - 1)] : Ctx.Assets.Lit;
                if (n.Mesh.sharedMaterial != mat) n.Mesh.sharedMaterial = mat;
            }
            _prevRender.Clear();
            foreach (var n in _renderSet) _prevRender.Add(n);

            // Outlines for tiles that are requested / downloading.
            foreach (var n in _all)
            {
                bool pending = n.State == TileState.Queued;
                if (pending && n.Outline == null)
                {
                    var mb = new MeshBuilder();
                    Rect r = n.Rect;
                    r.xMin += n.Size * 0.01f; r.yMin += n.Size * 0.01f; r.xMax -= n.Size * 0.01f; r.yMax -= n.Size * 0.01f;
                    mb.RectFrame(r, 0, Mathf.Max(2f, n.Size * 0.012f), Color.white);
                    n.Outline = DemoAssets.Spawn("Pending", Root, mb.ToMesh("Outline"), Ctx.Assets.Glow(Palette.Queued.WithAlpha(0.7f)), false);
                }
                if (n.Outline == null) continue;
                if (!pending) { DemoAssets.DestroyWithMesh(n.Outline.gameObject); n.Outline = null; continue; }
                n.Outline.transform.position = new Vector3(0, Mathf.Max(n.AvgHeight, 0) + 15f, 0);
                var m = n.Request.IsInFlight ? Ctx.Assets.Glow(Palette.Loading.WithAlpha(0.95f)) : Ctx.Assets.Glow(Palette.Queued.WithAlpha(0.55f));
                if (n.Outline.sharedMaterial != m) n.Outline.sharedMaterial = m;
            }
        }

        void Evict()
        {
            if (_freeze) return;
            // Tiles not touched by selection for a few seconds are dropped (children before parents).
            float now = Time.time;
            _scratch.Clear();
            foreach (var n in _all)
                if (n != _root && now - n.LastNeeded > 3f && n.Children == null) _scratch.Add(n);

            foreach (var n in _scratch)
            {
                if (n.State == TileState.Queued) Ctx.Disk.Cancel(n.Request);
                if (n.State == TileState.Loaded)
                {
                    Ctx.Memory.Free(TileMB);
                    DemoAssets.DestroyWithMesh(n.Mesh.gameObject);
                    n.Mesh = null;
                    _prevRender.Remove(n);
                }
                if (n.Outline != null) { DemoAssets.DestroyWithMesh(n.Outline.gameObject); n.Outline = null; }
                n.State = TileState.Unloaded;
            }

            // Collapse parents whose four children are all gone, so the tree shrinks back.
            foreach (var n in _all)
            {
                if (n.Children == null) continue;
                bool allGone = true;
                foreach (var c in n.Children) if (c.State != TileState.Unloaded || c.Children != null) { allGone = false; break; }
                if (allGone && now - n.LastNeeded > 3f) { foreach (var c in n.Children) _all.Remove(c); n.Children = null; break; }
            }
        }

        // =====================================================================================
        //  HUD
        // =====================================================================================

        public override void DrawSettings(HudUi ui)
        {
            ui.Header("Quadtree");
            _splitFactor = ui.Slider("Split factor", _splitFactor, 0.8f, 4f, "{0:0.0}");
            _maxDepth = ui.IntSlider("Max depth", _maxDepth, 3, 9, "{0:0}");
            bool f = ui.Toggle("Freeze LOD (then fly away)", _freeze);
            if (f != _freeze) { _freeze = f; Ctx.Banner(f ? "LOD FROZEN  ·  zoom out to inspect the tiles" : "LOD live again", Palette.Accent); }
            _tint = ui.Toggle("Tint tiles by depth", _tint);
        }

        public override void DrawStats(HudUi ui)
        {
            ui.Header("Tiles");
            int resident = 0, pending = 0;
            foreach (var n in _all)
            {
                if (n.State == TileState.Loaded) resident++;
                else if (n.State == TileState.Queued) pending++;
            }
            var pos = Ctx.Player.transform.position;
            ui.KV("Altitude above ground", $"{pos.y - Height(pos.x, pos.z):0} u");
            ui.KV("Tiles drawn", _render.Count.ToString(), Palette.Loaded);
            ui.KV("Tiles resident", resident.ToString());
            ui.KV("Tiles pending", pending.ToString(), pending > 0 ? Palette.Queued : Palette.Text);
            ui.KV("Deepest level drawn", $"{_deepest}  ({WorldSize / (1 << _deepest):0} u tiles)");
        }

        public override bool HasMinimap => true;

        public override void DrawMinimap(HudUi ui, Rect r)
        {
            if (Event.current.type != EventType.Repaint) return;
            float size = Mathf.Min(r.width, r.height);
            float ox = (r.width - size) * 0.5f;
            Rect M(Rect w) => new Rect(ox + (w.xMin / WorldSize + 0.5f) * size, (0.5f - w.yMax / WorldSize) * size, w.width / WorldSize * size, w.height / WorldSize * size);

            foreach (var n in _render)
            {
                var mr = M(n.Rect);
                Color c = DepthColors[Mathf.Min(n.Depth, DepthColors.Length - 1)];
                ui.Fill(mr, c.WithAlpha(0.35f));
                ui.Frame(mr, c.WithAlpha(0.9f));
            }
            foreach (var n in _all)
                if (n.State == TileState.Queued)
                    ui.Frame(M(n.Rect), (n.Request.IsInFlight ? Palette.Loading : Palette.Queued).WithAlpha(0.9f));

            var pos = Ctx.Player.transform.position;
            var pp = new Vector2(ox + (pos.x / WorldSize + 0.5f) * size, (0.5f - pos.z / WorldSize) * size);
            ui.Pill(new Rect(pp.x - 4, pp.y - 4, 8, 8), Palette.Player);
        }

        // =====================================================================================
        //  Terrain
        // =====================================================================================

        static float Height(float x, float z)
        {
            float continent = Noise.Fbm(x / 5200f + 3.1f, z / 5200f + 1.7f, 3);
            float ridges = Noise.Ridged(x / 1400f, z / 1400f, 5, 2.3f);
            float hills = Noise.Fbm(x / 300f, z / 300f, 3);
            float land = Mathf.SmoothStep(0.32f, 0.62f, continent);
            float h = land * (ridges * ridges * 1100f + hills * 120f) + (continent - 0.45f) * 400f;
            return Mathf.Max(h, 0f);
        }

        static Color TerrainColor(float h, float slope)
        {
            Color c;
            if (h <= 0.5f) c = Palette.Hex("#1f4f8a");
            else if (h < 25) c = Palette.Hex("#d1c38a");
            else if (h < 220) c = Palette.Hex("#4f8a3a");
            else if (h < 480) c = Palette.Hex("#3b6a35");
            else if (h < 760) c = Palette.Hex("#7a6f62");
            else c = Palette.Hex("#eef2f7");
            if (slope > 0.9f && h > 25) c = Color.Lerp(c, Palette.Hex("#6b655c"), 0.6f);
            return c;
        }

        MeshRenderer BuildTile(Node n)
        {
            var mb = _mb;
            mb.Clear();
            Rect r = n.Rect;
            float step = n.Size / TileRes;
            var hs = new float[TileRes + 1, TileRes + 1];
            for (int i = 0; i <= TileRes; i++)
            for (int j = 0; j <= TileRes; j++)
                hs[i, j] = Height(r.xMin + i * step, r.yMin + j * step);

            for (int i = 0; i < TileRes; i++)
            for (int j = 0; j < TileRes; j++)
            {
                Vector3 p00 = new Vector3(r.xMin + i * step, hs[i, j], r.yMin + j * step);
                Vector3 p10 = new Vector3(r.xMin + (i + 1) * step, hs[i + 1, j], r.yMin + j * step);
                Vector3 p01 = new Vector3(r.xMin + i * step, hs[i, j + 1], r.yMin + (j + 1) * step);
                Vector3 p11 = new Vector3(r.xMin + (i + 1) * step, hs[i + 1, j + 1], r.yMin + (j + 1) * step);
                float avg = (p00.y + p10.y + p01.y + p11.y) * 0.25f;
                float slope = (Mathf.Abs(p00.y - p11.y) + Mathf.Abs(p10.y - p01.y)) / (step * 2f);
                Color c = TerrainColor(avg, slope);
                // Darken the outer ring of quads so tile borders are visible.
                if (i == 0 || j == 0 || i == TileRes - 1 || j == TileRes - 1) c = c.Mul(0.72f);
                c = c.Glow(avg <= 0.5f ? 0.08f : 0f);
                mb.Tri(p00, p01, p11, c, Vector3.up);
                mb.Tri(p00, p11, p10, c.Mul(0.95f).Glow(c.a), Vector3.up);
            }

            // Skirts: hang a strip below every edge to hide T-junction cracks.
            float skirt = n.Size * 0.04f;
            Color sk = Palette.Hex("#3a3a40").Glow(0);
            for (int k = 0; k < TileRes; k++)
            {
                Skirt(mb, new Vector3(r.xMin + k * step, hs[k, 0], r.yMin), new Vector3(r.xMin + (k + 1) * step, hs[k + 1, 0], r.yMin), skirt, Vector3.back, sk);
                Skirt(mb, new Vector3(r.xMin + k * step, hs[k, TileRes], r.yMax), new Vector3(r.xMin + (k + 1) * step, hs[k + 1, TileRes], r.yMax), skirt, Vector3.forward, sk);
                Skirt(mb, new Vector3(r.xMin, hs[0, k], r.yMin + k * step), new Vector3(r.xMin, hs[0, k + 1], r.yMin + (k + 1) * step), skirt, Vector3.left, sk);
                Skirt(mb, new Vector3(r.xMax, hs[TileRes, k], r.yMin + k * step), new Vector3(r.xMax, hs[TileRes, k + 1], r.yMin + (k + 1) * step), skirt, Vector3.right, sk);
            }

            var mr = DemoAssets.Spawn($"Tile L{n.Depth}", Root, mb.ToMesh("Tile"), Ctx.Assets.Lit, n.Depth >= 5);
            return mr;
        }

        static void Skirt(MeshBuilder mb, Vector3 a, Vector3 b, float depth, Vector3 outward, Color c)
        {
            mb.Quad(a, b, b - Vector3.up * depth, a - Vector3.up * depth, c, outward);
        }
    }
}
