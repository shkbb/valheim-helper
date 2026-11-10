using HarmonyLib;
using UnityEngine;

namespace ValheimHelper.Patches
{
    // The mod's free camera (FreeCamera): the game's camera update is skipped while it is on, and the character
    // takes no input, so the movement keys fly the camera instead of walking.
    [HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
    internal static class FreeCameraMoves
    {
        private static bool Prefix(GameCamera __instance, float dt)
        {
            if (!FreeCamera.Active)
            {
                return true;
            }
            FreeCamera.Move(__instance, dt);
            return false;
        }
    }

    // The character takes no input while the free camera flies; in the photo mode it does when the keys move the
    // viking, except while the pointer is over the panel (a click there is not an attack).
    internal static class FreeCameraInput
    {
        internal static bool Held => FreeCamera.Active && (!PhotoMode.ControlsViking || PhotoPanel.PointerOver);
    }

    [HarmonyPatch(typeof(Player), "TakeInput")]
    internal static class FreeCameraHoldsPlayer
    {
        private static void Postfix(ref bool __result)
        {
            if (FreeCameraInput.Held)
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(PlayerController), "TakeInput")]
    internal static class FreeCameraHoldsController
    {
        private static void Postfix(ref bool __result)
        {
            if (FreeCameraInput.Held)
            {
                __result = false;
            }
        }
    }

    // In the photo mode the mouse turns the viking only while the right button is held (the cursor serves the panel).
    [HarmonyPatch(typeof(Player), nameof(Player.SetMouseLook))]
    internal static class PhotoModeMouseLook
    {
        private static void Prefix(ref Vector2 mouseLook)
        {
            if (PhotoMode.ControlsViking && !PhotoMode.Looking)
            {
                mouseLook = Vector2.zero;
            }
        }
    }
}
