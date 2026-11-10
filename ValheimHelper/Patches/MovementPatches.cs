using System;
using HarmonyLib;

namespace ValheimHelper.Patches
{
    // Fly reuses the game's own UpdateDebugFly movement, but without setting Player.m_debugFly:
    // IsDebugFlying() reports true only while Character.UpdateMotion runs for the local player.
    // Everywhere else (damage/cheat checks, the synced ZDO flag) the player is not "debug flying".
    [HarmonyPatch(typeof(Character), "UpdateMotion")]
    internal static class FlyMotionScope
    {
        internal static bool Active;

        private static readonly AccessTools.FieldRef<Character, float> s_maxAirAltitude =
            AccessTools.FieldRefAccess<Character, float>("m_maxAirAltitude");

        private static void Prefix(Character __instance)
        {
            if (!Util.IsLocalPlayer(__instance))
            {
                Active = false;
                return;
            }
            Active = State.Fly;
            if (!State.Fly && State.LandingProtection)
            {
                // After turning fly off in the air: keep resetting the fall start so landing does no damage.
                s_maxAirAltitude(__instance) = __instance.transform.position.y;
                if (__instance.IsOnGround())
                {
                    State.LandingProtection = false;
                }
            }
        }

        private static Exception Finalizer(Exception __exception)
        {
            Active = false;
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.IsDebugFlying))]
    internal static class FlyReportsDebugFlyDuringMotion
    {
        private static void Postfix(Player __instance, ref bool __result)
        {
            if (FlyMotionScope.Active && Util.IsLocalPlayer(__instance))
            {
                __result = true;
            }
        }
    }

    // Fast run: scales the same factors that skills and equipment already use for jogging/running.
    [HarmonyPatch(typeof(Player), "GetRunSpeedFactor")]
    internal static class FastRun
    {
        private static void Postfix(Player __instance, ref float __result)
        {
            if (State.RunSpeedMultiplier != 1f && Util.IsLocalPlayer(__instance))
            {
                __result *= State.RunSpeedMultiplier;
            }
        }
    }

    [HarmonyPatch(typeof(Player), "GetJogSpeedFactor")]
    internal static class FastJog
    {
        private static void Postfix(Player __instance, ref float __result)
        {
            if (State.RunSpeedMultiplier != 1f && Util.IsLocalPlayer(__instance))
            {
                __result *= State.RunSpeedMultiplier;
            }
        }
    }

    // Swimming uses the plain m_swimSpeed field, so it is scaled only for the duration of the call.
    [HarmonyPatch(typeof(Character), "UpdateSwimming")]
    internal static class FastSwim
    {
        private static float s_originalSpeed;
        private static bool s_scaled;

        private static void Prefix(Character __instance)
        {
            if (State.RunSpeedMultiplier == 1f || !Util.IsLocalPlayer(__instance))
            {
                return;
            }
            s_originalSpeed = __instance.m_swimSpeed;
            __instance.m_swimSpeed *= State.RunSpeedMultiplier;
            s_scaled = true;
        }

        private static Exception Finalizer(Character __instance, Exception __exception)
        {
            if (s_scaled)
            {
                __instance.m_swimSpeed = s_originalSpeed;
                s_scaled = false;
            }
            return __exception;
        }
    }

    // "Ходити по дну": the local player never counts as being at swimming depth, so it sinks to the bottom
    // and keeps weapons and tools in hand (the game hides them and blocks equipping only while swimming).
    // Mining, building, levelling ground and fighting then work as on land. Nothing is flagged.
    [HarmonyPatch(typeof(Character), "InLiquidSwimDepth", new Type[0])]
    internal static class WalkUnderwaterDepth
    {
        private static void Postfix(Character __instance, ref bool __result)
        {
            if (__result && State.WalkUnderwater && Util.IsLocalPlayer(__instance))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(Character), "InLiquidSwimDepth", new[] { typeof(float) })]
    internal static class WalkUnderwaterDepthValue
    {
        private static void Postfix(Character __instance, ref bool __result)
        {
            if (__result && State.WalkUnderwater && Util.IsLocalPlayer(__instance))
            {
                __result = false;
            }
        }
    }

    // Portals refuse ores/metals; this lifts that for the local player's inventory.
    // Items with toolTier >= 1000 are special quest items the game never lets through, keep that.
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.IsTeleportable))]
    internal static class TeleportAnything
    {
        private static void Postfix(Inventory __instance, ref bool __result)
        {
            if (__result || !State.TeleportAnything || Player.m_localPlayer == null || __instance != Player.m_localPlayer.GetInventory())
            {
                return;
            }
            foreach (ItemDrop.ItemData item in __instance.GetAllItems())
            {
                if (item.m_shared.m_toolTier >= 1000)
                {
                    return;
                }
            }
            __result = true;
        }
    }
}
