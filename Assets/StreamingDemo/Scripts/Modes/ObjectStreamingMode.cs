using System.Collections.Generic;
using UnityEngine;

namespace StreamingDemo
{
    /// <summary>
    /// 04 · OBJECT STREAMING  (Grand Theft Auto III → V - RenderWare / RAGE)
    ///
    /// There are no cells to load. Every model in the city is its own streamable
    /// asset with its own streaming distance (big things further, small props
    /// nearer). Each frame the streamer:
    ///   1. finds every object within its radius of the player (or of where the
    ///      player WILL be, if prediction is on),
    ///   2. ranks the missing ones by urgency (distance / radius),
    ///   3. feeds them through a single slow disc in priority order,
    ///   4. evicts the least useful objects when the memory budget is full.
    ///
    /// Big buildings have a permanently resident low-detail LOD so the skyline
    /// never disappears; small props have nothing and just pop in.
    /// Drive fast and the disc can't keep up: that's pop-in.
    /// </summary>
    public sealed class ObjectStreamingMode : DemoMode
    {
        public override string Number => "04";
        public override string Title => "Object Streaming";
        public override string ShortName => "Objects";
        public override string GameRef => "Grand Theft Auto  ·  RenderWare → RAGE";
        public override string Tagline => "No cells: every model streams on its own, ranked by distance and size, through one slow disc. Too fast and the world can't keep up.";
        public override string[] HowItWorks => new[]
        {
            "Each model (building, lamp, tree) is a separate asset with its own streaming radius: bigger objects are wanted from further away.",
            "Missing objects are ranked by urgency = distance ÷ radius, then read one by one from the disc.",
            "Big buildings keep a cheap LOD model resident forever, so the skyline is always there. Small props just pop in.",
            "Velocity prediction loads around where you'll be in ~1.5 s, not where you are.",
            "Memory is a fixed pool: when full, the least urgent objects are evicted.",
            "At high speed GTA lowers detail distance so the streamer can keep up. Try it.",
        };

        public override (Color color, string label)[] Legend => new[]
        {
            (Palette.Lod, "LOD model only"),
            (Palette.Queued, "Queued"),
            (Palette.Loading, "In flight"),
            (Palette.Loaded, "HD resident"),
            (Palette.Unloading, "Late pop-in"),
        };

        // ------------------------------------------------------------------ data

        enum Kind { Building, Lamp, Tree }
        enum State { Unloaded, Queued, Loaded }

        sealed class StreamObject
        {
            public Kind Kind;
            public Vector3 Pos;          // ground centre
            public Vector3 Size;
            public float Radius;         // base streaming distance
            public float SizeMB;
            public int Seed;
            public State State;
            public LoadRequest Request;
            public GameObject Hd;
            public Transform Lod;        // only buildings have one
            public float Urgency;        // updated every frame
        }

        const float Block = 36f, Road = 12f, Pitch = Block + Road;
        const int HalfBlocks = 7;
        const float Lookahead = 1.5f;

        readonly List<StreamObject> _objects = new List<StreamObject>();
        readonly List<StreamObject> _scratch = new List<StreamObject>();
        readonly MeshBuilder _mb = new MeshBuilder();
        readonly List<LineRenderer> _lines = new List<LineRenderer>();

        bool _prediction = true;
        bool _speedScaling = false;
        bool _hysteresis = true;
        float _detail = 1f;

        int _popIns;
        float _warmup;               // initial fill (GTA hides this behind its loading screen)
        int _evictions;
        float _effectiveDetail = 1f;

        Material _lodMat;
        Transform _ring, _focusRing;
        Vector3 _focus;

        // =====================================================================================
        //  Setup
        // =====================================================================================

        protected override void OnEnter()
        {
            Ctx.Disk.Name = "DVD (PS2 era)";
            Ctx.Disk.BandwidthMBps = 8f;
            Ctx.Disk.SeekMs = 25f;
            Ctx.Disk.MaxInFlight = 2;
            Ctx.Memory.BudgetMB = 64f;
            Ctx.SetFog(320f, 760f);
            Ctx.App.SetShadowDistance(260f);
            Ctx.Cam.farClipPlane = 1500f;

            _objects.Clear(); _lines.Clear();
            _popIns = _evictions = 0;
            _warmup = 3f;

            _lodMat = new Material(Ctx.Assets.Lit) { name = "SD_LodProxy" };
            _lodMat.SetColor(DemoAssets.BaseColorId, Palette.Hex("#3c4458"));

            var floor = DemoAssets.Spawn("Roads", Root, DemoAssets.UnitQuad, Ctx.Assets.Grid(Pitch, 4, Palette.Hex("#0c111c"), Palette.Hex("#26344f")), false);
            floor.transform.position = new Vector3(0, -0.05f, 0);
            floor.transform.localScale = new Vector3(4000, 1, 4000);

            GenerateCity();

            // Streaming radius indicators.
            var mb = new MeshBuilder();
            mb.Ring(Vector3.zero, 0.985f, 1f, 96, Color.white);
            _ring = DemoAssets.Spawn("StreamRadius", Root, mb.ToMesh("Ring"), Ctx.Assets.Glow(Palette.Loaded.WithAlpha(0.6f)), false).transform;
            mb.Clear();
            mb.Ring(Vector3.zero, 3f, 4f, 32, Color.white);
            _focusRing = DemoAssets.Spawn("PredictedFocus", Root, mb.ToMesh("Focus"), Ctx.Assets.Glow(Palette.Queued.WithAlpha(0.9f)), false).transform;

            for (int i = 0; i < 6; i++)
            {
                var go = new GameObject("IoLine");
                go.transform.SetParent(Root, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.sharedMaterial = Ctx.Assets.Glow(Color.white.WithAlpha(0.9f));
                lr.widthMultiplier = 0.8f;
                lr.positionCount = 2;
                lr.startColor = Palette.Player;
                lr.endColor = Palette.Loading;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.enabled = false;
                _lines.Add(lr);
            }

            var p = Ctx.Player;
            p.Speed = 30f;
            float a = 6 * Pitch, b = 2 * Pitch, c = 4 * Pitch;
            p.SetPath(new[]
            {
                new Vector3(-a, 0, -a), new Vector3(a, 0, -a), new Vector3(a, 0, -b), new Vector3(-b, 0, -b),
                new Vector3(-b, 0, c), new Vector3(a, 0, c), new Vector3(a, 0, a), new Vector3(-a, 0, a),
            }, true, 1);
            p.Teleport(new Vector3(-a, 0, -a));
            Ctx.Rig.SetView(52f, 230f, 20f);
        }

        void GenerateCity()
        {
            for (int bx = -HalfBlocks; bx < HalfBlocks; bx++)
            for (int bz = -HalfBlocks; bz < HalfBlocks; bz++)
            {
                var rng = Noise.Rng(bx, bz, 3);
                Vector3 bc = new Vector3((bx + 0.5f) * Pitch, 0, (bz + 0.5f) * Pitch);
                float downtown = Mathf.Exp(-(bc.x * bc.x + bc.z * bc.z) / (200f * 200f));

                bool park = rng.Chance(0.12f) && downtown < 0.6f;
                if (park)
                {
                    int trees = rng.Next(7, 13);
                    for (int t = 0; t < trees; t++)
                        AddObject(Kind.Tree, bc + new Vector3(rng.Range(-15, 15), 0, rng.Range(-15, 15)), new Vector3(4, rng.Range(6, 10), 4), 0.05f, rng);
                }
                else
                {
                    // 2x2 lots.
                    for (int lx = 0; lx < 2; lx++)
                    for (int lz = 0; lz < 2; lz++)
                    {
                        Vector3 lc = bc + new Vector3((lx - 0.5f) * 18f, 0, (lz - 0.5f) * 18f);
                        float h = 8f + rng.Range(0, 10) + 85f * downtown * rng.Range(0.4f, 1.2f);
                        var size = new Vector3(rng.Range(11, 16), h, rng.Range(11, 16));
                        AddObject(Kind.Building, lc, size, rng.Range(0.15f, 0.35f) + h * 0.004f, rng);
                    }
                }

                // Street lamps along two edges of every block.
                for (int k = 0; k < 2; k++)
                {
                    AddObject(Kind.Lamp, bc + new Vector3(-Block * 0.5f - 1.5f, 0, (k - 0.5f) * 20f), new Vector3(1, 6, 1), 0.02f, rng);
                    AddObject(Kind.Lamp, bc + new Vector3((k - 0.5f) * 20f, 0, -Block * 0.5f - 1.5f), new Vector3(1, 6, 1), 0.02f, rng);
                }
            }
        }

        void AddObject(Kind kind, Vector3 pos, Vector3 size, float mb, System.Random rng)
        {
            var o = new StreamObject
            {
                Kind = kind, Pos = pos, Size = size, SizeMB = mb, Seed = rng.Next(),
                // Streaming distance grows with the object's size: a tower matters from far away, a lamp doesn't.
                Radius = kind switch { Kind.Building => 70f + size.y * 1.6f, Kind.Tree => 60f, _ => 42f }
            };
            if (kind == Kind.Building)
            {
                var lod = DemoAssets.Spawn("LOD", Root, DemoAssets.UnitCube, _lodMat, false);
                lod.transform.position = pos + Vector3.up * size.y * 0.5f;
                lod.transform.localScale = new Vector3(size.x * 0.98f, size.y, size.z * 0.98f);
                o.Lod = lod.transform;
            }
            _objects.Add(o);
        }

        // =====================================================================================
        //  CORE ALGORITHM - per-object priority streaming
        // =====================================================================================

        public override void Tick(float dt)
        {
            var p = Ctx.Player;
            Vector3 pos = Flat(p.transform.position);
            _warmup -= dt;

            // Speed-based detail scaling: the faster you go, the less the streamer tries to load.
            float speed = p.Velocity.magnitude;
            float target = _detail * (_speedScaling ? Mathf.Lerp(1f, 0.5f, Mathf.InverseLerp(25f, 120f, speed)) : 1f);
            _effectiveDetail = Mathf.Lerp(_effectiveDetail, target, 1f - Mathf.Exp(-2f * dt));

            // Prediction: also care about where the player will be shortly.
            _focus = _prediction ? pos + Flat(p.Velocity) * Lookahead : pos;
            float keep = _hysteresis ? 1.25f : 1.0f;

            foreach (var o in _objects)
            {
                float r = o.Radius * _effectiveDetail;
                float d = Mathf.Min(Vector3.Distance(pos, o.Pos), Vector3.Distance(_focus, o.Pos));

                // Urgency: 0 = right on top of it, 1 = at the edge of its radius.
                o.Urgency = d / r;

                switch (o.State)
                {
                    case State.Unloaded:
                        if (o.Urgency < 1f)
                        {
                            var captured = o;
                            o.State = State.Queued;
                            o.Request = Ctx.Disk.Enqueue(o.Kind.ToString(), o.SizeMB, o.Urgency, _ => OnLoaded(captured), o);
                        }
                        break;

                    case State.Queued:
                        if (o.Urgency > keep)
                        {
                            // Nobody needs it any more: cancel (free if still queued).
                            Ctx.Disk.Cancel(o.Request);
                            o.State = State.Unloaded;
                        }
                        else o.Request.Priority = o.Urgency;
                        break;

                    case State.Loaded:
                        if (o.Urgency > keep) Unload(o, false);
                        break;
                }
            }

            EnforceBudget();
            UpdateVisuals(pos);
        }

        void OnLoaded(StreamObject o)
        {
            if (o.State != State.Queued) return;
            o.State = State.Loaded;
            Ctx.Memory.Allocate(o.SizeMB);
            o.Hd = BuildHd(o);
            if (o.Lod != null) o.Lod.gameObject.SetActive(false);

            // Arrived when the player was already close = the player saw it pop in.
            bool late = _warmup <= 0f && Vector3.Distance(Flat(Ctx.Player.transform.position), o.Pos) < o.Radius * 0.35f;
            Tweens.PopIn(o.Hd.transform, 0.3f);
            Tweens.Flash(o.Hd.GetComponent<Renderer>(), late ? Palette.Unloading * 1.5f : Palette.Loaded * 0.4f, late ? 1.2f : 0.6f);
            if (late)
            {
                _popIns++;
                if (o.Kind == Kind.Building) Ctx.Log("POP-IN  building loaded too late", Palette.Unloading);
            }
        }

        void Unload(StreamObject o, bool evicted)
        {
            Ctx.Memory.Free(o.SizeMB);
            DemoAssets.DestroyWithMesh(o.Hd);
            o.Hd = null;
            o.State = State.Unloaded;
            if (o.Lod != null) o.Lod.gameObject.SetActive(true);
            if (evicted) _evictions++;
        }

        void EnforceBudget()
        {
            if (!Ctx.Memory.OverBudget) return;
            // Evict the least urgent resident objects until we fit again.
            _scratch.Clear();
            foreach (var o in _objects) if (o.State == State.Loaded) _scratch.Add(o);
            _scratch.Sort((a, b) => b.Urgency.CompareTo(a.Urgency));
            int i = 0;
            while (Ctx.Memory.OverBudget && i < _scratch.Count)
            {
                Unload(_scratch[i++], true);
            }
            if (i > 0) Ctx.Log($"EVICT  {i} objects (memory budget)", Palette.Unloading);
        }

        // =====================================================================================
        //  Visualisation
        // =====================================================================================

        void UpdateVisuals(Vector3 pos)
        {
            float typical = (70f + 30f * 1.6f) * _effectiveDetail;
            _ring.position = pos + Vector3.up * 0.3f;
            _ring.localScale = Vector3.one * typical;

            _focusRing.gameObject.SetActive(_prediction);
            _focusRing.position = _focus + Vector3.up * 0.3f;

            // One line per in-flight read: the disc is busy fetching THIS.
            var inFlight = Ctx.Disk.InFlight;
            for (int i = 0; i < _lines.Count; i++)
            {
                bool on = i < inFlight.Count && inFlight[i].Owner is StreamObject;
                _lines[i].enabled = on;
                if (!on) continue;
                var o = (StreamObject)inFlight[i].Owner;
                _lines[i].SetPosition(0, Ctx.Player.transform.position + Vector3.up * 2f);
                _lines[i].SetPosition(1, o.Pos + Vector3.up * o.Size.y);
            }
        }

        public override void DrawSettings(HudUi ui)
        {
            ui.Header("Streamer");
            _detail = ui.Slider("Detail distance", _detail, 0.4f, 2.5f, "×{0:0.00}");
            _prediction = ui.Toggle("Velocity prediction", _prediction);
            _speedScaling = ui.Toggle("Lower detail at speed (GTA V)", _speedScaling);
            _hysteresis = ui.Toggle("Hysteresis (unload at 1.25× radius)", _hysteresis);
            Ctx.Memory.BudgetMB = ui.Slider("Memory budget", Ctx.Memory.BudgetMB, 16f, 256f, "{0:0} MB", true, Palette.Loaded);
        }

        public override void DrawStats(HudUi ui)
        {
            ui.Header("Objects");
            int hd = 0, queued = 0, lodOnly = 0;
            foreach (var o in _objects)
            {
                if (o.State == State.Loaded) hd++;
                else if (o.State == State.Queued) queued++;
                else if (o.Kind == Kind.Building && o.Urgency < 1f) lodOnly++;
            }
            ui.KV("Streamable objects", _objects.Count.ToString());
            ui.KV("HD resident", hd.ToString(), Palette.Loaded);
            ui.KV("Queued", queued.ToString(), queued > 0 ? Palette.Queued : Palette.Text);
            ui.KV("Wanted but LOD only", lodOnly.ToString(), lodOnly > 0 ? Palette.Loading : Palette.Text);
            ui.KV("Speed", $"{Ctx.Player.Velocity.magnitude:0} u/s");
            ui.KV("Effective detail", $"×{_effectiveDetail:0.00}");
            ui.KV("Late pop-ins", _popIns.ToString(), _popIns > 0 ? Palette.Warning : Palette.Text);
            ui.KV("Budget evictions", _evictions.ToString());
        }

        public override bool HasMinimap => true;

        public override void DrawMinimap(HudUi ui, Rect r)
        {
            if (Event.current.type != EventType.Repaint) return;
            float world = HalfBlocks * 2 * Pitch;
            float size = Mathf.Min(r.width, r.height);
            float ox = (r.width - size) * 0.5f;
            Vector2 P(Vector3 w) => new Vector2(ox + (w.x / world + 0.5f) * size, (0.5f - w.z / world) * size);

            foreach (var o in _objects)
            {
                Color c = o.State switch
                {
                    State.Loaded => Palette.Loaded,
                    State.Queued => o.Request != null && o.Request.IsInFlight ? Palette.Loading : Palette.Queued,
                    _ => o.Kind == Kind.Building ? Palette.Lod.WithAlpha(0.35f) : Palette.Unloaded.WithAlpha(0.5f),
                };
                var pt = P(o.Pos);
                float s = o.Kind == Kind.Building ? 4f : 2f;
                ui.Fill(new Rect(pt.x - s * 0.5f, pt.y - s * 0.5f, s, s), c);
            }
            var pp = P(Ctx.Player.transform.position);
            ui.Pill(new Rect(pp.x - 5, pp.y - 5, 10, 10), Palette.Player);
            if (_prediction)
            {
                var fp = P(_focus);
                ui.Frame(new Rect(fp.x - 4, fp.y - 4, 8, 8), Palette.Queued);
            }
        }

        // =====================================================================================
        //  Content (stands in for "parse the model file")
        // =====================================================================================

        GameObject BuildHd(StreamObject o)
        {
            var mb = _mb;
            mb.Clear();
            var rng = new System.Random(o.Seed);
            switch (o.Kind)
            {
                case Kind.Building: BuildBuilding(mb, o, rng); break;
                case Kind.Tree:
                    mb.RoundTree(o.Pos, o.Size.y, Palette.Hex("#3f8a4a").Jitter(rng, 0.15f).Glow(0), Palette.Hex("#5a4632").Glow(0));
                    break;
                case Kind.Lamp:
                    mb.Prism(o.Pos, 0.25f, o.Size.y, 5, Palette.Hex("#56606f").Glow(0));
                    mb.Box(o.Pos + new Vector3(0, o.Size.y, 0), new Vector3(1.4f, 0.5f, 1.4f), Palette.Hex("#ffe3a3").Glow(1f));
                    break;
            }
            var mr = DemoAssets.Spawn(o.Kind.ToString(), Root, mb.ToMesh("HD"), Ctx.Assets.Lit);
            return mr.gameObject;
        }

        static void BuildBuilding(MeshBuilder mb, StreamObject o, System.Random rng)
        {
            string[] palette = { "#c9b8a3", "#a7b6c7", "#8e9aaa", "#c7a58a", "#b7c2b0", "#d4cbbd" };
            Color body = Palette.Hex(palette[rng.Next(palette.Length)]).Jitter(rng, 0.08f).Glow(0);
            Vector3 s = o.Size;
            mb.BoxOnGround(o.Pos, s, body);

            // Window bands on all four faces.
            Color win = Palette.Hex(rng.Chance(0.5f) ? "#ffd98a" : "#9fd8ff").Glow(0.75f);
            Color dark = Palette.Hex("#1b2333").Glow(0);
            for (float y = 3f; y < s.y - 1.5f; y += 3.2f)
            {
                Color c = rng.Chance(0.7f) ? win : dark;
                mb.Box(o.Pos + new Vector3(0, y, -s.z * 0.5f - 0.05f), new Vector3(s.x * 0.8f, 1.1f, 0.1f), c);
                mb.Box(o.Pos + new Vector3(0, y, s.z * 0.5f + 0.05f), new Vector3(s.x * 0.8f, 1.1f, 0.1f), rng.Chance(0.7f) ? win : dark);
                mb.Box(o.Pos + new Vector3(-s.x * 0.5f - 0.05f, y, 0), new Vector3(0.1f, 1.1f, s.z * 0.8f), rng.Chance(0.7f) ? win : dark);
                mb.Box(o.Pos + new Vector3(s.x * 0.5f + 0.05f, y, 0), new Vector3(0.1f, 1.1f, s.z * 0.8f), rng.Chance(0.7f) ? win : dark);
            }

            // Roof clutter.
            Vector3 roof = o.Pos + Vector3.up * s.y;
            int units = rng.Next(1, 4);
            for (int i = 0; i < units; i++)
                mb.BoxOnGround(roof + new Vector3(rng.Range(-s.x * 0.3f, s.x * 0.3f), 0, rng.Range(-s.z * 0.3f, s.z * 0.3f)), new Vector3(2.5f, 1.5f, 2.5f), Palette.Hex("#6e7685").Glow(0));
            if (s.y > 45f)
            {
                mb.Prism(roof, 0.3f, 10f, 4, Palette.Hex("#9aa3b2").Glow(0));
                mb.Box(roof + Vector3.up * 10f, new Vector3(0.8f, 0.8f, 0.8f), Palette.Hex("#ff3b3b").Glow(1f));
            }
        }
    }
}
