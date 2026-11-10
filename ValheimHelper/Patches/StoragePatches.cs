using System.Collections.Generic;
using HarmonyLib;
using ValheimHelper.Storage;

namespace ValheimHelper.Patches
{
    // The owner's answer to a chest request sent by the storage sorter is handled by the sorter;
    // answers to the player's own "place stacks" still run the game's code, and the chest remembers what was stacked.
    [HarmonyPatch(typeof(Container), "RPC_StackResponse")]
    internal static class StorageStackResponse
    {
        private static bool Prefix(Container __instance, bool granted, out Dictionary<string, int> __state)
        {
            __state = null;
            if (StorageSorter.OnStackResponse(__instance, granted))
            {
                return false;
            }
            if (granted)
            {
                __state = ChestMemory.CountBefore(__instance);
            }
            return true;
        }

        private static void Postfix(Container __instance, Dictionary<string, int> __state)
        {
            ChestMemory.RememberAdded(__instance, __state);
        }
    }

    // Hand moves in the chest window: drag and drop, shift-click, splitting onto the chest grid.
    [HarmonyPatch(typeof(InventoryGui), "OnSelectedItem")]
    internal static class ChestMemoryHandMove
    {
        private static void Prefix(out KeyValuePair<Container, Dictionary<string, int>> __state)
        {
            Container container = ChestMemory.OpenContainer();
            __state = new KeyValuePair<Container, Dictionary<string, int>>(container, ChestMemory.CountBefore(container));
        }

        private static void Postfix(KeyValuePair<Container, Dictionary<string, int>> __state)
        {
            ChestMemory.RememberAdded(__state.Key, __state.Value);
        }
    }

    // The "place stacks" button of the chest window.
    [HarmonyPatch(typeof(InventoryGui), "OnStackAll")]
    internal static class ChestMemoryStackAll
    {
        private static void Prefix(out KeyValuePair<Container, Dictionary<string, int>> __state)
        {
            Container container = ChestMemory.OpenContainer();
            __state = new KeyValuePair<Container, Dictionary<string, int>>(container, ChestMemory.CountBefore(container));
        }

        private static void Postfix(KeyValuePair<Container, Dictionary<string, int>> __state)
        {
            ChestMemory.RememberAdded(__state.Key, __state.Value);
        }
    }
}
