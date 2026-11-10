using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace ValheimHelper.Storage
{
    // Sorting preferences, kept between game launches in ValheimHelper/storage_settings.txt.
    internal static class StorageSettings
    {
        internal const float DefaultRadius = 15f;
        internal const float MinRadius = 3f;
        // One radius for everything "near": putting away, furnaces, crafting and building from chests, and the storage
        // seen from afar around its centre.
        internal const float MaxRadius = 60f;

        internal static float Radius = DefaultRadius;
        internal static bool ShowChestTags = true;
        // Chests remember what the player puts into them by hand ("пам'ять сундука").
        internal static bool RememberHandPlacement = true;
        // Items no chest takes yet, and the overflow of full chests, are laid out over empty chests.
        internal static bool UseEmptyChests = true;

        internal const float DefaultFireRadius = 60f;
        internal const float MinFireRadius = 10f;
        internal const float MaxFireRadius = 150f;
        internal static float FireRadius = DefaultFireRadius;

        internal static bool UseChestsForCrafting = true;
        internal static bool UseChestsForBuilding = true;
        // The amount picker next to the game's craft button (CraftAmount).
        internal static bool CraftAmountPanel = true;
        // Prefab names of items the mod never takes from the inventory ("не чіпати").
        internal static readonly HashSet<string> KeptItems = new HashSet<string>();

        // Which stations "Завантажити печі" fills: every kind of station of the game can be switched on and off by
        // itself (its prefab). What is not listed here is filled.
        internal static readonly HashSet<string> StationsOff = new HashSet<string>();
        // Kept for worlds set up by an older version: they become the switches of the single stations.
        internal static bool StationsMigrated;
        internal static bool LoadSmelters = true;
        // What the stations make goes into a chest nearby instead of falling out (SmelterOutput). Off by default.
        internal static bool SmelterToChests;
        internal static bool LoadKilns = true;
        // Off by default: barley and flax are also what gets planted.
        internal static bool LoadOtherStations;

        private static bool s_dirty;
        private static float s_saveAt;

        private static string FilePath => Path.Combine(Loader.Dir ?? ".", "storage_settings.txt");

        internal static bool IsStationOn(string prefab)
        {
            return prefab != null && !StationsOff.Contains(prefab);
        }

        internal static void SetStationOn(string prefab, bool on)
        {
            if (prefab != null && (on ? StationsOff.Remove(prefab) : StationsOff.Add(prefab)))
            {
                Changed();
            }
        }

        internal static bool IsItemKept(string prefab)
        {
            return prefab != null && KeptItems.Contains(prefab);
        }

        internal static void SetItemKept(string prefab, bool kept)
        {
            if (prefab != null && (kept ? KeptItems.Add(prefab) : KeptItems.Remove(prefab)))
            {
                Changed();
            }
        }

        internal static void Changed()
        {
            s_dirty = true;
            s_saveAt = Time.unscaledTime + 1f;
        }

        internal static void SaveIfDirty()
        {
            if (s_dirty && Time.unscaledTime >= s_saveAt)
            {
                Save();
            }
        }

        internal static void Flush()
        {
            if (s_dirty)
            {
                Save();
            }
        }

        internal static void Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return;
                }
                float oldCraftRadius = 0f;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        continue;
                    }
                    string key = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).Trim();
                    switch (key)
                    {
                        case "radius":
                            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float radius))
                            {
                                Radius = Mathf.Clamp(radius, MinRadius, MaxRadius);
                            }
                            break;
                        case "tags": ShowChestTags = value == "1"; break;
                        case "memory": RememberHandPlacement = value == "1"; break;
                        case "emptychests": UseEmptyChests = value == "1"; break;
                        case "fireradius":
                            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float fireRadius))
                            {
                                FireRadius = Mathf.Clamp(fireRadius, MinFireRadius, MaxFireRadius);
                            }
                            break;
                        case "stationsmigrated": StationsMigrated = value == "1"; break;
                        case "stationsoff":
                            StationsOff.Clear();
                            foreach (string part in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                StationsOff.Add(part.Trim());
                            }
                            break;
                        case "keepitems":
                            KeptItems.Clear();
                            foreach (string part in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                KeptItems.Add(part.Trim());
                            }
                            break;
                        case "craftchests": UseChestsForCrafting = value == "1"; break;
                        case "buildchests": UseChestsForBuilding = value == "1"; break;
                        case "craftamount": CraftAmountPanel = value == "1"; break;
                        case "craftradius":
                            // older versions had a separate radius for crafting: the one radius keeps the larger
                            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out oldCraftRadius);
                            break;
                        case "smelters": LoadSmelters = value == "1"; break;
                        case "smelteroutput": SmelterToChests = value == "1"; break;
                        case "kilns": LoadKilns = value == "1"; break;
                        case "otherstations": LoadOtherStations = value == "1"; break;
                    }
                }
                if (oldCraftRadius > Radius)
                {
                    Radius = Mathf.Clamp(oldCraftRadius, MinRadius, MaxRadius);
                    s_dirty = true;
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private static void Save()
        {
            s_dirty = false;
            try
            {
                File.WriteAllLines(FilePath, new[]
                {
                    "radius=" + Radius.ToString(CultureInfo.InvariantCulture),
                    "tags=" + Bit(ShowChestTags),
                    "memory=" + Bit(RememberHandPlacement),
                    "emptychests=" + Bit(UseEmptyChests),
                    "fireradius=" + FireRadius.ToString(CultureInfo.InvariantCulture),
                    "craftchests=" + Bit(UseChestsForCrafting),
                    "buildchests=" + Bit(UseChestsForBuilding),
                    "craftamount=" + Bit(CraftAmountPanel),
                    "keepitems=" + string.Join(",", new List<string>(KeptItems).ToArray()),
                    "stationsoff=" + string.Join(",", new List<string>(StationsOff).ToArray()),
                    "stationsmigrated=" + Bit(StationsMigrated),
                    "smelters=" + Bit(LoadSmelters),
                    "smelteroutput=" + Bit(SmelterToChests),
                    "kilns=" + Bit(LoadKilns),
                    "otherstations=" + Bit(LoadOtherStations),
                });
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private static string Bit(bool value)
        {
            return value ? "1" : "0";
        }
    }
}
