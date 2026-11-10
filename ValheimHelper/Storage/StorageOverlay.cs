using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimHelper.Storage
{
    // Markers over the chests that hold the item picked in "Де лежить?", the mod label of the chest under
    // the crosshair and padlocks over "не чіпати" items in the inventory window. Drawn only on this client;
    // nothing is added to the world.
    internal sealed class StorageOverlay : MonoBehaviour
    {
        // Chests found with "Де лежить?" are marked briefly; empty fires stay marked longer, they are spread over the base.
        private const float HighlightSeconds = 10f;
        private const float LowFiresSeconds = 180f;
        private const float FadeSeconds = 2f;

        internal static string HighlightName;
        internal static float HighlightUntil;

        private sealed class Match
        {
            public Container Container;
            public int Count;
            // The chest's own box in the world (its colliders), so the marker frames this chest and not the one on top.
            public Bounds Bounds;
        }

        private readonly List<Match> m_matches = new List<Match>();
        private static readonly Vector3[] s_boxCorners = new Vector3[8];
        private float m_matchesAt = -10f;
        private string m_matchesFor;
        private GUIStyle m_badge;
        private GUIStyle m_tag;
        private float m_styleScale;
        private bool m_loggedError;

        internal static bool HighlightActive => HighlightName != null && Time.unscaledTime < HighlightUntil;

        internal static float LowFiresUntil;
        private readonly List<Fireplace> m_lowFires = new List<Fireplace>();
        private float m_lowFiresAt = -10f;

        internal static bool LowFiresActive => Time.unscaledTime < LowFiresUntil;

        internal static void ShowLowFires(bool show)
        {
            LowFiresUntil = show ? Time.unscaledTime + LowFiresSeconds : 0f;
        }

        internal static void Highlight(string sharedName)
        {
            HighlightName = sharedName;
            HighlightUntil = Time.unscaledTime + HighlightSeconds;
        }

        internal static void ClearHighlight()
        {
            HighlightName = null;
        }

        private void OnGUI()
        {
            GUI.depth = 10;
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }
            try
            {
                Player player = Player.m_localPlayer;
                Camera camera = Utils.GetMainCamera();
                if (player == null || camera == null)
                {
                    return;
                }
                float scale = Mathf.Max(1f, Screen.height / 1080f);
                EnsureStyles(scale);
                GUI.matrix = Matrix4x4.identity;
                if (HighlightActive && GameScreen.WorldMarkers)
                {
                    DrawHighlights(camera, player, scale);
                }
                if (LowFiresActive && GameScreen.WorldMarkers)
                {
                    DrawLowFires(camera, player, scale);
                }
                if (InventoryGui.IsVisible())
                {
                    InventoryMarks.DrawLocks();
                }
                else if (StorageSettings.ShowChestTags && !State.MenuOpen && !Hud.IsUserHidden())
                {
                    DrawHoveredTag(scale);
                }
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

        private void EnsureStyles(float scale)
        {
            if (m_badge != null && Mathf.Approximately(scale, m_styleScale))
            {
                return;
            }
            GUISkin skin = HelperSkin.Get();
            m_styleScale = scale;
            m_badge = new GUIStyle(HelperSkin.Tooltip)
            {
                fontSize = Mathf.RoundToInt(15f * scale),
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(Mathf.RoundToInt(36f * scale), Mathf.RoundToInt(10f * scale), 0, 0),
            };
            m_tag = new GUIStyle(HelperSkin.Tooltip)
            {
                fontSize = Mathf.RoundToInt(15f * scale),
                alignment = TextAnchor.MiddleCenter,
            };
            if (skin.font != null)
            {
                m_badge.font = skin.font;
                m_tag.font = skin.font;
            }
        }

        private void DrawHighlights(Camera camera, Player player, float scale)
        {
            if (m_matchesFor != HighlightName || Time.unscaledTime - m_matchesAt > 0.5f)
            {
                m_matchesFor = HighlightName;
                m_matchesAt = Time.unscaledTime;
                m_matches.Clear();
                foreach (Container container in StorageSorter.LoadedContainers())
                {
                    int count = 0;
                    Inventory inventory = container.GetInventory();
                    if (inventory == null)
                    {
                        continue;
                    }
                    foreach (ItemDrop.ItemData item in inventory.GetAllItems())
                    {
                        if (item.m_shared.m_name == HighlightName)
                        {
                            count += item.m_stack;
                        }
                    }
                    if (count > 0 && StorageSorter.IsStorageChest(container))
                    {
                        m_matches.Add(new Match { Container = container, Count = count, Bounds = ChestBounds(container) });
                    }
                }
            }
            StorageItem info = StorageCatalog.Get(HighlightName);
            float height = 34f * scale;
            float icon = 26f * scale;
            Color previous = GUI.color;
            // The markers fade out during the last seconds instead of vanishing at once.
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01((HighlightUntil - Time.unscaledTime) / FadeSeconds));
            float alpha = GUI.color.a;
            foreach (Match match in m_matches)
            {
                if (match.Container == null || !ScreenBox(camera, match.Bounds, out Rect box))
                {
                    continue;
                }
                // A frame around the chest itself, the count in its middle: with chests stacked on each other the
                // marker stays on the right one.
                Frame(box, Mathf.Max(2f, 2f * scale), new Color(1f, 0.8f, 0.3f, 0.95f * alpha));
                GUI.color = new Color(1f, 1f, 1f, alpha);
                float distance = Vector3.Distance(player.transform.position, match.Container.transform.position);
                GUIContent text = new GUIContent(Lang.F("×{0}   <color=#c8b89a>{1:0} м</color>", match.Count, distance));
                float width = m_badge.CalcSize(text).x;
                Rect rect = new Rect(box.center.x - width / 2f, box.center.y - height / 2f, width, height);
                GUI.Box(rect, text, m_badge);
                if (info?.IconTexture != null)
                {
                    GUI.DrawTextureWithTexCoords(new Rect(rect.x + 6f * scale, rect.y + (height - icon) / 2f, icon, icon), info.IconTexture, info.IconUv);
                }
            }
            GUI.color = previous;
        }

        private static Bounds ChestBounds(Container container)
        {
            bool any = false;
            Bounds bounds = new Bounds(container.transform.position, Vector3.zero);
            foreach (Collider collider in container.GetComponentsInChildren<Collider>())
            {
                if (collider == null || !collider.enabled || collider.isTrigger)
                {
                    continue;
                }
                if (!any)
                {
                    bounds = collider.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }
            if (!any)
            {
                bounds = new Bounds(container.transform.position + Vector3.up * 0.4f, new Vector3(1.2f, 0.8f, 0.8f));
            }
            return bounds;
        }

        // The screen rectangle around a world box; false when the box is behind the camera.
        private static bool ScreenBox(Camera camera, Bounds bounds, out Rect box)
        {
            box = default;
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            s_boxCorners[0] = new Vector3(min.x, min.y, min.z);
            s_boxCorners[1] = new Vector3(max.x, min.y, min.z);
            s_boxCorners[2] = new Vector3(min.x, max.y, min.z);
            s_boxCorners[3] = new Vector3(max.x, max.y, min.z);
            s_boxCorners[4] = new Vector3(min.x, min.y, max.z);
            s_boxCorners[5] = new Vector3(max.x, min.y, max.z);
            s_boxCorners[6] = new Vector3(min.x, max.y, max.z);
            s_boxCorners[7] = new Vector3(max.x, max.y, max.z);
            float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
            foreach (Vector3 corner in s_boxCorners)
            {
                if (!ToGui(camera, corner, out Vector2 point))
                {
                    return false;
                }
                xMin = Mathf.Min(xMin, point.x);
                yMin = Mathf.Min(yMin, point.y);
                xMax = Mathf.Max(xMax, point.x);
                yMax = Mathf.Max(yMax, point.y);
            }
            box = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            return box.width > 0f && box.height > 0f;
        }

        private static void Frame(Rect box, float thickness, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(box.xMin, box.yMin, box.width, thickness), HelperSkin.White);
            GUI.DrawTexture(new Rect(box.xMin, box.yMax - thickness, box.width, thickness), HelperSkin.White);
            GUI.DrawTexture(new Rect(box.xMin, box.yMin, thickness, box.height), HelperSkin.White);
            GUI.DrawTexture(new Rect(box.xMax - thickness, box.yMin, thickness, box.height), HelperSkin.White);
            GUI.color = previous;
        }

        // World point to GUI coordinates through the viewport, so a camera that renders at a scaled resolution
        // (the game's frame buffer scaling) does not shift the markers.
        private static bool ToGui(Camera camera, Vector3 world, out Vector2 gui)
        {
            Vector3 viewport = camera.WorldToViewportPoint(world);
            gui = new Vector2(viewport.x * Screen.width, (1f - viewport.y) * Screen.height);
            return viewport.z > 0f;
        }

        private void DrawLowFires(Camera camera, Player player, float scale)
        {
            if (Time.unscaledTime - m_lowFiresAt > 1f)
            {
                m_lowFiresAt = Time.unscaledTime;
                Fires.LowFires(player, StorageSettings.FireRadius, m_lowFires);
            }
            float height = 30f * scale;
            float icon = 22f * scale;
            foreach (Fireplace fire in m_lowFires)
            {
                if (fire == null || fire.m_fuelItem == null)
                {
                    continue;
                }
                if (!ToGui(camera, fire.transform.position + Vector3.up * 0.6f, out Vector2 point))
                {
                    continue;
                }
                float distance = Vector3.Distance(player.transform.position, fire.transform.position);
                GUIContent text = new GUIContent(Lang.F("<color=#ff9060>порожній</color>  <color=#c8b89a>{0:0} м</color>", distance));
                float width = m_badge.CalcSize(text).x;
                Rect rect = new Rect(point.x - width / 2f, point.y - height / 2f, width, height);
                GUI.Box(rect, text, m_badge);
                StorageItem fuel = StorageCatalog.Get(fire.m_fuelItem.m_itemData.m_shared.m_name);
                if (fuel?.IconTexture != null)
                {
                    GUI.DrawTextureWithTexCoords(new Rect(rect.x + 8f * scale, rect.y + (height - icon) / 2f, icon, icon), fuel.IconTexture, fuel.IconUv);
                }
            }
        }

        private void DrawHoveredTag(float scale)
        {
            Container chest = StorageSorter.HoveredChest();
            StorageGroup group = chest != null ? StorageCatalog.FindGroup(StorageSorter.GetTag(chest)) : null;
            if (group == null)
            {
                return;
            }
            GUIContent text = new GUIContent(Lang.T("Для: ") + group.DisplayName);
            Vector2 size = m_tag.CalcSize(text);
            size.x += 20f * scale;
            size.y = 30f * scale;
            GUI.Box(new Rect((Screen.width - size.x) / 2f, Screen.height / 2f - 70f * scale, size.x, size.y), text, m_tag);
        }
    }
}
