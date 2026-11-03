using System.Collections.Generic;
using UnityEngine;

namespace StreamingDemo
{
    /// <summary>
    /// Lays out the presentation HUD:
    ///   top-left     : what this system is + how it works + legend
    ///   top-right    : live memory / I/O telemetry + mode stats + minimap
    ///   bottom-left  : knobs (player speed, disk bandwidth, mode toggles)
    ///   bottom-right : event log
    ///   bottom-centre: mode tabs + key hints
    /// </summary>
    public sealed class Hud
    {
        public readonly HudUi Ui = new HudUi();
        public bool Visible = true;
        public bool ShowDetails = true;

        public int Draw(DemoContext ctx, IReadOnlyList<DemoMode> modes, int current)
        {
            Ui.BeginFrame();
            var mode = modes[current];

            // World labels + overlays first so panels sit on top.
            if (Visible) mode.OnWorldGUI(Ui);
            mode.DrawOverlay(Ui);
            DrawBanner(ctx);

            if (!Visible)
            {
                var r = new Rect(Ui.S(12), Screen.height - Ui.S(28), Ui.S(300), Ui.S(20));
                GUI.Label(r, "H: show UI", Ui.KeyHint);
                return current;
            }

            DrawInfoPanel(mode, modes.Count);
            DrawTelemetry(ctx, mode);
            DrawControls(ctx, mode);
            DrawLog(ctx);
            return DrawTabs(modes, current);
        }

        void DrawInfoPanel(DemoMode mode, int count)
        {
            var ui = Ui;
            ui.BeginPanel("info", Anchor.TopLeft, 16, 16, 470);
            ui.Label($"LOADING SYSTEMS  ·  {mode.Number} / {count:00}", ui.Eyebrow);
            ui.Label(mode.Title, ui.H1);
            ui.Label(mode.GameRef, ui.H2);
            ui.Space(2);
            ui.Label(mode.Tagline, ui.Body);
            if (ShowDetails)
            {
                ui.Header("How it works");
                foreach (var b in mode.HowItWorks) ui.Bullet(b);
            }
            ui.Header("Legend");
            ui.Legend(mode.Legend);
            ui.EndPanel();
        }

        void DrawTelemetry(DemoContext ctx, DemoMode mode)
        {
            var ui = Ui;
            var mem = ctx.Memory;
            var disk = ctx.Disk;
            ui.BeginPanel("telemetry", Anchor.TopRight, 16, 16, 360);

            ui.Header("Memory");
            Color memColor = mem.OverBudget ? Palette.Warning : Color.Lerp(Palette.Loaded, Palette.Loading, Mathf.InverseLerp(0.7f, 1f, mem.Fraction));
            ui.Bar(mem.Fraction, memColor, "Resident", $"{mem.UsedMB,6:0.0} / {mem.BudgetMB:0} MB");
            ui.Sparkline(ctx.Metrics.Memory, Mathf.Max(mem.BudgetMB * 1.15f, ctx.Metrics.Memory.Max()), memColor, 30, mem.BudgetMB);

            ui.Header($"I/O  ·  {disk.Name}");
            ui.Sparkline(ctx.Metrics.Throughput, Mathf.Max(0.01f, disk.BandwidthMBps), Palette.Loading, 30);
            ui.KV("Throughput", $"{(ctx.Metrics.Throughput.Count > 0 ? ctx.Metrics.Throughput[ctx.Metrics.Throughput.Count - 1] : 0):0.0} MB/s", Palette.Loading);
            ui.KV("Queued / in flight", $"{disk.Queued.Count} / {disk.InFlight.Count}", disk.Queued.Count > 0 ? Palette.Queued : Palette.Text);
            ui.KV("Loads completed", $"{disk.TotalCompleted}  ({disk.TotalReadMB:0} MB)");

            mode.DrawStats(ui);

            if (mode.HasMinimap)
            {
                ui.Header("Overview");
                var r = ui.Custom(220);
                ui.Fill(r, Palette.Background.WithAlpha(0.7f));
                GUI.BeginGroup(r);
                mode.DrawMinimap(ui, new Rect(0, 0, r.width, r.height));
                GUI.EndGroup();
            }
            ui.EndPanel();
        }

        void DrawControls(DemoContext ctx, DemoMode mode)
        {
            var ui = Ui;
            var p = ctx.Player;
            var disk = ctx.Disk;
            // If the description panel is tall enough to collide, slide the controls into the next column.
            float infoBottom = ui.S(16) + ui.LastHeight("info");
            float controlsTop = Screen.height - ui.S(70) - ui.LastHeight("controls");
            float x = controlsTop < infoBottom + ui.S(10) ? 16 + 470 + 12 : 16;
            ui.BeginPanel("controls", Anchor.BottomLeft, x, 70, 360);

            ui.Header("Player");
            p.Speed = ui.Slider("Movement speed", p.Speed, 2f, 300f, "{0:0} u/s", true, Palette.Player);
            p.Autopilot = ui.Toggle("Autopilot  (Space)", p.Autopilot);

            ui.Header("Storage");
            disk.BandwidthMBps = ui.Slider("Bandwidth", disk.BandwidthMBps, 0.25f, 1000f, "{0:0.0} MB/s", true, Palette.Loading);
            disk.SeekMs = ui.Slider("Latency per request", disk.SeekMs, 0f, 300f, "{0:0} ms", false, Palette.Loading);

            mode.DrawSettings(ui);

            ui.Header("Presentation");
            bool fog = ui.Toggle("Distance fog (hides the seams)", ctx.FogEnabled);
            if (fog != ctx.FogEnabled) { ctx.FogEnabled = fog; ctx.ApplyFog(); }
            ui.EndPanel();
        }

        void DrawLog(DemoContext ctx)
        {
            var ui = Ui;
            float telemetryBottom = ui.S(16) + ui.LastHeight("telemetry");
            float logTop = Screen.height - ui.S(70) - ui.LastHeight("log");
            float x = logTop < telemetryBottom + ui.S(10) ? 16 + 360 + 12 : 16;
            ui.BeginPanel("log", Anchor.BottomRight, x, 70, 380);
            ui.Header("Event log");
            int n = Mathf.Min(9, ctx.LogEntries.Count);
            if (n == 0) ui.Label("…", ui.Small);
            for (int i = ctx.LogEntries.Count - n; i < ctx.LogEntries.Count; i++)
            {
                var e = ctx.LogEntries[i];
                float age = Time.time - e.Time;
                var r = ui.Row(16);
                var st = ui.Mono;
                var old = st.normal.textColor;
                st.normal.textColor = Palette.TextDim;
                GUI.Label(new Rect(r.x, r.y, ui.S(56), r.height), $"{e.Time,6:0.0}s", st);
                st.normal.textColor = e.Color.WithAlpha(Mathf.Lerp(1f, 0.75f, Mathf.Clamp01(age / 5f)));
                GUI.Label(new Rect(r.x + ui.S(60), r.y, r.width - ui.S(60), r.height), e.Text, st);
                st.normal.textColor = old;
            }
            ui.EndPanel();
        }

        int DrawTabs(IReadOnlyList<DemoMode> modes, int current)
        {
            var ui = Ui;
            float tabW = 150, gap = 6;
            float total = modes.Count * tabW + (modes.Count - 1) * gap;
            float x = (Screen.width - ui.S(total)) * 0.5f;
            float y = Screen.height - ui.S(58);
            int result = current;
            for (int i = 0; i < modes.Count; i++)
            {
                var r = new Rect(x + ui.S(i * (tabW + gap)), y, ui.S(tabW), ui.S(30));
                if (ui.Button(r, $"{i + 1}  {modes[i].ShortName}", i == current)) result = i;
            }
            var hint = new Rect(0, y + ui.S(34), Screen.width, ui.S(18));
            GUI.Label(hint, "1-7 / PgUp PgDn  switch  ·  WASD move  ·  Space autopilot  ·  RMB orbit  ·  Wheel zoom  ·  T top-down  ·  I details  ·  H hide UI  ·  R restart", ui.KeyHint);
            return result;
        }

        void DrawBanner(DemoContext ctx)
        {
            if (string.IsNullOrEmpty(ctx.BannerText) || Time.time > ctx.BannerUntil) return;
            var ui = Ui;
            float remaining = ctx.BannerUntil - Time.time;
            float a = Mathf.Clamp01(remaining / 0.4f);
            Vector2 size = ui.BannerStyle.CalcSize(new GUIContent(ctx.BannerText));
            var r = new Rect((Screen.width - size.x) * 0.5f - ui.S(24), ui.S(22), size.x + ui.S(48), size.y + ui.S(18));
            ui.Pill(r, Palette.Background.WithAlpha(0.75f * a));
            ui.Fill(new Rect(r.x + ui.S(24), r.yMax - ui.S(4), r.width - ui.S(48), ui.S(2)), ctx.BannerColor.WithAlpha(a));
            ui.Shadowed(r, ctx.BannerText, ui.BannerStyle, ctx.BannerColor.WithAlpha(a));
        }
    }
}
