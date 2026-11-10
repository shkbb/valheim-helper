using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimHelper.Storage
{
    internal sealed class StorageGroup
    {
        public string Id;
        public string Label;
        // Optional explanation shown next to the label in lists.
        public string Details;
        public Heightmap.Biome Biome;

        public bool IsBiome => Biome != Heightmap.Biome.None;

        public string DisplayName => IsBiome ? Map.MapBiomes.BiomeName(Biome) : Label;
    }

    internal sealed class StorageItem
    {
        public string SharedName;
        public string Prefab;
        public string DisplayName;
        public string SearchKey;
        public StorageGroup Group;
        public StorageGroup BiomeGroup;
        public Texture2D IconTexture;
        public Rect IconUv;
        // The same group and biome in the form the sorting rules use.
        public readonly ItemTraits Traits = new ItemTraits();
    }

    // What kind of thing every item is, derived from the game data itself: item types, what smelters,
    // kilns, fermenters, cooking stations and plants take, recipes, and where the world spawns an item
    // (vegetation, ore veins, creatures of each biome). Keyed by the item's shared name, as the game's own
    // "place stacks" does.
    internal static class StorageCatalog
    {
        internal static readonly StorageGroup[] Groups =
        {
            Group("wood", Lang.T("Дерево")),
            Group("stone", Lang.T("Камінь"), Lang.T("камінь, кремінь, мармур, обсидіан, кристали")),
            Group("metal", Lang.T("Руда й метали"), Lang.T("руда, брухт, злитки, цвяхи")),
            Group("fuel", Lang.T("Вугілля")),
            Group("cores", Lang.T("Ядра")),
            Group("hides", Lang.T("Шкури"), Lang.T("шкури, шкіра, луска")),
            Group("seeds", Lang.T("Насіння"), Lang.T("насіння, шишки, ячмінь, льон — усе, що садять культиватором")),
            Group("rawfood", Lang.T("Сире м'ясо"), Lang.T("усе, що смажать або печуть")),
            Group("valuables", Lang.T("Цінності"), Lang.T("монети й те, що купує торговець")),
            Group("trophies", Lang.T("Трофеї")),
            Group("fish", Lang.T("Риба")),
            Group("food", Lang.T("Їжа")),
            Group("meads", Lang.T("Меди й зілля")),
            Group("ammo", Lang.T("Стріли й болти")),
            Group("gear", Lang.T("Спорядження"), Lang.T("зброя, броня, щити, інструменти, факели")),
        };

        // Progression order; the earliest biome an item appears in is "its" biome.
        internal static readonly StorageGroup[] Biomes =
        {
            BiomeGroup("b_meadows", Heightmap.Biome.Meadows),
            BiomeGroup("b_blackforest", Heightmap.Biome.BlackForest),
            BiomeGroup("b_swamp", Heightmap.Biome.Swamp),
            BiomeGroup("b_mountain", Heightmap.Biome.Mountain),
            BiomeGroup("b_plains", Heightmap.Biome.Plains),
            BiomeGroup("b_mistlands", Heightmap.Biome.Mistlands),
            BiomeGroup("b_ashlands", Heightmap.Biome.AshLands),
            BiomeGroup("b_deepnorth", Heightmap.Biome.DeepNorth),
            BiomeGroup("b_ocean", Heightmap.Biome.Ocean),
        };

        // Prefab name -> biome, for materials the world data does not place (or places too early). Names that do not
        // exist in the game are simply never matched.
        private static readonly Dictionary<string, Heightmap.Biome> s_knownBiomes = BuildKnownBiomes();

        private static Dictionary<string, Heightmap.Biome> BuildKnownBiomes()
        {
            Dictionary<string, Heightmap.Biome> result = new Dictionary<string, Heightmap.Biome>();
            void Add(Heightmap.Biome biome, params string[] prefabs)
            {
                foreach (string prefab in prefabs)
                {
                    result[prefab] = biome;
                }
            }
            Add(Heightmap.Biome.BlackForest, "SurtlingCore", "AncientSeed", "GreydwarfEye", "TrophyGdKing", "CryptKey", "YellowMushroom", "Thistle",
                "CarrotSeeds", "Carrot", "TinOre", "CopperOre", "RoundLog", "TrollHide", "TrophyFrostTroll", "TrophyForestTroll");
            Add(Heightmap.Biome.Swamp, "Chain", "WitheredBone", "IronScrap", "Ooze", "Entrails", "Bloodbag", "Guck", "ElderBark", "TurnipSeeds",
                "Turnip", "Root", "TrophyAbomination", "TrophyBonemass", "Wishbone", "TrophyDraugr", "TrophyDraugrElite", "TrophyBlob", "TrophyLeech",
                "TrophyWraith", "TrophySkeletonPoison", "Amber", "AmberPearl");
            Add(Heightmap.Biome.Mountain, "SilverOre", "Obsidian", "Crystal", "DragonEgg", "DragonTear", "WolfFang", "WolfPelt", "WolfMeat",
                "FreezeGland", "JuteRed", "Onion", "OnionSeeds", "TrophyCultist", "TrophyFenring", "TrophySGolem", "TrophyHatchling", "TrophyUlv",
                "TrophyWolf", "TrophyDragonQueen", "WolfClaw", "WolfHairBundle");
            Add(Heightmap.Biome.Plains, "BlackMetalScrap", "Barley", "Flax", "Tar", "Needle", "GoblinTotem", "LoxPelt", "LoxMeat", "Cloudberry",
                "YagluthDrop", "TrophyGoblin", "TrophyGoblinBrute", "TrophyGoblinShaman", "TrophyLox", "TrophyDeathsquito", "TrophyGrowth",
                "TrophyGoblinKing", "BarleyFlour", "LinenThread", "ChickenEgg", "ChickenMeat");
            Add(Heightmap.Biome.Mistlands, "Sap", "Eitr", "Softtissue", "BlackMarble", "BlackCore", "Carapace", "Mandible", "Bilebag", "RoyalJelly",
                "GiantBloodSack", "MushroomJotunPuffs", "MushroomMagecap", "YggdrasilWood", "ScaleHide", "BugMeat", "HareMeat", "Wisp",
                "DvergrNeedle", "DvergrKeyFragment", "DvergrKey", "JuteBlue", "TrophyHare", "TrophySeeker", "TrophySeekerBrute", "TrophyTick",
                "TrophyGjall", "TrophyDvergr", "TrophySeekerQueen", "QueenDrop", "Iolite");
            Add(Heightmap.Biome.AshLands, "Blackwood", "FlametalOreNew", "FlametalNew", "FlametalOre", "Flametal", "CharredBone", "CeramicPlate",
                "AskHide", "AsksvinMeat", "VoltureMeat", "VoltureEgg", "MorgenHeart", "MorgenSinew", "ProustitePowder", "SulfurStone", "Grausten",
                "BonemawSerpentMeat", "BonemawSerpentTooth", "CelestialFeather", "CharcoalResin", "CharredCogwheel", "MoltenCore", "Pukeberries",
                "Vineberry", "VineberrySeeds", "Fiddleheadfern", "MushroomSmokePuff", "TrophyAsksvin", "TrophyVolture", "TrophyMorgen",
                "TrophyCharredArcher", "TrophyCharredMage", "TrophyCharredMelee", "TrophyFallenValkyrie", "TrophyBonemawSerpent", "TrophyFader",
                "FaderDrop", "Bell");
            Add(Heightmap.Biome.Ocean, "SerpentMeat", "SerpentScale", "TrophySerpent", "Chitin");
            return result;
        }

        private static readonly Dictionary<string, StorageItem> s_items = new Dictionary<string, StorageItem>();
        private static readonly List<StorageItem> s_sorted = new List<StorageItem>();
        private static ObjectDB s_builtFor;
        private static ZoneSystem s_biomesFor;

        internal static IReadOnlyList<StorageItem> All => s_sorted;

        private static StorageGroup Group(string id, string label, string details = null)
        {
            return new StorageGroup { Id = id, Label = label, Details = details };
        }

        private static StorageGroup BiomeGroup(string id, Heightmap.Biome biome)
        {
            return new StorageGroup { Id = id, Label = biome.ToString(), Biome = biome };
        }

        internal static StorageGroup FindGroup(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            foreach (StorageGroup group in Groups)
            {
                if (group.Id == id)
                {
                    return group;
                }
            }
            foreach (StorageGroup group in Biomes)
            {
                if (group.Id == id)
                {
                    return group;
                }
            }
            return null;
        }

        internal static bool EnsureBuilt()
        {
            ObjectDB db = ObjectDB.instance;
            if (db == null || db.m_items == null || db.m_items.Count == 0)
            {
                return false;
            }
            if (!ReferenceEquals(db, s_builtFor))
            {
                BuildItems(db);
                s_builtFor = db;
                s_biomesFor = null;
            }
            ZoneSystem zones = ZoneSystem.instance;
            if (zones != null && !ReferenceEquals(zones, s_biomesFor))
            {
                s_biomesFor = zones;
                try
                {
                    AssignBiomes(db, zones);
                }
                catch (Exception e)
                {
                    Log.Error(e);
                }
            }
            return true;
        }

        internal static StorageItem Get(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null)
            {
                return null;
            }
            return Get(item.m_shared.m_name);
        }

        internal static StorageItem Get(string sharedName)
        {
            return sharedName != null && s_items.TryGetValue(sharedName, out StorageItem info) ? info : null;
        }

        internal static StorageItem GetByPrefab(string prefab)
        {
            foreach (StorageItem item in s_sorted)
            {
                if (item.Prefab == prefab)
                {
                    return item;
                }
            }
            return null;
        }

        // ---------------------------------------------------------------- groups

        private sealed class StationData
        {
            public readonly HashSet<string> Fermenter = new HashSet<string>();
            public readonly HashSet<string> CookingInputs = new HashSet<string>();
            public readonly HashSet<string> Metal = new HashSet<string>();
            public readonly HashSet<string> KilnInputs = new HashSet<string>();
            public readonly HashSet<string> PlantRequirements = new HashSet<string>();
            // output -> inputs, for passing a biome on from ore to bar, flax to thread and so on
            public readonly List<KeyValuePair<string, string>> Conversions = new List<KeyValuePair<string, string>>();
            // Items used together: the requirements of every recipe and build piece, and every station's fuel with what it processes.
            public readonly List<List<string>> UsedTogether = new List<List<string>>();
        }

        private static readonly HashSet<string> s_tieredGroups = new HashSet<string> { "wood", "stone", "metal" };
        // An ingredient in this many recipes or pieces (wood, stone, resin...) says little about a recipe's tier.
        private const int HubUses = 30;

        private static List<List<string>> s_usedTogether = new List<List<string>>();
        private static readonly Dictionary<string, int> s_uses = new Dictionary<string, int>();
        private static readonly Dictionary<string, Dictionary<string, int>> s_sharedUses = new Dictionary<string, Dictionary<string, int>>();

        private static void AddUsedTogether(StationData stations, IEnumerable<string> names)
        {
            List<string> set = new List<string>();
            foreach (string name in names)
            {
                if (name != null && !set.Contains(name))
                {
                    set.Add(name);
                }
            }
            if (set.Count > 0)
            {
                stations.UsedTogether.Add(set);
            }
        }

        private static IEnumerable<string> RequirementNames(Piece.Requirement[] requirements)
        {
            if (requirements == null)
            {
                yield break;
            }
            foreach (Piece.Requirement requirement in requirements)
            {
                yield return SharedName(requirement?.m_resItem);
            }
        }

        // 0..1: shared recipes relative to the rarer of the two items.
        private static float CoUse(string a, string b)
        {
            if (a == null || b == null || !s_sharedUses.TryGetValue(a, out Dictionary<string, int> shared) || !shared.TryGetValue(b, out int together))
            {
                return 0f;
            }
            int fewer = Math.Min(s_uses.TryGetValue(a, out int usesA) ? usesA : 0, s_uses.TryGetValue(b, out int usesB) ? usesB : 0);
            return fewer > 0 ? Math.Min(1f, together / (float)fewer) : 0f;
        }

        private static void CountUses(List<List<string>> sets)
        {
            s_uses.Clear();
            s_sharedUses.Clear();
            foreach (List<string> set in sets)
            {
                foreach (string a in set)
                {
                    s_uses[a] = (s_uses.TryGetValue(a, out int uses) ? uses : 0) + 1;
                    if (!s_sharedUses.TryGetValue(a, out Dictionary<string, int> shared))
                    {
                        shared = new Dictionary<string, int>();
                        s_sharedUses[a] = shared;
                    }
                    foreach (string b in set)
                    {
                        if (a != b)
                        {
                            shared[b] = (shared.TryGetValue(b, out int together) ? together : 0) + 1;
                        }
                    }
                }
            }
        }

        private static void BuildItems(ObjectDB db)
        {
            s_items.Clear();
            s_sorted.Clear();
            StationData stations = ReadStations(db);
            Dictionary<string, ItemDrop.ItemData.SharedData> shared = new Dictionary<string, ItemDrop.ItemData.SharedData>();
            foreach (GameObject prefab in db.m_items)
            {
                ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                ItemDrop.ItemData.SharedData data = drop?.m_itemData?.m_shared;
                if (data == null || string.IsNullOrEmpty(data.m_name) || s_items.ContainsKey(data.m_name))
                {
                    continue;
                }
                string display = Localization.instance != null ? Localization.instance.Localize(data.m_name) : data.m_name;
                StorageItem item = new StorageItem
                {
                    SharedName = data.m_name,
                    Prefab = prefab.name,
                    DisplayName = display,
                    SearchKey = (display + " " + prefab.name).ToLowerInvariant(),
                };
                if (data.m_icons != null && data.m_icons.Length > 0)
                {
                    SetIcon(item, data.m_icons[0]);
                }
                s_items[data.m_name] = item;
                shared[data.m_name] = data;
                s_sorted.Add(item);
            }
            foreach (StorageItem item in s_sorted)
            {
                item.Group = FindGroup(Classify(item.Prefab, shared[item.SharedName], stations));
            }
            // Bronze, nails and the like are made from metal at the forge.
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (Recipe recipe in db.m_recipes)
                {
                    StorageItem result = RecipeResult(recipe);
                    if (result == null || result.Group != null || !shared.TryGetValue(result.SharedName, out ItemDrop.ItemData.SharedData data)
                        || data.m_itemType != ItemDrop.ItemData.ItemType.Material || data.m_maxStackSize <= 1)
                    {
                        continue;
                    }
                    int metal = 0;
                    int total = 0;
                    foreach (Piece.Requirement requirement in recipe.m_resources)
                    {
                        StorageItem input = Get(requirement?.m_resItem?.m_itemData?.m_shared?.m_name);
                        if (input == null)
                        {
                            continue;
                        }
                        total++;
                        if (input.Group != null && input.Group.Id == "metal")
                        {
                            metal++;
                        }
                    }
                    if (total > 0 && metal * 2 >= total)
                    {
                        result.Group = FindGroup("metal");
                    }
                }
            }
            s_sorted.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
            s_conversions = stations.Conversions;
            foreach (Recipe recipe in db.m_recipes)
            {
                if (recipe != null)
                {
                    AddUsedTogether(stations, RequirementNames(recipe.m_resources));
                }
            }
            s_usedTogether = stations.UsedTogether;
            CountUses(s_usedTogether);
            SortRules.CoUse = CoUse;
            ShelfPlanner.GroupOrder = group => Array.FindIndex(Groups, g => g.Id == group);
            foreach (StorageItem item in s_sorted)
            {
                item.Traits.Id = item.SharedName;
                item.Traits.Group = item.Group?.Id;
                item.Traits.GroupIsTiered = item.Group != null && s_tieredGroups.Contains(item.Group.Id);
                item.Traits.Biome = -1;
            }
            Log.Info($"Storage catalog: {s_sorted.Count} items, {s_usedTogether.Count} recipes and pieces");
        }

        private static List<KeyValuePair<string, string>> s_conversions = new List<KeyValuePair<string, string>>();

        private static bool IsCurrency(string prefab)
        {
            ItemDrop coin = StoreGui.instance != null ? StoreGui.instance.m_coinPrefab : null;
            return coin != null ? coin.gameObject.name == prefab : prefab == "Coins";
        }

        private static string Classify(string prefab, ItemDrop.ItemData.SharedData data, StationData stations)
        {
            switch (data.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.Trophy:
                    return "trophies";
                case ItemDrop.ItemData.ItemType.Ammo:
                case ItemDrop.ItemData.ItemType.AmmoNonEquipable:
                    return "ammo";
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Shield:
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Hands:
                case ItemDrop.ItemData.ItemType.Shoulder:
                case ItemDrop.ItemData.ItemType.Utility:
                case ItemDrop.ItemData.ItemType.Trinket:
                case ItemDrop.ItemData.ItemType.Torch:
                case ItemDrop.ItemData.ItemType.Tool:
                case ItemDrop.ItemData.ItemType.Attach_Atgeir:
                    return "gear";
                case ItemDrop.ItemData.ItemType.Fish:
                    return "fish";
            }
            string name = data.m_name;
            if (stations.Fermenter.Contains(name))
            {
                return "meads";
            }
            if (data.m_itemType == ItemDrop.ItemData.ItemType.Consumable)
            {
                return data.m_food > 0f || data.m_foodStamina > 0f || data.m_foodEitr > 0f ? "food" : "meads";
            }
            // Coins: what traders take as money (the game's own coin item), and whatever traders buy.
            if (data.m_value > 0 || IsCurrency(prefab))
            {
                return "valuables";
            }
            if (prefab == "Coal")
            {
                return "fuel";
            }
            if (stations.Metal.Contains(name))
            {
                return "metal";
            }
            if (stations.KilnInputs.Contains(name) || prefab.IndexOf("wood", StringComparison.OrdinalIgnoreCase) >= 0
                || prefab.EndsWith("Log", StringComparison.Ordinal) || prefab.Contains("Bark"))
            {
                return "wood";
            }
            if (prefab.Contains("Stone") || prefab.Contains("Marble") || prefab.Contains("Crystal")
                || prefab == "Grausten" || prefab == "Obsidian" || prefab == "Flint")
            {
                return "stone";
            }
            if (prefab.Contains("Core"))
            {
                return "cores";
            }
            if (prefab.Contains("Hide") || prefab.Contains("Pelt") || prefab.Contains("Leather") || prefab.Contains("Scale"))
            {
                return "hides";
            }
            if (stations.PlantRequirements.Contains(name))
            {
                return "seeds";
            }
            if (stations.CookingInputs.Contains(name))
            {
                return "rawfood";
            }
            return null;
        }

        private static StationData ReadStations(ObjectDB db)
        {
            StationData stations = new StationData();
            HashSet<GameObject> pieces = new HashSet<GameObject>();
            foreach (GameObject prefab in db.m_items)
            {
                PieceTable table = prefab != null ? prefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces : null;
                if (table?.m_pieces == null)
                {
                    continue;
                }
                foreach (GameObject piece in table.m_pieces)
                {
                    if (piece != null)
                    {
                        pieces.Add(piece);
                    }
                }
            }
            foreach (GameObject piece in pieces)
            {
                AddUsedTogether(stations, RequirementNames(piece.GetComponent<Piece>()?.m_resources));
                foreach (Smelter smelter in piece.GetComponentsInChildren<Smelter>(true))
                {
                    if (smelter.m_fuelItem != null)
                    {
                        List<string> station = new List<string> { SharedName(smelter.m_fuelItem) };
                        foreach (Smelter.ItemConversion conversion in smelter.m_conversion)
                        {
                            station.Add(SharedName(conversion?.m_from));
                            station.Add(SharedName(conversion?.m_to));
                        }
                        AddUsedTogether(stations, station);
                    }
                    bool metal = smelter.m_fuelItem != null && smelter.m_fuelItem.name == "Coal";
                    foreach (Smelter.ItemConversion conversion in smelter.m_conversion)
                    {
                        string from = SharedName(conversion?.m_from);
                        string to = SharedName(conversion?.m_to);
                        if (from == null || to == null)
                        {
                            continue;
                        }
                        stations.Conversions.Add(new KeyValuePair<string, string>(to, from));
                        if (metal)
                        {
                            stations.Metal.Add(from);
                            stations.Metal.Add(to);
                        }
                        else if (conversion.m_to.name == "Coal")
                        {
                            stations.KilnInputs.Add(from);
                        }
                    }
                }
                foreach (Fermenter fermenter in piece.GetComponentsInChildren<Fermenter>(true))
                {
                    foreach (Fermenter.ItemConversion conversion in fermenter.m_conversion)
                    {
                        AddConversion(stations, stations.Fermenter, SharedName(conversion?.m_from), SharedName(conversion?.m_to));
                    }
                }
                foreach (CookingStation station in piece.GetComponentsInChildren<CookingStation>(true))
                {
                    foreach (CookingStation.ItemConversion conversion in station.m_conversion)
                    {
                        string from = SharedName(conversion?.m_from);
                        string to = SharedName(conversion?.m_to);
                        if (from != null && to != null)
                        {
                            stations.CookingInputs.Add(from);
                            stations.Conversions.Add(new KeyValuePair<string, string>(to, from));
                        }
                    }
                }
                if (piece.GetComponent<Plant>() != null)
                {
                    Piece plantPiece = piece.GetComponent<Piece>();
                    if (plantPiece?.m_resources != null)
                    {
                        foreach (Piece.Requirement requirement in plantPiece.m_resources)
                        {
                            string seed = SharedName(requirement?.m_resItem);
                            if (seed != null)
                            {
                                stations.PlantRequirements.Add(seed);
                            }
                        }
                    }
                }
            }
            return stations;
        }

        private static void AddConversion(StationData stations, HashSet<string> set, string from, string to)
        {
            if (from == null || to == null)
            {
                return;
            }
            set.Add(from);
            set.Add(to);
            stations.Conversions.Add(new KeyValuePair<string, string>(to, from));
        }

        private static string SharedName(ItemDrop drop)
        {
            string name = drop != null ? drop.m_itemData?.m_shared?.m_name : null;
            return string.IsNullOrEmpty(name) ? null : name;
        }

        private static StorageItem RecipeResult(Recipe recipe)
        {
            return recipe != null && recipe.m_resources != null ? Get(SharedName(recipe.m_item)) : null;
        }

        // ---------------------------------------------------------------- biomes

        private static void AssignBiomes(ObjectDB db, ZoneSystem zones)
        {
            Dictionary<string, int> rank = new Dictionary<string, int>();
            List<GameObject> drops = new List<GameObject>();
            HashSet<GameObject> visited = new HashSet<GameObject>();

            foreach (ZoneSystem.ZoneVegetation vegetation in zones.m_vegetation)
            {
                if (vegetation != null && vegetation.m_enable && vegetation.m_prefab != null)
                {
                    AddSource(rank, drops, visited, vegetation.m_prefab, vegetation.m_biome);
                }
            }
            HashSet<SpawnSystemList> lists = new HashSet<SpawnSystemList>(Resources.FindObjectsOfTypeAll<SpawnSystemList>());
            foreach (SpawnSystemList list in lists)
            {
                if (list?.m_spawners == null)
                {
                    continue;
                }
                foreach (SpawnSystem.SpawnData spawn in list.m_spawners)
                {
                    if (spawn != null && spawn.m_enabled && spawn.m_prefab != null)
                    {
                        AddSource(rank, drops, visited, spawn.m_prefab, spawn.m_biome);
                    }
                }
            }

            // Materials that come from dungeons, boss locations, extractors or rare spawns are not found through
            // world vegetation and spawns (or only through an earlier biome); their biome is set explicitly.
            foreach (StorageItem item in s_sorted)
            {
                if (s_knownBiomes.TryGetValue(item.Prefab, out Heightmap.Biome biome))
                {
                    rank[item.SharedName] = BiomeRank(biome);
                }
            }

            // Bars keep the biome of their ore, thread of its flax and so on; crafted materials take the
            // latest biome among their ingredients.
            for (int pass = 0; pass < 3; pass++)
            {
                foreach (KeyValuePair<string, string> conversion in s_conversions)
                {
                    if (!rank.ContainsKey(conversion.Key) && rank.TryGetValue(conversion.Value, out int fromRank))
                    {
                        rank[conversion.Key] = fromRank;
                    }
                }
                foreach (Recipe recipe in db.m_recipes)
                {
                    StorageItem result = RecipeResult(recipe);
                    if (result == null || rank.ContainsKey(result.SharedName) || (result.Group != null && result.Group.Id == "gear"))
                    {
                        continue;
                    }
                    int latest = -1;
                    foreach (Piece.Requirement requirement in recipe.m_resources)
                    {
                        string input = SharedName(requirement?.m_resItem);
                        if (input != null && rank.TryGetValue(input, out int inputRank))
                        {
                            latest = Mathf.Max(latest, inputRank);
                        }
                    }
                    if (latest >= 0)
                    {
                        rank[result.SharedName] = latest;
                    }
                }
            }

            int known = rank.Count;
            InferBiomesFromUse(rank);

            int assigned = 0;
            foreach (StorageItem item in s_sorted)
            {
                item.BiomeGroup = rank.TryGetValue(item.SharedName, out int index) ? Biomes[index] : null;
                item.Traits.Biome = item.BiomeGroup != null ? index : -1;
                if (item.BiomeGroup != null)
                {
                    assigned++;
                }
            }
            Log.Info($"Storage catalog: biomes known for {assigned} items ({assigned - Math.Min(assigned, known)} inferred from recipes)");
        }

        // Items the world data does not place (extractor products, dungeon loot and the like) get the biome of the
        // recipes, pieces and stations they are used in: the tier of a recipe is the latest biome among its other
        // known ingredients (ignoring wood, stone and other ingredients used almost everywhere when possible), and
        // the item takes the tier it is used at most often, the earlier one on a tie. A station's fuel and inputs
        // also take the biome of what the station makes.
        private static void InferBiomesFromUse(Dictionary<string, int> rank)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                Dictionary<string, Dictionary<int, int>> votes = new Dictionary<string, Dictionary<int, int>>();
                foreach (List<string> set in s_usedTogether)
                {
                    int tier = -1;
                    int hubTier = -1;
                    foreach (string name in set)
                    {
                        if (!rank.TryGetValue(name, out int biome))
                        {
                            continue;
                        }
                        if (s_uses.TryGetValue(name, out int uses) && uses >= HubUses)
                        {
                            hubTier = Math.Max(hubTier, biome);
                        }
                        else
                        {
                            tier = Math.Max(tier, biome);
                        }
                    }
                    tier = tier >= 0 ? tier : hubTier;
                    if (tier < 0)
                    {
                        continue;
                    }
                    foreach (string name in set)
                    {
                        if (rank.ContainsKey(name))
                        {
                            continue;
                        }
                        if (!votes.TryGetValue(name, out Dictionary<int, int> byTier))
                        {
                            byTier = new Dictionary<int, int>();
                            votes[name] = byTier;
                        }
                        byTier[tier] = (byTier.TryGetValue(tier, out int count) ? count : 0) + 1;
                    }
                }
                foreach (KeyValuePair<string, string> conversion in s_conversions)
                {
                    if (!rank.ContainsKey(conversion.Value) && rank.TryGetValue(conversion.Key, out int toRank) && !votes.ContainsKey(conversion.Value))
                    {
                        votes[conversion.Value] = new Dictionary<int, int> { { toRank, 1 } };
                    }
                }
                if (votes.Count == 0)
                {
                    return;
                }
                foreach (KeyValuePair<string, Dictionary<int, int>> vote in votes)
                {
                    int bestTier = -1;
                    int bestCount = 0;
                    foreach (KeyValuePair<int, int> byTier in vote.Value)
                    {
                        if (byTier.Value > bestCount || (byTier.Value == bestCount && byTier.Key < bestTier))
                        {
                            bestTier = byTier.Key;
                            bestCount = byTier.Value;
                        }
                    }
                    if (bestTier >= 0)
                    {
                        rank[vote.Key] = bestTier;
                    }
                }
            }
        }

        private static void AddSource(Dictionary<string, int> rank, List<GameObject> drops, HashSet<GameObject> visited, GameObject source, Heightmap.Biome biome)
        {
            int index = BiomeRank(biome);
            if (index < 0)
            {
                return;
            }
            drops.Clear();
            visited.Clear();
            CollectDrops(source, drops, visited, 0);
            foreach (GameObject drop in drops)
            {
                string name = SharedName(drop.GetComponent<ItemDrop>());
                if (name != null && (!rank.TryGetValue(name, out int current) || index < current))
                {
                    rank[name] = index;
                }
            }
        }

        private static int BiomeRank(Heightmap.Biome biome)
        {
            for (int i = 0; i < Biomes.Length; i++)
            {
                if ((biome & Biomes[i].Biome) != 0)
                {
                    return i;
                }
            }
            return -1;
        }

        private static void CollectDrops(GameObject go, List<GameObject> drops, HashSet<GameObject> visited, int depth)
        {
            if (go == null || depth > 3 || !visited.Add(go))
            {
                return;
            }
            if (go.GetComponent<ItemDrop>() != null)
            {
                drops.Add(go);
                return;
            }
            foreach (CharacterDrop characterDrop in go.GetComponentsInChildren<CharacterDrop>(true))
            {
                foreach (CharacterDrop.Drop drop in characterDrop.m_drops)
                {
                    CollectDrops(drop?.m_prefab, drops, visited, depth + 1);
                }
            }
            foreach (Pickable pickable in go.GetComponentsInChildren<Pickable>(true))
            {
                CollectDrops(pickable.m_itemPrefab, drops, visited, depth + 1);
                CollectTable(pickable.m_extraDrops, drops, visited, depth);
            }
            foreach (PickableItem pickable in go.GetComponentsInChildren<PickableItem>(true))
            {
                CollectDrops(pickable.m_itemPrefab != null ? pickable.m_itemPrefab.gameObject : null, drops, visited, depth + 1);
                if (pickable.m_randomItemPrefabs != null)
                {
                    foreach (PickableItem.RandomItem random in pickable.m_randomItemPrefabs)
                    {
                        CollectDrops(random.m_itemPrefab != null ? random.m_itemPrefab.gameObject : null, drops, visited, depth + 1);
                    }
                }
            }
            foreach (DropOnDestroyed dropOnDestroyed in go.GetComponentsInChildren<DropOnDestroyed>(true))
            {
                CollectTable(dropOnDestroyed.m_dropWhenDestroyed, drops, visited, depth);
            }
            foreach (MineRock rock in go.GetComponentsInChildren<MineRock>(true))
            {
                CollectTable(rock.m_dropItems, drops, visited, depth);
            }
            foreach (MineRock5 rock in go.GetComponentsInChildren<MineRock5>(true))
            {
                CollectTable(rock.m_dropItems, drops, visited, depth);
            }
            foreach (TreeBase tree in go.GetComponentsInChildren<TreeBase>(true))
            {
                CollectTable(tree.m_dropWhenDestroyed, drops, visited, depth);
                CollectDrops(tree.m_logPrefab, drops, visited, depth + 1);
            }
            foreach (TreeLog log in go.GetComponentsInChildren<TreeLog>(true))
            {
                CollectTable(log.m_dropWhenDestroyed, drops, visited, depth);
                CollectDrops(log.m_subLogPrefab, drops, visited, depth + 1);
            }
            foreach (Destructible destructible in go.GetComponentsInChildren<Destructible>(true))
            {
                CollectDrops(destructible.m_spawnWhenDestroyed, drops, visited, depth + 1);
            }
        }

        private static void CollectTable(DropTable table, List<GameObject> drops, HashSet<GameObject> visited, int depth)
        {
            if (table?.m_drops == null)
            {
                return;
            }
            foreach (DropTable.DropData drop in table.m_drops)
            {
                CollectDrops(drop.m_item, drops, visited, depth + 1);
            }
        }

        private static void SetIcon(StorageItem item, Sprite sprite)
        {
            try
            {
                if (sprite == null || sprite.texture == null)
                {
                    return;
                }
                Texture2D texture = sprite.texture;
                Rect rect = sprite.textureRect;
                item.IconTexture = texture;
                item.IconUv = new Rect(rect.x / texture.width, rect.y / texture.height, rect.width / texture.width, rect.height / texture.height);
            }
            catch
            {
                item.IconTexture = null;
            }
        }
    }
}
