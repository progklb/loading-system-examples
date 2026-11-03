using System.Collections.Generic;
using UnityEngine;

namespace StreamingDemo
{
    /// <summary>
    /// 07 · WORLD PARTITION  (Unreal Engine 5 - Fortnite, The Matrix Awakens...)
    ///
    /// The modern, engine-level take that combines most of the previous ideas:
    ///   * ONE world; the editor slices actors into grid cells automatically.
    ///   * Several RUNTIME GRIDS with different cell sizes and loading ranges:
    ///     landmarks stream from far away, small props only close up.
    ///   * HLOD: for every cell, a pre-built merged proxy mesh shows the world
    ///     beyond the loading range (Skyrim's distant LOD, automated).
    ///   * DATA LAYERS: whole sets of actors (an event, a time of day) can be
    ///     switched on and off at runtime, and stream like everything else.
    ///   * STREAMING SOURCES: the player is just one source. Add another at a
    ///     teleport destination and it is pre-warmed before you arrive.
    /// </summary>
    public sealed class WorldPartitionMode : DemoMode
    {
        public override string Number => "07";
        public override string Title => "World Partition";
        public override string ShortName => "Partition";
        public override string GameRef => "Unreal Engine 5  ·  Fortnite, The Matrix Awakens";
        public override string Tagline => "One world, sliced automatically into several grids by object size, with merged HLOD proxies beyond range, switchable data layers and multiple streaming sources.";
        public override string[] HowItWorks => new[]
        {
            "Designers build one big world. At cook time every actor is assigned to a cell in a runtime grid.",
            "Several grids, each with its own cell size and loading range: landmarks from far away, foliage only close up.",
            "A cell is loaded when it intersects the loading range circle of ANY streaming source.",
            "Outside the range, HLOD (merged, simplified proxies built offline) stands in.",
            "Data layers are sets of actors you can enable at runtime (a festival, night-time props). They stream like everything else.",
            "Streaming sources are not just the player. Put one at a teleport target to pre-warm it.",
        };

        public override (Color color, string label)[] Legend => new[]
        {
            (GridColors[0], "Landmarks grid"),
            (GridColors[1], "Main grid"),
            (GridColors[2], "Detail grid"),
            (Palette.Lod, "HLOD proxy"),
            (Palette.Loading, "Loading"),
            (Palette.Generating, "Data layer"),
        };

        // ------------------------------------------------------------------ data

        static readonly Color[] GridColors = { Palette.Hex("#c77dff"), Palette.Hex("#3ddc97"), Palette.Hex("#4ec9ff") };

        sealed class RuntimeGrid
        {
            public string Name;
            public float CellSize;
            public float Range;
            public int Index;
        }

        enum CellState { Unloaded, Queued, Loaded }

        sealed class Cell
        {
            public RuntimeGrid Grid;
            public Vector2Int Coord;
            public bool DataLayer;         // belongs to the "Festival" data layer
            public float SizeMB;
            public CellState State;
            public LoadRequest Request;
            public GameObject Content;
            public GameObject Hlod;        // only main grid cells have HLOD
            public Rect Rect;
        }

        const float Half = 512f;

        readonly List<RuntimeGrid> _grids = new List<RuntimeGrid>();
        readonly List<Cell> _cells = new List<Cell>();
        readonly List<Vector3> _sources = new List<Vector3>();
        readonly MeshBuilder _mb = new MeshBuilder();
        Transform[] _rings;
        Transform _prewarmMarker;

        bool _hlod = true;
        bool _festival;
        bool _prewarm = true;
        float _rangeScale = 1f;

        // Teleport pre-warm state.
        bool _warming;
        Vector3 _warmTarget;
        float _warmStarted;
        float _lastWarmSeconds = -1;
        int _holesOnArrival = -1;

        // =====================================================================================
        //  Setup
        // =====================================================================================

        protected override void OnEnter()
        {
            Ctx.Disk.Name = "NVMe SSD";
            Ctx.Disk.BandwidthMBps = 60f;
            Ctx.Disk.SeekMs = 2f;
            Ctx.Disk.MaxInFlight = 4;
            Ctx.Memory.BudgetMB = 768f;
            Ctx.SetFog(450f, 1100f);
            Ctx.App.SetShadowDistance(300f);
            Ctx.Cam.farClipPlane = 2500f;

            _grids.Clear(); _cells.Clear(); _sources.Clear();
            _warming = false; _lastWarmSeconds = -1; _holesOnArrival = -1;

            _grids.Add(new RuntimeGrid { Name = "Landmarks", CellSize = 256f, Range = 450f, Index = 0 });
            _grids.Add(new RuntimeGrid { Name = "Main", CellSize = 64f, Range = 220f, Index = 1 });
            _grids.Add(new RuntimeGrid { Name = "Detail", CellSize = 32f, Range = 110f, Index = 2 });

            var floor = DemoAssets.Spawn("Ground", Root, DemoAssets.UnitQuad, Ctx.Assets.Grid(64, 4, Palette.Hex("#0d1420"), Palette.Hex("#22324e")), false);
            floor.transform.localScale = new Vector3(Half * 2, 1, Half * 2);
            var outer = DemoAssets.Spawn("Outside", Root, DemoAssets.UnitQuad, Ctx.Assets.Grid(64, 4, Palette.Hex("#05070d"), Palette.Hex("#0d1424")), false);
            outer.transform.position = new Vector3(0, -0.5f, 0);
            outer.transform.localScale = new Vector3(8000, 1, 8000);

            // The "cook": slice the world into cells of every grid (+ the festival data layer on the main grid).
            foreach (var g in _grids)
            {
                int n = Mathf.RoundToInt(Half * 2 / g.CellSize);
                for (int x = 0; x < n; x++)
                for (int z = 0; z < n; z++)
                {
                    var rect = new Rect(-Half + x * g.CellSize, -Half + z * g.CellSize, g.CellSize, g.CellSize);
                    var rng = Noise.Rng(x, z, 50 + g.Index);
                    var cell = new Cell { Grid = g, Coord = new Vector2Int(x, z), Rect = rect, SizeMB = g.Index switch { 0 => 12f, 1 => rng.Range(5f, 10f), _ => rng.Range(0.8f, 2f) } };
                    _cells.Add(cell);
                    if (g.Index == 1)
                    {
                        cell.Hlod = BuildHlod(cell);
                        _cells.Add(new Cell { Grid = g, Coord = cell.Coord, Rect = rect, DataLayer = true, SizeMB = 2f });
                    }
                }
            }

            // Range rings for the player source.
            _rings = new Transform[_grids.Count];
            for (int i = 0; i < _grids.Count; i++)
            {
                var mb = new MeshBuilder();
                mb.Ring(Vector3.zero, 0.99f, 1f, 128, Color.white);
                _rings[i] = DemoAssets.Spawn($"Range {_grids[i].Name}", Root, mb.ToMesh("Ring"), Ctx.Assets.Glow(GridColors[i].WithAlpha(0.7f)), false).transform;
            }
            {
                var mb = new MeshBuilder();
                mb.Prism(Vector3.zero, 3f, 60f, 8, Color.white);
                _prewarmMarker = DemoAssets.Spawn("StreamingSource (prewarm)", Root, mb.ToMesh("Beacon"), Ctx.Assets.Glow(Palette.Queued.WithAlpha(0.35f), 1.5f, 1f), false).transform;
                _prewarmMarker.gameObject.SetActive(false);
            }

            var p = Ctx.Player;
            p.Speed = 24f;
            p.SetPath(new[]
            {
                new Vector3(-380, 0, -380), new Vector3(380, 0, -360), new Vector3(300, 0, 0), new Vector3(380, 0, 380),
                new Vector3(0, 0, 300), new Vector3(-380, 0, 380), new Vector3(-300, 0, 0),
            }, true, 1);
            p.Teleport(new Vector3(-380, 0, -380));
            Ctx.Rig.SetView(55f, 330f, 20f);
        }

        // =====================================================================================
        //  CORE ALGORITHM - cells x grids x streaming sources
        // =====================================================================================

        public override void Tick(float dt)
        {
            var p = Ctx.Player;

            // 1) Gather streaming sources: the player, plus an optional pre-warm source at a teleport target.
            _sources.Clear();
            _sources.Add(Flat(p.transform.position));
            if (_warming) _sources.Add(_warmTarget);

            // 2) A cell is wanted if it intersects ANY source's loading range for its grid.
            foreach (var c in _cells)
            {
                if (c.DataLayer && !_festival) { if (c.State != CellState.Unloaded) Release(c); continue; }

                float range = c.Grid.Range * _rangeScale;
                float best = float.MaxValue;
                foreach (var s in _sources) best = Mathf.Min(best, DistanceToRect(s, c.Rect));
                bool wanted = best <= range;

                if (wanted && c.State == CellState.Unloaded)
                {
                    var captured = c;
                    c.State = CellState.Queued;
                    // Bigger grids first (landmarks shape the skyline), then by distance.
                    c.Request = Ctx.Disk.Enqueue($"{c.Grid.Name}{(c.DataLayer ? "/Festival" : "")}({c.Coord.x},{c.Coord.y})", c.SizeMB, c.Grid.Index * 0.25f + best / range, _ => OnLoaded(captured), c);
                }
                else if (wanted && c.State == CellState.Queued && c.Request.State == RequestState.Queued)
                {
                    c.Request.Priority = c.Grid.Index * 0.25f + best / range;
                }
                else if (!wanted && c.State != CellState.Unloaded)
                {
                    Release(c);
                }
            }

            // 3) Teleport pre-warm: wait until the destination's cells are resident, then jump.
            if (_warming && DestinationReady(_warmTarget))
            {
                _warming = false;
                _lastWarmSeconds = Time.time - _warmStarted;
                DoTeleport(_warmTarget);
                Ctx.Banner($"Destination pre-warmed in {_lastWarmSeconds:0.0}s → seamless arrival", Palette.Loaded);
            }

            UpdateVisuals();
        }

        void OnLoaded(Cell c)
        {
            if (c.State != CellState.Queued) return;
            c.State = CellState.Loaded;
            Ctx.Memory.Allocate(c.SizeMB);
            c.Content = BuildCell(c);
            if (c.Content != null)
            {
                Tweens.PopIn(c.Content.transform, 0.4f);
                Tweens.Flash(c.Content.GetComponent<Renderer>(), (c.DataLayer ? Palette.Generating : GridColors[c.Grid.Index]) * 0.5f, 0.7f);
            }
            if (c.Hlod != null) c.Hlod.SetActive(false);
        }

        void Release(Cell c)
        {
            if (c.State == CellState.Queued) Ctx.Disk.Cancel(c.Request);
            if (c.State == CellState.Loaded)
            {
                Ctx.Memory.Free(c.SizeMB);
                if (c.Content != null) Tweens.SinkAndDestroy(c.Content, 0.3f);
                c.Content = null;
            }
            c.State = CellState.Unloaded;
            if (c.Hlod != null) c.Hlod.SetActive(_hlod);
        }

        bool DestinationReady(Vector3 target)
        {
            foreach (var c in _cells)
            {
                if (c.DataLayer && !_festival) continue;
                if (c.Grid.Index == 0) continue;
                if (DistanceToRect(target, c.Rect) <= c.Grid.Range * _rangeScale * 0.5f && c.State != CellState.Loaded) return false;
            }
            return true;
        }

        int CountHoles(Vector3 at)
        {
            int holes = 0;
            foreach (var c in _cells)
                if (!c.DataLayer && c.Grid.Index > 0 && DistanceToRect(at, c.Rect) <= c.Grid.Range * _rangeScale * 0.5f && c.State != CellState.Loaded) holes++;
            return holes;
        }

        void StartTeleport()
        {
            var pos = Flat(Ctx.Player.transform.position);
            // Jump to the mirrored point on the far side of the map.
            var target = new Vector3(-pos.x, 0, -pos.z);
            if (target.magnitude < 200f) target = new Vector3(Mathf.Sign(-pos.x + 0.01f) * 350f, 0, Mathf.Sign(-pos.z + 0.01f) * 350f);

            if (_prewarm)
            {
                _warming = true;
                _warmTarget = target;
                _warmStarted = Time.time;
                Ctx.Banner("Streaming source placed at destination · pre-warming", Palette.Queued);
                Ctx.Log("Prewarm source added", Palette.Queued);
            }
            else
            {
                DoTeleport(target);
                _holesOnArrival = CountHoles(target);
                Ctx.Banner($"Teleported cold: {_holesOnArrival} cells missing on arrival", Palette.Unloading);
            }
        }

        void DoTeleport(Vector3 target)
        {
            Ctx.Player.Teleport(target);
            Ctx.Player.Autopilot = false;
            Ctx.Rig.Snap();
            if (_prewarm) _holesOnArrival = CountHoles(target);
            Ctx.Log($"TELEPORT → ({target.x:0}, {target.z:0}), {_holesOnArrival} holes", _holesOnArrival > 0 ? Palette.Unloading : Palette.Loaded);
        }

        static float DistanceToRect(Vector3 p, Rect r)
        {
            float dx = Mathf.Max(r.xMin - p.x, 0, p.x - r.xMax);
            float dz = Mathf.Max(r.yMin - p.z, 0, p.z - r.yMax);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // =====================================================================================
        //  Visualisation & HUD
        // =====================================================================================

        void UpdateVisuals()
        {
            Vector3 pos = Flat(Ctx.Player.transform.position);
            for (int i = 0; i < _grids.Count; i++)
            {
                _rings[i].position = pos + Vector3.up * (0.3f + i * 0.1f);
                _rings[i].localScale = Vector3.one * _grids[i].Range * _rangeScale;
            }
            _prewarmMarker.gameObject.SetActive(_warming);
            if (_warming) _prewarmMarker.position = _warmTarget;
        }

        public override void OnWorldGUI(HudUi ui)
        {
            if (_warming)
                ui.WorldLabel(Ctx.Cam, _warmTarget + Vector3.up * 66f, "Streaming Source (pre-warm)", Palette.Queued);
        }

        public override void DrawSettings(HudUi ui)
        {
            ui.Header("World Partition");
            _rangeScale = ui.Slider("Loading range scale", _rangeScale, 0.4f, 2f, "×{0:0.00}");
            bool h = ui.Toggle("HLOD proxies beyond range", _hlod);
            if (h != _hlod)
            {
                _hlod = h;
                foreach (var c in _cells) if (c.Hlod != null) c.Hlod.SetActive(_hlod && c.State != CellState.Loaded);
            }
            bool f = ui.Toggle("Data layer: \"Festival\"", _festival);
            if (f != _festival) { _festival = f; Ctx.Log($"Data layer Festival {(f ? "ACTIVATED" : "deactivated")}", Palette.Generating); }
            _prewarm = ui.Toggle("Pre-warm teleport destination", _prewarm);
            if (ui.ButtonRow(_warming ? "Pre-warming…" : "Teleport to the far side") && !_warming) StartTeleport();
        }

        public override void DrawStats(HudUi ui)
        {
            ui.Header("Runtime grids");
            foreach (var g in _grids)
            {
                int total = 0, loaded = 0;
                foreach (var c in _cells) if (c.Grid == g && !c.DataLayer) { total++; if (c.State == CellState.Loaded) loaded++; }
                ui.KV($"{g.Name}  ({g.CellSize:0} u cells, {g.Range * _rangeScale:0} u range)", $"{loaded}/{total}", GridColors[g.Index]);
            }
            int fl = 0;
            foreach (var c in _cells) if (c.DataLayer && c.State == CellState.Loaded) fl++;
            ui.KV("Festival data layer cells", _festival ? fl.ToString() : "off", Palette.Generating);
            ui.KV("Streaming sources", _sources.Count.ToString());
            if (_lastWarmSeconds >= 0) ui.KV("Last pre-warm", $"{_lastWarmSeconds:0.0}s", Palette.Queued);
            if (_holesOnArrival >= 0) ui.KV("Missing cells on arrival", _holesOnArrival.ToString(), _holesOnArrival > 0 ? Palette.Warning : Palette.Loaded);
        }

        public override bool HasMinimap => true;

        public override void DrawMinimap(HudUi ui, Rect r)
        {
            if (Event.current.type != EventType.Repaint) return;
            float size = Mathf.Min(r.width, r.height);
            float ox = (r.width - size) * 0.5f;
            Rect M(Rect w) => new Rect(ox + (w.xMin / (Half * 2) + 0.5f) * size, (0.5f - w.yMax / (Half * 2)) * size, w.width / (Half * 2) * size, w.height / (Half * 2) * size);
            Vector2 P(Vector3 w) => new Vector2(ox + (w.x / (Half * 2) + 0.5f) * size, (0.5f - w.z / (Half * 2)) * size);

            // Draw coarse to fine so the detail grid sits on top.
            foreach (var c in _cells)
            {
                if (c.DataLayer || c.State == CellState.Unloaded) continue;
                var mr = M(c.Rect);
                Color col = c.State == CellState.Loaded ? GridColors[c.Grid.Index] : Palette.Loading;
                if (c.Grid.Index == 0) ui.Frame(mr, col.WithAlpha(0.9f), 2f);
                else ui.Fill(new Rect(mr.x + 0.5f, mr.y + 0.5f, mr.width - 1, mr.height - 1), col.WithAlpha(c.Grid.Index == 1 ? 0.3f : 0.55f));
            }
            foreach (var s in _sources)
            {
                var sp = P(s);
                ui.Pill(new Rect(sp.x - 4, sp.y - 4, 8, 8), s == _sources[0] ? Palette.Player : Palette.Queued);
            }
        }

        // =====================================================================================
        //  Content - all derived from world position, like a real cooked world
        // =====================================================================================

        static float Village(float x, float z) => Noise.Fbm(x / 260f + 9, z / 260f + 2, 3);

        GameObject BuildCell(Cell c)
        {
            var mb = _mb;
            mb.Clear();
            var rng = Noise.Rng(c.Coord.x, c.Coord.y, 70 + c.Grid.Index + (c.DataLayer ? 9 : 0));
            Rect r = c.Rect;

            if (c.DataLayer)
            {
                // Festival: tents + strings of lanterns, only in village areas.
                if (Village(r.center.x, r.center.y) < 0.5f) return null;
                for (int i = 0; i < 3; i++)
                {
                    Vector3 p = new Vector3(rng.Range(r.xMin + 8, r.xMax - 8), 0, rng.Range(r.yMin + 8, r.yMax - 8));
                    mb.Cone(p, 4f, 5f, 6, Palette.Hex(i % 2 == 0 ? "#ff5d73" : "#ffd23f").Glow(0.15f));
                    for (int k = 0; k < 6; k++)
                        mb.Sphere(p + new Vector3(Mathf.Cos(k) * 7f, 4f + Mathf.Sin(k * 2) * 0.5f, Mathf.Sin(k) * 7f), 0.5f, Palette.Hex("#ffb35c").Glow(1f), 3, 5);
                }
            }
            else switch (c.Grid.Index)
            {
                case 0: // landmarks: one tall tower or monolith per big cell
                {
                    Vector3 p = new Vector3(r.center.x + rng.Range(-60, 60), 0, r.center.y + rng.Range(-60, 60));
                    float h = rng.Range(50, 90);
                    mb.Prism(p, 7f, h, 8, Palette.Hex("#8f86a8").Glow(0), true, 4f);
                    mb.Prism(p + Vector3.up * h, 5f, 6f, 8, Palette.Hex("#c77dff").Glow(0.9f), true, 2f);
                    break;
                }
                case 1: // main: houses in villages
                {
                    float v = Village(r.center.x, r.center.y);
                    int houses = v > 0.5f ? rng.Next(3, 7) : (rng.Chance(0.2f) ? 1 : 0);
                    for (int i = 0; i < houses; i++)
                    {
                        Vector3 p = new Vector3(rng.Range(r.xMin + 8, r.xMax - 8), 0, rng.Range(r.yMin + 8, r.yMax - 8));
                        float yaw = rng.Next(0, 4) * 90f;
                        Vector3 s = new Vector3(rng.Range(7, 12), rng.Range(5, 9), rng.Range(7, 10));
                        mb.BoxOnGround(p, s, Palette.Hex("#d8c7a8").Jitter(rng, 0.1f).Glow(0), yaw);
                        mb.Cone(p + Vector3.up * s.y, Mathf.Max(s.x, s.z) * 0.62f, 3.5f, 4, Palette.Hex("#a6503c").Jitter(rng, 0.1f).Glow(0));
                        mb.Box(p + Quaternion.Euler(0, yaw, 0) * new Vector3(0, 2.5f, -s.z * 0.5f - 0.05f), new Vector3(2f, 1.5f, 0.1f), Palette.Hex("#ffd98a").Glow(0.8f), yaw);
                    }
                    break;
                }
                default: // detail: foliage and rocks, thinned out inside villages
                {
                    float v = Village(r.center.x, r.center.y);
                    int trees = v > 0.5f ? rng.Next(0, 3) : rng.Next(4, 10);
                    for (int i = 0; i < trees; i++)
                    {
                        Vector3 p = new Vector3(rng.Range(r.xMin + 2, r.xMax - 2), 0, rng.Range(r.yMin + 2, r.yMax - 2));
                        if (rng.Chance(0.5f)) mb.Tree(p, rng.Range(6, 11), Palette.Hex("#2f7a4a").Jitter(rng, 0.15f).Glow(0), Palette.Hex("#5a4632").Glow(0));
                        else mb.RoundTree(p, rng.Range(5, 9), Palette.Hex("#4c9a3f").Jitter(rng, 0.15f).Glow(0), Palette.Hex("#5a4632").Glow(0));
                    }
                    if (rng.Chance(0.4f))
                        mb.Box(new Vector3(rng.Range(r.xMin, r.xMax), 0.8f, rng.Range(r.yMin, r.yMax)), new Vector3(3, 1.6f, 2.5f), Palette.Hex("#7c7a76").Glow(0), rng.Range(0, 90));
                    break;
                }
            }

            if (mb.VertexCount == 0) return null;
            return DemoAssets.Spawn($"{c.Grid.Name}({c.Coord.x},{c.Coord.y})", Root, mb.ToMesh("Cell"), Ctx.Assets.Lit).gameObject;
        }

        GameObject BuildHlod(Cell c)
        {
            // HLOD = the cell's content merged and simplified offline: flat-coloured blocks, no detail.
            var mb = new MeshBuilder();
            Rect r = c.Rect;
            float v = Village(r.center.x, r.center.y);
            Color forest = Palette.Hex("#2c4a3a").Glow(0), roof = Palette.Hex("#6a5048").Glow(0);
            if (v > 0.5f)
            {
                var rng = Noise.Rng(c.Coord.x, c.Coord.y, 71);
                int houses = rng.Next(3, 7);
                for (int i = 0; i < houses; i++)
                {
                    Vector3 p = new Vector3(rng.Range(r.xMin + 8, r.xMax - 8), 0, rng.Range(r.yMin + 8, r.yMax - 8));
                    rng.Next(0, 4);
                    Vector3 s = new Vector3(rng.Range(7, 12), rng.Range(5, 9), rng.Range(7, 10));
                    rng.NextDouble(); rng.NextDouble(); // keep the RNG in step with BuildCell (colour jitter)
                    mb.BoxOnGround(p, s + Vector3.up * 3f, roof);
                }
            }
            else
            {
                // Forest canopy blob.
                mb.Box(new Vector3(r.center.x, 3f, r.center.y), new Vector3(r.width * 0.92f, 6f, r.height * 0.92f), forest);
            }
            var mr = DemoAssets.Spawn("HLOD", Root, mb.ToMesh("HLOD"), Ctx.Assets.Lit, false);
            return mr.gameObject;
        }
    }
}
