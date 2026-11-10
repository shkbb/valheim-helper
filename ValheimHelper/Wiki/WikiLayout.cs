using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ValheimHelper.Wiki
{
    internal enum ItemKind
    {
        Text,
        Image,
        Fill,
        Tab,
    }

    // One thing to draw at a place of the page: a run of text of one style, a picture, a filled rectangle or an
    // infobox tab.
    internal struct LayoutItem
    {
        public ItemKind Kind;
        public Rect Rect;
        public string Text;
        public int Style;
        public Color Color;
        public string Link;
        public int Block;
        public int Tab;
        public bool Selected;
    }

    // An article laid out for a width: text flows word by word, wraps, and goes around an infobox floating on the right
    // when there is room for both; tables share the width between their columns; galleries take as many columns as
    // fit. Laid out again whenever the width changes, so the page follows the window size.
    internal sealed class WikiLayout
    {
        internal readonly List<LayoutItem> Items = new List<LayoutItem>();
        internal readonly Dictionary<string, float> Anchors = new Dictionary<string, float>();
        internal float Height;
        internal float Width;

        private List<LayoutItem> m_out;
        private Func<string, bool> m_linkExists;
        private Dictionary<int, int> m_tabs;
        private Rect m_float;
        private bool m_hasFloat;

        private const float FloatGap = 18f;
        private const float WideLayout = 660f;
        private const float CellPad = 6f;

        private struct Token
        {
            public byte Type; // 0 word, 1 space, 2 break, 3 icon
            public string Text;
            public bool Bold;
            public bool Italic;
            public string Link;
        }

        private struct Piece
        {
            public float X;
            public float W;
            public string Text;
            public int Style;
            public string Link;
            public bool Icon;
            public bool SpaceBefore;
        }

        internal static WikiLayout Build(WikiArticle article, float width, Func<string, bool> linkExists, Dictionary<int, int> tabs, string footer)
        {
            WikiLayout layout = new WikiLayout { Width = width, m_linkExists = linkExists, m_tabs = tabs };
            layout.m_out = layout.Items;
            layout.Lay(article, footer);
            return layout;
        }

        // ---------------------------------------------------------------- blocks

        private void Lay(WikiArticle article, string footer)
        {
            float y = 0f;
            y = Flow(Tokens(article.Title, false, false), y, WikiStyles.Title, WikiStyles.Bright, false, 0f, Width, false);
            AddFill(new Rect(0f, y + 4f, Width, 1f), new Color(WikiStyles.Accent.r, WikiStyles.Accent.g, WikiStyles.Accent.b, 0.45f));
            y += 16f;
            int[] counters = new int[10];
            for (int i = 0; i < article.Blocks.Count; i++)
            {
                WikiBlock block = article.Blocks[i];
                if (block.Kind != BlockKind.ListItem)
                {
                    Array.Clear(counters, 0, counters.Length);
                }
                switch (block.Kind)
                {
                    case BlockKind.Heading:
                        y = Heading(block, y);
                        break;
                    case BlockKind.Paragraph:
                        y = Flow(Tokens(block.Text, false, false), y, WikiStyles.Body, WikiStyles.Text, false, 0f, Width, true) + 10f;
                        break;
                    case BlockKind.ListItem:
                        y = ListItem(block, y, counters);
                        bool lastItem = i + 1 >= article.Blocks.Count || article.Blocks[i + 1].Kind != BlockKind.ListItem;
                        y += lastItem ? 10f : 3f;
                        break;
                    case BlockKind.Quote:
                        y = Bar(block.Text, y, WikiStyles.Body, WikiStyles.Text, true, WikiStyles.Accent) + 4f;
                        break;
                    case BlockKind.QuoteSource:
                        y = Flow(Tokens("— " + block.Text, false, true), y, WikiStyles.Small, WikiStyles.Dim, false, 22f, Width, true) + 12f;
                        break;
                    case BlockKind.Notice:
                        y = Bar(block.Text, y, WikiStyles.Small, WikiStyles.Dim, true, new Color(0.5f, 0.62f, 0.8f)) + 8f;
                        break;
                    case BlockKind.Rule:
                        AddFill(new Rect(0f, y + 4f, RightAt(y, 1f), 1f), new Color(1f, 1f, 1f, 0.15f));
                        y += 12f;
                        break;
                    case BlockKind.Image:
                        y = Picture(block.Image, y) + 10f;
                        break;
                    case BlockKind.Gallery:
                        y = Gallery(block.Gallery, y) + 10f;
                        break;
                    case BlockKind.Infobox:
                        y = Infobox(block, i, y);
                        break;
                    case BlockKind.Table:
                        y = Table(block, y) + 14f;
                        break;
                    case BlockKind.Columns:
                        y = Columns(block.Items, y) + 10f;
                        break;
                }
            }
            if (!string.IsNullOrEmpty(footer))
            {
                y = Mathf.Max(y, FloatBottom()) + 16f;
                AddFill(new Rect(0f, y, Width, 1f), new Color(1f, 1f, 1f, 0.12f));
                y = Flow(Tokens(footer, false, false), y + 8f, WikiStyles.Small, WikiStyles.Dim, false, 0f, Width, false);
            }
            Height = Mathf.Max(y, FloatBottom()) + 8f;
        }

        private float Heading(WikiBlock block, float y)
        {
            y += block.Level == 2 ? 14f : 8f;
            if (!string.IsNullOrEmpty(block.Anchor))
            {
                Anchors[block.Anchor] = y;
            }
            string plain = Plain(block.Text);
            if (plain.Length > 0 && !Anchors.ContainsKey(plain.Replace(' ', '_')))
            {
                Anchors[plain.Replace(' ', '_')] = y;
            }
            int size = block.Level == 2 ? WikiStyles.H2 : block.Level == 3 ? WikiStyles.H3 : WikiStyles.H4;
            Color color = block.Level == 2 ? WikiStyles.Accent : WikiStyles.Bright;
            y = Flow(Tokens(block.Text, block.Level == 4, false), y, size, color, false, 0f, Width, true);
            if (block.Level == 2)
            {
                AddFill(new Rect(0f, y + 1f, RightAt(y, 1f), 1f), new Color(WikiStyles.Accent.r, WikiStyles.Accent.g, WikiStyles.Accent.b, 0.3f));
                y += 8f;
            }
            else
            {
                y += 3f;
            }
            return y;
        }

        private float ListItem(WikiBlock block, float y, int[] counters)
        {
            int level = Mathf.Clamp(block.Level, 1, 9);
            counters[level]++;
            for (int deeper = level + 1; deeper < counters.Length; deeper++)
            {
                counters[deeper] = 0;
            }
            float indent = 24f * level;
            string marker = block.Ordered ? counters[level] + "." : level % 2 == 1 ? "•" : "◦";
            int style = WikiStyles.Index(WikiStyles.Body, false, false);
            float markerWidth = WikiStyles.Width(style, marker);
            float line = WikiStyles.LineHeight(WikiStyles.Body);
            AddText(new Rect(indent - 8f - markerWidth, y, markerWidth, line), marker, style, block.Ordered ? WikiStyles.Dim : WikiStyles.Accent, null);
            return Flow(Tokens(block.Text, false, false), y, WikiStyles.Body, WikiStyles.Text, false, indent, Width, true);
        }

        // Quote or notice: text with a bar on its left.
        private float Bar(string text, float y, int size, Color color, bool italic, Color bar)
        {
            float top = y;
            y = Flow(Tokens(text, false, italic), y + 4f, size, color, false, 22f, Width - 10f, true) + 4f;
            AddFill(new Rect(6f, top, 3f, y - top), new Color(bar.r, bar.g, bar.b, 0.6f));
            return y;
        }

        private float Picture(WikiImage image, float y)
        {
            float available = RightAt(y, 40f);
            if (available < 160f && m_hasFloat)
            {
                y = ClearFloat(y);
                available = Width;
            }
            Vector2 size = Fit(image, Mathf.Min(available, 480f), 420f);
            float x = (available - size.x) / 2f;
            AddImage(new Rect(x, y, size.x, size.y), image.Key);
            y += size.y;
            if (!string.IsNullOrEmpty(image.Caption))
            {
                y = Flow(Tokens(image.Caption, false, true), y + 4f, WikiStyles.Small, WikiStyles.Dim, true, Mathf.Max(0f, x - 20f), Mathf.Min(available, x + size.x + 20f), false);
            }
            return y;
        }

        private float Gallery(List<WikiImage> images, float y)
        {
            float available = RightAt(y, 120f);
            if (available < 300f && m_hasFloat)
            {
                y = ClearFloat(y);
                available = Width;
            }
            const float gap = 10f;
            int columns = Mathf.Max(1, Mathf.FloorToInt((available + gap) / (170f + gap)));
            float cell = Mathf.Min(260f, (available - gap * (columns - 1)) / columns);
            float box = Mathf.Round(cell * 0.72f);
            for (int start = 0; start < images.Count; start += columns)
            {
                float rowBottom = y + box;
                for (int c = 0; c < columns && start + c < images.Count; c++)
                {
                    WikiImage image = images[start + c];
                    float x = c * (cell + gap);
                    AddFill(new Rect(x, y, cell, box), new Color(0f, 0f, 0f, 0.25f));
                    Vector2 size = Fit(image, cell - 6f, box - 6f);
                    AddImage(new Rect(x + (cell - size.x) / 2f, y + (box - size.y) / 2f, size.x, size.y), image.Key);
                    if (!string.IsNullOrEmpty(image.Caption))
                    {
                        float bottom = Flow(Tokens(image.Caption, false, false), y + box + 3f, WikiStyles.Small, WikiStyles.Dim, true, x, x + cell, false);
                        rowBottom = Mathf.Max(rowBottom, bottom);
                    }
                }
                y = rowBottom + gap;
            }
            return y;
        }

        private float Columns(List<string> items, float y)
        {
            if (items == null || items.Count == 0)
            {
                return y;
            }
            y = ClearFloat(y);
            int columns = Mathf.Clamp(Mathf.FloorToInt(Width / 250f), 1, 5);
            int perColumn = Mathf.CeilToInt(items.Count / (float)columns);
            float columnWidth = Width / columns;
            float bottom = y;
            for (int c = 0; c < columns; c++)
            {
                float cy = y;
                for (int i = c * perColumn; i < Mathf.Min(items.Count, (c + 1) * perColumn); i++)
                {
                    cy = Flow(Tokens(items[i], false, false), cy, WikiStyles.Body, WikiStyles.Text, false, c * columnWidth, (c + 1) * columnWidth - 12f, false) + 2f;
                }
                bottom = Mathf.Max(bottom, cy);
            }
            return bottom;
        }

        // ---------------------------------------------------------------- infobox

        private float Infobox(WikiBlock block, int index, float y)
        {
            bool wide = Width >= WideLayout;
            float width = wide ? Mathf.Clamp(Width * 0.36f, 270f, 340f) : Mathf.Min(Width, 440f);
            if (m_hasFloat)
            {
                y = ClearFloat(y);
            }
            float x = wide ? Width - width : (Width - width) / 2f;
            int background = AddFill(new Rect(x, y, width, 0f), new Color(0.09f, 0.075f, 0.06f, 0.96f));
            float cy = y;
            if (!string.IsNullOrEmpty(block.Text))
            {
                int title = AddFill(new Rect(x, cy, width, 0f), new Color(0.3f, 0.21f, 0.1f, 1f));
                cy = Flow(Tokens(block.Text, true, false), cy + 6f, WikiStyles.H3, WikiStyles.Bright, true, x + 8f, x + width - 8f, false) + 6f;
                SetHeight(title, cy - y);
            }
            cy = InfoItems(block.Before, x, width, cy);
            if (block.Tabs.Count > 0)
            {
                int selected = m_tabs != null && m_tabs.TryGetValue(index, out int tab) ? Mathf.Clamp(tab, 0, block.Tabs.Count - 1) : 0;
                if (block.Tabs.Count > 1)
                {
                    int style = WikiStyles.Index(WikiStyles.Small, true, false);
                    float tx = x + 6f;
                    float ty = cy + 6f;
                    for (int t = 0; t < block.Tabs.Count; t++)
                    {
                        string label = Plain(block.Tabs[t].Label);
                        float tw = Mathf.Min(width - 12f, WikiStyles.Width(style, label) + 18f);
                        if (tx + tw > x + width - 6f && tx > x + 6f)
                        {
                            tx = x + 6f;
                            ty += 28f;
                        }
                        m_out.Add(new LayoutItem
                        {
                            Kind = ItemKind.Tab,
                            Rect = new Rect(tx, ty, tw, 24f),
                            Text = label,
                            Style = style,
                            Block = index,
                            Tab = t,
                            Selected = t == selected,
                        });
                        tx += tw + 4f;
                    }
                    cy = ty + 24f + 4f;
                }
                cy = InfoItems(block.Tabs[selected].Items, x, width, cy);
            }
            cy = InfoItems(block.After, x, width, cy) + 6f;
            SetHeight(background, cy - y);
            // a thin frame
            Color frame = new Color(WikiStyles.Accent.r, WikiStyles.Accent.g, WikiStyles.Accent.b, 0.35f);
            AddFill(new Rect(x, y, width, 1f), frame);
            AddFill(new Rect(x, cy - 1f, width, 1f), frame);
            AddFill(new Rect(x, y, 1f, cy - y), frame);
            AddFill(new Rect(x + width - 1f, y, 1f, cy - y), frame);
            if (wide)
            {
                m_float = new Rect(x, y, width, cy - y);
                m_hasFloat = true;
                return y;
            }
            return cy + 14f;
        }

        private float InfoItems(List<InfoItem> items, float x, float width, float cy)
        {
            foreach (InfoItem item in items)
            {
                switch (item.Kind)
                {
                    case 'I':
                        Vector2 size = Fit(item.Image, width - 16f, 300f);
                        AddImage(new Rect(x + (width - size.x) / 2f, cy + 8f, size.x, size.y), item.Image.Key);
                        cy += size.y + 8f;
                        if (!string.IsNullOrEmpty(item.Image.Caption))
                        {
                            cy = Flow(Tokens(item.Image.Caption, false, true), cy + 2f, WikiStyles.Small, WikiStyles.Dim, true, x + 8f, x + width - 8f, false);
                        }
                        cy += 4f;
                        break;
                    case 'H':
                        int header = AddFill(new Rect(x + 1f, cy + 2f, width - 2f, 0f), new Color(0.22f, 0.16f, 0.09f, 1f));
                        float top = cy + 2f;
                        cy = Flow(Tokens(item.Text, true, false), top + 3f, WikiStyles.Small, WikiStyles.Accent, true, x + 8f, x + width - 8f, false) + 3f;
                        SetHeight(header, cy - top);
                        cy += 2f;
                        break;
                    default:
                        float labelWidth = item.Label.Length > 0 ? Mathf.Round(width * 0.38f) : 0f;
                        float bottom = cy + 4f;
                        if (labelWidth > 0f)
                        {
                            bottom = Flow(Tokens(item.Label, true, false), cy + 4f, WikiStyles.Small, WikiStyles.Dim, false, x + 8f, x + labelWidth, false);
                        }
                        float value = Flow(Tokens(item.Text, false, false), cy + 4f, WikiStyles.Small, WikiStyles.Text, labelWidth <= 0f,
                            x + (labelWidth > 0f ? labelWidth + 6f : 8f), x + width - 8f, false);
                        cy = Mathf.Max(bottom, value) + 4f;
                        AddFill(new Rect(x + 6f, cy, width - 12f, 1f), new Color(1f, 1f, 1f, 0.07f));
                        break;
                }
            }
            return cy;
        }

        // ---------------------------------------------------------------- tables

        private float Table(WikiBlock block, float y)
        {
            int cols = Mathf.Max(1, block.Cols);
            float[] min = new float[cols];
            float[] pref = new float[cols];
            List<List<Token>> tokens = new List<List<Token>>(block.Cells.Count);
            foreach (TableCell cell in block.Cells)
            {
                List<Token> cellTokens = Tokens(cell.Text, cell.Header, false);
                tokens.Add(cellTokens);
                if (cell.ColSpan != 1)
                {
                    continue;
                }
                Measure(cellTokens, out float cellMin, out float cellPref);
                min[cell.Col] = Mathf.Max(min[cell.Col], cellMin);
                pref[cell.Col] = Mathf.Max(pref[cell.Col], cellPref);
            }
            // cells spanning columns: their columns together must hold them
            for (int i = 0; i < block.Cells.Count; i++)
            {
                TableCell cell = block.Cells[i];
                if (cell.ColSpan == 1)
                {
                    continue;
                }
                Measure(tokens[i], out float cellMin, out float cellPref);
                int last = Mathf.Min(cols, cell.Col + cell.ColSpan);
                float haveMin = 0f;
                float havePref = 0f;
                for (int c = cell.Col; c < last; c++)
                {
                    haveMin += min[c];
                    havePref += pref[c];
                }
                int count = Mathf.Max(1, last - cell.Col);
                for (int c = cell.Col; c < last; c++)
                {
                    if (cellMin > haveMin)
                    {
                        min[c] += (cellMin - haveMin) / count;
                    }
                    if (cellPref > havePref)
                    {
                        pref[c] += (cellPref - havePref) / count;
                    }
                }
            }
            float sumMin = 0f;
            float sumPref = 0f;
            for (int c = 0; c < cols; c++)
            {
                min[c] = Mathf.Max(min[c], 28f);
                pref[c] = Mathf.Max(pref[c], min[c]);
                sumMin += min[c];
                sumPref += pref[c];
            }
            float available = RightAt(y, 60f);
            if (m_hasFloat && available < Width && sumMin > available)
            {
                y = ClearFloat(y);
                available = Width;
            }
            float[] widths = new float[cols];
            for (int c = 0; c < cols; c++)
            {
                if (sumPref <= available)
                {
                    widths[c] = pref[c];
                }
                else if (sumMin <= available)
                {
                    widths[c] = min[c] + (pref[c] - min[c]) * (available - sumMin) / Mathf.Max(1f, sumPref - sumMin);
                }
                else
                {
                    widths[c] = min[c] * available / sumMin;
                }
                widths[c] = Mathf.Floor(widths[c]);
            }
            float[] xs = new float[cols + 1];
            for (int c = 0; c < cols; c++)
            {
                xs[c + 1] = xs[c] + widths[c];
            }
            // lay out every cell's text at the origin, to learn the heights
            List<LayoutItem> saved = m_out;
            List<LayoutItem>[] cellItems = new List<LayoutItem>[block.Cells.Count];
            float[] heights = new float[block.Cells.Count];
            for (int i = 0; i < block.Cells.Count; i++)
            {
                TableCell cell = block.Cells[i];
                float cellWidth = xs[Mathf.Min(cols, cell.Col + cell.ColSpan)] - xs[cell.Col];
                m_out = new List<LayoutItem>();
                float bottom = Flow(tokens[i], CellPad - 2f, WikiStyles.Small, cell.Header ? WikiStyles.Bright : WikiStyles.Text,
                    cell.Header, CellPad, Mathf.Max(CellPad + 10f, cellWidth - CellPad), false);
                cellItems[i] = m_out;
                heights[i] = bottom + CellPad - 2f;
            }
            m_out = saved;
            int rows = Mathf.Max(1, block.Rows);
            float[] rowHeights = new float[rows];
            for (int r = 0; r < rows; r++)
            {
                rowHeights[r] = WikiStyles.LineHeight(WikiStyles.Small) + 6f;
            }
            for (int i = 0; i < block.Cells.Count; i++)
            {
                TableCell cell = block.Cells[i];
                if (cell.RowSpan == 1 && cell.Row < rows)
                {
                    rowHeights[cell.Row] = Mathf.Max(rowHeights[cell.Row], heights[i]);
                }
            }
            for (int i = 0; i < block.Cells.Count; i++)
            {
                TableCell cell = block.Cells[i];
                if (cell.RowSpan <= 1)
                {
                    continue;
                }
                int last = Mathf.Min(rows, cell.Row + cell.RowSpan) - 1;
                float have = 0f;
                for (int r = cell.Row; r <= last; r++)
                {
                    have += rowHeights[r];
                }
                if (heights[i] > have)
                {
                    rowHeights[last] += heights[i] - have;
                }
            }
            float[] ys = new float[rows + 1];
            ys[0] = y;
            for (int r = 0; r < rows; r++)
            {
                ys[r + 1] = ys[r] + rowHeights[r];
            }
            float tableWidth = xs[cols];
            Color line = new Color(0.45f, 0.37f, 0.25f, 0.8f);
            for (int i = 0; i < block.Cells.Count; i++)
            {
                TableCell cell = block.Cells[i];
                int lastCol = Mathf.Min(cols, cell.Col + cell.ColSpan);
                int lastRow = Mathf.Min(rows, cell.Row + cell.RowSpan);
                Rect rect = new Rect(xs[cell.Col], ys[cell.Row], xs[lastCol] - xs[cell.Col], ys[lastRow] - ys[cell.Row]);
                Color background = cell.Header ? new Color(0.24f, 0.18f, 0.1f, 1f)
                    : cell.Row % 2 == 0 ? new Color(0.11f, 0.095f, 0.075f, 0.95f) : new Color(0.145f, 0.125f, 0.095f, 0.95f);
                AddFill(rect, background);
                float offset = rect.y + Mathf.Max(0f, (rect.height - heights[i]) / 2f);
                foreach (LayoutItem item in cellItems[i])
                {
                    LayoutItem moved = item;
                    moved.Rect.x += rect.x;
                    moved.Rect.y += offset;
                    m_out.Add(moved);
                }
                AddFill(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), line);
                AddFill(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), line);
            }
            AddFill(new Rect(0f, y, tableWidth, 1f), line);
            AddFill(new Rect(0f, y, 1f, ys[rows] - y), line);
            return ys[rows];
        }

        // Narrowest width a cell can take (its longest word, capped: longer words break) and its width on one line.
        private static void Measure(List<Token> tokens, out float min, out float pref)
        {
            min = 0f;
            pref = 0f;
            float line = 0f;
            bool space = false;
            foreach (Token token in tokens)
            {
                switch (token.Type)
                {
                    case 1:
                        space = true;
                        continue;
                    case 2:
                        pref = Mathf.Max(pref, line);
                        line = 0f;
                        space = false;
                        continue;
                }
                int style = WikiStyles.Index(WikiStyles.Small, token.Bold, token.Italic);
                float width = token.Type == 3 ? IconSize(WikiStyles.Small) : WikiStyles.Width(style, token.Text);
                min = Mathf.Max(min, Mathf.Min(width, 130f));
                line += (space && line > 0f ? WikiStyles.Space(style) : 0f) + width;
                space = false;
            }
            pref = Mathf.Min(Mathf.Max(pref, line), 380f) + CellPad * 2f + 2f;
            min += CellPad * 2f + 2f;
        }

        // ---------------------------------------------------------------- text flow

        // Lays out rich text from y between left and right (and left of a floating infobox when useFloat); returns the
        // y under the last line.
        private float Flow(List<Token> tokens, float y, int size, Color color, bool center, float left, float right, bool useFloat)
        {
            float lineHeight = WikiStyles.LineHeight(size);
            List<Piece> line = new List<Piece>();
            float lineRight = useFloat ? Mathf.Min(right, RightAt(y, lineHeight)) : right;
            float cursor = 0f;
            bool pendingSpace = false;
            foreach (Token token in tokens)
            {
                if (token.Type == 2)
                {
                    y = Emit(line, y, left, lineRight, cursor, lineHeight, color, center);
                    line.Clear();
                    cursor = 0f;
                    pendingSpace = false;
                    lineRight = useFloat ? Mathf.Min(right, RightAt(y, lineHeight)) : right;
                    continue;
                }
                if (token.Type == 1)
                {
                    pendingSpace = true;
                    continue;
                }
                int style = WikiStyles.Index(size, token.Bold, token.Italic);
                bool icon = token.Type == 3;
                float width = icon ? IconSize(size) : WikiStyles.Width(style, token.Text);
                float space = pendingSpace && line.Count > 0 ? WikiStyles.Space(style) : 0f;
                float available = Mathf.Max(20f, lineRight - left);
                if (cursor + space + width > available && line.Count > 0)
                {
                    y = Emit(line, y, left, lineRight, cursor, lineHeight, color, center);
                    line.Clear();
                    cursor = 0f;
                    space = 0f;
                    lineRight = useFloat ? Mathf.Min(right, RightAt(y, lineHeight)) : right;
                    available = Mathf.Max(20f, lineRight - left);
                }
                if (!icon && width > available)
                {
                    // a word longer than the line: broken into pieces that fit
                    string rest = token.Text;
                    while (rest.Length > 0)
                    {
                        int take = rest.Length;
                        while (take > 1 && WikiStyles.Width(style, rest.Substring(0, take)) > available - cursor)
                        {
                            take--;
                        }
                        string part = rest.Substring(0, take);
                        float partWidth = WikiStyles.Width(style, part);
                        line.Add(new Piece { X = cursor, W = partWidth, Text = part, Style = style, Link = token.Link });
                        cursor += partWidth;
                        rest = rest.Substring(take);
                        if (rest.Length > 0)
                        {
                            y = Emit(line, y, left, lineRight, cursor, lineHeight, color, center);
                            line.Clear();
                            cursor = 0f;
                            lineRight = useFloat ? Mathf.Min(right, RightAt(y, lineHeight)) : right;
                            available = Mathf.Max(20f, lineRight - left);
                        }
                    }
                    pendingSpace = false;
                    continue;
                }
                line.Add(new Piece { X = cursor + space, W = width, Text = token.Text, Style = style, Link = token.Link, Icon = icon, SpaceBefore = space > 0f });
                cursor += space + width;
                pendingSpace = false;
            }
            if (line.Count > 0)
            {
                y = Emit(line, y, left, lineRight, cursor, lineHeight, color, center);
            }
            return y;
        }

        private float Emit(List<Piece> line, float y, float left, float right, float used, float lineHeight, Color color, bool center)
        {
            float shift = center ? Mathf.Max(0f, (right - left - used) / 2f) : 0f;
            int i = 0;
            while (i < line.Count)
            {
                Piece first = line[i];
                if (first.Icon)
                {
                    float size = first.W;
                    m_out.Add(new LayoutItem
                    {
                        Kind = ItemKind.Image,
                        Rect = new Rect(left + shift + first.X, y + (lineHeight - size) / 2f, size, size),
                        Text = first.Text,
                        Link = first.Link,
                    });
                    i++;
                    continue;
                }
                StringBuilder text = new StringBuilder(first.Text);
                float end = first.X + first.W;
                int j = i + 1;
                while (j < line.Count && !line[j].Icon && line[j].Style == first.Style && line[j].Link == first.Link)
                {
                    if (line[j].SpaceBefore)
                    {
                        text.Append(' ');
                    }
                    text.Append(line[j].Text);
                    end = line[j].X + line[j].W;
                    j++;
                }
                bool bold = (first.Style & 1) == 1;
                Color pieceColor = first.Link != null ? WikiStyles.Link : bold && color == WikiStyles.Text ? WikiStyles.Bright : color;
                AddText(new Rect(left + shift + first.X, y, end - first.X, lineHeight), text.ToString(), first.Style, pieceColor, first.Link);
                i = j;
            }
            return y + lineHeight;
        }

        private static float IconSize(int size)
        {
            return Mathf.Round(WikiStyles.FontSize(size) * 1.35f);
        }

        private List<Token> Tokens(string text, bool bold, bool italic)
        {
            List<Token> tokens = new List<Token>();
            if (string.IsNullOrEmpty(text))
            {
                return tokens;
            }
            StringBuilder word = new StringBuilder();
            string link = null;
            bool linkOk = false;
            int i = 0;
            void Flush()
            {
                if (word.Length > 0)
                {
                    tokens.Add(new Token { Type = 0, Text = word.ToString(), Bold = bold, Italic = italic, Link = linkOk ? link : null });
                    word.Length = 0;
                }
            }
            while (i < text.Length)
            {
                char c = text[i];
                switch (c)
                {
                    case WikiPack.LinkStart:
                        Flush();
                        int mid = text.IndexOf(WikiPack.LinkMid, i + 1);
                        if (mid < 0)
                        {
                            i++;
                            continue;
                        }
                        link = text.Substring(i + 1, mid - i - 1);
                        linkOk = m_linkExists == null || m_linkExists(link);
                        i = mid + 1;
                        continue;
                    case WikiPack.LinkEnd:
                        Flush();
                        link = null;
                        break;
                    case WikiPack.BoldOn:
                        Flush();
                        bold = true;
                        break;
                    case WikiPack.BoldOff:
                        Flush();
                        bold = false;
                        break;
                    case WikiPack.ItalicOn:
                        Flush();
                        italic = true;
                        break;
                    case WikiPack.ItalicOff:
                        Flush();
                        italic = false;
                        break;
                    case WikiPack.IconStart:
                        Flush();
                        int close = text.IndexOf(WikiPack.IconEnd, i + 1);
                        if (close < 0)
                        {
                            i = text.Length;
                            continue;
                        }
                        tokens.Add(new Token { Type = 3, Text = text.Substring(i + 1, close - i - 1), Link = linkOk ? link : null });
                        i = close + 1;
                        continue;
                    case WikiPack.Break:
                        Flush();
                        tokens.Add(new Token { Type = 2 });
                        break;
                    case ' ':
                    case '\t':
                        Flush();
                        if (tokens.Count > 0 && tokens[tokens.Count - 1].Type != 1)
                        {
                            tokens.Add(new Token { Type = 1 });
                        }
                        break;
                    default:
                        if (c >= ' ')
                        {
                            word.Append(c);
                        }
                        break;
                }
                i++;
            }
            Flush();
            return tokens;
        }

        internal static string Plain(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }
            StringBuilder plain = new StringBuilder(text.Length);
            bool inTarget = false;
            bool inIcon = false;
            foreach (char c in text)
            {
                switch (c)
                {
                    case WikiPack.LinkStart:
                        inTarget = true;
                        break;
                    case WikiPack.LinkMid:
                        inTarget = false;
                        break;
                    case WikiPack.IconStart:
                        inIcon = true;
                        break;
                    case WikiPack.IconEnd:
                        inIcon = false;
                        break;
                    case WikiPack.Break:
                        plain.Append(' ');
                        break;
                    default:
                        if (c >= ' ' && !inTarget && !inIcon)
                        {
                            plain.Append(c);
                        }
                        break;
                }
            }
            return plain.ToString().Trim();
        }

        // ---------------------------------------------------------------- helpers

        private float RightAt(float y, float height)
        {
            if (m_hasFloat && y < m_float.yMax + 6f && y + height > m_float.yMin)
            {
                return m_float.xMin - FloatGap;
            }
            return Width;
        }

        private float ClearFloat(float y)
        {
            if (m_hasFloat)
            {
                y = Mathf.Max(y, m_float.yMax + 10f);
                m_hasFloat = false;
            }
            return y;
        }

        private float FloatBottom()
        {
            return m_hasFloat ? m_float.yMax + 10f : 0f;
        }

        private static Vector2 Fit(WikiImage image, float maxWidth, float maxHeight)
        {
            float width = image.Width > 0 ? image.Width : 200f;
            float height = image.Height > 0 ? image.Height : width * 0.75f;
            float scale = Mathf.Min(1f, maxWidth / width, maxHeight / height);
            return new Vector2(Mathf.Round(width * scale), Mathf.Round(height * scale));
        }

        private int AddFill(Rect rect, Color color)
        {
            m_out.Add(new LayoutItem { Kind = ItemKind.Fill, Rect = rect, Color = color });
            return m_out.Count - 1;
        }

        private void SetHeight(int index, float height)
        {
            LayoutItem item = m_out[index];
            item.Rect.height = height;
            m_out[index] = item;
        }

        private void AddImage(Rect rect, string key)
        {
            m_out.Add(new LayoutItem { Kind = ItemKind.Image, Rect = rect, Text = key });
        }

        private void AddText(Rect rect, string text, int style, Color color, string link)
        {
            m_out.Add(new LayoutItem { Kind = ItemKind.Text, Rect = rect, Text = text, Style = style, Color = color, Link = link });
        }
    }
}
