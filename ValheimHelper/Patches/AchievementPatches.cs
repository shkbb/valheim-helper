using HarmonyLib;

namespace ValheimHelper.Patches
{
    // Runtime equivalent of the game's own "bypasscheatchecks" player key, without running the
    // console command (which itself marks the character with m_usedCheats).
    // With it CanGetAchievements() returns true even for an already tainted world/character/items,
    // and newly produced objects are no longer marked as cheated.
    [HarmonyPatch(typeof(PlayerProfile), nameof(PlayerProfile.s_bypassCheatChecks), MethodType.Getter)]
    internal static class BypassCheatChecksGetter
    {
        private static void Postfix(ref bool __result)
        {
            if (State.BypassCheatChecks)
            {
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(Achievements), nameof(Achievements.IsCheatedAtAll))]
    internal static class BypassIsCheatedAtAll
    {
        private static void Postfix(ref bool __result)
        {
            if (State.BypassCheatChecks)
            {
                __result = false;
            }
        }
    }
}
