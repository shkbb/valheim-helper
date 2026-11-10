using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using ValheimHelper.Storage;

namespace ValheimHelper.Patches
{
    // Crafting and building with materials from chests around (see CraftFromChests).
    //
    // Chest counting is switched on only inside game methods that merely display requirements. The checks right
    // before an item is crafted (InventoryGui.DoCrafting) or a piece is placed (Player.UpdatePlacement) always see
    // the real inventory, and the materials are moved into the inventory before those checks.

    // The local player's inventory count also includes usable chests while counting is on.
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.CountItems))]
    internal static class CraftCountsChests
    {
        private static void Postfix(Inventory __instance, string name, int quality, bool matchWorldLevel, ref int __result)
        {
            if (name == null || !CraftFromChests.Counting)
            {
                return;
            }
            Player player = Player.m_localPlayer;
            if (player != null && ReferenceEquals(__instance, player.GetInventory()))
            {
                __result += CraftFromChests.CountInChests(name, quality, matchWorldLevel);
            }
        }
    }

    // "Hide unavailable pieces" only asks whether any piece of a material is there.
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.HaveItem), new[] { typeof(string), typeof(bool) })]
    internal static class BuildHaveItemCountsChests
    {
        private static void Postfix(Inventory __instance, string name, bool matchWorldLevel, ref bool __result)
        {
            if (__result || name == null || !CraftFromChests.Counting)
            {
                return;
            }
            Player player = Player.m_localPlayer;
            if (player != null && ReferenceEquals(__instance, player.GetInventory()))
            {
                __result = CraftFromChests.CountInChests(name, -1, matchWorldLevel) > 0;
            }
        }
    }

    // ---------------------------------------------------------------- crafting window

    // The "Craft" button and the recipe list call this; counted only while the window is visible and no craft is running.
    [HarmonyPatch(typeof(Player), "HaveRequirementItems")]
    internal static class CraftRequirementScope
    {
        private static void Prefix(bool discover, out bool __state)
        {
            __state = !discover;
            if (__state)
            {
                CraftFromChests.VirtualScope++;
            }
        }

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state)
            {
                CraftFromChests.VirtualScope--;
            }
            return __exception;
        }
    }

    // The recipe list. InventoryGui.Show builds it before the window counts as visible, and DoCrafting rebuilds it
    // right after spending the materials; both are display only.
    [HarmonyPatch(typeof(InventoryGui), "UpdateCraftingPanel")]
    internal static class CraftPanelDisplay
    {
        private static void Prefix()
        {
            CraftFromChests.CraftDisplay++;
        }

        private static Exception Finalizer(Exception __exception)
        {
            CraftFromChests.CraftDisplay--;
            return __exception;
        }
    }

    // Real crafting only ever looks at the inventory.
    [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
    internal static class CraftUsesInventoryOnly
    {
        private static void Prefix()
        {
            CraftFromChests.Crafting = true;
        }

        private static Exception Finalizer(Exception __exception)
        {
            CraftFromChests.Crafting = false;
            return __exception;
        }
    }

    // The amount picked next to the craft button turns on the game's own multi-craft with that amount (CraftAmount).
    // Runs before the chests are asked, so they hand over materials for the whole amount.
    [HarmonyPatch]
    internal static class CraftAmountApply
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(InventoryGui), "UpdateRecipe");
            yield return AccessTools.Method(typeof(InventoryGui), "OnCraftPressed");
        }

        [HarmonyPriority(Priority.First)]
        private static void Prefix(InventoryGui __instance)
        {
            CraftAmount.Apply(__instance);
        }
    }

    // Pressing "Craft" first brings the missing materials over from the chests.
    [HarmonyPatch(typeof(InventoryGui), "OnCraftPressed")]
    internal static class CraftPullsFromChests
    {
        private static bool Prefix(InventoryGui __instance)
        {
            return CraftFromChests.BeforeCraftPressed(__instance);
        }

        private static void Postfix(InventoryGui __instance)
        {
            CraftFromChests.AfterCraftPressed(__instance);
        }
    }

    // ---------------------------------------------------------------- stations

    // What a station makes goes into a chest nearby when that is switched on (SmelterOutput).
    [HarmonyPatch(typeof(Smelter), "Spawn")]
    internal static class SmelterOutputToChests
    {
        private static bool Prefix(Smelter __instance, string ore, int stack)
        {
            return SmelterOutput.BeforeSpawn(__instance, ore, stack);
        }
    }

    // ---------------------------------------------------------------- building

    // Build menu buttons: colour set when the buttons are (re)built, e.g. when the menu opens.
    [HarmonyPatch(typeof(BuildUiPieceButton), nameof(BuildUiPieceButton.UpdateRequirements))]
    internal static class BuildButtonDisplay
    {
        private static void Prefix()
        {
            CraftFromChests.BuildDisplay++;
        }

        private static Exception Finalizer(Exception __exception)
        {
            CraftFromChests.BuildDisplay--;
            return __exception;
        }
    }

    // HUD build piece colours (the older piece bar).
    [HarmonyPatch]
    internal static class BuildHudStatusDisplay
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Hud), "UpdatePieceBuildStatus");
            yield return AccessTools.Method(typeof(Hud), "UpdatePieceBuildStatusAll");
        }

        private static void Prefix()
        {
            CraftFromChests.BuildDisplay++;
        }

        private static Exception Finalizer(Exception __exception)
        {
            CraftFromChests.BuildDisplay--;
            return __exception;
        }
    }

    // Which pieces are listed when "hide unavailable" is on (CanAlmostBuild). Which pieces are known is decided
    // elsewhere (IsKnown) and never counts chests.
    [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable))]
    internal static class BuildAvailableDisplay
    {
        private static void Prefix()
        {
            CraftFromChests.BuildDisplay++;
        }

        private static Exception Finalizer(Exception __exception)
        {
            CraftFromChests.BuildDisplay--;
            return __exception;
        }
    }

    // Placing a piece: right before the game checks the selected piece's requirements, the missing materials are
    // brought over from the chests. The build menu handling inside the same method is not a placement.
    [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), new[] { typeof(Piece), typeof(Player.RequirementMode) })]
    internal static class BuildPlacementPull
    {
        private static void Prefix(Player __instance, Piece piece, Player.RequirementMode mode)
        {
            if (CraftFromChests.Placing && !CraftFromChests.InBuildGui && mode == Player.RequirementMode.CanBuild
                && CraftFromChests.BuildDisplay == 0 && piece != null && Util.IsLocalPlayer(__instance) && piece == __instance.GetSelectedPiece())
            {
                CraftFromChests.BeforePlacementCheck(__instance, piece);
            }
        }
    }

    [HarmonyPatch(typeof(Player), "UpdatePlacement")]
    internal static class BuildPlacementScope
    {
        private static void Prefix(Player __instance)
        {
            CraftFromChests.Placing = Util.IsLocalPlayer(__instance);
        }

        private static Exception Finalizer(Exception __exception)
        {
            CraftFromChests.Placing = false;
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Player), "UpdateBuildGuiInput")]
    internal static class BuildGuiScope
    {
        private static void Prefix()
        {
            CraftFromChests.InBuildGui = true;
        }

        private static Exception Finalizer(Exception __exception)
        {
            CraftFromChests.InBuildGui = false;
            return __exception;
        }
    }

    // ---------------------------------------------------------------- requirement icons (crafting and building)

    // With chests counted, the amount reads "needed/available", available being the inventory plus chests around.
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetupRequirement))]
    internal static class RequirementIconsDisplay
    {
        // 0 = not counting, 1 = crafting, 2 = building
        private static void Prefix(bool craft, out int __state)
        {
            __state = craft ? (CraftFromChests.CraftEnabled ? 1 : 0) : (CraftFromChests.BuildEnabled ? 2 : 0);
            if (__state == 1)
            {
                CraftFromChests.CraftDisplay++;
            }
            else if (__state == 2)
            {
                CraftFromChests.BuildDisplay++;
            }
        }

        private static void Postfix(Transform elementRoot, Piece.Requirement req, Player player, int quality, int craftMultiplier, int __state)
        {
            if (__state == 0 || req?.m_resItem == null || elementRoot == null || player == null)
            {
                return;
            }
            string name = req.m_resItem.m_itemData.m_shared.m_name;
            int need = req.GetAmount(quality) * craftMultiplier;
            Transform amountRoot = elementRoot.Find("res_amount");
            TMP_Text amount = amountRoot != null ? amountRoot.GetComponent<TMP_Text>() : null;
            if (need > 0 && amount != null && amount.gameObject.activeSelf)
            {
                // Still inside the display scope, so this is the inventory plus the chests.
                amount.text = $"{need}/{player.GetInventory().CountItems(name)}";
            }
            int inChests = CraftFromChests.CountInChests(name, -1, true);
            UITooltip tooltip = elementRoot.GetComponent<UITooltip>();
            if (inChests > 0 && tooltip != null)
            {
                tooltip.m_text += Lang.F("\nУ скринях поруч: {0}", inChests);
            }
        }

        private static Exception Finalizer(Exception __exception, int __state)
        {
            if (__state == 1)
            {
                CraftFromChests.CraftDisplay--;
            }
            else if (__state == 2)
            {
                CraftFromChests.BuildDisplay--;
            }
            return __exception;
        }
    }
}
