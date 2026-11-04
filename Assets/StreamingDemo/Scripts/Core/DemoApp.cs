using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace StreamingDemo
{
    /// <summary>
    /// Entry point. Drop this on an empty GameObject in an otherwise empty scene
    /// and press Play: it builds the camera rig, lighting, post FX, player and
    /// HUD, then runs one <see cref="DemoMode"/> at a time.
    ///
    /// Frame order (deliberately explicit):
    ///   input -> mode.Tick (strategy decides what it wants) -> disk.Tick (bytes move)
    ///   -> metrics sample -> OnGUI
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class DemoApp : MonoBehaviour
    {
        [Tooltip("Mode to start in (0-based). Every run starts here, so the talk always opens the same way.")]
        public int StartMode = 0;

        /// <summary>The running app (handy for editor scripting / remote control).</summary>
        public static DemoApp Instance { get; private set; }

        public DemoContext Ctx { get; private set; }
        public IReadOnlyList<DemoMode> Modes => _modes;
        public int CurrentIndex => _current;

        readonly List<DemoMode> _modes = new List<DemoMode>();
        int _current = -1;
        Hud _hud;
        float _originalShadowDistance = -1f;
        UniversalRenderPipelineAsset _urp;

        void Awake()
        {
            Instance = this;
            Application.targetFrameRate = 120;
            // Stable number formatting ("12.5 MB", not "12,5 MB") regardless of OS locale.
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            _hud = new Hud();

            var assets = new DemoAssets();
            var cam = SetupCamera();
            var sun = SetupLighting();
            SetupPostFx();

            var playerGo = new GameObject("Player");
            var player = playerGo.AddComponent<PlayerAvatar>();
            player.Build(assets);

            // (No '??' here: Unity's fake-null objects don't play well with it.)
            var rig = cam.gameObject.GetComponent<CameraRig>();
            if (rig == null) rig = cam.gameObject.AddComponent<CameraRig>();
            rig.Cam = cam;
            rig.Target = player.transform;

            Ctx = new DemoContext
            {
                App = this,
                Assets = assets,
                Cam = cam,
                Rig = rig,
                Player = player,
                Sun = sun,
                Disk = new SimulatedIO("Disk"),
                Memory = new MemoryBudget(),
                Metrics = new Metrics(),
            };

            // The running order of the talk. Each one fixes a limitation of the previous.
            _modes.Add(new DiscreteLevelsMode());
            _modes.Add(new RoomPortalMode());
            _modes.Add(new CellGridMode());
            _modes.Add(new ObjectStreamingMode());
            _modes.Add(new ChunkGenerationMode());
            _modes.Add(new QuadtreeLodMode());
            _modes.Add(new WorldPartitionMode());

            SwitchTo(Mathf.Clamp(StartMode, 0, _modes.Count - 1));
        }

        Camera SetupCamera()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = go.AddComponent<Camera>();
            }
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Palette.Background;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 3000f;
            cam.fieldOfView = 50f;

            var data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            data.antialiasingQuality = AntialiasingQuality.High;
            return cam;
        }

        Light SetupLighting()
        {
            Light sun = null;
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional) { sun = l; break; }
            if (sun == null)
            {
                sun = new GameObject("Sun").AddComponent<Light>();
                sun.type = LightType.Directional;
            }
            sun.transform.rotation = Quaternion.Euler(52f, -38f, 0f);
            sun.color = Palette.Hex("#fff1dc");
            sun.intensity = 1.0f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.75f;

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Palette.Hex("#2b3550");
            RenderSettings.skybox = null;

            _urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (_urp != null) _originalShadowDistance = _urp.shadowDistance;
            return sun;
        }

        void SetupPostFx()
        {
            var go = new GameObject("[PostFX]");
            var vol = go.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 50;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "StreamingDemoPostFX";

            var bloom = profile.Add<Bloom>(true);
            bloom.intensity.Override(1.1f);
            bloom.threshold.Override(1.15f); // only emissive (HDR) surfaces bloom
            bloom.scatter.Override(0.7f);

            var tone = profile.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.Neutral);

            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(0.3f);
            vignette.smoothness.Override(0.5f);

            var color = profile.Add<ColorAdjustments>(true);
            color.contrast.Override(10f);
            color.saturation.Override(12f);

            vol.sharedProfile = profile;
        }

        /// <summary>Modes pick a shadow distance that suits their world scale.</summary>
        public void SetShadowDistance(float d)
        {
            if (_urp != null) _urp.shadowDistance = d;
        }

        public void SwitchTo(int index)
        {
            if (index == _current) return;
            if (_current >= 0) _modes[_current].Exit();

            // Clean slate: same hardware, empty memory, fresh stats.
            Ctx.Disk.CancelAll();
            Ctx.Disk.ResetStats();
            Ctx.Memory.Reset();
            Ctx.Metrics.Reset();
            Ctx.LogEntries.Clear();
            Ctx.BannerUntil = 0;

            var p = Ctx.Player;
            p.Frozen = false;
            p.AllowVertical = false;
            p.Walkable = null;
            p.Autopilot = true;
            p.ClearPath();
            p.ResetStats();
            p.SetVisualScale(1f);
            p.Teleport(Vector3.zero);

            _current = index;
            Ctx.Log($"Mode {_modes[_current].Number}: {_modes[_current].Title}", Palette.Accent);
            _modes[_current].Enter(Ctx);
            Ctx.ApplyFog();
            Ctx.Rig.Snap();
        }

        public void Restart()
        {
            int i = _current;
            _modes[_current].Exit();
            _current = -1;
            SwitchTo(i);
        }

        void Update()
        {
            HandleKeys();
            float dt = Time.deltaTime;
            Ctx.Rig.PointerOverUi = _hud.Visible && Mouse.current != null &&
                                   _hud.Ui.PointerOver(new Vector2(Mouse.current.position.x.value, Screen.height - Mouse.current.position.y.value));
            Ctx.Player.CameraYaw = Ctx.Rig.Yaw;

            _modes[_current].Tick(dt);
            Ctx.Disk.Tick(dt);
            Ctx.Metrics.Tick(dt, Ctx.Disk, Ctx.Memory);
        }

        void HandleKeys()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            for (int i = 0; i < _modes.Count && i < 9; i++)
            {
                if (kb[Key.Digit1 + i].wasPressedThisFrame || kb[Key.Numpad1 + i].wasPressedThisFrame)
                    SwitchTo(i);
            }
            // Presentation clickers usually send PageUp / PageDown.
            if (kb.pageDownKey.wasPressedThisFrame || kb.rightBracketKey.wasPressedThisFrame) SwitchTo((_current + 1) % _modes.Count);
            if (kb.pageUpKey.wasPressedThisFrame || kb.leftBracketKey.wasPressedThisFrame) SwitchTo((_current + _modes.Count - 1) % _modes.Count);

            if (kb.spaceKey.wasPressedThisFrame) Ctx.Player.Autopilot = !Ctx.Player.Autopilot;
            if (kb.hKey.wasPressedThisFrame) _hud.Visible = !_hud.Visible;
            if (kb.iKey.wasPressedThisFrame) _hud.ShowDetails = !_hud.ShowDetails;
            if (kb.rKey.wasPressedThisFrame) Restart();
        }

        void OnGUI()
        {
            if (_current < 0) return;
            int chosen = _hud.Draw(Ctx, _modes, _current);
            if (chosen != _current) SwitchTo(chosen);
        }

        void OnDestroy()
        {
            if (_current >= 0) _modes[_current].Exit();
            // The URP asset is a project asset: don't leave our runtime tweak behind.
            if (_urp != null && _originalShadowDistance > 0) _urp.shadowDistance = _originalShadowDistance;
        }
    }
}
