using System;
using HarmonyLib;

namespace ValheimHelper.Patches
{
    // Free build without Player.m_noPlacementCost: TryPlacePiece marks a piece as cheated when
    // NoCostCheat() is true, so instead requirements are reported as met and resources are not consumed.
    [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), new[] { typeof(Piece), typeof(Player.RequirementMode) })]
    internal static class FreeBuildRequirements
    {
        private static bool Prefix(Player __instance, Player.RequirementMode mode, ref bool __result)
        {
            // IsKnown decides which pieces are unlocked in the build menu — leave progression untouched.
            if (!State.FreeBuild || mode == Player.RequirementMode.IsKnown || !Util.IsLocalPlayer(__instance))
            {
                return true;
            }
            __result = true;
            return false;
        }
    }

    // Passing the game's own noPlacementCost argument lists every piece in the build menu, exactly like
    // debugmode, while Player.m_knownRecipes stays untouched: turning free build off restores the learned list.
    [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable))]
    internal static class FreeBuildShowsAllPieces
    {
        private static void Prefix(Player player, ref bool noPlacementCost)
        {
            if (State.FreeBuild && Util.IsLocalPlayer(player))
            {
                noPlacementCost = true;
            }
        }
    }

    [HarmonyPatch(typeof(Player), "UpdatePlacement")]
    internal static class FreeBuildPlacementScope
    {
        internal static bool Active;

        private static void Prefix(Player __instance)
        {
            Active = State.FreeBuild && Util.IsLocalPlayer(__instance);
        }

        private static Exception Finalizer(Exception __exception)
        {
            Active = false;
            return __exception;
        }
    }

    // ConsumeResources is shared with crafting, so it is skipped only inside build placement.
    [HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources))]
    internal static class FreeBuildConsume
    {
        private static bool Prefix()
        {
            return !FreeBuildPlacementScope.Active;
        }
    }

    // Removing a piece needs its crafting station in range; the game skips that only for its own
    // m_noPlacementCost debug flag. Free build skips it too. Wards and no-build zones still apply, and the
    // piece drops its resources exactly like a normal removal (not marked as cheated).
    [HarmonyPatch(typeof(Player), "CheckCanRemovePiece")]
    internal static class FreeBuildRemoveWithoutStation
    {
        private static bool Prefix(Player __instance, ref bool __result)
        {
            if (!State.FreeBuild || !Util.IsLocalPlayer(__instance))
            {
                return true;
            }
            __result = true;
            return false;
        }
    }

    // Structures never collapse from missing support. Runs on whichever client owns the piece,
    // so both players need it enabled when building together.
    [HarmonyPatch(typeof(WearNTear), "GetMaterialProperties")]
    internal static class NoSupportLimitMaterial
    {
        private static void Postfix(ref float minSupport)
        {
            if (State.NoSupportLimit)
            {
                minSupport = 0f;
            }
        }
    }

    [HarmonyPatch(typeof(WearNTear), "HaveSupport")]
    internal static class NoSupportLimitHaveSupport
    {
        private static void Postfix(ref bool __result)
        {
            if (State.NoSupportLimit)
            {
                __result = true;
            }
        }
    }
}
