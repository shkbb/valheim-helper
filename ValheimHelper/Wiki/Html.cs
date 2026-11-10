using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimHelper.Wiki
{
    // A small, forgiving HTML reader: enough for the pages the wiki renders (action=parse), and nothing more.
    // It makes a tree of elements and text, closes what the page forgets to close the way browsers do, and decodes
    // the entities. No outside library, so the same code runs in the game and in the tests.
    internal sealed class HtmlNode
    {
        // null for a piece of text.
        public string Tag;
        public string Text = "";
        public HtmlNode Parent;
        public readonly List<HtmlNode> Nodes = new List<HtmlNode>();

        private Dictionary<string, string> m_attributes;
        private string[] m_classes;

        public bool IsText => Tag == null;

        public bool Is(string tag)
        {
            return Tag == tag;
        }

        public void SetAttribute(string name, string value)
        {
            if (m_attributes == null)
            {
                m_attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            m_attributes[name] = value;
            if (name == "class")
            {
                m_classes = null;
            }
        }

        public bool HasAttr(string name)
        {
            return m_attributes != null && m_attributes.ContainsKey(name);
        }

        public string Attr(string name)
        {
            return m_attributes != null && m_attributes.TryGetValue(name, out string value) ? value : "";
        }

        public string Id => Attr("id");

        public bool HasClass(string name)
        {
            if (m_classes == null)
            {
                m_classes = Attr("class").Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            }
            foreach (string cls in m_classes)
            {
                if (cls == name)
                {
                    return true;
                }
            }
            return false;
        }

        public void Add(HtmlNode child)
        {
            child.Parent = this;
            Nodes.Add(child);
        }

        public void Remove()
        {
            Parent?.Nodes.Remove(this);
            Parent = null;
        }

        // Child elements (no text).
        public IEnumerable<HtmlNode> Children
        {
            get
            {
                foreach (HtmlNode node in Nodes)
                {
                    if (!node.IsText)
                    {
                        yield return node;
                    }
                }
            }
        }

        // Every element below, this one not included, in the order of the page.
        public IEnumerable<HtmlNode> Descendants()
        {
            foreach (HtmlNode node in Nodes)
            {
                if (node.IsText)
                {
                    continue;
                }
                yield return node;
                foreach (HtmlNode deeper in node.Descendants())
                {
                    yield return deeper;
                }
            }
        }

        // The first element below that the test accepts.
        public HtmlNode FindFirst(Func<HtmlNode, bool> test)
        {
            foreach (HtmlNode node in Descendants())
            {
                if (test(node))
                {
                    return node;
                }
            }
            return null;
        }

        public List<HtmlNode> FindAll(Func<HtmlNode, bool> test)
        {
            List<HtmlNode> found = new List<HtmlNode>();
            foreach (HtmlNode node in Descendants())
            {
                if (test(node))
                {
                    found.Add(node);
                }
            }
            return found;
        }

        public List<HtmlNode> ChildrenWhere(Func<HtmlNode, bool> test)
        {
            List<HtmlNode> found = new List<HtmlNode>();
            foreach (HtmlNode node in Children)
            {
                if (test(node))
                {
                    found.Add(node);
                }
            }
            return found;
        }

        // This element or the nearest one above it that the test accepts.
        public HtmlNode Closest(Func<HtmlNode, bool> test)
        {
            for (HtmlNode node = this; node != null; node = node.Parent)
            {
                if (!node.IsText && test(node))
                {
                    return node;
                }
            }
            return null;
        }

        public bool HasAncestor(HtmlNode other)
        {
            for (HtmlNode node = Parent; node != null; node = node.Parent)
            {
                if (ReferenceEquals(node, other))
                {
                    return true;
                }
            }
            return false;
        }

        // All the text below, with a space where a block or a line break separates two pieces — the way a browser
        // (and the tool the packs were built with) reads a page, so "14 cutting" and "30 poison" never stick together.
        public string InnerText()
        {
            StringBuilder text = new StringBuilder();
            Collect(this, text);
            return text.ToString();
        }

        private static readonly HashSet<string> s_blockTags = new HashSet<string>
        {
            "address", "article", "aside", "blockquote", "body", "caption", "center", "col", "colgroup", "dd",
            "details", "dir", "div", "dl", "dt", "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2",
            "h3", "h4", "h5", "h6", "header", "hgroup", "hr", "li", "main", "nav", "ol", "p", "pre", "section",
            "summary", "table", "tbody", "td", "tfoot", "th", "thead", "tr", "ul",
        };

        private static bool IsBlockTag(string tag)
        {
            return tag != null && s_blockTags.Contains(tag);
        }

        private static void Collect(HtmlNode node, StringBuilder into)
        {
            for (int i = 0; i < node.Nodes.Count; i++)
            {
                HtmlNode child = node.Nodes[i];
                if (child.IsText)
                {
                    into.Append(child.Text);
                    continue;
                }
                if (into.Length > 0 && (IsBlockTag(child.Tag) || child.Tag == "br") && !EndsWithSpace(into))
                {
                    into.Append(' ');
                }
                Collect(child, into);
                HtmlNode next = i + 1 < node.Nodes.Count ? node.Nodes[i + 1] : null;
                if (IsBlockTag(child.Tag) && next != null && (next.IsText || !IsBlockTag(next.Tag)) && !EndsWithSpace(into) && into.Length > 0)
                {
                    into.Append(' ');
                }
            }
        }

        private static bool EndsWithSpace(StringBuilder text)
        {
            return text.Length == 0 || char.IsWhiteSpace(text[text.Length - 1]);
        }

        public HtmlNode Clone()
        {
            HtmlNode copy = new HtmlNode { Tag = Tag, Text = Text };
            if (m_attributes != null)
            {
                copy.m_attributes = new Dictionary<string, string>(m_attributes, StringComparer.OrdinalIgnoreCase);
            }
            foreach (HtmlNode child in Nodes)
            {
                copy.Add(child.Clone());
            }
            return copy;
        }
    }

    internal static class Html
    {
        private static readonly HashSet<string> s_void = new HashSet<string>
        {
            "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
        };

        private static readonly HashSet<string> s_raw = new HashSet<string> { "script", "style" };

        // Elements that cannot stand inside a paragraph: they close it.
        private static readonly HashSet<string> s_blocks = new HashSet<string>
        {
            "address", "article", "aside", "blockquote", "center", "div", "dl", "fieldset", "figcaption", "figure",
            "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p",
            "pre", "section", "table", "tbody", "td", "tfoot", "th", "thead", "tr", "ul",
        };

        public static HtmlNode Parse(string html)
        {
            HtmlNode root = new HtmlNode { Tag = "body" };
            HtmlNode current = root;
            int i = 0;
            StringBuilder text = new StringBuilder();
            while (i < html.Length)
            {
                char c = html[i];
                if (c != '<')
                {
                    text.Append(c);
                    i++;
                    continue;
                }
                if (html.Length - i > 3 && html[i + 1] == '!' && html[i + 2] == '-' && html[i + 3] == '-')
                {
                    int end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    i = end < 0 ? html.Length : end + 3;
                    continue;
                }
                if (html[i + 1] == '!' || html[i + 1] == '?')
                {
                    int end = html.IndexOf('>', i);
                    i = end < 0 ? html.Length : end + 1;
                    continue;
                }
                Flush(current, text);
                if (html[i + 1] == '/')
                {
                    int end = html.IndexOf('>', i);
                    string name = (end < 0 ? html.Substring(i + 2) : html.Substring(i + 2, end - i - 2)).Trim().ToLowerInvariant();
                    current = CloseTag(current, root, name);
                    i = end < 0 ? html.Length : end + 1;
                    continue;
                }
                int tagEnd = FindTagEnd(html, i);
                string raw = html.Substring(i + 1, tagEnd - i - 1);
                i = tagEnd + 1;
                bool selfClosing = raw.EndsWith("/", StringComparison.Ordinal);
                if (selfClosing)
                {
                    raw = raw.Substring(0, raw.Length - 1);
                }
                int space = IndexOfSpace(raw);
                string tag = (space < 0 ? raw : raw.Substring(0, space)).ToLowerInvariant();
                if (tag.Length == 0)
                {
                    continue;
                }
                HtmlNode element = new HtmlNode { Tag = tag };
                if (space >= 0)
                {
                    ReadAttributes(element, raw, space);
                }
                current = OpenTag(current, root, element);
                if (s_raw.Contains(tag))
                {
                    int end = IndexOfClose(html, i, tag);
                    i = end < 0 ? html.Length : end;
                    current = CloseTag(current, root, tag);
                    continue;
                }
                if (selfClosing || s_void.Contains(tag))
                {
                    current = element.Parent ?? root;
                }
            }
            Flush(current, text);
            return root;
        }

        private static void Flush(HtmlNode current, StringBuilder text)
        {
            if (text.Length == 0)
            {
                return;
            }
            current.Add(new HtmlNode { Text = Decode(text.ToString()) });
            text.Length = 0;
        }

        // The tag ends at the first '>' outside a quoted attribute value.
        private static int FindTagEnd(string html, int start)
        {
            char quote = '\0';
            for (int i = start + 1; i < html.Length; i++)
            {
                char c = html[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    quote = c;
                }
                else if (c == '>')
                {
                    return i;
                }
            }
            return html.Length - 1;
        }

        private static int IndexOfSpace(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    return i;
                }
            }
            return -1;
        }

        private static int IndexOfClose(string html, int from, string tag)
        {
            int at = html.IndexOf("</" + tag, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return -1;
            }
            int end = html.IndexOf('>', at);
            return end < 0 ? -1 : end + 1;
        }

        private static void ReadAttributes(HtmlNode element, string raw, int from)
        {
            int i = from;
            while (i < raw.Length)
            {
                while (i < raw.Length && char.IsWhiteSpace(raw[i]))
                {
                    i++;
                }
                int nameStart = i;
                while (i < raw.Length && !char.IsWhiteSpace(raw[i]) && raw[i] != '=')
                {
                    i++;
                }
                if (i == nameStart)
                {
                    break;
                }
                string name = raw.Substring(nameStart, i - nameStart).ToLowerInvariant();
                while (i < raw.Length && char.IsWhiteSpace(raw[i]))
                {
                    i++;
                }
                string value = "";
                if (i < raw.Length && raw[i] == '=')
                {
                    i++;
                    while (i < raw.Length && char.IsWhiteSpace(raw[i]))
                    {
                        i++;
                    }
                    if (i < raw.Length && (raw[i] == '"' || raw[i] == '\''))
                    {
                        char quote = raw[i++];
                        int start = i;
                        while (i < raw.Length && raw[i] != quote)
                        {
                            i++;
                        }
                        value = raw.Substring(start, i - start);
                        i = Math.Min(raw.Length, i + 1);
                    }
                    else
                    {
                        int start = i;
                        while (i < raw.Length && !char.IsWhiteSpace(raw[i]))
                        {
                            i++;
                        }
                        value = raw.Substring(start, i - start);
                    }
                }
                element.SetAttribute(name, Decode(value));
            }
        }

        // What a browser closes by itself when a new tag starts.
        private static HtmlNode OpenTag(HtmlNode current, HtmlNode root, HtmlNode element)
        {
            string tag = element.Tag;
            if (tag == "li")
            {
                current = CloseWhile(current, root, open => open == "li");
            }
            else if (tag == "td" || tag == "th")
            {
                current = CloseWhile(current, root, open => open == "td" || open == "th");
            }
            else if (tag == "tr")
            {
                current = CloseWhile(current, root, open => open == "td" || open == "th" || open == "tr");
            }
            else if (tag == "dd" || tag == "dt")
            {
                current = CloseWhile(current, root, open => open == "dd" || open == "dt");
            }
            else if (tag == "thead" || tag == "tbody" || tag == "tfoot")
            {
                current = CloseWhile(current, root, open => open == "td" || open == "th" || open == "tr" || open == "thead" || open == "tbody" || open == "tfoot");
            }
            else if (tag == "option")
            {
                current = CloseWhile(current, root, open => open == "option");
            }
            else if (s_blocks.Contains(tag))
            {
                current = CloseWhile(current, root, open => open == "p");
            }
            current.Add(element);
            return element;
        }

        private static HtmlNode CloseWhile(HtmlNode current, HtmlNode root, Func<string, bool> close)
        {
            while (current != root && current.Tag != null && close(current.Tag))
            {
                current = current.Parent ?? root;
            }
            return current;
        }

        private static HtmlNode CloseTag(HtmlNode current, HtmlNode root, string tag)
        {
            for (HtmlNode node = current; node != null && node != root; node = node.Parent)
            {
                if (node.Tag == tag)
                {
                    return node.Parent ?? root;
                }
            }
            // a closing tag for something never opened: ignored, as browsers do
            return current;
        }

        // ---------------------------------------------------------------- entities

        private static readonly Dictionary<string, string> s_entities = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["amp"] = "&", ["lt"] = "<", ["gt"] = ">", ["quot"] = "\"", ["apos"] = "'", ["nbsp"] = " ",
            ["ndash"] = "–", ["mdash"] = "—", ["hellip"] = "…", ["laquo"] = "«", ["raquo"] = "»", ["times"] = "×",
            ["middot"] = "·", ["bull"] = "•", ["deg"] = "°", ["plusmn"] = "±", ["frac12"] = "½", ["frac14"] = "¼",
            ["lsquo"] = "‘", ["rsquo"] = "’", ["ldquo"] = "“", ["rdquo"] = "”", ["sbquo"] = "‚", ["bdquo"] = "„",
            ["dagger"] = "†", ["Dagger"] = "‡", ["permil"] = "‰", ["prime"] = "′", ["Prime"] = "″", ["euro"] = "€",
            ["copy"] = "©", ["reg"] = "®", ["trade"] = "™", ["sect"] = "§", ["para"] = "¶", ["shy"] = "­",
            ["ensp"] = " ", ["emsp"] = " ", ["thinsp"] = " ", ["zwnj"] = "‌", ["zwj"] = "‍",
            ["larr"] = "←", ["uarr"] = "↑", ["rarr"] = "→", ["darr"] = "↓", ["harr"] = "↔", ["star"] = "☆",
            ["starf"] = "★", ["check"] = "✓", ["cross"] = "✗", ["infin"] = "∞", ["ne"] = "≠", ["le"] = "≤", ["ge"] = "≥",
        };

        internal static string Decode(string text)
        {
            if (text.IndexOf('&') < 0)
            {
                return text;
            }
            StringBuilder out_ = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c != '&')
                {
                    out_.Append(c);
                    i++;
                    continue;
                }
                int end = text.IndexOf(';', i + 1);
                if (end < 0 || end - i > 12)
                {
                    out_.Append(c);
                    i++;
                    continue;
                }
                string name = text.Substring(i + 1, end - i - 1);
                if (name.Length > 1 && name[0] == '#')
                {
                    string digits = name.Substring(1);
                    bool hex = digits.Length > 0 && (digits[0] == 'x' || digits[0] == 'X');
                    if (hex)
                    {
                        digits = digits.Substring(1);
                    }
                    if (int.TryParse(digits, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out int code)
                        && code > 0 && code <= 0x10FFFF)
                    {
                        out_.Append(char.ConvertFromUtf32(code));
                        i = end + 1;
                        continue;
                    }
                }
                else if (s_entities.TryGetValue(name, out string value))
                {
                    out_.Append(value);
                    i = end + 1;
                    continue;
                }
                out_.Append(c);
                i++;
            }
            return out_.ToString();
        }
    }
}
