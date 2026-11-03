using System.Collections.Generic;
using UnityEngine;

namespace StreamingDemo
{
    /// <summary>
    /// 01 · DISCRETE LEVELS  (Half-Life 2 - Source Engine)
    ///
    /// The simplest possible "streaming": don't. Exactly ONE map is in memory.
    /// Walking into a trigger_changelevel volume freezes the game, saves the
    /// entities standing in the transition volume, throws the whole map away,
    /// reads the next .bsp from disk in one blocking load, then restores those
    /// entities.
    ///
    /// The clever part is the info_landmark: every map has its own coordinate
    /// system, so carried entities are stored RELATIVE to a landmark entity that
    /// exists in both maps. Level designers duplicate the geometry around the
    /// transition in both maps so the hand-over is seamless.
    /// </summary>
    public sealed class DiscreteLevelsMode : DemoMode
    {
        public override string Number => "01";
        public override string Title => "Discrete Levels";
        public override string ShortName => "Levels";
        public override string GameRef => "Half-Life 2  ·  Source Engine";
        public override string Tagline => "One map in memory at a time. Walk into a changelevel trigger: freeze, swap the whole map, carry nearby entities across via a shared landmark.";
        public override string[] HowItWorks => new[]
        {
            "The world is split into separate maps (.bsp). Only the current one is resident: simple, predictable memory.",
            "Adjacent maps overlap: the transition area is built in BOTH maps so you can't tell where one ends.",
            "trigger_changelevel: the game freezes, 'LOADING…' appears, nothing else can happen.",
            "Entities inside the transition volume (you, your companion, that crate you pushed) are saved relative to an info_landmark.",
            "Each map has its own coordinate system. The landmark exists in both, so the offset from it is preserved.",
            "Memory budget is the reason: two whole maps simply don't fit.",
        };

        public override (Color color, string label)[] Legend => new[]
        {
            (Palette.Unloaded, "Map on disk"),
            (Palette.Loading, "Loading (blocking)"),
            (Palette.Loaded, "Current map"),
            (Palette.Queued, "changelevel trigger"),
            (Palette.Accent, "info_landmark"),
            (Palette.Cached, "Carried entity"),
        };

        // ------------------------------------------------------------------ data

        sealed class MapDef
        {
            public string Name;
            public Rect Area;            // world-space XZ footprint
            public Vector2 Origin;       // world position of this map's own (0,0)
            public float SizeMB;
            public Color Floor, Prop;
            public int Style;
            public GameObject Content;
            public GameObject Ghost;
        }

        sealed class Transition
        {
            public int From, To;
            public Vector3 Landmark;
            public Rect Trigger;         // forward trigger (From -> To)
            public Rect BackTrigger;     // reverse trigger (To -> From) on the arriving lane
            public Rect Volume;          // transition volume (= overlap area)
        }

        sealed class Entity
        {
            public string Name;
            public Transform T;
            public int OwnerMap;
            public bool Carried;
        }

        enum Phase { Playing, SavingEntities, Unloading, Loading, Restoring }

        readonly List<MapDef> _maps = new List<MapDef>();
        readonly List<Transition> _transitions = new List<Transition>();
        readonly List<Entity> _entities = new List<Entity>();
        readonly List<Entity> _carried = new List<Entity>();
        readonly MeshBuilder _mb = new MeshBuilder();

        const float Lane = 130f;   // the autopilot loop is a square at +-130
        const float HalfWidth = 40f;

        int _current = -1;
        int _next = -1;
        Transition _active;
        Phase _phase = Phase.Playing;
        float _phaseT;
        LoadRequest _request;
        bool _armed = true;

        int _transitionCount;
        float _frozenTotal;
        float _lastLoad;
        string _landmarkMath = "";

        Transform _volumeFx;
        readonly List<GameObject> _triggerFx = new List<GameObject>();
        Entity _companion;

        // =====================================================================================
        //  Setup
        // =====================================================================================

        protected override void OnEnter()
        {
            Ctx.Disk.Name = "HDD (2004)";
            Ctx.Disk.BandwidthMBps = 35f;
            Ctx.Disk.SeekMs = 12f;
            Ctx.Disk.MaxInFlight = 1;
            Ctx.Memory.BudgetMB = 192f;
            Ctx.SetFog(320f, 900f);
            Ctx.App.SetShadowDistance(300f);
            Ctx.Cam.farClipPlane = 2000f;

            _maps.Clear(); _transitions.Clear(); _entities.Clear(); _carried.Clear(); _triggerFx.Clear();
            _current = -1; _transitionCount = 0; _frozenTotal = 0; _lastLoad = 0; _landmarkMath = "";

            var floor = DemoAssets.Spawn("Floor", Root, DemoAssets.UnitQuad, Ctx.Assets.Grid(20, 5, Palette.Hex("#080c18"), Palette.Hex("#17243d")), false);
            floor.transform.position = new Vector3(0, -0.6f, 0);
            floor.transform.localScale = new Vector3(4000, 1, 4000);

            // Four linear maps forming a loop; neighbouring maps overlap at the corners.
            float o = Lane + HalfWidth, i = Lane - HalfWidth;
            AddMap("d1_canals",    new Rect(-o, -o, 2 * o, 2 * HalfWidth), new Vector2(-o, -o),   136f, "#1d2a36", "#7d5b49", 0);
            AddMap("d1_docks",     new Rect(i, -o, 2 * HalfWidth, 2 * o),  new Vector2(512, -96), 148f, "#2a2a24", "#9b8f74", 1);
            AddMap("d2_coast",     new Rect(-o, i, 2 * o, 2 * HalfWidth),  new Vector2(0, 960),   124f, "#1f2c24", "#5f7d4e", 2);
            AddMap("d3_highway",   new Rect(-o, -o, 2 * HalfWidth, 2 * o), new Vector2(-2048, 0), 158f, "#25222c", "#8a7fa5", 3);

            // Corners: (position, direction arriving, direction leaving, from, to)
            AddTransition(new Vector3(Lane, 0, -Lane), Vector3.right, Vector3.forward, 0, 1);
            AddTransition(new Vector3(Lane, 0, Lane), Vector3.forward, Vector3.left, 1, 2);
            AddTransition(new Vector3(-Lane, 0, Lane), Vector3.left, Vector3.back, 2, 3);
            AddTransition(new Vector3(-Lane, 0, -Lane), Vector3.back, Vector3.right, 3, 0);

            // Ghost outlines for every map (what's "on disk").
            foreach (var m in _maps) m.Ghost = BuildGhost(m);

            // Physics props near each corner, owned by the map you arrive from.
            foreach (var t in _transitions)
            {
                Vector3 outDir = (t.Trigger.center3() - t.Landmark).normalized;
                Vector3 side = Vector3.Cross(Vector3.up, outDir);
                for (int k = 0; k < 3; k++)
                    AddCrate($"prop_physics #{_entities.Count + 1}", t.Landmark + outDir * (6 + k * 4) + side * (k - 1) * 2.2f, t.From);
            }
            _companion = AddCompanion();

            BuildVolumeFx();

            var p = Ctx.Player;
            p.Speed = 22f;
            p.SetPath(new[] { new Vector3(Lane, 0, -Lane), new Vector3(Lane, 0, Lane), new Vector3(-Lane, 0, Lane), new Vector3(-Lane, 0, -Lane) }, true, 0);
            p.Teleport(new Vector3(-60, 0, -Lane));
            Ctx.Rig.SetView(58f, 230f, 0f);

            // New game: a blocking load of the first map.
            BeginLoad(0, null);
        }

        protected override void OnExit()
        {
            Ctx.Player.Frozen = false;
        }

        void AddMap(string name, Rect area, Vector2 origin, float mb, string floor, string prop, int style)
        {
            _maps.Add(new MapDef { Name = name, Area = area, Origin = origin, SizeMB = mb, Floor = Palette.Hex(floor), Prop = Palette.Hex(prop), Style = style });
        }

        void AddTransition(Vector3 corner, Vector3 inDir, Vector3 outDir, int from, int to)
        {
            Rect trig = LaneRect(corner + outDir * 24f, outDir);
            _transitions.Add(new Transition
            {
                From = from, To = to, Landmark = corner, Trigger = trig, BackTrigger = LaneRect(corner - inDir * 24f, inDir),
                Volume = new Rect(corner.x - HalfWidth, corner.z - HalfWidth, HalfWidth * 2, HalfWidth * 2)
            });
        }

        // =====================================================================================
        //  CORE ALGORITHM - the changelevel sequence
        // =====================================================================================

        public override void Tick(float dt)
        {
            var p = Ctx.Player;
            _phaseT += dt;

            switch (_phase)
            {
                case Phase.Playing:
                {
                    // 1) Is the player standing in a trigger_changelevel that belongs to this map?
                    Vector2 pp = new Vector2(p.transform.position.x, p.transform.position.z);
                    Transition hit = null;
                    foreach (var t in _transitions)
                    {
                        bool forward = t.From == _current && t.Trigger.Contains(pp);
                        bool backward = t.To == _current && t.BackTrigger.Contains(pp);
                        if (forward || backward) { hit = t; break; }
                    }
                    if (hit == null) _armed = true;
                    else if (_armed)
                    {
                        _armed = false;
                        StartTransition(hit, hit.From == _current ? hit.To : hit.From);
                    }
                    break;
                }

                case Phase.SavingEntities:
                    // 2) Everything freezes. Entities in the transition volume are recorded
                    //    as offsets from the landmark (we just flag them; positions are kept).
                    if (_phaseT > 0.9f) { _phase = Phase.Unloading; _phaseT = 0; UnloadCurrent(); }
                    break;

                case Phase.Unloading:
                    // 3) The old map is thrown away completely. Memory drops to ~0.
                    if (_phaseT > 0.6f) { _phase = Phase.Loading; _phaseT = 0; BeginLoad(_next, _active); }
                    break;

                case Phase.Loading:
                    // 4) One big blocking read. Waiting on _request's callback.
                    break;

                case Phase.Restoring:
                    // 5) Entities re-created at landmark + saved offset in the new map's coordinates.
                    if (_phaseT > 0.7f)
                    {
                        _phase = Phase.Playing;
                        p.Frozen = false;
                        foreach (var e in _carried) e.Carried = false;
                        _carried.Clear();
                    }
                    break;
            }

            if (_phase != Phase.Playing) _frozenTotal += dt;
            p.Frozen = _phase != Phase.Playing;

            UpdateEntities(dt);
            UpdateFx(dt);
        }

        void StartTransition(Transition t, int target)
        {
            _active = t;
            _next = target;
            _phase = Phase.SavingEntities;
            _phaseT = 0;
            _transitionCount++;

            // Who comes with us? Everything of ours inside the transition volume.
            _carried.Clear();
            foreach (var e in _entities)
            {
                bool inside = t.Volume.Contains(new Vector2(e.T.position.x, e.T.position.z));
                if (e == _companion || (e.OwnerMap == _current && inside))
                {
                    e.Carried = true;
                    _carried.Add(e);
                }
            }

            // The landmark maths, for the HUD.
            var from = _maps[_current];
            var to = _maps[target];
            Vector3 pos = Ctx.Player.transform.position;
            Vector2 offset = new Vector2(pos.x - t.Landmark.x, pos.z - t.Landmark.z);
            Vector2 lmFrom = new Vector2(t.Landmark.x, t.Landmark.z) - from.Origin;
            Vector2 lmTo = new Vector2(t.Landmark.x, t.Landmark.z) - to.Origin;
            _landmarkMath = $"landmark in {from.Name}: ({lmFrom.x:0}, {lmFrom.y:0})\n" +
                            $"landmark in {to.Name}: ({lmTo.x:0}, {lmTo.y:0})\n" +
                            $"player offset kept: ({offset.x:0.0}, {offset.y:0.0})\n" +
                            $"player in {to.Name}: ({lmTo.x + offset.x:0.0}, {lmTo.y + offset.y:0.0})";

            Ctx.Banner($"trigger_changelevel  →  {to.Name}", Palette.Queued, 2.5f);
            Ctx.Log($"changelevel {from.Name} → {to.Name}", Palette.Queued);
            Ctx.Log($"save {_carried.Count} entities rel. to landmark", Palette.Cached);
        }

        void UnloadCurrent()
        {
            var m = _maps[_current];
            if (m.Content != null) Tweens.SinkAndDestroy(m.Content, 0.5f);
            m.Content = null;
            m.Ghost.SetActive(true);
            Ctx.Memory.Free(m.SizeMB);
            Ctx.Log($"FREE  {m.Name}.bsp  {m.SizeMB:0} MB", Palette.Unloading);
        }

        void BeginLoad(int index, Transition via)
        {
            var m = _maps[index];
            _phase = Phase.Loading;
            _phaseT = 0;
            _next = index;
            Ctx.Player.Frozen = true;
            _request = Ctx.Disk.Enqueue($"{m.Name}.bsp", m.SizeMB, 0, _ => OnMapLoaded(index));
            Ctx.Log($"READ  {m.Name}.bsp  ({m.SizeMB:0} MB, blocking)", Palette.Loading);
        }

        void OnMapLoaded(int index)
        {
            var m = _maps[index];
            Ctx.Memory.Allocate(m.SizeMB);
            m.Content = BuildMap(m);
            m.Ghost.SetActive(false);
            Tweens.PopIn(m.Content.transform, 0.6f);
            _lastLoad = _request.CompletedAt - _request.QueuedAt;
            Ctx.Log($"LOAD  {m.Name}.bsp in {_lastLoad:0.0}s", Palette.Loaded);

            // Hand carried entities to the new map.
            foreach (var e in _carried) e.OwnerMap = index;
            _current = index;
            _phase = Phase.Restoring;
            _phaseT = 0;
        }

        /// <summary>A 6-unit-deep strip across a lane, perpendicular to <paramref name="dir"/>.</summary>
        static Rect LaneRect(Vector3 c, Vector3 dir)
            => Mathf.Abs(dir.z) > 0.5f
                ? new Rect(c.x - HalfWidth, c.z - 3, HalfWidth * 2, 6)
                : new Rect(c.x - 3, c.z - HalfWidth, 6, HalfWidth * 2);

        // =====================================================================================
        //  Entities
        // =====================================================================================

        void AddCrate(string name, Vector3 pos, int owner)
        {
            var mb = new MeshBuilder();
            mb.Box(new Vector3(0, 1.1f, 0), new Vector3(2.2f, 2.2f, 2.2f), Palette.Hex("#a0713f").Glow(0));
            mb.Box(new Vector3(0, 1.1f, 0), new Vector3(2.3f, 0.35f, 2.3f), Palette.Hex("#ffb35c").Glow(0.6f));
            var mr = DemoAssets.Spawn(name, Root, mb.ToMesh("Crate"), Ctx.Assets.Lit);
            mr.transform.position = pos;
            _entities.Add(new Entity { Name = name, T = mr.transform, OwnerMap = owner });
        }

        Entity AddCompanion()
        {
            var mb = new MeshBuilder();
            mb.Sphere(Vector3.zero, 0.9f, Palette.Hex("#ffd23f").Glow(0.8f), 5, 8);
            var mr = DemoAssets.Spawn("npc_companion", Root, mb.ToMesh("Companion"), Ctx.Assets.Lit, false);
            var e = new Entity { Name = "npc_companion", T = mr.transform, OwnerMap = 0 };
            _entities.Add(e);
            return e;
        }

        void UpdateEntities(float dt)
        {
            var p = Ctx.Player.transform.position;
            // Companion trails behind the player.
            Vector3 follow = p - Ctx.Player.Velocity.normalized * 5f + Vector3.up * 2.5f;
            if (_phase == Phase.Playing)
                _companion.T.position = Vector3.Lerp(_companion.T.position, follow, 1f - Mathf.Exp(-3f * dt));

            foreach (var e in _entities)
            {
                if (e == _companion) continue;
                bool visible = e.Carried || (e.OwnerMap == _current && _maps[_current].Content != null);
                if (e.T.gameObject.activeSelf != visible) e.T.gameObject.SetActive(visible);
                if (!visible || _phase != Phase.Playing) continue;

                // Very simple "physics": the player shoves crates out of the way.
                Vector3 d = e.T.position - new Vector3(p.x, e.T.position.y, p.z);
                float dist = d.magnitude;
                if (dist < 3.2f && dist > 0.01f)
                    e.T.position += d / dist * (3.2f - dist) + Ctx.Player.Velocity * dt * 0.6f;
            }
        }

        // =====================================================================================
        //  Visualisation
        // =====================================================================================

        void BuildVolumeFx()
        {
            var mr = DemoAssets.Spawn("TransitionVolume", Root, DemoAssets.UnitCube, Ctx.Assets.Glow(Palette.Cached.WithAlpha(0.1f), 2f, 1f), false);
            _volumeFx = mr.transform;
            _volumeFx.gameObject.SetActive(false);

            foreach (var t in _transitions)
            {
                var fx = DemoAssets.Spawn("trigger_changelevel", Root, DemoAssets.UnitCube, Ctx.Assets.Glow(Palette.Queued.WithAlpha(0.22f), 1.5f, 0.8f, 3f), false);
                fx.transform.position = new Vector3(t.Trigger.center.x, 4, t.Trigger.center.y);
                fx.transform.localScale = new Vector3(t.Trigger.width, 8, t.Trigger.height);
                _triggerFx.Add(fx.gameObject);

                var mb = new MeshBuilder();
                mb.Crystal(Vector3.zero, 1.6f, 5f, 4, Palette.Accent.Glow(1f));
                var lm = DemoAssets.Spawn("info_landmark", Root, mb.ToMesh("Landmark"), Ctx.Assets.Lit, false);
                lm.transform.position = t.Landmark + Vector3.up * 9f;
            }
        }

        void UpdateFx(float dt)
        {
            for (int i = 0; i < _transitions.Count; i++)
                _triggerFx[i].SetActive(_phase == Phase.Playing && (_transitions[i].From == _current));

            bool showVolume = _phase != Phase.Playing && _active != null;
            _volumeFx.gameObject.SetActive(showVolume);
            if (showVolume)
            {
                _volumeFx.position = new Vector3(_active.Volume.center.x, 6, _active.Volume.center.y);
                _volumeFx.localScale = new Vector3(_active.Volume.width, 12, _active.Volume.height);
            }
        }

        public override void OnWorldGUI(HudUi ui)
        {
            var cam = Ctx.Cam;
            foreach (var t in _transitions)
            {
                ui.WorldLabel(cam, t.Landmark + Vector3.up * 15f, "info_landmark", Palette.Accent);
                if (_phase == Phase.Playing && t.From == _current)
                    ui.WorldLabel(cam, t.Trigger.center3() + Vector3.up * 11f, $"trigger_changelevel → {_maps[t.To].Name}", Palette.Queued);
            }
            foreach (var m in _maps)
            {
                bool loaded = m.Content != null;
                Vector3 c = new Vector3(m.Area.center.x, 0, m.Area.center.y);
                ui.WorldLabel(cam, c + Vector3.up * 30f, loaded ? $"{m.Name}  ·  RESIDENT" : $"{m.Name}  ·  on disk", loaded ? Palette.Loaded : Palette.TextDim);
            }
            if (_phase != Phase.Playing)
                foreach (var e in _carried)
                    ui.WorldLabel(cam, e.T.position + Vector3.up * 5f, $"↻ {e.Name}", Palette.Cached, ui.Small);
        }

        public override void DrawOverlay(HudUi ui)
        {
            if (_phase == Phase.Playing) return;

            // The famous small "LOADING..." box over a frozen frame.
            float w = ui.S(520), h = ui.S(_phase == Phase.Loading ? 196 : 180);
            var r = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.5f + ui.S(120), w, h);
            ui.Panel(r);
            float x = r.x + ui.S(18), y = r.y + ui.S(14);
            ui.Shadowed(new Rect(x, y, w, ui.S(36)), "LOADING…", ui.H1, Palette.Text);
            y += ui.S(46);

            string target = _next >= 0 ? _maps[_next].Name : "?";
            string from = _active != null ? _maps[_active.From == _next ? _active.To : _active.From].Name : "(new game)";
            Step(ui, ref x, ref y, w, 1, $"Save {_carried.Count} entities relative to info_landmark", _phase > Phase.SavingEntities || _active == null, _phase == Phase.SavingEntities);
            Step(ui, ref x, ref y, w, 2, $"Unload {from} (geometry, models, sounds)", _phase > Phase.Unloading || _active == null, _phase == Phase.Unloading);
            string read = _request != null && _phase == Phase.Loading ? $"  {_request.ReadMB:0}/{_request.SizeMB:0} MB" : "";
            Step(ui, ref x, ref y, w, 3, $"Read {target}.bsp{read}", _phase > Phase.Loading, _phase == Phase.Loading);
            Step(ui, ref x, ref y, w, 4, "Restore entities, resume simulation", false, _phase == Phase.Restoring);

            if (_phase == Phase.Loading && _request != null)
            {
                var bar = new Rect(x, y + ui.S(6), w - ui.S(36), ui.S(8));
                ui.Pill(bar, Palette.PanelEdge);
                ui.Pill(new Rect(bar.x, bar.y, Mathf.Max(bar.height, bar.width * _request.Progress), bar.height), Palette.Loading);
            }
        }

        static void Step(HudUi ui, ref float x, ref float y, float w, int n, string text, bool done, bool active)
        {
            Color c = done ? Palette.Loaded : active ? Palette.Loading : Palette.TextDim;
            string mark = done ? "✓" : active ? "►" : "·";
            var st = ui.Body;
            var old = st.normal.textColor;
            st.normal.textColor = c;
            GUI.Label(new Rect(x, y, w - ui.S(36), ui.S(22)), $"{mark}  {n}. {text}", st);
            st.normal.textColor = old;
            y += ui.S(26);
        }

        public override void DrawSettings(HudUi ui)
        {
            ui.Header("Level");
            if (ui.ButtonRow("Force changelevel now")) ForceNext();
        }

        void ForceNext()
        {
            if (_phase != Phase.Playing) return;
            foreach (var t in _transitions)
                if (t.From == _current)
                {
                    // Corner i leads from map i to map i+1, so resume the tour at waypoint i+1.
                    var tour = new List<Vector3>(Ctx.Player.Path);
                    Ctx.Player.Teleport(t.Trigger.center3());
                    Ctx.Player.SetPath(tour, true, (_current + 1) % tour.Count);
                    break;
                }
        }

        public override void DrawStats(HudUi ui)
        {
            ui.Header("Levels");
            var p = Ctx.Player.transform.position;
            if (_current >= 0)
            {
                var m = _maps[_current];
                ui.KV("Current map", m.Name, Palette.Loaded);
                ui.KV("Player (map coordinates)", $"({p.x - m.Origin.x:0}, {p.z - m.Origin.y:0})");
            }
            ui.KV("Map transitions", _transitionCount.ToString());
            ui.KV("Last blocking load", $"{_lastLoad:0.0}s", Palette.Loading);
            ui.KV("Total time frozen", $"{_frozenTotal:0.0}s", Palette.Warning);
            if (!string.IsNullOrEmpty(_landmarkMath))
            {
                ui.Header("Landmark maths (last transition)");
                ui.Label(_landmarkMath, ui.Mono);
            }
        }

        public override bool HasMinimap => true;

        public override void DrawMinimap(HudUi ui, Rect r)
        {
            float world = 2 * (Lane + HalfWidth) + 20;
            float size = Mathf.Min(r.width, r.height);
            float ox = (r.width - size) * 0.5f;
            Rect ToMap(Rect w) => new Rect(ox + (w.xMin / world + 0.5f) * size, (0.5f - w.yMax / world) * size, w.width / world * size, w.height / world * size);
            Vector2 P(Vector3 w) => new Vector2(ox + (w.x / world + 0.5f) * size, (0.5f - w.z / world) * size);

            for (int i = 0; i < _maps.Count; i++)
            {
                var m = _maps[i];
                Color c = m.Content != null ? Palette.Loaded : (i == _next && _phase == Phase.Loading) ? Palette.Loading : Palette.Unloaded;
                ui.Fill(ToMap(m.Area), c.WithAlpha(m.Content != null || i == _next ? 0.55f : 0.35f));
            }
            foreach (var t in _transitions)
            {
                ui.Frame(ToMap(t.Volume), Palette.Cached.WithAlpha(0.8f));
                var lp = P(t.Landmark);
                ui.Fill(new Rect(lp.x - 3, lp.y - 3, 6, 6), Palette.Accent);
            }
            var pp = P(Ctx.Player.transform.position);
            ui.Pill(new Rect(pp.x - 5, pp.y - 5, 10, 10), Palette.Player);
        }

        // =====================================================================================
        //  Content generation
        // =====================================================================================

        GameObject BuildGhost(MapDef m)
        {
            var mb = new MeshBuilder();
            mb.RectFrame(m.Area, 0.2f, 1.2f, Color.white);
            var mr = DemoAssets.Spawn($"{m.Name} (ghost)", Root, mb.ToMesh("Ghost"), Ctx.Assets.Glow(Palette.Unloaded.WithAlpha(0.9f)), false);
            return mr.gameObject;
        }

        static float DistanceToLane(Vector2 p)
        {
            // Distance from a point to the square loop at +-Lane.
            float dx = Mathf.Abs(Mathf.Abs(p.x) - Lane), dz = Mathf.Abs(Mathf.Abs(p.y) - Lane);
            float a = Mathf.Abs(p.y) <= Lane ? dx : new Vector2(dx, Mathf.Abs(p.y) - Lane).magnitude;
            float b = Mathf.Abs(p.x) <= Lane ? dz : new Vector2(dz, Mathf.Abs(p.x) - Lane).magnitude;
            return Mathf.Min(a, b);
        }

        bool InAnyOverlap(Vector2 p)
        {
            foreach (var t in _transitions) if (t.Volume.Contains(p)) return true;
            return false;
        }

        GameObject BuildMap(MapDef m)
        {
            var mb = _mb;
            mb.Clear();

            // Floor, road and perimeter walls.
            mb.Box(new Vector3(m.Area.center.x, -0.3f, m.Area.center.y), new Vector3(m.Area.width, 0.6f, m.Area.height), m.Floor.Glow(0));
            Color wall = m.Prop.Mul(0.6f).Glow(0);
            mb.Box(new Vector3(m.Area.center.x, 1.5f, m.Area.yMin), new Vector3(m.Area.width, 3, 1), wall);
            mb.Box(new Vector3(m.Area.center.x, 1.5f, m.Area.yMax), new Vector3(m.Area.width, 3, 1), wall);
            mb.Box(new Vector3(m.Area.xMin, 1.5f, m.Area.center.y), new Vector3(1, 3, m.Area.height), wall);
            mb.Box(new Vector3(m.Area.xMax, 1.5f, m.Area.center.y), new Vector3(1, 3, m.Area.height), wall);

            // Props on a world-space lattice. Seeds come from WORLD position, so the overlap area
            // generates identically in both maps (just like designers copy it by hand).
            const float step = 9f;
            for (float x = m.Area.xMin + step * 0.5f; x < m.Area.xMax; x += step)
            for (float z = m.Area.yMin + step * 0.5f; z < m.Area.yMax; z += step)
            {
                var cell = new Vector2Int(Mathf.RoundToInt(x), Mathf.RoundToInt(z));
                var rng = Noise.Rng(cell.x, cell.y, 11);
                Vector2 p = new Vector2(x + rng.Range(-2.5f, 2.5f), z + rng.Range(-2.5f, 2.5f));
                float lane = DistanceToLane(p);
                if (lane < 9f) continue;
                bool overlap = InAnyOverlap(p);
                int style = overlap ? 99 : m.Style;
                Prop(mb, style, new Vector3(p.x, 0, p.y), rng, lane, overlap ? Palette.Hex("#6c7383") : m.Prop);
            }

            // Lane markings.
            for (float s = -Lane; s <= Lane; s += 8)
            {
                foreach (var v in new[] { new Vector2(s, -Lane), new Vector2(s, Lane), new Vector2(-Lane, s), new Vector2(Lane, s) })
                    if (m.Area.Contains(v)) mb.Box(new Vector3(v.x, 0.05f, v.y), new Vector3(1.2f, 0.1f, 1.2f), Palette.Hex("#c9d3e6").Glow(0.25f));
            }

            var mr = DemoAssets.Spawn(m.Name, Root, mb.ToMesh(m.Name), Ctx.Assets.Lit);
            return mr.gameObject;
        }

        static void Prop(MeshBuilder mb, int style, Vector3 pos, System.Random rng, float laneDist, Color baseCol)
        {
            Color c = baseCol.Jitter(rng, 0.15f).Glow(0);
            switch (style)
            {
                case 0: // canals: brick blocks with lit windows, water channels
                    if (rng.Chance(0.25f)) { mb.Box(pos + Vector3.up * 0.1f, new Vector3(8, 0.2f, 8), Palette.Hex("#1f5b78").Glow(0.25f)); break; }
                    float h = rng.Range(4, 14) + Mathf.Min(laneDist, 30) * 0.3f;
                    mb.BoxOnGround(pos, new Vector3(7, h, 7), c);
                    if (rng.Chance(0.6f)) mb.Box(pos + new Vector3(0, h * 0.6f, -3.55f), new Vector3(4, 1.2f, 0.1f), Palette.Hex("#ffcf7a").Glow(0.8f));
                    break;
                case 1: // docks: containers, cranes
                    if (rng.Chance(0.7f))
                    {
                        int stack = rng.Next(1, 4);
                        for (int k = 0; k < stack; k++) mb.Box(pos + Vector3.up * (1.3f + k * 2.6f), new Vector3(7.5f, 2.5f, 3), Palette.Hex(k % 2 == 0 ? "#b5523b" : "#3b74b5").Jitter(rng, 0.2f).Glow(0), rng.Next(0, 2) * 90);
                    }
                    else
                    {
                        mb.Prism(pos, 0.6f, 16, 4, c);
                        mb.Box(pos + new Vector3(3, 16, 0), new Vector3(10, 1, 1), c);
                        mb.Box(pos + new Vector3(7, 15, 0), new Vector3(0.4f, 1.6f, 0.4f), Palette.Hex("#ff4f4f").Glow(1));
                    }
                    break;
                case 2: // coast: rocks and trees
                    if (rng.Chance(0.6f)) mb.Tree(pos, rng.Range(5, 10), Palette.Hex("#3f7a45").Jitter(rng, 0.15f).Glow(0), Palette.Hex("#5a4632").Glow(0));
                    else mb.Box(pos + Vector3.up * 1.2f, new Vector3(rng.Range(3, 7), rng.Range(2, 5), rng.Range(3, 7)), Palette.Hex("#6f6c66").Jitter(rng, 0.1f).Glow(0), rng.Range(0, 90));
                    break;
                case 3: // highway: pylons, wrecks, barriers
                    if (rng.Chance(0.3f)) { mb.Prism(pos, 0.4f, 12, 6, c); mb.Box(pos + Vector3.up * 12, new Vector3(0.6f, 0.6f, 3), Palette.Hex("#d6f0ff").Glow(0.9f)); }
                    else if (rng.Chance(0.5f)) mb.Box(pos + Vector3.up * 0.9f, new Vector3(4.2f, 1.8f, 2), Palette.Hex("#7b8796").Jitter(rng, 0.25f).Glow(0), rng.Range(0, 180));
                    else mb.BoxOnGround(pos, new Vector3(6, rng.Range(3, 9), 6), c);
                    break;
                default: // shared transition geometry (identical in both maps)
                    mb.BoxOnGround(pos, new Vector3(5, rng.Range(2, 6), 5), c);
                    if (rng.Chance(0.3f)) mb.Box(pos + Vector3.up * 6.5f, new Vector3(0.8f, 0.8f, 0.8f), Palette.Accent.Glow(0.7f));
                    break;
            }
        }
    }

    static class RectExt
    {
        public static Vector3 center3(this Rect r) => new Vector3(r.center.x, 0, r.center.y);
    }
}
