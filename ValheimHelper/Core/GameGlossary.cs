using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper
{
    // Exports the game's own translations (English and Ukrainian) of every localization key into
    // ValheimHelper/wiki/glossary_game.tsv, so the wiki translation uses the official Ukrainian names the players see.
    // Only the game's localization files are read; nothing in the game changes.
    internal static class GameGlossary
    {
        private static readonly FieldInfo s_settings = AccessTools.Field(typeof(Localization), "m_localizationSettings");
        private static readonly MethodInfo s_split = AccessTools.Method(typeof(Localization), "DoQuoteLineSplit");

        internal static string FilePath => Path.Combine(Path.Combine(Loader.Dir ?? ".", "wiki"), "glossary_game.tsv");

        internal static string Export()
        {
            try
            {
                LocalizationSettings settings = s_settings?.GetValue(null) as LocalizationSettings;
                if (settings == null || settings.Localizations == null || s_split == null || Localization.instance == null)
                {
                    return Lang.T("Локалізація гри ще не завантажена.");
                }
                // Later files override earlier ones, as in the game.
                Dictionary<string, string[]> rows = new Dictionary<string, string[]>();
                List<string> order = new List<string>();
                foreach (TextAsset file in settings.Localizations)
                {
                    if (file == null)
                    {
                        continue;
                    }
                    StringReader reader = new StringReader(file.text);
                    string[] header = reader.ReadLine().Split(',');
                    int english = Column(header, "English");
                    int ukrainian = Column(header, "Ukrainian");
                    if (english < 0)
                    {
                        continue;
                    }
                    List<List<string>> lines = (List<List<string>>)s_split.Invoke(Localization.instance, new object[] { reader });
                    foreach (List<string> line in lines)
                    {
                        if (line.Count <= english || line[0].StartsWith("//") || line[0].Trim().Length == 0)
                        {
                            continue;
                        }
                        string key = line[0].Trim();
                        string en = Clean(line[english]);
                        string uk = ukrainian >= 0 && line.Count > ukrainian ? Clean(line[ukrainian]) : "";
                        if (!rows.ContainsKey(key))
                        {
                            order.Add(key);
                        }
                        rows[key] = new[] { en, uk };
                    }
                }
                StringBuilder text = new StringBuilder("key\tcategory\tenglish\tukrainian\n");
                int translated = 0;
                foreach (string key in order)
                {
                    string[] row = rows[key];
                    translated += row[1].Length > 0 ? 1 : 0;
                    text.Append(key).Append('\t').Append(CategoryOf(key)).Append('\t').Append(row[0]).Append('\t').Append(row[1]).Append('\n');
                }
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, text.ToString(), new UTF8Encoding(false));
                Log.Info($"Game glossary exported: {order.Count} keys, {translated} with Ukrainian");
                return Lang.F("Глосарій гри збережено: {0} ключів, українською {1}. Файл: {2}", order.Count, translated, FilePath);
            }
            catch (Exception e)
            {
                Log.Error(e);
                return Lang.T("Не вдалося вивантажити глосарій, деталі у ValheimHelper.log");
            }
        }

        private static int Column(string[] header, string language)
        {
            for (int i = 0; i < header.Length; i++)
            {
                if (header[i].Trim().Trim('"') == language)
                {
                    return i;
                }
            }
            return -1;
        }

        // One line per key: tabs and line breaks inside a text would break the table.
        private static string Clean(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length > 0 && value[0] == '\r')
            {
                return "";
            }
            return value.Replace("\t", " ").Replace("\r", "").Replace("\n", "\\n");
        }

        // The prefix of a key tells what it names: item_, enemy_, piece_, biome_, se_, location_…
        private static string CategoryOf(string key)
        {
            int underscore = key.IndexOf('_');
            return underscore > 0 ? key.Substring(0, underscore) : "";
        }
    }
}
