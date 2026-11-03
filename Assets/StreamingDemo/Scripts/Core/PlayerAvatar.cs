using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StreamingDemo
{
    /// <summary>
    /// The "player": a glowing orb that either follows an autopilot route
    /// (hands-free for presenting) or is driven with WASD.
    ///
    /// Streaming systems only care about a handful of player facts, all exposed here:
    /// position, velocity (for prediction) and whether movement is currently
    /// blocked (e.g. waiting at a locked door, or frozen on a loading screen).
    /// </summary>
    public sealed class PlayerAvatar : MonoBehaviour
    {
        public float Speed = 20f;
        public bool Autopilot = true;

        /// <summary>Set by modes during loading screens. Nothing moves.</summary>
        public bool Frozen;

        /// <summary>Allow Q/E altitude control and 3D autopilot waypoints (flight sim mode).</summary>
        public bool AllowVertical;

        /// <summary>Optional collision query supplied by the mode (rooms, locked doors...).</summary>
        public Func<Vector3, bool> Walkable;

        public Vector3 Velocity { get; private set; }

        /// <summary>True when the player wanted to move this frame but the world said no.</summary>
        public bool IsBlocked { get; private set; }

        /// <summary>Accumulated time spent blocked or frozen ("the player noticed the streaming").</summary>
        public float StallSeconds { get; private set; }

        public float CameraYaw { get; set; }

        public event Action<int> WaypointReached;
        public event Action PathFinished;

        readonly List<Vector3> _path = new List<Vector3>();
        int _target;
        bool _loop = true;
        bool _pathDone;

        Transform _visual;
        TrailRenderer _trail;
        Transform _ring;
        float _visualScale = 1f;

        public IReadOnlyList<Vector3> Path => _path;
        public int TargetIndex => _target;

        public void Build(DemoAssets assets)
        {
            var mb = new MeshBuilder();
            mb.Sphere(Vector3.zero, 1f, Palette.Player.Glow(0.9f), 8, 14);
            var orb = DemoAssets.Spawn("Orb", transform, mb.ToMesh("PlayerOrb"), assets.Lit, false);
            _visual = orb.transform;

            mb.Clear();
            mb.Ring(Vector3.zero, 1.9f, 2.3f, 48, Color.white);
            var ring = DemoAssets.Spawn("Ring", transform, mb.ToMesh("PlayerRing"), assets.Glow(Palette.Player.WithAlpha(0.8f)), false);
            _ring = ring.transform;

            _trail = orb.gameObject.AddComponent<TrailRenderer>();
            _trail.sharedMaterial = assets.Glow(Color.white.WithAlpha(0.9f));
            _trail.time = 1.2f;
            _trail.minVertexDistance = 0.5f;
            _trail.widthCurve = AnimationCurve.EaseInOut(0, 1, 1, 0);
            _trail.colorGradient = new Gradient
            {
                colorKeys = new[] { new GradientColorKey(Palette.Player, 0), new GradientColorKey(Palette.Player, 1) },
                alphaKeys = new[] { new GradientAlphaKey(0.8f, 0), new GradientAlphaKey(0, 1) }
            };
            _trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            SetVisualScale(1f);
        }

        /// <summary>Different modes work at very different world scales (1 m voxels vs 8 km terrain).</summary>
        public void SetVisualScale(float s, bool showGroundRing = true)
        {
            _visualScale = s;
            _visual.localScale = Vector3.one * 1.4f * s;
            _ring.localScale = Vector3.one * s;
            _ring.gameObject.SetActive(showGroundRing);
            _trail.widthMultiplier = 1.6f * s;
            _trail.Clear();
        }

        public void SetPath(IList<Vector3> points, bool loop, int startIndex = 0)
        {
            _path.Clear();
            _path.AddRange(points);
            _loop = loop;
            _target = Mathf.Clamp(startIndex, 0, Mathf.Max(0, _path.Count - 1));
            _pathDone = false;
        }

        public void ClearPath()
        {
            _path.Clear();
            _pathDone = true;
        }

        public void Teleport(Vector3 p)
        {
            transform.position = p;
            Velocity = Vector3.zero;
            if (_trail != null) _trail.Clear();
        }

        public int NearestWaypoint()
        {
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i < _path.Count; i++)
            {
                float d = (Flat(_path[i]) - Flat(transform.position)).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        public void ResetStats() => StallSeconds = 0;

        void Update()
        {
            float dt = Time.deltaTime;
            Vector3 before = transform.position;
            IsBlocked = false;

            if (!Frozen)
            {
                Vector3 input = ReadManualInput();
                if (input.sqrMagnitude > 0.01f)
                {
                    Autopilot = false;
                    MoveManual(input * Speed * dt);
                }
                else if (Autopilot && !_pathDone && _path.Count > 0)
                {
                    StepAutopilot(dt);
                }
            }

            if (Frozen || IsBlocked) StallSeconds += dt;
            Velocity = (transform.position - before) / Mathf.Max(dt, 1e-5f);

            // Cosmetic bob + spin.
            if (_visual != null)
            {
                _visual.localPosition = Vector3.up * (1.6f + Mathf.Sin(Time.time * 3f) * 0.25f) * _visualScale;
                _visual.Rotate(0, 90 * dt, 0, Space.World);
            }
        }

        Vector3 ReadManualInput()
        {
            var kb = Keyboard.current;
            if (kb == null) return Vector3.zero;
            float x = 0, z = 0, y = 0;
            if (kb.wKey.isPressed) z += 1;
            if (kb.sKey.isPressed) z -= 1;
            if (kb.dKey.isPressed) x += 1;
            if (kb.aKey.isPressed) x -= 1;
            if (AllowVertical)
            {
                if (kb.eKey.isPressed) y += 1;
                if (kb.qKey.isPressed) y -= 1;
            }
            Vector3 v = Quaternion.Euler(0, CameraYaw, 0) * new Vector3(x, 0, z);
            v.y = y * (AllowVertical ? 2f : 0f);
            return v.sqrMagnitude > 1f ? v.normalized : v;
        }

        void MoveManual(Vector3 delta)
        {
            Vector3 p = transform.position;
            if (TryMoveTo(p + delta)) return;
            // Slide along walls.
            if (TryMoveTo(p + new Vector3(delta.x, delta.y, 0))) return;
            TryMoveTo(p + new Vector3(0, delta.y, delta.z));
        }

        void StepAutopilot(float dt)
        {
            float budget = Speed * dt;
            for (int guard = 0; guard < 8 && budget > 1e-4f; guard++)
            {
                Vector3 pos = transform.position;
                Vector3 target = _path[_target];
                if (!AllowVertical) target.y = pos.y;

                Vector3 d = target - pos;
                float dist = d.magnitude;
                if (dist <= budget)
                {
                    if (!TryMoveTo(target)) return;
                    budget -= dist;
                    int reached = _target;
                    _target++;
                    if (_target >= _path.Count)
                    {
                        if (_loop) _target = 0;
                        else { _target = _path.Count - 1; _pathDone = true; }
                    }
                    WaypointReached?.Invoke(reached);
                    if (_pathDone) { PathFinished?.Invoke(); return; }
                }
                else
                {
                    if (!TryMoveTo(pos + d / dist * budget)) return;
                    budget = 0;
                }
            }
        }

        bool TryMoveTo(Vector3 p)
        {
            if (Walkable != null && !Walkable(p))
            {
                IsBlocked = true;
                return false;
            }
            transform.position = p;
            return true;
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0, v.z);
    }
}
