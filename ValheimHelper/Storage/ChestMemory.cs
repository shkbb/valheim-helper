using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper.Storage
{
    // "Пам'ять сундука": when the player puts an item into a chest by hand (drag, shift-click, "place stacks"), the
    // chest remembers it in its ZDO, so everyone with the mod shares it. The stash then sends that item to the chest
    // where it was put by hand most recently, even when that chest is empty now. Moves made by the mod itself are not
    // remembered. The rules are in ChestMemoryRules.
    internal static class ChestMemory
    {
        internal const string Key = "ValheimHelper_chestMemory";
        private const float PendingSec = 5f;

        private static readonly AccessTools.FieldRef<InventoryGui, Container> s_currentContainer =
            AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer");

        private sealed class PendingWrite
        {
            public Container Container;
            public readonly List<string> Names = new List<string>();
            public long Time;
            public float Until;
        }

        private static readonly List<PendingWrite> s_pending = new List<PendingWrite>();

        internal static Container OpenContainer()
        {
            return InventoryGui.instance != null ? s_currentContainer(InventoryGui.instance) : null;
        }

        internal static Dictionary<string, long> Read(Container container)
        {
            ZNetView view = StorageSorter.View(container);
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            return ChestMemoryRules.Parse(zdo != null ? zdo.GetString(Key, "") : "");
        }

        // Item counts by shared name, taken right before a hand move.
        internal static Dictionary<string, int> CountBefore(Container container)
        {
            if (!StorageSettings.RememberHandPlacement || container == null || container.GetInventory() == null)
            {
                return null;
            }
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (ItemDrop.ItemData item in container.GetInventory().GetAllItems())
            {
                string name = item.m_shared.m_name;
                counts[name] = (counts.TryGetValue(name, out int count) ? count : 0) + item.m_stack;
            }
            return counts;
        }

        // Remembers every item of which the chest holds more than before the hand move.
        internal static void RememberAdded(Container container, Dictionary<string, int> before)
        {
            if (before == null || container == null || container.GetInventory() == null || ZNet.instance == null
                || !StorageSettings.RememberHandPlacement || !StorageSorter.IsStorageChest(container))
            {
                return;
            }
            PendingWrite write = null;
            Dictionary<string, int> after = CountBefore(container);
            foreach (KeyValuePair<string, int> pair in after)
            {
                if (pair.Value > (before.TryGetValue(pair.Key, out int count) ? count : 0))
                {
                    write = write ?? new PendingWrite
                    {
                        Container = container,
                        // Tenths of a second of world time, the same clock on every client.
                        Time = (long)(ZNet.instance.GetTimeSeconds() * 10.0),
                        Until = Time.unscaledTime + PendingSec,
                    };
                    write.Names.Add(pair.Key);
                }
            }
            if (write != null && !TryWrite(write))
            {
                // The chest is being handed over right now; written as soon as this client owns it.
                s_pending.Add(write);
            }
        }

        private static bool TryWrite(PendingWrite write)
        {
            ZNetView view = StorageSorter.View(write.Container);
            if (view == null || !view.IsValid() || !view.IsOwner())
            {
                return false;
            }
            ZDO zdo = view.GetZDO();
            Dictionary<string, long> entries = ChestMemoryRules.Parse(zdo.GetString(Key, ""));
            if (ChestMemoryRules.Remember(entries, write.Names, write.Time))
            {
                zdo.Set(Key, ChestMemoryRules.Format(entries));
            }
            return true;
        }

        internal static void Tick()
        {
            for (int i = s_pending.Count - 1; i >= 0; i--)
            {
                PendingWrite write = s_pending[i];
                if (write.Container == null || TryWrite(write) || Time.unscaledTime > write.Until)
                {
                    s_pending.RemoveAt(i);
                }
            }
        }

        internal static string Forget(Container container)
        {
            if (container == null || !StorageSorter.IsStorageChest(container))
            {
                return Lang.T("Ця скриня недоступна.");
            }
            s_pending.RemoveAll(write => write.Container == container);
            StorageSorter.SetZdoString(container, Key, "");
            return Lang.T("Скриня забула, що в неї клали руками.");
        }
    }
}
