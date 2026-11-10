using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace ValheimHelper.Wiki
{
    // "Довідник": the wiki inside the mod window. A bar with back, forward, home, search and the language of the
    // articles; under it the page, laid out for the width of the window (again when the window is resized) and
    // scrolled; links open articles here, pictures load in the background.
    internal sealed class WikiView
    {
        private const string SearchControl = "vh_wiki_search";
        private const float BarHeight = 30f;
        private const float Pad = 16f;
        private const float ScrollbarWidth = 16f;
        private const float MaxReadingWidth = 1150f;
        private const float HitHeight = 28f;
        private const int MaxHits = 12;
        private const string Home = "::home";
        private const string Donate = "::donate";

        // English category of the wiki → its name here, in the order of the home page.
        private static readonly string[][] s_sections =
        {
            new[] { "Biomes", Lang.T("Біоми") },
            new[] { "Creatures", Lang.T("Істоти") },
            new[] { "Bosses", Lang.T("Боси") },
            new[] { "Tameable creatures", Lang.T("Приручувані тварини") },
            new[] { "NPCs", Lang.T("Торговці й персонажі") },
            new[] { "Points of interest", Lang.T("Цікаві місця") },
            new[] { "Dungeons", Lang.T("Підземелля") },
            new[] { "Materials", Lang.T("Матеріали") },
            new[] { "Ores", Lang.T("Руди") },
            new[] { "Metals", Lang.T("Метали") },
            new[] { "Wood", Lang.T("Деревина") },
            new[] { "Resource nodes", Lang.T("Джерела ресурсів") },
            new[] { "Food", Lang.T("Їжа") },
            new[] { "Mead", Lang.T("Меди й зілля") },
            new[] { "Fish", Lang.T("Риба") },
            new[] { "Farming", Lang.T("Фермерство") },
            new[] { "Weapons", Lang.T("Зброя") },
            new[] { "Armor", Lang.T("Броня") },
            new[] { "Shields", Lang.T("Щити") },
            new[] { "Arrows", Lang.T("Стріли") },
            new[] { "Tools", Lang.T("Інструменти") },
            new[] { "Crafting structures", Lang.T("Верстати й станції") },
            new[] { "Building structures", Lang.T("Будівельні блоки") },
            new[] { "Furniture", Lang.T("Меблі") },
            new[] { "Light sources", Lang.T("Освітлення") },
            new[] { "Storage", Lang.T("Зберігання") },
            new[] { "Effects", Lang.T("Ефекти") },
            new[] { "Skills", Lang.T("Навички") },
            new[] { "Mechanics", Lang.T("Механіки") },
            new[] { "Magic", Lang.T("Магія") },
            new[] { "Guides", Lang.T("Посібники") },
            new[] { "Lore", Lang.T("Історія світу") },
        };

        private struct HistoryEntry
        {
            public string Page;
            public float Scroll;
        }

        private readonly Action<string> m_message;
        private readonly Dictionary<string, WikiPack> m_packs = new Dictionary<string, WikiPack>();
        private readonly List<string> m_languages = new List<string>();
        private bool m_scanned;
        private WikiPack m_pack;
        private string m_page;
        private string m_anchor;
        private float m_pendingScroll = -1f;
        private readonly List<HistoryEntry> m_history = new List<HistoryEntry>();
        private int m_historyIndex = -1;
        private WikiArticle m_article;
        private string m_articleKey;
        private WikiLayout m_layout;
        private float m_layoutWidth;
        private bool m_layoutDirty;
        private string m_footer;
        private readonly Dictionary<int, int> m_tabs = new Dictionary<int, int>();
        private Vector2 m_scroll;
        private string m_search = "";
        private string m_searchFor;
        private List<SearchHit> m_hits = new List<SearchHit>();
        private bool m_showHits;
        private int m_hitIndex;
        private bool m_langOpen;
        private Rect m_lastArea = new Rect(0f, 0f, 600f, 400f);
        private GUIStyle m_rowButton;
        // Switching the language on an article: that article in the new language once its pack is read.
        private WikiPack m_switchFrom;
        private string m_switchTitle;
        // A note over the home page (the article was not found in the chosen language), in that language.
        private string m_notice;

        // "There is no such article in this language", in the language of the wiki chosen.
        private static readonly Dictionary<string, string> s_notFound = new Dictionary<string, string>
        {
            ["en"] = "There is no article «{0}» in English. Here is the main page.",
            ["uk"] = "Статті «{0}» українською ще немає. Ось головна сторінка.",
            ["de"] = "Den Artikel „{0}“ gibt es nicht auf Deutsch. Hier ist die Startseite.",
            ["fr"] = "L’article « {0} » n’existe pas en français. Voici la page d’accueil.",
            ["pl"] = "Artykułu „{0}” nie ma po polsku. Oto strona główna.",
            ["pt-br"] = "O artigo “{0}” não existe em português. Esta é a página inicial.",
            ["cs"] = "Článek „{0}“ v češtině není. Toto je hlavní stránka.",
            ["zh"] = "没有“{0}”的中文条目。这里是首页。",
        };
        private GUIStyle m_placeholder;

        internal WikiView(Action<string> message)
        {
            m_message = message;
        }

        private static string WikiDir => Path.Combine(Loader.Dir ?? ".", "wiki");

        // ---------------------------------------------------------------- packs and pages

        private void EnsurePacks()
        {
            if (m_scanned)
            {
                return;
            }
            m_scanned = true;
            try
            {
                if (Directory.Exists(WikiDir))
                {
                    foreach (string dir in Directory.GetDirectories(WikiDir))
                    {
                        string code = Path.GetFileName(dir);
                        if (code != "img" && File.Exists(Path.Combine(dir, "pages.txt")))
                        {
                            m_languages.Add(code);
                            m_pageCounts[code] = PageCount(dir);
                        }
                    }
                }
                // English (the fullest) first, then Ukrainian, then the others
                m_languages.Sort((a, b) => Order(a) != Order(b) ? Order(a).CompareTo(Order(b)) : string.CompareOrdinal(a, b));
                WikiImages.SetDir(Path.Combine(WikiDir, "img"));
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
            string language = m_languages.Contains(State.WikiLanguage) ? State.WikiLanguage
                : m_languages.Contains("en") ? "en" : m_languages.Count > 0 ? m_languages[0] : null;
            SetLanguage(language, keepPage: true);
        }

        private readonly Dictionary<string, int> m_pageCounts = new Dictionary<string, int>();

        // "pages N" of a pack's meta.txt, without loading the pack.
        private static int PageCount(string dir)
        {
            try
            {
                foreach (string line in File.ReadAllLines(Path.Combine(dir, "meta.txt")))
                {
                    if (line.StartsWith("pages ", StringComparison.Ordinal) && int.TryParse(line.Substring(6).Trim(), out int count))
                    {
                        return count;
                    }
                }
            }
            catch (Exception)
            {
                // no meta: the count is not shown
            }
            return 0;
        }

        private static int Order(string code)
        {
            return code == "en" ? 0 : code == "uk" ? 1 : 2;
        }

        private void SetLanguage(string code, bool keepPage)
        {
            if (code == null)
            {
                m_pack = null;
            }
            else
            {
                if (!m_packs.TryGetValue(code, out WikiPack pack))
                {
                    pack = new WikiPack(code, Path.Combine(WikiDir, code));
                    m_packs[code] = pack;
                }
                m_pack = pack;
                State.WikiLanguage = code;
            }
            string start = keepPage && !string.IsNullOrEmpty(State.WikiPage) ? State.WikiPage : Home;
            m_history.Clear();
            m_historyIndex = -1;
            m_page = null;
            GoTo(start, null, 0f, push: true);
            m_searchFor = null;
        }

        // Another language of the articles, staying on the same page: the article in that language (through the
        // wikis' interlanguage links), the same page of the mod, or the home page with a note when there is none.
        private void SwitchLanguage(string code)
        {
            WikiPack from = m_pack;
            string page = m_page;
            SetLanguage(code, keepPage: false);
            if (page == null || page == Home)
            {
                return;
            }
            if (page == "::all" || page == "::categories" || page == Donate)
            {
                GoTo(page, null, 0f, push: false);
                return;
            }
            if (!page.StartsWith("::", StringComparison.Ordinal) && from != null)
            {
                m_switchFrom = from;
                m_switchTitle = page;
            }
        }

        // Called while drawing: finishes a language switch once both packs are read.
        private void FinishSwitch()
        {
            if (m_switchTitle == null || m_pack == null || !m_pack.Ready || !m_switchFrom.Ready)
            {
                return;
            }
            string title = m_switchTitle;
            WikiPack from = m_switchFrom;
            m_switchTitle = null;
            m_switchFrom = null;
            string anchor = null;
            string target = from.LinkTo(title, m_pack.Language) ?? m_pack.LinkedFrom(from.Language, title);
            string found = target != null ? m_pack.Resolve(target, out anchor) : null;
            if (found == null)
            {
                // wikis in other languages often keep the English name
                found = m_pack.Resolve(title, out anchor);
            }
            if (found != null)
            {
                m_history.Clear();
                m_historyIndex = -1;
                m_page = null;
                GoTo(found, anchor, 0f, push: true);
                return;
            }
            string format = s_notFound.TryGetValue(m_pack.Language, out string text) ? text : s_notFound["en"];
            m_notice = string.Format(format, title);
            m_articleKey = null;
        }

        // The guide keeps itself up to date: once a game it asks the wiki which articles changed and brings them in.
        private void CheckUpdates()
        {
            if (m_pack == null || !m_pack.Ready)
            {
                return;
            }
            StartUpdates(byHand: false);
            if (WikiUpdate.PackChanged && !WikiUpdate.Running)
            {
                WikiUpdate.PackChanged = false;
                WikiImages.Reload();
                foreach (string code in WikiUpdate.TakeChanged())
                {
                    WikiPack fresh = new WikiPack(code, Path.Combine(WikiDir, code));
                    m_packs[code] = fresh;
                    if (m_pack.Language == code)
                    {
                        m_pack = fresh;
                    }
                }
                m_articleKey = null;
                m_layout = null;
            }
        }

        // Two wikis are kept fresh: the chosen language and English. English is where an article missing in the
        // chosen language is read from, so a stale English pack would mean stale articles there too.
        private void StartUpdates(bool byHand)
        {
            if (m_pack == null || !m_pack.Ready)
            {
                return;
            }
            WikiUpdate.Start(m_pack.Language, m_pack.Dir, byHand);
            if (m_pack.Language != "en" && m_languages.Contains("en"))
            {
                WikiUpdate.Start("en", Path.Combine(WikiDir, "en"), byHand);
            }
        }

        // Opens what a link points to: an article (with a section), a page of the mod, a web page or an action.
        private void Open(string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                return;
            }
            if (target.StartsWith("http", StringComparison.Ordinal))
            {
                Application.OpenURL(target);
                m_message(Lang.T("Посилання відкрито в браузері."));
                return;
            }
            if (target == "::action:glossary")
            {
                m_message(GameDataExport.ExportAll());
                return;
            }
            if (target == "::action:placeshots")
            {
                m_message(WikiStudio.StartPlaces());
                return;
            }
            if (target == "::action:wikiupdate")
            {
                StartUpdates(byHand: true);
                m_articleKey = null;
                return;
            }
            if (target == "::action:wikiauto")
            {
                State.WikiAutoUpdate = !State.WikiAutoUpdate;
                m_message(State.WikiAutoUpdate
                    ? Lang.T("Довідник оновлюватиметься з вікі сам.")
                    : Lang.T("Автоматичне оновлення довідника вимкнено."));
                m_articleKey = null;
                return;
            }
            string anchor = null;
            string page;
            if (target.StartsWith("::", StringComparison.Ordinal))
            {
                page = target;
            }
            else if (target.StartsWith("Category:", StringComparison.Ordinal))
            {
                page = "::category:" + target.Substring("Category:".Length);
            }
            else
            {
                page = m_pack != null ? m_pack.Resolve(target, out anchor) : null;
                if (page == null)
                {
                    m_message(Lang.F("Статті «{0}» у довіднику немає.", target));
                    return;
                }
            }
            GoTo(page, anchor, 0f, push: true);
        }

        private void GoTo(string page, string anchor, float scroll, bool push)
        {
            if (page == m_page && m_page != null)
            {
                if (anchor != null)
                {
                    m_anchor = anchor;
                }
                return;
            }
            if (push)
            {
                if (m_historyIndex >= 0 && m_historyIndex < m_history.Count)
                {
                    m_history[m_historyIndex] = new HistoryEntry { Page = m_page, Scroll = m_scroll.y };
                }
                if (m_historyIndex + 1 < m_history.Count)
                {
                    m_history.RemoveRange(m_historyIndex + 1, m_history.Count - m_historyIndex - 1);
                }
                m_history.Add(new HistoryEntry { Page = page });
                m_historyIndex = m_history.Count - 1;
            }
            m_page = page;
            m_anchor = anchor;
            m_pendingScroll = anchor == null ? scroll : -1f;
            m_article = null;
            m_articleKey = null;
            m_tabs.Clear();
            m_search = "";
            m_showHits = false;
            m_langOpen = false;
            m_notice = null;
            State.WikiPage = page;
        }

        private void Step(int direction)
        {
            int index = m_historyIndex + direction;
            if (index < 0 || index >= m_history.Count)
            {
                return;
            }
            m_history[m_historyIndex] = new HistoryEntry { Page = m_page, Scroll = m_scroll.y };
            m_historyIndex = index;
            HistoryEntry entry = m_history[index];
            m_page = null;
            GoTo(entry.Page, null, entry.Scroll, push: false);
        }

        private void EnsureArticle()
        {
            FinishSwitch();
            string key = (m_pack != null ? m_pack.Language + (m_pack.Ready ? "+" : "-") : "none") + "|" + m_page;
            if (key == m_articleKey && m_article != null)
            {
                return;
            }
            m_articleKey = key;
            m_layout = null;
            m_footer = null;
            if (m_page == Home)
            {
                m_article = HomePage();
            }
            else if (m_page == Donate)
            {
                m_article = DonatePage();
            }
            else if (m_page == "::categories")
            {
                m_article = CategoriesPage();
            }
            else if (m_page == "::all")
            {
                m_article = AllPage();
            }
            else if (m_page != null && m_page.StartsWith("::category:", StringComparison.Ordinal))
            {
                m_article = CategoryPage(m_page.Substring("::category:".Length));
            }
            else
            {
                m_article = m_pack?.Get(m_page);
                if (m_article == null)
                {
                    m_article = Own(m_page ?? "", m_pack != null && !m_pack.Ready ? Lang.T("Статті завантажуються…") : Lang.T("Цієї статті в довіднику немає."));
                }
                else
                {
                    m_footer = ArticleFooter(m_article);
                }
            }
        }

        private bool LinkExists(string target)
        {
            if (target.StartsWith("::", StringComparison.Ordinal) || target.StartsWith("http", StringComparison.Ordinal))
            {
                return true;
            }
            if (m_pack == null)
            {
                return false;
            }
            if (target.StartsWith("Category:", StringComparison.Ordinal))
            {
                return m_pack.Categories.ContainsKey(target.Substring("Category:".Length));
            }
            return m_pack.Resolve(target, out _) != null;
        }

        // ---------------------------------------------------------------- pages of the mod

        private static string Link(string target, string text)
        {
            return WikiPack.LinkStart + target + WikiPack.LinkMid + text + WikiPack.LinkEnd;
        }

        private static WikiArticle Own(string title, params string[] paragraphs)
        {
            WikiArticle article = new WikiArticle { Title = title, Own = true };
            foreach (string paragraph in paragraphs)
            {
                article.Blocks.Add(new WikiBlock { Kind = BlockKind.Paragraph, Text = paragraph });
            }
            return article;
        }

        private static void Heading(WikiArticle article, string text)
        {
            article.Blocks.Add(new WikiBlock { Kind = BlockKind.Heading, Level = 2, Text = text, Anchor = text.Replace(' ', '_') });
        }

        private WikiArticle HomePage()
        {
            WikiArticle article = Own(Lang.T("Довідник Valheim"),
                Lang.T("Статті вікі Valheim прямо в грі: предмети, істоти, біоми, будівлі, механіки — з картинками, таблицями й посиланнями між статтями. " +
                "Пошук угорі розуміє й назви з гри українською («мідь», «кабан»). Посилання відкривають статті тут же, «‹ Назад» повертає."));
            if (m_notice != null)
            {
                article.Blocks.Insert(0, new WikiBlock { Kind = BlockKind.Notice, Text = m_notice });
            }
            if (m_pack == null)
            {
                article.Blocks.Add(new WikiBlock
                {
                    Kind = BlockKind.Paragraph,
                    Text = Lang.T("Статей не знайдено: у папці мода немає wiki. Перевстанови мод із повного архіву."),
                });
                return article;
            }
            if (!m_pack.Ready)
            {
                article.Blocks.Add(new WikiBlock { Kind = BlockKind.Paragraph, Text = m_pack.Error != null ? Lang.T("Не вдалося прочитати статті: ") + m_pack.Error : Lang.T("Статті завантажуються…") });
                return article;
            }
            Heading(article, Lang.T("Розділи"));
            List<string> items = new List<string>();
            foreach (string[] section in s_sections)
            {
                if (m_pack.Categories.TryGetValue(section[0], out List<string> pages))
                {
                    items.Add(Link("Category:" + section[0], section[1]) + $" ({pages.Count})");
                }
            }
            if (items.Count == 0)
            {
                // a wiki in another language names its categories its own way: the biggest ones
                List<KeyValuePair<string, List<string>>> biggest = new List<KeyValuePair<string, List<string>>>(m_pack.Categories);
                biggest.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));
                for (int i = 0; i < Math.Min(30, biggest.Count); i++)
                {
                    items.Add(Link("Category:" + biggest[i].Key, biggest[i].Key) + $" ({biggest[i].Value.Count})");
                }
                items.Sort(StringComparer.CurrentCultureIgnoreCase);
            }
            article.Blocks.Add(new WikiBlock { Kind = BlockKind.Columns, Items = items });
            article.Blocks.Add(new WikiBlock
            {
                Kind = BlockKind.Paragraph,
                Text = Link("::categories", Lang.F("Усі категорії ({0})", m_pack.Categories.Count)) + "   ·   " + Link("::all", Lang.F("Усі статті від A до Z ({0})", m_pack.Count)),
            });
            Heading(article, Lang.T("Мова статей"));
            List<string> names = new List<string>();
            foreach (string code in m_languages)
            {
                names.Add(WikiPack.LanguageName(code));
            }
            article.Blocks.Add(new WikiBlock
            {
                Kind = BlockKind.Paragraph,
                Text = Lang.T("Кнопка вгорі праворуч перемикає мову статей — окремо від мови меню. Зараз є: ") + string.Join(", ", names.ToArray()) +
                    (m_pageCounts.TryGetValue("uk", out int ukCount)
                        ? Lang.F(". Українських статей поки {0}: решта з'являться, щойно їх перекладуть і вичитають.", ukCount)
                        : Lang.T(". Українські статті з'являться тут, щойно їх перекладуть і вичитають.")),
            });
            Heading(article, Lang.T("Оновлення статей"));
            List<KeyValuePair<string, string>> lines = WikiUpdate.StatusLines();
            string status;
            if (lines.Count == 0)
            {
                status = Lang.T("Ще не перевіряв.");
            }
            else
            {
                List<string> parts = new List<string>();
                foreach (KeyValuePair<string, string> line in lines)
                {
                    parts.Add(WikiPack.LanguageName(line.Key) + " — " + line.Value);
                }
                status = string.Join("   ·   ", parts.ToArray());
            }
            article.Blocks.Add(new WikiBlock
            {
                Kind = BlockKind.Paragraph,
                Text = Lang.T("Мод сам питає дві вікі — твоєї мови й англійську, бо звідти беруться статті, яких у твоїй ще немає, — і докачує лише змінені статті та їхні картинки. Перевіряє, поки довідник відкритий, не частіше ніж раз на 15 хвилин і не частіше за один запит на секунду. ") +
                    Lang.F("Стан: {0}", status) + "   ·   " +
                    Link("::action:wikiupdate", Lang.T("Оновити зараз")) + "   ·   " +
                    Link("::action:wikiauto", State.WikiAutoUpdate ? Lang.T("Вимкнути автооновлення") : Lang.T("Увімкнути автооновлення")),
            });
            Heading(article, Lang.T("Для перекладачів"));
            article.Blocks.Add(new WikiBlock
            {
                Kind = BlockKind.Paragraph,
                Text = Link("::action:glossary", Lang.T("Вивантажити дані гри")) + Lang.T(" — для статей вікі з цифрами самої гри: офіційні назви англійською й українською " +
                    "(wiki/glossary_game.tsv) і все про предмети, рецепти, будівлі, істот, де вони з'являються, та ефекти (wiki/game_data.json). Працює у світі."),
            });
            article.Blocks.Add(new WikiBlock
            {
                Kind = BlockKind.Paragraph,
                Text = Lang.T("Разом із даними мод знімає кожну істоту (і з зірками) та кожну будівлю на прозорому тлі — картинки для вікі (wiki/renders). ") +
                    Link("::action:placeshots", Lang.T("Зняти місця для вікі")) + Lang.T(" — вікінга по черзі перенесе до найближчого місця кожного виду, " +
                    "без шкоди й непомітно, камера зніме його опівдні без інтерфейсу, і вікінг повернеться назад. Краще робити це в тестовому світі."),
            });
            m_footer = Attribution();
            return article;
        }

        private WikiArticle CategoryPage(string name)
        {
            string label = name;
            foreach (string[] section in s_sections)
            {
                if (section[0] == name)
                {
                    label = section[1] == name ? name : section[1] + " (" + name + ")";
                }
            }
            WikiArticle article = Own(label);
            if (m_pack == null || !m_pack.Categories.TryGetValue(name, out List<string> pages))
            {
                article.Blocks.Add(new WikiBlock { Kind = BlockKind.Paragraph, Text = Lang.T("У цій категорії немає статей.") });
                return article;
            }
            article.Blocks.Add(new WikiBlock { Kind = BlockKind.Paragraph, Text = Lang.F("Статей: {0}. ", pages.Count) + Link(Home, Lang.T("До розділів")) });
            List<string> items = new List<string>();
            foreach (string page in pages)
            {
                items.Add(Link(page, page));
            }
            article.Blocks.Add(new WikiBlock { Kind = BlockKind.Columns, Items = items });
            return article;
        }

        private WikiArticle CategoriesPage()
        {
            WikiArticle article = Own(Lang.T("Усі категорії"));
            if (m_pack == null)
            {
                return article;
            }
            List<string> names = new List<string>(m_pack.Categories.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            List<string> items = new List<string>();
            foreach (string name in names)
            {
                items.Add(Link("Category:" + name, name) + $" ({m_pack.Categories[name].Count})");
            }
            article.Blocks.Add(new WikiBlock { Kind = BlockKind.Columns, Items = items });
            return article;
        }

        private WikiArticle AllPage()
        {
            WikiArticle article = Own(Lang.T("Усі статті"));
            if (m_pack == null)
            {
                return article;
            }
            List<string> items = new List<string>();
            foreach (string title in m_pack.Titles)
            {
                items.Add(Link(title, title));
            }
            article.Blocks.Add(new WikiBlock { Kind = BlockKind.Columns, Items = items });
            return article;
        }

        private static WikiArticle DonatePage()
        {
            WikiArticle article = Own(Lang.T("Російської версії немає"),
                Lang.T("Довідника російською немає й не буде. Якщо хочеш допомогти — підтримай Сили оборони України та людей, які постраждали від війни:"));
            string[][] links =
            {
                new[] { "https://savelife.in.ua/en/", Lang.T("Повернись живим (Come Back Alive)"), Lang.T("допомога армії") },
                new[] { "https://prytulafoundation.org/en", Lang.T("Фонд Сергія Притули (Serhiy Prytula Foundation)"), Lang.T("допомога армії й цивільним") },
                new[] { "https://u24.gov.ua/", "United24", Lang.T("офіційна платформа збору коштів України") },
                new[] { "https://redcross.org.ua/en/", Lang.T("Товариство Червоного Хреста України (Ukrainian Red Cross)"), Lang.T("гуманітарна допомога") },
            };
            foreach (string[] link in links)
            {
                article.Blocks.Add(new WikiBlock { Kind = BlockKind.ListItem, Level = 1, Text = Link(link[0], link[1]) + " — " + link[2] });
            }
            article.Blocks.Add(new WikiBlock
            {
                Kind = BlockKind.Paragraph,
                Text = "There is no Russian version of this guide. If you want to help, support Ukraine's defenders and the people affected by the war.",
            });
            article.Blocks.Add(new WikiBlock { Kind = BlockKind.Notice, Text = Lang.T("Посилання відкриваються в браузері.") });
            return article;
        }

        private string ArticleFooter(WikiArticle article)
        {
            StringBuilder text = new StringBuilder();
            if (article.Categories.Length > 0)
            {
                text.Append(Lang.T("Категорії: "));
                for (int i = 0; i < article.Categories.Length; i++)
                {
                    if (i > 0)
                    {
                        text.Append(" · ");
                    }
                    text.Append(Link("Category:" + article.Categories[i], article.Categories[i]));
                }
                text.Append(WikiPack.Break);
            }
            string source = m_pack.MetaValue("source");
            if (source.Length > 0)
            {
                text.Append(Lang.T("Стаття з ")).Append(Link(source + article.Title.Replace(' ', '_'), "valheim.fandom.com")).Append(Lang.T(" (відкриється в браузері). "));
            }
            text.Append(Attribution());
            return text.ToString();
        }

        private string Attribution()
        {
            string license = m_pack != null ? m_pack.MetaValue("license") : "";
            string date = m_pack != null ? m_pack.MetaValue("date") : "";
            return Lang.F("Тексти й зображення — автори вікі Valheim, ліцензія {0}", (license.Length > 0 ? license : "CC BY-SA")) + (date.Length > 0 ? Lang.F("; знімок від {0}.", date) : ".");
        }

        // ---------------------------------------------------------------- drawing

        internal void Draw(Rect area)
        {
            WikiStyles.Ensure();
            WikiImages.Update();
            EnsurePacks();
            CheckUpdates();
            EnsureStyles();
            Event current = Event.current;
            if (current.type == EventType.Layout)
            {
                area = m_lastArea;
            }
            else
            {
                m_lastArea = area;
            }

            Rect back = new Rect(area.x, area.y, 84f, BarHeight);
            Rect forward = new Rect(back.xMax + 4f, area.y, 84f, BarHeight);
            Rect home = new Rect(forward.xMax + 4f, area.y, 90f, BarHeight);
            Rect language = new Rect(area.xMax - 180f, area.y, 180f, BarHeight);
            Rect search = new Rect(home.xMax + 8f, area.y, Mathf.Max(80f, language.x - 8f - home.xMax - 8f), BarHeight);
            Rect view = new Rect(area.x, area.y + BarHeight + 6f, area.width, Mathf.Max(40f, area.height - BarHeight - 6f));

            string searchKey = m_search + (m_pack != null && m_pack.Ready ? "+" : "-");
            if (current.type == EventType.Layout && m_searchFor != searchKey)
            {
                m_searchFor = searchKey;
                m_hits = m_pack != null && m_search.Trim().Length > 0 ? m_pack.Search(m_search, MaxHits) : new List<SearchHit>();
                m_hitIndex = 0;
            }
            bool hitsShown = m_showHits && m_search.Length > 0;
            Rect hitsRect = new Rect(search.x, search.yMax + 2f, Mathf.Max(search.width, 320f), Mathf.Max(1, m_hits.Count) * HitHeight + 6f);
            Rect languageRect = new Rect(language.xMax - 220f, language.yMax + 2f, 220f, (m_languages.Count + 1) * HitHeight + 6f);
            bool overPopup = (hitsShown && hitsRect.Contains(current.mousePosition)) || (m_langOpen && languageRect.Contains(current.mousePosition));

            // a click elsewhere closes the popups
            if (current.type == EventType.MouseDown && !overPopup)
            {
                if (!search.Contains(current.mousePosition))
                {
                    m_showHits = false;
                }
                if (!language.Contains(current.mousePosition))
                {
                    m_langOpen = false;
                }
            }
            if (current.type == EventType.MouseDown && (current.button == 3 || current.button == 4) && area.Contains(current.mousePosition))
            {
                Step(current.button == 3 ? -1 : 1);
                current.Use();
            }

            DrawPage(view, overPopup);

            GUI.enabled = m_historyIndex > 0;
            if (GUI.Button(back, Lang.T("‹ Назад")))
            {
                Step(-1);
            }
            GUI.enabled = m_historyIndex + 1 < m_history.Count;
            if (GUI.Button(forward, Lang.T("Вперед ›")))
            {
                Step(1);
            }
            GUI.enabled = true;
            if (GUI.Button(home, Lang.T("Головна")))
            {
                GoTo(Home, null, 0f, push: true);
            }
            DrawSearch(search, hitsShown);
            string languageName = m_pack != null ? m_pack.Name : "—";
            if (GUI.Button(language, Lang.T("Статті: ") + languageName + " ▾"))
            {
                m_langOpen = !m_langOpen;
                m_showHits = false;
            }
            if (hitsShown)
            {
                DrawHits(hitsRect);
            }
            if (m_langOpen)
            {
                DrawLanguages(languageRect);
            }
        }

        private void DrawSearch(Rect search, bool hitsShown)
        {
            Event current = Event.current;
            bool focused = GUI.GetNameOfFocusedControl() == SearchControl;
            if (focused && current.type == EventType.KeyDown)
            {
                switch (current.keyCode)
                {
                    case KeyCode.Return:
                    case KeyCode.KeypadEnter:
                        if (m_hits.Count > 0)
                        {
                            SearchHit hit = m_hits[Mathf.Clamp(m_hitIndex, 0, m_hits.Count - 1)];
                            GUIUtility.keyboardControl = 0;
                            Open(hit.Anchor != null ? hit.Title + "#" + hit.Anchor : hit.Title);
                        }
                        current.Use();
                        break;
                    case KeyCode.DownArrow:
                        m_hitIndex = Mathf.Min(m_hits.Count - 1, m_hitIndex + 1);
                        m_showHits = true;
                        current.Use();
                        break;
                    case KeyCode.UpArrow:
                        m_hitIndex = Mathf.Max(0, m_hitIndex - 1);
                        current.Use();
                        break;
                }
            }
            GUI.SetNextControlName(SearchControl);
            string text = GUI.TextField(search, m_search ?? "", 80);
            if (text != m_search)
            {
                m_search = text;
                m_showHits = text.Length > 0;
            }
            if (m_search.Length == 0 && !focused && current.type == EventType.Repaint)
            {
                GUI.Label(new Rect(search.x + 8f, search.y, search.width - 16f, search.height), Lang.T("Пошук статті (можна й назвою з гри українською)…"), m_placeholder);
            }
        }

        private void DrawHits(Rect rect)
        {
            GUI.color = new Color(0.07f, 0.06f, 0.05f, 0.98f);
            GUI.DrawTexture(rect, HelperSkin.White);
            GUI.color = Color.white;
            if (m_hits.Count == 0)
            {
                GUI.Label(new Rect(rect.x + 8f, rect.y + 3f, rect.width - 16f, HitHeight), m_pack != null && !m_pack.Ready ? Lang.T("Статті ще завантажуються…") : Lang.T("Нічого не знайдено."), m_placeholder);
                return;
            }
            for (int i = 0; i < m_hits.Count; i++)
            {
                SearchHit hit = m_hits[i];
                Rect row = new Rect(rect.x + 3f, rect.y + 3f + i * HitHeight, rect.width - 6f, HitHeight - 2f);
                string label = hit.Name == hit.Title ? hit.Title : hit.Name.Contains("→") ? hit.Name : $"{hit.Name}  →  {hit.Title}";
                if (i == m_hitIndex)
                {
                    GUI.color = new Color(WikiStyles.Accent.r, WikiStyles.Accent.g, WikiStyles.Accent.b, 0.35f);
                    GUI.DrawTexture(row, HelperSkin.White);
                    GUI.color = Color.white;
                }
                if (GUI.Button(row, label, m_rowButton))
                {
                    GUIUtility.keyboardControl = 0;
                    Open(hit.Anchor != null ? hit.Title + "#" + hit.Anchor : hit.Title);
                }
            }
        }

        private void DrawLanguages(Rect rect)
        {
            GUI.color = new Color(0.07f, 0.06f, 0.05f, 0.98f);
            GUI.DrawTexture(rect, HelperSkin.White);
            GUI.color = Color.white;
            float y = rect.y + 3f;
            foreach (string code in m_languages)
            {
                bool current = m_pack != null && m_pack.Language == code;
                if (GUI.Button(new Rect(rect.x + 3f, y, rect.width - 6f, HitHeight - 2f), (current ? "● " : "   ") + WikiPack.LanguageName(code), m_rowButton))
                {
                    m_langOpen = false;
                    if (!current)
                    {
                        SwitchLanguage(code);
                    }
                }
                y += HitHeight;
            }
            if (GUI.Button(new Rect(rect.x + 3f, y, rect.width - 6f, HitHeight - 2f), Lang.T("   Русский"), m_rowButton))
            {
                m_langOpen = false;
                GoTo(Donate, null, 0f, push: true);
            }
        }

        private void DrawPage(Rect view, bool overPopup)
        {
            Event current = Event.current;
            GUI.color = new Color(0.045f, 0.038f, 0.032f, 0.6f);
            if (current.type == EventType.Repaint)
            {
                GUI.DrawTexture(view, HelperSkin.White);
            }
            GUI.color = Color.white;
            EnsureArticle();
            float room = view.width - ScrollbarWidth - Pad * 2f;
            float width = Mathf.Max(200f, Mathf.Min(room, MaxReadingWidth));
            if (m_layout == null || m_layoutDirty || Mathf.Abs(width - m_layoutWidth) > 0.5f)
            {
                m_layout = WikiLayout.Build(m_article, width, LinkExists, m_tabs, m_footer);
                m_layoutWidth = width;
                m_layoutDirty = false;
            }
            float offsetX = Pad + Mathf.Max(0f, (room - width) / 2f);
            Rect content = new Rect(0f, 0f, view.width - ScrollbarWidth, m_layout.Height + Pad * 2f);
            if (m_anchor != null)
            {
                if (m_layout.Anchors.TryGetValue(m_anchor, out float y) || m_layout.Anchors.TryGetValue(m_anchor.Replace(' ', '_'), out y))
                {
                    m_scroll.y = Mathf.Max(0f, y + Pad - 8f);
                }
                m_anchor = null;
                m_pendingScroll = -1f;
            }
            else if (m_pendingScroll >= 0f)
            {
                m_scroll.y = m_pendingScroll;
                m_pendingScroll = -1f;
            }
            bool mouseInView = !overPopup && view.Contains(current.mousePosition);
            m_scroll = GUI.BeginScrollView(view, m_scroll, content, false, true);
            Vector2 mouse = current.mousePosition;
            float top = m_scroll.y - 60f;
            float bottom = m_scroll.y + view.height + 60f;
            string clicked = null;
            int clickedBlock = -1;
            int clickedTab = -1;
            bool repaint = current.type == EventType.Repaint;
            bool press = current.type == EventType.MouseDown && current.button == 0 && mouseInView;
            foreach (LayoutItem item in m_layout.Items)
            {
                Rect rect = item.Rect;
                rect.x += offsetX;
                rect.y += Pad;
                if (rect.yMax < top || rect.y > bottom)
                {
                    continue;
                }
                bool hover = mouseInView && rect.Contains(mouse);
                if (press && hover)
                {
                    if (item.Kind == ItemKind.Tab)
                    {
                        clickedBlock = item.Block;
                        clickedTab = item.Tab;
                    }
                    else if (item.Link != null)
                    {
                        clicked = item.Link;
                    }
                }
                if (!repaint)
                {
                    continue;
                }
                switch (item.Kind)
                {
                    case ItemKind.Fill:
                        GUI.color = item.Color;
                        GUI.DrawTexture(rect, HelperSkin.White);
                        break;
                    case ItemKind.Image:
                        Texture2D texture = WikiImages.Get(item.Text);
                        if (texture != null)
                        {
                            GUI.color = Color.white;
                            GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, true);
                        }
                        else if (rect.width > 40f && WikiImages.Has(item.Text))
                        {
                            GUI.color = new Color(1f, 1f, 1f, 0.05f);
                            GUI.DrawTexture(rect, HelperSkin.White);
                        }
                        break;
                    case ItemKind.Tab:
                        GUI.color = item.Selected ? new Color(WikiStyles.Accent.r, WikiStyles.Accent.g, WikiStyles.Accent.b, 0.5f)
                            : hover ? new Color(1f, 1f, 1f, 0.16f) : new Color(1f, 1f, 1f, 0.07f);
                        GUI.DrawTexture(rect, HelperSkin.White);
                        float labelWidth = WikiStyles.Width(item.Style, item.Text);
                        GUI.color = item.Selected ? WikiStyles.Bright : WikiStyles.Text;
                        GUI.Label(new Rect(rect.x + Mathf.Max(0f, (rect.width - labelWidth) / 2f), rect.y, Mathf.Min(labelWidth, rect.width), rect.height), item.Text, WikiStyles.Style(item.Style));
                        break;
                    default:
                        bool link = item.Link != null && hover;
                        GUI.color = link ? WikiStyles.LinkHover : item.Color;
                        GUI.Label(rect, item.Text, WikiStyles.Style(item.Style));
                        if (link)
                        {
                            float size = WikiStyles.FontSize(WikiStyles.SizeOf(item.Style));
                            GUI.DrawTexture(new Rect(rect.x, rect.y + rect.height / 2f + size * 0.55f, rect.width, 1f), HelperSkin.White);
                        }
                        break;
                }
            }
            GUI.color = Color.white;
            GUI.EndScrollView();
            if (clickedBlock >= 0)
            {
                m_tabs[clickedBlock] = clickedTab;
                m_layoutDirty = true;
                current.Use();
            }
            else if (clicked != null)
            {
                current.Use();
                Open(clicked);
            }
        }

        private void EnsureStyles()
        {
            if (m_rowButton != null && m_rowButton.font == GUI.skin.button.font)
            {
                return;
            }
            m_rowButton = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                richText = false,
                wordWrap = false,
                clipping = TextClipping.Clip,
                padding = new RectOffset(8, 8, 2, 2),
            };
            m_rowButton.normal.textColor = WikiStyles.Text;
            m_rowButton.hover.textColor = WikiStyles.Bright;
            m_rowButton.hover.background = HelperSkin.MakeTexture(new Color(1f, 1f, 1f, 0.1f));
            m_placeholder = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip, richText = false };
            m_placeholder.normal.textColor = WikiStyles.Dim;
        }
    }
}
