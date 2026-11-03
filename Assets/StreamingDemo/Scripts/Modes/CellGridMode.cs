using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StreamingDemo
{
    /// <summary>
    /// 02 · CELL GRID  (The Elder Scrolls IV/V - Gamebryo / Creation Engine)
    ///
    /// The exterior world is a uniform grid of "cells". The engine keeps an
    /// N x N block of cells (uGridsToLoad, default 5) centred on the player's
    /// cell fully loaded. Crossing a cell edge shifts the block by one: a whole
    /// row/column (N cells) is requested and the opposite row is dropped.
    ///
    /// Everything outside the block is represented by pre-baked distant LOD
    /// (low-res terrain), so the horizon never disappears.
    ///
    /// Interiors are separate cells in their own space: entering a door shows a
    /// loading screen, unloads the exterior grid and loads just that one interior.
    ///
    /// Extras shown here:
    ///  * Cell buffer (cache): recently dropped cells stay in memory so walking
    ///    back is instant. Try the "boundary wiggle" stress test with it on/off.
    ///  * Hitch: if the player reaches a cell that isn't loaded yet, the engine
    ///    has no choice but to block until it is - the infamous stutter.
    /// </summary>
    public sealed class CellGridMode : DemoMode
    {
        public override string Number => "02";
        public override string Title => "Cell Grid";
        public override string ShortName => "Cells";
        public override string GameRef => "The Elder Scrolls V: Skyrim  ·  Creation Engine";
        public override string Tagline => "A uniform grid of cells. Keep an N×N block around the player loaded; crossing a cell edge streams in one row and drops the opposite one.";
        public override string[] HowItWorks => new[]
        {
            "The worldspace is cut into equal square cells. Each cell is one streaming unit (terrain, objects, navmesh).",
            "uGridsToLoad = N keeps an N×N block around the player's cell resident (default 5×5 = 25 cells).",
            "Crossing a cell edge = N cells requested, N cells released. Loads are prioritised nearest-first.",
            "Beyond the grid, pre-baked low-res distant LOD keeps the horizon on screen.",
            "Interiors are separate cells: a door means a loading screen, the exterior grid is unloaded.",
            "If the player's own cell isn't ready, the game must block: a hitch.",
        };

        public override (Color color, string label)[] Legend => new[]
        {
            (Palette.Lod, "Distant LOD"),
            (Palette.Queued, "Queued"),
            (Palette.Loading, "Loading"),
            (Palette.Loaded, "Resident"),
            (Palette.Cached, "Cell buffer (cached)"),
            (Palette.Unloading, "Hitch"),
        };

        // ------------------------------------------------------------------ world constants
        const float CellSize = 40f;
        const int HalfWorld = 10;                 // cells -10..9 on each axis = 20x20 cells
        const float InteriorMB = 5f;

        enum CellState { Unloaded, Queued, Loading, Loaded, Cached }

        sealed class Cell
        {
            public Vector2Int Coord;
            public CellState State;
            public LoadRequest Request;
            public GameObject Content;
            public GameObject Lod;
            public float SizeMB;
            public float LastUsed;
            public float AvgHeight;
        }

        sealed class Door
        {
            public Vector3 Position;
            public int WaypointIndex;
            public string Name;
        }

        enum Phase { Exterior, LoadingInterior, Interior, LoadingExterior }

        // ------------------------------------------------------------------ settings (HUD)
        int _gridsToLoad = 5;
        bool _cellBuffer = true;
        int _cellBufferCapacity = 12;
        bool _wiggle;
        bool _showLod = true;

        // ------------------------------------------------------------------ state
        readonly Dictionary<Vector2Int, Cell> _cells = new Dictionary<Vector2Int, Cell>();
        readonly List<Door> _doors = new List<Door>();
        readonly List<Vector3> _exteriorPath = new List<Vector3>();
        readonly MeshBuilder _mb = new MeshBuilder();

        Vector2Int _playerCell = new Vector2Int(int.MinValue, 0);
        Phase _phase = Phase.Exterior;
        Door _activeDoor;
        LoadRequest _interiorRequest;
        GameObject _interior;
        float _phaseStarted;
        int _crossings, _hitches;
        float _hitchSeconds;
        bool _hitching;
        float _lastLoadScreenSeconds;

        Transform _cellsRoot, _lodRoot, _markersRoot;
        Transform _curtain;
        readonly Dictionary<Vector2Int, MeshRenderer> _markers = new Dictionary<Vector2Int, MeshRenderer>();

        static readonly Vector3 InteriorOrigin = new Vector3(0, 0, 5000);

        // =====================================================================================
        //  Setup
        // =====================================================================================

        protected override void OnEnter()
        {
            Ctx.Disk.Name = "DVD (console)";
            Ctx.Disk.BandwidthMBps = 12f;
            Ctx.Disk.SeekMs = 60f;
            Ctx.Disk.MaxInFlight = 2;
            Ctx.Memory.BudgetMB = 400f;
            Ctx.SetFog(330f, 820f);
            Ctx.App.SetShadowDistance(260f);
            Ctx.Cam.farClipPlane = 1500f;

            _cells.Clear(); _doors.Clear(); _markers.Clear();
            _phase = Phase.LoadingExterior;   // new game = loading screen until the first grid is in
            _phaseStarted = Time.time;
            _playerCell = new Vector2Int(int.MinValue, 0);
            _crossings = _hitches = 0;
            _hitchSeconds = 0;
            _wiggle = false;

            _cellsRoot = new GameObject("Cells").transform; _cellsRoot.SetParent(Root, false);
            _lodRoot = new GameObject("DistantLOD").transform; _lodRoot.SetParent(Root, false);
            _markersRoot = new GameObject("Markers").transform; _markersRoot.SetParent(Root, false);

            // Floor under everything, with the cell grid baked in.
            var floor = DemoAssets.Spawn("Floor", Root, DemoAssets.UnitQuad, Ctx.Assets.Grid(CellSize, 5, Palette.Hex("#0a1020"), Palette.Hex("#1d3150")), false);
            floor.transform.position = new Vector3(0, -2f, 0);
            floor.transform.localScale = new Vector3(4000, 1, 4000);

            BuildRouteAndDoors();

            // Every cell exists as data + a distant-LOD proxy from the start (LOD is tiny and always resident).
            for (int x = -HalfWorld; x < HalfWorld; x++)
            for (int z = -HalfWorld; z < HalfWorld; z++)
            {
                var c = new Vector2Int(x, z);
                var rng = Noise.Rng(x, z, 2);
                var cell = new Cell { Coord = c, SizeMB = rng.Range(4f, 9f), AvgHeight = Height(CellCenter(c).x, CellCenter(c).z) };
                cell.Lod = BuildLod(c);
                _cells[c] = cell;
            }

            BuildCurtain();

            var p = Ctx.Player;
            p.Speed = 16f;
            p.SetPath(_exteriorPath, true, 0);
            p.Teleport(Ground(_exteriorPath[_exteriorPath.Count - 1]));
            p.WaypointReached += OnWaypoint;
            p.PathFinished += OnPathFinished;
            Ctx.Rig.SetView(55f, 240f, 25f);
        }

        protected override void OnExit()
        {
            Ctx.Player.WaypointReached -= OnWaypoint;
            Ctx.Player.PathFinished -= OnPathFinished;
            Ctx.Player.Frozen = false;
        }

        void BuildRouteAndDoors()
        {
            _exteriorPath.Clear();
            _exteriorPath.AddRange(new[]
            {
                new Vector3(-290, 0, -260), new Vector3(-60, 0, -300), new Vector3(220, 0, -250),
                new Vector3(300, 0, -40),   // door 0
                new Vector3(250, 0, 210), new Vector3(40, 0, 320), new Vector3(-200, 0, 270),
                new Vector3(-310, 0, 60),   // door 1
                new Vector3(-150, 0, -60), new Vector3(-320, 0, -150),
            });
            _doors.Add(new Door { Position = _exteriorPath[3], WaypointIndex = 3, Name = "Riverside cottage" });
            _doors.Add(new Door { Position = _exteriorPath[7], WaypointIndex = 7, Name = "Hunter's lodge" });
        }

        // =====================================================================================
        //  CORE ALGORITHM - the N x N grid
        // =====================================================================================

        void UpdateGrid()
        {
            Vector2Int pc = WorldToCell(Ctx.Player.transform.position);
            if (pc != _playerCell)
            {
                if (_playerCell.x != int.MinValue)
                {
                    _crossings++;
                    Ctx.Log($"Crossed into cell ({pc.x},{pc.y})", Palette.Accent);
                }
                _playerCell = pc;
            }

            int r = (_gridsToLoad - 1) / 2;
            int newlyQueued = 0;

            // 1) DESIRED SET: every cell within r of the player's cell (a square, not a circle).
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (!_cells.TryGetValue(new Vector2Int(pc.x + dx, pc.y + dz), out var cell)) continue; // edge of the world
                cell.LastUsed = Time.time;

                switch (cell.State)
                {
                    case CellState.Unloaded:
                        // 2) REQUEST what is missing. Priority = Chebyshev distance, player cell first.
                        cell.State = CellState.Queued;
                        var captured = cell;
                        cell.Request = Ctx.Disk.Enqueue($"cell({cell.Coord.x},{cell.Coord.y})", cell.SizeMB, 0, req => OnCellLoaded(captured));
                        newlyQueued++;
                        break;
                    case CellState.Cached:
                        // Cell buffer hit: data is still in memory, re-show it for free.
                        cell.State = CellState.Loaded;
                        cell.Content.SetActive(true);
                        cell.Lod.SetActive(false);
                        Ctx.Log($"Cache hit  cell({cell.Coord.x},{cell.Coord.y})", Palette.Cached);
                        break;
                }

                if (cell.Request != null && cell.Request.IsPending)
                {
                    float dist = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) + (Mathf.Abs(dx) + Mathf.Abs(dz)) * 0.01f;
                    cell.Request.Priority = (dx == 0 && dz == 0) ? -1000 : dist;
                }
            }

            if (newlyQueued > 0 && _crossings > 0)
                Ctx.Banner($"CELL EDGE CROSSED  →  {newlyQueued} cells requested", Palette.Queued, 1.6f);

            // 3) EVICT everything outside the block.
            foreach (var cell in _cells.Values)
            {
                bool inGrid = Mathf.Abs(cell.Coord.x - pc.x) <= r && Mathf.Abs(cell.Coord.y - pc.y) <= r;
                if (inGrid) continue;

                if (cell.State == CellState.Queued || cell.State == CellState.Loading)
                {
                    Ctx.Disk.Cancel(cell.Request);
                    cell.State = CellState.Unloaded;
                }
                else if (cell.State == CellState.Loaded)
                {
                    if (_cellBuffer && _cellBufferCapacity > 0)
                    {
                        // Keep it in memory, just stop presenting it.
                        cell.State = CellState.Cached;
                        cell.Content.SetActive(false);
                        cell.Lod.SetActive(_showLod);
                    }
                    else UnloadCell(cell);
                }
            }

            // 4) Cell buffer is an LRU with fixed capacity.
            EnforceCellBuffer();

            // 5) HITCH: the player stands in a cell that isn't resident yet. Nothing to stand on: block.
            var mine = _cells.TryGetValue(pc, out var mc) ? mc : null;
            bool hitch = _phase == Phase.Exterior && mine != null && mine.State != CellState.Loaded;
            if (hitch && !_hitching)
            {
                _hitches++;
                Ctx.Banner("HITCH!  Player cell not loaded - blocking", Palette.Unloading, 1.5f);
                Ctx.Log($"HITCH waiting for cell({pc.x},{pc.y})", Palette.Unloading);
            }
            _hitching = hitch;
            Ctx.Player.Frozen = hitch;
            if (hitch) _hitchSeconds += Time.deltaTime;
        }

        void OnCellLoaded(Cell cell)
        {
            if (cell.State != CellState.Queued && cell.State != CellState.Loading) return;
            cell.State = CellState.Loaded;
            Ctx.Memory.Allocate(cell.SizeMB);
            cell.Content = BuildCellContent(cell.Coord);
            cell.Lod.SetActive(false);
            Tweens.PopIn(cell.Content.transform, 0.5f);
            Tweens.Flash(cell.Content.GetComponent<Renderer>(), Palette.Loaded * 0.6f, 0.9f);
            Ctx.Log($"LOAD  cell({cell.Coord.x},{cell.Coord.y})  {cell.SizeMB:0.0} MB", Palette.Loaded);
        }

        void UnloadCell(Cell cell)
        {
            if (cell.Content != null)
            {
                cell.Content.SetActive(true);
                Tweens.SinkAndDestroy(cell.Content, 0.35f);
            }
            cell.Content = null;
            Ctx.Memory.Free(cell.SizeMB);
            cell.State = CellState.Unloaded;
            cell.Lod.SetActive(_showLod);
            Ctx.Log($"FREE  cell({cell.Coord.x},{cell.Coord.y})", Palette.Unloading);
        }

        void EnforceCellBuffer()
        {
            int cachedCount = 0;
            foreach (var c in _cells.Values) if (c.State == CellState.Cached) cachedCount++;
            int capacity = _cellBuffer ? _cellBufferCapacity : 0;
            while (cachedCount > capacity)
            {
                Cell oldest = null;
                foreach (var c in _cells.Values)
                    if (c.State == CellState.Cached && (oldest == null || c.LastUsed < oldest.LastUsed)) oldest = c;
                if (oldest == null) break;
                UnloadCell(oldest);
                cachedCount--;
            }
        }

        void UnloadExterior()
        {
            foreach (var cell in _cells.Values)
            {
                if (cell.Request != null && cell.Request.IsPending) Ctx.Disk.Cancel(cell.Request);
                if (cell.State == CellState.Loaded || cell.State == CellState.Cached)
                {
                    DemoAssets.DestroyWithMesh(cell.Content);
                    cell.Content = null;
                    Ctx.Memory.Free(cell.SizeMB);
                }
                cell.State = CellState.Unloaded;
                cell.Lod.SetActive(_showLod);
            }
        }

        // =====================================================================================
        //  Interiors: a completely separate cell behind a loading screen
        // =====================================================================================

        void OnWaypoint(int index)
        {
            if (_phase != Phase.Exterior || _wiggle) return;
            foreach (var d in _doors)
                if (d.WaypointIndex == index) { EnterInterior(d); return; }
        }

        void OnPathFinished()
        {
            if (_phase == Phase.Interior) ExitInterior();
        }

        void EnterInterior(Door door)
        {
            _activeDoor = door;
            _phase = Phase.LoadingInterior;
            _phaseStarted = Time.time;
            Ctx.Player.Frozen = true;
            Ctx.Log($"DOOR  → {door.Name}: unloading exterior", Palette.Queued);
            UnloadExterior();
            _interiorRequest = Ctx.Disk.Enqueue($"interior:{door.Name}", InteriorMB, -1, _ => OnInteriorLoaded());
        }

        void OnInteriorLoaded()
        {
            Ctx.Memory.Allocate(InteriorMB);
            _interior = BuildInterior();
            Tweens.PopIn(_interior.transform, 0.4f);
            Ctx.Log($"LOAD  interior  {InteriorMB:0.0} MB", Palette.Loaded);

            var p = Ctx.Player;
            p.Teleport(InteriorOrigin + new Vector3(0, 0, -9));
            p.SetPath(new[]
            {
                InteriorOrigin + new Vector3(-9, 0, -2), InteriorOrigin + new Vector3(-6, 0, 7),
                InteriorOrigin + new Vector3(8, 0, 6), InteriorOrigin + new Vector3(10, 0, -5),
                InteriorOrigin + new Vector3(0, 0, -9),
            }, false);
            Ctx.Rig.SetView(62f, 60f, Ctx.Rig.Yaw);
            _phase = Phase.Interior;
            _lastLoadScreenSeconds = Time.time - _phaseStarted;
            p.Frozen = false;
        }

        void ExitInterior()
        {
            _phase = Phase.LoadingExterior;
            _phaseStarted = Time.time;
            var p = Ctx.Player;
            p.Frozen = true;
            Ctx.Log("DOOR  → exterior: unloading interior", Palette.Queued);
            DemoAssets.DestroyWithMesh(_interior);
            _interior = null;
            Ctx.Memory.Free(InteriorMB);

            p.Teleport(Ground(_activeDoor.Position) + new Vector3(0, 0, -6));
            p.SetPath(_exteriorPath, true, (_activeDoor.WaypointIndex + 1) % _exteriorPath.Count);
            Ctx.Rig.SetView(55f, 240f, Ctx.Rig.Yaw);
            _playerCell = new Vector2Int(int.MinValue, 0);
        }

        bool ExteriorGridReady()
        {
            Vector2Int pc = WorldToCell(Ctx.Player.transform.position);
            int r = (_gridsToLoad - 1) / 2;
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
                if (_cells.TryGetValue(new Vector2Int(pc.x + dx, pc.y + dz), out var c) && c.State != CellState.Loaded)
                    return false;
            return true;
        }

        // =====================================================================================
        //  Tick
        // =====================================================================================

        public override void Tick(float dt)
        {
            var p = Ctx.Player;
            var kb = Keyboard.current;

            switch (_phase)
            {
                case Phase.Exterior:
                    UpdateGrid();
                    // Manual door use.
                    if (kb != null && kb.eKey.wasPressedThisFrame)
                        foreach (var d in _doors)
                            if (Vector3.Distance(Flat(d.Position), Flat(p.transform.position)) < 10f) { EnterInterior(d); break; }
                    break;

                case Phase.LoadingExterior:
                    UpdateGrid();
                    p.Frozen = true;
                    if (ExteriorGridReady())
                    {
                        _phase = Phase.Exterior;
                        _lastLoadScreenSeconds = Time.time - _phaseStarted;
                        p.Frozen = false;
                        Ctx.Log($"Exterior ready after {_lastLoadScreenSeconds:0.0}s loading screen", Palette.Loaded);
                    }
                    break;

                case Phase.Interior:
                    if (kb != null && kb.eKey.wasPressedThisFrame) ExitInterior();
                    break;
            }

            // Keep the player on the terrain.
            if (_phase == Phase.Exterior || _phase == Phase.LoadingExterior)
            {
                var pos = p.transform.position;
                pos.y = Mathf.Lerp(pos.y, Height(pos.x, pos.z), 1f - Mathf.Exp(-12f * dt));
                p.transform.position = pos;
            }

            UpdateMarkers();
            UpdateCurtain(dt);
        }

        void SetWiggle(bool on)
        {
            _wiggle = on;
            var p = Ctx.Player;
            if (on)
            {
                Vector3 pos = p.transform.position;
                float bx = Mathf.Round(pos.x / CellSize) * CellSize;
                p.SetPath(new[] { new Vector3(bx - 9, 0, pos.z), new Vector3(bx + 9, 0, pos.z) }, true);
                Ctx.Banner("STRESS TEST: walking back and forth over a cell edge", Palette.Accent);
            }
            else
            {
                // Restore the tour, resuming from whichever waypoint is closest.
                p.SetPath(_exteriorPath, true, 0);
                p.SetPath(_exteriorPath, true, p.NearestWaypoint());
                Ctx.Banner("Stress test off: back to the tour", Palette.Accent);
            }
        }

        // =====================================================================================
        //  Visualisation
        // =====================================================================================

        void UpdateMarkers()
        {
            // Hologram columns on queued / loading cells, rising with progress.
            foreach (var cell in _cells.Values)
            {
                bool want = cell.State == CellState.Queued || cell.State == CellState.Loading
                            || (cell.Request != null && cell.Request.IsInFlight);
                _markers.TryGetValue(cell.Coord, out var m);
                if (!want)
                {
                    if (m != null) { Object.Destroy(m.gameObject); _markers.Remove(cell.Coord); }
                    continue;
                }

                if (cell.Request != null && cell.Request.IsInFlight) cell.State = CellState.Loading;
                if (m == null)
                {
                    m = DemoAssets.Spawn("Marker", _markersRoot, DemoAssets.UnitCube, Ctx.Assets.Glow(Palette.Queued.WithAlpha(0.08f)), false);
                    _markers[cell.Coord] = m;
                }

                bool loading = cell.State == CellState.Loading;
                float h = loading ? Mathf.Lerp(2f, 40f, cell.Request.Progress) : 2f;
                Vector3 c = CellCenter(cell.Coord);
                m.transform.position = new Vector3(c.x, cell.AvgHeight + h * 0.5f, c.z);
                m.transform.localScale = new Vector3(CellSize - 2f, h, CellSize - 2f);
                m.sharedMaterial = loading
                    ? Ctx.Assets.Glow(Palette.Loading.WithAlpha(0.18f), 1.5f, 0.6f)
                    : Ctx.Assets.Glow(Palette.Queued.WithAlpha(0.12f), 1f, 0f, 4f);
            }
        }

        void BuildCurtain()
        {
            // A translucent "fence" around the loaded N x N block.
            var mb = new MeshBuilder();
            Color c = Palette.Loaded;
            float h = 1f;
            Vector3 a = new Vector3(-0.5f, 0, -0.5f), b = new Vector3(0.5f, 0, -0.5f), cc = new Vector3(0.5f, 0, 0.5f), d = new Vector3(-0.5f, 0, 0.5f);
            void Wall(Vector3 p0, Vector3 p1)
            {
                mb.Quad(p0, p1, p1 + Vector3.up * h, p0 + Vector3.up * h, c, Vector3.Cross(p1 - p0, Vector3.up));
            }
            Wall(a, b); Wall(b, cc); Wall(cc, d); Wall(d, a);
            var mr = DemoAssets.Spawn("LoadedGridCurtain", Root, mb.ToMesh("Curtain"), Ctx.Assets.Glow(Palette.Loaded.WithAlpha(0.13f), 0f, 1.2f), false);
            _curtain = mr.transform;
        }

        void UpdateCurtain(float dt)
        {
            bool show = _phase == Phase.Exterior || _phase == Phase.LoadingExterior;
            _curtain.gameObject.SetActive(show);
            if (!show || _playerCell.x == int.MinValue) return;
            Vector3 target = CellCenter(_playerCell);
            target.y = -5f;
            float size = _gridsToLoad * CellSize;
            _curtain.position = Vector3.Lerp(_curtain.position, target, 1f - Mathf.Exp(-6f * dt));
            _curtain.localScale = Vector3.Lerp(_curtain.localScale, new Vector3(size, 35f, size), 1f - Mathf.Exp(-6f * dt));
        }

        public override void OnWorldGUI(HudUi ui)
        {
            if (_phase == Phase.Exterior)
            {
                foreach (var d in _doors)
                    ui.WorldLabel(Ctx.Cam, Ground(d.Position) + Vector3.up * 16, $"DOOR → {d.Name}  (E)", Palette.Queued);

                foreach (var cell in _cells.Values)
                    if (cell.State == CellState.Loading && cell.Request != null)
                        ui.WorldLabel(Ctx.Cam, CellCenter(cell.Coord) + Vector3.up * (cell.AvgHeight + 45),
                            $"cell({cell.Coord.x},{cell.Coord.y})  {cell.Request.Progress * 100:0}%", Palette.Loading);
            }
            else if (_phase == Phase.Interior)
            {
                ui.WorldLabel(Ctx.Cam, InteriorOrigin + new Vector3(0, 8, -12), "INTERIOR CELL  ·  exterior grid unloaded", Palette.Accent);
            }
        }

        public override void DrawOverlay(HudUi ui)
        {
            if (_phase != Phase.LoadingInterior && _phase != Phase.LoadingExterior) return;
            // Classic full-screen loading screen.
            float t = Time.time - _phaseStarted;
            ui.Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0.01f, 0.015f, 0.03f, Mathf.Clamp01(t / 0.2f) * 0.94f));

            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
            var title = new Rect(0, cy - ui.S(90), Screen.width, ui.S(50));
            ui.Shadowed(title, "LOADING", ui.BannerStyle, Palette.Text);

            string what = _phase == Phase.LoadingInterior
                ? $"Interior cell: {_activeDoor.Name}"
                : $"Exterior worldspace: {_gridsToLoad}×{_gridsToLoad} = {_gridsToLoad * _gridsToLoad} cells";
            ui.Shadowed(new Rect(0, cy - ui.S(40), Screen.width, ui.S(24)), what, ui.Center, Palette.Accent);

            float progress;
            if (_phase == Phase.LoadingInterior) progress = _interiorRequest?.Progress ?? 0;
            else
            {
                Vector2Int pc = WorldToCell(Ctx.Player.transform.position);
                int r = (_gridsToLoad - 1) / 2, total = 0, done = 0;
                for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                    if (_cells.TryGetValue(new Vector2Int(pc.x + dx, pc.y + dz), out var c)) { total++; if (c.State == CellState.Loaded) done++; }
                progress = total == 0 ? 1 : done / (float)total;
            }

            var bar = new Rect(cx - ui.S(220), cy, ui.S(440), ui.S(10));
            ui.Pill(bar, Palette.PanelEdge);
            ui.Pill(new Rect(bar.x, bar.y, Mathf.Max(bar.height, bar.width * progress), bar.height), Palette.Loading);
            ui.Shadowed(new Rect(0, cy + ui.S(22), Screen.width, ui.S(20)), $"{t:0.0}s", ui.Center, Palette.TextDim);

            string tip = _phase == Phase.LoadingInterior
                ? "Interiors live in their own space. Only this one cell is loaded while you're inside."
                : "The game waits for the whole grid before handing control back. Bigger uGridsToLoad = longer wait.";
            ui.Shadowed(new Rect(0, Screen.height - ui.S(210), Screen.width, ui.S(24)), tip, ui.Center, Palette.TextDim);
        }

        public override void DrawSettings(HudUi ui)
        {
            ui.Header("Cell grid");
            int g = ui.IntSlider("uGridsToLoad", _gridsToLoad, 3, 9, "{0:0}", 2);
            if (g % 2 == 0) g++;
            if (g != _gridsToLoad)
            {
                _gridsToLoad = g;
                Ctx.Log($"uGridsToLoad = {g}  ({g * g} cells)", Palette.Accent);
            }
            _cellBuffer = ui.Toggle("Cell buffer (keep recent cells)", _cellBuffer);
            if (_cellBuffer) _cellBufferCapacity = ui.IntSlider("Buffer capacity", _cellBufferCapacity, 0, 40, "{0:0} cells");
            bool lod = ui.Toggle("Distant LOD", _showLod);
            if (lod != _showLod)
            {
                _showLod = lod;
                foreach (var c in _cells.Values) c.Lod.SetActive(_showLod && c.State != CellState.Loaded);
            }
            bool w = ui.Toggle("Stress test: wiggle over a cell edge", _wiggle);
            if (w != _wiggle && _phase == Phase.Exterior) SetWiggle(w);
        }

        public override void DrawStats(HudUi ui)
        {
            ui.Header("Cells");
            int loaded = 0, cached = 0, pending = 0;
            foreach (var c in _cells.Values)
            {
                if (c.State == CellState.Loaded) loaded++;
                else if (c.State == CellState.Cached) cached++;
                else if (c.State != CellState.Unloaded) pending++;
            }
            ui.KV("Player cell", _playerCell.x == int.MinValue ? "-" : $"({_playerCell.x}, {_playerCell.y})");
            ui.KV("Grid", $"{_gridsToLoad}×{_gridsToLoad} = {_gridsToLoad * _gridsToLoad} cells");
            ui.KV("Resident / cached / pending", $"{loaded} / {cached} / {pending}", Palette.Loaded);
            ui.KV("Edge crossings", _crossings.ToString());
            ui.KV("Hitches (time blocked)", $"{_hitches}  ({_hitchSeconds:0.0}s)", _hitches > 0 ? Palette.Warning : Palette.Text);
            if (_lastLoadScreenSeconds > 0) ui.KV("Last loading screen", $"{_lastLoadScreenSeconds:0.0}s", Palette.Queued);
        }

        public override bool HasMinimap => true;

        public override void DrawMinimap(HudUi ui, Rect r)
        {
            int n = HalfWorld * 2;
            float size = Mathf.Min(r.width, r.height);
            float cs = size / n;
            float ox = (r.width - size) * 0.5f;
            foreach (var cell in _cells.Values)
            {
                Color c = cell.State switch
                {
                    CellState.Loaded => Palette.Loaded,
                    CellState.Cached => Palette.Cached,
                    CellState.Queued => Palette.Queued,
                    CellState.Loading => Palette.Loading,
                    _ => Palette.Unloaded.WithAlpha(0.6f),
                };
                if (_hitching && cell.Coord == _playerCell) c = Palette.Unloading;
                float x = ox + (cell.Coord.x + HalfWorld) * cs;
                float y = (n - 1 - (cell.Coord.y + HalfWorld)) * cs;
                ui.Fill(new Rect(x + 1, y + 1, cs - 2, cs - 2), c);
            }

            foreach (var d in _doors)
            {
                Vector2 dp = MapPoint(d.Position, ox, size);
                ui.Fill(new Rect(dp.x - 3, dp.y - 3, 6, 6), Color.white);
            }

            if (_phase == Phase.Exterior || _phase == Phase.LoadingExterior)
            {
                Vector2 pp = MapPoint(Ctx.Player.transform.position, ox, size);
                ui.Pill(new Rect(pp.x - 5, pp.y - 5, 10, 10), Palette.Player);
            }
        }

        static Vector2 MapPoint(Vector3 w, float ox, float size)
        {
            float world = HalfWorld * 2 * CellSize;
            return new Vector2(ox + (w.x / world + 0.5f) * size, (0.5f - w.z / world) * size);
        }

        // =====================================================================================
        //  Content generation (stands in for "deserialise the cell from disk")
        // =====================================================================================

        static Vector2Int WorldToCell(Vector3 p) => new Vector2Int(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.z / CellSize));
        static Vector3 CellCenter(Vector2Int c) => new Vector3((c.x + 0.5f) * CellSize, 0, (c.y + 0.5f) * CellSize);
        static Vector3 Ground(Vector3 p) => new Vector3(p.x, Height(p.x, p.z), p.z);

        static float Height(float x, float z)
        {
            float n = Noise.Fbm(x / 300f + 10, z / 300f + 5, 4);
            float ridge = Noise.Ridged(x / 220f, z / 220f, 3, 4.2f);
            float h = Mathf.Pow(n, 1.7f) * 55f + ridge * ridge * 40f * Mathf.SmoothStep(0.35f, 0.7f, n) - 6f;
            return Mathf.Max(h, 0f);
        }

        static Color TerrainColor(float h, System.Random rng)
        {
            Color c;
            if (h < 0.3f) c = Palette.Hex("#2b5f75");        // lake
            else if (h < 3f) c = Palette.Hex("#8a9a5b");     // shore grass
            else if (h < 16f) c = Palette.Hex("#4f7d3a");    // meadow
            else if (h < 30f) c = Palette.Hex("#3c5e3a");    // pine forest
            else if (h < 42f) c = Palette.Hex("#6f6a63");    // rock
            else c = Palette.Hex("#e8eef5");                 // snow
            return c.Mul(0.78f).Jitter(rng, 0.06f).Glow(0);
        }

        GameObject BuildCellContent(Vector2Int c)
        {
            var mb = _mb;
            mb.Clear();
            var rng = Noise.Rng(c.x, c.y, 7);
            float x0 = c.x * CellSize, z0 = c.y * CellSize;
            const int res = 10;
            float step = CellSize / res;

            // Faceted terrain.
            for (int i = 0; i < res; i++)
            for (int j = 0; j < res; j++)
            {
                float ax = x0 + i * step, az = z0 + j * step;
                Vector3 p00 = new Vector3(ax, Height(ax, az), az);
                Vector3 p10 = new Vector3(ax + step, Height(ax + step, az), az);
                Vector3 p01 = new Vector3(ax, Height(ax, az + step), az + step);
                Vector3 p11 = new Vector3(ax + step, Height(ax + step, az + step), az + step);
                float avg = (p00.y + p10.y + p01.y + p11.y) * 0.25f;
                Color col = TerrainColor(avg, rng);
                mb.Tri(p00, p01, p11, col, Vector3.up);
                mb.Tri(p00, p11, p10, col.Mul(0.94f).Glow(0), Vector3.up);
            }

            // Trees and rocks.
            int trees = rng.Next(6, 20);
            for (int t = 0; t < trees; t++)
            {
                float tx = x0 + rng.Range(2, CellSize - 2), tz = z0 + rng.Range(2, CellSize - 2);
                float h = Height(tx, tz);
                if (h < 1f || h > 34f) continue;
                float th = rng.Range(5f, 9f);
                mb.Tree(new Vector3(tx, h - 0.3f, tz), th, Palette.Hex(h > 16 ? "#2f5a3c" : "#4a8a45").Jitter(rng, 0.12f).Glow(0), Palette.Hex("#5a4632").Glow(0));
            }
            int rocks = rng.Next(0, 5);
            for (int k = 0; k < rocks; k++)
            {
                float rx = x0 + rng.Range(2, CellSize - 2), rz = z0 + rng.Range(2, CellSize - 2);
                float s = rng.Range(1f, 3f);
                mb.Box(new Vector3(rx, Height(rx, rz) + s * 0.3f, rz), new Vector3(s, s * 0.7f, s * 1.2f), Palette.Hex("#77746e").Jitter(rng, 0.1f).Glow(0), rng.Range(0, 90));
            }

            // Houses with doors live in their cells too.
            foreach (var d in _doors)
            {
                if (WorldToCell(d.Position) != c) continue;
                float gh = Height(d.Position.x, d.Position.z);
                Vector3 hc = new Vector3(d.Position.x, gh, d.Position.z + 5.5f);
                mb.Box(hc + Vector3.up * 2f, new Vector3(12, 10, 10), Palette.Hex("#8b6b4a").Glow(0));
                mb.Cone(hc + Vector3.up * 7f, 9.5f, 6f, 4, Palette.Hex("#5b4a42").Glow(0));
                mb.Box(new Vector3(d.Position.x, gh + 2f, d.Position.z + 0.45f), new Vector3(2.6f, 4f, 0.3f), Palette.Hex("#ffb35c").Glow(0.9f));
                mb.Box(hc + new Vector3(4, 6.5f, 0), new Vector3(1.2f, 5, 1.2f), Palette.Hex("#6e6a66").Glow(0));
            }

            var mr = DemoAssets.Spawn($"Cell({c.x},{c.y})", _cellsRoot, mb.ToMesh($"Cell({c.x},{c.y})"), Ctx.Assets.Lit);
            return mr.gameObject;
        }

        GameObject BuildLod(Vector2Int c)
        {
            // 2x2 quads per cell, desaturated: "this is a stand-in".
            var mb = _mb;
            mb.Clear();
            var rng = Noise.Rng(c.x, c.y, 9);
            float x0 = c.x * CellSize, z0 = c.y * CellSize, step = CellSize / 2f;
            for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                float ax = x0 + i * step, az = z0 + j * step;
                Vector3 p00 = new Vector3(ax, Height(ax, az), az);
                Vector3 p10 = new Vector3(ax + step, Height(ax + step, az), az);
                Vector3 p01 = new Vector3(ax, Height(ax, az + step), az + step);
                Vector3 p11 = new Vector3(ax + step, Height(ax + step, az + step), az + step);
                float avg = (p00.y + p10.y + p01.y + p11.y) * 0.25f;
                Color col = Color.Lerp(TerrainColor(avg, rng), Palette.Hex("#2e3850"), 0.7f).Glow(0);
                mb.Quad(p00, p01, p11, p10, col, Vector3.up);
            }
            var mr = DemoAssets.Spawn($"LOD({c.x},{c.y})", _lodRoot, mb.ToMesh("LOD"), Ctx.Assets.Lit, false);
            return mr.gameObject;
        }

        GameObject BuildInterior()
        {
            var mb = _mb;
            mb.Clear();
            Vector3 o = InteriorOrigin;
            Color wood = Palette.Hex("#6b4e36").Glow(0), wall = Palette.Hex("#8f7a63").Glow(0);
            mb.Box(o + new Vector3(0, -0.5f, 0), new Vector3(32, 1, 26), wood);
            mb.Box(o + new Vector3(0, 3, 13), new Vector3(32, 7, 1), wall);
            mb.Box(o + new Vector3(-16, 3, 0), new Vector3(1, 7, 26), wall);
            mb.Box(o + new Vector3(16, 3, 0), new Vector3(1, 7, 26), wall);
            mb.Box(o + new Vector3(-10, 3, -13), new Vector3(12, 7, 1), wall);
            mb.Box(o + new Vector3(10, 3, -13), new Vector3(12, 7, 1), wall);
            mb.Box(o + new Vector3(0, 1, 2), new Vector3(7, 0.6f, 4), Palette.Hex("#7d5a3c").Glow(0));              // table
            mb.Box(o + new Vector3(0, 0.5f, 12), new Vector3(6, 4, 1.5f), Palette.Hex("#575250").Glow(0));        // hearth
            mb.Box(o + new Vector3(0, 0.8f, 11.2f), new Vector3(3, 1.4f, 0.6f), Palette.Hex("#ff7a2e").Glow(1f)); // fire
            for (int i = 0; i < 4; i++) mb.Prism(o + new Vector3(12, 0, -8 + i * 2.4f), 1f, 2f, 7, Palette.Hex("#7a5534").Glow(0)); // barrels
            mb.Box(o + new Vector3(-11, 0.8f, 7), new Vector3(6, 1.2f, 4), Palette.Hex("#9b8d7c").Glow(0));       // bed
            var mr = DemoAssets.Spawn("Interior", Root, mb.ToMesh("Interior"), Ctx.Assets.Lit);
            return mr.gameObject;
        }
    }
}
