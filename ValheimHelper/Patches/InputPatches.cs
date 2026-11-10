using HarmonyLib;
using UnityEngine;

namespace ValheimHelper.Patches
{
    internal static class HelperInput
    {
        // A text field of the menu or of the map panel has keyboard focus.
        internal static bool Typing => State.GuiTextFocus || Map.MapOverlay.SearchFocused;

        internal static bool PointerOverUi()
        {
            if (Map.MapOverlay.PointerOverPanel)
            {
                return true;
            }
            if (!State.MenuOpen)
            {
                return false;
            }
            Vector3 pointer = ZInput.pointerPosition;
            return State.MenuScreenRect.Contains(new Vector2(pointer.x, Screen.height - pointer.y));
        }
    }

    // While typing in the menu, named game buttons (M map, Tab inventory, F5 console, E use, hotbar, attack...)
    // are ignored. The mod itself reads raw keys through ZInput.GetKeyDown, which stays untouched.
    [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonDown))]
    internal static class TypingBlocksButtonDown
    {
        private static void Postfix(ref bool __result)
        {
            if (__result && HelperInput.Typing)
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButton))]
    internal static class TypingBlocksButton
    {
        private static void Postfix(ref bool __result)
        {
            if (__result && HelperInput.Typing)
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonUp))]
    internal static class TypingBlocksButtonUp
    {
        private static void Postfix(ref bool __result)
        {
            if (__result && HelperInput.Typing)
            {
                __result = false;
            }
        }
    }

    // Game code that asks "is a text dialog open" (player input, camera, chat, pause menu) treats our fields the same way.
    [HarmonyPatch(typeof(TextInput), nameof(TextInput.IsVisible))]
    internal static class TypingIsTextInput
    {
        private static void Postfix(ref bool __result)
        {
            if (!__result && HelperInput.Typing)
            {
                __result = true;
            }
        }
    }

    // The mouse wheel over the menu or the map panel scrolls only them: not the camera, map zoom or build rotation.
    [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseScrollWheel))]
    internal static class WheelOverHelperUi
    {
        private static void Postfix(ref float __result)
        {
            if (__result != 0f && HelperInput.PointerOverUi())
            {
                __result = 0f;
            }
        }
    }
}
