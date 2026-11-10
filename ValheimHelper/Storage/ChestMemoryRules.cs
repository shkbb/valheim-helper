using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimHelper.Storage
{
    // What a chest remembers: the items the player put into it by hand, each with the world time of the latest such
    // move. Stored in the chest's ZDO as "name|time;name|time", so everyone with the mod shares it. Pure rules without
    // Unity types, so tools/SortTests can check them.
    internal static class ChestMemoryRules
    {
        // A chest holds at most 32 kinds of items; a few more leave room for recent history.
        internal const int MaxEntries = 40;

        internal static Dictionary<string, long> Parse(string text)
        {
            Dictionary<string, long> entries = new Dictionary<string, long>();
            if (string.IsNullOrEmpty(text))
            {
                return entries;
            }
            foreach (string part in text.Split(';'))
            {
                int bar = part.LastIndexOf('|');
                if (bar <= 0 || !long.TryParse(part.Substring(bar + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long time))
                {
                    continue;
                }
                string name = part.Substring(0, bar);
                if (!entries.TryGetValue(name, out long known) || time > known)
                {
                    entries[name] = time;
                }
            }
            return entries;
        }

        internal static string Format(Dictionary<string, long> entries)
        {
            List<KeyValuePair<string, long>> sorted = new List<KeyValuePair<string, long>>(entries);
            sorted.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
            StringBuilder text = new StringBuilder();
            foreach (KeyValuePair<string, long> entry in sorted)
            {
                if (text.Length > 0)
                {
                    text.Append(';');
                }
                text.Append(entry.Key).Append('|').Append(entry.Value.ToString(CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        // Adds the names with this time (a later time only), then drops the oldest entries beyond MaxEntries.
        // Returns whether anything changed.
        internal static bool Remember(Dictionary<string, long> entries, IEnumerable<string> names, long time)
        {
            bool changed = false;
            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name) || name.IndexOf(';') >= 0 || name.IndexOf('|') >= 0)
                {
                    continue;
                }
                if (!entries.TryGetValue(name, out long known) || time > known)
                {
                    entries[name] = time;
                    changed = true;
                }
            }
            while (entries.Count > MaxEntries)
            {
                string oldest = null;
                long oldestTime = long.MaxValue;
                foreach (KeyValuePair<string, long> entry in entries)
                {
                    if (entry.Value < oldestTime || (entry.Value == oldestTime && string.CompareOrdinal(entry.Key, oldest) > 0))
                    {
                        oldest = entry.Key;
                        oldestTime = entry.Value;
                    }
                }
                entries.Remove(oldest);
                changed = true;
            }
            return changed;
        }

        // Index of the chest where the player put the item by hand most recently, or -1. On a tie the earlier chest
        // in the list wins (the sorter lists chests nearest first).
        internal static int Latest(IList<Dictionary<string, long>> chests, string name)
        {
            int best = -1;
            long bestTime = long.MinValue;
            for (int i = 0; i < chests.Count; i++)
            {
                if (chests[i] != null && chests[i].TryGetValue(name, out long time) && time > bestTime)
                {
                    best = i;
                    bestTime = time;
                }
            }
            return best;
        }
    }
}
