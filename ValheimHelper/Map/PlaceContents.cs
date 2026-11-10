using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace ValheimHelper.Map
{
    // What each kind of place holds, read from the game's own prefabs: items lying there or on altars, what can be
    // picked, the loot of its chests, the creatures it spawns (and so their drops), bosses of its altars, what its
    // trader sells, where its vegvisirs point, and for dungeons everything in the rooms of its themes.
    // This is how "фрагмент Дирнвіна" finds the Mysterious Locations without anybody listing them by hand: any item
    // the game puts in a place is found the same way, including content of future game updates.
    //
    // Location and room prefabs are asset bundles loaded on demand. They are read only when the map search is first
    // opened, one at a time and asynchronously (LoadAsync/Release, as the game does), so a frame never waits for a
    // bundle; each frame looks at one loaded prefab at most. The result is cached next to the mod and rebuilt only when
    // the game files change, so usually this happens once.
    internal static class PlaceContents
    {
        internal sealed class Content
        {
            // Shared item names ($item_…).
            public readonly HashSet<string> Items = new HashSet<string>();
            public readonly HashSet<string> Sold = new HashSet<string>();
            // Creature prefab names.
            public readonly HashSet<string> Creatures = new HashSet<string>();
            // Location prefab names its vegvisirs reveal.
            public readonly HashSet<string> Points = new HashSet<string>();
            public int Themes;

            internal string SearchText;
            internal List<string> Lines;
            // Where the location puts what the map can show (null for dungeon rooms).
            internal Layout Layout;
        }

        // What a location's prefab places, for LocationObjects: its objects that give something (as they sit in the
        // prefab) and the game's random choices deciding which of them stay (RandomSpawn and RandomObject, in the
        // order ZoneSystem.SpawnLocation draws them).
        internal sealed class Layout
        {
            public readonly List<Chance> Spawns = new List<Chance>();
            public readonly List<Choice> Choices = new List<Choice>();
            public readonly List<Point> Points = new List<Point>();
        }

        // A RandomSpawn: kept with this chance, and only in these conditions.
        internal sealed class Chance
        {
            public float Percent;
            public int Biome;
            public int MinY;
            public int MaxY;
            public bool NoLava;
            public Vector3 Local;
        }

        // A RandomObject: keeps one of its entries by weight (or none, when its conditions fail).
        internal sealed class Choice
        {
            public float[] Weights;
            public int Biome;
            public int MinY;
            public int MaxY;
            public bool NoLava;
            public Vector3 Local;
        }

        internal sealed class Point
        {
            public string Prefab;
            public Vector3 Local;
            // RandomSpawns it is under: all must be kept.
            public int[] Spawns;
            // RandomObjects it is under and the entry it belongs to (-1: any entry kept).
            public int[] Choices;
            public int[] Entries;
        }

        private const int CacheFormat = 3;
        // A bundle that has not loaded in this time is skipped.
        private const float LoadTimeoutSec = 30f;

        internal static int Version;
        internal static bool Ready;

        private static readonly Dictionary<string, Content> s_byPlace = new Dictionary<string, Content>();
        private static readonly Dictionary<int, Content> s_rooms = new Dictionary<int, Content>();
        private static readonly HashSet<string> s_roomPrefabs = new HashSet<string>();
        private static Queue<ZoneSystem.ZoneLocation> s_locations;
        private static Queue<DungeonDB.RoomData> s_roomQueue;
        private static ZoneSystem s_zoneSystem;
        private static bool s_loggedError;
        private static bool s_requested;
        // The location or room whose prefab is loading now.
        private static ZoneSystem.ZoneLocation s_loadingLocation;
        private static DungeonDB.RoomData s_loadingRoom;
        private static float s_loadingSince;

        // Called when the map search is opened: from then on the places are read in the background.
        internal static void Request()
        {
            s_requested = true;
        }

        // Creature prefab name -> shared names it drops; filled lazily.
        private static readonly Dictionary<string, HashSet<string>> s_drops = new Dictionary<string, HashSet<string>>();
        // How many kinds of place hold an item: rarer items are listed first.
        private static readonly Dictionary<string, int> s_itemPlaces = new Dictionary<string, int>();

        private static string CachePath => Path.Combine(Path.Combine(Loader.Dir ?? ".", "cache"), "places.txt");

        // Location and room prefabs still to read (0 when ready or read from the cache).
        internal static int Pending => Ready ? 0 : (s_locations?.Count ?? 0) + (s_roomQueue?.Count ?? 0) + (s_loadingLocation != null || s_loadingRoom != null ? 1 : 0);

        internal static Content Get(string prefab)
        {
            return Ready && prefab != null && s_byPlace.TryGetValue(prefab, out Content content) ? content : null;
        }

        internal static void Tick()
        {
            ZoneSystem zones = ZoneSystem.instance;
            if (zones == null || ObjectDB.instance == null || ZNetScene.instance == null || Localization.instance == null)
            {
                return;
            }
            if (!ReferenceEquals(zones, s_zoneSystem))
            {
                s_zoneSystem = zones;
                Start(zones);
            }
            if (Ready || !s_requested)
            {
                return;
            }
            try
            {
                if (s_loadingRoom != null)
                {
                    if (s_loadingRoom.m_prefab.IsLoaded)
                    {
                        DungeonDB.RoomData room = s_loadingRoom;
                        s_loadingRoom = null;
                        ScanRoom(room);
                    }
                    else if (Time.unscaledTime - s_loadingSince > LoadTimeoutSec)
                    {
                        s_loadingRoom.m_prefab.Release();
                        s_loadingRoom = null;
                    }
                    return;
                }
                if (s_loadingLocation != null)
                {
                    if (s_loadingLocation.m_prefab.IsLoaded)
                    {
                        ZoneSystem.ZoneLocation location = s_loadingLocation;
                        s_loadingLocation = null;
                        ScanLocation(location);
                    }
                    else if (Time.unscaledTime - s_loadingSince > LoadTimeoutSec)
                    {
                        s_loadingLocation.m_prefab.Release();
                        s_loadingLocation = null;
                    }
                    return;
                }
                s_loadingSince = Time.unscaledTime;
                if (s_roomQueue != null && s_roomQueue.Count > 0)
                {
                    s_loadingRoom = s_roomQueue.Dequeue();
                    s_loadingRoom.m_prefab.LoadAsync();
                }
                else if (s_locations != null && s_locations.Count > 0)
                {
                    s_loadingLocation = s_locations.Dequeue();
                    s_loadingLocation.m_prefab.LoadAsync();
                }
                else
                {
                    Finish();
                    SaveCache();
                }
            }
            catch (Exception e)
            {
                if (!s_loggedError)
                {
                    s_loggedError = true;
                    Log.Error(e);
                }
            }
        }

        private static void Start(ZoneSystem zones)
        {
            Ready = false;
            // A bundle still loading for the previous world is let go, so it does not stay in memory.
            if (s_loadingLocation != null)
            {
                s_loadingLocation.m_prefab.Release();
                s_loadingLocation = null;
            }
            if (s_loadingRoom != null)
            {
                s_loadingRoom.m_prefab.Release();
                s_loadingRoom = null;
            }
            s_byPlace.Clear();
            s_rooms.Clear();
            s_roomPrefabs.Clear();
            s_drops.Clear();
            s_itemPlaces.Clear();
            if (LoadCache())
            {
                Finish();
                return;
            }
            s_locations = new Queue<ZoneSystem.ZoneLocation>();
            foreach (ZoneSystem.ZoneLocation location in zones.m_locations)
            {
                if (location != null && location.m_prefab.IsValid && !string.IsNullOrEmpty(location.m_prefabName) && !s_byPlace.ContainsKey(location.m_prefabName))
                {
                    s_byPlace[location.m_prefabName] = new Content();
                    s_locations.Enqueue(location);
                }
            }
            s_roomQueue = new Queue<DungeonDB.RoomData>();
            if (DungeonDB.instance != null)
            {
                foreach (DungeonDB.RoomData room in DungeonDB.GetRooms())
                {
                    if (room != null && room.m_enabled && room.m_prefab.IsValid)
                    {
                        s_roomQueue.Enqueue(room);
                    }
                }
            }
            Log.Info($"Place contents: scanning {s_locations.Count} kinds of places and {s_roomQueue.Count} dungeon rooms");
        }

        private static void ScanLocation(ZoneSystem.ZoneLocation location)
        {
            Content content = s_byPlace[location.m_prefabName];
            try
            {
                GameObject asset = location.m_prefab.Asset;
                if (asset != null)
                {
                    Scan(asset, content);
                    foreach (DungeonGenerator dungeon in asset.GetComponentsInChildren<DungeonGenerator>(true))
                    {
                        content.Themes |= (int)dungeon.m_themes;
                    }
                    content.Layout = ReadLayout(asset);
                }
            }
            finally
            {
                location.m_prefab.Release();
            }
        }

        // The prefab as ZoneSystem.SpawnLocation sees it: the same component lists (in the same order), positions
        // relative to the location's centre. The inside of a dungeon (far above the ground) is not on the map.
        private static Layout ReadLayout(GameObject asset)
        {
            Layout layout = new Layout();
            Transform root = asset.transform;
            Quaternion toLocal = Quaternion.Inverse(root.rotation);
            RandomSpawn[] spawns = Utils.GetEnabledComponentsInChildren<RandomSpawn>(asset);
            foreach (RandomSpawn spawn in spawns)
            {
                layout.Spawns.Add(new Chance
                {
                    Percent = spawn.m_chanceToSpawn,
                    Biome = (int)spawn.m_requireBiome,
                    MinY = spawn.m_minElevation,
                    MaxY = spawn.m_maxElevation,
                    NoLava = spawn.m_notInLava,
                    Local = toLocal * (spawn.transform.position - root.position),
                });
            }
            RandomObject[] choices = Utils.GetEnabledComponentsInChildren<RandomObject>(asset);
            foreach (RandomObject choice in choices)
            {
                float[] weights = new float[choice.m_objects.Count];
                for (int i = 0; i < weights.Length; i++)
                {
                    weights[i] = choice.m_objects[i] != null ? choice.m_objects[i].m_weight : 0f;
                }
                layout.Choices.Add(new Choice
                {
                    Weights = weights,
                    Biome = (int)choice.m_requireBiome,
                    MinY = choice.m_minElevation,
                    MaxY = choice.m_maxElevation,
                    NoLava = choice.m_notInLava,
                    Local = toLocal * (choice.transform.position - root.position),
                });
            }
            foreach (ZNetView view in Utils.GetEnabledComponentsInChildren<ZNetView>(asset))
            {
                if (!view.gameObject.activeSelf || !Wanted(view.gameObject))
                {
                    continue;
                }
                Vector3 local = toLocal * (view.transform.position - root.position);
                if (Mathf.Abs(local.y) > 1000f)
                {
                    continue;
                }
                List<int> needSpawns = new List<int>();
                for (int i = 0; i < spawns.Length; i++)
                {
                    if (view.transform.IsChildOf(spawns[i].transform))
                    {
                        needSpawns.Add(i);
                    }
                }
                List<int> needChoices = new List<int>();
                List<int> entries = new List<int>();
                for (int i = 0; i < choices.Length; i++)
                {
                    if (!view.transform.IsChildOf(choices[i].transform))
                    {
                        continue;
                    }
                    int entry = -1;
                    for (int e = 0; e < choices[i].m_objects.Count; e++)
                    {
                        GameObject target = choices[i].m_objects[e]?.m_object;
                        if (target != null && view.transform.IsChildOf(target.transform))
                        {
                            entry = e;
                            break;
                        }
                    }
                    needChoices.Add(i);
                    entries.Add(entry);
                }
                layout.Points.Add(new Point
                {
                    Prefab = Utils.GetPrefabName(view.gameObject),
                    Local = local,
                    Spawns = needSpawns.ToArray(),
                    Choices = needChoices.ToArray(),
                    Entries = entries.ToArray(),
                });
            }
            return layout;
        }

        // Objects that can give something or bring creatures; walls and decorations are left out.
        private static bool Wanted(GameObject go)
        {
            if (go.GetComponent<Container>() != null || WorldVegetation.IsStack(go))
            {
                return true;
            }
            if (go.GetComponent<Piece>() != null || go.GetComponent<Character>() != null)
            {
                return false;
            }
            return go.GetComponent<Pickable>() != null || go.GetComponent<PickableItem>() != null || go.GetComponent<MineRock>() != null
                || go.GetComponent<MineRock5>() != null || go.GetComponent<Destructible>() != null || go.GetComponent<ItemDrop>() != null
                || go.GetComponent<CreatureSpawner>() != null || go.GetComponent<SpawnArea>() != null || go.GetComponent<TreeBase>() != null
                || go.GetComponent<TreeLog>() != null || go.GetComponent<Beehive>() != null || go.GetComponent<ResourceRoot>() != null
                || go.GetComponent<LootSpawner>() != null;
        }

        internal static Layout LayoutOf(string place)
        {
            return Ready && place != null && s_byPlace.TryGetValue(place, out Content content) ? content.Layout : null;
        }

        // Prefabs of what dungeon and camp rooms hold.
        internal static IEnumerable<string> RoomPrefabs()
        {
            return Ready ? s_roomPrefabs : (IEnumerable<string>)new string[0];
        }

        internal static IEnumerable<Layout> Layouts()
        {
            foreach (Content content in s_byPlace.Values)
            {
                if (content.Layout != null)
                {
                    yield return content.Layout;
                }
            }
        }

        private static void ScanRoom(DungeonDB.RoomData room)
        {
            try
            {
                GameObject asset = room.m_prefab.Asset;
                if (asset == null)
                {
                    return;
                }
                int theme = (int)room.m_theme;
                if (!s_rooms.TryGetValue(theme, out Content content))
                {
                    content = new Content();
                    s_rooms[theme] = content;
                }
                Scan(asset, content);
                // rooms are laid out only when somebody comes near: their objects are shown as found then
                foreach (ZNetView view in asset.GetComponentsInChildren<ZNetView>(true))
                {
                    if (Wanted(view.gameObject))
                    {
                        s_roomPrefabs.Add(Utils.GetPrefabName(view.gameObject));
                    }
                }
            }
            finally
            {
                room.m_prefab.Release();
            }
        }

        // Everything a prefab hierarchy offers. Inactive children count too: they are the random variants.
        private static void Scan(GameObject root, Content content)
        {
            foreach (ItemDrop item in root.GetComponentsInChildren<ItemDrop>(true))
            {
                AddItem(content.Items, item);
            }
            foreach (Pickable pickable in root.GetComponentsInChildren<Pickable>(true))
            {
                AddItem(content.Items, pickable.m_itemPrefab);
                AddTable(content.Items, pickable.m_extraDrops);
            }
            foreach (Container container in root.GetComponentsInChildren<Container>(true))
            {
                AddTable(content.Items, container.m_defaultItems);
            }
            foreach (DropOnDestroyed drop in root.GetComponentsInChildren<DropOnDestroyed>(true))
            {
                AddTable(content.Items, drop.m_dropWhenDestroyed);
            }
            foreach (MineRock rock in root.GetComponentsInChildren<MineRock>(true))
            {
                AddTable(content.Items, rock.m_dropItems);
            }
            foreach (MineRock5 rock in root.GetComponentsInChildren<MineRock5>(true))
            {
                AddTable(content.Items, rock.m_dropItems);
            }
            foreach (Beehive hive in root.GetComponentsInChildren<Beehive>(true))
            {
                AddItem(content.Items, hive.m_honeyItem);
            }
            foreach (CreatureSpawner spawner in root.GetComponentsInChildren<CreatureSpawner>(true))
            {
                AddCreature(content, spawner.m_creaturePrefab);
            }
            foreach (SpawnArea area in root.GetComponentsInChildren<SpawnArea>(true))
            {
                foreach (SpawnArea.SpawnData spawn in area.m_prefabs)
                {
                    AddCreature(content, spawn?.m_prefab);
                }
            }
            foreach (OfferingBowl bowl in root.GetComponentsInChildren<OfferingBowl>(true))
            {
                AddCreature(content, bowl.m_bossPrefab);
            }
            foreach (Character character in root.GetComponentsInChildren<Character>(true))
            {
                AddCreature(content, character.gameObject);
            }
            foreach (Trader trader in root.GetComponentsInChildren<Trader>(true))
            {
                foreach (Trader.TradeItem trade in trader.m_items)
                {
                    AddItem(content.Sold, trade?.m_prefab);
                }
            }
            foreach (Vegvisir vegvisir in root.GetComponentsInChildren<Vegvisir>(true))
            {
                foreach (Vegvisir.VegvisrLocation target in vegvisir.m_locations)
                {
                    if (target != null && !string.IsNullOrEmpty(target.m_locationName))
                    {
                        content.Points.Add(target.m_locationName);
                    }
                }
            }
        }

        private static void AddItem(HashSet<string> into, ItemDrop item)
        {
            string name = item?.m_itemData?.m_shared?.m_name;
            if (!string.IsNullOrEmpty(name))
            {
                into.Add(name);
            }
        }

        private static void AddItem(HashSet<string> into, GameObject prefab)
        {
            if (prefab == null)
            {
                return;
            }
            ItemDrop item = prefab.GetComponent<ItemDrop>();
            if (item != null)
            {
                AddItem(into, item);
                return;
            }
            // Some drops are pickables themselves (Lord Reto drops the Dyrnwyn hilt as Pickable_Swordpiece1).
            Pickable pickable = prefab.GetComponent<Pickable>();
            if (pickable != null && pickable.m_itemPrefab != null)
            {
                AddItem(into, pickable.m_itemPrefab.GetComponent<ItemDrop>());
            }
        }

        private static void AddTable(HashSet<string> into, DropTable table)
        {
            if (table?.m_drops == null)
            {
                return;
            }
            foreach (DropTable.DropData drop in table.m_drops)
            {
                AddItem(into, drop.m_item);
            }
        }

        private static void AddCreature(Content content, GameObject prefab)
        {
            if (prefab != null && prefab.GetComponent<Character>() != null)
            {
                // Instances inside prefabs keep the prefab's name, sometimes with " (1)" after it.
                string name = prefab.name;
                int cut = name.IndexOf(" (", StringComparison.Ordinal);
                content.Creatures.Add(cut > 0 ? name.Substring(0, cut) : name);
            }
        }

        private static void Finish()
        {
            foreach (KeyValuePair<string, Content> pair in s_byPlace)
            {
                if (pair.Value.Themes == 0)
                {
                    continue;
                }
                foreach (KeyValuePair<int, Content> room in s_rooms)
                {
                    if ((room.Key & pair.Value.Themes) != 0)
                    {
                        pair.Value.Items.UnionWith(room.Value.Items);
                        pair.Value.Creatures.UnionWith(room.Value.Creatures);
                        pair.Value.Sold.UnionWith(room.Value.Sold);
                        pair.Value.Points.UnionWith(room.Value.Points);
                    }
                }
            }
            s_itemPlaces.Clear();
            foreach (Content content in s_byPlace.Values)
            {
                foreach (string item in AllItems(content))
                {
                    s_itemPlaces[item] = (s_itemPlaces.TryGetValue(item, out int count) ? count : 0) + 1;
                }
                content.SearchText = null;
                content.Lines = null;
            }
            s_locations = null;
            s_roomQueue = null;
            Ready = true;
            Version++;
            Log.Info($"Place contents ready: {s_byPlace.Count} kinds of places");
        }

        internal static HashSet<string> DropsOf(string creature)
        {
            if (s_drops.TryGetValue(creature, out HashSet<string> drops))
            {
                return drops;
            }
            drops = new HashSet<string>();
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(creature) : null;
            CharacterDrop characterDrop = prefab != null ? prefab.GetComponent<CharacterDrop>() : null;
            if (characterDrop != null)
            {
                foreach (CharacterDrop.Drop drop in characterDrop.m_drops)
                {
                    AddItem(drops, drop?.m_prefab);
                }
            }
            s_drops[creature] = drops;
            return drops;
        }

        private static HashSet<string> AllItems(Content content)
        {
            HashSet<string> all = new HashSet<string>(content.Items);
            all.UnionWith(content.Sold);
            foreach (string creature in content.Creatures)
            {
                all.UnionWith(DropsOf(creature));
            }
            return all;
        }

        internal static string CreatureName(string prefab)
        {
            GameObject go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            Character character = go != null ? go.GetComponent<Character>() : null;
            return character != null ? Localization.instance.Localize(character.m_name) : prefab;
        }

        // Lowercase names of everything there (items, creatures, what is sold) for the map search.
        internal static string SearchText(string prefab)
        {
            Content content = Get(prefab);
            if (content == null)
            {
                return "";
            }
            if (content.SearchText == null)
            {
                StringBuilder text = new StringBuilder();
                foreach (string item in AllItems(content))
                {
                    text.Append(' ').Append(Localization.instance.Localize(item)).Append(' ').Append(item);
                }
                foreach (string creature in content.Creatures)
                {
                    text.Append(' ').Append(CreatureName(creature)).Append(' ').Append(creature);
                }
                content.SearchText = text.ToString().ToLowerInvariant();
            }
            return content.SearchText;
        }

        // What of a place matches the search: "Фрагмент вістря Дирнвіна" for "дирнвін"; null when only the name matches.
        internal static string MatchFor(string prefab, string query)
        {
            Content content = Get(prefab);
            if (content == null || string.IsNullOrEmpty(query))
            {
                return null;
            }
            foreach (string item in content.Items)
            {
                string name = Localization.instance.Localize(item);
                if (name.ToLowerInvariant().Contains(query) || item.ToLowerInvariant().Contains(query))
                {
                    return name;
                }
            }
            foreach (string item in content.Sold)
            {
                string name = Localization.instance.Localize(item);
                if (name.ToLowerInvariant().Contains(query) || item.ToLowerInvariant().Contains(query))
                {
                    return name + Lang.T(" (продає)");
                }
            }
            foreach (string creature in content.Creatures)
            {
                string creatureName = CreatureName(creature);
                if (creatureName.ToLowerInvariant().Contains(query) || creature.ToLowerInvariant().Contains(query))
                {
                    return creatureName;
                }
                foreach (string item in DropsOf(creature))
                {
                    string name = Localization.instance.Localize(item);
                    if (name.ToLowerInvariant().Contains(query) || item.ToLowerInvariant().Contains(query))
                    {
                        return Lang.F("{0} (з: {1})", name, creatureName);
                    }
                }
            }
            return null;
        }

        // Tooltip lines: the rarest things first, a few of each kind.
        internal static List<string> Lines(string prefab)
        {
            Content content = Get(prefab);
            if (content == null)
            {
                return null;
            }
            if (content.Lines != null)
            {
                return content.Lines;
            }
            List<string> lines = new List<string>();
            AddLine(lines, Lang.T("Тут є"), Names(content.Items, 6));
            AddLine(lines, Lang.T("Продає"), Names(content.Sold, 6));
            List<string> creatures = new List<string>();
            foreach (string creature in content.Creatures)
            {
                string name = CreatureName(creature);
                if (!creatures.Contains(name))
                {
                    creatures.Add(name);
                }
            }
            AddLine(lines, Lang.T("Істоти"), Limit(creatures, 6));
            HashSet<string> drops = new HashSet<string>();
            foreach (string creature in content.Creatures)
            {
                drops.UnionWith(DropsOf(creature));
            }
            drops.ExceptWith(content.Items);
            AddLine(lines, Lang.T("З істот"), Names(drops, 5));
            List<string> points = new List<string>();
            foreach (string point in content.Points)
            {
                string name = MapCatalog.Get(point).Name;
                if (!points.Contains(name))
                {
                    points.Add(name);
                }
            }
            AddLine(lines, Lang.T("Вегвісір вказує на"), Limit(points, 4));
            content.Lines = lines;
            return lines;
        }

        private static void AddLine(List<string> lines, string title, string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                lines.Add($"<color=#c0c0c0>{title}:</color> {text}");
            }
        }

        private static string Names(HashSet<string> items, int max)
        {
            List<string> sorted = new List<string>(items);
            sorted.Sort((a, b) =>
            {
                int byRarity = Places(a).CompareTo(Places(b));
                return byRarity != 0 ? byRarity : string.CompareOrdinal(a, b);
            });
            List<string> names = new List<string>();
            foreach (string item in sorted)
            {
                string name = Localization.instance.Localize(item);
                if (!names.Contains(name))
                {
                    names.Add(name);
                }
            }
            return Limit(names, max);
        }

        private static int Places(string item)
        {
            return s_itemPlaces.TryGetValue(item, out int count) ? count : 0;
        }

        private static string Limit(List<string> names, int max)
        {
            if (names.Count == 0)
            {
                return null;
            }
            if (names.Count <= max)
            {
                return string.Join(", ", names.ToArray());
            }
            return string.Join(", ", names.GetRange(0, max).ToArray()) + Lang.F(" і ще {0}", names.Count - max);
        }

        // ---------------------------------------------------------------- cache

        // The game's code assembly changes with every game update; its size and date say whether the cache still fits.
        private static string GameStamp()
        {
            try
            {
                FileInfo file = new FileInfo(typeof(ZoneSystem).Assembly.Location);
                return $"{CacheFormat}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            }
            catch
            {
                return $"{CacheFormat}|?";
            }
        }

        private static bool LoadCache()
        {
            try
            {
                if (!File.Exists(CachePath))
                {
                    return false;
                }
                string[] lines = File.ReadAllLines(CachePath, Encoding.UTF8);
                if (lines.Length == 0 || lines[0] != GameStamp())
                {
                    return false;
                }
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split('\t');
                    if (parts[0] == "R")
                    {
                        if (parts.Length >= 2)
                        {
                            Split(parts[1], s_roomPrefabs);
                        }
                        continue;
                    }
                    if (parts[0] == "@")
                    {
                        if (parts.Length >= 5 && s_byPlace.TryGetValue(parts[1], out Content placed))
                        {
                            placed.Layout = ParseLayout(parts[2], parts[3], parts[4]);
                        }
                        continue;
                    }
                    if (parts.Length < 6)
                    {
                        continue;
                    }
                    Content content = new Content();
                    Split(parts[1], content.Items);
                    Split(parts[2], content.Sold);
                    Split(parts[3], content.Creatures);
                    Split(parts[4], content.Points);
                    int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out content.Themes);
                    s_byPlace[parts[0]] = content;
                }
                // Rooms were merged into the saved places already.
                return s_byPlace.Count > 0;
            }
            catch (Exception e)
            {
                Log.Error(e);
                s_byPlace.Clear();
                return false;
            }
        }

        // "@" lines: RandomSpawns "percent,biome,min,max,lava,x,y,z;…", RandomObjects "w/w/w,biome,min,max,lava,x,y,z;…",
        // points "prefab,x,y,z,spawn.spawn,choice:entry.choice:entry;…".
        private static string FormatLayout(Layout layout)
        {
            List<string> spawns = new List<string>();
            foreach (Chance chance in layout.Spawns)
            {
                spawns.Add(Num(chance.Percent) + "," + chance.Biome + "," + chance.MinY + "," + chance.MaxY + "," + (chance.NoLava ? 1 : 0) + "," + Vec(chance.Local));
            }
            List<string> choices = new List<string>();
            foreach (Choice choice in layout.Choices)
            {
                List<string> weights = new List<string>();
                foreach (float weight in choice.Weights)
                {
                    weights.Add(Num(weight));
                }
                choices.Add(string.Join("/", weights.ToArray()) + "," + choice.Biome + "," + choice.MinY + "," + choice.MaxY + "," + (choice.NoLava ? 1 : 0) + "," + Vec(choice.Local));
            }
            List<string> points = new List<string>();
            foreach (Point point in layout.Points)
            {
                List<string> needs = new List<string>();
                for (int i = 0; i < point.Choices.Length; i++)
                {
                    needs.Add(point.Choices[i] + ":" + point.Entries[i]);
                }
                List<string> spawnIndexes = new List<string>();
                foreach (int index in point.Spawns)
                {
                    spawnIndexes.Add(index.ToString(CultureInfo.InvariantCulture));
                }
                points.Add(point.Prefab + "," + Vec(point.Local) + "," + string.Join(".", spawnIndexes.ToArray()) + "," + string.Join(".", needs.ToArray()));
            }
            return string.Join(";", spawns.ToArray()) + "\t" + string.Join(";", choices.ToArray()) + "\t" + string.Join(";", points.ToArray());
        }

        private static Layout ParseLayout(string spawns, string choices, string points)
        {
            Layout layout = new Layout();
            foreach (string item in spawns.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] f = item.Split(',');
                layout.Spawns.Add(new Chance
                {
                    Percent = Float(f[0]),
                    Biome = Int(f[1]),
                    MinY = Int(f[2]),
                    MaxY = Int(f[3]),
                    NoLava = f[4] == "1",
                    Local = new Vector3(Float(f[5]), Float(f[6]), Float(f[7])),
                });
            }
            foreach (string item in choices.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] f = item.Split(',');
                string[] w = f[0].Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                float[] weights = new float[w.Length];
                for (int i = 0; i < w.Length; i++)
                {
                    weights[i] = Float(w[i]);
                }
                layout.Choices.Add(new Choice
                {
                    Weights = weights,
                    Biome = Int(f[1]),
                    MinY = Int(f[2]),
                    MaxY = Int(f[3]),
                    NoLava = f[4] == "1",
                    Local = new Vector3(Float(f[5]), Float(f[6]), Float(f[7])),
                });
            }
            foreach (string item in points.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] f = item.Split(',');
                List<int> spawnIndexes = new List<int>();
                foreach (string index in f[4].Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    spawnIndexes.Add(Int(index));
                }
                List<int> needChoices = new List<int>();
                List<int> entries = new List<int>();
                foreach (string need in f[5].Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] pair = need.Split(':');
                    needChoices.Add(Int(pair[0]));
                    entries.Add(Int(pair[1]));
                }
                layout.Points.Add(new Point
                {
                    Prefab = f[0],
                    Local = new Vector3(Float(f[1]), Float(f[2]), Float(f[3])),
                    Spawns = spawnIndexes.ToArray(),
                    Choices = needChoices.ToArray(),
                    Entries = entries.ToArray(),
                });
            }
            return layout;
        }

        private static string Num(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static string Vec(Vector3 v) => Num(v.x) + "," + Num(v.y) + "," + Num(v.z);

        private static float Float(string text) => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

        private static int Int(string text) => int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);

        private static void Split(string text, HashSet<string> into)
        {
            foreach (string part in text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                into.Add(part);
            }
        }

        private static void SaveCache()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                List<string> lines = new List<string> { GameStamp() };
                foreach (KeyValuePair<string, Content> pair in s_byPlace)
                {
                    Content content = pair.Value;
                    lines.Add(string.Join("\t", new[]
                    {
                        pair.Key,
                        string.Join(",", new List<string>(content.Items).ToArray()),
                        string.Join(",", new List<string>(content.Sold).ToArray()),
                        string.Join(",", new List<string>(content.Creatures).ToArray()),
                        string.Join(",", new List<string>(content.Points).ToArray()),
                        // 0: the rooms are merged in already
                        "0",
                    }));
                }
                foreach (KeyValuePair<string, Content> pair in s_byPlace)
                {
                    if (pair.Value.Layout != null)
                    {
                        lines.Add("@\t" + pair.Key + "\t" + FormatLayout(pair.Value.Layout));
                    }
                }
                lines.Add("R\t" + string.Join(",", new List<string>(s_roomPrefabs).ToArray()));
                File.WriteAllLines(CachePath, lines.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }
    }
}
