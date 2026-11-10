using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using ValheimHelper.Wiki;

// The in-game wiki: every article of the pack the wiki tool built parses, and its tables and infoboxes are sound.
internal static class WikiTests
{
    internal static void Run(Action<string, bool> check)
    {
        string pages = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "wiki-localizer", "data", "modpack", "en", "pages.txt");
        pages = Path.GetFullPath(pages);
        if (!File.Exists(pages))
        {
            Console.WriteLine("--   Вікі: пакета статей немає (" + pages + "), перевірку пропущено");
            return;
        }
        string text = File.ReadAllText(pages, Encoding.UTF8);
        Stopwatch watch = Stopwatch.StartNew();
        int articles = 0;
        int blocks = 0;
        int tables = 0;
        int badTables = 0;
        int infoboxes = 0;
        int emptyTabs = 0;
        int links = 0;
        int brokenLinks = 0;
        long slowestTicks = 0;
        string slowest = "";
        int start = text.IndexOf("=== ", StringComparison.Ordinal);
        while (start >= 0)
        {
            int titleEnd = text.IndexOf('\n', start);
            string title = text.Substring(start + 4, titleEnd - start - 4).TrimEnd('\r');
            int next = text.IndexOf("\n=== ", titleEnd, StringComparison.Ordinal);
            string body = text.Substring(titleEnd + 1, (next < 0 ? text.Length : next) - titleEnd - 1);
            long before = watch.ElapsedTicks;
            WikiArticle article = WikiParser.Parse(title, body);
            long took = watch.ElapsedTicks - before;
            if (took > slowestTicks)
            {
                slowestTicks = took;
                slowest = title;
            }
            articles++;
            blocks += article.Blocks.Count;
            foreach (WikiBlock block in article.Blocks)
            {
                if (block.Kind == BlockKind.Table)
                {
                    tables++;
                    foreach (TableCell cell in block.Cells)
                    {
                        if (cell.Col + cell.ColSpan > block.Cols || cell.Row + cell.RowSpan > block.Rows)
                        {
                            badTables++;
                            break;
                        }
                    }
                }
                else if (block.Kind == BlockKind.Infobox)
                {
                    infoboxes++;
                    foreach (InfoTab tab in block.Tabs)
                    {
                        if (tab.Items.Count == 0)
                        {
                            emptyTabs++;
                        }
                    }
                }
                CountLinks(block.Text, ref links, ref brokenLinks);
            }
            start = next < 0 ? -1 : next + 1;
        }
        watch.Stop();
        Console.WriteLine($"--   Вікі: статей {articles}, блоків {blocks}, таблиць {tables}, інфобоксів {infoboxes}, посилань {links}; " +
            $"усе за {watch.ElapsedMilliseconds} мс, найдовша «{slowest}» {slowestTicks * 1000.0 / Stopwatch.Frequency:0.0} мс");
        check("Вікі: пакет містить статті", articles > 900);
        check("Вікі: таблиці без клітинок поза сіткою", badTables == 0);
        check("Вікі: вкладки інфобоксів не порожні", emptyTabs == 0);
        check("Вікі: посилання цілі (початок, середина, кінець)", brokenLinks == 0);
        check("Вікі: стаття розбирається швидко (межа 20 мс)", slowestTicks * 1000.0 / Stopwatch.Frequency < 20.0);
    }

    private static void CountLinks(string text, ref int links, ref int broken)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        int depth = 0;
        bool mid = false;
        foreach (char c in text)
        {
            switch (c)
            {
                case '':
                    if (depth > 0)
                    {
                        broken++;
                    }
                    depth = 1;
                    mid = false;
                    break;
                case '':
                    mid = depth == 1;
                    break;
                case '':
                    if (depth == 1 && mid)
                    {
                        links++;
                    }
                    else
                    {
                        broken++;
                    }
                    depth = 0;
                    mid = false;
                    break;
            }
        }
        if (depth != 0)
        {
            broken++;
        }
    }
}
