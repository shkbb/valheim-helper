using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper.Storage
{
    // Ordering from the base storage anywhere in the world. You mark the storage's centre once; from anywhere you see
    // what its chests hold and order items. They are taken out of the chests at once and fly to you: a delivery takes
    // time (3 s plus 1 s for every 12 m, at most 10 min) and is seen moving on the map, so it helps without making
    // the trip pointless. The host does the taking, from the chests' saved data (he holds every chest of the world);
    // a chest someone has open, one loaded by another player right now, or a private chest of someone else is skipped.
    // Items keep everything they had (quality, durability, crafter, cheat mark) because they travel in the game's
    // own inventory format. A delivery on its way is saved, so leaving the game does not lose it.
    internal static class RemoteStorage
    {
        internal sealed class Entry
        {
            public string Prefab;
            public string SharedName;
            public int Quality;
            public int Amount;
        }

        internal sealed class Delivery
        {
            public long Id;
            public Vector3 From;
            public float Duration;
            public float Elapsed;
            public string Icon;
            public string Summary;
            public byte[] Items;

            public float Progress => Duration <= 0f ? 1f : Mathf.Clamp01(Elapsed / Duration);
            public float Left => Mathf.Max(0f, Duration - Elapsed);
        }

        private sealed class ChestInfo
        {
            public int Width;
            public int Height;
            public Container.PrivacySetting Privacy;
        }

        private const string RpcList = "ValheimHelper_BaseList";
        private const string RpcListed = "ValheimHelper_BaseListed";
        private const string RpcTake = "ValheimHelper_BaseTake";
        private const string RpcTaken = "ValheimHelper_BaseTaken";
        internal const float MinRadius = 10f;
        internal const float MaxRadius = 100f;
        // A delivery flies like a ship under full sail: a few seconds to load it, then 12 m a second, at most 10 minutes
        // (from the other end of the world). Right by the storage it is there almost at once, far away it takes long.
        private const float Speed = 12f;
        private const float LoadSeconds = 3f;
        private const float MaxSeconds = 600f;

        internal static bool HasBase;
        internal static Vector3 BasePos;
        internal static float BaseRadius = 30f;
        internal static readonly List<Entry> Catalog = new List<Entry>();
        internal static float CatalogAt = -1000f;
        // A request is on its way to the host; given up after a while (a host without the mod never answers).
        internal static bool Waiting => Time.unscaledTime < s_waitUntil;
        private static float s_waitUntil;
        internal static int Version;
        internal static readonly List<Delivery> Deliveries = new List<Delivery>();
        internal static string LastMessage = "";

        private static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> s_sectors = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");
        private static Dictionary<int, ChestInfo> s_chests;
        private static ZNetScene s_chestsScene;
        private static long s_worldUid;
        private static long s_playerId;
        private static bool s_dirty;
        private static float s_saveAt;
        private static readonly System.Random s_random = new System.Random();

        private static string Folder => Path.Combine(Loader.Dir ?? ".", "maps");
        private static string BasePath => Path.Combine(Folder, s_worldUid.ToString(CultureInfo.InvariantCulture) + ".base.txt");
        private static string DeliveriesPath => Path.Combine(Folder, s_worldUid.ToString(CultureInfo.InvariantCulture) + "." + s_playerId.ToString(CultureInfo.InvariantCulture) + ".deliveries.txt");

        // ---------------------------------------------------------------- session

        internal static void Register(ZRoutedRpc rpc)
        {
            rpc.Register<ZPackage>(RpcList, OnList);
            rpc.Register<ZPackage>(RpcListed, OnListed);
            rpc.Register<ZPackage>(RpcTake, OnTake);
            rpc.Register<ZPackage>(RpcTaken, OnTaken);
        }

        internal static void Tick(float dt)
        {
            Player player = Player.m_localPlayer;
            if (ZNet.instance == null || player == null || Game.instance == null)
            {
                if (s_worldUid != 0)
                {
                    Save();
                    s_worldUid = 0;
                    Deliveries.Clear();
                    Catalog.Clear();
                    HasBase = false;
                }
                return;
            }
            long world = ZNet.instance.GetWorldUID();
            long playerId = Game.instance.GetPlayerProfile().GetPlayerID();
            if (world != s_worldUid || playerId != s_playerId)
            {
                Save();
                s_worldUid = world;
                s_playerId = playerId;
                Load();
            }
            for (int i = Deliveries.Count - 1; i >= 0; i--)
            {
                Delivery delivery = Deliveries[i];
                delivery.Elapsed += dt;
                if (delivery.Elapsed >= delivery.Duration && !player.IsDead() && !player.IsTeleporting())
                {
                    Deliveries.RemoveAt(i);
                    Arrive(player, delivery);
                    s_dirty = true;
                    s_saveAt = 0f;
                }
            }
            if (Deliveries.Count > 0 && Time.unscaledTime >= s_saveAt)
            {
                s_dirty = true;
            }
            if (s_dirty && Time.unscaledTime >= s_saveAt)
            {
                Save();
                s_saveAt = Time.unscaledTime + 10f;
            }
        }

        internal static void SetBase(Vector3 pos)
        {
            HasBase = true;
            BasePos = pos;
            Catalog.Clear();
            CatalogAt = -1000f;
            s_dirty = true;
            s_saveAt = 0f;
            Version++;
        }

        internal static void SetRadius(float radius)
        {
            BaseRadius = Mathf.Clamp(radius, MinRadius, MaxRadius);
            s_dirty = true;
            s_saveAt = Time.unscaledTime + 1f;
        }

        private static bool MapIsHost => ZNet.instance != null && ZNet.instance.IsServer();

        // ---------------------------------------------------------------- asking the host

        internal static string RequestCatalog()
        {
            Player player = Player.m_localPlayer;
            if (player == null || ZRoutedRpc.instance == null)
            {
                return Lang.T("Зайди у світ.");
            }
            if (!HasBase)
            {
                return Lang.T("Спершу стань посеред складу й натисни «Тут центр складу».");
            }
            ZPackage package = new ZPackage();
            package.Write(BasePos);
            package.Write(StorageSettings.Radius);
            package.Write(Game.instance.GetPlayerProfile().GetPlayerID());
            s_waitUntil = Time.unscaledTime + 10f;
            if (MapIsHost)
            {
                OnList(ZDOMan.GetSessionID(), new ZPackage(package.GetArray()));
            }
            else
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(RpcList, package);
            }
            return "";
        }

        internal static string Order(Entry entry, int amount)
        {
            Player player = Player.m_localPlayer;
            if (player == null || ZRoutedRpc.instance == null || entry == null || amount <= 0)
            {
                return Lang.T("Зайди у світ.");
            }
            ZPackage package = new ZPackage();
            package.Write(BasePos);
            package.Write(StorageSettings.Radius);
            package.Write(Game.instance.GetPlayerProfile().GetPlayerID());
            package.Write(entry.Prefab);
            package.Write(entry.Quality);
            package.Write(amount);
            s_waitUntil = Time.unscaledTime + 10f;
            if (MapIsHost)
            {
                OnTake(ZDOMan.GetSessionID(), new ZPackage(package.GetArray()));
            }
            else
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(RpcTake, package);
            }
            return Lang.T("Замовлено, склад збирає…");
        }

        // ---------------------------------------------------------------- the host's side

        private static void OnList(long sender, ZPackage package)
        {
            if (!MapIsHost)
            {
                return;
            }
            Vector3 center = package.ReadVector3();
            float radius = Mathf.Clamp(package.ReadSingle(), MinRadius, MaxRadius);
            long playerId = package.ReadLong();
            Dictionary<string, Entry> totals = new Dictionary<string, Entry>();
            int chests = 0;
            int busyChests = 0;
            List<KeyValuePair<ZDO, ChestInfo>> found = Chests(center, radius, playerId, out busyChests);
            foreach (KeyValuePair<ZDO, ChestInfo> chest in found)
            {
                chests++;
                Inventory inventory = Read(chest.Key, chest.Value, out _);
                foreach (ItemDrop.ItemData item in inventory.GetAllItems())
                {
                    if (item.m_dropPrefab == null)
                    {
                        continue;
                    }
                    string key = item.m_dropPrefab.name + "|" + item.m_quality;
                    if (!totals.TryGetValue(key, out Entry entry))
                    {
                        entry = new Entry { Prefab = item.m_dropPrefab.name, SharedName = item.m_shared.m_name, Quality = item.m_quality };
                        totals[key] = entry;
                    }
                    entry.Amount += item.m_stack;
                }
            }
            ZPackage reply = new ZPackage();
            reply.Write(chests);
            reply.Write(busyChests);
            reply.Write(totals.Count);
            foreach (Entry entry in totals.Values)
            {
                reply.Write(entry.Prefab);
                reply.Write(entry.SharedName);
                reply.Write(entry.Quality);
                reply.Write(entry.Amount);
            }
            if (sender == ZDOMan.GetSessionID())
            {
                OnListed(sender, new ZPackage(reply.GetArray()));
            }
            else
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcListed, reply);
            }
        }

        private static void OnTake(long sender, ZPackage package)
        {
            if (!MapIsHost)
            {
                return;
            }
            Vector3 center = package.ReadVector3();
            float radius = Mathf.Clamp(package.ReadSingle(), MinRadius, MaxRadius);
            long playerId = package.ReadLong();
            string prefab = package.ReadString();
            int quality = package.ReadInt();
            int wanted = Mathf.Clamp(package.ReadInt(), 1, 100000);
            if (sender != ZDOMan.GetSessionID() && (ZNet.instance == null || ZNet.instance.GetPeer(sender) == null))
            {
                return;
            }
            Inventory taken = new Inventory("delivery", null, 8, 64);
            int left = wanted;
            List<KeyValuePair<ZDO, ChestInfo>> usable = Chests(center, radius, playerId, out int busyChests, forTaking: true);
            foreach (KeyValuePair<ZDO, ChestInfo> chest in usable)
            {
                if (left <= 0)
                {
                    break;
                }
                Inventory inventory = Read(chest.Key, chest.Value, out Container loaded);
                bool changed = false;
                foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(inventory.GetAllItems()))
                {
                    if (left <= 0)
                    {
                        break;
                    }
                    if (item.m_dropPrefab == null || item.m_dropPrefab.name != prefab || item.m_quality != quality)
                    {
                        continue;
                    }
                    int take = Mathf.Min(item.m_stack, left);
                    ItemDrop.ItemData copy = item.Clone();
                    copy.m_stack = take;
                    copy.m_equipped = false;
                    if (!taken.AddItem(copy))
                    {
                        left = 0;
                        break;
                    }
                    if (take >= item.m_stack)
                    {
                        inventory.RemoveItem(item);
                    }
                    else
                    {
                        inventory.RemoveItem(item, take);
                    }
                    left -= take;
                    changed = true;
                }
                if (changed && loaded == null)
                {
                    Write(chest.Key, inventory);
                }
            }
            ZPackage items = new ZPackage();
            taken.Save(items);
            ZPackage reply = new ZPackage();
            reply.Write(wanted - left);
            reply.Write(center);
            reply.Write(items.GetArray());
            reply.Write(busyChests);
            if (sender == ZDOMan.GetSessionID())
            {
                OnTaken(sender, new ZPackage(reply.GetArray()));
            }
            else
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcTaken, reply);
            }
        }

        // Storage chests around the centre that the asking player may open and nobody is using, nearest first.
        // A player this close to a chest has it loaded in his game (the game loads a few zones of 64 m around a player).
        private const float PeerNearSqr = 250f * 250f;

        // Every chest of the storage. Seeing what is in them and taking from them are different things: a chest a
        // friend stands by, or one somebody has open, is still listed (its saved contents are known), but nothing is
        // taken out of it while his game owns it. Only another player's personal chest stays out of sight.
        private static List<KeyValuePair<ZDO, ChestInfo>> Chests(Vector3 center, float radius, long playerId, out int busy, bool forTaking = false)
        {
            busy = 0;
            EnsureChests();
            List<KeyValuePair<ZDO, ChestInfo>> result = new List<KeyValuePair<ZDO, ChestInfo>>();
            ZDOMan zdoMan = ZDOMan.instance;
            List<ZDO>[] sectors = zdoMan != null ? s_sectors(zdoMan) : null;
            if (sectors == null || s_chests == null)
            {
                return result;
            }
            HashSet<uint> seen = new HashSet<uint>();
            long me = ZDOMan.GetSessionID();
            int inRadius = 0, inUse = 0, privateOther = 0;
            float radiusSqr = radius * radius;
            for (float dx = -radius - 64f; dx <= radius + 64f; dx += 32f)
            {
                for (float dz = -radius - 64f; dz <= radius + 64f; dz += 32f)
                {
                    uint index = ZoneSystem.GetSectorIndex(center + new Vector3(dx, 0f, dz)).Sector;
                    if (!seen.Add(index) || index >= sectors.Length || sectors[index] == null)
                    {
                        continue;
                    }
                    foreach (ZDO zdo in sectors[index])
                    {
                        if (zdo == null || !s_chests.TryGetValue(zdo.GetPrefab(), out ChestInfo info))
                        {
                            continue;
                        }
                        Vector3 offset = zdo.GetPosition() - center;
                        offset.y = 0f;
                        if (offset.sqrMagnitude > radiusSqr)
                        {
                            continue;
                        }
                        inRadius++;
                        if (info.Privacy == Container.PrivacySetting.Private && zdo.GetLong(ZDOVars.s_creator) != playerId)
                        {
                            // somebody else's personal chest: not his business
                            privateOther++;
                            continue;
                        }
                        bool open = zdo.GetInt(ZDOVars.s_inUse) == 1;
                        if (open)
                        {
                            inUse++;
                        }
                        long owner = zdo.GetOwner();
                        bool nearOther = false;
                        if (owner != 0L && owner != me)
                        {
                            ZNetPeer peer = ZNet.instance.GetPeer(owner);
                            Vector3 away = peer != null ? peer.m_refPos - zdo.GetPosition() : Vector3.zero;
                            away.y = 0f;
                            // The other player is by this chest: his game has it loaded and may change it right now.
                            nearOther = peer != null && away.sqrMagnitude < PeerNearSqr;
                        }
                        if (open || nearOther)
                        {
                            busy++;
                            if (forTaking)
                            {
                                continue;
                            }
                        }
                        result.Add(new KeyValuePair<ZDO, ChestInfo>(zdo, info));
                    }
                }
            }
            result.Sort((a, b) => (a.Key.GetPosition() - center).sqrMagnitude.CompareTo((b.Key.GetPosition() - center).sqrMagnitude));
            Log.Info($"Base storage at {center.x:0},{center.z:0} r={radius:0}: chests in radius {inRadius}, usable {result.Count}, " +
                $"open {inUse}, private {privateOther}, by another player {busy}; chest kinds known {s_chests.Count}");
            return result;
        }

        // Every buildable storage chest of the game: a piece with a container on it (not a cart, ship, tombstone
        // or obliterator).
        private static void EnsureChests()
        {
            ZNetScene scene = ZNetScene.instance;
            if (scene == null || (s_chests != null && ReferenceEquals(scene, s_chestsScene)))
            {
                return;
            }
            s_chestsScene = scene;
            s_chests = new Dictionary<int, ChestInfo>();
            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (prefab == null)
                {
                    continue;
                }
                Container container = prefab.GetComponent<Container>();
                if (container == null || prefab.GetComponent<Piece>() == null || prefab.GetComponent<TombStone>() != null
                    || prefab.GetComponentInChildren<Vagon>(true) != null || prefab.GetComponentInChildren<Ship>(true) != null
                    || prefab.GetComponentInChildren<Incinerator>(true) != null)
                {
                    continue;
                }
                s_chests[prefab.name.GetStableHashCode()] = new ChestInfo { Width = container.m_width, Height = container.m_height, Privacy = container.m_privacy };
            }
        }

        // The chest's items: from the chest itself when the host has it loaded, otherwise from its saved data.
        private static Inventory Read(ZDO zdo, ChestInfo info, out Container loaded)
        {
            loaded = null;
            ZNetView view = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
            Container container = view != null ? view.GetComponent<Container>() : null;
            if (container != null && container.GetInventory() != null && view.IsOwner())
            {
                loaded = container;
                return container.GetInventory();
            }
            Inventory inventory = new Inventory("chest", null, info.Width, info.Height);
            byte[] bytes = zdo.GetByteArray(ZDOVars.s_items);
            if (bytes != null)
            {
                inventory.Load(new ZPackage(bytes));
            }
            return inventory;
        }

        private static void Write(ZDO zdo, Inventory inventory)
        {
            if (!zdo.IsOwner())
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
            }
            ZPackage package = new ZPackage();
            inventory.Save(package);
            zdo.Set(ZDOVars.s_items, package.GetArray());
        }

        // ---------------------------------------------------------------- the asking player's side

        private static void OnListed(long sender, ZPackage package)
        {
            int chests = package.ReadInt();
            int busyChests = package.ReadInt();
            int count = Mathf.Clamp(package.ReadInt(), 0, 5000);
            Catalog.Clear();
            for (int i = 0; i < count; i++)
            {
                Catalog.Add(new Entry { Prefab = package.ReadString(), SharedName = package.ReadString(), Quality = package.ReadInt(), Amount = package.ReadInt() });
            }
            Catalog.Sort((a, b) => string.Compare(Localization.instance.Localize(a.SharedName), Localization.instance.Localize(b.SharedName), StringComparison.CurrentCultureIgnoreCase));
            CatalogAt = Time.unscaledTime;
            s_waitUntil = 0f;
            string busyText = busyChests > 0 ? Lang.F(" З них {0} зараз відкриті або біля друга: видно, що в них є, але замовити звідти не вийде, поки він поруч.", busyChests) : "";
            LastMessage = (chests == 0 ? Lang.T("У радіусі складу немає скринь, доступних тобі.") : Lang.F("Скринь на складі: {0}.", chests)) + busyText;
            Version++;
        }

        private static void OnTaken(long sender, ZPackage package)
        {
            int amount = package.ReadInt();
            Vector3 from = package.ReadVector3();
            byte[] items = package.ReadByteArray();
            int busyChests = package.ReadInt();
            s_waitUntil = 0f;
            CatalogAt = -1000f;
            Player player = Player.m_localPlayer;
            if (amount <= 0 || player == null)
            {
                LastMessage = busyChests > 0
                    ? Lang.F("Нічого не взято: {0} скринь зараз відкриті або біля друга. Спробуй, коли він відійде.", busyChests)
                    : Lang.T("Нічого не взято: цього вже немає.");
                player?.Message(MessageHud.MessageType.Center, LastMessage);
                return;
            }
            Inventory inventory = new Inventory("delivery", null, 8, 64);
            inventory.Load(new ZPackage(items));
            List<ItemDrop.ItemData> all = inventory.GetAllItems();
            string icon = all.Count > 0 && all[0].m_dropPrefab != null ? all[0].m_dropPrefab.name : "";
            string name = all.Count > 0 ? Localization.instance.Localize(all[0].m_shared.m_name) : "";
            float distance = Vector3.Distance(new Vector3(from.x, 0f, from.z), new Vector3(player.transform.position.x, 0f, player.transform.position.z));
            Delivery delivery = new Delivery
            {
                Id = NewId(),
                From = from,
                Duration = Seconds(distance),
                Icon = icon,
                Summary = $"{name} ×{amount}",
                Items = items,
            };
            Deliveries.Add(delivery);
            s_dirty = true;
            s_saveAt = 0f;
            LastMessage = Lang.F("{0} летить до тебе, прибуде за {1}.", delivery.Summary, Clock(delivery.Duration));
            player.Message(MessageHud.MessageType.TopLeft, LastMessage);
            Version++;
        }

        private static void Arrive(Player player, Delivery delivery)
        {
            Inventory inventory = new Inventory("delivery", null, 8, 64);
            inventory.Load(new ZPackage(delivery.Items));
            Inventory mine = player.GetInventory();
            int dropped = 0;
            foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(inventory.GetAllItems()))
            {
                if (mine.CanAddItem(item) && mine.AddItem(item))
                {
                    continue;
                }
                ItemDrop.DropItem(item, item.m_stack, player.transform.position + player.transform.forward + Vector3.up, Quaternion.identity);
                dropped++;
            }
            string text = dropped > 0 ? Lang.F("Доставка прибула: {0}. Не влізло в інвентар — лежить біля тебе.", delivery.Summary) : Lang.F("Доставка прибула: {0}.", delivery.Summary);
            player.Message(MessageHud.MessageType.Center, text);
            LastMessage = text;
            Version++;
        }

        // Where a delivery is now: on the way from the storage to the player.
        internal static Vector3 Position(Delivery delivery, Vector3 player)
        {
            float t = delivery.Progress;
            Vector3 pos = Vector3.Lerp(delivery.From, player, t);
            return pos;
        }

        internal static float Seconds(float distance)
        {
            return Mathf.Min(LoadSeconds + distance / Speed, MaxSeconds);
        }

        // How long a delivery from the storage to this place takes.
        internal static float SecondsTo(Vector3 pos)
        {
            return Seconds(Vector3.Distance(new Vector3(BasePos.x, 0f, BasePos.z), new Vector3(pos.x, 0f, pos.z)));
        }

        internal static string Clock(float seconds)
        {
            int total = Mathf.CeilToInt(seconds);
            return $"{total / 60}:{total % 60:00}";
        }

        private static long NewId()
        {
            byte[] bytes = new byte[8];
            s_random.NextBytes(bytes);
            return BitConverter.ToInt64(bytes, 0) & long.MaxValue;
        }

        // ---------------------------------------------------------------- files

        private static void Load()
        {
            HasBase = false;
            Deliveries.Clear();
            Catalog.Clear();
            try
            {
                if (File.Exists(BasePath))
                {
                    string[] parts = File.ReadAllText(BasePath).Trim().Split(' ');
                    if (parts.Length >= 4
                        && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                        && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                        && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
                        && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float radius))
                    {
                        HasBase = true;
                        BasePos = new Vector3(x, y, z);
                        BaseRadius = Mathf.Clamp(radius, MinRadius, MaxRadius);
                    // the base radius of older versions becomes the one storage radius (never smaller than it was)
                    if (BaseRadius > StorageSettings.Radius)
                    {
                        StorageSettings.Radius = Mathf.Min(BaseRadius, StorageSettings.MaxRadius);
                        StorageSettings.Changed();
                    }
                    }
                }
                if (File.Exists(DeliveriesPath))
                {
                    foreach (string line in File.ReadAllLines(DeliveriesPath, Encoding.UTF8))
                    {
                        string[] parts = line.Split('|');
                        if (parts.Length < 9)
                        {
                            continue;
                        }
                        Deliveries.Add(new Delivery
                        {
                            Id = long.Parse(parts[0], CultureInfo.InvariantCulture),
                            From = new Vector3(float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture), float.Parse(parts[3], CultureInfo.InvariantCulture)),
                            Duration = float.Parse(parts[4], CultureInfo.InvariantCulture),
                            Elapsed = float.Parse(parts[5], CultureInfo.InvariantCulture),
                            Icon = parts[6],
                            Summary = parts[7],
                            Items = Convert.FromBase64String(parts[8]),
                        });
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
            Version++;
        }

        private static void Save()
        {
            s_dirty = false;
            if (s_worldUid == 0 || s_playerId == 0)
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(Folder);
                if (HasBase)
                {
                    File.WriteAllText(BasePath, string.Join(" ", new[]
                    {
                        BasePos.x.ToString("0.##", CultureInfo.InvariantCulture),
                        BasePos.y.ToString("0.##", CultureInfo.InvariantCulture),
                        BasePos.z.ToString("0.##", CultureInfo.InvariantCulture),
                        BaseRadius.ToString("0", CultureInfo.InvariantCulture),
                    }));
                }
                List<string> lines = new List<string>();
                foreach (Delivery delivery in Deliveries)
                {
                    lines.Add(string.Join("|", new[]
                    {
                        delivery.Id.ToString(CultureInfo.InvariantCulture),
                        delivery.From.x.ToString("0.##", CultureInfo.InvariantCulture),
                        delivery.From.y.ToString("0.##", CultureInfo.InvariantCulture),
                        delivery.From.z.ToString("0.##", CultureInfo.InvariantCulture),
                        delivery.Duration.ToString("0.##", CultureInfo.InvariantCulture),
                        delivery.Elapsed.ToString("0.##", CultureInfo.InvariantCulture),
                        delivery.Icon ?? "",
                        (delivery.Summary ?? "").Replace("|", "/"),
                        Convert.ToBase64String(delivery.Items ?? new byte[0]),
                    }));
                }
                if (lines.Count > 0)
                {
                    File.WriteAllLines(DeliveriesPath, lines.ToArray(), new UTF8Encoding(false));
                }
                else if (File.Exists(DeliveriesPath))
                {
                    File.Delete(DeliveriesPath);
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        internal static void SaveNow()
        {
            if (s_worldUid != 0)
            {
                Save();
            }
        }
    }
}
