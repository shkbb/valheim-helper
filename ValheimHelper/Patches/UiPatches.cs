using HarmonyLib;
using UnityEngine;

namespace ValheimHelper.Patches
{
    // GameCamera locks and hides the cursor every frame unless a game window is open. While the menu is open
    // that step is skipped entirely: locking and unlocking the cursor again in the same frame made it flicker.
    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture))]
    internal static class MenuShowsCursor
    {
        private static bool Prefix()
        {
            if (!State.MenuOpen && !PhotoMode.Active)
            {
                return true;
            }
            // the photo mode: the cursor serves the panel, and hides while the right button looks around
            bool look = !State.MenuOpen && PhotoMode.Looking;
            CursorLockMode wanted = look ? CursorLockMode.Locked : CursorLockMode.None;
            if (ZCursor.LockState != wanted)
            {
                ZCursor.LockState = wanted;
            }
            if (look && ZCursor.IsVisible)
            {
                ZCursor.Hide();
            }
            else if (!look && !ZCursor.IsVisible)
            {
                ZCursor.Show();
            }
            return false;
        }
    }

    // Esc closes the helper menu. The pause menu reacts to Esc in the same frame, so its Update is
    // skipped for that one frame; the frame marker makes this independent of script execution order.
    [HarmonyPatch(typeof(Menu), "Update")]
    internal static class EscapeClosesMenu
    {
        private static int s_closedFrame = -1;

        internal static bool TryHandle()
        {
            if (s_closedFrame == Time.frameCount)
            {
                return true;
            }
            if (FreeCamera.Active && ZInput.GetKeyDown(KeyCode.Escape) && !State.MenuOpen)
            {
                // step by step: a hidden photo panel comes back, then the photo mode ends, then the free camera
                if (PhotoMode.Active && PhotoMode.PanelHidden)
                {
                    PhotoMode.PanelHidden = false;
                }
                else if (PhotoMode.Active)
                {
                    PhotoMode.Stop();
                }
                else
                {
                    FreeCamera.Stop();
                }
                s_closedFrame = Time.frameCount;
                return true;
            }
            if (!State.MenuOpen || !ZInput.GetKeyDown(KeyCode.Escape))
            {
                return false;
            }
            if (Binds.CapturingId != null)
            {
                // Esc only cancels waiting for a key to bind; the menu stays open.
                Binds.CapturingId = null;
            }
            else
            {
                State.MenuOpen = false;
            }
            s_closedFrame = Time.frameCount;
            return true;
        }

        private static bool Prefix()
        {
            // An already open pause menu still handles Esc as usual.
            return !(TryHandle() && !Menu.IsVisible());
        }
    }

    // Blocks camera look and attacks while the menu is open.
    [HarmonyPatch(typeof(PlayerController), "TakeInput")]
    internal static class MenuBlocksControllerInput
    {
        private static void Postfix(ref bool __result)
        {
            if (State.MenuOpen)
            {
                __result = false;
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
