using UnityEngine.UI;

namespace ValheimHelper
{
    // Whether the game's own picture is free to draw on. Markers in the world and the icons on the minimap are drawn
    // with IMGUI, which always lies on top of the game's interface, while the game does not switch the minimap off:
    // it covers it (a crafting station's or chest's window, a trader, a lore text, the loading screen of a portal) or
    // moves the whole HUD away (Ctrl+F3, cutscenes). So the mod leaves them out whenever the game covers the view.
    internal static class GameScreen
    {
        internal static bool LoadingScreen =>
            Hud.instance != null && Hud.instance.m_loadingScreen != null && Hud.instance.m_loadingScreen.gameObject.activeInHierarchy
            && Hud.instance.m_loadingScreen.alpha > 0.01f;

        internal static bool HudShown => Hud.instance == null || (Hud.instance.IsVisible() && !Hud.IsUserHidden());

        // A window of the game over the world.
        internal static bool WindowOpen =>
            InventoryGui.IsVisible() || StoreGui.IsVisible() || Menu.IsVisible() || TextInput.IsVisible()
            || (TextViewer.instance != null && TextViewer.instance.IsVisible());

        // Markers in the world (the mod's marks, chests found, torches low on fuel).
        internal static bool WorldMarkers =>
            HudShown && !LoadingScreen && !WindowOpen && (Player.m_localPlayer == null || !Player.m_localPlayer.IsTeleporting());

        // The minimap in the corner is really seen: as the world markers, and not faded out.
        internal static bool MinimapShown(RawImage image)
        {
            return image != null && image.isActiveAndEnabled && WorldMarkers && image.canvasRenderer.GetInheritedAlpha() > 0.05f;
        }
    }
}
