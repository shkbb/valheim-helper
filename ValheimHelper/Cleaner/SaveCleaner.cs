using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper
{
    // Removes the game's "cheated" markers from the character and from the world so achievements
    // and stats progress again. Details: ARCHITECTURE.md, "Очищення сейвів".
    internal static class SaveCleaner
    {
        // Inventory.Load re-marks any item above this damage as cheated, so such items cannot be cleaned.
        private const float MaxSafeItemDamage = 10000f;
        private const int MaxIndexedItemSlots = 32;
        private const int MaxQueuedSlots = 16;

        internal sealed class CharacterStatus
        {
            public bool UsedCheats;
            public int CheatedItems;
            public readonly List<string> UncleanableItems = new List<string>();
        }

        internal sealed class WorldStatus
        {
            public string Error;
            public string KeysError;
            public List<string> CheatedWorldKeys = new List<string>();
            public int FlaggedObjects;
            public int FlaggedItemDrops;
            public int FlaggedContainers;
            public int FlaggedContainerItems;
            public int UnreadableRecords;
            public readonly HashSet<string> UncleanableItems = new HashSet<string>();
        }

        private static readonly FieldInfo s_extraInts = AccessTools.Field(typeof(ZDOExtraData), "s_ints");
        private static readonly FieldInfo s_extraByteArrays = AccessTools.Field(typeof(ZDOExtraData), "s_byteArrays");
        private static readonly MethodInfo s_possibleGlobalKeyValues = AccessTools.Method(typeof(ServerOptionsGUI), "GetPossibleGlobalKeyValues");
        private static readonly FieldInfo s_itemDropInstances = AccessTools.Field(typeof(ItemDrop), "s_instances");

        private static int[] s_flagHashes;
        private static int[] s_itemDataHashes;

        // ---------------------------------------------------------------- character

        internal static CharacterStatus ScanCharacter()
        {
            CharacterStatus status = new CharacterStatus();
            PlayerProfile profile = Game.instance != null ? Game.instance.GetPlayerProfile() : null;
            status.UsedCheats = profile != null && profile.m_usedCheats;
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return status;
            }
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems())
            {
                if (IsUncleanable(item))
                {
                    status.UncleanableItems.Add(Localization.instance.Localize(item.m_shared.m_name));
                }
                else if (item.m_cheated)
                {
                    status.CheatedItems++;
                }
            }
            return status;
        }

        internal static string CleanCharacter()
        {
            Player player = Player.m_localPlayer;
            if (Game.instance == null || player == null)
            {
                return Lang.T("Спершу зайди у світ цим персонажем.");
            }
            PlayerProfile profile = Game.instance.GetPlayerProfile();
            bool usedCheats = profile.m_usedCheats;
            profile.m_usedCheats = false;

            int cleaned = 0;
            List<string> uncleanable = new List<string>();
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems())
            {
                if (IsUncleanable(item))
                {
                    uncleanable.Add(Localization.instance.Localize(item.m_shared.m_name));
                }
                else if (item.m_cheated)
                {
                    item.m_cheated = false;
                    cleaned++;
                }
            }
            Game.instance.SavePlayerProfile(setLogoutPoint: false);

            string result = Lang.F("Персонажа очищено і збережено. Позначка консольних читів: {0}. Предметів очищено: {1}.", (usedCheats ? Lang.T("знята") : Lang.T("не стояла")), cleaned);
            if (uncleanable.Count > 0)
            {
                result += Lang.T("\nЦі предмети гра знову позначить (урон > 10000), викинь їх: ") + string.Join(", ", uncleanable);
            }
            return result;
        }

        // ---------------------------------------------------------------- world

        internal static string WhyWorldCannotBeCleaned()
        {
            if (ZNet.instance == null || ZNet.World == null || ZDOMan.instance == null)
            {
                return Lang.T("зайди у світ.");
            }
            if (!ZNet.instance.IsServer())
            {
                return Lang.T("це робить лише хост світу.");
            }
            if (ZNet.instance.GetConnectedPeers().Count > 0)
            {
                return Lang.T("у світі є інші гравці, нехай на хвилину вийдуть.");
            }
            return null;
        }

        internal static WorldStatus ScanWorld()
        {
            return ProcessWorld(apply: false, removeCheatedKeys: false);
        }

        internal static string CleanWorld(bool removeCheatedKeys)
        {
            string blocker = WhyWorldCannotBeCleaned();
            if (blocker != null)
            {
                return Lang.T("Очистити світ не можна: ") + blocker;
            }
            WorldStatus status = ProcessWorld(apply: true, removeCheatedKeys);
            if (status.Error != null)
            {
                return Lang.T("Помилка під час очищення: ") + status.Error;
            }
            // Next frame, so the RemoveGlobalKey RPCs are processed before the world is written.
            ZNet.instance.Save(sync: true, saveOtherPlayerProfiles: false, waitForNextFrame: true);

            string result = Lang.F("Світ очищено і збережено. Об'єктів: {0}, предметів на землі/стійках: {1}, " +
                "предметів у скринях: {2} (скринь: {3}).", status.FlaggedObjects, status.FlaggedItemDrops, status.FlaggedContainerItems, status.FlaggedContainers);
            if (removeCheatedKeys && status.CheatedWorldKeys.Count > 0)
            {
                result += Lang.T("\nПрибрано модифікатори: ") + string.Join(", ", status.CheatedWorldKeys);
            }
            if (status.UncleanableItems.Count > 0)
            {
                result += Lang.T("\nУ скринях лишились предмети, які гра знову позначить (урон > 10000), викинь їх: ") + string.Join(", ", status.UncleanableItems);
            }
            return result;
        }

        private static WorldStatus ProcessWorld(bool apply, bool removeCheatedKeys)
        {
            WorldStatus status = new WorldStatus();
            try
            {
                if (ZNet.instance == null || ZNet.World == null || ZDOMan.instance == null)
                {
                    status.Error = Lang.T("зайди у світ.");
                    return status;
                }
                if (!ZNet.instance.IsServer())
                {
                    status.Error = Lang.T("перевірити й очистити весь світ може лише хост.");
                    return status;
                }
                EnsureHashes();

                try
                {
                    status.CheatedWorldKeys = GetCheatedWorldKeys();
                    if (apply && removeCheatedKeys)
                    {
                        RemoveWorldKeys(status.CheatedWorldKeys);
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e);
                    status.KeysError = Lang.T("не вдалося прочитати модифікатори світу");
                }

                ProcessFlags(status, apply);
                ProcessSerializedItems(status, apply);
                if (apply)
                {
                    CleanLoadedObjects();
                }
                Log.Info($"World {(apply ? "clean" : "scan")}: objects={status.FlaggedObjects} drops={status.FlaggedItemDrops} " +
                    $"containers={status.FlaggedContainers}/{status.FlaggedContainerItems} unreadable={status.UnreadableRecords} keys=[{string.Join(", ", status.CheatedWorldKeys)}]");
            }
            catch (Exception e)
            {
                Log.Error(e);
                status.Error = e.Message;
            }
            return status;
        }

        // Same rule as ServerOptionsGUI.WorldContainsCheatedModifiers.
        private static List<string> GetCheatedWorldKeys()
        {
            List<string> possible = (List<string>)s_possibleGlobalKeyValues.Invoke(null, null);
            List<string> result = new List<string>();
            foreach (string key in ZNet.World.m_startingGlobalKeys)
            {
                if (!key.StartsWith("preset") && !possible.Contains(key))
                {
                    result.Add(key);
                }
            }
            return result;
        }

        private static void RemoveWorldKeys(List<string> keys)
        {
            foreach (string key in keys)
            {
                ZNet.World.m_startingGlobalKeys.Remove(key);
                ZoneSystem.instance.RemoveGlobalKey(key.Split(' ')[0]);
            }
        }

        // Bool markers are stored as ints. ZDO.Set is used (not Remove*) because only Set bumps the
        // data revision and marks the chunk dirty, otherwise the change would never be saved.
        private static void ProcessFlags(WorldStatus status, bool apply)
        {
            foreach (ZDOID zid in CopyKeys((IDictionary)s_extraInts.GetValue(null)))
            {
                bool flagged = false;
                foreach (int hash in s_flagHashes)
                {
                    if (ZDOExtraData.GetInt(zid, hash, out int value) && value == 1)
                    {
                        flagged = true;
                        if (apply)
                        {
                            ZDOMan.instance.GetZDO(zid)?.Set(hash, false);
                        }
                    }
                }
                if (flagged)
                {
                    status.FlaggedObjects++;
                }
            }
        }

        private static void ProcessSerializedItems(WorldStatus status, bool apply)
        {
            foreach (ZDOID zid in CopyKeys((IDictionary)s_extraByteArrays.GetValue(null)))
            {
                // Containers, tombstones, ships, carts.
                if (ZDOExtraData.GetByteArray(zid, ZDOVars.s_items, out byte[] inventory) && inventory != null)
                {
                    int count = ClearInventoryBytes(inventory, status, out byte[] cleaned);
                    if (count > 0)
                    {
                        status.FlaggedContainers++;
                        status.FlaggedContainerItems += count;
                        if (apply)
                        {
                            ZDOMan.instance.GetZDO(zid)?.Set(ZDOVars.s_items, cleaned);
                        }
                    }
                }
                // Items on the ground ("itemData") and item/armor stand slots ("N_itemData").
                foreach (int hash in s_itemDataHashes)
                {
                    if (ZDOExtraData.GetByteArray(zid, hash, out byte[] itemData) && itemData != null && itemData.Length > 2
                        && ClearItemDataBytes(itemData, status, out byte[] cleaned))
                    {
                        status.FlaggedItemDrops++;
                        if (apply)
                        {
                            ZDOMan.instance.GetZDO(zid)?.Set(hash, cleaned);
                        }
                    }
                }
            }
        }

        // Inventory.Save format: int version, ushort count, ItemData records. The cheated flag is the
        // last byte of each record, so it is cleared in place: nothing else in the data is rewritten,
        // and items with prefabs unknown to this game version are preserved as is.
        private static int ClearInventoryBytes(byte[] data, WorldStatus status, out byte[] cleaned)
        {
            cleaned = null;
            try
            {
                ZPackage pkg = new ZPackage(data);
                Version.Item version = (Version.Item)pkg.ReadInt();
                if (version < Version.Item.Smaller || !HasCheatedByte(version))
                {
                    return 0;
                }
                int count = pkg.ReadUShort();
                List<int> offsets = null;
                for (int i = 0; i < count; i++)
                {
                    ItemDrop.ItemData item = new ItemDrop.ItemData();
                    int prefabHash = ItemDrop.ItemData.Load(pkg, item, version);
                    string dangerous = DangerousPrefabName(prefabHash);
                    if (dangerous != null)
                    {
                        status.UncleanableItems.Add(dangerous);
                    }
                    else if (item.m_cheated)
                    {
                        (offsets ?? (offsets = new List<int>())).Add(pkg.GetPos() - 1);
                    }
                }
                if (offsets == null)
                {
                    return 0;
                }
                cleaned = (byte[])data.Clone();
                foreach (int offset in offsets)
                {
                    cleaned[offset] = (byte)(cleaned[offset] & 0xFE);
                }
                return offsets.Count;
            }
            catch (Exception)
            {
                status.UnreadableRecords++;
                return 0;
            }
        }

        // ItemDrop.SaveToZDO format: byte version, one ItemData record.
        private static bool ClearItemDataBytes(byte[] data, WorldStatus status, out byte[] cleaned)
        {
            cleaned = null;
            try
            {
                ZPackage pkg = new ZPackage(data);
                Version.Item version = (Version.Item)pkg.ReadByte();
                if (!HasCheatedByte(version))
                {
                    return false;
                }
                ItemDrop.ItemData item = new ItemDrop.ItemData();
                ItemDrop.ItemData.Load(pkg, item, version);
                if (!item.m_cheated)
                {
                    return false;
                }
                cleaned = (byte[])data.Clone();
                int offset = pkg.GetPos() - 1;
                cleaned[offset] = (byte)(cleaned[offset] & 0xFE);
                return true;
            }
            catch (Exception)
            {
                status.UnreadableRecords++;
                return false;
            }
        }

        // Objects currently loaded in the scene keep their own copies of the data in memory.
        private static void CleanLoadedObjects()
        {
            if (s_itemDropInstances?.GetValue(null) is IList drops)
            {
                foreach (object obj in drops)
                {
                    if (obj is ItemDrop drop && drop != null && drop.m_itemData != null && !IsUncleanable(drop.m_itemData))
                    {
                        drop.m_itemData.m_cheated = false;
                    }
                }
            }
            foreach (Container container in UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None))
            {
                Inventory inventory = container.GetInventory();
                if (inventory == null)
                {
                    continue;
                }
                foreach (ItemDrop.ItemData item in inventory.GetAllItems())
                {
                    if (!IsUncleanable(item))
                    {
                        item.m_cheated = false;
                    }
                }
            }
        }

        // ---------------------------------------------------------------- helpers

        private static void EnsureHashes()
        {
            if (s_flagHashes != null)
            {
                return;
            }
            HashSet<int> flags = new HashSet<int> { ZDOVars.s_cheated, ZDOVars.s_cheatedQueued };
            // CookingStation stores one flag per slot as s_cheatedQueued + slot.
            for (int slot = 0; slot < MaxQueuedSlots; slot++)
            {
                flags.Add(ZDOVars.s_cheatedQueued + slot);
            }
            s_flagHashes = new List<int>(flags).ToArray();

            List<int> itemData = new List<int> { ZDOVars.s_itemData };
            for (int index = 0; index < MaxIndexedItemSlots; index++)
            {
                itemData.Add((index + "_itemData").GetStableHashCode());
            }
            s_itemDataHashes = itemData.ToArray();
        }

        private static bool HasCheatedByte(Version.Item version)
        {
            return version >= Version.Item.ChunksNCheats || version == Version.Item.AbandonedDN;
        }

        private static bool IsUncleanable(ItemDrop.ItemData item)
        {
            return item.GetDamage().GetTotalDamage() > MaxSafeItemDamage;
        }

        private static string DangerousPrefabName(int prefabHash)
        {
            if (prefabHash == 0 || ObjectDB.instance == null)
            {
                return null;
            }
            GameObject prefab = ObjectDB.instance.GetItemPrefab(prefabHash);
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            return drop != null && IsUncleanable(drop.m_itemData) ? prefab.name : null;
        }

        private static List<ZDOID> CopyKeys(IDictionary dictionary)
        {
            List<ZDOID> keys = new List<ZDOID>(dictionary.Count);
            foreach (object key in dictionary.Keys)
            {
                keys.Add((ZDOID)key);
            }
            return keys;
        }
    }
}
