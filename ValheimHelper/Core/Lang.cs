using System;
using System.Collections.Generic;

namespace ValheimHelper
{
    // The language of the mod's own texts. Every text of the menu and the map is written in Ukrainian in the code and
    // goes through T (or F, with values put in): in English the table of LangEn gives the translation, and a text
    // without one stays Ukrainian. The language is read at start, so a change applies after restarting the game.
    internal static class Lang
    {
        internal const string Ukrainian = "uk";
        internal const string English = "en";

        // What the menu shows now (fixed for the session) and what was chosen for the next start.
        internal static string Code = Ukrainian;
        internal static string Chosen = "";

        private static Dictionary<string, string> s_table;

        internal static bool IsEnglish => Code == English;

        // The language of the session: the one chosen in the menu, or else the game's own (Ukrainian when the game
        // is in Ukrainian, English for any other language).
        internal static void Start(string gameLanguage)
        {
            if (Chosen == Ukrainian || Chosen == English)
            {
                Code = Chosen;
            }
            else
            {
                Code = gameLanguage == "Ukrainian" ? Ukrainian : English;
            }
            s_table = Code == English ? LangEn.Table() : null;
        }

        internal static string T(string text)
        {
            if (s_table == null || text == null)
            {
                return text;
            }
            return s_table.TryGetValue(text, out string translated) ? translated : text;
        }

        internal static string F(string format, params object[] args)
        {
            string translated = T(format);
            try
            {
                return string.Format(translated, args);
            }
            catch (FormatException)
            {
                return string.Format(format, args);
            }
        }
    }
}
