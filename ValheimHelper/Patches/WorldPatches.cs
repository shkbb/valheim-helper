using HarmonyLib;
using UnityEngine;

namespace ValheimHelper.Patches
{
    // Loot multiplier without the world "resourcerate" key. Every drop (creatures, drop tables in
    // dungeons/chests, trees, rocks, pickables) goes through Game.ScaleDrops, which reads
    // Game.m_resourceRate. The world keys, the modifier summary and the achievement difficulty stay
    // exactly as they were; only the runtime rate is scaled after the game recomputes it from the keys.
    [HarmonyPatch(typeof(Game), nameof(Game.UpdateWorldRates))]
    internal static class LootMultiplier
    {
        private static void Postfix()
        {
            if (State.LootMultiplier != 1f)
            {
                Game.m_resourceRate *= State.LootMultiplier;
            }
            // Every durability drain of weapons, tools, shields and torches is multiplied by this rate
            // (the world "durabilityrate" key itself stays untouched).
            if (State.NoDurabilityLoss)
            {
                Game.m_durabilityRate = 0f;
            }
        }
    }

    // Game.UpdatePause resets Time.timeScale every frame (and forces 1 while anyone is connected).
    [HarmonyPatch(typeof(Game), "UpdatePause")]
    internal static class GameSpeed
    {
        private static void Postfix()
        {
            if (State.GameSpeed != 1f && Time.timeScale > 0f)
            {
                Time.timeScale = State.GameSpeed;
            }
        }
    }
}
