using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimHelper
{
    // Shared look of the menu and the map panel. The font is the serif Valheim itself renders Cyrillic with
    // (Noto Serif from the game's loaded assets, a system serif if it is missing), and every control is drawn
    // procedurally in dark wood and amber tones. Nothing is downloaded or taken from other sources.
    // Must be requested from inside OnGUI.
    internal static class HelperSkin
    {
        internal static readonly Color TextColor = new Color(0.93f, 0.87f, 0.74f);
        internal static readonly Color TextBright = new Color(1f, 0.95f, 0.84f);
        internal static readonly Color AccentColor = new Color(1f, 0.8f, 0.42f);

        private static readonly Color s_border = new Color(0.50f, 0.38f, 0.22f, 1f);
        private static readonly Color s_borderLight = new Color(0.86f, 0.66f, 0.34f, 1f);
        private static readonly Color s_darkText = new Color(0.14f, 0.09f, 0.04f, 1f);

        private static GUISkin s_skin;
        private static Texture2D s_white;
        private static Texture2D s_circle;
        private static Texture2D s_ring;

        internal static GUIStyle Section { get; private set; }
        internal static GUIStyle Tooltip { get; private set; }
        internal static GUIStyle Header { get; private set; }

        internal static Texture2D White => s_white != null ? s_white : (s_white = MakeTexture(Color.white));

        // White anti-aliased disc and ring, tinted with GUI.color when drawn.
        internal static Texture2D Circle => s_circle != null ? s_circle : (s_circle = MakeCircle(64, 0f));

        internal static Texture2D Ring => s_ring != null ? s_ring : (s_ring = MakeCircle(64, 5f));

        // White arrow pointing right, tinted with GUI.color and rotated when drawn.
        internal static Texture2D Arrow => s_arrow != null ? s_arrow : (s_arrow = MakeArrow(64));

        private static Texture2D s_arrow;

        // White padlock, tinted with GUI.color when drawn.
        internal static Texture2D Lock => s_lock != null ? s_lock : (s_lock = MakeLock(32));

        private static Texture2D s_lock;

        internal static GUISkin Get()
        {
            if (s_skin != null)
            {
                return s_skin;
            }
            GUISkin skin = UnityEngine.Object.Instantiate(GUI.skin);
            skin.hideFlags = HideFlags.HideAndDontSave;
            Font font = FindFont();
            if (font != null)
            {
                skin.font = font;
            }

            GUIStyle box = skin.box;
            SetAll(box, Rounded(32, 32, 6f, C(0.105f, 0.082f, 0.058f, 0.96f), C(0.07f, 0.055f, 0.04f, 0.96f), s_border, 1f), TextColor);
            box.border = new RectOffset(8, 8, 8, 8);
            box.padding = new RectOffset(10, 10, 8, 8);
            box.richText = true;
            box.fontSize = 15;

            GUIStyle window = skin.window;
            SetAll(window, WindowTexture(), AccentColor);
            window.border = new RectOffset(9, 9, 36, 9);
            window.padding = new RectOffset(12, 12, 42, 10);
            window.contentOffset = new Vector2(0f, -33f);
            window.overflow = new RectOffset(0, 0, 0, 0);
            window.alignment = TextAnchor.UpperCenter;
            window.fontSize = 17;
            window.fontStyle = FontStyle.Bold;

            GUIStyle button = skin.button;
            Texture2D buttonNormal = Rounded(24, 24, 4f, C(0.31f, 0.23f, 0.14f), C(0.20f, 0.145f, 0.085f), s_border, 1f);
            Texture2D buttonHover = Rounded(24, 24, 4f, C(0.41f, 0.31f, 0.19f), C(0.27f, 0.195f, 0.11f), s_borderLight, 1f);
            Texture2D buttonActive = Rounded(24, 24, 4f, C(0.18f, 0.13f, 0.08f), C(0.27f, 0.19f, 0.11f), s_borderLight, 1f);
            Texture2D buttonOn = Rounded(24, 24, 4f, C(0.80f, 0.60f, 0.28f), C(0.58f, 0.41f, 0.17f), C(1f, 0.86f, 0.55f), 1f);
            Texture2D buttonOnHover = Rounded(24, 24, 4f, C(0.88f, 0.68f, 0.34f), C(0.64f, 0.46f, 0.20f), C(1f, 0.9f, 0.62f), 1f);
            SetState(button.normal, buttonNormal, TextColor);
            SetState(button.hover, buttonHover, TextBright);
            SetState(button.active, buttonActive, AccentColor);
            SetState(button.focused, buttonNormal, TextColor);
            SetState(button.onNormal, buttonOn, s_darkText);
            SetState(button.onHover, buttonOnHover, s_darkText);
            SetState(button.onActive, buttonOn, s_darkText);
            SetState(button.onFocused, buttonOn, s_darkText);
            button.border = new RectOffset(6, 6, 6, 6);
            // The menu font is wider than Unity's own, and many buttons have a fixed width: the text may run into the
            // padding instead of losing its last letters («Виконат», «Головн»).
            button.padding = new RectOffset(7, 7, 5, 6);
            button.clipping = TextClipping.Overflow;
            button.margin = new RectOffset(3, 3, 3, 3);
            button.overflow = new RectOffset(0, 0, 0, 0);
            button.alignment = TextAnchor.MiddleCenter;
            button.fontSize = 15;
            button.richText = true;
            button.wordWrap = false;

            // The checkbox sits unscaled in the top-left corner of the texture; the rest is transparent,
            // so multi-line labels keep a square box.
            GUIStyle toggle = skin.toggle;
            Texture2D boxOff = Checkbox(false, false);
            Texture2D boxOffHot = Checkbox(false, true);
            Texture2D boxOn = Checkbox(true, false);
            Texture2D boxOnHot = Checkbox(true, true);
            SetState(toggle.normal, boxOff, TextColor);
            SetState(toggle.hover, boxOffHot, TextBright);
            SetState(toggle.active, boxOffHot, TextBright);
            SetState(toggle.focused, boxOff, TextColor);
            SetState(toggle.onNormal, boxOn, TextBright);
            SetState(toggle.onHover, boxOnHot, TextBright);
            SetState(toggle.onActive, boxOnHot, TextBright);
            SetState(toggle.onFocused, boxOn, TextBright);
            toggle.border = new RectOffset(26, 1, 21, 0);
            toggle.padding = new RectOffset(28, 4, 3, 3);
            toggle.margin = new RectOffset(4, 4, 3, 3);
            toggle.overflow = new RectOffset(0, 0, 0, 0);
            toggle.contentOffset = Vector2.zero;
            toggle.fixedHeight = 0f;
            toggle.alignment = TextAnchor.UpperLeft;
            toggle.imagePosition = ImagePosition.ImageLeft;
            toggle.fontSize = 15;
            toggle.richText = true;
            toggle.wordWrap = true;

            GUIStyle field = skin.textField;
            Texture2D fieldNormal = Rounded(24, 24, 4f, C(0.045f, 0.036f, 0.028f, 0.98f), C(0.06f, 0.048f, 0.036f, 0.98f), s_border, 1f);
            Texture2D fieldFocus = Rounded(24, 24, 4f, C(0.045f, 0.036f, 0.028f, 0.98f), C(0.06f, 0.048f, 0.036f, 0.98f), s_borderLight, 1f);
            SetState(field.normal, fieldNormal, TextBright);
            SetState(field.hover, fieldNormal, TextBright);
            SetState(field.active, fieldFocus, TextBright);
            SetState(field.focused, fieldFocus, TextBright);
            SetState(field.onNormal, fieldNormal, TextBright);
            SetState(field.onFocused, fieldFocus, TextBright);
            field.border = new RectOffset(5, 5, 5, 5);
            field.padding = new RectOffset(8, 8, 5, 5);
            field.margin = new RectOffset(3, 3, 3, 3);
            field.overflow = new RectOffset(0, 0, 0, 0);
            field.fontSize = 15;
            skin.settings.cursorColor = AccentColor;
            skin.settings.selectionColor = new Color(0.85f, 0.6f, 0.25f, 0.45f);

            GUIStyle slider = skin.horizontalSlider;
            SetAll(slider, Rounded(16, 6, 3f, C(0.04f, 0.032f, 0.024f, 0.95f), C(0.06f, 0.048f, 0.036f, 0.95f), s_border, 1f), TextColor);
            slider.border = new RectOffset(4, 4, 2, 2);
            slider.fixedHeight = 16f;
            slider.overflow = new RectOffset(0, 0, -5, -5);
            slider.padding = new RectOffset(0, 0, 0, 0);
            slider.margin = new RectOffset(4, 4, 6, 6);
            GUIStyle thumb = skin.horizontalSliderThumb;
            Texture2D thumbNormal = Rounded(14, 18, 3f, C(0.98f, 0.8f, 0.45f), C(0.72f, 0.52f, 0.24f), C(0.25f, 0.17f, 0.08f), 1f);
            Texture2D thumbHot = Rounded(14, 18, 3f, C(1f, 0.88f, 0.58f), C(0.82f, 0.6f, 0.3f), C(0.25f, 0.17f, 0.08f), 1f);
            SetAll(thumb, thumbNormal, TextColor);
            thumb.hover.background = thumbHot;
            thumb.active.background = thumbHot;
            thumb.border = new RectOffset(4, 4, 4, 4);
            thumb.fixedWidth = 14f;
            thumb.fixedHeight = 16f;
            thumb.overflow = new RectOffset(0, 0, 1, 1);
            thumb.padding = new RectOffset(0, 0, 0, 0);

            Texture2D track = Rounded(10, 24, 4f, C(0.03f, 0.025f, 0.02f, 0.6f), C(0.03f, 0.025f, 0.02f, 0.6f), Color.clear, 0f);
            Texture2D bar = Rounded(10, 24, 4f, C(0.62f, 0.47f, 0.27f), C(0.45f, 0.33f, 0.18f), Color.clear, 0f);
            Texture2D barHot = Rounded(10, 24, 4f, C(0.78f, 0.6f, 0.34f), C(0.58f, 0.43f, 0.23f), Color.clear, 0f);
            foreach (GUIStyle scrollbar in new[] { skin.verticalScrollbar, skin.horizontalScrollbar })
            {
                SetAll(scrollbar, track, TextColor);
                scrollbar.border = new RectOffset(4, 4, 4, 4);
                scrollbar.padding = new RectOffset(0, 0, 0, 0);
                scrollbar.overflow = new RectOffset(0, 0, 0, 0);
            }
            skin.verticalScrollbar.fixedWidth = 10f;
            skin.verticalScrollbar.margin = new RectOffset(4, 1, 2, 2);
            skin.horizontalScrollbar.fixedHeight = 10f;
            skin.horizontalScrollbar.margin = new RectOffset(2, 2, 4, 1);
            foreach (GUIStyle scrollThumb in new[] { skin.verticalScrollbarThumb, skin.horizontalScrollbarThumb })
            {
                SetAll(scrollThumb, bar, TextColor);
                scrollThumb.hover.background = barHot;
                scrollThumb.active.background = barHot;
                scrollThumb.border = new RectOffset(4, 4, 5, 5);
                scrollThumb.padding = new RectOffset(0, 0, 0, 0);
                scrollThumb.overflow = new RectOffset(0, 0, 0, 0);
            }
            skin.verticalScrollbarThumb.fixedWidth = 10f;
            skin.horizontalScrollbarThumb.fixedHeight = 10f;
            foreach (GUIStyle arrow in new[] { skin.verticalScrollbarUpButton, skin.verticalScrollbarDownButton, skin.horizontalScrollbarLeftButton, skin.horizontalScrollbarRightButton })
            {
                SetAll(arrow, null, TextColor);
                arrow.fixedWidth = 0f;
                arrow.fixedHeight = 0f;
                arrow.margin = new RectOffset(0, 0, 0, 0);
            }
            skin.scrollView.normal.background = null;

            GUIStyle label = skin.label;
            label.normal.textColor = TextColor;
            label.fontSize = 15;
            label.richText = true;
            label.wordWrap = true;
            label.padding = new RectOffset(2, 2, 2, 2);
            label.margin = new RectOffset(4, 4, 2, 2);

            Section = new GUIStyle(box);
            SetAll(Section, Rounded(32, 32, 6f, C(0.17f, 0.13f, 0.085f, 0.94f), C(0.12f, 0.092f, 0.062f, 0.94f), s_border, 1f), TextColor);
            Section.margin = new RectOffset(2, 2, 4, 6);

            Tooltip = new GUIStyle(box)
            {
                alignment = TextAnchor.UpperLeft,
                wordWrap = false,
                richText = true,
                padding = new RectOffset(11, 11, 8, 8),
            };
            SetAll(Tooltip, Rounded(32, 32, 6f, C(0.06f, 0.048f, 0.035f, 0.97f), C(0.04f, 0.032f, 0.024f, 0.97f), s_borderLight, 1f), TextBright);

            Header = new GUIStyle(label) { fontSize = 18, fontStyle = FontStyle.Bold, wordWrap = false };
            Header.normal.textColor = AccentColor;

            s_skin = skin;
            return s_skin;
        }

        internal static Texture2D MakeTexture(Color color)
        {
            Texture2D texture = NewTexture(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        // ---------------------------------------------------------------- font

        private static Font FindFont()
        {
            // The game's Noto Serif has no Chinese characters: the menu in Chinese takes a system font that has them
            if (Lang.Code == "zh")
            {
                try
                {
                    Font cjk = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun",
                        "Noto Sans CJK SC", "Noto Sans SC", "Source Han Sans SC", "PingFang SC", "Arial Unicode MS" }, 15);
                    if (cjk != null)
                    {
                        Log.Info("UI font: system CJK");
                        return cjk;
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e);
                }
            }
            try
            {
                Font best = null;
                int bestScore = 0;
                List<string> names = new List<string>();
                foreach (Font candidate in Resources.FindObjectsOfTypeAll<Font>())
                {
                    if (candidate == null)
                    {
                        continue;
                    }
                    names.Add(candidate.name);
                    int score = ScoreFont(candidate);
                    if (score > bestScore)
                    {
                        best = candidate;
                        bestScore = score;
                    }
                }
                Log.Info("Fonts in memory: " + string.Join(", ", names.ToArray()));
                if (best != null)
                {
                    Log.Info("UI font: " + best.name);
                    return best;
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
            try
            {
                Font system = Font.CreateDynamicFontFromOSFont(new[] { "Georgia", "Cambria", "Palatino Linotype", "Times New Roman" }, 15);
                Log.Info("UI font: system serif");
                return system;
            }
            catch (Exception e)
            {
                Log.Error(e);
                return null;
            }
        }

        // Noto Serif is what the game draws Ukrainian text with; Averia Serif Libre has no Cyrillic,
        // and script-specific Noto variants are skipped.
        private static int ScoreFont(Font font)
        {
            string name = font.name.ToLowerInvariant();
            if (!font.dynamic || !font.HasCharacter('Ж') || !font.HasCharacter('ї'))
            {
                return 0;
            }
            foreach (string skip in new[] { "jp", "kr", "sc", "tc", "armenian", "bengali", "devanagari", "georgian", "hebrew", "thai", "arabic", "mono", "outline", "legacyruntime", "arial" })
            {
                if (name.Contains(skip))
                {
                    return 0;
                }
            }
            if (name.Contains("notoserif") || name.Contains("noto serif"))
            {
                return name.Contains("bold") || name.Contains("italic") || name.Contains("light") ? 2 : 3;
            }
            return name.Contains("averia") ? 2 : 0;
        }

        // ---------------------------------------------------------------- textures

        private static Color C(float r, float g, float b, float a = 1f)
        {
            return new Color(r, g, b, a);
        }

        private static void SetState(GUIStyleState state, Texture2D background, Color text)
        {
            state.background = background;
            state.textColor = text;
        }

        private static void SetAll(GUIStyle style, Texture2D background, Color text)
        {
            foreach (GUIStyleState state in new[] { style.normal, style.hover, style.active, style.focused, style.onNormal, style.onHover, style.onActive, style.onFocused })
            {
                SetState(state, background, text);
            }
        }

        private static Texture2D NewTexture(int width, int height)
        {
            return new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
        }

        // Signed distance to a rounded rectangle, negative inside.
        private static float RoundedDistance(float x, float y, float left, float bottom, float width, float height, float radius)
        {
            float qx = Mathf.Abs(x - (left + width / 2f)) - (width / 2f - radius);
            float qy = Mathf.Abs(y - (bottom + height / 2f)) - (height / 2f - radius);
            float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
            return outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
        }

        private static Color Shade(float distance, float t, Color top, Color bottom, Color border, float borderWidth)
        {
            float coverage = Mathf.Clamp01(0.5f - distance);
            if (coverage <= 0f)
            {
                return Color.clear;
            }
            Color color = Color.Lerp(bottom, top, t);
            if (borderWidth > 0f)
            {
                color = Color.Lerp(color, border, Mathf.Clamp01(distance + borderWidth + 0.5f));
            }
            color.a *= coverage;
            return color;
        }

        private static Texture2D Rounded(int width, int height, float radius, Color top, Color bottom, Color border, float borderWidth)
        {
            Texture2D texture = NewTexture(width, height);
            Color[] pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float distance = RoundedDistance(x + 0.5f, y + 0.5f, 0f, 0f, width, height, radius);
                    pixels[y * width + x] = Shade(distance, (y + 0.5f) / height, top, bottom, border, borderWidth);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        // Window body with a darker header band and an amber line under the title.
        private static Texture2D WindowTexture()
        {
            const int width = 32;
            const int height = 72;
            const int header = 34;
            Texture2D texture = NewTexture(width, height);
            Color[] pixels = new Color[width * height];
            Color headerTop = C(0.22f, 0.155f, 0.09f, 0.98f);
            Color headerBottom = C(0.15f, 0.105f, 0.06f, 0.98f);
            Color line = C(0.72f, 0.54f, 0.28f, 1f);
            Color bodyTop = C(0.10f, 0.078f, 0.055f, 0.97f);
            Color bodyBottom = C(0.07f, 0.055f, 0.04f, 0.97f);
            for (int y = 0; y < height; y++)
            {
                int fromTop = height - 1 - y;
                Color color;
                if (fromTop < header)
                {
                    color = Color.Lerp(headerTop, headerBottom, fromTop / (float)(header - 1));
                }
                else if (fromTop == header)
                {
                    color = line;
                }
                else
                {
                    color = Color.Lerp(bodyTop, bodyBottom, (fromTop - header) / (float)(height - header));
                }
                for (int x = 0; x < width; x++)
                {
                    float distance = RoundedDistance(x + 0.5f, y + 0.5f, 0f, 0f, width, height, 7f);
                    Color pixel = Color.Lerp(color, s_border, Mathf.Clamp01(distance + 1.5f));
                    pixel.a *= Mathf.Clamp01(0.5f - distance);
                    pixels[y * width + x] = pixel;
                }
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private static Texture2D Checkbox(bool on, bool hot)
        {
            const int width = 28;
            const int height = 22;
            const float left = 2f;
            const float bottom = 2f;
            const float box = 18f;
            Texture2D texture = NewTexture(width, height);
            Color[] pixels = new Color[width * height];
            Color border = hot ? s_borderLight : s_border;
            Color top = on ? C(0.95f, 0.74f, 0.38f) : hot ? C(0.10f, 0.078f, 0.056f, 0.97f) : C(0.05f, 0.04f, 0.03f, 0.97f);
            Color low = on ? C(0.70f, 0.50f, 0.22f) : C(0.08f, 0.064f, 0.048f, 0.97f);
            Vector2 a = new Vector2(6.5f, 11.5f);
            Vector2 b = new Vector2(9.5f, 8f);
            Vector2 c = new Vector2(16f, 15.5f);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Vector2 point = new Vector2(x + 0.5f, y + 0.5f);
                    float distance = RoundedDistance(point.x, point.y, left, bottom, box, box, 3.5f);
                    Color color = Shade(distance, (point.y - bottom) / box, top, low, border, 1f);
                    if (on && color.a > 0f)
                    {
                        float stroke = Mathf.Min(SegmentDistance(point, a, b), SegmentDistance(point, b, c));
                        float mark = Mathf.Clamp01(1.6f - stroke);
                        color = Color.Lerp(color, new Color(s_darkText.r, s_darkText.g, s_darkText.b, color.a), mark);
                    }
                    pixels[y * width + x] = color;
                }
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private static float SegmentDistance(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / ab.sqrMagnitude);
            return (point - (a + ab * t)).magnitude;
        }

        private static Texture2D MakeLock(int size)
        {
            Texture2D texture = NewTexture(size, size);
            Color[] pixels = new Color[size * size];
            float unit = size / 32f;
            Vector2 shackleCenter = new Vector2(16f, 17f) * unit;
            Vector2 keyhole = new Vector2(16f, 9.5f) * unit;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 point = new Vector2(x + 0.5f, y + 0.5f);
                    float body = Mathf.Clamp01(0.5f - RoundedDistance(point.x, point.y, 5f * unit, 2f * unit, 22f * unit, 15f * unit, 3f * unit));
                    body *= 1f - Mathf.Clamp01(2.4f * unit - (point - keyhole).magnitude + 0.5f);
                    float shackle = 0f;
                    if (point.y >= 15f * unit)
                    {
                        float radius = (point - shackleCenter).magnitude;
                        shackle = Mathf.Clamp01(0.5f - Mathf.Max(radius - 8.5f * unit, 5.2f * unit - radius));
                    }
                    pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Max(body, shackle));
                }
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        // A triangle from the left edge to a point on the right, anti-aliased by distance to its sides.
        private static Texture2D MakeArrow(int size)
        {
            Texture2D texture = NewTexture(size, size);
            Color[] pixels = new Color[size * size];
            Vector2 tip = new Vector2(size - 2f, size / 2f);
            Vector2 top = new Vector2(4f, size - 6f);
            Vector2 bottom = new Vector2(4f, 6f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                    float inside = Mathf.Min(Side(bottom, tip, p), Mathf.Min(Side(tip, top, p), Side(top, bottom, p)));
                    pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(inside + 0.5f));
                }
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        // Signed distance of p to the line a→b, positive on the left (inside a counter-clockwise triangle).
        private static float Side(Vector2 a, Vector2 b, Vector2 p)
        {
            Vector2 edge = b - a;
            return (edge.x * (p.y - a.y) - edge.y * (p.x - a.x)) / edge.magnitude;
        }

        private static Texture2D MakeCircle(int size, float ringWidth)
        {
            Texture2D texture = NewTexture(size, size);
            Color[] pixels = new Color[size * size];
            float center = size / 2f;
            float radius = size / 2f - 1f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = new Vector2(x + 0.5f - center, y + 0.5f - center).magnitude;
                    float alpha = Mathf.Clamp01(radius - distance + 0.5f);
                    if (ringWidth > 0f)
                    {
                        alpha = Mathf.Min(alpha, Mathf.Clamp01(distance - (radius - ringWidth) + 0.5f));
                    }
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }
    }
}
