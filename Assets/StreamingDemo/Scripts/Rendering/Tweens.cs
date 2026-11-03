using System;
using System.Collections.Generic;
using UnityEngine;

namespace StreamingDemo
{
    /// <summary>
    /// Minimal tween runner for the "pop in / sink out" animations that make
    /// streaming events readable from the back of the room. Purely cosmetic.
    /// </summary>
    public sealed class Tweens : MonoBehaviour
    {
        enum Kind { ScaleY, ScaleUniform, Flash }

        sealed class Tween
        {
            public Kind Kind;
            public Transform Target;
            public Renderer Renderer;
            public float T, Duration, From, To;
            public Color Color;
            public Action Done;
        }

        static Tweens _instance;
        readonly List<Tween> _tweens = new List<Tween>();
        static MaterialPropertyBlock _mpb;

        static Tweens Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("[Tweens]");
                    _instance = go.AddComponent<Tweens>();
                }
                return _instance;
            }
        }

        /// <summary>Grow from flat to full height with a little overshoot. Pivot must be on the ground.</summary>
        public static void PopIn(Transform t, float duration = 0.45f, Action done = null)
        {
            if (t == null) return;
            Cancel(t);
            t.localScale = new Vector3(1, 0.01f, 1);
            Instance._tweens.Add(new Tween { Kind = Kind.ScaleY, Target = t, Duration = duration, From = 0.01f, To = 1f, Done = done });
        }

        /// <summary>Uniform scale up from zero (for floating things).</summary>
        public static void GrowIn(Transform t, float duration = 0.35f)
        {
            if (t == null) return;
            Cancel(t);
            t.localScale = Vector3.one * 0.01f;
            Instance._tweens.Add(new Tween { Kind = Kind.ScaleUniform, Target = t, Duration = duration, From = 0.01f, To = 1f });
        }

        /// <summary>Flatten into the ground then destroy (and free its meshes).</summary>
        public static void SinkAndDestroy(GameObject go, float duration = 0.35f)
        {
            if (go == null) return;
            Cancel(go.transform);
            float from = go.transform.localScale.y;
            Instance._tweens.Add(new Tween
            {
                Kind = Kind.ScaleY, Target = go.transform, Duration = duration, From = from, To = 0.001f,
                Done = () => DemoAssets.DestroyWithMesh(go)
            });
        }

        /// <summary>Briefly add emission to a VertexLit renderer (e.g. green "just loaded" flash).</summary>
        public static void Flash(Renderer r, Color c, float duration = 0.8f)
        {
            if (r == null) return;
            Instance._tweens.Add(new Tween { Kind = Kind.Flash, Renderer = r, Duration = duration, Color = c });
        }

        public static void Cancel(Transform t)
        {
            if (_instance == null) return;
            _instance._tweens.RemoveAll(x => x.Target == t);
        }

        void Update()
        {
            _mpb ??= new MaterialPropertyBlock();
            float dt = Time.deltaTime;
            for (int i = _tweens.Count - 1; i >= 0; i--)
            {
                var tw = _tweens[i];
                if ((tw.Kind != Kind.Flash && tw.Target == null) || (tw.Kind == Kind.Flash && tw.Renderer == null))
                {
                    _tweens.RemoveAt(i);
                    continue;
                }

                tw.T += dt;
                float u = Mathf.Clamp01(tw.T / tw.Duration);

                switch (tw.Kind)
                {
                    case Kind.ScaleY:
                    {
                        float e = tw.To > tw.From ? EaseOutBack(u) : u * u;
                        var s = tw.Target.localScale;
                        s.y = Mathf.LerpUnclamped(tw.From, tw.To, e);
                        tw.Target.localScale = s;
                        break;
                    }
                    case Kind.ScaleUniform:
                        tw.Target.localScale = Vector3.one * Mathf.LerpUnclamped(tw.From, tw.To, EaseOutBack(u));
                        break;
                    case Kind.Flash:
                        _mpb.Clear();
                        _mpb.SetColor(DemoAssets.EmissionId, tw.Color * (1f - u) * (1f - u));
                        tw.Renderer.SetPropertyBlock(u >= 1f ? null : _mpb);
                        break;
                }

                if (u >= 1f)
                {
                    _tweens.RemoveAt(i);
                    tw.Done?.Invoke();
                }
            }
        }

        static float EaseOutBack(float x)
        {
            const float c1 = 1.70158f, c3 = c1 + 1f;
            return 1f + c3 * Mathf.Pow(x - 1f, 3) + c1 * Mathf.Pow(x - 1f, 2);
        }
    }
}
