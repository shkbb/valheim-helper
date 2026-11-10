using System.Collections.Generic;
using UnityEngine;

namespace ValheimHelper.Wiki
{
    // Text styles of the wiki reader (sizes × bold × italic) and cached text widths: the layout measures every word.
    internal static class WikiStyles
    {
        internal const int Body = 0;
        internal const int Small = 1;
        internal const int H4 = 2;
        internal const int H3 = 3;
        internal const int H2 = 4;
        internal const int Title = 5;
        private const int Sizes = 6;

        private static readonly int[] s_fontSizes = { 16, 14, 17, 19, 23, 30 };
        private static readonly float[] s_lineFactors = { 1.5f, 1.42f, 1.4f, 1.35f, 1.3f, 1.25f };

        internal static readonly Color Text = new Color(0.9f, 0.86f, 0.77f);
        internal static readonly Color Bright = new Color(1f, 0.96f, 0.88f);
        internal static readonly Color Dim = new Color(0.7f, 0.66f, 0.58f);
        internal static readonly Color Accent = new Color(1f, 0.78f, 0.42f);
        internal static readonly Color Link = new Color(0.6f, 0.8f, 1f);
        internal static readonly Color LinkHover = new Color(0.82f, 0.92f, 1f);

        private static GUIStyle[] s_styles;
        private static Font s_font;
        private static Dictionary<string, float>[] s_widths;
        private static float[] s_spaces;
        private static readonly GUIContent s_content = new GUIContent();

        internal static int Index(int size, bool bold, bool italic)
        {
            return size * 4 + (bold ? 1 : 0) + (italic ? 2 : 0);
        }

        internal static int SizeOf(int style)
        {
            return style / 4;
        }

        // Called at the start of each draw: a new skin font (the menu skin is rebuilt sometimes) resets the styles.
        internal static void Ensure()
        {
            Font font = GUI.skin != null ? GUI.skin.font : null;
            if (s_styles != null && font == s_font)
            {
                return;
            }
            s_font = font;
            s_styles = new GUIStyle[Sizes * 4];
            s_widths = new Dictionary<string, float>[Sizes * 4];
            s_spaces = new float[Sizes * 4];
            for (int i = 0; i < s_styles.Length; i++)
            {
                GUIStyle style = new GUIStyle(GUI.skin.label)
                {
                    font = font,
                    fontSize = s_fontSizes[i / 4],
                    fontStyle = (i & 3) == 3 ? FontStyle.BoldAndItalic : (i & 1) == 1 ? FontStyle.Bold : (i & 2) == 2 ? FontStyle.Italic : FontStyle.Normal,
                    wordWrap = false,
                    richText = false,
                    clipping = TextClipping.Overflow,
                    alignment = TextAnchor.MiddleLeft,
                    padding = new RectOffset(0, 0, 0, 0),
                    margin = new RectOffset(0, 0, 0, 0),
                    border = new RectOffset(0, 0, 0, 0),
                    overflow = new RectOffset(0, 0, 0, 0),
                    contentOffset = Vector2.zero,
                    fixedWidth = 0f,
                    fixedHeight = 0f,
                    stretchWidth = false,
                    stretchHeight = false,
                };
                style.normal.background = null;
                style.normal.textColor = Color.white;
                style.hover.textColor = Color.white;
                s_styles[i] = style;
                s_widths[i] = new Dictionary<string, float>();
                s_spaces[i] = -1f;
            }
        }

        internal static GUIStyle Style(int index)
        {
            return s_styles[index];
        }

        internal static float Width(int style, string text)
        {
            Dictionary<string, float> cache = s_widths[style];
            if (cache.TryGetValue(text, out float width))
            {
                return width;
            }
            if (cache.Count > 40000)
            {
                cache.Clear();
            }
            s_content.text = text;
            width = s_styles[style].CalcSize(s_content).x;
            cache[text] = width;
            return width;
        }

        internal static float Space(int style)
        {
            if (s_spaces[style] < 0f)
            {
                s_spaces[style] = Mathf.Max(2f, Width(style, "a a") - Width(style, "aa"));
            }
            return s_spaces[style];
        }

        internal static float LineHeight(int size)
        {
            return Mathf.Ceil(s_fontSizes[size] * s_lineFactors[size]);
        }

        internal static float FontSize(int size)
        {
            return s_fontSizes[size];
        }
    }
}
