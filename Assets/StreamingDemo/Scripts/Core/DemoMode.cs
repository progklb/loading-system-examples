using UnityEngine;

namespace StreamingDemo
{
    /// <summary>
    /// Base class for one loading-system demonstration.
    ///
    /// Every mode follows the same shape, which is the shape of almost every real
    /// streaming system:
    ///
    ///   1. DESIRED SET  - given the player, what SHOULD be resident? (the strategy)
    ///   2. DIFF         - compare with what IS resident / in flight
    ///   3. REQUEST      - enqueue loads for what is missing, with a priority
    ///   4. EVICT        - release what is no longer wanted (or over budget)
    ///   5. PRESENT      - decide what to draw while data is still missing
    ///                     (nothing, a loading screen, a LOD, a locked door...)
    ///
    /// Look for the "CORE ALGORITHM" banner comment in each mode.
    /// </summary>
    public abstract class DemoMode
    {
        public abstract string Number { get; }
        public abstract string Title { get; }
        public abstract string ShortName { get; }
        public abstract string GameRef { get; }
        public abstract string Tagline { get; }
        public abstract string[] HowItWorks { get; }

        /// <summary>Colour legend shown under the description.</summary>
        public virtual (Color color, string label)[] Legend => new[]
        {
            (Palette.Unloaded, "On disk"),
            (Palette.Queued, "Queued"),
            (Palette.Loading, "Loading"),
            (Palette.Loaded, "Resident"),
            (Palette.Unloading, "Evicting"),
        };

        protected DemoContext Ctx;
        protected Transform Root;

        public void Enter(DemoContext ctx)
        {
            Ctx = ctx;
            Root = new GameObject($"[Mode {Number}] {Title}").transform;
            OnEnter();
        }

        public void Exit()
        {
            OnExit();
            if (Root != null) DemoAssets.DestroyWithMesh(Root.gameObject);
            Root = null;
        }

        protected abstract void OnEnter();
        protected virtual void OnExit() { }

        public abstract void Tick(float dt);

        /// <summary>Mode specific sliders / toggles (bottom-left panel).</summary>
        public virtual void DrawSettings(HudUi ui) { }

        /// <summary>Mode specific numbers (top-right panel).</summary>
        public virtual void DrawStats(HudUi ui) { }

        public virtual bool HasMinimap => false;
        public virtual void DrawMinimap(HudUi ui, Rect r) { }

        /// <summary>World-anchored labels.</summary>
        public virtual void OnWorldGUI(HudUi ui) { }

        /// <summary>Full-screen overlays such as loading screens (drawn under the HUD panels).</summary>
        public virtual void DrawOverlay(HudUi ui) { }

        protected static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0, v.z);
    }
}
