using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace ValheimHelper.Wiki
{
    // Names of the game (items, creatures, biomes, buildings, effects…) in the language the game is set to, with the
    // English name of the same thing: the wiki's articles are named in English, so "мідь" finds Copper. Read from the
    // game's own localization table once, on a background thread.
    internal static class GameNames
    {
        internal struct Name
        {
            public string Key;
            public string Local;
            public string English;
        }

        private static readonly string[] s_prefixes =
        {
            "item_", "enemy_", "biome_", "piece_", "se_", "location_", "npc_", "skill_", "event_", "raven_",
        };

        private static volatile List<Name> s_names = new List<Name>();
        private static bool s_started;

        internal static List<Name> Pairs
        {
            get
            {
                Start();
                return s_names;
            }
        }

        private static void Start()
        {
            if (s_started || Localization.instance == null)
            {
                return;
            }
            s_started = true;
            try
            {
                string language = Localization.instance.GetSelectedLanguage();
                if (language == "English")
                {
                    return;
                }
                LocalizationSettings settings = Resources.Load<LocalizationSettings>("LocalizationSettings");
                if (settings == null || settings.Localizations == null)
                {
                    return;
                }
                List<string> texts = new List<string>();
                foreach (TextAsset asset in settings.Localizations)
                {
                    if (asset != null)
                    {
                        texts.Add(asset.text);
                    }
                }
                Thread thread = new Thread(() => Build(texts, language)) { IsBackground = true, Name = "VH game names" };
                thread.Start();
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private static void Build(List<string> texts, string language)
        {
            try
            {
                List<Name> names = new List<Name>();
                HashSet<string> seen = new HashSet<string>();
                foreach (string text in texts)
                {
                    List<List<string>> rows = Csv(text);
                    if (rows.Count == 0)
                    {
                        continue;
                    }
                    int english = rows[0].IndexOf("English");
                    int local = rows[0].IndexOf(language);
                    if (english < 0 || local < 0)
                    {
                        continue;
                    }
                    for (int i = 1; i < rows.Count; i++)
                    {
                        List<string> row = rows[i];
                        if (row.Count <= Math.Max(english, local) || !Wanted(row[0]) || row[0].EndsWith("_description", StringComparison.Ordinal))
                        {
                            continue;
                        }
                        string en = row[english].Trim();
                        string loc = row[local].Trim();
                        if (en.Length == 0 || loc.Length == 0 || en.Length > 40 || loc.Length > 40 || en.Contains("$") || !seen.Add(loc + "|" + en))
                        {
                            continue;
                        }
                        string key = WikiPack.Normalize(loc);
                        if (key.Length > 0 && key != WikiPack.Normalize(en))
                        {
                            names.Add(new Name { Key = key, Local = loc, English = en });
                        }
                    }
                }
                s_names = names;
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private static bool Wanted(string key)
        {
            foreach (string prefix in s_prefixes)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // The game's CSV: commas, quoted fields with doubled quotes and line breaks inside.
        private static List<List<string>> Csv(string text)
        {
            List<List<string>> rows = new List<List<string>>();
            List<string> row = new List<string>();
            System.Text.StringBuilder field = new System.Text.StringBuilder();
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            quoted = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                    }
                    continue;
                }
                switch (c)
                {
                    case '"':
                        quoted = true;
                        break;
                    case ',':
                        row.Add(field.ToString());
                        field.Length = 0;
                        break;
                    case '\r':
                        break;
                    case '\n':
                        row.Add(field.ToString());
                        field.Length = 0;
                        rows.Add(row);
                        row = new List<string>();
                        break;
                    default:
                        field.Append(c);
                        break;
                }
            }
            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                rows.Add(row);
            }
            return rows;
        }
    }
}
