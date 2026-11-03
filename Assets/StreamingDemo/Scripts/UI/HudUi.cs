using System.Collections.Generic;
using UnityEngine;

namespace StreamingDemo
{
    public enum Anchor { TopLeft, TopRight, BottomLeft, BottomRight, TopCenter, BottomCenter }

    /// <summary>
    /// A small immediate-mode widget kit on top of IMGUI.
    ///
    /// Why IMGUI? Zero assets, zero setup, and every widget is one line of code
    /// in the mode classes, keeping the focus on the streaming logic.
    /// All sizes are authored for 1080p and multiplied by <see cref="Scale"/>,
    /// with fonts re-rasterised at the scaled size so text stays sharp on a projector.
    /// </summary>
    public sealed class HudUi
    {
        public float Scale { get; private set; } = 1f;

        public GUIStyle Eyebrow, H1, H2, Body, Small, Mono, Center, BannerStyle, Tab, TabActive, KeyHint;

        Texture2D _white, _panelTex, _roundTex, _tabTex, _tabActiveTex;
        GUIStyle _panelStyle, _sliderStyle, _thumbStyle, _clickStyle;
        Font _font, _fontBold, _fontMono;

        readonly Dictionary<string, float> _heights = new Dictionary<string, float>();
        readonly List<Rect> _panelRects = new List<Rect>();
        float _builtScale = -1;

        // Layout cursor (pixels).
        float _x, _y, _w;
        string _panelId;
        float _panelTop;

        const float Pad = 14f;
        const float Gap = 5f;

        public float S(float v) => v * Scale;
        public float ContentWidth => _w;

        public bool PointerOver(Vector2 guiPos)
        {
            foreach (var r in _panelRects) if (r.Contains(guiPos)) return true;
            return false;
        }

        public void BeginFrame()
        {
            Scale = Mathf.Clamp(Screen.height / 1080f, 0.55f, 3f);
            if (Event.current.type == EventType.Layout) _panelRects.Clear();
            if (!Mathf.Approximately(Scale, _builtScale)) Build();
        }

        // ------------------------------------------------------------------ setup

        void Build()
        {
            _builtScale = Scale;
            if (_font == null)
            {
                _font = OsFont(16, "Segoe UI", "Helvetica Neue", "Arial");
                _fontBold = _font; // bold via FontStyle (OS "Bold" faces often fail to load)
                _fontMono = OsFont(14, "Consolas", "Menlo", "Courier New");

                _white = Texture2D.whiteTexture;
                _panelTex = MakeRounded(32, 10, Palette.Panel.WithAlpha(0.86f), Palette.PanelEdge.WithAlpha(0.9f));
                _roundTex = MakeRounded(32, 15, Color.white, Color.white);
                _tabTex = MakeRounded(32, 8, Palette.Panel.WithAlpha(0.9f), Palette.PanelEdge);
                _tabActiveTex = MakeRounded(32, 8, Palette.Accent.WithAlpha(0.22f), Palette.Accent);
            }

            GUIStyle Text(Font f, int size, Color c, bool wrap = true, TextAnchor a = TextAnchor.UpperLeft, bool bold = false)
                => new GUIStyle
                {
                    font = f, fontSize = Mathf.RoundToInt(size * Scale), wordWrap = wrap, alignment = a, richText = true,
                    fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
                    normal = { textColor = c }
                };

            Eyebrow = Text(_fontBold, 12, Palette.Accent.WithAlpha(0.85f), true, TextAnchor.UpperLeft, true);
            H1 = Text(_fontBold, 30, Palette.Text, true, TextAnchor.UpperLeft, true);
            H2 = Text(_fontBold, 17, Palette.Accent, true, TextAnchor.UpperLeft, true);
            Body = Text(_font, 15, Palette.Text);
            Small = Text(_font, 13, Palette.TextDim);
            Mono = Text(_fontMono, 13, Palette.Text, false);
            Center = Text(_fontBold, 14, Palette.Text, false, TextAnchor.MiddleCenter, true);
            BannerStyle = Text(_fontBold, 30, Palette.Text, false, TextAnchor.MiddleCenter, true);
            KeyHint = Text(_font, 12, Palette.TextDim, false, TextAnchor.MiddleCenter);

            int b = Mathf.RoundToInt(10 * Scale);
            _panelStyle = new GUIStyle { normal = { background = _panelTex }, border = new RectOffset(12, 12, 12, 12) };
            Tab = new GUIStyle(Text(_fontBold, 13, Palette.TextDim, false, TextAnchor.MiddleCenter, true))
            {
                normal = { background = _tabTex, textColor = Palette.TextDim },
                hover = { background = _tabTex, textColor = Palette.Text },
                border = new RectOffset(10, 10, 10, 10),
                padding = new RectOffset(b, b, 0, 0)
            };
            TabActive = new GUIStyle(Tab)
            {
                normal = { background = _tabActiveTex, textColor = Palette.Text },
                hover = { background = _tabActiveTex, textColor = Palette.Text }
            };

            // Invisible slider: we draw the visuals ourselves and let IMGUI handle the mouse.
            _sliderStyle = new GUIStyle { fixedHeight = 0, stretchWidth = true };
            _thumbStyle = new GUIStyle { fixedWidth = S(16), fixedHeight = S(18) };
            _clickStyle = new GUIStyle();
        }

        static HashSet<string> _installed;

        /// <summary>First installed OS font from the list (falls back to Unity's built-in font).</summary>
        static Font OsFont(int size, params string[] names)
        {
            _installed ??= new HashSet<string>(Font.GetOSInstalledFontNames());
            foreach (var n in names)
                if (_installed.Contains(n)) return Font.CreateDynamicFontFromOSFont(n, size);
            return null; // GUIStyle falls back to the default font
        }

        static Texture2D MakeRounded(int size, int radius, Color fill, Color edge)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave };
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // Signed distance to a rounded square.
                float cx = Mathf.Max(Mathf.Abs(x + 0.5f - size * 0.5f) - (size * 0.5f - radius), 0);
                float cy = Mathf.Max(Mathf.Abs(y + 0.5f - size * 0.5f) - (size * 0.5f - radius), 0);
                float d = Mathf.Sqrt(cx * cx + cy * cy) - radius;
                float a = Mathf.Clamp01(0.5f - d);
                float edgeK = Mathf.Clamp01(1.5f - Mathf.Abs(d + 1f));
                Color c = Color.Lerp(fill, edge, edgeK);
                c.a *= a;
                px[y * size + x] = c;
            }
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        // ------------------------------------------------------------------ panels

        /// <summary>
        /// Start an auto-sized panel. Height comes from the previous frame's content,
        /// so panels can be anchored to any corner without manual measuring.
        /// </summary>
        public void BeginPanel(string id, Anchor anchor, float offsetX, float offsetY, float width)
        {
            float w = S(width);
            _heights.TryGetValue(id, out float h);
            if (h <= 0) h = S(100);

            float x = 0, y = 0;
            switch (anchor)
            {
                case Anchor.TopLeft: x = S(offsetX); y = S(offsetY); break;
                case Anchor.TopRight: x = Screen.width - w - S(offsetX); y = S(offsetY); break;
                case Anchor.BottomLeft: x = S(offsetX); y = Screen.height - h - S(offsetY); break;
                case Anchor.BottomRight: x = Screen.width - w - S(offsetX); y = Screen.height - h - S(offsetY); break;
                case Anchor.TopCenter: x = (Screen.width - w) * 0.5f; y = S(offsetY); break;
                case Anchor.BottomCenter: x = (Screen.width - w) * 0.5f; y = Screen.height - h - S(offsetY); break;
            }

            var rect = new Rect(x, y, w, h);
            if (Event.current.type == EventType.Repaint) _panelStyle.Draw(rect, false, false, false, false);
            if (Event.current.type == EventType.Layout) _panelRects.Add(rect);

            _panelId = id;
            _panelTop = y;
            _x = x + S(Pad);
            _y = y + S(Pad);
            _w = w - S(Pad) * 2;
        }

        public void EndPanel()
        {
            _heights[_panelId] = _y - _panelTop + S(Pad) - S(Gap);
        }

        /// <summary>Reserve a row of the given (unscaled) height and return its rect.</summary>
        public Rect Row(float height)
        {
            var r = new Rect(_x, _y, _w, S(height));
            _y += S(height) + S(Gap);
            return r;
        }

        public void Space(float h = 6) => _y += S(h);

        // ------------------------------------------------------------------ text

        public void Label(string text, GUIStyle style = null)
        {
            style ??= Body;
            float h = style.CalcHeight(new GUIContent(text), _w);
            var r = new Rect(_x, _y, _w, h);
            GUI.Label(r, text, style);
            _y += h + S(Gap * 0.6f);
        }

        public void Header(string text)
        {
            Space(4);
            float h = Eyebrow.CalcHeight(new GUIContent(text), _w);
            var r = new Rect(_x, _y, _w, h);
            GUI.Label(r, text.ToUpperInvariant(), Eyebrow);
            Fill(new Rect(_x, _y + h + S(1), _w, Mathf.Max(1, S(1))), Palette.PanelEdge);
            _y += h + S(7);
        }

        public void Bullet(string text)
        {
            float indent = S(14);
            float h = Small.CalcHeight(new GUIContent(text), _w - indent);
            Fill(new Rect(_x + S(2), _y + S(8), S(5), S(5)), Palette.Accent.WithAlpha(0.8f));
            GUI.Label(new Rect(_x + indent, _y, _w - indent, h), text, Small);
            _y += h + S(3);
        }

        public void KV(string key, string value, Color? valueColor = null)
        {
            var r = Row(18);
            GUI.Label(r, key, Small);
            var vs = new GUIStyle(Mono) { alignment = TextAnchor.UpperRight };
            vs.normal.textColor = valueColor ?? Palette.Text;
            GUI.Label(r, value, vs);
        }

        // ------------------------------------------------------------------ widgets

        public bool Toggle(string label, bool value, string hint = null)
        {
            var r = Row(20);
            var box = new Rect(r.x, r.y + S(2), S(30), S(16));
            Pill(box, value ? Palette.Accent.WithAlpha(0.85f) : Palette.PanelEdge);
            float knob = S(12);
            Pill(new Rect(value ? box.xMax - knob - S(2) : box.x + S(2), box.y + S(2), knob, knob), value ? Palette.Background : Palette.TextDim);

            GUI.Label(new Rect(r.x + S(40), r.y, r.width - S(40), r.height), label, Body);
            if (GUI.Button(r, GUIContent.none, _clickStyle)) value = !value;
            if (!string.IsNullOrEmpty(hint)) Label(hint, Small);
            return value;
        }

        /// <summary>Labelled slider. With <paramref name="log"/> the track is logarithmic (good for bandwidth).</summary>
        public float Slider(string label, float value, float min, float max, string format, bool log = false, Color? color = null)
        {
            var head = Row(18);
            GUI.Label(head, label, Small);
            var vs = new GUIStyle(Mono) { alignment = TextAnchor.UpperRight };
            vs.normal.textColor = color ?? Palette.Accent;
            GUI.Label(head, string.Format(format, value), vs);

            var r = Row(16);
            float Map(float v) => log ? Mathf.InverseLerp(Mathf.Log(min), Mathf.Log(max), Mathf.Log(Mathf.Max(v, 1e-6f))) : Mathf.InverseLerp(min, max, v);
            float Unmap(float t) => log ? Mathf.Exp(Mathf.Lerp(Mathf.Log(min), Mathf.Log(max), t)) : Mathf.Lerp(min, max, t);

            float t01 = Map(value);
            var track = new Rect(r.x, r.y + r.height * 0.5f - S(3), r.width, S(6));
            Pill(track, Palette.PanelEdge);
            Pill(new Rect(track.x, track.y, Mathf.Max(track.height, track.width * t01), track.height), (color ?? Palette.Accent).WithAlpha(0.85f));
            float kx = track.x + track.width * t01;
            Pill(new Rect(kx - S(7), r.y + r.height * 0.5f - S(7), S(14), S(14)), Palette.Text);

            float nt = GUI.HorizontalSlider(r, t01, 0f, 1f, _sliderStyle, _thumbStyle);
            return Mathf.Approximately(nt, t01) ? value : Unmap(nt);
        }

        public int IntSlider(string label, int value, int min, int max, string format, int step = 1)
        {
            float v = Slider(label, value, min, max, format);
            int iv = Mathf.RoundToInt((v - min) / step) * step + min;
            return Mathf.Clamp(iv, min, max);
        }

        /// <summary>Horizontal fill bar with optional budget marker.</summary>
        public void Bar(float fraction, Color color, string leftText, string rightText, float height = 14)
        {
            if (!string.IsNullOrEmpty(leftText) || !string.IsNullOrEmpty(rightText))
            {
                var head = Row(18);
                GUI.Label(head, leftText, Small);
                var vs = new GUIStyle(Mono) { alignment = TextAnchor.UpperRight };
                vs.normal.textColor = color;
                GUI.Label(head, rightText, vs);
            }
            var r = Row(height);
            Pill(r, Palette.PanelEdge.WithAlpha(0.7f));
            float f = Mathf.Clamp01(fraction);
            if (f > 0) Pill(new Rect(r.x, r.y, Mathf.Max(r.height, r.width * f), r.height), color);
            if (fraction > 1f)
            {
                // Over budget: hatch the bar red and show a marker at 100 %.
                Pill(r, Palette.Warning.WithAlpha(0.35f + 0.25f * Mathf.Sin(Time.unscaledTime * 8f)));
            }
        }

        /// <summary>Simple area chart of a series.</summary>
        public void Sparkline(RingSeries s, float max, Color color, float height = 38, float budgetLine = -1)
        {
            var r = Row(height);
            Fill(r, Palette.Background.WithAlpha(0.55f));
            if (s.Count == 0 || max <= 0) return;
            float bw = r.width / s.Capacity;
            int start = s.Capacity - s.Count;
            for (int i = 0; i < s.Count; i++)
            {
                float v = Mathf.Clamp01(s[i] / max);
                float h = v * r.height;
                Fill(new Rect(r.x + (start + i) * bw, r.yMax - h, Mathf.Ceil(bw), h), color.WithAlpha(0.35f + 0.5f * v));
            }
            if (budgetLine > 0)
            {
                float y = r.yMax - Mathf.Clamp01(budgetLine / max) * r.height;
                Fill(new Rect(r.x, y, r.width, Mathf.Max(1, S(1))), Palette.Warning.WithAlpha(0.9f));
            }
        }

        /// <summary>Flowing row of colour chips + labels.</summary>
        public void Legend((Color color, string label)[] items)
        {
            float x = _x, y = _y, rowH = S(18);
            foreach (var (c, label) in items)
            {
                float lw = Small.CalcSize(new GUIContent(label)).x;
                float w = S(16) + lw + S(12);
                if (x + w > _x + _w) { x = _x; y += rowH + S(2); }
                Pill(new Rect(x, y + S(4), S(11), S(11)), c);
                GUI.Label(new Rect(x + S(16), y, lw + S(4), rowH), label, Small);
                x += w;
            }
            _y = y + rowH + S(Gap);
        }

        public bool Button(Rect r, string text, bool active)
            => GUI.Button(r, text, active ? TabActive : Tab);

        public bool ButtonRow(string text)
        {
            var r = Row(26);
            return GUI.Button(r, text, Tab);
        }

        // ------------------------------------------------------------------ drawing primitives

        public void Fill(Rect r, Color c)
        {
            if (Event.current.type != EventType.Repaint) return;
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = old;
        }

        public void Pill(Rect r, Color c)
        {
            if (Event.current.type != EventType.Repaint) return;
            var old = GUI.color;
            GUI.color = Color.white;
            // IMGUI can draw rounded rects natively: zero border widths = filled, radius = half height.
            float radius = Mathf.Min(r.height, r.width) * 0.5f;
            GUI.DrawTexture(r, _white, ScaleMode.StretchToFill, true, 0, c, Vector4.zero, radius);
            GUI.color = old;
        }

        public void Frame(Rect r, Color c, float t = 1)
        {
            t = Mathf.Max(1, S(t));
            Fill(new Rect(r.x, r.y, r.width, t), c);
            Fill(new Rect(r.x, r.yMax - t, r.width, t), c);
            Fill(new Rect(r.x, r.y, t, r.height), c);
            Fill(new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        public void Panel(Rect r)
        {
            if (Event.current.type == EventType.Repaint) _panelStyle.Draw(r, false, false, false, false);
        }

        public void Shadowed(Rect r, string text, GUIStyle style, Color color)
        {
            var old = style.normal.textColor;
            style.normal.textColor = new Color(0, 0, 0, 0.8f * color.a);
            GUI.Label(new Rect(r.x + S(1.5f), r.y + S(1.5f), r.width, r.height), text, style);
            style.normal.textColor = color;
            GUI.Label(r, text, style);
            style.normal.textColor = old;
        }

        /// <summary>Text anchored to a world position.</summary>
        public void WorldLabel(Camera cam, Vector3 world, string text, Color color, GUIStyle style = null)
        {
            if (Event.current.type != EventType.Repaint) return;
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0) return;
            style ??= Center;
            Vector2 size = style.CalcSize(new GUIContent(text));
            var r = new Rect(sp.x - size.x * 0.5f, Screen.height - sp.y - size.y * 0.5f, size.x, size.y);
            Shadowed(r, text, style, color);
        }

        /// <summary>Rect allocated from the layout for a custom drawing (e.g. minimap).</summary>
        public Rect Custom(float height) => Row(height);
    }
}
