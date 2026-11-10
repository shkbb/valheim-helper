using System.Collections.Generic;
using UnityEngine;

namespace ValheimHelper.Map
{
    internal enum MapCategory
    {
        Bosses,
        Traders,
        Dungeons,
        Special,
        Runestones,
        Camps,
        Resources,
        Ships,
        Ruins,
        Other,
    }

    internal sealed class MapType
    {
        public string Prefab;
        public MapCategory Category;
        // Game localization keys (without '$'); when all resolve, they replace Fallback.
        public string[] LocKeys;
        public string Fallback;
        // Item prefabs whose inventory icon represents the place.
        public string[] IconItems;
        public Minimap.PinType? Pin;

        private string m_name;
        private string m_searchKey;

        internal string Name
        {
            get
            {
                if (m_name != null)
                {
                    return m_name;
                }
                string name = MapCatalog.Localize(LocKeys) ?? Fallback;
                if (Localization.instance != null)
                {
                    m_name = name;
                }
                return name;
            }
        }

        internal string SearchKey
        {
            get
            {
                if (m_searchKey == null)
                {
                    string items = MapCatalog.ItemNames(IconItems);
                    string key = (Name + " " + Prefab + " " + MapCatalog.CategoryName(Category) + " " + items).ToLowerInvariant();
                    if (items.Length == 0 || ObjectDB.instance != null)
                    {
                        m_searchKey = key;
                    }
                    return key;
                }
                return m_searchKey;
            }
        }
    }

    // Every location prefab of Valheim 1.0.12 (taken from the game's SoftRef manifest) with a category,
    // a name and an icon. Names come from the game's own localization where such a key exists, so they
    // match what the player sees in game; the rest are descriptive Ukrainian names.
    internal static class MapCatalog
    {
        internal static readonly MapCategory[] Categories = (MapCategory[])System.Enum.GetValues(typeof(MapCategory));

        private static readonly Dictionary<string, MapType> s_types = new Dictionary<string, MapType>();

        static MapCatalog()
        {
            Minimap.PinType boss = Minimap.PinType.Boss;
            Add(MapCategory.Bosses, Lang.T("Ейктюр"), Keys("enemy_eikthyr"), boss, null, "Eikthyrnir");
            Add(MapCategory.Bosses, Lang.T("Прадавній"), Keys("enemy_gdking"), boss, null, "GDKing");
            Add(MapCategory.Bosses, Lang.T("Кістянка"), Keys("enemy_bonemass"), boss, null, "Bonemass");
            Add(MapCategory.Bosses, Lang.T("Мати"), Keys("enemy_dragon"), boss, null, "Dragonqueen");
            Add(MapCategory.Bosses, Lang.T("Яґлут"), Keys("enemy_goblinking"), boss, null, "GoblinKing");
            Add(MapCategory.Bosses, Lang.T("Королева — Заражена цитадель"), Keys("enemy_seekerqueen", "location_dvergrboss"), boss, null, "Mistlands_DvergrBossEntrance1");
            Add(MapCategory.Bosses, Lang.T("Батько"), Keys("enemy_fader"), boss, null, "FaderLocation");
            Add(MapCategory.Bosses, Lang.T("В’язниця"), Keys("location_dnbossroomnew"), boss, null, "DN_Bossroom");

            Add(MapCategory.Traders, Lang.T("Гальдор"), Keys("npc_haldor"), null, Items("Coins"), "Vendor_BlackForest");
            Add(MapCategory.Traders, Lang.T("Гільдір"), Keys("npc_hildir"), null, Items("Coins"), "Hildir_camp");
            Add(MapCategory.Traders, Lang.T("Болотяна відьма"), Keys("npc_bogwitch"), null, Items("Coins"), "BogWitch_Camp");

            Minimap.PinType cave = Minimap.PinType.Icon4;
            Add(MapCategory.Dungeons, Lang.T("Поховальні"), Keys("location_forestcrypt"), cave, Items("BoneFragments"), "Crypt2", "Crypt3", "Crypt4", "HalfBurried_ForestCrypt");
            Add(MapCategory.Dungeons, Lang.T("Трольська печера"), Keys("location_forestcave"), cave, Items("TrophyForestTroll"), "TrollCave02");
            Add(MapCategory.Dungeons, Lang.T("Затоплені склепи"), Keys("location_sunkencrypt"), cave, Items("WitheredBone"), "SunkenCrypt4");
            Add(MapCategory.Dungeons, Lang.T("Морозні печери"), Keys("location_mountaincave"), cave, Items("TrophyCultist"), "MountainCave02");
            Add(MapCategory.Dungeons, Lang.T("Заражена копальня"), Keys("location_dvergrtown"), cave, Items("BlackCore"), "Mistlands_DvergrTownEntrance1", "Mistlands_DvergrTownEntrance2");
            Add(MapCategory.Dungeons, Lang.T("Обвуглена фортеця"), Keys("charredfortress"), cave, Items("TrophyCharredMelee"), "CharredFortress");
            Add(MapCategory.Dungeons, Lang.T("Зотліла поховальня"), Keys("hud_pin_hildir1"), Minimap.PinType.Hildir1, null, "Hildir_crypt");
            Add(MapCategory.Dungeons, Lang.T("Завивальна печера"), Keys("hud_pin_hildir2"), Minimap.PinType.Hildir2, null, "Hildir_cave");
            Add(MapCategory.Dungeons, Lang.T("Запечатана вежа"), Keys("hud_pin_hildir3"), Minimap.PinType.Hildir3, null, "Hildir_plainsfortress");
            Add(MapCategory.Dungeons, Lang.T("Ведмежий барліг"), Keys("location_bearcave"), cave, Items("TrophyBjorn"), "BearCave");
            Add(MapCategory.Dungeons, Lang.T("Смердючий отвір"), Keys("location_morgenhole"), cave, Items("TrophyMorgen"), "MorgenHole1", "MorgenHole2", "MorgenHole3");
            Add(MapCategory.Dungeons, Lang.T("Провал"), null, cave, null, "TheHole01");
            Add(MapCategory.Dungeons, Lang.T("Найтемніший провал"), null, cave, null, "TheDarkestHole");

            Minimap.PinType star = Minimap.PinType.Icon0;
            Add(MapCategory.Special, Lang.T("Стартовий храм"), null, star, null, "StartTemple");
            Add(MapCategory.Special, Lang.T("Давня кузня"), null, Minimap.PinType.Icon2, null, "AncientUpgradeStation");
            Add(MapCategory.Special, Lang.T("Кам'яне коло"), null, star, null, "StoneCircle");
            Add(MapCategory.Special, Lang.T("Кам'яне коло Рівнин"), null, star, null, Range("StoneHenge", 1, 6));
            Add(MapCategory.Special, Lang.T("Загадкова місцина"), null, star, null, "PlaceofMystery1", "PlaceofMystery2", "PlaceofMystery3");
            Add(MapCategory.Special, Lang.T("Галявина з великим каменем"), null, star, null, "BigRockClearing");
            Add(MapCategory.Special, Lang.T("Дороговказ"), null, star, null, "Waymarker01", "Waymarker02");
            Add(MapCategory.Special, Lang.T("Місце давньої битви"), null, star, null, "CombatRuin01");
            Add(MapCategory.Special, Lang.T("Дольмен"), null, star, null, "Dolmen01", "Dolmen02", "Dolmen03");
            Add(MapCategory.Special, Lang.T("Північний меморіал"), null, star, null, "NorthMemorialPlace");
            Add(MapCategory.Special, Lang.T("Фімбуль"), null, star, null, "FimbulLocation01");
            Add(MapCategory.Special, Lang.T("Моркборґ"), null, star, null, "MorkBorg");
            Add(MapCategory.Special, Lang.T("Гаряче джерело"), null, star, null, "HotSpring1", "HotSpring2", "HotSpring3");
            Add(MapCategory.Special, Lang.T("Крижане озерце"), null, star, null, "IcePond1");

            Minimap.PinType rune = Minimap.PinType.Icon3;
            Add(MapCategory.Runestones, Lang.T("Рунний камінь (Левади)"), null, rune, null, "Runestone_Meadows");
            Add(MapCategory.Runestones, Lang.T("Рунний камінь (кабани)"), null, rune, null, "Runestone_Boars");
            Add(MapCategory.Runestones, Lang.T("Рунний камінь (Чорнолісся)"), null, rune, null, "Runestone_BlackForest");
            Add(MapCategory.Runestones, Lang.T("Рунний камінь (грейдварфи)"), null, rune, null, "Runestone_Greydwarfs");
            Add(MapCategory.Runestones, Lang.T("Рунний камінь (Болото)"), null, rune, null, "Runestone_Swamps");
            Add(MapCategory.Runestones, Lang.T("Рунний камінь (драуґри)"), null, rune, null, "Runestone_Draugr");
            Add(MapCategory.Runestones, Lang.T("Рунний камінь (Гори)"), null, rune, null, "Runestone_Mountains");
            Add(MapCategory.Runestones, Lang.T("Рунний камінь (Рівнини)"), null, rune, null, "Runestone_Plains");
            Add(MapCategory.Runestones, Lang.T("Рунний камінь (Імлисті землі)"), null, rune, null, "Runestone_Mistlands");
            Add(MapCategory.Runestones, Lang.T("Рунний камінь (Попелисті землі)"), null, rune, null, "Runestone_Ashlands");
            Add(MapCategory.Runestones, Lang.T("Рунний камінь (Далека північ)"), null, rune, null, "Runestone_DeepNorth");
            Add(MapCategory.Runestones, Lang.T("Рунний камінь (дракони)"), null, rune, null, "DrakeLorestone");

            Minimap.PinType house = Minimap.PinType.Icon1;
            Add(MapCategory.Camps, Lang.T("Табір грейдварфів"), null, house, Items("TrophyGreydwarf"), "Greydwarf_camp1");
            Add(MapCategory.Camps, Lang.T("Поселення фулінґів"), null, house, Items("GoblinTotem", "TrophyGoblin"), "GoblinCamp2", "GoblinCamp2_1");
            Add(MapCategory.Camps, Lang.T("Хатина фулінґів"), null, house, Items("TrophyGoblin"), "GoblinHut01", "GoblinHut02", "GoblinHut03");
            Add(MapCategory.Camps, Lang.T("Покинуте село"), null, house, null, "WoodVillage1", "WoodVillage2");
            Add(MapCategory.Camps, Lang.T("Покинута ферма"), null, house, null, "WoodFarm1");
            Add(MapCategory.Camps, Lang.T("Болотяна хатина"), null, house, Items("TrophyDraugr"),
                "SwampHut1", "SwampHut1_1", "SwampHut2", "SwampHut2_1", "SwampHut3", "SwampHut3_1", "SwampHut4", "SwampHut5");
            Add(MapCategory.Camps, Lang.T("Північне поселення"), null, house, null, "NorthVillage");
            Add(MapCategory.Camps, Lang.T("Північна хатина"), null, house, null, "DN_hut01");
            Add(MapCategory.Camps, Lang.T("Табір лісорубів"), null, house, null, "LumberCamp");

            Minimap.PinType resource = Minimap.PinType.Icon2;
            Add(MapCategory.Resources, Lang.T("Смоляна яма"), null, resource, Items("Tar"), "TarPit1", "TarPit1_1", "TarPit2", "TarPit2_1", "TarPit3", "TarPit3_1");
            Add(MapCategory.Resources, Lang.T("Вогняна нора суртлінґів"), null, resource, Items("SurtlingCore"), "FireHole");
            Add(MapCategory.Resources, Lang.T("Заражене дерево"), null, resource, Items("Guck"), "InfestedTree01");
            Add(MapCategory.Resources, Lang.T("Гніздо дракона"), null, resource, Items("DragonEgg"), "DrakeNest01");
            Add(MapCategory.Resources, Lang.T("Останки велетня"), null, resource, Items("Softtissue", "BlackMarble"), "Mistlands_Giant1", "Mistlands_Giant2");
            Add(MapCategory.Resources, Lang.T("Розкопки двергрів"), null, resource, Items("BlackMarble"), "Mistlands_Excavation1", "Mistlands_Excavation2", "Mistlands_Excavation3");
            Add(MapCategory.Resources, Lang.T("Гніздо стерв'ятника"), null, resource, Items("VoltureEgg"), "VoltureNest");
            Add(MapCategory.Resources, Lang.T("Сірчана арка"), null, resource, Items("SulfurStone"), "SulfurArch");
            Add(MapCategory.Resources, Lang.T("Обвуглений камінь"), null, resource, null, "CharredStone_Spawner");
            Add(MapCategory.Resources, Lang.T("Жила вогняного металу (лавовий левіафан)"), null, resource, Items("FlametalOreNew", "FlametalOre"), "LeviathanLava");

            Minimap.PinType ship = Minimap.PinType.Icon2;
            Add(MapCategory.Ships, Lang.T("Уламки корабля"), null, ship, null, "ShipWreck01", "ShipWreck02", "ShipWreck03", "ShipWreck04");
            Add(MapCategory.Ships, Lang.T("Стоянка корабля"), null, ship, null, "ShipSetting01", "ShipSetting02", "ShipSetting03");
            Add(MapCategory.Ships, Lang.T("Вмерзлий корабель"), null, ship, null, "FrozenShip01_DN", "FrozenShip02_DN", "FrozenShip03_DN");
            Add(MapCategory.Ships, Lang.T("Уламки корабля (Далека північ)"), null, ship, null, "ShipWreck01_DN", "ShipWreck02_DN");

            Add(MapCategory.Ruins, Lang.T("Руїни"), null, house, null, "Ruin1", "Ruin2", "Ruin3");
            Add(MapCategory.Ruins, Lang.T("Руїни кам'яної вежі"), null, house, null,
                "StoneTowerRuins03", "StoneTowerRuins04", "StoneTowerRuins05", "StoneTowerRuins05_leet", "StoneTowerRuins07", "StoneTowerRuins07_sunk",
                "StoneTowerRuins08", "StoneTowerRuins08_sunk", "StoneTowerRuins09", "StoneTowerRuins09_sunk", "StoneTowerRuins10", "StoneTowerRuins10_sunk");
            Add(MapCategory.Ruins, Lang.T("Кам'яна вежа"), null, house, null, "StoneTower1", "StoneTower3");
            Add(MapCategory.Ruins, Lang.T("Кам'яний будинок"), null, house, null, "StoneHouse1_heath", "StoneHouse2_heath", "StoneHouse3", "StoneHouse4", "StoneHouse5_heath");
            Add(MapCategory.Ruins, Lang.T("Дерев'яний будинок"), null, house, null, Range("WoodHouse", 1, 13));
            Add(MapCategory.Ruins, Lang.T("Болотяні руїни"), null, house, null, "SwampRuin1", "SwampRuin2");
            Add(MapCategory.Ruins, Lang.T("Болотяний колодязь"), null, house, null, "SwampWell1");
            Add(MapCategory.Ruins, Lang.T("Могила"), null, house, null, "Grave1");
            Add(MapCategory.Ruins, Lang.T("Гірський колодязь"), null, house, null, "MountainWell1");
            Add(MapCategory.Ruins, Lang.T("Гірська могила"), null, house, null, "MountainGrave01");
            Add(MapCategory.Ruins, Lang.T("Покинута хижа"), null, house, null, "AbandonedLogCabin02", "AbandonedLogCabin03", "AbandonedLogCabin04");
            Add(MapCategory.Ruins, Lang.T("Вартова вежа двергрів"), null, house, null,
                "Mistlands_GuardTower1_new", "Mistlands_GuardTower1_ruined_new", "Mistlands_GuardTower1_ruined_new2",
                "Mistlands_GuardTower2_new", "Mistlands_GuardTower3_new", "Mistlands_GuardTower3_ruined_new");
            Add(MapCategory.Ruins, Lang.T("Гавань двергрів"), null, house, null, "Mistlands_Harbour1");
            Add(MapCategory.Ruins, Lang.T("Маяк двергрів"), null, house, null, "Mistlands_Lighthouse1_new");
            Add(MapCategory.Ruins, Lang.T("Дорожній стовп двергрів"), null, house, null, "Mistlands_RoadPost1");
            Add(MapCategory.Ruins, Lang.T("Скельний шпиль"), null, house, null, "Mistlands_RockSpire1");
            Add(MapCategory.Ruins, Lang.T("Статуя"), null, house, null, "Mistlands_Statue1", "Mistlands_Statue2", "Mistlands_StatueGroup1");
            Add(MapCategory.Ruins, Lang.T("Мечі в землі"), null, house, null, "Mistlands_Swords1", "Mistlands_Swords2", "Mistlands_Swords3");
            Add(MapCategory.Ruins, Lang.T("Віадук"), null, house, null, "Mistlands_Viaduct1", "Mistlands_Viaduct2");
            Add(MapCategory.Ruins, Lang.T("Обвуглені руїни"), null, house, null, "CharredRuins1", "CharredRuins2", "CharredRuins3", "CharredRuins4");
            Add(MapCategory.Ruins, Lang.T("Обвуглена вежа"), null, house, null, "CharredTowerRuins1", "CharredTowerRuins1_dvergr", "CharredTowerRuins2", "CharredTowerRuins3");
            Add(MapCategory.Ruins, Lang.T("Руїни Попелистих земель"), null, house, null, "AshlandRuins");
            Add(MapCategory.Ruins, Lang.T("Руїни фортеці"), null, house, null, "FortressRuins");
            Add(MapCategory.Ruins, Lang.T("Скам'янілий троль"), null, house, null, "DN_gammeltrollFrac01", "DN_gammeltrollFrac02");
        }

        private static string[] Keys(params string[] keys)
        {
            return keys;
        }

        private static string[] Items(params string[] items)
        {
            return items;
        }

        private static string[] Range(string prefix, int from, int to)
        {
            string[] result = new string[to - from + 1];
            for (int i = from; i <= to; i++)
            {
                result[i - from] = prefix + i;
            }
            return result;
        }

        private static void Add(MapCategory category, string fallback, string[] locKeys, Minimap.PinType? pin, string[] icons, params string[] prefabs)
        {
            foreach (string prefab in prefabs)
            {
                s_types[prefab] = new MapType
                {
                    Prefab = prefab,
                    Category = category,
                    Fallback = fallback,
                    LocKeys = locKeys,
                    Pin = pin,
                    IconItems = icons,
                };
            }
        }

        internal static IEnumerable<MapType> Known => s_types.Values;

        // Unknown prefabs (dev locations, future game content) still show up, under "Інше".
        internal static MapType Get(string prefab)
        {
            if (!s_types.TryGetValue(prefab, out MapType type))
            {
                type = new MapType { Prefab = prefab, Category = MapCategory.Other, Fallback = prefab };
                s_types[prefab] = type;
            }
            return type;
        }

        // The game's names of the items a place gives (its icon items), for the search.
        internal static string ItemNames(string[] prefabs)
        {
            if (prefabs == null || ObjectDB.instance == null || Localization.instance == null)
            {
                return "";
            }
            List<string> names = new List<string>();
            foreach (string prefab in prefabs)
            {
                GameObject go = ObjectDB.instance.GetItemPrefab(prefab);
                ItemDrop item = go != null ? go.GetComponent<ItemDrop>() : null;
                if (item != null)
                {
                    names.Add(Localization.instance.Localize(item.m_itemData.m_shared.m_name));
                }
            }
            return string.Join(" ", names.ToArray());
        }

        internal static string Localize(string[] keys)
        {
            if (keys == null || keys.Length == 0 || Localization.instance == null)
            {
                return null;
            }
            string[] parts = new string[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                string text = Localization.instance.Localize("$" + keys[i]);
                if (string.IsNullOrEmpty(text) || text.StartsWith("[") || text.StartsWith("$") || text == keys[i])
                {
                    return null;
                }
                parts[i] = text;
            }
            return string.Join(" — ", parts);
        }

        internal static string CategoryName(MapCategory category)
        {
            switch (category)
            {
                case MapCategory.Bosses: return Lang.T("Боси");
                case MapCategory.Traders: return Lang.T("Торговці");
                case MapCategory.Dungeons: return Lang.T("Підземелля");
                case MapCategory.Special: return Lang.T("Особливі місця");
                case MapCategory.Runestones: return Lang.T("Рунні камені");
                case MapCategory.Camps: return Lang.T("Табори й поселення");
                case MapCategory.Resources: return Lang.T("Ресурси");
                case MapCategory.Ships: return Lang.T("Кораблі");
                case MapCategory.Ruins: return Lang.T("Руїни й будівлі");
                default: return Lang.T("Інше");
            }
        }

        internal static Color CategoryColor(MapCategory category)
        {
            switch (category)
            {
                case MapCategory.Bosses: return new Color(1f, 0.3f, 0.3f);
                case MapCategory.Traders: return new Color(1f, 0.85f, 0.2f);
                case MapCategory.Dungeons: return new Color(0.75f, 0.45f, 1f);
                case MapCategory.Special: return new Color(0.3f, 0.9f, 1f);
                case MapCategory.Runestones: return new Color(0.6f, 0.75f, 1f);
                case MapCategory.Camps: return new Color(1f, 0.55f, 0.2f);
                case MapCategory.Resources: return new Color(0.45f, 1f, 0.45f);
                case MapCategory.Ships: return new Color(0.85f, 0.65f, 0.45f);
                case MapCategory.Ruins: return new Color(0.8f, 0.8f, 0.8f);
                default: return new Color(0.6f, 0.6f, 0.6f);
            }
        }

        internal static Minimap.PinType CategoryPin(MapCategory category)
        {
            switch (category)
            {
                case MapCategory.Bosses: return Minimap.PinType.Boss;
                case MapCategory.Dungeons: return Minimap.PinType.Icon4;
                case MapCategory.Special: return Minimap.PinType.Icon0;
                case MapCategory.Resources:
                case MapCategory.Ships: return Minimap.PinType.Icon2;
                case MapCategory.Traders:
                case MapCategory.Camps:
                case MapCategory.Ruins: return Minimap.PinType.Icon1;
                default: return Minimap.PinType.Icon3;
            }
        }

        // Layers shown before the player changes anything: the places people usually look for.
        internal static bool DefaultVisible(MapCategory category)
        {
            return category == MapCategory.Bosses || category == MapCategory.Traders || category == MapCategory.Dungeons;
        }
    }
}
