using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper.Storage
{
    // "Не чіпати" marks right in the game's inventory window: a bindable key toggles the item under the cursor,
    // and marked items get a small padlock over their slot. The marks live in storage_settings.txt only.
    internal static class InventoryMarks
    {
        private static readonly AccessTools.FieldRef<InventoryGrid, List<InventoryElement>> s_elements =
            AccessTools.FieldRefAccess<InventoryGrid, List<InventoryElement>>("m_elements");
        private static readonly Vector3[] s_corners = new Vector3[4];
        // The game's tooltip being shown right now (UITooltip.m_tooltip); null if a game update renamed it, and then
        // the padlocks are simply drawn as before.
        private static readonly System.Reflection.FieldInfo s_tooltip = AccessTools.Field(typeof(UITooltip), "m_tooltip");

        internal static string ToggleHovered()
        {
            InventoryGui gui = InventoryGui.instance;
            if (gui == null || !InventoryGui.IsVisible() || gui.m_playerGrid == null)
            {
                return Lang.T("Відкрий інвентар і наведи курсор на предмет.");
            }
            if (!StorageCatalog.EnsureBuilt())
            {
                return null;
            }
            ItemDrop.ItemData item = HoveredItem(gui.m_playerGrid);
            StorageItem info = StorageCatalog.Get(item);
            if (info == null)
            {
                return Lang.T("Наведи курсор на предмет у своєму інвентарі.");
            }
            bool kept = !StorageSettings.IsItemKept(info.Prefab);
            StorageSettings.SetItemKept(info.Prefab, kept);
            return kept ? Lang.T("Не чіпати: ") + info.DisplayName : Lang.T("Знову складається: ") + info.DisplayName;
        }

        // Where the game's tooltip is on the screen (GUI coordinates), with everything inside it: text and background.
        private static bool TooltipScreenRect(out Rect rect)
        {
            rect = default;
            GameObject tooltip = s_tooltip != null ? s_tooltip.GetValue(null) as GameObject : null;
            if (tooltip == null || !tooltip.activeInHierarchy)
            {
                return false;
            }
            float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
            foreach (RectTransform part in tooltip.GetComponentsInChildren<RectTransform>())
            {
                part.GetWorldCorners(s_corners);
                Camera camera = CanvasCamera(part);
                for (int i = 0; i < 4; i++)
                {
                    Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, s_corners[i]);
                    xMin = Mathf.Min(xMin, point.x);
                    xMax = Mathf.Max(xMax, point.x);
                    yMin = Mathf.Min(yMin, point.y);
                    yMax = Mathf.Max(yMax, point.y);
                }
            }
            if (xMax <= xMin || yMax <= yMin)
            {
                return false;
            }
            // screen coordinates grow upwards, GUI coordinates downwards
            rect = Rect.MinMaxRect(xMin, Screen.height - yMax, xMax, Screen.height - yMin);
            return true;
        }

        private static ItemDrop.ItemData HoveredItem(InventoryGrid grid)
        {
            Inventory inventory = grid.GetInventory();
            List<InventoryElement> elements = s_elements(grid);
            if (inventory == null || elements == null)
            {
                return null;
            }
            foreach (InventoryElement element in elements)
            {
                RectTransform rect = element != null ? element.GetElementRectTransform() : null;
                if (rect != null && RectTransformUtility.RectangleContainsScreenPoint(rect, ZInput.pointerPosition, CanvasCamera(rect)))
                {
                    return inventory.GetItemAt(element.Position.x, element.Position.y);
                }
            }
            return null;
        }

        // Draws a padlock over every marked item of the player's inventory. Call on Repaint.
        internal static void DrawLocks()
        {
            InventoryGui gui = InventoryGui.instance;
            InventoryGrid grid = gui != null ? gui.m_playerGrid : null;
            Inventory inventory = grid != null ? grid.GetInventory() : null;
            List<InventoryElement> elements = grid != null ? s_elements(grid) : null;
            if (inventory == null || elements == null || StorageSettings.KeptItems.Count == 0 || !StorageCatalog.EnsureBuilt())
            {
                return;
            }
            Color previous = GUI.color;
            bool covered = TooltipScreenRect(out Rect tooltip);
            foreach (InventoryElement element in elements)
            {
                if (element == null)
                {
                    continue;
                }
                StorageItem info = StorageCatalog.Get(inventory.GetItemAt(element.Position.x, element.Position.y));
                if (info == null || !StorageSettings.IsItemKept(info.Prefab))
                {
                    continue;
                }
                RectTransform rect = element.transform as RectTransform;
                if (rect == null)
                {
                    continue;
                }
                rect.GetWorldCorners(s_corners);
                Camera camera = CanvasCamera(rect);
                Vector2 min = RectTransformUtility.WorldToScreenPoint(camera, s_corners[0]);
                Vector2 max = RectTransformUtility.WorldToScreenPoint(camera, s_corners[2]);
                float width = max.x - min.x;
                if (width <= 2f)
                {
                    continue;
                }
                float size = width * 0.34f;
                Rect badge = new Rect(min.x + width * 0.04f, Screen.height - max.y + (max.y - min.y) * 0.33f, size, size);
                if (covered && badge.Overlaps(tooltip))
                {
                    // IMGUI is drawn over the game's canvas: a padlock here would sit on top of the item description
                    continue;
                }
                GUI.color = new Color(0.05f, 0.035f, 0.02f, 0.85f);
                GUI.DrawTexture(badge, HelperSkin.Circle);
                GUI.color = HelperSkin.AccentColor;
                float inset = size * 0.2f;
                GUI.DrawTexture(new Rect(badge.x + inset, badge.y + inset, size - inset * 2f, size - inset * 2f), HelperSkin.Lock);
            }
            GUI.color = previous;
        }

        internal static Camera CanvasCamera(RectTransform rect)
        {
            Canvas canvas = rect.GetComponentInParent<Canvas>();
            canvas = canvas != null ? canvas.rootCanvas : null;
            return canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        }
    }
}
