using System.Collections.Generic;
using UnityEngine;

namespace StreamingDemo
{
    /// <summary>
    /// 05 · CHUNKS: GENERATE OR LOAD  (Minecraft → No Man's Sky)
    ///
    /// An effectively infinite world of 16x16 column "chunks". Every chunk within
    /// the render distance must be resident, and there are two ways to get one:
    ///   * never visited  -> GENERATE it from the seed (expensive CPU work)
    ///   * visited before -> LOAD it from the region file on disk (cheap I/O)
    /// Generated chunks are saved, so the second lap of a route is much faster.
    ///
    /// Turn off saving and the world still comes back identical, because
    /// generation is deterministic. That is the No Man's Sky idea: store only
    /// the player's changes, regenerate everything else.
    /// </summary>
    public sealed class ChunkGenerationMode : DemoMode
    {
        public override string Number => "05";
        public override string Title => "Chunks: Generate or Load";
        public override string ShortName => "Chunks";
        public override string GameRef => "Minecraft  ·  and No Man's Sky";
        public override string Tagline => "An infinite grid of 16×16 chunks. New ground is generated from a seed on worker threads; visited ground is read back from region files.";
        public override string[] HowItWorks => new[]
        {
            "Everything within the render distance (a circle) must be resident.",
            "Never-seen chunks are GENERATED from the world seed: noise → terrain → trees. Costly CPU work on a worker pool.",
            "Generated chunks are written to region files. Next time you come back they are LOADED, which is much cheaper.",
            "Chunks load nearest-first, so the world grows outward in rings.",
            "Outrun the workers and you meet the void at the edge.",
            "Same seed, same chunk. No Man's Sky stores only what you changed.",
        };

        public override (Color color, string label)[] Legend => new[]
        {
            (Palette.Queued, "Queued"),
            (Palette.Generating, "Generating (CPU)"),
            (Palette.Cached, "Loading region file"),
            (Palette.Loaded, "Resident"),
            (Palette.Unloading, "Player in void"),
        };

        // ------------------------------------------------------------------ data

        const int Size = 16;
        const int Water = 12;
        const float ChunkMB = 0.25f;     // resident footprint
        const float RegionMB = 0.06f;    // compressed on disk

        enum ChunkState { Unloaded, QueuedGen, QueuedDisk, Loaded }

        sealed class Chunk
        {
            public Vector2Int C;
            public ChunkState State;
            public LoadRequest Request;
            public GameObject Mesh;
        }

        readonly Dictionary<Vector2Int, Chunk> _chunks = new Dictionary<Vector2Int, Chunk>();
        readonly HashSet<Vector2Int> _saved = new HashSet<Vector2Int>();   // "region files"
        readonly Dictionary<Vector2Int, MeshRenderer> _markers = new Dictionary<Vector2Int, MeshRenderer>();
        readonly List<Vector2Int> _scratch = new List<Vector2Int>();
        readonly MeshBuilder _mb = new MeshBuilder();
        readonly int[,] _heights = new int[Size + 2, Size + 2];

        SimulatedIO _gen;   // the worldgen worker pool, modelled as a second "device"
        int _renderDistance = 6;
        bool _saving = true;
        bool _hysteresis = true;
        int _generated, _loadedFromDisk;
        float _voidSeconds;
        bool _inVoid;
        int _seed = 1337;
        Transform _markersRoot;

        // =====================================================================================
        //  Setup
        // =====================================================================================

        protected override void OnEnter()
        {
            Ctx.Disk.Name = "SSD region files";
            Ctx.Disk.BandwidthMBps = 40f;
            Ctx.Disk.SeekMs = 6f;
            Ctx.Disk.MaxInFlight = 4;
            Ctx.Memory.BudgetMB = 160f;
            Ctx.App.SetShadowDistance(180f);
            Ctx.Cam.farClipPlane = 1500f;

            _gen = new SimulatedIO("Worldgen workers") { BandwidthMBps = 10f, SeekMs = 0f, MaxInFlight = 2, Unit = "chunks" };

            _chunks.Clear(); _saved.Clear(); _markers.Clear();
            _generated = _loadedFromDisk = 0;
            _voidSeconds = 0;

            _markersRoot = new GameObject("Markers").transform;
            _markersRoot.SetParent(Root, false);

            var floor = DemoAssets.Spawn("Void", Root, DemoAssets.UnitQuad, Ctx.Assets.Grid(Size, 32, Palette.Hex("#05070e"), Palette.Hex("#1a2440")), false);
            floor.transform.position = new Vector3(0, 0f, 0);
            floor.transform.localScale = new Vector3(6000, 1, 6000);

            // Autopilot: a big circle, so the second lap is all region-file loads.
            var path = new List<Vector3>();
            for (int i = 0; i < 48; i++)
            {
                float a = i / 48f * Mathf.PI * 2f;
                path.Add(new Vector3(Mathf.Cos(a) * 170f, 0, Mathf.Sin(a) * 170f));
            }
            var p = Ctx.Player;
            p.Speed = 10f;
            p.SetPath(path, true, 1);
            p.Teleport(path[0]);
            p.SetVisualScale(0.6f);
            Ctx.Rig.SetView(55f, 150f, 30f);
        }

        // =====================================================================================
        //  CORE ALGORITHM - nearest-first chunk pipeline with two sources
        // =====================================================================================

        public override void Tick(float dt)
        {
            _gen.Tick(dt);
            var p = Ctx.Player;
            Vector3 pos = p.transform.position;
            var pc = new Vector2Int(Mathf.FloorToInt(pos.x / Size), Mathf.FloorToInt(pos.z / Size));
            int r = _renderDistance;
            float r2 = (r + 0.5f) * (r + 0.5f);

            // 1) DESIRED SET: all chunks inside a circle of radius r around the player's chunk.
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                float d2 = dx * dx + dz * dz;
                if (d2 > r2) continue;
                var c = new Vector2Int(pc.x + dx, pc.y + dz);
                if (!_chunks.TryGetValue(c, out var ch)) { ch = new Chunk { C = c }; _chunks[c] = ch; }

                if (ch.State == ChunkState.Unloaded)
                {
                    // 2) TWO SOURCES: read it back if we saved it, otherwise generate it from the seed.
                    var captured = ch;
                    if (_saved.Contains(c))
                    {
                        ch.State = ChunkState.QueuedDisk;
                        ch.Request = Ctx.Disk.Enqueue($"r.{c.x >> 5}.{c.y >> 5}.mca", RegionMB, d2, _ => OnChunkReady(captured, false), ch);
                    }
                    else
                    {
                        ch.State = ChunkState.QueuedGen;
                        ch.Request = _gen.Enqueue($"gen({c.x},{c.y})", 1f, d2, _ => OnChunkReady(captured, true), ch);
                    }
                }
                else if (ch.Request != null && ch.Request.State == RequestState.Queued)
                {
                    ch.Request.Priority = d2;   // nearest first, re-sorted as the player moves
                }
            }

            // 3) EVICT chunks outside the (slightly larger, if hysteresis) unload circle.
            float u = r + (_hysteresis ? 2f : 0.5f);
            float u2 = u * u;
            _scratch.Clear();
            foreach (var kv in _chunks)
            {
                var d = kv.Key - pc;
                if (d.x * d.x + d.y * d.y > u2) _scratch.Add(kv.Key);
            }
            foreach (var c in _scratch)
            {
                var ch = _chunks[c];
                if (ch.State == ChunkState.QueuedGen) _gen.Cancel(ch.Request);
                if (ch.State == ChunkState.QueuedDisk) Ctx.Disk.Cancel(ch.Request);
                if (ch.State == ChunkState.Loaded)
                {
                    Ctx.Memory.Free(ChunkMB);
                    DemoAssets.DestroyWithMesh(ch.Mesh);
                }
                _chunks.Remove(c);
            }

            // 4) Standing in an unloaded chunk: the game won't let you move (you'd fall into the void).
            bool inVoid = !_chunks.TryGetValue(pc, out var mine) || mine.State != ChunkState.Loaded;
            if (inVoid && !_inVoid) Ctx.Banner("THE VOID  ·  chunk not generated yet", Palette.Unloading, 1.2f);
            _inVoid = inVoid;
            p.Frozen = inVoid;
            if (inVoid) _voidSeconds += dt;

            // Keep the player on top of the terrain.
            var tp = p.transform.position;
            tp.y = Mathf.Lerp(tp.y, Mathf.Max(Height(Mathf.FloorToInt(tp.x), Mathf.FloorToInt(tp.z)), Water) + 0.5f, 1f - Mathf.Exp(-10f * dt));
            p.transform.position = tp;

            // Fog sits just inside the render distance - exactly what Minecraft does to hide the edge.
            float camDist = Ctx.Rig.Distance;
            float edge = r * Size;
            if (Ctx.FogEnabled) { Ctx.FogStart = camDist + edge * 0.35f; Ctx.FogEnd = camDist + edge * 1.05f; Ctx.ApplyFog(); }
            else { Ctx.FogStart = 5000; Ctx.FogEnd = 6000; }

            UpdateMarkers();
        }

        void OnChunkReady(Chunk ch, bool generated)
        {
            if (!_chunks.TryGetValue(ch.C, out var live) || live != ch) return;
            ch.State = ChunkState.Loaded;
            Ctx.Memory.Allocate(ChunkMB);
            if (generated)
            {
                _generated++;
                if (_saving) _saved.Add(ch.C);   // write it to the region file
            }
            else _loadedFromDisk++;

            ch.Mesh = BuildChunk(ch.C);
            Tweens.PopIn(ch.Mesh.transform, 0.35f);
            Tweens.Flash(ch.Mesh.GetComponent<Renderer>(), (generated ? Palette.Generating : Palette.Cached) * 0.5f, 0.7f);
        }

        // =====================================================================================
        //  Visualisation
        // =====================================================================================

        void UpdateMarkers()
        {
            // Remove markers for chunks that are done or gone.
            _scratch.Clear();
            foreach (var kv in _markers)
                if (!_chunks.TryGetValue(kv.Key, out var ch) || ch.State == ChunkState.Loaded || ch.State == ChunkState.Unloaded)
                    _scratch.Add(kv.Key);
            foreach (var c in _scratch) { Object.Destroy(_markers[c].gameObject); _markers.Remove(c); }

            foreach (var ch in _chunks.Values)
            {
                if (ch.State != ChunkState.QueuedGen && ch.State != ChunkState.QueuedDisk) continue;
                if (!_markers.TryGetValue(ch.C, out var m))
                {
                    m = DemoAssets.Spawn("Marker", _markersRoot, DemoAssets.UnitQuad, null, false);
                    _markers[ch.C] = m;
                }
                bool active = ch.Request.IsInFlight;
                Color c = !active ? Palette.Queued : ch.State == ChunkState.QueuedGen ? Palette.Generating : Palette.Cached;
                var mat = active ? Ctx.Assets.Glow(c.WithAlpha(0.55f)) : Ctx.Assets.Glow(c.WithAlpha(0.12f));
                if (m.sharedMaterial != mat) m.sharedMaterial = mat;
                float s = active ? Mathf.Lerp(4f, Size - 1f, ch.Request.Progress) : Size - 2f;
                m.transform.position = new Vector3((ch.C.x + 0.5f) * Size, Water + 0.2f, (ch.C.y + 0.5f) * Size);
                m.transform.localScale = new Vector3(s, 1, s);
            }
        }

        public override void DrawSettings(HudUi ui)
        {
            ui.Header("World");
            _renderDistance = ui.IntSlider("Render distance", _renderDistance, 2, 12, "{0:0} chunks");
            _gen.BandwidthMBps = ui.Slider("Worldgen speed", _gen.BandwidthMBps, 1f, 120f, "{0:0} chunks/s", true, Palette.Generating);
            _gen.MaxInFlight = ui.IntSlider("Worker threads", _gen.MaxInFlight, 1, 8, "{0:0}");
            _saving = ui.Toggle("Save chunks to region files", _saving);
            _hysteresis = ui.Toggle("Hysteresis (unload at +2 chunks)", _hysteresis);
            if (ui.ButtonRow("Delete save (forget visited chunks)"))
            {
                _saved.Clear();
                Ctx.Log("Region files deleted", Palette.Unloading);
            }
        }

        public override void DrawStats(HudUi ui)
        {
            ui.Header("Chunks");
            int loaded = 0, gen = 0, disk = 0;
            foreach (var ch in _chunks.Values)
            {
                if (ch.State == ChunkState.Loaded) loaded++;
                else if (ch.State == ChunkState.QueuedGen) gen++;
                else if (ch.State == ChunkState.QueuedDisk) disk++;
            }
            ui.KV("Resident chunks", loaded.ToString(), Palette.Loaded);
            ui.KV("Waiting for worldgen", gen.ToString(), gen > 0 ? Palette.Generating : Palette.Text);
            ui.KV("Waiting for disk", disk.ToString(), disk > 0 ? Palette.Cached : Palette.Text);
            ui.KV("Generated / loaded from disk", $"{_generated} / {_loadedFromDisk}");
            ui.KV("Chunks in region files", _saved.Count.ToString());
            ui.KV("Workers busy", $"{_gen.InFlight.Count} / {_gen.MaxInFlight}", Palette.Generating);
            ui.KV("Time stuck at the void", $"{_voidSeconds:0.0}s", _voidSeconds > 0 ? Palette.Warning : Palette.Text);
        }

        public override bool HasMinimap => true;

        public override void DrawMinimap(HudUi ui, Rect r)
        {
            if (Event.current.type != EventType.Repaint) return;
            var pos = Ctx.Player.transform.position;
            var pc = new Vector2(pos.x / Size, pos.z / Size);
            float span = (_renderDistance + 4) * 2f;
            float cs = Mathf.Min(r.width, r.height) / span;
            Vector2 center = new Vector2(r.width * 0.5f, r.height * 0.5f);

            // Saved (visited) chunks in faint blue, so you can see the "explored" trail.
            foreach (var c in _saved)
            {
                Vector2 d = new Vector2(c.x + 0.5f - pc.x, c.y + 0.5f - pc.y);
                if (Mathf.Abs(d.x) > span * 0.5f || Mathf.Abs(d.y) > span * 0.5f) continue;
                ui.Fill(new Rect(center.x + d.x * cs - cs * 0.5f, center.y - d.y * cs - cs * 0.5f, cs, cs), Palette.Cached.WithAlpha(0.18f));
            }
            foreach (var ch in _chunks.Values)
            {
                Vector2 d = new Vector2(ch.C.x + 0.5f - pc.x, ch.C.y + 0.5f - pc.y);
                if (Mathf.Abs(d.x) > span * 0.5f || Mathf.Abs(d.y) > span * 0.5f) continue;
                Color c = ch.State switch
                {
                    ChunkState.Loaded => Palette.Loaded,
                    ChunkState.QueuedGen => ch.Request.IsInFlight ? Palette.Generating : Palette.Queued.WithAlpha(0.6f),
                    ChunkState.QueuedDisk => ch.Request.IsInFlight ? Palette.Cached : Palette.Queued.WithAlpha(0.6f),
                    _ => Palette.Unloaded,
                };
                ui.Fill(new Rect(center.x + d.x * cs - cs * 0.5f + 0.5f, center.y - d.y * cs - cs * 0.5f + 0.5f, cs - 1, cs - 1), c);
            }
            ui.Pill(new Rect(center.x - 4, center.y - 4, 8, 8), _inVoid ? Palette.Unloading : Palette.Player);
        }

        // =====================================================================================
        //  Worldgen (deterministic from the seed)
        // =====================================================================================

        int Height(int x, int z)
        {
            float n = Noise.Fbm(x / 70f + _seed, z / 70f - _seed, 4);
            float m = Noise.Ridged(x / 140f, z / 140f, 3, _seed * 0.1f);
            return Mathf.FloorToInt(4 + n * 18 + m * m * 26);
        }

        GameObject BuildChunk(Vector2Int c)
        {
            var mb = _mb;
            mb.Clear();
            int x0 = c.x * Size, z0 = c.y * Size;
            for (int i = 0; i < Size + 2; i++)
            for (int j = 0; j < Size + 2; j++)
                _heights[i, j] = Height(x0 + i - 1, z0 + j - 1);

            var rng = Noise.Rng(c.x, c.y, _seed);
            for (int i = 0; i < Size; i++)
            for (int j = 0; j < Size; j++)
            {
                int h = _heights[i + 1, j + 1];
                float x = x0 + i, z = z0 + j;
                if (h < Water)
                {
                    mb.FlatQuad(new Rect(x, z, 1, 1), Water, Palette.Hex("#2f6fb0").Jitter(rng, 0.03f).Glow(0.12f));
                    continue;
                }

                Color top = h <= Water + 1 ? Palette.Hex("#d9c98c")
                          : h > 34 ? Palette.Hex("#f2f5fa")
                          : h > 27 ? Palette.Hex("#8c8f96")
                          : Palette.Hex("#5fa83f");
                Color side = h > 27 ? Palette.Hex("#6f7279") : h <= Water + 1 ? Palette.Hex("#c2b47a") : Palette.Hex("#7a5a3a");
                top = top.Jitter(rng, 0.05f).Glow(0);
                side = side.Glow(0);
                mb.FlatQuad(new Rect(x, z, 1, 1), h, top);

                // Exposed sides down to each neighbour's (or water) height.
                SideIfExposed(mb, x, z, h, Mathf.Max(_heights[i + 2, j + 1], Water), Vector3.right, side);
                SideIfExposed(mb, x, z, h, Mathf.Max(_heights[i, j + 1], Water), Vector3.left, side);
                SideIfExposed(mb, x, z, h, Mathf.Max(_heights[i + 1, j + 2], Water), Vector3.forward, side);
                SideIfExposed(mb, x, z, h, Mathf.Max(_heights[i + 1, j], Water), Vector3.back, side);

                // Trees on grass, kept inside the chunk so they never straddle a border.
                if (h > Water + 1 && h < 26 && i > 1 && i < Size - 2 && j > 1 && j < Size - 2 && rng.Chance(0.018f))
                {
                    Vector3 b = new Vector3(x + 0.5f, h, z + 0.5f);
                    mb.Box(b + Vector3.up * 2f, new Vector3(0.9f, 4f, 0.9f), Palette.Hex("#6b4a2b").Glow(0));
                    mb.Box(b + Vector3.up * 4.5f, new Vector3(3f, 2f, 3f), Palette.Hex("#3d8a34").Jitter(rng, 0.1f).Glow(0));
                    mb.Box(b + Vector3.up * 6f, new Vector3(1.8f, 1f, 1.8f), Palette.Hex("#46993b").Glow(0));
                }
            }
            var mr = DemoAssets.Spawn($"chunk({c.x},{c.y})", Root, mb.ToMesh("Chunk"), Ctx.Assets.Lit);
            return mr.gameObject;
        }

        static void SideIfExposed(MeshBuilder mb, float x, float z, int h, int hn, Vector3 dir, Color col)
        {
            if (hn >= h) return;
            float x0 = x, x1 = x + 1, z0 = z, z1 = z + 1;
            Vector3 a, b;
            if (dir == Vector3.right) { a = new Vector3(x1, 0, z0); b = new Vector3(x1, 0, z1); }
            else if (dir == Vector3.left) { a = new Vector3(x0, 0, z0); b = new Vector3(x0, 0, z1); }
            else if (dir == Vector3.forward) { a = new Vector3(x0, 0, z1); b = new Vector3(x1, 0, z1); }
            else { a = new Vector3(x0, 0, z0); b = new Vector3(x1, 0, z0); }
            mb.Quad(a + Vector3.up * hn, b + Vector3.up * hn, b + Vector3.up * h, a + Vector3.up * h, col, dir);
        }
    }
}
