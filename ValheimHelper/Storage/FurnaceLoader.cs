using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper.Storage
{
    // "Завантажити печі поруч": fills every station around that turns one thing into another (smelters, blast furnaces,
    // charcoal and frigid kilns, the frost foundry, spinning wheels, windmills, eitr refineries…) with what it takes and
    // its fuel, from the inventory. Every piece goes in exactly like pressing the station's switch: the item is removed
    // from the inventory and the station's own RPC (RPC_AddOre / RPC_AddItem / RPC_AddFuel) is sent to its owner, one
    // piece per frame per station, so it works with friends without the mod as well. Which kind a station is follows
    // from the game's data, not from names, so stations of new biomes are found too.
    internal static class FurnaceLoader
    {
        private static readonly AccessTools.FieldRef<Smelter, ZNetView> s_smelterView = AccessTools.FieldRefAccess<Smelter, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<CookingStation, ZNetView> s_cookingView = AccessTools.FieldRefAccess<CookingStation, ZNetView>("m_nview");

        internal enum Kind
        {
            // melts or casts with a fuel that a kiln makes (smelter, blast furnace, frost foundry)
            Smelter,
            // makes a fuel for other stations (charcoal kiln, frigid kiln)
            Kiln,
            // cooks what is put in its slots (grill, oven…)
            Food,
            // everything else (spinning wheel, windmill, eitr refinery…)
            Other,
        }

        // A kind of station of the game, for the switches: its own name, what it takes and what it makes.
        internal sealed class StationKind
        {
            public string Prefab;
            public string Name;
            public Kind Kind;
            public string Takes;
            public string Makes;
        }

        private sealed class Station
        {
            public Component Source;
            public string Prefab;
            // a station that holds items in slots (the frost foundry, a grill), not an ore queue
            public bool Slots;
            public string NameKey;
            public ZNetView View;
            public Kind Kind;
            public bool IsEnabled;
            public float Distance;
            public int Queue;
            public int MaxQueue;
            public float Fuel;
            public int MaxFuel;
            public float FuelPerProduct;
            public int PlannedOre;
            public int PlannedFuel;
            public readonly HashSet<string> Accepts = new HashSet<string>();
            public string FuelPrefab;

            public int OreRoom => MaxQueue - Queue - PlannedOre;

            // The station's own "full" check is fuel > max - 1.
            public int FuelRoom => MaxFuel <= 0 || FuelPrefab == null ? 0 : Mathf.Max(0, Mathf.FloorToInt(MaxFuel - Fuel + 0.001f)) - PlannedFuel;

            // The frigid kiln has no ore at all: what it burns is what it works with, so it is filled to the top.
            public bool FuelIsTheOre;

            public int FuelNeeded => FuelIsTheOre
                ? FuelRoom
                : Mathf.Max(0, Mathf.CeilToInt((Queue + PlannedOre) * FuelPerProduct - Fuel - PlannedFuel - 0.001f));
        }

        private sealed class Delivery
        {
            public Station Station;
            public string Prefab;
            public bool IsFuel;
            public int Left;
        }

        private static readonly List<Delivery> s_deliveries = new List<Delivery>();
        private static readonly Dictionary<string, int> s_loaded = new Dictionary<string, int>();
        private static readonly HashSet<Component> s_touched = new HashSet<Component>();
        private static bool s_running;
        // One rule for everything: the inventory first, then the chests around, for every kind of station switched
        // on. A station never takes more than fits in it, and of several materials it takes the one there is most of.
        private static readonly HashSet<string> s_fromChestsAllowed = new HashSet<string>();
        private static readonly List<Container> s_chests = new List<Container>();
        private static int s_takenFromChests;

        // Fuels of the game's stations and those a station makes, read from the prefabs once per scene.
        private static ZNetScene s_fuelScene;
        private static readonly HashSet<string> s_fuels = new HashSet<string>();
        private static readonly HashSet<string> s_madeFuels = new HashSet<string>();
        private static readonly Dictionary<Kind, string> s_kindNames = new Dictionary<Kind, string>();

        internal static bool Busy => s_running;
        internal static string LastReport = "";

        private static void EnsureFuels()
        {
            ZNetScene scene = ZNetScene.instance;
            if (scene == null || ReferenceEquals(scene, s_fuelScene))
            {
                return;
            }
            s_fuelScene = scene;
            s_fuels.Clear();
            s_madeFuels.Clear();
            s_kindNames.Clear();
            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (prefab == null)
                {
                    continue;
                }
                Smelter smelter = prefab.GetComponentInChildren<Smelter>(true);
                if (smelter != null && smelter.m_maxFuel > 0 && smelter.m_fuelItem != null)
                {
                    s_fuels.Add(smelter.m_fuelItem.name);
                }
                CookingStation cooking = prefab.GetComponentInChildren<CookingStation>(true);
                if (cooking != null && cooking.m_useFuel && cooking.m_fuelItem != null)
                {
                    s_fuels.Add(cooking.m_fuelItem.name);
                }
            }
            foreach (GameObject prefab in scene.m_prefabs)
            {
                Smelter smelter = prefab != null ? prefab.GetComponentInChildren<Smelter>(true) : null;
                if (smelter == null)
                {
                    continue;
                }
                foreach (Smelter.ItemConversion conversion in smelter.m_conversion)
                {
                    if (conversion?.m_to != null && s_fuels.Contains(conversion.m_to.name))
                    {
                        s_madeFuels.Add(conversion.m_to.name);
                    }
                }
            }
        }

        private static Kind KindOf(IEnumerable<string> outputs, string fuel)
        {
            EnsureFuels();
            foreach (string output in outputs)
            {
                if (s_fuels.Contains(output))
                {
                    return Kind.Kiln;
                }
            }
            return fuel != null && s_madeFuels.Contains(fuel) ? Kind.Smelter : Kind.Other;
        }

        // The game's names of the stations of a kind ("Плавильня, Доменна піч, Морозна ливарня").
        internal static string KindNames(Kind kind)
        {
            EnsureFuels();
            if (s_fuelScene == null || Localization.instance == null)
            {
                return "";
            }
            if (s_kindNames.TryGetValue(kind, out string cached))
            {
                return cached;
            }
            List<string> names = new List<string>();
            foreach (GameObject prefab in s_fuelScene.m_prefabs)
            {
                Piece piece = prefab != null ? prefab.GetComponent<Piece>() : null;
                if (piece == null)
                {
                    continue;
                }
                Smelter smelter = prefab.GetComponentInChildren<Smelter>(true);
                CookingStation cooking = prefab.GetComponentInChildren<CookingStation>(true);
                Kind? found = null;
                if (smelter != null && (smelter.m_maxOre > 0 || HasFuel(smelter)))
                {
                    found = KindOf(Outputs(smelter), HasFuel(smelter) ? smelter.m_fuelItem.name : null);
                }
                else if (cooking != null && cooking.m_useFuel && cooking.m_fuelItem != null)
                {
                    found = KindOf(Outputs(cooking), cooking.m_fuelItem.name);
                }
                if (found != kind)
                {
                    continue;
                }
                string name = Localization.instance.Localize(piece.m_name);
                if (!string.IsNullOrEmpty(name) && !name.StartsWith("[") && !names.Contains(name))
                {
                    names.Add(name);
                }
            }
            string text = string.Join(", ", names.ToArray());
            s_kindNames[kind] = text;
            return text;
        }

        // A station that burns something: for the frigid kiln that fuel is also its raw material.
        private static bool HasFuel(Smelter smelter)
        {
            return smelter.m_maxFuel > 0 && smelter.m_fuelItem != null;
        }

        private static List<string> Outputs(Smelter smelter)
        {
            List<string> outputs = new List<string>();
            foreach (Smelter.ItemConversion conversion in smelter.m_conversion)
            {
                if (conversion?.m_to != null)
                {
                    outputs.Add(conversion.m_to.name);
                }
            }
            return outputs;
        }

        private static List<string> Outputs(CookingStation cooking)
        {
            List<string> outputs = new List<string>();
            foreach (CookingStation.ItemConversion conversion in cooking.m_conversion)
            {
                if (conversion?.m_to != null)
                {
                    outputs.Add(conversion.m_to.name);
                }
            }
            return outputs;
        }

        private static bool Enabled(string prefab)
        {
            EnsureStations();
            return StorageSettings.IsStationOn(prefab);
        }

        // Every station of the game that turns one thing into another, with its own switch. Read once per scene;
        // worlds set up by an older version keep what its three switches said.
        internal static List<StationKind> Stations()
        {
            EnsureStations();
            return s_stations;
        }

        private static List<StationKind> s_stations = new List<StationKind>();
        private static ZNetScene s_stationScene;

        private static void EnsureStations()
        {
            ZNetScene scene = ZNetScene.instance;
            if (scene == null || Localization.instance == null || (s_stations.Count > 0 && ReferenceEquals(scene, s_stationScene)))
            {
                return;
            }
            EnsureFuels();
            s_stationScene = scene;
            List<StationKind> stations = new List<StationKind>();
            foreach (GameObject prefab in scene.m_prefabs)
            {
                Piece piece = prefab != null ? prefab.GetComponent<Piece>() : null;
                if (piece == null)
                {
                    continue;
                }
                Smelter smelter = prefab.GetComponentInChildren<Smelter>(true);
                CookingStation cooking = prefab.GetComponentInChildren<CookingStation>(true);
                StationKind station = null;
                if (smelter != null && smelter.m_conversion.Count > 0 && (smelter.m_maxOre > 0 || HasFuel(smelter)))
                {
                    station = new StationKind
                    {
                        Prefab = prefab.name,
                        Kind = KindOf(Outputs(smelter), smelter.m_maxFuel > 0 && smelter.m_fuelItem != null ? smelter.m_fuelItem.name : null),
                        Takes = Names(Inputs(smelter), smelter.m_maxFuel > 0 && smelter.m_fuelItem != null ? smelter.m_fuelItem : null),
                        Makes = Names(Outputs(smelter), null),
                    };
                }
                else if (cooking != null && cooking.m_conversion.Count > 0)
                {
                    Kind kind = cooking.m_useFuel && cooking.m_fuelItem != null
                        ? KindOf(Outputs(cooking), cooking.m_fuelItem.name)
                        : Kind.Food;
                    station = new StationKind
                    {
                        Prefab = prefab.name,
                        Kind = kind == Kind.Other ? Kind.Food : kind,
                        Takes = Names(Inputs(cooking), cooking.m_useFuel ? cooking.m_fuelItem : null),
                        Makes = Names(Outputs(cooking), null),
                    };
                }
                if (station == null)
                {
                    continue;
                }
                string name = Localization.instance.Localize(piece.m_name);
                station.Name = string.IsNullOrEmpty(name) || name.StartsWith("[") ? prefab.name : name;
                stations.Add(station);
            }
            stations.Sort((a, b) =>
            {
                int byKind = a.Kind.CompareTo(b.Kind);
                return byKind != 0 ? byKind : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            s_stations = stations;
            if (!StorageSettings.StationsMigrated && stations.Count > 0)
            {
                StorageSettings.StationsMigrated = true;
                foreach (StationKind station in stations)
                {
                    bool on = station.Kind == Kind.Smelter ? StorageSettings.LoadSmelters
                        : station.Kind == Kind.Kiln ? StorageSettings.LoadKilns
                        : station.Kind == Kind.Food ? false
                        : StorageSettings.LoadOtherStations;
                    StorageSettings.SetStationOn(station.Prefab, on);
                }
                StorageSettings.Changed();
            }
        }

        private static List<string> Inputs(Smelter smelter)
        {
            List<string> inputs = new List<string>();
            foreach (Smelter.ItemConversion conversion in smelter.m_conversion)
            {
                if (conversion?.m_from != null)
                {
                    inputs.Add(conversion.m_from.name);
                }
            }
            return inputs;
        }

        private static List<string> Inputs(CookingStation cooking)
        {
            List<string> inputs = new List<string>();
            foreach (CookingStation.ItemConversion conversion in cooking.m_conversion)
            {
                if (conversion?.m_from != null)
                {
                    inputs.Add(conversion.m_from.name);
                }
            }
            return inputs;
        }

        // Item names for a switch's line, a few of them.
        private static string Names(List<string> prefabs, ItemDrop extra)
        {
            List<string> names = new List<string>();
            if (extra != null)
            {
                names.Add(Localization.instance.Localize(extra.m_itemData.m_shared.m_name));
            }
            foreach (string prefab in prefabs)
            {
                GameObject item = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
                ItemDrop drop = item != null ? item.GetComponent<ItemDrop>() : null;
                string name = drop != null ? Localization.instance.Localize(drop.m_itemData.m_shared.m_name) : prefab;
                if (!names.Contains(name))
                {
                    names.Add(name);
                }
                if (names.Count >= 4)
                {
                    break;
                }
            }
            return string.Join(", ", names.ToArray());
        }

        // Player-built stations around whose owner is known (a request to nobody would lose the item).
        internal static int CountAround(Vector3 center, float radius)
        {
            return FindStations(center, radius).Count;
        }

        private static List<Station> FindStations(Vector3 center, float radius, bool includeDisabled = false)
        {
            List<Station> result = new List<Station>();
            float radiusSqr = radius * radius;
            foreach (Smelter smelter in UnityEngine.Object.FindObjectsByType<Smelter>(FindObjectsSortMode.None))
            {
                if (smelter == null || (smelter.transform.position - center).sqrMagnitude > radiusSqr)
                {
                    continue;
                }
                Station station = NewStation(smelter, s_smelterView(smelter), center, includeDisabled,
                    Outputs(smelter), smelter.m_maxFuel > 0 && smelter.m_fuelItem != null ? smelter.m_fuelItem.name : null);
                if (station == null)
                {
                    continue;
                }
                station.NameKey = smelter.m_name;
                station.Queue = station.View.GetZDO().GetInt(ZDOVars.s_queued);
                station.MaxQueue = smelter.m_maxOre;
                station.MaxFuel = smelter.m_maxFuel;
                station.FuelPerProduct = smelter.m_fuelPerProduct;
                foreach (Smelter.ItemConversion conversion in smelter.m_conversion)
                {
                    if (conversion?.m_from != null)
                    {
                        station.Accepts.Add(conversion.m_from.gameObject.name);
                    }
                }
                station.FuelIsTheOre = station.Accepts.Count == 0 && smelter.m_maxOre <= 0 && station.FuelPrefab != null;
                if ((station.Accepts.Count > 0 && smelter.m_maxOre > 0) || station.FuelIsTheOre)
                {
                    result.Add(station);
                }
            }
            // stations that hold items in slots: the frost foundry, grills and ovens (each with a switch of its own)
            foreach (CookingStation cooking in UnityEngine.Object.FindObjectsByType<CookingStation>(FindObjectsSortMode.None))
            {
                if (cooking == null || cooking.m_slots == null || (cooking.transform.position - center).sqrMagnitude > radiusSqr)
                {
                    continue;
                }
                Station station = NewStation(cooking, s_cookingView(cooking), center, includeDisabled, Outputs(cooking),
                    cooking.m_useFuel && cooking.m_fuelItem != null ? cooking.m_fuelItem.name : null);
                if (station == null)
                {
                    continue;
                }
                ZDO zdo = station.View.GetZDO();
                int filled = 0;
                for (int i = 0; i < cooking.m_slots.Length; i++)
                {
                    if (zdo.GetString("slot" + i) != "")
                    {
                        filled++;
                    }
                }
                float cookTime = 0f;
                foreach (CookingStation.ItemConversion conversion in cooking.m_conversion)
                {
                    if (conversion?.m_from != null)
                    {
                        station.Accepts.Add(conversion.m_from.gameObject.name);
                        cookTime = Mathf.Max(cookTime, conversion.m_cookTime);
                    }
                }
                station.Slots = true;
                station.NameKey = cooking.m_name;
                station.Queue = filled;
                station.MaxQueue = cooking.m_slots.Length;
                station.MaxFuel = cooking.m_useFuel && cooking.m_fuelItem != null ? cooking.m_maxFuel : 0;
                station.FuelPerProduct = cooking.m_secPerFuel > 0 ? cookTime / cooking.m_secPerFuel : 1f;
                if (station.Accepts.Count > 0 && station.MaxQueue > 0)
                {
                    result.Add(station);
                }
            }
            result.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            return result;
        }

        private static Station NewStation(Component source, ZNetView view, Vector3 center, bool includeDisabled, List<string> outputs, string fuel)
        {
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            Piece piece = source.GetComponentInParent<Piece>();
            if (zdo == null || zdo.GetOwner() == 0L || piece == null || !piece.IsPlacedByPlayer())
            {
                return null;
            }
            Kind kind = KindOf(outputs, fuel);
            string prefabName = Utils.GetPrefabName(source.gameObject);
            if (!includeDisabled && !Enabled(prefabName))
            {
                return null;
            }
            return new Station
            {
                Source = source,
                View = view,
                Kind = kind,
                Prefab = prefabName,
                IsEnabled = Enabled(prefabName),
                Distance = Vector3.Distance(center, source.transform.position),
                Fuel = zdo.GetFloat(ZDOVars.s_fuel),
                FuelPrefab = fuel,
            };
        }

        internal static string Start()
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return Lang.T("Зайди у світ.");
            }
            if (s_running)
            {
                return Lang.T("Уже завантажую печі, зачекай секунду.");
            }
            if (!StorageCatalog.EnsureBuilt())
            {
                return Lang.T("Дані предметів ще не готові, спробуй за мить.");
            }
            List<Station> allStations = FindStations(player.transform.position, StorageSettings.Radius, includeDisabled: true);
            List<Station> stations = allStations.FindAll(s => s.IsEnabled);
            if (stations.Count == 0)
            {
                return Report(allStations.Count == 0
                    ? Lang.F("У радіусі {0:0} м немає печей, які можна завантажити.", StorageSettings.Radius)
                    : Lang.F("Печі поруч є ({0}), але всі їхні види вимкнені галочками в «База» → «Печі».", allStations.Count));
            }

            s_fromChestsAllowed.Clear();
            foreach (Station station in stations)
            {
                s_fromChestsAllowed.UnionWith(station.Accepts);
                if (station.FuelPrefab != null)
                {
                    s_fromChestsAllowed.Add(station.FuelPrefab);
                }
            }
            s_chests.Clear();
            foreach (Container chest in StorageSorter.ChestsAround(player.transform.position, StorageSettings.Radius))
            {
                ZNetView chestView = StorageSorter.View(chest);
                if (chestView != null && chestView.IsValid() && chestView.IsOwner() && !StorageSorter.InUseByOther(chest))
                {
                    s_chests.Add(chest);
                }
            }
            Dictionary<string, int> available = CountAvailable(player);
            Dictionary<string, Delivery> deliveries = new Dictionary<string, Delivery>();

            // Ore first, spread so that every station gets work: each piece goes to the emptiest one.
            List<KeyValuePair<string, int>> byAmount = new List<KeyValuePair<string, int>>(available);
            byAmount.Sort((a, b) => b.Value.CompareTo(a.Value));
            foreach (KeyValuePair<string, int> pair in byAmount)
            {
                int left = pair.Value;
                while (left > 0)
                {
                    Station best = null;
                    foreach (Station station in stations)
                    {
                        if (station.Accepts.Contains(pair.Key) && station.OreRoom > 0
                            && (best == null || station.Queue + station.PlannedOre < best.Queue + best.PlannedOre))
                        {
                            best = station;
                        }
                    }
                    if (best == null)
                    {
                        break;
                    }
                    best.PlannedOre++;
                    Plan(deliveries, best, pair.Key, isFuel: false);
                    left--;
                }
                available[pair.Key] = left;
            }

            // Fuel: only what the queued ore needs (fuel per product × ore, minus what is already burning). A furnace
            // is not topped up beyond that: coal without ore to melt would only sit there.
            foreach (Station station in stations)
            {
                if (station.FuelPrefab == null)
                {
                    continue;
                }
                int have = available.TryGetValue(station.FuelPrefab, out int count) ? count : 0;
                int want = Mathf.Min(station.FuelNeeded, station.FuelRoom, have);
                if (want <= 0)
                {
                    continue;
                }
                station.PlannedFuel += want;
                available[station.FuelPrefab] = have - want;
                Plan(deliveries, station, station.FuelPrefab, isFuel: true, want);
            }

            s_deliveries.Clear();
            s_deliveries.AddRange(deliveries.Values);
            s_loaded.Clear();
            s_touched.Clear();
            s_takenFromChests = 0;
            s_notes = Explain(player, allStations, available);
            if (s_deliveries.Count == 0)
            {
                return Report(s_notes.Length > 0
                    ? Lang.T("Нічого не завантажено. ") + s_notes
                    : Lang.F("Печей поруч: {0}, але ні в інвентарі, ні в скринях поруч немає нічого, що в них кладуть.", stations.Count));
            }
            s_running = true;
            Tick();
            return s_running ? Lang.F("Завантажую печі ({0})…", CountStations()) : null;
        }

        private static string s_notes = "";

        // Why materials that the stations around take are still in the inventory after planning.
        private static string Explain(Player player, List<Station> allStations, Dictionary<string, int> leftover)
        {
            List<string> notes = new List<string>();
            HashSet<string> keptPrefabs = new HashSet<string>();
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems())
            {
                string prefab = PrefabName(item);
                if (prefab != null && StorageSorter.IsKept(player, item) && !item.m_equipped)
                {
                    keptPrefabs.Add(prefab);
                }
            }
            HashSet<string> prefabs = new HashSet<string>(keptPrefabs);
            foreach (KeyValuePair<string, int> pair in leftover)
            {
                if (pair.Value > 0)
                {
                    prefabs.Add(pair.Key);
                }
            }
            foreach (string prefab in prefabs)
            {
                List<Station> takesAsOre = allStations.FindAll(s => s.Accepts.Contains(prefab));
                List<Station> takesAsFuel = allStations.FindAll(s => s.FuelPrefab == prefab);
                if (takesAsOre.Count == 0 && takesAsFuel.Count == 0)
                {
                    continue;
                }
                string name = StorageCatalog.GetByPrefab(prefab)?.DisplayName ?? prefab;
                int count = leftover.TryGetValue(prefab, out int left) ? left : 0;
                if (keptPrefabs.Contains(prefab) && count == 0)
                {
                    notes.Add(Lang.F("{0}: позначено «не чіпати»", name));
                    continue;
                }
                List<Station> enabledOre = takesAsOre.FindAll(s => s.IsEnabled);
                List<Station> enabledFuel = takesAsFuel.FindAll(s => s.IsEnabled);
                if (enabledOre.Count == 0 && enabledFuel.Count == 0)
                {
                    notes.Add(Lang.F("{0} ×{1}: такі печі вимкнені галочкою", name, count));
                }
                else if (enabledFuel.Count > 0)
                {
                    Station sample = enabledFuel[0];
                    notes.Add(Lang.F("{0} ×{1}: у печах ({2}) уже досить палива на всю руду; " +
                        "більше не кладу, бо без руди воно лише лежало б", name, count, StationName(sample)));
                }
                else
                {
                    notes.Add(Lang.F("{0} ×{1}: {2} вже заповнені", name, count, StationName(enabledOre[0])));
                }
            }
            return notes.Count == 0 ? "" : string.Join(". ", notes.ToArray()) + ".";
        }

        private static string StationName(Station station)
        {
            return Localization.instance != null ? Localization.instance.Localize(station.NameKey) : station.NameKey;
        }

        private static void Plan(Dictionary<string, Delivery> deliveries, Station station, string prefab, bool isFuel, int amount = 1)
        {
            string key = station.Source.GetInstanceID() + "|" + prefab + "|" + isFuel;
            if (!deliveries.TryGetValue(key, out Delivery delivery))
            {
                delivery = new Delivery { Station = station, Prefab = prefab, IsFuel = isFuel };
                deliveries[key] = delivery;
            }
            delivery.Left += amount;
        }

        private static int CountStations()
        {
            HashSet<Component> stations = new HashSet<Component>();
            foreach (Delivery delivery in s_deliveries)
            {
                stations.Add(delivery.Station.Source);
            }
            return stations.Count;
        }

        private static Dictionary<string, int> CountAvailable(Player player)
        {
            Dictionary<string, int> result = new Dictionary<string, int>();
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems())
            {
                string prefab = PrefabName(item);
                if (prefab != null && !StorageSorter.IsKept(player, item))
                {
                    result[prefab] = (result.TryGetValue(prefab, out int count) ? count : 0) + item.m_stack;
                }
            }
            foreach (Container chest in s_chests)
            {
                foreach (ItemDrop.ItemData item in chest.GetInventory().GetAllItems())
                {
                    string prefab = PrefabName(item);
                    if (prefab != null && s_fromChestsAllowed.Contains(prefab))
                    {
                        result[prefab] = (result.TryGetValue(prefab, out int count) ? count : 0) + item.m_stack;
                    }
                }
            }
            return result;
        }

        private static string PrefabName(ItemDrop.ItemData item)
        {
            if (item?.m_dropPrefab != null)
            {
                return item.m_dropPrefab.name;
            }
            return StorageCatalog.Get(item)?.Prefab;
        }

        // Called every frame: one piece per station and item kind.
        internal static void Tick()
        {
            if (!s_running)
            {
                return;
            }
            Player player = Player.m_localPlayer;
            if (player == null || ZNet.instance == null)
            {
                s_running = false;
                s_deliveries.Clear();
                return;
            }
            bool pending = false;
            foreach (Delivery delivery in s_deliveries)
            {
                if (delivery.Left <= 0)
                {
                    continue;
                }
                ZNetView view = delivery.Station.View;
                if (delivery.Station.Source == null || view == null || !view.IsValid() || view.GetZDO().GetOwner() == 0L)
                {
                    delivery.Left = 0;
                    continue;
                }
                ItemDrop.ItemData item = FindItem(player, delivery.Prefab, out Container chest);
                if (item == null)
                {
                    delivery.Left = 0;
                    continue;
                }
                string name = item.m_shared.m_name;
                bool cheated = item.m_cheated;
                if (chest != null)
                {
                    chest.GetInventory().RemoveItem(item, 1);
                    s_takenFromChests++;
                }
                else
                {
                    player.GetInventory().RemoveItem(item, 1);
                }
                if (delivery.IsFuel)
                {
                    view.InvokeRPC("RPC_AddFuel");
                }
                else if (delivery.Station.Slots)
                {
                    view.InvokeRPC("RPC_AddItem", delivery.Prefab, cheated);
                }
                else
                {
                    view.InvokeRPC("RPC_AddOre", delivery.Prefab, cheated);
                }
                s_loaded[name] = (s_loaded.TryGetValue(name, out int count) ? count : 0) + 1;
                s_touched.Add(delivery.Station.Source);
                delivery.Left--;
                pending |= delivery.Left > 0;
            }
            if (!pending)
            {
                s_running = false;
                s_deliveries.Clear();
                Report(FinishText());
            }
        }

        private static ItemDrop.ItemData FindItem(Player player, string prefab, out Container from)
        {
            from = null;
            ItemDrop.ItemData best = null;
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems())
            {
                if (PrefabName(item) == prefab && !StorageSorter.IsKept(player, item) && (best == null || item.m_stack < best.m_stack))
                {
                    // The smallest stack first, so the inventory frees slots sooner.
                    best = item;
                }
            }
            if (best != null || !s_fromChestsAllowed.Contains(prefab))
            {
                return best;
            }
            foreach (Container chest in s_chests)
            {
                ZNetView view = chest != null ? StorageSorter.View(chest) : null;
                if (view == null || !view.IsValid() || !view.IsOwner() || StorageSorter.InUseByOther(chest))
                {
                    continue;
                }
                foreach (ItemDrop.ItemData item in chest.GetInventory().GetAllItems())
                {
                    if (PrefabName(item) == prefab && (best == null || item.m_stack < best.m_stack))
                    {
                        best = item;
                        from = chest;
                    }
                }
            }
            return best;
        }

        private static string FinishText()
        {
            string notes = s_notes.Length > 0 ? Lang.T(" Не завантажено: ") + s_notes : "";
            if (s_loaded.Count == 0)
            {
                return Lang.T("Нічого не завантажено.") + notes;
            }
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> pair in s_loaded)
            {
                parts.Add($"{Localization.instance.Localize(pair.Key)} ×{pair.Value}");
            }
            string chests = s_takenFromChests > 0 ? Lang.F(" Зі скринь поруч узято {0}.", s_takenFromChests) : "";
            return new StringBuilder(Lang.T("Завантажено в печі (")).Append(s_touched.Count).Append("): ").Append(string.Join(", ", parts.ToArray())).Append('.')
                .Append(chests).Append(notes).ToString();
        }

        private static string Report(string message)
        {
            LastReport = message;
            Log.Info(message);
            Player player = Player.m_localPlayer;
            if (player != null)
            {
                player.Message(MessageHud.MessageType.TopLeft, message);
            }
            return null;
        }
    }
}
