using System;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper.Storage
{
    // Crafting several items with one press. The game can already do it: with Shift held the craft button makes
    // m_multiCraftAmount items (5) at once, spending the materials and raising the skill for all of them. The mod
    // only sets that amount to the one picked next to the button and switches the game's multi-craft on, so the
    // game itself checks the materials, the free space and does the crafting.
    internal static class CraftAmount
    {
        internal const int Limit = 999;

        private static readonly System.Reflection.FieldInfo s_selected = AccessTools.Field(typeof(InventoryGui), "m_selectedRecipe");
        private static readonly AccessTools.FieldRef<InventoryGui, bool> s_touchMultiCrafting = AccessTools.FieldRefAccess<InventoryGui, bool>("m_touchMultiCrafting");
        private static readonly AccessTools.FieldRef<InventoryGui, float> s_craftTimer = AccessTools.FieldRefAccess<InventoryGui, float>("m_craftTimer");

        // The amount picked for the selected recipe; back to 1 when another recipe is selected or the window closes.
        internal static int Amount = 1;
        private static Recipe s_recipe;
        private static int s_gameAmount = -1;
        private static bool s_forced;

        internal static bool Crafting(InventoryGui gui)
        {
            return s_craftTimer(gui) >= 0f;
        }

        // The recipe selected in the crafting window, and the item being upgraded (null for a new item).
        internal static Recipe Selected(InventoryGui gui, out ItemDrop.ItemData upgradeItem)
        {
            upgradeItem = null;
            object pair = s_selected?.GetValue(gui);
            if (pair == null)
            {
                return null;
            }
            Traverse traverse = Traverse.Create(pair);
            upgradeItem = traverse.Property("ItemData").GetValue<ItemDrop.ItemData>();
            return traverse.Property("Recipe").GetValue<Recipe>();
        }

        // Prefix of InventoryGui.UpdateRecipe (every frame the window is open) and OnCraftPressed.
        internal static void Apply(InventoryGui gui)
        {
            if (gui == null)
            {
                return;
            }
            if (s_gameAmount < 0)
            {
                s_gameAmount = gui.m_multiCraftAmount;
            }
            // While an item is being made the game still reads the amount when the timer ends: leave everything as it is.
            if (Crafting(gui))
            {
                return;
            }
            Recipe recipe = Selected(gui, out ItemDrop.ItemData upgradeItem);
            if (recipe != s_recipe)
            {
                s_recipe = recipe;
                Amount = 1;
            }
            if (StorageSettings.CraftAmountPanel && Amount > 1 && recipe != null && upgradeItem == null)
            {
                gui.m_multiCraftAmount = Amount;
                s_touchMultiCrafting(gui) = true;
                s_forced = true;
                return;
            }
            gui.m_multiCraftAmount = s_gameAmount;
            if (s_forced)
            {
                s_touchMultiCrafting(gui) = false;
                s_forced = false;
            }
        }

        internal static void Reset()
        {
            Amount = 1;
            s_recipe = null;
        }

        // The most that can be made right now: enough materials (counting the chests around when that is on, like the
        // button does) and room in the inventory for the result.
        internal static int Max(Player player, Recipe recipe)
        {
            if (player == null || recipe == null || recipe.m_item == null)
            {
                return 1;
            }
            bool free = player.NoCostCheat() || (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost));
            int low = 1;
            int high = Limit;
            if (!Fits(player, recipe, 1, free))
            {
                return 1;
            }
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (Fits(player, recipe, middle, free))
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }
            return low;
        }

        private static bool Fits(Player player, Recipe recipe, int amount, bool free)
        {
            if (!player.GetInventory().CanAddItem(recipe.m_item.gameObject, Math.Max(1, recipe.m_amount) * amount))
            {
                return false;
            }
            return free || player.HaveRequirements(recipe, false, 1, amount);
        }
    }

    // The amount picker right above the game's craft button: "−", the amount, "+", "Макс".
    internal sealed class CraftAmountPanel : MonoBehaviour
    {
        private static readonly Vector3[] s_corners = new Vector3[4];
        private GUIStyle m_amount;
        private bool m_loggedError;

        private void Update()
        {
            if (!InventoryGui.IsVisible() && CraftAmount.Amount != 1)
            {
                CraftAmount.Reset();
            }
        }

        private void OnGUI()
        {
            try
            {
                InventoryGui gui = InventoryGui.instance;
                Player player = Player.m_localPlayer;
                if (!StorageSettings.CraftAmountPanel || gui == null || player == null || !InventoryGui.IsVisible() || !gui.InCraftTab()
                    || gui.m_craftButton == null || !gui.m_craftButton.gameObject.activeInHierarchy || CraftAmount.Crafting(gui))
                {
                    return;
                }
                Recipe recipe = CraftAmount.Selected(gui, out ItemDrop.ItemData upgradeItem);
                if (recipe == null || upgradeItem != null)
                {
                    return;
                }
                if (!ButtonRect(gui.m_craftButton.transform as RectTransform, out Rect button))
                {
                    return;
                }
                // The whole crafting panel: the picker goes outside it, so it never covers the recipe or its materials.
                if (!ButtonRect(gui.m_crafting, out Rect panel))
                {
                    panel = button;
                }
                Draw(player, recipe, button, panel);
            }
            catch (Exception e)
            {
                if (!m_loggedError)
                {
                    m_loggedError = true;
                    Log.Error(e);
                }
            }
        }

        private void Draw(Player player, Recipe recipe, Rect button, Rect panel)
        {
            GUI.depth = 5;
            GUI.skin = HelperSkin.Get();
            float scale = Mathf.Max(1f, Screen.height / 1080f);
            if (m_amount == null)
            {
                m_amount = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                m_amount.normal.textColor = HelperSkin.AccentColor;
            }
            // In unscaled units, outside the crafting panel: to its right, level with the craft button; under the panel,
            // right-aligned with the button, when the screen has no room on the right.
            const float width = 220f;
            const float height = 30f;
            const float margin = 8f;
            float screenWidth = Screen.width / scale;
            float screenHeight = Screen.height / scale;
            float x = panel.xMax / scale + margin;
            float y = button.center.y / scale - height * 0.5f;
            if (x + width > screenWidth - margin)
            {
                x = Mathf.Clamp(button.xMax / scale - width, margin, screenWidth - width - margin);
                y = panel.yMax / scale + margin;
                if (y + height > screenHeight - margin)
                {
                    // No room under the panel either: above it.
                    y = panel.yMin / scale - height - margin;
                }
            }
            Rect row = new Rect(x, y, width, height);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            // A backdrop, so the picker reads well over the world behind it.
            GUI.Box(new Rect(row.x - 4f, row.y - 4f, row.width + 8f, row.height + 8f), GUIContent.none);

            Event current = Event.current;
            if (current.type == EventType.ScrollWheel && row.Contains(current.mousePosition))
            {
                Change(current.delta.y < 0f ? 1 : -1, current.shift);
                current.Use();
            }

            float small = 34f;
            float max = 64f;
            float gap = 4f;
            float middle = width - 2f * small - max - 3f * gap;
            Rect minus = new Rect(x, y, small, height);
            Rect amount = new Rect(minus.xMax + gap, y, middle, height);
            Rect plus = new Rect(amount.xMax + gap, y, small, height);
            Rect all = new Rect(plus.xMax + gap, y, max, height);

            GUI.Box(amount, GUIContent.none);
            if (GUI.Button(minus, "−"))
            {
                Change(-1, current.shift);
            }
            if (GUI.Button(amount, "×" + CraftAmount.Amount, m_amount))
            {
                CraftAmount.Amount = 1;
            }
            if (GUI.Button(plus, "+"))
            {
                Change(1, current.shift);
            }
            if (GUI.Button(all, Lang.T("Макс")))
            {
                CraftAmount.Amount = CraftAmount.Max(player, recipe);
            }
            GUI.matrix = Matrix4x4.identity;
        }

        // Shift changes the amount by 10.
        private static void Change(int direction, bool shift)
        {
            int step = shift ? 10 : 1;
            int next = CraftAmount.Amount + direction * step;
            if (shift && direction > 0 && CraftAmount.Amount == 1)
            {
                next = 10;
            }
            CraftAmount.Amount = Mathf.Clamp(next, 1, CraftAmount.Limit);
        }

        // The button's rectangle in GUI coordinates (from the top-left corner of the screen).
        private static bool ButtonRect(RectTransform rect, out Rect result)
        {
            result = default;
            if (rect == null)
            {
                return false;
            }
            rect.GetWorldCorners(s_corners);
            Camera camera = InventoryMarks.CanvasCamera(rect);
            Vector2 min = RectTransformUtility.WorldToScreenPoint(camera, s_corners[0]);
            Vector2 max = RectTransformUtility.WorldToScreenPoint(camera, s_corners[2]);
            if (max.x <= min.x || max.y <= min.y)
            {
                return false;
            }
            result = Rect.MinMaxRect(min.x, Screen.height - max.y, max.x, Screen.height - min.y);
            return true;
        }
    }
}
