using System.Collections.Generic;
using UnityEngine;

namespace StreamingDemo
{
    /// <summary>
    /// 04 · ROOMS &amp; DOORS  (Metroid Prime - also Jak &amp; Daxter, God of War 2018)
    ///
    /// The world is a GRAPH of rooms connected by doors. When you enter a room,
    /// every room one door away starts loading. A door only opens once the room
    /// behind it is resident - if you're faster than the disc, you wait at the
    /// door (and the game pretends the door is just slow).
    ///
    /// The design trick: short corridors ("airlocks") between big rooms. Because
    /// only neighbours are resident, a corridor guarantees two big rooms are
    /// never in memory together. Level design IS the memory manager.
    /// </summary>
    public sealed class RoomPortalMode : DemoMode
    {
        public override string Number => "04";
        public override string Title => "Rooms & Doors";
        public override string ShortName => "Rooms";
        public override string GameRef => "Metroid Prime  ·  also Jak & Daxter, God of War (2018)";
        public override string Tagline => "A graph of rooms. Entering a room loads its neighbours; a door won't open until the room behind it is resident. Loading is hidden inside level design.";
        public override string[] HowItWorks => new[]
        {
            "The world is a graph: rooms are nodes, doors are edges. Rooms are the streaming unit.",
            "Resident set = current room + every room one door away. Everything else is unloaded.",
            "Doors stay locked until the room behind them is resident. A slow disc = a slow door, never a loading screen.",
            "Small corridors separate big rooms, so two big rooms are never neighbours: the memory peak is bounded by design.",
            "Prefetch depth 2 means fewer waits but more memory. On a 24 MB console that doesn't fit.",
            "Same idea, other disguises: elevators, crawl spaces, squeezing through cracks in walls, long boat rides.",
        };

        public override (Color color, string label)[] Legend => new[]
        {
            (Palette.Unloaded, "Room on disc"),
            (Palette.Queued, "Queued"),
            (Palette.Loading, "Loading"),
            (Palette.Loaded, "Resident"),
            (Palette.Cached, "Door open"),
            (Palette.Unloading, "Door locked"),
        };

        // ------------------------------------------------------------------ data

        enum RoomState { Unloaded, Queued, Loaded }

        sealed class Room
        {
            public int Index;
            public string Name;
            public Rect Area;
            public float SizeMB;
            public Color Theme;
            public bool Corridor;
            public readonly List<Door> Doors = new List<Door>();
            public RoomState State;
            public LoadRequest Request;
            public GameObject Content;
            public MeshRenderer Ghost;
        }

        sealed class Door
        {
            public Room A, B;
            public Vector3 Pos;
            public bool NormalX;         // door faces along X (opening in a wall of constant x)
            public Rect Block;
            public Transform Panel;
            public MeshRenderer PanelRenderer;
            public float Open;           // 0 closed .. 1 open
            public bool Locked => A.State != RoomState.Loaded || B.State != RoomState.Loaded;
            public Room Other(Room r) => r == A ? B : A;
        }

        readonly List<Room> _rooms = new List<Room>();
        readonly List<Door> _doors = new List<Door>();
        readonly MeshBuilder _mb = new MeshBuilder();
        readonly HashSet<Room> _desired = new HashSet<Room>();

        Room _current;
        int _prefetchDepth = 1;
        int _doorWaits;
        float _doorWaitSeconds;
        bool _wasBlocked;
        bool _oomLogged;
        float _peakMB;

        // =====================================================================================
        //  Setup
        // =====================================================================================

        protected override void OnEnter()
        {
            Ctx.Disk.Name = "Optical disc (GameCube)";
            Ctx.Disk.BandwidthMBps = 2.5f;
            Ctx.Disk.SeekMs = 100f;
            Ctx.Disk.MaxInFlight = 1;
            Ctx.Memory.BudgetMB = 24f;
            Ctx.SetFog(280f, 700f);
            Ctx.App.SetShadowDistance(220f);
            Ctx.Cam.farClipPlane = 1500f;

            _rooms.Clear(); _doors.Clear(); _desired.Clear();
            _current = null; _prefetchDepth = 1;
            _doorWaits = 0; _doorWaitSeconds = 0; _wasBlocked = false; _oomLogged = false; _peakMB = 0;

            var floor = DemoAssets.Spawn("Floor", Root, DemoAssets.UnitQuad, Ctx.Assets.Grid(10, 5, Palette.Hex("#060912"), Palette.Hex("#141f36")), false);
            floor.transform.position = new Vector3(0, -0.5f, 0);
            floor.transform.localScale = new Vector3(3000, 1, 3000);

            // Layout: big rooms joined by small corridors. Sizes are "MB on a GameCube".
            AddRoom("Landing Site",   -30, -25, 30, 25, 7.0f, "#2b6a7e");
            AddRoom("Corridor A",      30,  -5, 60,  5, 1.0f, "#3a4660", true);
            AddRoom("Crystal Hall",    60, -30, 110, 30, 8.0f, "#5b3f9a");
            AddRoom("Lift Shaft",      80,  30, 90, 60, 1.0f, "#3a4660", true);
            AddRoom("Reactor",         60,  60, 110, 110, 9.0f, "#9a4f2b");
            AddRoom("Corridor B",      30,  80, 60, 90, 1.2f, "#3a4660", true);
            AddRoom("Observatory",    -30,  60, 30, 120, 7.5f, "#2b8a5e");
            AddRoom("Tunnel",          -5,  25,  5, 60, 1.0f, "#3a4660", true);
            AddRoom("Crawlspace",     -60,  -5, -30,  5, 1.0f, "#3a4660", true);
            AddRoom("Vault",         -100, -20, -60, 20, 6.0f, "#9a852b");

            AddDoor(0, 1, 30, 0, true);   AddDoor(1, 2, 60, 0, true);
            AddDoor(2, 3, 85, 30, false); AddDoor(3, 4, 85, 60, false);
            AddDoor(4, 5, 60, 85, true);  AddDoor(5, 6, 30, 85, true);
            AddDoor(6, 7, 0, 60, false);  AddDoor(7, 0, 0, 25, false);
            AddDoor(0, 8, -30, 0, true);  AddDoor(8, 9, -60, 0, true);

            foreach (var r in _rooms) r.Ghost = BuildGhost(r);
            foreach (var d in _doors) BuildDoor(d);

            var p = Ctx.Player;
            p.Speed = 12f;
            p.Walkable = IsWalkable;
            Vector3 C(int i) => new Vector3(_rooms[i].Area.center.x, 0, _rooms[i].Area.center.y);
            Vector3 D(int i) => _doors[i].Pos;
            p.SetPath(new[]
            {
                D(0), D(1), C(2), D(2), D(3), C(4), D(4), D(5), C(6), D(6), D(7), C(0), D(8), D(9), C(9), D(9), D(8), C(0)
            }, true, 0);
            p.Teleport(C(0));
            Ctx.Rig.SetView(60f, 240f, 0f);
        }

        protected override void OnExit()
        {
            Ctx.Player.Walkable = null;
            Ctx.Player.Frozen = false;
        }

        void AddRoom(string name, float x0, float z0, float x1, float z1, float mb, string theme, bool corridor = false)
        {
            _rooms.Add(new Room
            {
                Index = _rooms.Count, Name = name, Area = Rect.MinMaxRect(x0, z0, x1, z1), SizeMB = mb,
                Theme = Palette.Hex(theme), Corridor = corridor
            });
        }

        void AddDoor(int a, int b, float x, float z, bool normalX)
        {
            var d = new Door
            {
                A = _rooms[a], B = _rooms[b], Pos = new Vector3(x, 0, z), NormalX = normalX,
                Block = normalX ? new Rect(x - 1.5f, z - 5, 3, 10) : new Rect(x - 5, z - 1.5f, 10, 3)
            };
            _doors.Add(d);
            d.A.Doors.Add(d);
            d.B.Doors.Add(d);
        }

        // =====================================================================================
        //  CORE ALGORITHM - load the neighbours, lock the doors
        // =====================================================================================

        public override void Tick(float dt)
        {
            var p = Ctx.Player;
            Vector2 pp = new Vector2(p.transform.position.x, p.transform.position.z);

            // 1) Which room are we in? (Stick with the old one while standing in a doorway.)
            if (_current == null || !_current.Area.Contains(pp))
                foreach (var r in _rooms)
                    if (r.Area.Contains(pp)) { EnterRoom(r); break; }
            if (_current == null) return;

            // 2) DESIRED SET = breadth-first search through doors, up to the prefetch depth.
            _desired.Clear();
            var frontier = new List<Room> { _current };
            _desired.Add(_current);
            for (int depth = 1; depth <= _prefetchDepth; depth++)
            {
                var next = new List<Room>();
                foreach (var r in frontier)
                foreach (var d in r.Doors)
                {
                    var o = d.Other(r);
                    if (_desired.Add(o)) next.Add(o);
                }
                frontier = next;
            }

            // 3) REQUEST missing rooms. The room behind the door you're walking towards goes first.
            foreach (var r in _desired)
            {
                float urgency = r == _current ? -1000f : DistanceToRect(pp, r.Area);
                if (r.State == RoomState.Unloaded)
                {
                    var captured = r;
                    r.State = RoomState.Queued;
                    r.Request = Ctx.Disk.Enqueue(r.Name, r.SizeMB, urgency, _ => OnRoomLoaded(captured));
                    Ctx.Log($"REQ   {r.Name} ({r.SizeMB:0.0} MB)", Palette.Queued);
                }
                else if (r.State == RoomState.Queued) r.Request.Priority = urgency;
            }

            // 4) EVICT rooms that are no longer neighbours.
            foreach (var r in _rooms)
            {
                if (_desired.Contains(r)) continue;
                if (r.State == RoomState.Queued) { Ctx.Disk.Cancel(r.Request); r.State = RoomState.Unloaded; }
                else if (r.State == RoomState.Loaded) UnloadRoom(r);
            }

            // 5) Can't stand in an unloaded room (only happens at game start).
            p.Frozen = _current.State != RoomState.Loaded;

            // Stats: waiting at a locked door.
            if (p.IsBlocked)
            {
                _doorWaitSeconds += dt;
                if (!_wasBlocked)
                {
                    _doorWaits++;
                    Ctx.Banner("WAITING AT DOOR  ·  room behind it still loading", Palette.Unloading, 1.4f);
                    Ctx.Log("Door locked: next room not resident", Palette.Unloading);
                }
            }
            _wasBlocked = p.IsBlocked;

            _peakMB = Mathf.Max(_peakMB, Ctx.Memory.UsedMB);
            if (Ctx.Memory.OverBudget && !_oomLogged)
            {
                _oomLogged = true;
                Ctx.Banner("OVER 24 MB  ·  this would crash on the real console", Palette.Warning, 2.5f);
                Ctx.Log("OUT OF MEMORY (budget exceeded)", Palette.Warning);
            }
            if (!Ctx.Memory.OverBudget) _oomLogged = false;

            UpdateDoors(dt);
            UpdateGhosts();
        }

        void EnterRoom(Room r)
        {
            if (_current != null) Ctx.Log($"ENTER {r.Name}", Palette.Accent);
            _current = r;
        }

        void OnRoomLoaded(Room r)
        {
            if (r.State != RoomState.Queued) return;
            r.State = RoomState.Loaded;
            Ctx.Memory.Allocate(r.SizeMB);
            r.Content = BuildRoom(r);
            Tweens.PopIn(r.Content.transform, 0.5f);
            Tweens.Flash(r.Content.GetComponent<Renderer>(), Palette.Loaded * 0.5f, 0.8f);
            Ctx.Log($"LOAD  {r.Name}", Palette.Loaded);
        }

        void UnloadRoom(Room r)
        {
            Ctx.Memory.Free(r.SizeMB);
            Tweens.SinkAndDestroy(r.Content, 0.4f);
            r.Content = null;
            r.State = RoomState.Unloaded;
            Ctx.Log($"FREE  {r.Name}", Palette.Unloading);
        }

        /// <summary>Collision: inside a room, and not inside a locked doorway.</summary>
        bool IsWalkable(Vector3 p)
        {
            Vector2 q = new Vector2(p.x, p.z);
            foreach (var d in _doors)
                if (d.Locked && d.Block.Contains(q)) return false;
            foreach (var r in _rooms)
                if (r.Area.Contains(q)) return true;
            return false;
        }

        static float DistanceToRect(Vector2 p, Rect r)
        {
            float dx = Mathf.Max(r.xMin - p.x, 0, p.x - r.xMax);
            float dz = Mathf.Max(r.yMin - p.y, 0, p.y - r.yMax);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // =====================================================================================
        //  Visualisation
        // =====================================================================================

        void UpdateDoors(float dt)
        {
            Vector3 pp = Ctx.Player.transform.position;
            foreach (var d in _doors)
            {
                bool near = Vector3.Distance(Flat(pp), d.Pos) < 11f;
                float target = !d.Locked && near ? 1f : 0f;
                d.Open = Mathf.MoveTowards(d.Open, target, dt * 2.5f);
                d.Panel.localPosition = new Vector3(0, -d.Open * 5.5f, 0);

                Material m = d.Locked
                    ? (near ? Ctx.Assets.Glow(Palette.Unloading.WithAlpha(0.85f), 1.5f, 0f, 8f) : Ctx.Assets.Glow(Palette.Unloading.WithAlpha(0.45f)))
                    : Ctx.Assets.Glow(Palette.Cached.WithAlpha(0.7f), 1.5f);
                if (d.PanelRenderer.sharedMaterial != m) d.PanelRenderer.sharedMaterial = m;
            }
        }

        void UpdateGhosts()
        {
            foreach (var r in _rooms)
            {
                bool show = r.State != RoomState.Loaded;
                r.Ghost.gameObject.SetActive(show);
                if (!show) continue;
                bool loading = r.Request != null && r.Request.IsInFlight;
                var m = r.State == RoomState.Queued
                    ? (loading ? Ctx.Assets.Glow(Palette.Loading.WithAlpha(0.9f), 0, 0, 5f) : Ctx.Assets.Glow(Palette.Queued.WithAlpha(0.8f)))
                    : Ctx.Assets.Glow(Palette.Unloaded.WithAlpha(0.8f));
                if (r.Ghost.sharedMaterial != m) r.Ghost.sharedMaterial = m;
            }
        }

        public override void OnWorldGUI(HudUi ui)
        {
            foreach (var r in _rooms)
            {
                if (r.Corridor && r.State == RoomState.Loaded) continue;
                Vector3 c = new Vector3(r.Area.center.x, 6, r.Area.center.y);
                string s;
                Color col;
                if (r.State == RoomState.Loaded) { s = r.Name; col = Palette.Loaded; }
                else if (r.Request != null && r.Request.IsInFlight) { s = $"{r.Name}  {r.Request.Progress * 100:0}%"; col = Palette.Loading; }
                else if (r.State == RoomState.Queued) { s = $"{r.Name}  (queued)"; col = Palette.Queued; }
                else { s = $"{r.Name}  ·  {r.SizeMB:0.0} MB on disc"; col = Palette.TextDim; }
                ui.WorldLabel(Ctx.Cam, c, s, col, r.Corridor ? ui.Small : ui.Center);
            }
            foreach (var d in _doors)
                if (d.Locked && Vector3.Distance(Flat(Ctx.Player.transform.position), d.Pos) < 14f)
                    ui.WorldLabel(Ctx.Cam, d.Pos + Vector3.up * 9, "LOCKED · loading", Palette.Unloading);
        }

        public override void DrawSettings(HudUi ui)
        {
            ui.Header("Rooms");
            int depth = ui.IntSlider("Prefetch depth (doors away)", _prefetchDepth, 1, 3, "{0:0}");
            if (depth != _prefetchDepth)
            {
                _prefetchDepth = depth;
                Ctx.Log($"Prefetch depth = {depth}", Palette.Accent);
            }
        }

        public override void DrawStats(HudUi ui)
        {
            ui.Header("Rooms");
            int resident = 0;
            foreach (var r in _rooms) if (r.State == RoomState.Loaded) resident++;
            ui.KV("Current room", _current?.Name ?? "-", Palette.Loaded);
            ui.KV("Resident rooms", $"{resident} / {_rooms.Count}");
            ui.KV("Peak memory", $"{_peakMB:0.0} MB", _peakMB > Ctx.Memory.BudgetMB ? Palette.Warning : Palette.Text);
            ui.KV("Door waits", $"{_doorWaits}  ({_doorWaitSeconds:0.0}s)", _doorWaits > 0 ? Palette.Warning : Palette.Text);
        }

        public override bool HasMinimap => true;

        public override void DrawMinimap(HudUi ui, Rect r)
        {
            // Draw the room GRAPH: nodes = rooms, edges = doors.
            Rect bounds = Rect.MinMaxRect(-110, -40, 120, 130);
            float scale = Mathf.Min(r.width / bounds.width, r.height / bounds.height) * 0.92f;
            Vector2 off = new Vector2((r.width - bounds.width * scale) * 0.5f, (r.height - bounds.height * scale) * 0.5f);
            Vector2 P(float x, float z) => new Vector2(off.x + (x - bounds.xMin) * scale, off.y + (bounds.yMax - z) * scale);

            foreach (var room in _rooms)
            {
                var a = P(room.Area.xMin, room.Area.yMax);
                var b = P(room.Area.xMax, room.Area.yMin);
                Color c = room.State == RoomState.Loaded ? Palette.Loaded
                        : room.Request != null && room.Request.IsInFlight ? Palette.Loading
                        : room.State == RoomState.Queued ? Palette.Queued : Palette.Unloaded;
                ui.Fill(new Rect(a.x, a.y, b.x - a.x, b.y - a.y), c.WithAlpha(room == _current ? 0.9f : 0.5f));
                if (room == _current) ui.Frame(new Rect(a.x, a.y, b.x - a.x, b.y - a.y), Color.white, 1.5f);
            }
            foreach (var d in _doors)
            {
                var q = P(d.Pos.x, d.Pos.z);
                ui.Fill(new Rect(q.x - 3, q.y - 3, 6, 6), d.Locked ? Palette.Unloading : Palette.Cached);
            }
            var pp = P(Ctx.Player.transform.position.x, Ctx.Player.transform.position.z);
            ui.Pill(new Rect(pp.x - 4, pp.y - 4, 8, 8), Palette.Player);
        }

        // =====================================================================================
        //  Content
        // =====================================================================================

        MeshRenderer BuildGhost(Room r)
        {
            var mb = new MeshBuilder();
            mb.RectFrame(r.Area, 0.15f, 0.8f, Color.white);
            return DemoAssets.Spawn($"{r.Name} (ghost)", Root, mb.ToMesh("Ghost"), Ctx.Assets.Glow(Palette.Unloaded.WithAlpha(0.8f)), false);
        }

        void BuildDoor(Door d)
        {
            var frame = new GameObject("Door").transform;
            frame.SetParent(Root, false);
            frame.position = d.Pos;

            var mb = new MeshBuilder();
            Vector3 size = d.NormalX ? new Vector3(1.2f, 5.5f, 10f) : new Vector3(10f, 5.5f, 1.2f);
            mb.Box(new Vector3(0, 2.75f, 0), size, Color.white);
            var panel = DemoAssets.Spawn("Panel", frame, mb.ToMesh("DoorPanel"), Ctx.Assets.Glow(Palette.Unloading.WithAlpha(0.5f)), false);
            d.Panel = panel.transform;
            d.PanelRenderer = panel;

            mb.Clear();
            Vector3 post = new Vector3(1.6f, 7f, 1.6f);
            Vector3 side = d.NormalX ? new Vector3(0, 0, 5.8f) : new Vector3(5.8f, 0, 0);
            mb.Box(side + Vector3.up * 3.5f, post, Palette.Hex("#58627a").Glow(0));
            mb.Box(-side + Vector3.up * 3.5f, post, Palette.Hex("#58627a").Glow(0));
            mb.Box(Vector3.up * 7.2f, d.NormalX ? new Vector3(1.6f, 0.8f, 13f) : new Vector3(13f, 0.8f, 1.6f), Palette.Hex("#7a86a3").Glow(0.2f));
            DemoAssets.Spawn("Frame", frame, mb.ToMesh("DoorFrame"), Ctx.Assets.Lit);
        }

        GameObject BuildRoom(Room r)
        {
            var mb = _mb;
            mb.Clear();
            var rng = Noise.Rng(r.Index, 5, 21);
            Rect a = r.Area;
            Color floor = r.Theme.Mul(0.55f).Glow(0);
            Color wall = r.Theme.Mul(0.85f).Glow(0);
            Color trim = r.Theme.Glow(0.6f);

            mb.Box(new Vector3(a.center.x, -0.25f, a.center.y), new Vector3(a.width, 0.5f, a.height), floor);
            // Floor tiles for texture.
            for (float x = a.xMin + 5; x < a.xMax; x += 10)
            for (float z = a.yMin + 5; z < a.yMax; z += 10)
                if (rng.Chance(0.35f)) mb.Box(new Vector3(x, 0.02f, z), new Vector3(9.2f, 0.06f, 9.2f), r.Theme.Mul(rng.Range(0.6f, 0.75f)).Glow(0));

            // Walls with gaps where doors are.
            const float wh = 5f, wt = 1f;
            WallSegments(mb, r, new Vector3(a.xMin, 0, a.yMin), new Vector3(a.xMax, 0, a.yMin), wh, wt, wall, trim);
            WallSegments(mb, r, new Vector3(a.xMin, 0, a.yMax), new Vector3(a.xMax, 0, a.yMax), wh, wt, wall, trim);
            WallSegments(mb, r, new Vector3(a.xMin, 0, a.yMin), new Vector3(a.xMin, 0, a.yMax), wh, wt, wall, trim);
            WallSegments(mb, r, new Vector3(a.xMax, 0, a.yMin), new Vector3(a.xMax, 0, a.yMax), wh, wt, wall, trim);

            if (r.Corridor)
            {
                // Light strips along the corridor.
                bool alongX = a.width > a.height;
                int n = Mathf.FloorToInt((alongX ? a.width : a.height) / 6f);
                for (int i = 0; i < n; i++)
                {
                    float t = (i + 0.5f) / n;
                    Vector3 c = alongX ? new Vector3(Mathf.Lerp(a.xMin, a.xMax, t), 0.1f, a.center.y) : new Vector3(a.center.x, 0.1f, Mathf.Lerp(a.yMin, a.yMax, t));
                    mb.Box(c, alongX ? new Vector3(2.5f, 0.15f, 0.6f) : new Vector3(0.6f, 0.15f, 2.5f), Palette.Accent.Glow(0.8f));
                }
            }
            else
            {
                Vector3 center = new Vector3(a.center.x, 0, a.center.y);
                switch (r.Name)
                {
                    case "Reactor":
                        mb.Prism(center, 7f, 2f, 12, Palette.Hex("#4a4f5c").Glow(0));
                        mb.Prism(center + Vector3.up * 2f, 3.5f, 14f, 10, Palette.Hex("#ff7a2e").Glow(1f));
                        break;
                    case "Observatory":
                        mb.Ring(center + Vector3.up * 0.2f, 12f, 14f, 40, Palette.Hex("#7dffcf").Glow(0.9f));
                        mb.Sphere(center + Vector3.up * 6f, 4f, Palette.Hex("#c6f7ff").Glow(0.6f), 6, 12);
                        break;
                    case "Vault":
                        mb.BoxOnGround(center, new Vector3(6, 3, 4), Palette.Hex("#d8b24a").Glow(0.4f));
                        break;
                    case "Landing Site":
                        mb.BoxOnGround(center + new Vector3(0, 0, 8), new Vector3(16, 2, 10), Palette.Hex("#c9d3e6").Glow(0));
                        mb.Cone(center + new Vector3(0, 2, 8), 6f, 3f, 6, Palette.Hex("#ffb35c").Glow(0.5f));
                        break;
                }

                // Crystals / pillars scattered (away from the walking lines through the centre).
                int props = rng.Next(10, 18);
                for (int i = 0; i < props; i++)
                {
                    Vector3 pp = new Vector3(rng.Range(a.xMin + 4, a.xMax - 4), 0, rng.Range(a.yMin + 4, a.yMax - 4));
                    if (Mathf.Abs(pp.x - a.center.x) < 7 || Mathf.Abs(pp.z - a.center.y) < 7) continue;
                    if (r.Name == "Crystal Hall" || rng.Chance(0.4f))
                        mb.Crystal(pp + Vector3.up * 1.5f, rng.Range(0.8f, 1.8f), rng.Range(4, 9), 5, r.Theme.Mul(1.6f).Glow(0.8f));
                    else
                        mb.Prism(pp, rng.Range(1, 2), rng.Range(3, 8), 6, wall);
                }
            }

            var mr = DemoAssets.Spawn(r.Name, Root, mb.ToMesh(r.Name), Ctx.Assets.Lit);
            return mr.gameObject;
        }

        void WallSegments(MeshBuilder mb, Room r, Vector3 from, Vector3 to, float h, float t, Color wall, Color trim)
        {
            // Split the wall around any door that sits on it.
            bool alongX = Mathf.Abs(to.x - from.x) > Mathf.Abs(to.z - from.z);
            var cuts = new List<(float, float)>();
            foreach (var d in r.Doors)
            {
                bool onWall = alongX ? Mathf.Abs(d.Pos.z - from.z) < 0.5f && !d.NormalX : Mathf.Abs(d.Pos.x - from.x) < 0.5f && d.NormalX;
                if (!onWall) continue;
                float c = alongX ? d.Pos.x : d.Pos.z;
                cuts.Add((c - 5f, c + 5f));
            }
            cuts.Sort((x, y) => x.Item1.CompareTo(y.Item1));

            float start = alongX ? Mathf.Min(from.x, to.x) : Mathf.Min(from.z, to.z);
            float end = alongX ? Mathf.Max(from.x, to.x) : Mathf.Max(from.z, to.z);
            float cursor = start;
            void Seg(float s, float e)
            {
                if (e - s < 0.1f) return;
                float mid = (s + e) * 0.5f, len = e - s;
                Vector3 c = alongX ? new Vector3(mid, h * 0.5f, from.z) : new Vector3(from.x, h * 0.5f, mid);
                Vector3 size = alongX ? new Vector3(len, h, t) : new Vector3(t, h, len);
                mb.Box(c, size, wall);
                mb.Box(c + Vector3.up * (h * 0.5f + 0.1f), alongX ? new Vector3(len, 0.25f, t * 1.2f) : new Vector3(t * 1.2f, 0.25f, len), trim);
            }
            foreach (var (s, e) in cuts) { Seg(cursor, s); cursor = e; }
            Seg(cursor, end);
        }
    }
}
