using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValheimHelper.Map;

namespace ValheimHelper.Patches
{
    // The map panel is IMGUI drawn over the game's UGUI map, so clicks and the scroll wheel would also reach
    // the map underneath (dragging it, adding pins, zooming). These patches stop that while the pointer is
    // over the panel or over a map marker, and while the pen or the eraser is used on the map.
    [HarmonyPatch(typeof(Minimap), "OnMapLeftDown")]
    internal static class MapPanelBlocksDrag
    {
        private static bool Prefix()
        {
            return !MapOverlay.PointerOverPanel && !MapSketch.CapturesMouse && !Waypoints.CapturesMouse;
        }
    }

    [HarmonyPatch(typeof(Minimap), nameof(Minimap.OnMapLeftClick))]
    internal static class MapPanelBlocksLeftClick
    {
        private static bool Prefix()
        {
            return !MapOverlay.PointerOverPanel && MapOverlay.Hovered == null && !MapSketch.CapturesMouse && !Waypoints.CapturesMouse;
        }
    }

    // Double click normally opens the "new pin" name input.
    [HarmonyPatch(typeof(Minimap), nameof(Minimap.OnMapDblClick))]
    internal static class MapPanelBlocksDoubleClick
    {
        private static bool Prefix()
        {
            return !MapOverlay.PointerOverPanel && MapOverlay.Hovered == null && !MapSketch.CapturesMouse && !Waypoints.CapturesMouse;
        }
    }

    [HarmonyPatch(typeof(Minimap), nameof(Minimap.OnMapMiddleClick))]
    internal static class MapPanelBlocksMiddleClick
    {
        private static bool Prefix()
        {
            return !MapOverlay.PointerOverPanel && !MapSketch.CapturesMouse && !Waypoints.CapturesMouse;
        }
    }

    [HarmonyPatch(typeof(Minimap), "RemovePinUnderPointer")]
    internal static class MapPanelBlocksRightClick
    {
        private static bool Prefix()
        {
            return !MapOverlay.PointerOverPanel && !MapSketch.CapturesMouse && !Waypoints.CapturesMouse;
        }
    }

    // takeInput covers wheel zoom and map hotkeys; dragging is handled separately by the game.
    [HarmonyPatch(typeof(Minimap), "UpdateMap")]
    internal static class MapPanelBlocksZoom
    {
        private static void Prefix(ref bool takeInput)
        {
            if (MapOverlay.PointerOverPanel || MapOverlay.SearchFocused)
            {
                takeInput = false;
            }
        }
    }

    // While typing in the panel's search field, "M" must not close the map.
    [HarmonyPatch(typeof(Minimap), nameof(Minimap.InTextInput))]
    internal static class MapSearchIsTextInput
    {
        private static void Postfix(ref bool __result)
        {
            if (MapOverlay.SearchFocused)
            {
                __result = true;
            }
        }
    }

    // The game draws its own fixed-size icons for a few locations (traders, start temple, boss spots found with
    // a vegvisir). Where the overlay already draws its marker (large map, and the minimap when enabled), the game's
    // icon there is removed, so there are no doubles.
    [HarmonyPatch(typeof(Minimap), "UpdatePins")]
    internal static class MapHidesDuplicateGameIcons
    {
        private static readonly AccessTools.FieldRef<Minimap, Dictionary<Vector3, Minimap.PinData>> s_locationPins =
            AccessTools.FieldRefAccess<Minimap, Dictionary<Vector3, Minimap.PinData>>("m_locationPins");
        private static readonly MethodInfo s_destroyPinMarker = AccessTools.Method(typeof(Minimap), "DestroyPinMarker");

        private static void Postfix(Minimap __instance)
        {
            bool drawn = __instance.m_mode == Minimap.MapMode.Large || (__instance.m_mode == Minimap.MapMode.Small && MapSettings.ShowOnMinimap);
            if (!drawn || !MapSettings.Enabled || s_destroyPinMarker == null)
            {
                return;
            }
            foreach (KeyValuePair<Vector3, Minimap.PinData> pair in s_locationPins(__instance))
            {
                if (pair.Value.m_uiElement != null && MapOverlay.IsDrawnAt(pair.Key))
                {
                    s_destroyPinMarker.Invoke(__instance, new object[] { pair.Value });
                }
            }
        }
    }

    // The game rebuilt its fog texture: re-apply "show unexplored" if it is enabled.
    [HarmonyPatch(typeof(Minimap), nameof(Minimap.Reset))]
    internal static class MapFogAfterReset
    {
        private static void Postfix()
        {
            MapFog.OnGameReset();
        }
    }

    [HarmonyPatch(typeof(Minimap), nameof(Minimap.ResetSharedMapData))]
    internal static class MapFogAfterSharedReset
    {
        private static void Postfix()
        {
            MapFog.OnGameReset();
        }
    }

    [HarmonyPatch(typeof(Minimap), "ResetAndExplore", new[] { typeof(BitArray), typeof(BitArray) })]
    internal static class MapFogAfterLoad
    {
        private static void Postfix()
        {
            MapFog.OnGameReset();
        }
    }
}
