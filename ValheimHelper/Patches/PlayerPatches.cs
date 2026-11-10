using System.Collections.Generic;
using HarmonyLib;

namespace ValheimHelper.Patches
{
    // God mode is implemented by dropping damage for the local player only.
    // Player.m_godMode is NOT used: the game checks InGodMode() when a creature is hit and marks
    // that creature (and its drops) as cheated.
    [HarmonyPatch(typeof(Character), "RPC_Damage")]
    internal static class GodModeRpcDamage
    {
        private static bool Prefix(Character __instance)
        {
            return !(State.GodMode && Util.IsLocalPlayer(__instance));
        }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
    internal static class GodModeApplyDamage
    {
        private static bool Prefix(Character __instance)
        {
            return !(State.GodMode && Util.IsLocalPlayer(__instance));
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.UseStamina))]
    internal static class InfiniteStaminaUse
    {
        private static bool Prefix(Player __instance)
        {
            return !(State.InfiniteStamina && Util.IsLocalPlayer(__instance));
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.HaveStamina))]
    internal static class InfiniteStaminaHave
    {
        private static void Postfix(Player __instance, ref bool __result)
        {
            if (State.InfiniteStamina && Util.IsLocalPlayer(__instance))
            {
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.UseEitr))]
    internal static class InfiniteEitrUse
    {
        private static bool Prefix(Player __instance)
        {
            return !(State.InfiniteEitr && Util.IsLocalPlayer(__instance));
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.HaveEitr))]
    internal static class InfiniteEitrHave
    {
        private static void Postfix(Player __instance, ref bool __result)
        {
            if (State.InfiniteEitr && Util.IsLocalPlayer(__instance))
            {
                __result = true;
            }
        }
    }

    // Keeps every eaten food at full duration, so max health/stamina/eitr never drop.
    [HarmonyPatch(typeof(Player), "UpdateFood")]
    internal static class FoodNeverExpires
    {
        private static readonly AccessTools.FieldRef<Player, List<Player.Food>> s_foods =
            AccessTools.FieldRefAccess<Player, List<Player.Food>>("m_foods");

        private static void Prefix(Player __instance)
        {
            if (!State.FoodNeverExpires || !Util.IsLocalPlayer(__instance))
            {
                return;
            }
            foreach (Player.Food food in s_foods(__instance))
            {
                food.m_time = food.m_item.m_shared.m_foodBurnTime;
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.IsEncumbered))]
    internal static class NoWeightLimit
    {
        private static void Postfix(Player __instance, ref bool __result)
        {
            if (State.NoWeightLimit && Util.IsLocalPlayer(__instance))
            {
                __result = false;
            }
        }
    }

    // Armor is the one durability drain that ignores Game.m_durabilityRate.
    [HarmonyPatch(typeof(Player), "DamageArmorDurability")]
    internal static class NoArmorDurabilityLoss
    {
        private static bool Prefix(Player __instance)
        {
            return !(State.NoDurabilityLoss && Util.IsLocalPlayer(__instance));
        }
    }

    // Damage multiplier on the attacker side: every melee hit, projectile and area hit of the local
    // player gets its own fresh HitData and passes through SEMan.ModifyAttack. The target's game marks a
    // creature as cheated when one hit exceeds 99999 after backstab (xN), stagger (x2), weakness (up to x2)
    // and the world player-damage rate, so the total is capped below that worst case. Items are untouched.
    [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyAttack))]
    internal static class DamageMultiplier
    {
        private const float CheatThreshold = 99999f;
        private const float SafetyMargin = 0.9f;
        private const float StaggerTimesWeakness = 4f;

        private static readonly AccessTools.FieldRef<SEMan, Character> s_character =
            AccessTools.FieldRefAccess<SEMan, Character>("m_character");

        internal static float MaxSafeHit(float backstabBonus)
        {
            return CheatThreshold * SafetyMargin
                / (UnityEngine.Mathf.Max(1f, backstabBonus) * StaggerTimesWeakness * UnityEngine.Mathf.Max(1f, Game.m_playerDamageRate));
        }

        private static void Postfix(SEMan __instance, ref HitData hitData)
        {
            if (State.DamageMultiplier == 1f || hitData == null || !Util.IsLocalPlayer(s_character(__instance)))
            {
                return;
            }
            hitData.m_damage.Modify(State.DamageMultiplier);
            float cap = MaxSafeHit(hitData.m_backstabBonus);
            float total = hitData.m_damage.GetTotalDamage();
            if (total > cap)
            {
                hitData.m_damage.Modify(cap / total);
            }
        }
    }

    // While the menu is open the character must not walk/attack when typing in the search field.
    [HarmonyPatch(typeof(Player), "TakeInput")]
    internal static class MenuBlocksPlayerInput
    {
        private static void Postfix(ref bool __result)
        {
            if (State.MenuOpen)
            {
                __result = false;
            }
        }
    }
}
