using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ValheimHelper.Wiki;

namespace SortTests
{
    // The mod's own converter (WikiConvert) against the one the pack was built with: every cached page of the wiki
    // is converted here and compared with the lines in pages.txt. When they match, the guide can bring in changed
    // articles by itself, without a new build of the mod.
    internal static class ConvertTests
    {
        internal static bool Run(string packRoot, int maxReported = 6)
        {
            if (!Directory.Exists(packRoot))
            {
                Console.WriteLine($"--   Конвертер вікі: пакета немає ({packRoot}), пропущено");
                return true;
            }
            bool ok = true;
            // every language of the pack, so a wiki in a new language is checked by itself
            List<string> languages = new List<string>();
            foreach (string path in Directory.GetDirectories(packRoot))
            {
                string name = Path.GetFileName(path);
                if (name != "img")
                {
                    languages.Add(name);
                }
            }
            languages.Sort(StringComparer.Ordinal);
            foreach (string lang in languages)
            {
                string dir = Path.Combine(packRoot, lang);
                string pages = Path.Combine(dir, "pages.txt");
                string htmlDir = Path.Combine(dir, "html");
                if (!File.Exists(pages) || !Directory.Exists(htmlDir))
                {
                    continue;
                }
                ok &= Compare(lang, pages, htmlDir, maxReported);
            }
            return ok;
        }

        private static bool Compare(string lang, string pagesPath, string htmlDir, int maxReported)
        {
            Dictionary<string, string> expected = ReadPages(pagesPath);
            int checkedPages = 0;
            int same = 0;
            int reported = 0;
            List<string> problems = new List<string>();
            foreach (string file in Directory.GetFiles(htmlDir, "*.json"))
            {
                JsonDocument json;
                try
                {
                    json = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
                }
                catch (JsonException)
                {
                    continue;
                }
                using (json)
                {
                    string title = json.RootElement.GetProperty("title").GetString();
                    string html = json.RootElement.GetProperty("html").GetString();
                    if (title == null || html == null || !expected.TryGetValue(title, out string want))
                    {
                        continue;
                    }
                    checkedPages++;
                    string got = string.Join("\n", WikiConvert.Convert(html, lang).Lines);
                    if (got == want)
                    {
                        same++;
                        continue;
                    }
                    if (reported < maxReported)
                    {
                        reported++;
                        problems.Add($"     «{title}»: {FirstDifference(want, got)}");
                    }
                }
            }
            if (checkedPages == 0)
            {
                return true;
            }
            float percent = 100f * same / checkedPages;
            string mark = same == checkedPages ? "OK  " : "FAIL";
            Console.WriteLine($"{mark} Конвертер вікі ({lang}): {same} з {checkedPages} сторінок збігаються з пакетом ({percent:0.0}%)");
            foreach (string problem in problems)
            {
                Console.WriteLine(problem);
            }
            return same == checkedPages;
        }

        private static string FirstDifference(string want, string got)
        {
            string[] a = want.Split('\n');
            string[] b = got.Split('\n');
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                string left = i < a.Length ? a[i] : "<немає>";
                string right = i < b.Length ? b[i] : "<немає>";
                if (left != right)
                {
                    return $"рядок {i + 1}\n       пакет: {Show(left)}\n       мод:   {Show(right)}";
                }
            }
            return "різниця в кінці";
        }

        private static string Show(string line)
        {
            StringBuilder text = new StringBuilder();
            foreach (char c in line.Length > 160 ? line.Substring(0, 160) + "…" : line)
            {
                text.Append(c < ' ' ? '·' : c);
            }
            return text.ToString();
        }

        // pages.txt: "=== Title", "rev …", "cat …", then the article's lines until the next title.
        private static Dictionary<string, string> ReadPages(string path)
        {
            Dictionary<string, string> pages = new Dictionary<string, string>();
            string title = null;
            List<string> lines = new List<string>();
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (line.StartsWith("=== ", StringComparison.Ordinal))
                {
                    if (title != null)
                    {
                        pages[title] = string.Join("\n", lines);
                    }
                    title = line.Substring(4);
                    lines.Clear();
                    continue;
                }
                if (title == null || line.StartsWith("rev ", StringComparison.Ordinal) || line.StartsWith("cat ", StringComparison.Ordinal))
                {
                    continue;
                }
                lines.Add(line);
            }
            if (title != null)
            {
                pages[title] = string.Join("\n", lines);
            }
            return pages;
        }
    }
}
