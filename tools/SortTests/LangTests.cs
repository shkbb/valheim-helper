using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ValheimHelper;

// The English texts of the menu: every translation keeps the values the Ukrainian text puts in, and formats.
internal static class LangTests
{
    internal static void Run(Action<string, bool> check)
    {
        Dictionary<string, string> table = LangEn.Table();
        Regex hole = new Regex(@"\{(\d+)(?:[,:][^}]*)?\}");
        int broken = 0;
        foreach (KeyValuePair<string, string> pair in table)
        {
            int count = 0;
            foreach (Match match in hole.Matches(pair.Key))
            {
                count = Math.Max(count, int.Parse(match.Groups[1].Value) + 1);
            }
            object[] args = new object[count];
            for (int i = 0; i < count; i++)
            {
                args[i] = 1.5f;
            }
            try
            {
                string.Format(pair.Value, args);
                string.Format(pair.Key, args);
            }
            catch (FormatException)
            {
                broken++;
                Console.WriteLine("     не форматується: " + pair.Key);
            }
        }
        Console.WriteLine($"--   Мова: англійських текстів {table.Count}");
        check("Мова: англійські тексти форматуються так само, як українські", broken == 0);
        Lang.Chosen = Lang.English;
        Lang.Start("Ukrainian");
        bool english = Lang.T("Склад") == "Storage" && Lang.F("Радіус складу: {0:0} м", 15f) == "Storage radius: 15 m";
        Lang.Chosen = "";
        Lang.Start("Ukrainian");
        bool ukrainian = Lang.T("Склад") == "Склад";
        check("Мова: вибрана англійська перекладає, «як у грі» з українською грою — ні", english && ukrainian);
    }
}
