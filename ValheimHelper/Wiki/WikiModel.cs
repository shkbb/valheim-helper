using System;
using System.Collections.Generic;
using System.Globalization;

namespace ValheimHelper.Wiki
{
    // The article model of the in-game wiki and its parser: plain C#, so the tests read the whole pack without the game.
    internal struct SearchHit
    {
        public string Title;
        public string Anchor;
        public string Name;
    }

    // ---------------------------------------------------------------- article model

    internal enum BlockKind
    {
        Heading,
        Paragraph,
        ListItem,
        Quote,
        QuoteSource,
        Notice,
        Image,
        Gallery,
        Infobox,
        Table,
        Rule,
        Columns,
    }

    internal sealed class WikiImage
    {
        public string Key;
        public int Width;
        public int Height;
        public string Caption = "";
    }

    internal sealed class InfoItem
    {
        public char Kind; // I image, H header, R row
        public WikiImage Image;
        public string Label = "";
        public string Text = "";
    }

    internal sealed class InfoTab
    {
        public string Label;
        public readonly List<InfoItem> Items = new List<InfoItem>();
    }

    internal sealed class TableCell
    {
        public int Row;
        public int Col;
        public int ColSpan = 1;
        public int RowSpan = 1;
        public bool Header;
        public string Text = "";
    }

    internal sealed class WikiBlock
    {
        public BlockKind Kind;
        public int Level;
        public bool Ordered;
        public string Text = "";
        public string Anchor;
        public WikiImage Image;
        public List<WikiImage> Gallery;
        public List<InfoItem> Before;
        public List<InfoTab> Tabs;
        public List<InfoItem> After;
        // Weird Gloop's infobox: the game's own ID of the thing (the guide draws it as a card with the game's name)
        public string GameId;
        public List<TableCell> Cells;
        public int Rows;
        public int Cols;
        public List<string> Items;
    }

    internal sealed class WikiArticle
    {
        public string Title;
        public long Revision;
        public string[] Categories = new string[0];
        public readonly List<WikiBlock> Blocks = new List<WikiBlock>();
        // Pages the mod makes itself (home, categories, donations) are not from the wiki.
        public bool Own;
    }

    internal static class WikiParser
    {
        private const string Stars = "\u2B50\u2606\u272A\u272B\u272C\u272D\u272E\u272F\u2730\u2729";

        // Symbols the game's font cannot draw, as the ones it can.
        internal static string Drawable(string text)
        {
            if (text.IndexOfAny(Stars.ToCharArray()) < 0)
            {
                return text;
            }
            System.Text.StringBuilder builder = new System.Text.StringBuilder(text);
            foreach (char star in Stars)
            {
                builder.Replace(star, '\u2605');
            }
            return builder.ToString();
        }

        internal static WikiArticle Parse(string title, string text)
        {
            text = Drawable(text);
            WikiArticle article = new WikiArticle { Title = title };
            WikiBlock infobox = null;
            InfoTab tab = null;
            bool afterTabs = false;
            WikiBlock gallery = null;
            WikiBlock table = null;
            List<string[]> rows = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0)
                {
                    continue;
                }
                int space = line.IndexOf(' ');
                string code = space < 0 ? line : line.Substring(0, space);
                string rest = space < 0 ? "" : line.Substring(space + 1);
                if (infobox != null)
                {
                    switch (code)
                    {
                        case "IT":
                            tab = new InfoTab { Label = rest };
                            infobox.Tabs.Add(tab);
                            afterTabs = false;
                            continue;
                        case "/IT":
                            tab = null;
                            afterTabs = true;
                            continue;
                        case "IG":
                            infobox.GameId = rest.Trim();
                            continue;
                        case "/IB":
                            article.Blocks.Add(infobox);
                            infobox = null;
                            tab = null;
                            continue;
                        case "II":
                        case "IH":
                        case "IR":
                            InfoItem item = new InfoItem { Kind = code[1] };
                            if (code == "II")
                            {
                                item.Image = ParseImage(rest);
                            }
                            else if (code == "IH")
                            {
                                item.Text = rest;
                            }
                            else
                            {
                                int tabAt = rest.IndexOf('\t');
                                item.Label = tabAt >= 0 ? rest.Substring(0, tabAt) : "";
                                item.Text = tabAt >= 0 ? rest.Substring(tabAt + 1) : rest;
                            }
                            if (item.Kind == 'I' && item.Image == null)
                            {
                                continue;
                            }
                            (tab != null ? tab.Items : afterTabs ? infobox.After : infobox.Before).Add(item);
                            continue;
                    }
                    continue;
                }
                if (gallery != null)
                {
                    if (code == "GI")
                    {
                        WikiImage image = ParseImage(rest);
                        if (image != null)
                        {
                            gallery.Gallery.Add(image);
                        }
                    }
                    else if (code == "/GAL")
                    {
                        if (gallery.Gallery.Count > 0)
                        {
                            article.Blocks.Add(gallery);
                        }
                        gallery = null;
                    }
                    continue;
                }
                if (table != null)
                {
                    if (code == "TR")
                    {
                        rows.Add(rest.Split('\t'));
                    }
                    else if (code == "/TB")
                    {
                        BuildTable(table, rows);
                        if (table.Cells.Count > 0)
                        {
                            article.Blocks.Add(table);
                        }
                        table = null;
                    }
                    continue;
                }
                switch (code)
                {
                    case "rev":
                        long.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out article.Revision);
                        break;
                    case "cat":
                        article.Categories = rest.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                        break;
                    case "H2":
                    case "H3":
                    case "H4":
                        int tabIndex = rest.IndexOf('\t');
                        article.Blocks.Add(new WikiBlock
                        {
                            Kind = BlockKind.Heading,
                            Level = code[1] - '0',
                            Anchor = tabIndex >= 0 ? rest.Substring(0, tabIndex) : "",
                            Text = tabIndex >= 0 ? rest.Substring(tabIndex + 1) : rest,
                        });
                        break;
                    case "P":
                        article.Blocks.Add(new WikiBlock { Kind = BlockKind.Paragraph, Text = rest });
                        break;
                    case "Q":
                        article.Blocks.Add(new WikiBlock { Kind = BlockKind.Quote, Text = rest });
                        break;
                    case "QS":
                        article.Blocks.Add(new WikiBlock { Kind = BlockKind.QuoteSource, Text = rest });
                        break;
                    case "N":
                        article.Blocks.Add(new WikiBlock { Kind = BlockKind.Notice, Text = rest });
                        break;
                    case "HR":
                        article.Blocks.Add(new WikiBlock { Kind = BlockKind.Rule });
                        break;
                    case "IMG":
                        WikiImage picture = ParseImage(rest);
                        if (picture != null)
                        {
                            article.Blocks.Add(new WikiBlock { Kind = BlockKind.Image, Image = picture });
                        }
                        break;
                    case "GAL":
                        gallery = new WikiBlock { Kind = BlockKind.Gallery, Gallery = new List<WikiImage>() };
                        break;
                    case "TB":
                        table = new WikiBlock { Kind = BlockKind.Table, Cells = new List<TableCell>() };
                        rows = new List<string[]>();
                        break;
                    case "IB":
                        infobox = new WikiBlock
                        {
                            Kind = BlockKind.Infobox,
                            Text = rest,
                            Before = new List<InfoItem>(),
                            Tabs = new List<InfoTab>(),
                            After = new List<InfoItem>(),
                        };
                        afterTabs = false;
                        tab = null;
                        break;
                    default:
                        if (code.Length == 2 && (code[0] == 'L' || code[0] == 'O') && code[1] >= '1' && code[1] <= '9')
                        {
                            article.Blocks.Add(new WikiBlock { Kind = BlockKind.ListItem, Level = code[1] - '0', Ordered = code[0] == 'O', Text = rest });
                        }
                        break;
                }
            }
            if (infobox != null)
            {
                article.Blocks.Add(infobox);
            }
            return article;
        }

        private static WikiImage ParseImage(string rest)
        {
            string[] parts = rest.Split('\t');
            if (parts.Length < 1 || parts[0].Length == 0)
            {
                return null;
            }
            WikiImage image = new WikiImage { Key = parts[0] };
            image.Width = parts.Length > 1 && int.TryParse(parts[1], out int w) ? w : 0;
            image.Height = parts.Length > 2 && int.TryParse(parts[2], out int h) ? h : 0;
            image.Caption = parts.Length > 3 ? parts[3] : "";
            return image;
        }

        // Rows of "H|span|text" / "D|span|text" / "~" (the cell above spans into this row) into a grid.
        private static void BuildTable(WikiBlock table, List<string[]> rows)
        {
            Dictionary<long, TableCell> occupied = new Dictionary<long, TableCell>();
            int cols = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                int col = 0;
                foreach (string raw in rows[r])
                {
                    while (occupied.ContainsKey(Key(r, col)))
                    {
                        col++;
                    }
                    if (raw == "~")
                    {
                        if (r > 0 && occupied.TryGetValue(Key(r - 1, col), out TableCell above))
                        {
                            if (above.Row + above.RowSpan - 1 == r - 1)
                            {
                                above.RowSpan++;
                            }
                            occupied[Key(r, col)] = above;
                        }
                        col++;
                        continue;
                    }
                    TableCell cell = new TableCell { Row = r, Col = col };
                    string[] parts = raw.Split(new[] { '|' }, 3);
                    if (parts.Length == 3 && (parts[0] == "H" || parts[0] == "D"))
                    {
                        cell.Header = parts[0] == "H";
                        cell.ColSpan = int.TryParse(parts[1], out int span) ? Math.Max(1, Math.Min(20, span)) : 1;
                        cell.Text = parts[2];
                    }
                    else
                    {
                        cell.Text = raw;
                    }
                    for (int c = 0; c < cell.ColSpan; c++)
                    {
                        occupied[Key(r, col + c)] = cell;
                    }
                    table.Cells.Add(cell);
                    col += cell.ColSpan;
                }
                cols = Math.Max(cols, col);
            }
            table.Rows = rows.Count;
            table.Cols = cols;
        }

        private static long Key(int row, int col)
        {
            return ((long)row << 32) | (uint)col;
        }
    }
}
