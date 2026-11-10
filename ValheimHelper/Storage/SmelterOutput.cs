using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper.Storage
{
    // What a smelter, kiln, windmill, spinning wheel or refinery makes goes straight into a chest nearby instead of
    // falling out: the chest this item was last put in by hand, or else one that already holds it. When no such chest
    // has room, it falls out as usual. Works on the client that runs the station (whoever is nearest), and only
    // with chests that client owns, so nothing is written to a chest someone else is changing.
    internal static class SmelterOutput
    {
        // Prefix of Smelter.Spawn (runs on the station's owner). Returns false when the item went into a chest.
        internal static bool BeforeSpawn(Smelter smelter, string ore, int stack)
        {
            if (!StorageSettings.SmelterToChests || smelter == null || stack <= 0)
            {
                return true;
            }
            ZNetView view = smelter.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || !view.IsOwner())
            {
                return true;
            }
            Smelter.ItemConversion conversion = null;
            foreach (Smelter.ItemConversion candidate in smelter.m_conversion)
            {
                if (candidate.m_from == null || candidate.m_from.gameObject.name == ore)
                {
                    conversion = candidate;
                    break;
                }
            }
            if (conversion == null || conversion.m_to == null)
            {
                return true;
            }
            GameObject prefab = conversion.m_to.gameObject;
            Container chest = Target(smelter.transform.position, conversion.m_to.m_itemData.m_shared.m_name, prefab, stack);
            if (chest == null)
            {
                return true;
            }
            // Same cheat mark the game would give the dropped item.
            ZDO zdo = view.GetZDO();
            bool cheated = (zdo.GetBool(ZDOVars.s_cheatedQueued) || zdo.GetBool(ZDOVars.s_cheated)) && !PlayerProfile.s_bypassCheatChecks;
            if (chest.GetInventory().AddItem(prefab.name, stack, 1, 0, 0L, "", cheated) == null)
            {
                return true;
            }
            smelter.m_produceEffects.Create(smelter.transform.position, smelter.transform.rotation);
            return false;
        }

        private static Container Target(Vector3 station, string sharedName, GameObject prefab, int stack)
        {
            Container remembered = null;
            long rememberedAt = long.MinValue;
            Container holding = null;
            foreach (Container chest in StorageSorter.ChestsAround(station, StorageSettings.Radius))
            {
                ZNetView chestView = StorageSorter.View(chest);
                if (chestView == null || !chestView.IsOwner() || StorageSorter.InUseByOther(chest) || !chest.GetInventory().CanAddItem(prefab, stack))
                {
                    continue;
                }
                Dictionary<string, long> memory = ChestMemory.Read(chest);
                if (memory.TryGetValue(sharedName, out long at) && at > rememberedAt)
                {
                    remembered = chest;
                    rememberedAt = at;
                }
                if (holding == null && chest.GetInventory().ContainsItemByName(sharedName))
                {
                    holding = chest;
                }
            }
            return remembered ?? holding;
        }
    }
}
