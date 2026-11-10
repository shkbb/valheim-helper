using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper
{
    // Personal statistics. The game keeps a few hundred counters for every character (PlayerProfile.m_playerStats:
    // deaths, kills by enemy, distances, trees, fish…) but shows almost none of them; they are only read here, never
    // changed. For the current world the mod counts the same numbers itself (the game keeps only totals), from the
    // moment it first sees the world with this character. Friends with the mod send their numbers for comparison,
    // and everything can be copied as a table for Discord.
    internal static class StatsBook
    {
        internal const int CharacterScope = 0;
        internal const int WorldScope = 1;

        internal enum Format
        {
            Count,
            Distance,
            Time,
            Meters,
        }

        internal sealed class Row
        {
            public string Key;
            public string Label;
            public Format Format;
            public bool CharacterOnly;
            public bool Always;
        }

        internal sealed class Group
        {
            public string Title;
            public readonly List<Row> Rows = new List<Row>();
            // a "top" group lists the biggest counters with this key prefix (enemies killed, food eaten)
            public string TopPrefix;
            public int TopCount;
            public bool HideZero;
        }

        internal sealed class Column
        {
            public string Name;
            public bool Me;
            public Dictionary<string, float> Values;
        }

        private sealed class Friend
        {
            public string Name;
            public int Scope;
            public long World;
            public Dictionary<string, float> Values;
            public float At;
        }

        private const string RpcAsk = "ValheimHelper_StatsAsk";
        private const string RpcData = "ValheimHelper_Stats";
        private const float SampleEverySec = 2f;
        private const float SaveEverySec = 60f;
        private const float AskEverySec = 10f;
        private const float FriendDataFreshSec = 45f;

        // counters that are records or current values, not sums: a world has no share of them
        private static readonly HashSet<string> s_notSums = new HashSet<string>
        {
            "s.ConsecutiveDaysSurvived", "s.ConsecutiveDaysSurvivedMax", "s.MaxBuildingHeight", "s.MaxBuildingHeightWorld", "s.MaxComfort",
        };

        private static readonly AccessTools.FieldRef<PlayerProfile, DateTime> s_lastSaveLoad =
            AccessTools.FieldRefAccess<PlayerProfile, DateTime>("m_lastSaveLoad");

        internal static int Scope;

        private static List<Group> s_groups;

        // the world being counted
        private static PlayerProfile s_profile;
        private static long s_player;
        private static long s_world;
        private static string s_worldName;
        private static Dictionary<string, float> s_base;
        private static Dictionary<string, float> s_worldValues = new Dictionary<string, float>();
        private static DateTime s_since;
        private static float s_sampleAt;
        private static float s_saveAt;
        private static bool s_dirty;

        // friends
        private static ZRoutedRpc s_rpc;
        private static readonly Dictionary<long, Friend> s_friends = new Dictionary<long, Friend>();
        private static float s_askAt = -100f;

        // what the page shows, rebuilt at most once a second
        private static List<Column> s_columns;
        private static float s_columnsAt = -100f;
        private static int s_columnsScope = -1;

        internal static List<Group> Groups => s_groups ?? (s_groups = BuildGroups());

        internal static string WorldName => s_worldName;

        internal static DateTime Since => s_since;

        private static string FilePath(long player, long world) =>
            Path.Combine(Path.Combine(Loader.Dir ?? ".", "stats"), player.ToString(CultureInfo.InvariantCulture) + "_" + world.ToString(CultureInfo.InvariantCulture) + ".txt");

        // ---------------------------------------------------------------- the rows

        private static List<Group> BuildGroups()
        {
            List<Group> groups = new List<Group>();
            Group group = Add(groups, Lang.T("Загальне"));
            AddRow(group, "t.play", Lang.T("Часу в грі"), Format.Time).Always = true;
            AddRow(group, "s.Deaths", Lang.T("Смертей")).Always = true;
            AddRow(group, "s.ConsecutiveDaysSurvived", Lang.T("Днів без смерті зараз")).CharacterOnly = true;
            AddRow(group, "s.ConsecutiveDaysSurvivedMax", Lang.T("Рекорд днів без смерті")).CharacterOnly = true;
            AddRow(group, "s.Sleep", Lang.T("Ночей проспано"));
            AddRow(group, "s.FoodEaten", Lang.T("Страв з'їдено"));
            AddRow(group, "x.foodkinds", Lang.T("Різних страв скуштовано"));
            AddRow(group, "s.ItemsPickedUp", Lang.T("Предметів підібрано"));
            AddRow(group, "s.Jumps", Lang.T("Стрибків"));
            AddRow(group, "s.TombstonesOpenedOwn", Lang.T("Походів до своєї могили"));

            group = Add(groups, Lang.T("Бій"));
            AddRow(group, "s.EnemyKills", Lang.T("Ворогів убито"));
            AddRow(group, "s.BossKills", Lang.T("Босів переможено"));
            AddRow(group, "s.EnemyHits", Lang.T("Влучань по ворогах"));
            AddRow(group, "s.HitsTakenEnemies", Lang.T("Ударів від ворогів"));
            AddRow(group, "s.ArrowsShot", Lang.T("Стріл і болтів випущено"));
            AddRow(group, "s.PlayerKills", Lang.T("Гравців убито"));

            group = Add(groups, Lang.T("Кого вбито найбільше"));
            group.TopPrefix = "k.";
            group.TopCount = 5;

            group = Add(groups, Lang.T("Причини смерті"));
            group.HideZero = true;
            AddRow(group, "s.DeathByEnemyHit", Lang.T("Вороги"));
            AddRow(group, "s.DeathByPlayerHit", Lang.T("Інші гравці"));
            AddRow(group, "s.DeathByFall", Lang.T("Падіння"));
            AddRow(group, "s.DeathByDrowning", Lang.T("Утоплення"));
            AddRow(group, "s.DeathByBurning", Lang.T("Вогонь"));
            AddRow(group, "s.DeathByFreezing", Lang.T("Холод"));
            AddRow(group, "s.DeathByPoisoned", Lang.T("Отрута"));
            AddRow(group, "s.DeathBySmoke", Lang.T("Дим"));
            AddRow(group, "s.DeathByWater", Lang.T("Вода"));
            AddRow(group, "s.DeathByImpact", Lang.T("Удар об щось"));
            AddRow(group, "s.DeathByTree", Lang.T("Дерево"));
            AddRow(group, "s.DeathByStructural", Lang.T("Обвал будівлі"));
            AddRow(group, "s.DeathByCart", Lang.T("Віз"));
            AddRow(group, "s.DeathByBoat", Lang.T("Корабель"));
            AddRow(group, "s.DeathByTurret", Lang.T("Баліста"));
            AddRow(group, "s.DeathByStalagtite", Lang.T("Сталактит"));
            AddRow(group, "s.DeathByAshlandsLava", Lang.T("Лава"));
            AddRow(group, "s.DeathByAshlandsOcean", Lang.T("Кипуче море"));
            AddRow(group, "s.DeathByCinderFire", Lang.T("Жар"));
            AddRow(group, "s.DeathByCatapult", Lang.T("Катапульта"));
            AddRow(group, "s.DeathByIncinerator", Lang.T("Спалювач"));
            AddRow(group, "s.DeathByDrawBridge", Lang.T("Звідний міст"));
            AddRow(group, "s.DeathByEdgeOfWorld", Lang.T("Край світу"));
            AddRow(group, "s.DeathBySelf", Lang.T("Власна рука"));
            AddRow(group, "s.DeathByUndefined", Lang.T("Інше"));

            group = Add(groups, Lang.T("Мандри"));
            AddRow(group, "s.DistanceTraveled", Lang.T("Шлях загалом"), Format.Distance);
            AddRow(group, "s.DistanceWalk", Lang.T("Пішки"), Format.Distance);
            AddRow(group, "s.DistanceRun", Lang.T("Бігом"), Format.Distance);
            AddRow(group, "s.DistanceSail", Lang.T("На кораблі"), Format.Distance);
            AddRow(group, "s.DistanceAir", Lang.T("У повітрі"), Format.Distance);
            AddRow(group, "s.PortalsUsed", Lang.T("Порталів пройдено"));
            AddRow(group, "s.PortalDungeonIn", Lang.T("Входів у підземелля"));
            AddRow(group, "s.TimeInBase", Lang.T("Часу на базі"), Format.Time);
            AddRow(group, "s.TimeOutOfBase", Lang.T("Часу поза базою"), Format.Time);

            group = Add(groups, Lang.T("Ремесло й будівництво"));
            AddRow(group, "s.Crafts", Lang.T("Предметів виготовлено"));
            AddRow(group, "x.craftkinds", Lang.T("Різних предметів виготовлено"));
            AddRow(group, "s.Upgrades", Lang.T("Покращень"));
            AddRow(group, "s.CraftFood", Lang.T("Страв приготовано"));
            AddRow(group, "s.Builds", Lang.T("Частин споруд поставлено"));
            AddRow(group, "s.BuildPiecesRemoved", Lang.T("Частин розібрано"));
            AddRow(group, "s.MaxBuildingHeight", Lang.T("Найвища будівля"), Format.Meters).CharacterOnly = true;
            AddRow(group, "s.MaxComfort", Lang.T("Найбільший комфорт")).CharacterOnly = true;
            AddRow(group, "s.DoorsOpened", Lang.T("Дверей відчинено"));

            group = Add(groups, Lang.T("Господарство"));
            AddRow(group, "s.Tree", Lang.T("Дерев зрубано"));
            AddRow(group, "s.Logs", Lang.T("Колод розрубано"));
            AddRow(group, "s.Mines", Lang.T("Каменів і жил розбито"));
            AddRow(group, "x.harvest", Lang.T("Зібрано ягід, грибів і врожаю"));
            AddRow(group, "s.FishCaught", Lang.T("Риби спіймано"));
            AddRow(group, "s.BeesHarvested", Lang.T("Разів зібрано мед"));
            AddRow(group, "s.SapHarvested", Lang.T("Разів зібрано сік"));
            AddRow(group, "s.CreatureTamed", Lang.T("Тварин приручено"));
            AddRow(group, "s.TamedPetting", Lang.T("Разів погладжено тварин"));

            group = Add(groups, Lang.T("Що з'їдено найбільше"));
            group.TopPrefix = "f.";
            group.TopCount = 3;
            return groups;
        }

        private static Group Add(List<Group> groups, string title)
        {
            Group group = new Group { Title = title };
            groups.Add(group);
            return group;
        }

        private static Row AddRow(Group group, string key, string label, Format format = Format.Count)
        {
            Row row = new Row { Key = key, Label = label, Format = format };
            group.Rows.Add(row);
            return row;
        }

        // ---------------------------------------------------------------- reading the game

        // Every counter of the character as "s.<stat>", "k.<enemy>", "f.<food>", "c.<crafted item>", "p.<picked>".
        private static Dictionary<string, float> Raw(PlayerProfile profile)
        {
            Dictionary<string, float> values = new Dictionary<string, float>();
            PlayerProfile.PlayerStats stats = profile.m_playerStats[0];
            if (stats == null)
            {
                return values;
            }
            foreach (KeyValuePair<PlayerStatType, float> pair in stats.m_stats)
            {
                if (pair.Value != 0f)
                {
                    values["s." + pair.Key] = pair.Value;
                }
            }
            AddAll(values, "k.", stats.m_enemyStats != null && stats.m_enemyStats.Length > 0 ? stats.m_enemyStats[0] : null);
            AddAll(values, "f.", stats.m_foodEatenStats);
            AddAll(values, "c.", stats.m_itemCraftStats);
            AddAll(values, "p.", stats.m_pickableStats);
            return values;
        }

        private static void AddAll(Dictionary<string, float> values, string prefix, Dictionary<string, float> source)
        {
            if (source == null)
            {
                return;
            }
            foreach (KeyValuePair<string, float> pair in source)
            {
                if (pair.Value != 0f && !string.IsNullOrEmpty(pair.Key))
                {
                    values[prefix + pair.Key] = pair.Value;
                }
            }
        }

        // The seconds played: the game adds them to its per-world table when it saves the character.
        private static float PlayTime(PlayerProfile profile, string world)
        {
            PlayerProfile.PlayerStats stats = profile.m_playerStats[0];
            float seconds = 0f;
            if (stats != null && stats.m_knownWorlds != null)
            {
                if (world == null)
                {
                    foreach (float value in stats.m_knownWorlds.Values)
                    {
                        seconds += value;
                    }
                }
                else if (stats.m_knownWorlds.TryGetValue(world, out float value))
                {
                    seconds = value;
                }
            }
            if (ZNet.World != null && Game.instance != null && Game.instance.GetPlayerProfile() == profile)
            {
                seconds += Mathf.Max(0f, (float)(DateTime.Now - s_lastSaveLoad(profile)).TotalSeconds);
            }
            return seconds;
        }

        // Numbers made of others: kinds of food and items, everything picked that is an item (fish are counted apart).
        private static void Derive(Dictionary<string, float> values)
        {
            int foods = 0;
            int crafted = 0;
            float harvest = 0f;
            foreach (KeyValuePair<string, float> pair in values)
            {
                if (pair.Value <= 0f)
                {
                    continue;
                }
                if (pair.Key.StartsWith("f.", StringComparison.Ordinal))
                {
                    foods++;
                }
                else if (pair.Key.StartsWith("c.", StringComparison.Ordinal))
                {
                    crafted++;
                }
                else if (pair.Key.StartsWith("p.", StringComparison.Ordinal) && ObjectDB.instance != null
                    && ObjectDB.instance.GetItemPrefab(pair.Key.Substring(2)) != null)
                {
                    harvest += pair.Value;
                }
            }
            values["x.foodkinds"] = foods;
            values["x.craftkinds"] = crafted;
            values["x.harvest"] = harvest;
        }

        // The numbers of the local character for a scope, complete.
        internal static Dictionary<string, float> Mine(int scope)
        {
            PlayerProfile profile = Game.instance != null ? Game.instance.GetPlayerProfile() : null;
            if (profile == null)
            {
                return new Dictionary<string, float>();
            }
            Dictionary<string, float> values;
            if (scope == WorldScope)
            {
                Sample();
                values = new Dictionary<string, float>(s_worldValues);
                values["t.play"] = PlayTime(profile, ZNet.World != null ? ZNet.World.m_name : "");
            }
            else
            {
                values = Raw(profile);
                values["t.play"] = PlayTime(profile, null);
            }
            Derive(values);
            return values;
        }

        // What a friend needs to draw the page: the rows' numbers and the biggest of the top lists.
        private static Dictionary<string, float> Compact(Dictionary<string, float> values)
        {
            Dictionary<string, float> compact = new Dictionary<string, float>();
            foreach (Group group in Groups)
            {
                foreach (Row row in group.Rows)
                {
                    if (values.TryGetValue(row.Key, out float value) && value != 0f)
                    {
                        compact[row.Key] = value;
                    }
                }
                if (group.TopPrefix != null)
                {
                    foreach (KeyValuePair<string, float> pair in Top(values, group.TopPrefix, group.TopCount * 2))
                    {
                        compact[pair.Key] = pair.Value;
                    }
                }
            }
            return compact;
        }

        private static IEnumerable<KeyValuePair<string, float>> Top(Dictionary<string, float> values, string prefix, int count)
        {
            return values.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal) && pair.Value > 0f)
                .OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(count);
        }

        // ---------------------------------------------------------------- counting the world

        internal static void Tick()
        {
            EnsureRpc();
            if (Time.unscaledTime < s_sampleAt)
            {
                return;
            }
            s_sampleAt = Time.unscaledTime + SampleEverySec;
            PlayerProfile profile = Game.instance != null ? Game.instance.GetPlayerProfile() : null;
            World world = ZNet.World;
            if (profile == null || world == null)
            {
                EndWorld();
                return;
            }
            if (!ReferenceEquals(profile, s_profile) || world.m_uid != s_world || profile.GetPlayerID() != s_player)
            {
                EndWorld();
                StartWorld(profile, world);
                return;
            }
            Sample();
            if (s_dirty && Time.unscaledTime >= s_saveAt)
            {
                Save();
            }
        }

        internal static void SaveNow()
        {
            if (s_profile != null)
            {
                Sample();
                Save();
            }
        }

        private static void StartWorld(PlayerProfile profile, World world)
        {
            s_profile = profile;
            s_player = profile.GetPlayerID();
            s_world = world.m_uid;
            s_worldName = world.m_name;
            s_worldValues = new Dictionary<string, float>();
            s_since = DateTime.Now;
            s_dirty = true;
            Load();
            s_base = Raw(profile);
            s_saveAt = Time.unscaledTime + SaveEverySec;
        }

        private static void EndWorld()
        {
            if (s_profile == null)
            {
                return;
            }
            Sample();
            Save();
            s_profile = null;
            s_base = null;
            s_world = 0L;
            s_worldName = null;
            s_worldValues = new Dictionary<string, float>();
        }

        // Adds what grew since the last look to the world's numbers.
        private static void Sample()
        {
            if (s_profile == null || s_base == null)
            {
                return;
            }
            Dictionary<string, float> now = Raw(s_profile);
            foreach (KeyValuePair<string, float> pair in now)
            {
                if (s_notSums.Contains(pair.Key) || pair.Key.StartsWith("s.BuildCluster", StringComparison.Ordinal))
                {
                    continue;
                }
                s_base.TryGetValue(pair.Key, out float before);
                float grown = pair.Value - before;
                if (grown > 0f)
                {
                    s_worldValues.TryGetValue(pair.Key, out float sum);
                    s_worldValues[pair.Key] = sum + grown;
                    s_dirty = true;
                }
            }
            s_base = now;
        }

        private static void Load()
        {
            string path = FilePath(s_player, s_world);
            if (!File.Exists(path))
            {
                return;
            }
            try
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0)
                    {
                        continue;
                    }
                    string key = line.Substring(0, tab);
                    string value = line.Substring(tab + 1);
                    if (key == "since")
                    {
                        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unix))
                        {
                            s_since = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
                        }
                    }
                    else if (key != "world" && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
                    {
                        s_worldValues[key] = number;
                    }
                }
                s_dirty = false;
            }
            catch (Exception e)
            {
                Log.Warn("Stats: " + path + ": " + e.Message);
            }
        }

        private static void Save()
        {
            if (s_profile == null || !s_dirty)
            {
                return;
            }
            s_dirty = false;
            s_saveAt = Time.unscaledTime + SaveEverySec;
            string path = FilePath(s_player, s_world);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                List<string> lines = new List<string>
                {
                    "since\t" + new DateTimeOffset(s_since).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                    "world\t" + (s_worldName ?? "").Replace('\t', ' '),
                };
                foreach (KeyValuePair<string, float> pair in s_worldValues.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    lines.Add(pair.Key.Replace('\t', ' ') + "\t" + pair.Value.ToString("R", CultureInfo.InvariantCulture));
                }
                File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Log.Warn("Stats: " + path + ": " + e.Message);
            }
        }

        // ---------------------------------------------------------------- friends

        private static void EnsureRpc()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (ReferenceEquals(rpc, s_rpc))
            {
                return;
            }
            s_rpc = rpc;
            s_friends.Clear();
            s_columns = null;
            if (rpc != null)
            {
                rpc.Register<int>(RpcAsk, OnAsk);
                rpc.Register<ZPackage>(RpcData, OnData);
            }
        }

        // Asks the friends with the mod for their numbers (while the page is open, every few seconds).
        internal static void AskFriends(bool force)
        {
            EnsureRpc();
            if (s_rpc == null || (!force && Time.unscaledTime - s_askAt < AskEverySec))
            {
                return;
            }
            s_askAt = Time.unscaledTime;
            Gifts.RequestCheck(false);
            foreach (ZNet.PlayerInfo info in Gifts.Friends())
            {
                long peer = info.m_characterID.UserID;
                if (Gifts.GetState(peer) == Gifts.ModState.HasMod)
                {
                    s_rpc.InvokeRoutedRPC(peer, RpcAsk, Scope);
                }
            }
        }

        private static void OnAsk(long sender, int scope)
        {
            if (ZRoutedRpc.instance == null || Player.m_localPlayer == null)
            {
                return;
            }
            Dictionary<string, float> values = Compact(Mine(scope == WorldScope ? WorldScope : CharacterScope));
            ZPackage package = new ZPackage();
            package.Write(Player.m_localPlayer.GetPlayerName());
            package.Write(scope);
            package.Write(ZNet.World != null ? ZNet.World.m_uid : 0L);
            package.Write(values.Count);
            foreach (KeyValuePair<string, float> pair in values)
            {
                package.Write(pair.Key);
                package.Write(pair.Value);
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcData, package);
        }

        private static void OnData(long sender, ZPackage package)
        {
            try
            {
                Friend friend = new Friend
                {
                    Name = package.ReadString(),
                    Scope = package.ReadInt(),
                    World = package.ReadLong(),
                    Values = new Dictionary<string, float>(),
                    At = Time.unscaledTime,
                };
                int count = Mathf.Clamp(package.ReadInt(), 0, 2000);
                for (int i = 0; i < count; i++)
                {
                    string key = package.ReadString();
                    friend.Values[key] = package.ReadSingle();
                }
                s_friends[sender] = friend;
                s_columns = null;
            }
            catch (Exception e)
            {
                Log.Warn("Stats from a friend: " + e.Message);
            }
        }

        // Friends online who have not sent numbers (no mod, or a version without statistics).
        internal static List<string> Silent()
        {
            List<string> names = new List<string>();
            foreach (ZNet.PlayerInfo info in Gifts.Friends())
            {
                if (!s_friends.TryGetValue(info.m_characterID.UserID, out Friend friend) || Time.unscaledTime - friend.At > FriendDataFreshSec)
                {
                    names.Add(info.m_name);
                }
            }
            return names;
        }

        // ---------------------------------------------------------------- the table

        // The local character first, then every friend whose numbers for this scope came lately.
        internal static List<Column> Columns()
        {
            if (s_columns != null && s_columnsScope == Scope && Time.unscaledTime - s_columnsAt < 1f)
            {
                return s_columns;
            }
            List<Column> columns = new List<Column>();
            string me = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : Lang.T("Я");
            columns.Add(new Column { Name = me, Me = true, Values = Mine(Scope) });
            HashSet<long> online = new HashSet<long>(Gifts.Friends().Select(info => info.m_characterID.UserID));
            long world = ZNet.World != null ? ZNet.World.m_uid : 0L;
            foreach (KeyValuePair<long, Friend> pair in s_friends.OrderBy(pair => pair.Value.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Friend friend = pair.Value;
                if (online.Contains(pair.Key) && friend.Scope == Scope && Time.unscaledTime - friend.At <= FriendDataFreshSec
                    && (Scope != WorldScope || friend.World == world))
                {
                    columns.Add(new Column { Name = friend.Name, Values = friend.Values });
                }
            }
            s_columns = columns;
            s_columnsAt = Time.unscaledTime;
            s_columnsScope = Scope;
            return columns;
        }

        // The rows worth showing for these columns: a top group becomes the union of everyone's biggest counters.
        internal static List<KeyValuePair<Group, List<Row>>> Visible(List<Column> columns, int scope)
        {
            List<KeyValuePair<Group, List<Row>>> result = new List<KeyValuePair<Group, List<Row>>>();
            foreach (Group group in Groups)
            {
                List<Row> rows = new List<Row>();
                if (group.TopPrefix != null)
                {
                    List<string> keys = new List<string>();
                    foreach (Column column in columns)
                    {
                        foreach (KeyValuePair<string, float> pair in Top(column.Values, group.TopPrefix, group.TopCount))
                        {
                            if (!keys.Contains(pair.Key))
                            {
                                keys.Add(pair.Key);
                            }
                        }
                    }
                    Dictionary<string, float> mine = columns[0].Values;
                    foreach (string key in keys.OrderByDescending(key => mine.TryGetValue(key, out float value) ? value : 0f))
                    {
                        rows.Add(new Row { Key = key, Label = TopName(key), Format = Format.Count });
                    }
                }
                else
                {
                    foreach (Row row in group.Rows)
                    {
                        if (row.CharacterOnly && scope == WorldScope)
                        {
                            continue;
                        }
                        if (row.Always || columns.Any(column => column.Values.TryGetValue(row.Key, out float value) && value != 0f))
                        {
                            rows.Add(row);
                        }
                    }
                }
                if (rows.Count > 0)
                {
                    result.Add(new KeyValuePair<Group, List<Row>>(group, rows));
                }
            }
            return result;
        }

        private static string TopName(string key)
        {
            string name = key.Substring(2);
            if (name.StartsWith("$", StringComparison.Ordinal) && Localization.instance != null)
            {
                name = Localization.instance.Localize(name);
            }
            return name;
        }

        internal static string Value(Column column, Row row)
        {
            if (!column.Values.TryGetValue(row.Key, out float value))
            {
                return column.Me || row.Key.StartsWith("s.", StringComparison.Ordinal) || row.Key.StartsWith("x.", StringComparison.Ordinal) ? "0" : "—";
            }
            switch (row.Format)
            {
                case Format.Distance:
                    return value >= 1000f ? Lang.F("{0:0.0} км", value / 1000f) : Lang.F("{0:0} м", value);
                case Format.Meters:
                    return Lang.F("{0:0} м", value);
                case Format.Time:
                    int minutes = Mathf.FloorToInt(value / 60f);
                    return minutes >= 60 ? Lang.F("{0} год {1} хв", minutes / 60, minutes % 60) : Lang.F("{0} хв", minutes);
                default:
                    return Number(value);
            }
        }

        private static string Number(float value)
        {
            long whole = (long)Math.Round(value);
            NumberFormatInfo format = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
            format.NumberGroupSeparator = Lang.Code == Lang.English ? "," : " ";
            return whole.ToString("#,0", format);
        }

    }
}
