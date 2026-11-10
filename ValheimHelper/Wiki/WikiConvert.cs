using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimHelper.Wiki
{
    // A page as the wiki renders it (the HTML of action=parse, every template already expanded) made into the block
    // lines the mod reads. The same conversion the mod's own tool does when a pack is built, so the guide can bring
    // in changed articles by itself, without a new build of the mod. Navigation boxes, edit links and the table of
    // contents are dropped: the guide has its own navigation. Pictures are collected with the address of a copy of
    // the size needed.
    internal static class WikiConvert
    {
        internal sealed class Picture
        {
            public string Key;
            public string Url;
            public int Width;
        }

        internal sealed class Result
        {
            public readonly List<string> Lines = new List<string>();
            public readonly Dictionary<string, Picture> Pictures = new Dictionary<string, Picture>();
        }

        // Figures are kept this wide at most, icons in text this wide.
        internal const int FigureWidth = 320;
        internal const int IconWidth = 64;

        private const char LinkStart = '';
        private const char LinkMid = '';
        private const char LinkEnd = '';
        private const char BoldOn = '';
        private const char BoldOff = '';
        private const char ItalicOn = '';
        private const char ItalicOff = '';
        private const char IconStart = '';
        private const char IconEnd = '';
        private const char Break = '';

        private sealed class Converter
        {
            internal readonly Result Out = new Result();
            private readonly string m_wikiPath;
            private readonly string m_keyPrefix;

            internal Converter(string lang)
            {
                bool english = string.IsNullOrEmpty(lang) || lang == "en";
                m_wikiPath = english ? "/wiki/" : "/" + lang + "/wiki/";
                m_keyPrefix = english ? "" : lang + "~";
            }

            private void Line(string line)
            {
                Out.Lines.Add(line);
            }

            // ------------------------------------------------------------ blocks

            internal void Blocks(HtmlNode parent)
            {
                foreach (HtmlNode node in new List<HtmlNode>(parent.Nodes))
                {
                    if (node.IsText)
                    {
                        string value = Clean(node.Text);
                        if (value.Length > 0)
                        {
                            Line("P " + value);
                        }
                    }
                    else
                    {
                        Block(node);
                    }
                }
            }

            private void Block(HtmlNode element)
            {
                string tag = element.Tag;
                if (tag == "aside" && element.HasClass("portable-infobox"))
                {
                    Infobox(element);
                    return;
                }
                if (element.HasClass("wds-tabber") || element.HasClass("tabber"))
                {
                    List<string> labels = new List<string>();
                    foreach (HtmlNode label in element.FindAll(IsTabLabel))
                    {
                        labels.Add(Clean(label.InnerText()));
                    }
                    int index = 0;
                    foreach (HtmlNode content in element.ChildrenWhere(IsTabContent))
                    {
                        string label = index < labels.Count ? labels[index] : content.Attr("title");
                        if (label.Length > 0)
                        {
                            Line("P " + BoldOn + label + BoldOff);
                        }
                        Blocks(content);
                        index++;
                    }
                    return;
                }
                if (element.HasClass("wds-tabs__wrapper"))
                {
                    return;
                }
                switch (tag)
                {
                    case "h1":
                    case "h2":
                    case "h3":
                    case "h4":
                    case "h5":
                    case "h6":
                        Heading(element);
                        return;
                    case "p":
                        Paragraph("P ", element);
                        return;
                    case "ul":
                    case "ol":
                        if (element.HasClass("gallery"))
                        {
                            Gallery(element);
                        }
                        else
                        {
                            List(element, 1);
                        }
                        return;
                    case "dl":
                        foreach (HtmlNode item in element.Children)
                        {
                            Paragraph(item.Tag == "dt" ? "P " + BoldOn : "P ", item);
                        }
                        return;
                    case "table":
                        Table(element);
                        return;
                    case "figure":
                        Figure(element);
                        return;
                    case "blockquote":
                        Quote(element);
                        return;
                    case "hr":
                        Line("HR");
                        return;
                    case "pre":
                    {
                        string text = Clean(element.InnerText());
                        if (text.Length > 0)
                        {
                            Line("P " + text);
                        }
                        return;
                    }
                    case "img":
                        Picture_("IMG ", element, "");
                        return;
                }
                if (tag == "div" && (element.HasClass("thumb") || element.HasClass("floatright") || element.HasClass("floatleft"))
                    && element.FindFirst(node => node.Tag == "img") != null
                    && element.FindFirst(node => node.Tag == "p" || node.Tag == "table") == null)
                {
                    Figure(element);
                }
                else if (tag == "div" && (element.HasClass("wikia-gallery") || element.HasClass("gallery") || element.HasClass("wikia-gallery-item")))
                {
                    Gallery(element);
                }
                else if (tag == "div" && (element.HasClass("notice") || element.HasClass("hatnote") || element.HasClass("dablink") || element.Attr("role") == "note"))
                {
                    Paragraph("N ", element);
                }
                else if (HasBlockChildren(element))
                {
                    Blocks(element);
                }
                else
                {
                    Paragraph("P ", element);
                }
            }

            private static bool IsTabLabel(HtmlNode node)
            {
                return node.HasClass("wds-tabs__tab-label") || (node.Tag == "li" && node.Parent != null && node.Closest(up => up.HasClass("tabbernav")) != null);
            }

            private static bool IsTabContent(HtmlNode node)
            {
                return node.HasClass("wds-tab__content") || node.HasClass("tabbertab");
            }

            private static readonly HashSet<string> s_blockTags = new HashSet<string>
            {
                "p", "div", "table", "ul", "ol", "dl", "h1", "h2", "h3", "h4", "h5", "h6", "figure", "blockquote",
                "aside", "hr", "pre", "center", "section",
            };

            private static bool HasBlockChildren(HtmlNode element)
            {
                foreach (HtmlNode child in element.Children)
                {
                    if (s_blockTags.Contains(child.Tag))
                    {
                        return true;
                    }
                }
                return false;
            }

            private void Heading(HtmlNode element)
            {
                HtmlNode headline = element.FindFirst(node => node.HasClass("mw-headline"));
                string id = headline != null ? headline.Id : element.Id;
                string text = Inline(headline ?? element);
                if (text.Length > 0)
                {
                    int level = Math.Min(4, Math.Max(2, element.Tag[1] - '0'));
                    Line("H" + level + " " + Field(id) + "\t" + text);
                }
            }

            private void Paragraph(string prefix, HtmlNode element)
            {
                string text = Inline(element);
                if (text.Length > 0 && text != BoldOn.ToString())
                {
                    Line(prefix + text + (prefix.EndsWith(BoldOn.ToString(), StringComparison.Ordinal) ? BoldOff.ToString() : ""));
                }
            }

            private void List(HtmlNode list, int level)
            {
                string kind = list.Tag == "ol" ? "O" : "L";
                foreach (HtmlNode item in list.Children)
                {
                    if (item.Tag != "li")
                    {
                        continue;
                    }
                    // the item's own text first, its nested lists after it one level deeper
                    HtmlNode copy = item.Clone();
                    foreach (HtmlNode nested in copy.ChildrenWhere(node => node.Tag == "ul" || node.Tag == "ol"))
                    {
                        nested.Remove();
                    }
                    string text = Inline(copy);
                    if (text.Length > 0)
                    {
                        Line(kind + Math.Min(level, 4) + " " + text);
                    }
                    foreach (HtmlNode nested in item.ChildrenWhere(node => node.Tag == "ul" || node.Tag == "ol"))
                    {
                        List(nested, level + 1);
                    }
                }
            }

            private void Quote(HtmlNode element)
            {
                HtmlNode source = element.FindFirst(node => node.HasClass("pull-quote__source") || node.Tag == "cite");
                source?.Remove();
                string text = Inline(element);
                if (text.Length > 0)
                {
                    Line("Q " + text);
                }
                if (source != null)
                {
                    string from = Inline(source);
                    if (from.Length > 0)
                    {
                        Line("QS " + from);
                    }
                }
            }

            private void Figure(HtmlNode element)
            {
                HtmlNode caption = element.FindFirst(node => node.Tag == "figcaption" || node.HasClass("thumbcaption") || node.HasClass("lightbox-caption"));
                string text = caption != null ? Inline(caption) : "";
                HtmlNode img = element.FindFirst(node => node.Tag == "img");
                if (img != null)
                {
                    Picture_("IMG ", img, text);
                }
            }

            private void Gallery(HtmlNode element)
            {
                List<string> items = new List<string>();
                foreach (HtmlNode img in element.FindAll(node => node.Tag == "img"))
                {
                    HtmlNode item = img.Closest(node => node.Tag == "li" || node.HasClass("wikia-gallery-item") || node.HasClass("gallerybox"));
                    HtmlNode caption = item?.FindFirst(node => node.HasClass("gallerytext") || node.HasClass("lightbox-caption") || node.Tag == "figcaption");
                    string line = PictureLine("GI ", img, caption != null ? Inline(caption) : "");
                    if (line != null)
                    {
                        items.Add(line);
                    }
                }
                if (items.Count > 0)
                {
                    Line("GAL");
                    Out.Lines.AddRange(items);
                    Line("/GAL");
                }
            }

            private void Picture_(string prefix, HtmlNode img, string caption)
            {
                string line = PictureLine(prefix, img, caption);
                if (line != null)
                {
                    Line(line);
                }
            }

            private string PictureLine(string prefix, HtmlNode img, string caption)
            {
                Picture picture = Register(img, FigureWidth);
                if (picture == null)
                {
                    return null;
                }
                int[] size = DisplaySize(img, FigureWidth);
                return prefix + picture.Key + "\t" + size[0] + "\t" + size[1] + "\t" + caption;
            }

            // ------------------------------------------------------------ infobox

            private void Infobox(HtmlNode aside)
            {
                HtmlNode title = aside.FindFirst(node => node.HasClass("pi-title"));
                Line("IB " + (title != null ? Inline(title) : ""));
                HtmlNode tabber = aside.FindFirst(node => node.Tag == "section" && node.HasClass("wds-tabber"));
                List<HtmlNode> parts = new List<HtmlNode>();
                foreach (HtmlNode child in aside.Children)
                {
                    if (!ReferenceEquals(child, title))
                    {
                        parts.Add(child);
                    }
                }
                foreach (HtmlNode part in parts)
                {
                    if (ReferenceEquals(part, tabber))
                    {
                        List<string> labels = new List<string>();
                        foreach (HtmlNode label in tabber.FindAll(node => node.HasClass("wds-tabs__tab-label")))
                        {
                            labels.Add(Clean(label.InnerText()));
                        }
                        int index = 0;
                        foreach (HtmlNode content in tabber.ChildrenWhere(node => node.HasClass("wds-tab__content")))
                        {
                            Line("IT " + (index < labels.Count ? labels[index] : (index + 1).ToString(CultureInfo.InvariantCulture)));
                            InfoboxItems(content);
                            index++;
                        }
                        // what follows the tabs is common to all of them again
                        Line("/IT");
                    }
                    else
                    {
                        InfoboxItem(part);
                    }
                }
                Line("/IB");
            }

            private void InfoboxItems(HtmlNode parent)
            {
                foreach (HtmlNode child in parent.Children)
                {
                    InfoboxItem(child);
                }
            }

            private void InfoboxItem(HtmlNode item)
            {
                if (item.HasClass("pi-image"))
                {
                    HtmlNode img = item.FindFirst(node => node.Tag == "img");
                    if (img != null)
                    {
                        HtmlNode caption = item.FindFirst(node => node.HasClass("pi-caption") || node.Tag == "figcaption");
                        string line = PictureLine("II ", img, caption != null ? Inline(caption) : "");
                        if (line != null)
                        {
                            Line(line);
                        }
                    }
                }
                else if (item.HasClass("pi-header") || item.Tag == "h2")
                {
                    string text = Inline(item);
                    if (text.Length > 0)
                    {
                        Line("IH " + text);
                    }
                }
                else if (item.HasClass("pi-data"))
                {
                    HtmlNode label = item.FindFirst(node => node.HasClass("pi-data-label"));
                    HtmlNode value = item.FindFirst(node => node.HasClass("pi-data-value"));
                    string text = value != null ? Inline(value) : "";
                    if (text.Length > 0)
                    {
                        Line("IR " + (label != null ? Field(Inline(label)) : "") + "\t" + text);
                    }
                }
                else if (item.HasClass("pi-smart-group"))
                {
                    List<HtmlNode> labels = item.FindAll(node => node.HasClass("pi-smart-data-label"));
                    List<HtmlNode> values = item.FindAll(node => node.HasClass("pi-smart-data-value"));
                    for (int i = 0; i < values.Count; i++)
                    {
                        string text = Inline(values[i]);
                        if (text.Length > 0)
                        {
                            Line("IR " + (i < labels.Count ? Field(Inline(labels[i])) : "") + "\t" + text);
                        }
                    }
                }
                else if (item.Tag == "section" || item.HasClass("pi-group") || item.HasClass("pi-panel") || item.Tag == "div")
                {
                    InfoboxItems(item);
                }
            }

            // ------------------------------------------------------------ tables

            private void Table(HtmlNode table)
            {
                List<HtmlNode> rows = new List<HtmlNode>();
                foreach (HtmlNode row in table.FindAll(node => node.Tag == "tr"))
                {
                    if (ReferenceEquals(row.Closest(node => node.Tag == "table"), table))
                    {
                        rows.Add(row);
                    }
                }
                if (rows.Count == 0)
                {
                    return;
                }
                // rowspans: cells still covering later rows, by column
                Dictionary<int, int> covering = new Dictionary<int, int>();
                List<string> out_ = new List<string>();
                foreach (HtmlNode row in rows)
                {
                    List<string> cells = new List<string>();
                    int column = 0;
                    foreach (HtmlNode cell in row.Children)
                    {
                        if (cell.Tag != "td" && cell.Tag != "th")
                        {
                            continue;
                        }
                        while (Covering(covering, column) > 0)
                        {
                            cells.Add("~");
                            covering[column] = Covering(covering, column) - 1;
                            column++;
                        }
                        int colspan = Span(cell.Attr("colspan"));
                        int rowspan = Span(cell.Attr("rowspan"));
                        string text = CellText(cell);
                        cells.Add((cell.Tag == "th" ? "H|" : "D|") + colspan + "|" + text);
                        for (int c = 0; c < colspan; c++)
                        {
                            if (rowspan > 1)
                            {
                                covering[column + c] = rowspan - 1;
                            }
                        }
                        column += colspan;
                    }
                    while (Covering(covering, column) > 0)
                    {
                        cells.Add("~");
                        covering[column] = Covering(covering, column) - 1;
                        column++;
                    }
                    if (cells.Count > 0)
                    {
                        out_.Add("TR " + string.Join("\t", cells.ToArray()));
                    }
                }
                if (out_.Count > 0)
                {
                    Line("TB");
                    Out.Lines.AddRange(out_);
                    Line("/TB");
                }
            }

            private static int Covering(Dictionary<int, int> covering, int column)
            {
                return covering.TryGetValue(column, out int value) ? value : 0;
            }

            private string CellText(HtmlNode cell)
            {
                // lists and paragraphs inside a cell become lines separated by breaks
                StringBuilder text = new StringBuilder();
                foreach (HtmlNode node in cell.Nodes)
                {
                    string part;
                    bool block = false;
                    if (!node.IsText && (node.Tag == "ul" || node.Tag == "ol"))
                    {
                        StringBuilder items = new StringBuilder();
                        foreach (HtmlNode li in node.FindAll(child => child.Tag == "li"))
                        {
                            if (items.Length > 0)
                            {
                                items.Append(Break);
                            }
                            items.Append("• ").Append(Inline(li));
                        }
                        part = items.ToString();
                        block = true;
                    }
                    else if (!node.IsText && (node.Tag == "p" || node.Tag == "div"))
                    {
                        part = Inline(node);
                        block = true;
                    }
                    else
                    {
                        part = InlineNode(node);
                    }
                    if (IsBlank(part))
                    {
                        continue;
                    }
                    if (text.Length > 0 && block)
                    {
                        text.Append(Break);
                    }
                    text.Append(part);
                }
                return Field(Tidy(text.ToString()));
            }

            private static int Span(string value)
            {
                return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int span)
                    ? Math.Max(1, Math.Min(20, span)) : 1;
            }

            // ------------------------------------------------------------ inline

            internal string Inline(HtmlNode element)
            {
                StringBuilder out_ = new StringBuilder();
                foreach (HtmlNode node in element.Nodes)
                {
                    out_.Append(InlineNode(node));
                }
                return Field(Tidy(out_.ToString()));
            }

            private string InlineNode(HtmlNode node)
            {
                if (node.IsText)
                {
                    return node.Text.Replace(' ', ' ');
                }
                switch (node.Tag)
                {
                    case "br":
                        return Break.ToString();
                    case "b":
                    case "strong":
                        return BoldOn + Children(node) + BoldOff;
                    case "i":
                    case "em":
                        return ItalicOn + Children(node) + ItalicOff;
                    case "img":
                        return Icon(node);
                    case "a":
                        return Link(node);
                    case "sup":
                        return node.HasClass("reference") ? "" : Children(node);
                    default:
                        return Children(node);
                }
            }

            private string Children(HtmlNode element)
            {
                StringBuilder out_ = new StringBuilder();
                foreach (HtmlNode node in element.Nodes)
                {
                    out_.Append(InlineNode(node));
                }
                return out_.ToString();
            }

            private string Link(HtmlNode a)
            {
                string href = a.Attr("href");
                string inner = Children(a);
                // other sites, and missing articles (a link to create them: "…?action=edit&redlink=1")
                if (!href.StartsWith(m_wikiPath, StringComparison.Ordinal) || href.IndexOf('?') >= 0 || a.HasClass("new"))
                {
                    return inner;
                }
                string target = Decode(href.Substring(m_wikiPath.Length)).Replace('_', ' ');
                if (target.IndexOf(':') > 0 && target.IndexOf(' ') < 0)
                {
                    // another namespace in the wiki's language (Kategorie:, Datei:…): not an article
                    string space = target.Substring(0, target.IndexOf(':'));
                    if (space.Length > 0 && char.IsUpper(space[0]) && space.Length <= 12)
                    {
                        return inner;
                    }
                }
                if (target.StartsWith("File:", StringComparison.Ordinal) || target.StartsWith("Category:", StringComparison.Ordinal)
                    || target.StartsWith("Special:", StringComparison.Ordinal))
                {
                    return inner;
                }
                if (a.FindFirst(node => node.Tag == "img") != null)
                {
                    // an icon linking to its page: the icon stays, and the page is what the text beside it links to anyway
                    return inner;
                }
                if (IsBlank(inner))
                {
                    return "";
                }
                return LinkStart + target + LinkMid + inner + LinkEnd;
            }

            private string Icon(HtmlNode img)
            {
                Picture picture = Register(img, IconWidth);
                return picture == null ? "" : IconStart + picture.Key + IconEnd.ToString();
            }

            // ------------------------------------------------------------ pictures

            private Picture Register(HtmlNode img, int maxWidth)
            {
                string key = img.Attr("data-image-key");
                if (key.Length > 0)
                {
                    key = m_keyPrefix + key;
                }
                string url = img.HasAttr("data-src") ? img.Attr("data-src") : img.Attr("src");
                if (key.Length == 0 || url.Length == 0 || url.StartsWith("data:", StringComparison.Ordinal))
                {
                    return null;
                }
                int display = ParseInt(img.Attr("width"), maxWidth);
                // twice the shown size for sharpness on large screens, within the limit
                int width = Math.Min(maxWidth, Math.Max(display * 2, 32));
                if (Out.Pictures.TryGetValue(key, out Picture existing) && existing.Width >= width)
                {
                    return existing;
                }
                Picture picture = new Picture { Key = key, Url = Sized(url, width), Width = width };
                Out.Pictures[key] = picture;
                return picture;
            }

            private static int[] DisplaySize(HtmlNode img, int maxWidth)
            {
                int width = ParseInt(img.Attr("width"), maxWidth);
                int height = ParseInt(img.Attr("height"), width);
                if (width > maxWidth)
                {
                    height = height * maxWidth / Math.Max(1, width);
                    width = maxWidth;
                }
                return new[] { width, height };
            }
        }

        // The address of the same picture scaled to the width, as PNG/JPEG (the image server gives WebP otherwise).
        internal static string Sized(string url, int width)
        {
            string base_ = System.Text.RegularExpressions.Regex.Replace(url, "/scale-to-width-down/\\d+", "");
            base_ = System.Text.RegularExpressions.Regex.Replace(base_, "/scale-to-width/\\d+", "");
            int query = base_.IndexOf('?');
            string path = query >= 0 ? base_.Substring(0, query) : base_;
            string params_ = query >= 0 ? base_.Substring(query + 1) : "";
            if (path.Contains("/revision/latest"))
            {
                path = path.Replace("/revision/latest", "/revision/latest/scale-to-width-down/" + width);
            }
            return path + "?" + (params_.Length == 0 ? "" : params_ + "&") + "format=original";
        }

        internal static Result Convert(string html, string lang)
        {
            Converter converter = new Converter(lang);
            HtmlNode root = Html.Parse(html);
            HtmlNode content = root.FindFirst(node => node.Tag == "div" && node.HasClass("mw-parser-output")) ?? root;
            foreach (HtmlNode node in content.FindAll(IsDropped))
            {
                node.Remove();
            }
            converter.Blocks(content);
            return converter.Out;
        }

        // The wiki's own furniture: the mod has its own navigation and shows no footnotes.
        private static bool IsDropped(HtmlNode node)
        {
            switch (node.Tag)
            {
                case "style":
                case "script":
                case "link":
                case "meta":
                case "noscript":
                    return true;
                case "sup":
                    return node.HasClass("reference");
            }
            return node.HasClass("mw-editsection") || node.HasClass("toc") || node.Id == "toc" || node.HasClass("navbox")
                || node.HasClass("printfooter") || node.HasClass("references") || node.HasClass("mw-references-wrap")
                || node.HasClass("noprint") || node.HasClass("mw-empty-elt") || node.HasClass("navigation-not-searchable");
        }

        // ---------------------------------------------------------------- text

        private static int ParseInt(string value, int fallback)
        {
            return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : fallback;
        }

        private static string Decode(string value)
        {
            try
            {
                return Uri.UnescapeDataString(value);
            }
            catch (UriFormatException)
            {
                return value;
            }
        }

        private static bool IsBlank(string text)
        {
            foreach (char c in text)
            {
                if (!char.IsWhiteSpace(c))
                {
                    return false;
                }
            }
            return true;
        }

        // Runs of spaces become one; spaces next to a break go.
        internal static string Tidy(string text)
        {
            StringBuilder collapsed = new StringBuilder(text.Length);
            bool space = false;
            foreach (char c in text)
            {
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == ' ')
                {
                    space = true;
                    continue;
                }
                // what a page cannot show anyway: zero width space, zero width non-joiner, soft hyphen
                if (c == '​' || c == '‌' || c == '­')
                {
                    continue;
                }
                if (space && collapsed.Length > 0)
                {
                    collapsed.Append(' ');
                }
                else if (space && collapsed.Length == 0)
                {
                    collapsed.Append(' ');
                }
                space = false;
                collapsed.Append(c);
            }
            if (space)
            {
                collapsed.Append(' ');
            }
            string result = collapsed.ToString().Replace(" " + Break, Break.ToString()).Replace(Break + " ", Break.ToString());
            return Trim(result);
        }

        internal static string Clean(string text)
        {
            return Tidy(text.Replace(' ', ' '));
        }

        // Text safe for one line and one tab-separated field.
        internal static string Field(string text)
        {
            StringBuilder out_ = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                out_.Append(c == '\t' || c == '\n' || c == '\r' ? ' ' : c);
            }
            return Trim(out_.ToString());
        }

        // Like Trim, but only real spaces: the formatting characters are below ' ' and must stay.
        internal static string Trim(string text)
        {
            int start = 0;
            int end = text.Length;
            while (start < end && IsSpace(text[start]))
            {
                start++;
            }
            while (end > start && IsSpace(text[end - 1]))
            {
                end--;
            }
            return text.Substring(start, end - start);
        }

        private static bool IsSpace(char c)
        {
            return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == ' ';
        }
    }
}
