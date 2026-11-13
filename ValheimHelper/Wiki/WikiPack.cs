using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ValheimHelper.Wiki
{
    // One language of the in-game wiki: ValheimHelper/wiki/<lang>/pages.txt (every article, in the block format the
    // wiki tool writes, see PackFormat.java), redirects.tsv and meta.txt. The file is read and indexed on a background
    // thread; an article is parsed when it is opened.
    internal sealed class WikiPack
    {
        // Formatting inside text: characters below the space that never occur in wiki text.
        internal const char LinkStart = '';
        internal const char LinkMid = '';
        internal const char LinkEnd = '';
        internal const char BoldOn = '';
        internal const char BoldOff = '';
        internal const char ItalicOn = '';
        internal const char ItalicOff = '';
        internal const char IconStart = '';
        internal const char IconEnd = '';
        internal const char Break = '';

        internal readonly string Language;
        internal readonly string Dir;
        internal volatile bool Ready;
        internal volatile string Error;
        internal readonly Dictionary<string, string> Meta = new Dictionary<string, string>();

        private string m_text = "";
        private readonly Dictionary<string, int> m_starts = new Dictionary<string, int>();
        private readonly Dictionary<string, string> m_byLower = new Dictionary<string, string>();
        private readonly Dictionary<string, string> m_redirects = new Dictionary<string, string>();
        private readonly Dictionary<string, List<string>> m_categories = new Dictionary<string, List<string>>();
        private readonly List<string> m_titles = new List<string>();
        private readonly List<KeyValuePair<string, string>> m_searchNames = new List<KeyValuePair<string, string>>();
        private readonly Dictionary<string, WikiArticle> m_cache = new Dictionary<string, WikiArticle>();
        // The same article in other languages: title → language → title there (langlinks.tsv).
        private readonly Dictionary<string, Dictionary<string, string>> m_links = new Dictionary<string, Dictionary<string, string>>();
        private readonly LinkedList<string> m_cacheOrder = new LinkedList<string>();

        internal WikiPack(string language, string dir)
        {
            Language = language;
            Dir = dir;
            Thread thread = new Thread(Load) { IsBackground = true, Name = "VH wiki " + language };
            thread.Start();
        }

        internal int Count => m_titles.Count;
        internal IList<string> Titles => m_titles;
        internal Dictionary<string, List<string>> Categories => m_categories;

        internal string Name
        {
            get
            {
                return LanguageName(Language);
            }
        }

        internal static string LanguageName(string code)
        {
            code = WikiSources.Language(code);
            switch (code)
            {
                case "en": return "English";
                case "uk": return "Українська";
                case "de": return "Deutsch";
                case "fr": return "Français";
                case "es": return "Español";
                case "pl": return "Polski";
                case "pt-br": return "Português (Brasil)";
                case "it": return "Italiano";
                case "zh": return "中文";
                case "ja": return "日本語";
                case "ko": return "한국어";
                case "cs": return "Čeština";
                default: return code;
            }
        }

        private void Load()
        {
            try
            {
                string metaPath = Path.Combine(Dir, "meta.txt");
                if (File.Exists(metaPath))
                {
                    foreach (string line in File.ReadAllLines(metaPath, Encoding.UTF8))
                    {
                        int space = line.IndexOf(' ');
                        if (space > 0)
                        {
                            Meta[line.Substring(0, space)] = line.Substring(space + 1).Trim();
                        }
                    }
                }
                string text = File.ReadAllText(Path.Combine(Dir, "pages.txt"), Encoding.UTF8);
                // articles the mod brought in from the wiki itself stand before the pack, so they are the ones read
                WikiUpdate.Prune(Dir);
                string updates = WikiUpdate.UpdatesPath(Dir);
                if (File.Exists(updates))
                {
                    text = File.ReadAllText(updates, Encoding.UTF8) + "\n" + text;
                }
                Index(text);
                string redirectPath = Path.Combine(Dir, "redirects.tsv");
                if (File.Exists(redirectPath))
                {
                    foreach (string line in File.ReadAllLines(redirectPath, Encoding.UTF8))
                    {
                        int tab = line.IndexOf('\t');
                        if (tab > 0 && !line.StartsWith("#", StringComparison.Ordinal))
                        {
                            string from = line.Substring(0, tab);
                            string to = line.Substring(tab + 1).Trim();
                            m_redirects[from] = to;
                            string lower = from.ToLowerInvariant();
                            if (!m_byLower.ContainsKey(lower))
                            {
                                m_byLower[lower] = from;
                            }
                            string toPage = to;
                            int hash = toPage.IndexOf('#');
                            if (hash >= 0)
                            {
                                toPage = toPage.Substring(0, hash);
                            }
                            if (m_starts.ContainsKey(toPage))
                            {
                                m_searchNames.Add(new KeyValuePair<string, string>(Normalize(from), from));
                            }
                        }
                    }
                }
                string linksPath = Path.Combine(Dir, "langlinks.tsv");
                if (File.Exists(linksPath))
                {
                    foreach (string line in File.ReadAllLines(linksPath, Encoding.UTF8))
                    {
                        string[] parts = line.Split('\t');
                        if (parts.Length < 3)
                        {
                            continue;
                        }
                        if (!m_links.TryGetValue(parts[0], out Dictionary<string, string> byLanguage))
                        {
                            byLanguage = new Dictionary<string, string>();
                            m_links[parts[0]] = byLanguage;
                        }
                        byLanguage[parts[1]] = parts[2];
                    }
                }
                m_text = text;
                Ready = true;
            }
            catch (Exception e)
            {
                Error = e.Message;
                Log.Error(e);
            }
        }

        private void Index(string text)
        {
            int position = 0;
            string current = null;
            while (position < text.Length)
            {
                int end = text.IndexOf('\n', position);
                if (end < 0)
                {
                    end = text.Length;
                }
                if (end - position > 4 && text[position] == '=' && string.CompareOrdinal(text, position, "=== ", 0, 4) == 0)
                {
                    string title = text.Substring(position + 4, end - position - 4).TrimEnd('\r');
                    if (m_starts.ContainsKey(title))
                    {
                        // the same article once more: the first one (an update) wins, the pack's copy is skipped
                        current = null;
                        position = end + 1;
                        continue;
                    }
                    current = title;
                    m_starts[current] = end + 1;
                    m_titles.Add(current);
                    m_byLower[current.ToLowerInvariant()] = current;
                    m_searchNames.Add(new KeyValuePair<string, string>(Normalize(current), current));
                }
                else if (current != null && end - position > 4 && text[position] == 'c' && string.CompareOrdinal(text, position, "cat ", 0, 4) == 0)
                {
                    foreach (string category in text.Substring(position + 4, end - position - 4).TrimEnd('\r').Split('|'))
                    {
                        if (category.Length == 0)
                        {
                            continue;
                        }
                        if (!m_categories.TryGetValue(category, out List<string> pages))
                        {
                            pages = new List<string>();
                            m_categories[category] = pages;
                        }
                        pages.Add(current);
                    }
                }
                position = end + 1;
            }
            m_titles.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (List<string> pages in m_categories.Values)
            {
                pages.Sort(StringComparer.OrdinalIgnoreCase);
            }
        }

        internal bool Has(string title)
        {
            return title != null && m_starts.ContainsKey(title);
        }

        // A link target → the article it shows (following redirects, ignoring letter case) and the section anchor.
        internal string Resolve(string target, out string anchor)
        {
            anchor = null;
            if (string.IsNullOrEmpty(target))
            {
                return null;
            }
            string page = target;
            for (int hop = 0; hop < 4; hop++)
            {
                int hash = page.IndexOf('#');
                if (hash >= 0)
                {
                    string part = page.Substring(hash + 1);
                    if (part.Length > 0)
                    {
                        anchor = part;
                    }
                    page = page.Substring(0, hash);
                }
                if (page.Length == 0)
                {
                    return null;
                }
                if (m_starts.ContainsKey(page))
                {
                    return page;
                }
                if (m_redirects.TryGetValue(page, out string to))
                {
                    page = to;
                    continue;
                }
                if (m_byLower.TryGetValue(page.ToLowerInvariant(), out string known) && known != page)
                {
                    page = known;
                    continue;
                }
                return null;
            }
            return null;
        }

        internal WikiArticle Get(string title)
        {
            if (!Ready || title == null || !m_starts.TryGetValue(title, out int start))
            {
                return null;
            }
            if (m_cache.TryGetValue(title, out WikiArticle cached))
            {
                m_cacheOrder.Remove(title);
                m_cacheOrder.AddFirst(title);
                return cached;
            }
            int end = m_text.IndexOf("\n=== ", start - 1, StringComparison.Ordinal);
            if (end < 0)
            {
                end = m_text.Length;
            }
            WikiArticle article = WikiParser.Parse(title, m_text.Substring(start, end - start));
            m_cache[title] = article;
            m_cacheOrder.AddFirst(title);
            while (m_cacheOrder.Count > 24)
            {
                m_cache.Remove(m_cacheOrder.Last.Value);
                m_cacheOrder.RemoveLast();
            }
            return article;
        }

        // Titles and other names of articles for the query, best first: exact, then the start of the name, the start of
        // a word, anywhere. Names of the game in the player's language lead to the articles too ("мідь" → Copper).
        internal List<SearchHit> Search(string query, int max)
        {
            List<SearchHit> hits = new List<SearchHit>();
            string q = Normalize(query);
            if (q.Length == 0 || !Ready)
            {
                return hits;
            }
            HashSet<string> seen = new HashSet<string>();
            for (int rank = 0; rank < 4 && hits.Count < max; rank++)
            {
                foreach (KeyValuePair<string, string> name in m_searchNames)
                {
                    if (Rank(name.Key, q) != rank)
                    {
                        continue;
                    }
                    string page = Resolve(name.Value, out string anchor);
                    if (page == null || !seen.Add(page + "#" + anchor))
                    {
                        continue;
                    }
                    hits.Add(new SearchHit { Title = page, Anchor = anchor, Name = name.Value });
                    if (hits.Count >= max)
                    {
                        break;
                    }
                }
                foreach (GameNames.Name name in GameNames.Pairs)
                {
                    if (hits.Count >= max)
                    {
                        break;
                    }
                    if (Rank(name.Key, q) != rank)
                    {
                        continue;
                    }
                    string page = Resolve(name.English, out string anchor);
                    if (page == null || !seen.Add(page + "#" + anchor))
                    {
                        continue;
                    }
                    hits.Add(new SearchHit { Title = page, Anchor = anchor, Name = name.Local + "  →  " + page });
                }
            }
            return hits;
        }

        private static int Rank(string name, string query)
        {
            if (name == query)
            {
                return 0;
            }
            if (name.StartsWith(query, StringComparison.Ordinal))
            {
                return 1;
            }
            int at = name.IndexOf(query, StringComparison.Ordinal);
            if (at < 0)
            {
                return -1;
            }
            return name[at - 1] == ' ' ? 2 : 3;
        }

        internal static string Normalize(string text)
        {
            StringBuilder builder = new StringBuilder(text.Length);
            bool space = false;
            foreach (char c in text.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (space && builder.Length > 0)
                    {
                        builder.Append(' ');
                    }
                    space = false;
                    builder.Append(c == 'ё' ? 'е' : c);
                }
                else if (c != '\'' && c != '’' && c != 'ʼ')
                {
                    space = true;
                }
            }
            return builder.ToString();
        }

        // The title of this article in another language's wiki, if the wiki links them.
        internal string LinkTo(string title, string language)
        {
            return title != null && m_links.TryGetValue(title, out Dictionary<string, string> byLanguage)
                && byLanguage.TryGetValue(language, out string target) ? target : null;
        }

        // The article here that another language's article is linked with (the link written only on this side).
        internal string LinkedFrom(string language, string title)
        {
            foreach (KeyValuePair<string, Dictionary<string, string>> pair in m_links)
            {
                if (pair.Value.TryGetValue(language, out string target) && string.Equals(target, title, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Key;
                }
            }
            return null;
        }

        internal string MetaValue(string key)
        {
            return Meta.TryGetValue(key, out string value) ? value : "";
        }
    }
}
