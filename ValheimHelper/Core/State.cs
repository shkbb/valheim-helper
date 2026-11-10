namespace ValheimHelper
{
    // Runtime toggles. Settings keeps them (and the menu window position and tab) between game launches.
    internal static class State
    {
        internal static bool MenuOpen;
        // A text field of the menu has keyboard focus, so key binds must not fire.
        internal static bool GuiTextFocus;
        // Menu window in GUI coordinates (top-left origin, real pixels), for blocking the mouse wheel over it.
        internal static UnityEngine.Rect MenuScreenRect;
        // Menu window position (menu units) and open tab, restored on launch.
        internal static float MenuX = 60f;
        internal static float MenuY = 60f;
        internal static float MenuW = 880f;
        internal static float MenuH = 720f;
        // Picture: brightness (exposure stops), gamma (midtones, -1..1) and glow strength (bloom, 0..1).
        internal static float ImageBrightness;
        internal static float ImageGamma;
        internal static float ImageGlow = 1f;
        // Tab number of versions before the sections; read only to open the same place after an update.
        internal static int MenuTab = -1;
        internal static int MenuSection = -1;
        // The in-game wiki: the language of its articles (apart from the menu language) and the page open last.
        internal static string WikiLanguage = "";
        internal static string WikiPage = "";
        // The guide brings in changed articles from the wiki by itself (WikiUpdate).
        internal static bool WikiAutoUpdate = true;
        internal static int MenuPage;
        // Index in PlayerTools.DurationNames: how long an effect put on with "Накласти" lasts.
        internal static int EffectDuration;

        internal static bool GodMode;
        internal static bool InfiniteStamina;
        internal static bool InfiniteEitr;
        internal static bool FoodNeverExpires;
        internal static bool NoWeightLimit;
        internal static bool NoDurabilityLoss;

        internal static bool Fly;
        internal static bool LandingProtection;
        internal static bool MapTeleport;
        internal static bool TeleportAnything;
        internal static bool WalkUnderwater;

        internal static bool FreeBuild;
        internal static bool NoSupportLimit;

        internal static bool BypassCheatChecks;

        // 1 = unchanged
        internal static float RunSpeedMultiplier = 1f;
        internal static float DamageMultiplier = 1f;
        internal static float LootMultiplier = 1f;
        internal static float WorldClockMultiplier = 1f;
        internal static float GameSpeed = 1f;
        internal static bool SyncWithFriends = true;
    }

    internal static class Util
    {
        internal static bool IsLocalPlayer(Character character)
        {
            return (object)character != null && ReferenceEquals(character, Player.m_localPlayer);
        }
    }
}
