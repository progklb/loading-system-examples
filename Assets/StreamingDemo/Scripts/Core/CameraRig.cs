using UnityEngine;
using UnityEngine.InputSystem;

namespace StreamingDemo
{
    /// <summary>
    /// Smooth orbit camera that follows the player.
    /// Right mouse drag = orbit, wheel = zoom, T = toggle top-down view.
    /// </summary>
    public sealed class CameraRig : MonoBehaviour
    {
        public Camera Cam;
        public Transform Target;

        public float Yaw = 30f, Pitch = 50f, Distance = 120f;
        public float MinDistance = 5f, MaxDistance = 6000f;
        public Vector3 TargetOffset = Vector3.zero;

        /// <summary>When true the HUD is under the mouse and wheel zoom should be ignored.</summary>
        public bool PointerOverUi;

        float _yaw, _pitch, _dist;
        Vector3 _focus;
        bool _topDown;
        float _savedPitch;

        public void SetView(float pitch, float distance, float yaw, bool snap = true)
        {
            Pitch = pitch;
            Distance = distance;
            Yaw = yaw;
            _topDown = false;
            if (snap) Snap();
        }

        public void Snap()
        {
            _yaw = Yaw;
            _pitch = Pitch;
            _dist = Distance;
            if (Target != null) _focus = Target.position + TargetOffset;
            Apply();
        }

        void LateUpdate()
        {
            var mouse = Mouse.current;
            var kb = Keyboard.current;

            if (mouse != null)
            {
                if (mouse.rightButton.isPressed)
                {
                    Vector2 d = mouse.delta.ReadValue();
                    Yaw += d.x * 0.25f;
                    Pitch = Mathf.Clamp(Pitch - d.y * 0.2f, 8f, 89f);
                }

                float scroll = mouse.scroll.ReadValue().y;
                if (!PointerOverUi && Mathf.Abs(scroll) > 0.01f)
                    Distance = Mathf.Clamp(Distance * (scroll > 0 ? 0.88f : 1.12f), MinDistance, MaxDistance);
            }

            if (kb != null && kb.tKey.wasPressedThisFrame)
            {
                _topDown = !_topDown;
                if (_topDown) { _savedPitch = Pitch; Pitch = 89f; }
                else Pitch = _savedPitch;
            }

            float k = 1f - Mathf.Exp(-8f * Time.deltaTime);
            _yaw = Mathf.LerpAngle(_yaw, Yaw, k);
            _pitch = Mathf.Lerp(_pitch, Pitch, k);
            _dist = Mathf.Lerp(_dist, Distance, k);
            if (Target != null)
            {
                Vector3 goal = Target.position + TargetOffset;
                // Snap if the player teleported (interior doors, level transitions).
                _focus = (goal - _focus).sqrMagnitude > 250f * 250f ? goal : Vector3.Lerp(_focus, goal, 1f - Mathf.Exp(-10f * Time.deltaTime));
            }
            Apply();
        }

        void Apply()
        {
            Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0);
            transform.SetPositionAndRotation(_focus - rot * Vector3.forward * _dist, rot);
        }
    }
}
