using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ValheimHelper
{
    // The language of the mod's own texts. Every text of the menu and the map is written in Ukrainian in the code and
    // goes through T (or F, with values put in). English is built in (LangEn); the other languages are files beside the
    // mod (lang/<code>.json: Ukrainian text → translation, made by tools/l10n). A text the language has no translation for
    // is shown in English, and one English lacks stays Ukrainian. There is no Russian, by the author's decision.
    // The language is read at start, so a change applies after restarting the game.
    internal static class Lang
    {
        internal const string Ukrainian = "uk";
        internal const string English = "en";

        // Codes of the menu languages and their own names, in the order of the language page.
        internal static readonly string[] Codes = { "uk", "en", "de", "fr", "es", "pl", "pt-br", "zh", "cs" };
        internal static readonly string[] Names = { "Українська", "English", "Deutsch", "Français", "Español", "Polski", "Português (Brasil)", "简体中文", "Čeština" };

        // What the menu shows now (fixed for the session) and what was chosen for the next start.
        internal static string Code = Ukrainian;
        internal static string Chosen = "";

        private static Dictionary<string, string> s_table;
        private static Dictionary<string, string> s_english;

        internal static bool IsEnglish => Code == English;

        // The game's language name (Localization.GetSelectedLanguage) → the menu language; English for the rest.
        internal static string FromGame(string gameLanguage)
        {
            switch (gameLanguage)
            {
                case "Ukrainian": return Ukrainian;
                case "German": return "de";
                case "French": return "fr";
                case "Spanish": return "es";
                case "Polish": return "pl";
                case "Portuguese_Brazilian": return "pt-br";
                case "Chinese": return "zh";
                case "Czech": return "cs";
                default: return English;
            }
        }

        // The language a start would use: the one chosen in the menu, or else the game's own.
        internal static string Resolve(string chosen, string gameLanguage)
        {
            return Array.IndexOf(Codes, chosen) >= 0 ? chosen : FromGame(gameLanguage);
        }

        internal static void Start(string gameLanguage)
        {
            Code = Resolve(Chosen, gameLanguage);
            s_english = Code == Ukrainian ? null : LangEn.Table();
            s_table = Code == Ukrainian || Code == English ? null : Load(Code);
            if (s_table == null && Code != Ukrainian && Code != English)
            {
                Code = English;
            }
        }

        private static Dictionary<string, string> Load(string code)
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(Lang).Assembly.Location) ?? "";
                string file = Path.Combine(Path.Combine(dir, "lang"), code + ".json");
                if (!File.Exists(file))
                {
                    return null;
                }
                Dictionary<string, string> table = new Dictionary<string, string>();
                foreach (KeyValuePair<string, JToken> pair in JObject.Parse(File.ReadAllText(file)))
                {
                    table[pair.Key] = pair.Value.ToString();
                }
                return table;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static string T(string text)
        {
            if (text == null || s_english == null)
            {
                return text;
            }
            if (s_table != null && s_table.TryGetValue(text, out string own))
            {
                return own;
            }
            return s_english.TryGetValue(text, out string translated) ? translated : text;
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
