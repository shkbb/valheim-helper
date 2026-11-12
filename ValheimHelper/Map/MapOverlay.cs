using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimHelper.Map
{
    internal sealed class MapIcon
    {
        public Texture2D Texture;
        public Rect Uv;
    }

    // In-game port of valheim-map.world. Markers for every location the world generator placed are drawn
    // over the large map, with a side panel for layers, search, favorites, details and map options.
    // The world is only read: no console commands, no world keys, nothing is marked as cheated.
    internal sealed class MapOverlay : MonoBehaviour
    {
        internal static bool PointerOverPanel;
        internal static bool SearchFocused;
        internal static MapPlace Hovered;

        private const string SearchControl = "vh_map_search";
        private const float PanelWidth = 390f;
        private const float BaseScreenHeight = 1080f;
        private const int MaxSearchResults = 60;
        // Minimap.m_largeZoom default; the marker size setting applies at this zoom.
        private const float DefaultZoom = 0.1f;

        private static readonly AccessTools.FieldRef<Minimap, Vector3> s_mapOffset = AccessTools.FieldRefAccess<Minimap, Vector3>("m_mapOffset");
        private static readonly AccessTools.FieldRef<Minimap, bool> s_showSharedMap = AccessTools.FieldRefAccess<Minimap, bool>("m_showSharedMapData");
        private static readonly AccessTools.FieldRef<Minimap, bool> s_pinUpdateRequired = AccessTools.FieldRefAccess<Minimap, bool>("m_pinUpdateRequired");
        private static readonly MethodInfo s_isExplored = AccessTools.Method(typeof(Minimap), "IsExplored", new[] { typeof(Vector3) });
        private static readonly MethodInfo s_screenToWorld = AccessTools.Method(typeof(Minimap), "ScreenToWorldPoint", new[] { typeof(Vector3) });
        private static readonly string[] s_panelTabs = { Lang.T("Шари"), Lang.T("Пошук"), Lang.T("Мітки"), Lang.T("Малюнки"), Lang.T("Вигляд") };
        private static readonly string[] s_sketchLayers = { Lang.T("Лише для мене"), Lang.T("Для друзів з модом") };
        private static readonly Dictionary<MapType, MapIcon> s_icons = new Dictionary<MapType, MapIcon>();
        // Positions that currently get a marker, so the game's own fixed-size icon there can be hidden.
        private static readonly HashSet<Vector3> s_drawnPositions = new HashSet<Vector3>();

        private static string s_confirmId;
        private static float s_confirmUntil;
        private static string s_message;
        private static float s_messageUntil;

        private readonly Vector3[] m_corners = new Vector3[4];
        private readonly List<MapPlace> m_visible = new List<MapPlace>();
        private readonly Dictionary<MapType, int[]> m_typeCounts = new Dictionary<MapType, int[]>();
        private readonly Dictionary<MapCategory, List<MapType>> m_typesByCategory = new Dictionary<MapCategory, List<MapType>>();

        // One line of the layers tab: everything of one name (several location prefabs are one "Смоляна яма"; a
        // whole-world resource and a place of the same name are one thing), switched on and off together.
        private sealed class LayerRow
        {
            public string Name;
            public string Group;
            public MapIcon Icon;
            public readonly List<MapType> Types = new List<MapType>();
            public readonly List<WorldVegetation.Kind> Kinds = new List<WorldVegetation.Kind>();
            public int[] Counts = new int[3];

            public int Shown()
            {
                int shown = 0;
                foreach (MapType type in Types)
                {
                    shown += MapSettings.IsTypeShown(type) ? 1 : 0;
                }
                foreach (WorldVegetation.Kind kind in Kinds)
                {
                    shown += MapSettings.VegShown.Contains(kind.Id) ? 1 : 0;
                }
                return shown;
            }

            public int Parts => Types.Count + Kinds.Count;

            public int Count
            {
                get
                {
                    int count = Counts[0];
                    foreach (WorldVegetation.Kind kind in Kinds)
                    {
                        count += kind.Total;
                    }
                    return count;
                }
            }

            public void Set(bool shown)
            {
                if (Types.Count > 0)
                {
                    MapSettings.SetTypesShown(Types, shown);
                }
                if (Kinds.Count > 0)
                {
                    List<string> ids = new List<string>();
                    foreach (WorldVegetation.Kind kind in Kinds)
                    {
                        ids.Add(kind.Id);
                    }
                    MapSettings.SetVegShown(ids, shown);
                }
            }
        }

        private readonly Dictionary<MapCategory, List<LayerRow>> m_rowsByCategory = new Dictionary<MapCategory, List<LayerRow>>();
        private readonly HashSet<MapCategory> m_expanded = new HashSet<MapCategory>();
        private readonly List<MapPlace> m_searchResults = new List<MapPlace>();

        private int m_visibleDataVersion = -1;
        private int m_visibleSettingsVersion = -1;
        private int m_visibleMarksVersion = -1;
        private int m_visibleFocusVersion = -1;
        private int m_visibleStamp;
        private int m_visibleVegKinds = -1;
        // Visible places bucketed by a coarse world grid, so the minimap only looks at the few cells it shows.
        private const float MinimapCell = 256f;
        private readonly Dictionary<long, List<MapPlace>> m_minimapCells = new Dictionary<long, List<MapPlace>>();
        private int m_minimapStamp = -1;
        private int m_countsVersion = -1;
        private int m_countsVegKinds = -1;
        private string m_lastSearch = "";
        private int m_searchDataVersion = -1;
        private int m_searchContentsVersion = -1;
        // Things found by the search (items), each with everything that gives it.
        private sealed class ItemRow
        {
            public string Item;
            public string Name;
            public MapIcon Icon;
            public List<ItemSources.Source> Sources = new List<ItemSources.Source>();
            public List<WorldVegetation.Kind> Kinds = new List<WorldVegetation.Kind>();
            public int WorldCount;
            public string Where;
        }

        private readonly List<ItemRow> m_itemRows = new List<ItemRow>();
        private const int MaxItemRows = 6;
        // The item shown on the map from the search, and the resource layers it switched on (switched off with it).
        private static string s_shownItem;
        private static int s_shownWorldCount;
        private static readonly List<string> s_shownKinds = new List<string>();
        // Why a place matched the search when it is not its name: the item or creature found there.
        private readonly Dictionary<MapType, string> m_searchMatches = new Dictionary<MapType, string>();
        private float m_searchRefreshAt;
        private int m_searchTotal;

        // Layout changes requested by a click are applied on the next Layout event, so IMGUI never sees
        // a different number of controls between the Layout and the event pass of the same frame.
        private int m_pendingTab = -1;
        private int m_pendingExpand = -1;

        private Rect m_panelRect;
        private bool m_panelDrawn;
        private int m_panelTab;
        private Vector2 m_panelScroll;
        private string m_search = "";
        private string m_selectedKey;
        private MapPlace m_selected;
        private int m_selectedVersion = -1;
        private Vector2 m_mouseDownPos;
        private bool m_mouseDownOnMap;
        private bool m_cursorValid;
        private Vector3 m_cursorWorld;
        private GUIStyle m_tooltipStyle;
        private GUIStyle m_smallStyle;
        private GUIStyle m_badgeStyle;
        private GUIStyle m_captionStyle;
        private GUIStyle m_leftButton;
        private bool m_loggedError;

        private static float UiScale => Mathf.Max(1f, Screen.height / BaseScreenHeight);

        internal static bool IsDrawnAt(Vector3 position)
        {
            return s_drawnPositions.Contains(position);
        }

        private void Update()
        {
            try
            {
                MapSettings.SaveIfDirty();
                Minimap map = Minimap.instance;
                bool largeOpen = map != null && map.m_mode == Minimap.MapMode.Large;
                MapData.Tick(largeOpen && MapSettings.Enabled);
                if (!MapData.InWorld)
                {
                    s_favoritesMoved = false;
                }
                else if (largeOpen)
                {
                    MoveFavoritesToMarks();
                }
                MapFog.Tick();
                MapBiomes.Tick();
                MapSketch.Tick();
                if (!largeOpen || !MapSettings.Enabled || !MapData.InWorld)
                {
                    MapSketch.OnMapClosed();
                    Waypoints.CapturesMouse = false;
                    PointerOverPanel = false;
                    Hovered = null;
                    SearchFocused = false;
                    m_mouseDownOnMap = false;
                    return;
                }
                Vector3 pointer = ZInput.pointerPosition;
                Vector2 guiPointer = new Vector2(pointer.x, Screen.height - pointer.y);
                // the menu window over the map counts as a panel: no hover, clicks or drawing through it
                PointerOverPanel = (m_panelDrawn && m_panelRect.Contains(guiPointer)) || (State.MenuOpen && State.MenuScreenRect.Contains(guiPointer));
                bool haveRect = GetMapRect(map, out Rect mapRect);
                m_cursorValid = !PointerOverPanel && haveRect && mapRect.Contains(guiPointer);
                if (m_cursorValid)
                {
                    m_cursorWorld = (Vector3)s_screenToWorld.Invoke(map, new object[] { pointer });
                }
                bool typing = SearchFocused || Minimap.InTextInput() || (Chat.instance != null && Chat.instance.HasFocus()) || m_waypointFocused;
                bool alt = !typing && (ZInput.GetKey(KeyCode.LeftAlt) || ZInput.GetKey(KeyCode.RightAlt));
                Waypoints.CapturesMouse = alt && m_cursorValid;
                if (Waypoints.CapturesMouse && haveRect)
                {
                    if (ZInput.GetKeyDown(KeyCode.Mouse0))
                    {
                        ShowMessage(Waypoints.Add(m_cursorWorld, NearbyPlaceName(m_cursorWorld)));
                    }
                    else if (ZInput.GetKeyDown(KeyCode.Mouse1))
                    {
                        float metersPerPixel = map.m_mapImageLarge.uvRect.height * map.m_textureSize * map.m_pixelSize / Mathf.Max(1f, mapRect.height);
                        Waypoints.Mark near = Waypoints.Nearest(m_cursorWorld, 14f * metersPerPixel);
                        ShowMessage(near != null ? Waypoints.Remove(near) : Lang.T("Під курсором немає мітки."));
                    }
                }
                if (haveRect)
                {
                    MapSketch.UpdateInput(map, mapRect, m_cursorValid, screen => (Vector3)s_screenToWorld.Invoke(map, new object[] { screen }));
                }
                // A short click (not a drag) on a marker selects it; with the pen or eraser a click draws instead.
                if (ZInput.GetKeyDown(KeyCode.Mouse0))
                {
                    m_mouseDownPos = guiPointer;
                    m_mouseDownOnMap = !PointerOverPanel && !MapSketch.CapturesMouse;
                }
                if (ZInput.GetKeyUp(KeyCode.Mouse0) && m_mouseDownOnMap)
                {
                    m_mouseDownOnMap = false;
                    if (Hovered != null && (guiPointer - m_mouseDownPos).sqrMagnitude < 36f)
                    {
                        Select(Hovered, center: false);
                    }
                }
            }
            catch (Exception e)
            {
                LogOnce(e);
            }
        }

        private void OnGUI()
        {
            m_panelDrawn = false;
            Minimap map = Minimap.instance;
            if (map != null && map.m_mode == Minimap.MapMode.Small && MapSettings.Enabled && MapSettings.ShowOnMinimap && MapData.InWorld)
            {
                Hovered = null;
                SearchFocused = false;
                if (Event.current.type == EventType.Repaint)
                {
                    try
                    {
                        DrawMinimap(map);
                    }
                    catch (Exception e)
                    {
                        LogOnce(e);
                    }
                }
                return;
            }
            if (map == null || map.m_mode != Minimap.MapMode.Large || !MapSettings.Enabled || !MapData.InWorld)
            {
                Hovered = null;
                SearchFocused = false;
                return;
            }
            // Under the mod's menu window (it has a lower depth), so markers never cover it.
            GUI.depth = 5;
            try
            {
                GUISkin skin = HelperSkin.Get();
                GUI.skin = skin;
                EnsureStyles(skin);
                if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape && SearchFocused)
                {
                    GUI.FocusControl(null);
                    Event.current.Use();
                }
                GUI.matrix = Matrix4x4.identity;
                if (!GetMapRect(map, out Rect mapRect))
                {
                    return;
                }
                if (Event.current.type == EventType.Repaint)
                {
                    MapSketch.Draw(map, map.m_mapImageLarge.uvRect, mapRect, 1f);
                    MapSketch.DrawRemote(map, map.m_mapImageLarge.uvRect, mapRect);
                    DrawMarkers(map, mapRect);
                    Vector3 pointer = ZInput.pointerPosition;
                    Vector2 guiPointer = new Vector2(pointer.x, Screen.height - pointer.y);
                    DrawTamed(map, mapRect, guiPointer);
                    DrawVegetation(map, map.m_mapImageLarge.uvRect, mapRect, Mathf.Clamp(MarkerSize(map) * 0.75f, 10f, 30f), guiPointer, true);
                    DrawSources(map, map.m_mapImageLarge.uvRect, mapRect, Mathf.Clamp(MarkerSize(map) * 0.85f, 10f, 34f), guiPointer, true);
                    DrawWaypointPins(map, mapRect);
                    DrawDeliveries(map, map.m_mapImageLarge.uvRect, mapRect, 26f);
                    if (!PointerOverPanel && mapRect.Contains(guiPointer))
                    {
                        MapSketch.DrawCursor(guiPointer);
                    }
                }
                float scale = UiScale;
                GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
                bool searchFocused = false;
                DrawPanel(scale, ref searchFocused);
                SearchFocused = searchFocused;
                GUI.matrix = Matrix4x4.identity;
                if (Event.current.type == EventType.Repaint && Hovered == null && m_hoveredAnimal == null && s_sourceHovered && !PointerOverPanel && !MapSketch.CapturesMouse)
                {
                    DrawSourceTooltip();
                }
                else if (Event.current.type == EventType.Repaint && Hovered == null && m_hoveredAnimal == null && s_hoveredVeg != null && !PointerOverPanel && !MapSketch.CapturesMouse)
                {
                    DrawVegetationTooltip();
                }
                if (Event.current.type == EventType.Repaint && Hovered == null && m_hoveredAnimal != null && !PointerOverPanel && !MapSketch.CapturesMouse)
                {
                    DrawAnimalTooltip(m_hoveredAnimal);
                }
                if (Event.current.type == EventType.Repaint && Hovered != null && !PointerOverPanel && !MapSketch.CapturesMouse)
                {
                    DrawTooltip(Hovered);
                }
            }
            catch (Exception e)
            {
                LogOnce(e);
            }
        }

        // ---------------------------------------------------------------- map geometry

        private bool GetMapRect(Minimap map, out Rect guiRect)
        {
            return GetImageRect(map.m_mapImageLarge, out guiRect);
        }

        private bool GetImageRect(RawImage image, out Rect guiRect)
        {
            guiRect = default(Rect);
            if (image == null)
            {
                return false;
            }
            image.rectTransform.GetWorldCorners(m_corners);
            Canvas canvas = image.canvas;
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            Vector2 min = RectTransformUtility.WorldToScreenPoint(camera, m_corners[0]);
            Vector2 max = RectTransformUtility.WorldToScreenPoint(camera, m_corners[2]);
            guiRect = Rect.MinMaxRect(min.x, Screen.height - max.y, max.x, Screen.height - min.y);
            return guiRect.width > 1f && guiRect.height > 1f;
        }

        // Same mapping as Minimap.WorldToMapPoint, then through the visible uv window of the large map image.
        private static bool WorldToGui(Minimap map, Rect mapRect, Vector3 pos, out Vector2 gui)
        {
            return WorldToGui(map, map.m_mapImageLarge.uvRect, mapRect, pos, out gui);
        }

        private static bool WorldToGui(Minimap map, Rect uv, Rect mapRect, Vector3 pos, out Vector2 gui)
        {
            float half = map.m_textureSize / 2;
            float mx = (pos.x / map.m_pixelSize + half) / map.m_textureSize;
            float my = (pos.z / map.m_pixelSize + half) / map.m_textureSize;
            float nx = (mx - uv.xMin) / uv.width;
            float ny = (my - uv.yMin) / uv.height;
            gui = new Vector2(mapRect.xMin + nx * mapRect.width, mapRect.yMax - ny * mapRect.height);
            return nx >= 0f && nx <= 1f && ny >= 0f && ny <= 1f;
        }

        // Markers grow and shrink with the map zoom like on valheim-map.world (unlike the game's own pins,
        // which keep one size). Limits keep them clickable when zoomed out and stop them covering the map
        // when zoomed in. The large map's uvRect.height equals Minimap.LargeZoom (0.01 closest .. 1 whole world).
        private static float MarkerSize(Minimap map)
        {
            float zoom = Mathf.Max(0.001f, map.m_mapImageLarge.uvRect.height);
            float factor = Mathf.Clamp(Mathf.Pow(DefaultZoom / zoom, 0.7f), 0.3f, 3f);
            return MapSettings.IconSize * UiScale * factor;
        }

        // Opens the large map on a spot (closing the menu, which would cover it) and rings the spot for a few seconds.
        internal static string ShowOnMap(Vector3 pos)
        {
            Minimap map = Minimap.instance;
            if (map == null || Player.m_localPlayer == null)
            {
                return Lang.T("Зайди у світ.");
            }
            State.MenuOpen = false;
            map.SetMapMode(Minimap.MapMode.Large);
            CenterOn(pos);
            s_focusPos = pos;
            s_focusUntil = Time.unscaledTime + 4f;
            return "";
        }

        private static Vector3 s_focusPos;
        private static float s_focusUntil;
        private TamedAnimals.Animal m_hoveredAnimal;

        // Tamed animals: a circle each, green when fed, orange when hungry, with the trophy icon when big enough.
        // The one under the pointer gets a tooltip. Repaint only, identity GUI.matrix.
        private void DrawTamed(Minimap map, Rect mapRect, Vector2 pointer)
        {
            m_hoveredAnimal = null;
            if (Time.unscaledTime < s_focusUntil && WorldToGui(map, mapRect, s_focusPos, out Vector2 focus))
            {
                float pulse = 36f + 14f * Mathf.Sin(Time.unscaledTime * 6f);
                GUI.color = new Color(1f, 0.85f, 0.35f, 0.95f);
                GUI.DrawTexture(new Rect(focus.x - pulse * 0.5f, focus.y - pulse * 0.5f, pulse, pulse), HelperSkin.Ring);
                GUI.color = Color.white;
            }
            if (!MapSettings.ShowTamed)
            {
                return;
            }
            TamedAnimals.Want();
            float size = Mathf.Clamp(MarkerSize(map) * 0.6f, 10f, 30f);
            float best = float.MaxValue;
            foreach (TamedAnimals.Animal animal in TamedAnimals.Animals)
            {
                if (!WorldToGui(map, mapRect, animal.Pos, out Vector2 at))
                {
                    continue;
                }
                Rect outer = new Rect(at.x - size * 0.5f - 1.5f, at.y - size * 0.5f - 1.5f, size + 3f, size + 3f);
                GUI.color = new Color(0f, 0f, 0f, 0.75f);
                GUI.DrawTexture(outer, HelperSkin.Circle);
                GUI.color = animal.Hungry ? new Color(1f, 0.6f, 0.15f, 0.95f) : new Color(0.35f, 0.85f, 0.35f, 0.95f);
                GUI.DrawTexture(new Rect(at.x - size * 0.5f, at.y - size * 0.5f, size, size), HelperSkin.Circle);
                if (animal.Icon != null && size >= 16f)
                {
                    GUI.color = Color.white;
                    float inner = size * 0.78f;
                    GUI.DrawTextureWithTexCoords(new Rect(at.x - inner * 0.5f, at.y - inner * 0.5f, inner, inner), animal.Icon, animal.IconUv);
                }
                float distance = (at - pointer).sqrMagnitude;
                if (distance < (size * 0.5f + 3f) * (size * 0.5f + 3f) && distance < best)
                {
                    best = distance;
                    m_hoveredAnimal = animal;
                }
            }
            GUI.color = Color.white;
        }

        // Waypoints on the large map: a pin of their colour with the name; the followed one has a ring.
        private void DrawWaypointPins(Minimap map, Rect mapRect)
        {
            foreach (Waypoints.Mark mark in Waypoints.All())
            {
                if (!WorldToGui(map, mapRect, mark.Pos, out Vector2 at))
                {
                    continue;
                }
                Color color = Waypoints.ColorOf(mark);
                GUI.color = new Color(0f, 0f, 0f, 0.85f);
                GUI.DrawTexture(new Rect(at.x - 9f, at.y - 9f, 18f, 18f), HelperSkin.Circle);
                GUI.color = color;
                GUI.DrawTexture(new Rect(at.x - 7f, at.y - 7f, 14f, 14f), HelperSkin.Circle);
                if (mark.Id == Waypoints.GuideId)
                {
                    GUI.color = Color.white;
                    GUI.DrawTexture(new Rect(at.x - 14f, at.y - 14f, 28f, 28f), HelperSkin.Ring);
                }
                GUIContent name = new GUIContent(mark.Name);
                Vector2 size = m_smallStyle.CalcSize(name);
                Rect label = new Rect(at.x + 11f, at.y - size.y * 0.5f, size.x, size.y);
                GUI.color = new Color(0f, 0f, 0f, 0.9f);
                GUI.Label(new Rect(label.x + 1f, label.y + 1f, label.width, label.height), name, m_smallStyle);
                GUI.color = Color.Lerp(color, Color.white, 0.5f);
                GUI.Label(label, name, m_smallStyle);
            }
            GUI.color = Color.white;
        }

        // Deliveries from the base storage on their way: the item's icon in a circle moving to the player, and the time left.
        private void DrawDeliveries(Minimap map, Rect uv, Rect mapRect, float size)
        {
#if EDITION_HELPER
            Player player = Player.m_localPlayer;
            if (player == null || Storage.RemoteStorage.Deliveries.Count == 0 || Event.current.type != EventType.Repaint)
            {
                return;
            }
            foreach (Storage.RemoteStorage.Delivery delivery in Storage.RemoteStorage.Deliveries)
            {
                Vector3 pos = Storage.RemoteStorage.Position(delivery, player.transform.position);
                if (!WorldToGui(map, uv, mapRect, pos, out Vector2 at))
                {
                    continue;
                }
                GUI.color = new Color(0f, 0f, 0f, 0.85f);
                GUI.DrawTexture(new Rect(at.x - size * 0.5f - 2f, at.y - size * 0.5f - 2f, size + 4f, size + 4f), HelperSkin.Circle);
                GUI.color = new Color(1f, 0.85f, 0.45f, 1f);
                GUI.DrawTexture(new Rect(at.x - size * 0.5f, at.y - size * 0.5f, size, size), HelperSkin.Ring);
                GameObject prefab = ObjectDB.instance != null && !string.IsNullOrEmpty(delivery.Icon) ? ObjectDB.instance.GetItemPrefab(delivery.Icon) : null;
                Sprite sprite = prefab != null ? prefab.GetComponent<ItemDrop>()?.m_itemData.GetIcon() : null;
                if (sprite != null)
                {
                    GUI.color = Color.white;
                    Rect r = sprite.textureRect;
                    float inner = size * 0.72f;
                    GUI.DrawTextureWithTexCoords(new Rect(at.x - inner * 0.5f, at.y - inner * 0.5f, inner, inner), sprite.texture,
                        new Rect(r.x / sprite.texture.width, r.y / sprite.texture.height, r.width / sprite.texture.width, r.height / sprite.texture.height));
                }
                GUIContent time = new GUIContent(Storage.RemoteStorage.Clock(delivery.Left));
                Vector2 textSize = m_smallStyle.CalcSize(time);
                Rect label = new Rect(at.x - textSize.x * 0.5f, at.y + size * 0.5f + 1f, textSize.x, textSize.y);
                GUI.color = new Color(0f, 0f, 0f, 0.9f);
                GUI.Label(new Rect(label.x + 1f, label.y + 1f, label.width, label.height), time, m_smallStyle);
                GUI.color = new Color(1f, 0.9f, 0.6f, 1f);
                GUI.Label(label, time, m_smallStyle);
            }
            GUI.color = Color.white;
#endif
        }

        private static string NearbyPlaceName(Vector3 pos)
        {
            MapPlace best = null;
            float bestDistance = 80f * 80f;
            foreach (MapPlace place in MapData.Places)
            {
                float dx = place.Pos.x - pos.x;
                float dz = place.Pos.z - pos.z;
                float distance = dx * dx + dz * dz;
                if (distance < bestDistance)
                {
                    best = place;
                    bestDistance = distance;
                }
            }
            return best?.Type.Name;
        }

        private bool m_waypointFocused;
        private readonly Dictionary<long, string> m_waypointNames = new Dictionary<long, string>();

        // "Мітки" tab: how to place them, whether friends see them, the list with follow, show, rename, remove.
        private void DrawWaypoints()
        {
            GUILayout.Label(Lang.T("<b>Alt + ліва кнопка</b> на карті — поставити мітку, <b>Alt + права</b> по мітці — прибрати. " +
                "Ще — кнопкою «Поставити мітку» в картці місця або клавішею «Мітка там, куди дивишся» (Налаштування → Клавіші). " +
                "Мітку видно й у самому світі: значок, назва й відстань. «Вести» — стрілка на краю екрана показує, куди йти. " +
                "Кружечок біля назви — колір мітки: натисни, щоб змінити."), m_smallStyle);
            Toggle(ref MapSettings.WaypointsIn3D, Lang.T(" Показувати мітки в світі (не лише на карті)"));
            // what friends with the mod see of mine: the marks (on the map and in the world) and the game's own pins
            GUILayout.Label(Lang.T("<b>Друзі з модом бачать:</b>"), m_smallStyle);
            bool shareMarks = GUILayout.Toggle(MapSettings.WaypointsShared, Lang.T(" мої мітки (на карті й у світі)") + Binds.Hint("sharemarks"));
            CheatMenu.BindableControl("sharemarks");
            if (shareMarks != MapSettings.WaypointsShared)
            {
                MapSettings.WaypointsShared = shareMarks;
                MapSettings.Changed();
                int moved = Waypoints.SetMineShared(shareMarks);
                if (moved > 0)
                {
                    ShowMessage(shareMarks ? Lang.F("Твої мітки ({0}) тепер бачать друзі.", moved) : Lang.F("Твої мітки ({0}) тепер бачиш лише ти.", moved));
                }
            }
            if (MapSettings.WaypointsShared && !Waypoints.SharedAvailable)
            {
                GUILayout.Label(Lang.T("<color=#ffb040>Спільні мітки зберігає хост, від нього ще нічого не прийшло: поки що мітки лише твої.</color>"), m_smallStyle);
            }
            bool sharePins = GUILayout.Toggle(MapSettings.ShareGamePins, Lang.T(" мої значки гри на карті") + Binds.Hint("sharepins"));
            CheatMenu.BindableControl("sharepins");
            if (sharePins != MapSettings.ShareGamePins)
            {
                MapSettings.ShareGamePins = sharePins;
                MapSettings.Changed();
            }
            GUILayout.Label(Lang.T("<color=#a0a0a0>Значки гри — ті, що ставиш подвійним кліком по карті (вогнище, дім, молот, крапка, портал), і знайдені боси. " +
                "У друзів вони видні як спільні дані карти: їх ховає звичайний перемикач гри «спільне» на великій карті.</color>"), m_smallStyle);
            bool focused = false;
            Waypoints.Mark remove = null;
            foreach (Waypoints.Mark mark in Waypoints.All())
            {
                GUILayout.BeginHorizontal();
                Rect swatch = GUILayoutUtility.GetRect(18f, 18f, GUILayout.Width(18f), GUILayout.Height(22f));
                if (Event.current.type == EventType.Repaint)
                {
                    GUI.color = Waypoints.ColorOf(mark);
                    GUI.DrawTexture(new Rect(swatch.x, swatch.y + 2f, 18f, 18f), HelperSkin.Circle);
                    GUI.color = Color.white;
                }
                if (GUI.Button(swatch, GUIContent.none, GUIStyle.none))
                {
                    Waypoints.Recolor(mark, (mark.Color + 1) % SketchRules.Palette.Length);
                }
                string control = "waypoint_" + mark.Id;
                if (!m_waypointNames.TryGetValue(mark.Id, out string edited))
                {
                    edited = mark.Name;
                }
                GUI.SetNextControlName(control);
                edited = GUILayout.TextField(edited, GUILayout.Width(120f));
                bool thisFocused = GUI.GetNameOfFocusedControl() == control;
                focused |= thisFocused;
                if (thisFocused)
                {
                    m_waypointNames[mark.Id] = edited;
                }
                else if (m_waypointNames.TryGetValue(mark.Id, out string pending))
                {
                    m_waypointNames.Remove(mark.Id);
                    Waypoints.Rename(mark, pending.Trim());
                }
                GUILayout.Label($"<color=#a0a0a0>{DistanceText(mark.Pos)}{(mark.Shared ? Lang.T(" · спільна") : "")}</color>", m_smallStyle, GUILayout.Width(76f));
                bool guided = Waypoints.GuideId == mark.Id;
                if (GUILayout.Button(guided ? Lang.T("Веду") : Lang.T("Вести"), GUILayout.Width(52f)))
                {
                    Waypoints.GuideId = guided ? 0L : mark.Id;
                }
                if (GUILayout.Button(Lang.T("На карті"), GUILayout.Width(70f)))
                {
                    CenterOn(mark.Pos);
                }
                if (GUILayout.Button("✕", GUILayout.Width(26f)))
                {
                    remove = mark;
                }
                GUILayout.EndHorizontal();
            }
            m_waypointFocused = focused;
            if (remove != null)
            {
                ShowMessage(Waypoints.Remove(remove));
            }
        }

        private static readonly HashSet<long> s_sourceCells = new HashSet<long>();
        private static ItemSources.Found s_hoveredSource;
        private static bool s_sourceHovered;

        // Sources of the material picked in the search: the item's own icon in the resources' ring, like any marker.
        // Markers that would cover each other are drawn once, so thousands of them cost no more than a screenful.
        private static void DrawSources(Minimap map, Rect uv, Rect mapRect, float size, Vector2 pointer, bool hover)
        {
            if (hover)
            {
                s_sourceHovered = false;
            }
            if (ItemSources.Results.Count == 0)
            {
                return;
            }
            s_sourceCells.Clear();
            float cell = Mathf.Max(6f, size * 0.75f);
            float best = (size * 0.5f + 2f) * (size * 0.5f + 2f);
            Color ring = MapCatalog.CategoryColor(MapCategory.Resources);
            MapIcon icon = ItemSources.Icon;
            foreach (ItemSources.Found found in ItemSources.Results)
            {
                if (!WorldToGui(map, uv, mapRect, found.Pos, out Vector2 at))
                {
                    continue;
                }
                long key = ((long)Mathf.FloorToInt(at.x / cell) << 32) ^ (uint)Mathf.FloorToInt(at.y / cell);
                if (!s_sourceCells.Add(key))
                {
                    continue;
                }
                if (icon == null || size < 12f)
                {
                    GUI.color = new Color(0f, 0f, 0f, 0.8f);
                    GUI.DrawTexture(Square(at, Mathf.Max(6f, size * 0.5f) + 3f), HelperSkin.Circle);
                    GUI.color = ring;
                    GUI.DrawTexture(Square(at, Mathf.Max(6f, size * 0.5f)), HelperSkin.Circle);
                }
                else
                {
                    GUI.color = new Color(0.06f, 0.05f, 0.04f, 0.85f);
                    GUI.DrawTexture(Square(at, size), HelperSkin.Circle);
                    GUI.color = ring;
                    GUI.DrawTexture(Square(at, size), HelperSkin.Ring);
                    GUI.color = Color.white;
                    GUI.DrawTextureWithTexCoords(Square(at, size * 0.7f), icon.Texture, icon.Uv);
                }
                if (hover)
                {
                    float distance = (at - pointer).sqrMagnitude;
                    if (distance < best)
                    {
                        best = distance;
                        s_hoveredSource = found;
                        s_sourceHovered = true;
                    }
                }
            }
            GUI.color = Color.white;
        }

        private void DrawSourceTooltip()
        {
            string item = Localization.instance.Localize(ItemSources.ResultsFor ?? "");
            string title = string.IsNullOrEmpty(s_hoveredSource.Name) ? item : s_hoveredSource.Name;
            GUIContent content = new GUIContent($"<b>{title}</b>\n{item} · {DistanceText(s_hoveredSource.Pos)}");
            Vector2 size = m_tooltipStyle.CalcSize(content);
            Vector2 mouse = Event.current.mousePosition;
            Rect rect = new Rect(mouse.x + 18f, mouse.y + 18f, size.x, size.y);
            if (rect.xMax > Screen.width)
            {
                rect.x = mouse.x - size.x - 12f;
            }
            if (rect.yMax > Screen.height)
            {
                rect.y = mouse.y - size.y - 12f;
            }
            GUI.color = Color.white;
            GUI.Box(rect, content, m_tooltipStyle);
        }

        private void DrawAnimalTooltip(TamedAnimals.Animal animal)
        {
            string stars = animal.Stars > 0 ? " " + new string('★', animal.Stars) : "";
            string state = animal.Summon ? Lang.T("виклик посохом") : !animal.Eats ? Lang.T("не їсть") : animal.Hungry ? Lang.T("<color=#ffb040>голодна</color>") : Lang.T("<color=#80ff80>сита</color>");
            if (animal.Pregnant)
            {
                state += Lang.T(", <color=#ff9ad0>вагітна</color>");
            }
            if (!string.IsNullOrEmpty(animal.Following))
            {
                state += Lang.F(", іде за {0}", animal.Following);
            }
            GUIContent content = new GUIContent(Lang.F("<b>{0}</b>{1}\n{2:0}/{3:0} здоров'я · {4}\n{5}", animal.Title, stars, animal.Health, animal.MaxHealth, state, DistanceText(animal.Pos)));
            Vector2 size = m_tooltipStyle.CalcSize(content);
            Vector2 mouse = Event.current.mousePosition;
            Rect rect = new Rect(mouse.x + 18f, mouse.y + 18f, size.x, size.y);
            if (rect.xMax > Screen.width)
            {
                rect.x = mouse.x - size.x - 12f;
            }
            if (rect.yMax > Screen.height)
            {
                rect.y = mouse.y - size.y - 12f;
            }
            GUI.color = Color.white;
            GUI.Box(rect, content, m_tooltipStyle);
        }

        private static void CenterOn(Vector3 pos)
        {
            Minimap map = Minimap.instance;
            Player player = Player.m_localPlayer;
            if (map == null || player == null)
            {
                return;
            }
            Vector3 offset = pos - player.transform.position;
            offset.y = 0f;
            s_mapOffset(map) = offset;
        }

        // ---------------------------------------------------------------- minimap

        // The small map in the corner shows the same markers, smaller and without the panel. It never rotates
        // (only the player arrow does), so the mapping is the large map's one with the small image's uv window.
        private void DrawMinimap(Minimap map)
        {
            RawImage image = map.m_mapImageSmall;
            // the game covers the minimap instead of switching it off (a station's window, a portal's loading screen…)
            if (!GameScreen.MinimapShown(image) || !GetImageRect(image, out Rect rect))
            {
                return;
            }
            RefreshVisible();
            if (m_minimapStamp != m_visibleStamp)
            {
                m_minimapStamp = m_visibleStamp;
                m_minimapCells.Clear();
                foreach (MapPlace place in m_visible)
                {
                    long cell = CellKey(Mathf.FloorToInt(place.Pos.x / MinimapCell), Mathf.FloorToInt(place.Pos.z / MinimapCell));
                    if (!m_minimapCells.TryGetValue(cell, out List<MapPlace> list))
                    {
                        list = new List<MapPlace>();
                        m_minimapCells[cell] = list;
                    }
                    list.Add(place);
                }
            }
            // Drawn under the mod's menu window.
            GUI.depth = 5;
            GUI.matrix = Matrix4x4.identity;
            if (MapSettings.SketchOnMinimap)
            {
                MapSketch.Draw(map, image.uvRect, rect, 0.6f);
            }
            DrawDeliveries(map, image.uvRect, rect, 18f);
            if (MapSettings.VegShown.Count > 0)
            {
                GUI.BeginGroup(rect);
                DrawVegetation(map, image.uvRect, new Rect(0f, 0f, rect.width, rect.height), MapSettings.MinimapIconSize * UiScale * 0.75f, Vector2.zero, false);
                GUI.EndGroup();
            }
            if (ItemSources.Results.Count > 0)
            {
                GUI.BeginGroup(rect);
                DrawSources(map, image.uvRect, new Rect(0f, 0f, rect.width, rect.height), MapSettings.MinimapIconSize * UiScale * 0.8f, Vector2.zero, false);
                GUI.EndGroup();
            }
            if (m_minimapCells.Count == 0)
            {
                return;
            }
            Rect uv = image.uvRect;
            float half = map.m_textureSize / 2f;
            float minX = (uv.xMin * map.m_textureSize - half) * map.m_pixelSize;
            float maxX = (uv.xMax * map.m_textureSize - half) * map.m_pixelSize;
            float minZ = (uv.yMin * map.m_textureSize - half) * map.m_pixelSize;
            float maxZ = (uv.yMax * map.m_textureSize - half) * map.m_pixelSize;
            float size = MapSettings.MinimapIconSize * UiScale;
            Color previous = GUI.color;
            // The group clips markers at the edge of the minimap.
            GUI.BeginGroup(rect);
            for (int cx = Mathf.FloorToInt(minX / MinimapCell); cx <= Mathf.FloorToInt(maxX / MinimapCell); cx++)
            {
                for (int cz = Mathf.FloorToInt(minZ / MinimapCell); cz <= Mathf.FloorToInt(maxZ / MinimapCell); cz++)
                {
                    if (!m_minimapCells.TryGetValue(CellKey(cx, cz), out List<MapPlace> places))
                    {
                        continue;
                    }
                    foreach (MapPlace place in places)
                    {
                        if (!WorldToGui(map, uv, rect, place.Pos, out Vector2 center))
                        {
                            continue;
                        }
                        int flags = MapData.GetFlags(place.Key);
                        DrawPlace(place, center - rect.position, size, (flags & MapData.FlagDone) != 0 ? 0.35f : 1f);
                    }
                }
            }
            GUI.EndGroup();
            GUI.color = previous;
        }

        private static long CellKey(int x, int z)
        {
            return ((long)x << 32) ^ (uint)z;
        }

        // ---------------------------------------------------------------- markers

        private void RefreshVisible()
        {
            if (m_visibleDataVersion == MapData.Version && m_visibleSettingsVersion == MapSettings.Version && m_visibleMarksVersion == MapData.MarksVersion
                && m_visibleFocusVersion == s_focusVersion && m_visibleVegKinds == WorldVegetation.Kinds.Count)
            {
                return;
            }
            m_visibleVegKinds = WorldVegetation.Kinds.Count;
            m_visibleFocusVersion = s_focusVersion;
            m_visibleDataVersion = MapData.Version;
            m_visibleSettingsVersion = MapSettings.Version;
            m_visibleMarksVersion = MapData.MarksVersion;
            m_visibleStamp++;
            m_visible.Clear();
            s_drawnPositions.Clear();
            foreach (MapPlace place in MapData.Places)
            {
                if (IsPlaceVisible(place))
                {
                    m_visible.Add(place);
                    s_drawnPositions.Add(place.Pos);
                }
            }
            // Let the game redraw its pins, so its own icons hide or come back with the layers.
            if (Minimap.instance != null)
            {
                s_pinUpdateRequired(Minimap.instance) = true;
            }
        }

        private static bool IsPlaceVisible(MapPlace place)
        {
            int flags = MapData.GetFlags(place.Key);
            if (s_focusName != null && place.Type.Name == s_focusName)
            {
                return true;
            }
            if (MapSettings.HideDone && (flags & MapData.FlagDone) != 0)
            {
                return false;
            }
            return MapSettings.IsTypeShown(place.Type);
        }

        private void DrawMarkers(Minimap map, Rect mapRect)
        {
            RefreshVisible();
            float size = MarkerSize(map);
            float hoverRadius = Mathf.Max(8f, size * 0.55f);
            Vector2 mouse = Event.current.mousePosition;
            MapPlace hovered = null;
            float hoveredDistance = hoverRadius * hoverRadius;
            m_badgeStyle.fontSize = Mathf.RoundToInt(Mathf.Clamp(size * 0.55f, 11f, 26f));
            m_captionStyle.fontSize = Mathf.RoundToInt(Mathf.Clamp(size * 0.42f, 11f, 20f));
            bool captions = size >= 18f;
            Color previous = GUI.color;
            foreach (MapPlace place in m_visible)
            {
                if (!WorldToGui(map, mapRect, place.Pos, out Vector2 center) || (m_panelDrawn && m_panelRect.Contains(center)))
                {
                    continue;
                }
                int flags = MapData.GetFlags(place.Key);
                float alpha = (flags & MapData.FlagDone) != 0 ? 0.35f : 1f;
                DrawPlace(place, center, size, alpha);
                if (HasMark(place))
                {
                    DrawBadge(center, size, "★", new Color(1f, 0.85f, 0.2f));
                }
                // Every possible spot of a trader shows that trader's icon and name, so they are told apart at a glance.
                if (captions && place.Type.Category == MapCategory.Traders)
                {
                    DrawCaption(center, size, place.Type.Name, alpha);
                }
                float distance = (center - mouse).sqrMagnitude;
                if (!PointerOverPanel && distance < hoveredDistance)
                {
                    hovered = place;
                    hoveredDistance = distance;
                }
            }
            MapPlace selected = GetSelected();
            if (selected != null && WorldToGui(map, mapRect, selected.Pos, out Vector2 selectedCenter))
            {
                if (!IsPlaceVisible(selected))
                {
                    DrawPlace(selected, selectedCenter, size, 1f);
                }
                DrawFrame(selectedCenter, size + 8f, new Color(0.3f, 0.9f, 1f));
            }
            GUI.color = previous;
            Hovered = hovered;
        }

        // Round marker: dark backdrop, a ring in the category color and the game's own icon inside.
        private static void DrawPlace(MapPlace place, Vector2 center, float size, float alpha)
        {
            Color ring = MapCatalog.CategoryColor(place.Type.Category);
            MapIcon icon = MapSettings.UseDots ? null : GetIcon(place.Type);
            if (icon == null)
            {
                float dot = Mathf.Max(7f, size * 0.5f);
                GUI.color = new Color(0f, 0f, 0f, 0.75f * alpha);
                GUI.DrawTexture(Square(center, dot + 4f), HelperSkin.Circle);
                ring.a = alpha;
                GUI.color = ring;
                GUI.DrawTexture(Square(center, dot), HelperSkin.Circle);
                return;
            }
            GUI.color = new Color(0.06f, 0.05f, 0.04f, 0.8f * alpha);
            GUI.DrawTexture(Square(center, size), HelperSkin.Circle);
            ring.a = alpha;
            GUI.color = ring;
            GUI.DrawTexture(Square(center, size), HelperSkin.Ring);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.DrawTextureWithTexCoords(Square(center, size * 0.68f), icon.Texture, icon.Uv);
        }

        private static Rect Square(Vector2 center, float size)
        {
            return new Rect(center.x - size / 2f, center.y - size / 2f, size, size);
        }

        private void DrawBadge(Vector2 center, float size, string text, Color color)
        {
            Rect rect = new Rect(center.x + size * 0.2f, center.y - size * 0.85f, size, size * 0.8f);
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, m_badgeStyle);
            GUI.color = color;
            GUI.Label(rect, text, m_badgeStyle);
        }

        private void DrawCaption(Vector2 center, float size, string text, float alpha)
        {
            float height = m_captionStyle.fontSize + 8f;
            Rect rect = new Rect(center.x - 100f, center.y + size * 0.5f, 200f, height);
            GUI.color = new Color(0f, 0f, 0f, 0.9f * alpha);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, m_captionStyle);
            GUI.color = new Color(1f, 0.9f, 0.55f, alpha);
            GUI.Label(rect, text, m_captionStyle);
        }

        // Selection: a wider double ring around the marker.
        private static void DrawFrame(Vector2 center, float size, Color color)
        {
            GUI.color = color;
            GUI.DrawTexture(Square(center, size), HelperSkin.Ring);
            GUI.DrawTexture(Square(center, size + 3f), HelperSkin.Ring);
        }

        // Location icon of the game when it has one (traders, start temple), else an item icon, else a pin icon.
        private static MapIcon GetIcon(MapType type)
        {
            if (s_icons.TryGetValue(type, out MapIcon cached))
            {
                return cached;
            }
            Minimap map = Minimap.instance;
            if (map == null || ObjectDB.instance == null)
            {
                return null;
            }
            Sprite sprite = null;
            foreach (Minimap.LocationSpriteData data in map.m_locationIcons)
            {
                if (data.m_name == type.Prefab && data.m_icon != null)
                {
                    sprite = data.m_icon;
                    break;
                }
            }
            if (sprite == null && type.IconItems != null)
            {
                foreach (string itemName in type.IconItems)
                {
                    GameObject prefab = ObjectDB.instance.GetItemPrefab(itemName);
                    ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                    Sprite[] icons = drop != null && drop.m_itemData != null && drop.m_itemData.m_shared != null ? drop.m_itemData.m_shared.m_icons : null;
                    if (icons != null && icons.Length > 0 && icons[0] != null)
                    {
                        sprite = icons[0];
                        break;
                    }
                }
            }
            if (sprite == null)
            {
                Minimap.PinType pin = type.Pin ?? MapCatalog.CategoryPin(type.Category);
                foreach (Minimap.SpriteData data in map.m_icons)
                {
                    if (data.m_name == pin && data.m_icon != null)
                    {
                        sprite = data.m_icon;
                        break;
                    }
                }
            }
            MapIcon icon = null;
            if (sprite != null && sprite.texture != null)
            {
                Texture2D texture = sprite.texture;
                Rect rect = sprite.textureRect;
                icon = new MapIcon
                {
                    Texture = texture,
                    Uv = new Rect(rect.x / texture.width, rect.y / texture.height, rect.width / texture.width, rect.height / texture.height),
                };
            }
            s_icons[type] = icon;
            return icon;
        }

        private void DrawTooltip(MapPlace place)
        {
            GUIContent content = new GUIContent(BuildTooltip(place));
            Vector2 size = m_tooltipStyle.CalcSize(content);
            Vector2 mouse = Event.current.mousePosition;
            Rect rect = new Rect(mouse.x + 18f, mouse.y + 18f, size.x, size.y);
            if (rect.xMax > Screen.width)
            {
                rect.x = mouse.x - size.x - 12f;
            }
            if (rect.yMax > Screen.height)
            {
                rect.y = mouse.y - size.y - 12f;
            }
            GUI.color = Color.white;
            GUI.Box(rect, content, m_tooltipStyle);
        }

        private static string BuildTooltip(MapPlace place)
        {
            StringBuilder text = new StringBuilder();
            text.Append("<b>").Append(place.Type.Name).Append("</b>  <color=#c0c0c0>").Append(MapCatalog.CategoryName(place.Type.Category)).Append("</color>\n");
            text.Append(CoordText(place.Pos)).Append("   ").Append(DistanceText(place.Pos));
            List<string> contents = PlaceContents.Lines(place.Type.Prefab);
            if (contents != null)
            {
                foreach (string line in contents)
                {
                    text.Append('\n').Append(line);
                }
            }
            string status = ShortStatus(place);
            if (status.Length > 0)
            {
                text.Append('\n').Append(status);
            }
            int flags = MapData.GetFlags(place.Key);
            bool marked = HasMark(place);
            if (marked || (flags & MapData.FlagDone) != 0)
            {
                text.Append('\n');
                if (marked)
                {
                    text.Append(Lang.T("<color=#ffd84a>★ мітка</color>  "));
                }
                if ((flags & MapData.FlagDone) != 0)
                {
                    text.Append(Lang.T("<color=#80ff80>✓ виконано</color>"));
                }
            }
            text.Append(Lang.T("\n<color=#a0a0a0>Клік — докладніше</color>"));
            return text.ToString();
        }

        // ---------------------------------------------------------------- panel

        private void SetPanelRect(Rect rect, float scale)
        {
            m_panelRect = new Rect(rect.x * scale, rect.y * scale, rect.width * scale, rect.height * scale);
            m_panelDrawn = true;
        }

        private const float MinPanelWidth = 320f;
        private bool m_panelResizing;

        // Dragging the panel's left edge makes it wider or narrower; the width is saved.
        private void ResizePanel(Rect area, float screenWidth)
        {
            Rect grip = new Rect(area.x - 6f, area.y, 12f, area.height);
            int id = GUIUtility.GetControlID(FocusType.Passive);
            Event current = Event.current;
            switch (current.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (current.button == 0 && grip.Contains(current.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        m_panelResizing = true;
                        current.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        MapSettings.PanelWidth = Mathf.Clamp(screenWidth - 10f - current.mousePosition.x, MinPanelWidth, screenWidth * 0.7f);
                        current.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        m_panelResizing = false;
                        MapSettings.Changed();
                        current.Use();
                    }
                    break;
                case EventType.Repaint:
                    GUI.color = grip.Contains(current.mousePosition) || m_panelResizing ? new Color(1f, 0.8f, 0.4f, 0.9f) : new Color(1f, 1f, 1f, 0.25f);
                    GUI.DrawTexture(new Rect(area.x - 3f, area.y + area.height * 0.5f - 30f, 3f, 60f), HelperSkin.White);
                    GUI.color = Color.white;
                    break;
            }
        }

        private void DrawPanel(float scale, ref bool searchFocused)
        {
            // Clicking anywhere drops the search focus; a click on the field itself focuses it again.
            if (Event.current.type == EventType.MouseDown && SearchFocused)
            {
                GUIUtility.keyboardControl = 0;
            }
            if (Event.current.type == EventType.Layout)
            {
                if (m_pendingTab >= 0)
                {
                    m_panelTab = m_pendingTab;
                    m_pendingTab = -1;
                    m_panelScroll = Vector2.zero;
                }
                if (m_pendingExpand >= 0)
                {
                    MapCategory category = (MapCategory)m_pendingExpand;
                    if (!m_expanded.Remove(category))
                    {
                        m_expanded.Add(category);
                    }
                    m_pendingExpand = -1;
                }
                if (m_pendingGroup != null)
                {
                    if (!m_expandedGroups.Remove(m_pendingGroup))
                    {
                        m_expandedGroups.Add(m_pendingGroup);
                    }
                    m_pendingGroup = null;
                }
            }
            float width = Screen.width / scale;
            float height = Screen.height / scale;
            if (MapSettings.PanelCollapsed)
            {
                Rect button = new Rect(width - 180f, 64f, 170f, 36f);
                SetPanelRect(button, scale);
                if (GUI.Button(button, Lang.T("« Карта світу")))
                {
                    MapSettings.PanelCollapsed = false;
                    MapSettings.Changed();
                }
                return;
            }
            float panelWidth = Mathf.Clamp(MapSettings.PanelWidth, MinPanelWidth, Mathf.Max(MinPanelWidth, width * 0.7f));
            Rect area = new Rect(width - panelWidth - 10f, 56f, panelWidth, height - 116f);
            ResizePanel(area, width);
            SetPanelRect(new Rect(area.x - 6f, area.y, area.width + 6f, area.height), scale);
            GUI.Box(area, GUIContent.none);
            GUILayout.BeginArea(new Rect(area.x + 8f, area.y + 6f, area.width - 16f, area.height - 12f));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("<b><color=#ffd88c>Карта світу</color></b>"));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("»", GUILayout.Width(34f)))
            {
                MapSettings.PanelCollapsed = true;
                MapSettings.Changed();
            }
            GUILayout.EndHorizontal();
            DrawWorldInfo();
            DrawSelected();
            int tab = GUILayout.SelectionGrid(m_panelTab, s_panelTabs, 3);
            if (tab != m_panelTab)
            {
                m_pendingTab = tab;
            }
            m_panelScroll = GUILayout.BeginScrollView(m_panelScroll);
            switch (m_panelTab)
            {
                case 0: DrawLayers(); break;
                case 1: DrawSearch(ref searchFocused); break;
                case 2: DrawWaypoints(); break;
                case 3: DrawSketches(); break;
                default: DrawSettings(); break;
            }
            GUILayout.EndScrollView();
            DrawCursor();
            GUILayout.Label(Time.unscaledTime < s_messageUntil ? "<color=#80ff80>" + s_message + "</color>" : "", m_smallStyle);
            GUILayout.EndArea();
        }

        // "Малюнки": pen, eraser, layer, colour and width.
        private void DrawSketches()
        {
            GUILayout.Label(Lang.T("<b>Затисни Ctrl</b> і малюй прямо по карті: <b>ліва кнопка</b> — лінія, <b>права</b> — стерти. " +
                "<b>Ctrl+Z</b> — скасувати, <b>Ctrl+Y</b> — повернути (і для стирання теж). Без Ctrl карта працює як завжди; колесо масштабує. " +
                "Лінії прив'язані до місць у світі й видно на мінімапі. Коли друг малює спільне, видно його курсор з іменем і лінію, яку він веде."), m_smallStyle);
            int layer = GUILayout.SelectionGrid(MapSettings.SketchShared ? 1 : 0, s_sketchLayers, 2);
            if ((layer == 1) != MapSettings.SketchShared)
            {
                MapSettings.SketchShared = layer == 1;
                MapSettings.Changed();
            }
            if (MapSettings.SketchShared && !MapSketch.SharedAvailable)
            {
                GUILayout.Label(Lang.T("<color=#ffb040>Спільні малюнки зберігає хост. Від хоста ще нічого не прийшло: у нього немає Valheim Helper цієї версії " +
                    "або дані ще в дорозі. Поки що олівець малює лише для тебе.</color>"), m_smallStyle);
            }
            else
            {
                GUILayout.Label(MapSettings.SketchShared
                    ? Lang.T("<color=#c0c0c0>Спільні лінії бачать і можуть стерти всі гравці з модом; зберігає їх хост.</color>")
                    : Lang.T("<color=#c0c0c0>Ці лінії бачиш лише ти; зберігаються на твоєму комп'ютері для цього світу.</color>"), m_smallStyle);
            }

            GUILayout.BeginHorizontal();
            for (int i = 0; i < SketchRules.Palette.Length; i++)
            {
                Rect swatch = GUILayoutUtility.GetRect(30f, 30f, GUILayout.Width(30f), GUILayout.Height(30f));
                if (Event.current.type == EventType.Repaint)
                {
                    int rgb = SketchRules.Palette[i];
                    Color previous = GUI.color;
                    GUI.color = i == MapSettings.SketchColor ? new Color(1f, 0.85f, 0.4f) : new Color(0f, 0f, 0f, 0.7f);
                    GUI.DrawTexture(swatch, Texture2D.whiteTexture);
                    GUI.color = new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
                    GUI.DrawTexture(new Rect(swatch.x + 3f, swatch.y + 3f, swatch.width - 6f, swatch.height - 6f), Texture2D.whiteTexture);
                    GUI.color = previous;
                }
                if (GUI.Button(swatch, GUIContent.none, GUIStyle.none))
                {
                    MapSettings.SketchColor = i;
                    MapSettings.Changed();
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.F("Товщина: {0:0}", MapSettings.SketchWidth), GUILayout.Width(110f));
            float width = Mathf.Round(GUILayout.HorizontalSlider(MapSettings.SketchWidth, SketchRules.MinWidth, SketchRules.MaxWidth));
            if (!width.Equals(MapSettings.SketchWidth))
            {
                MapSettings.SketchWidth = width;
                MapSettings.Changed();
            }
            GUILayout.EndHorizontal();

            bool shared = MapSettings.SketchShared && MapSketch.SharedAvailable;
            GUILayout.BeginHorizontal();
            GUI.enabled = MapSketch.CanUndo;
            if (GUILayout.Button(Lang.T("Скасувати (Ctrl+Z)")))
            {
                ShowMessage(MapSketch.Undo());
            }
            GUI.enabled = MapSketch.CanRedo;
            if (GUILayout.Button(Lang.T("Повернути (Ctrl+Y)")))
            {
                ShowMessage(MapSketch.Redo());
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (ConfirmButton(shared ? "sketch_clear_my_shared" : "sketch_clear_mine", shared ? Lang.T("Стерти мої спільні") : Lang.T("Стерти всі мої")))
            {
                ShowMessage(MapSketch.ClearMine(shared));
            }
            GUILayout.EndHorizontal();
            if (MapData.IsHost && ConfirmButton("sketch_clear_shared", Lang.T("Стерти всі спільні (хост)")))
            {
                ShowMessage(MapSketch.ClearShared());
            }

            Toggle(ref MapSettings.SketchShowMine, Lang.F(" Показувати мої ({0})", MapSketch.MineCount));
            Toggle(ref MapSettings.SketchShowShared, Lang.F(" Показувати спільні ({0})", MapSketch.SharedCount));
            Toggle(ref MapSettings.SketchOnMinimap, Lang.T(" Малюнки на мінімапі"));
        }

        private void DrawWorldInfo()
        {
            World world = ZNet.World;
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Сід: <b>") + world.m_seedName + "</b>");
            if (GUILayout.Button(Lang.T("Копіювати"), GUILayout.Width(100f)))
            {
                GUIUtility.systemCopyBuffer = world.m_seedName;
                ShowMessage(Lang.T("Сід скопійовано в буфер обміну."));
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(SourceText(), m_smallStyle);
            if (!MapData.IsHost && GUILayout.Button(Lang.T("Оновити"), GUILayout.Width(90f)))
            {
                MapData.RequestFromHost();
            }
            GUILayout.EndHorizontal();
        }

        private static string SourceText()
        {
            switch (MapData.Source)
            {
                case MapData.SourceKind.Host:
                    return Lang.F("Дані з твого світу: {0} місць", MapData.Places.Count);
                case MapData.SourceKind.Client:
                    return Lang.F("Дані від хоста: {0} місць, оновлено {1} с тому", MapData.Places.Count, Mathf.RoundToInt(Time.unscaledTime - MapData.UpdatedAt));
                case MapData.SourceKind.Waiting:
                    return Lang.T("Запитую дані в хоста…");
                case MapData.SourceKind.HostWithoutMod:
                    return Lang.T("<color=#ffb040>Хост не відповів: схоже, у нього немає Valheim Helper. Список місць світу знає лише хост.</color>");
                case MapData.SourceKind.OtherVersion:
                    return Lang.T("<color=#ffb040>У хоста інша версія Valheim Helper — оновіться обидва.</color>");
                default:
                    return Lang.T("Завантаження…");
            }
        }

        private void DrawSelected()
        {
            MapPlace place = GetSelected();
            if (place == null)
            {
                return;
            }
            EnsureCounts();
            int flags = MapData.GetFlags(place.Key);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{place.Type.Name}</b>  <color=#c0c0c0>{MapCatalog.CategoryName(place.Type.Category)}</color>");
            GUILayout.FlexibleSpace();
            bool close = GUILayout.Button("✕", GUILayout.Width(30f));
            GUILayout.EndHorizontal();
            GUILayout.Label(CoordText(place.Pos) + "   " + DistanceText(place.Pos), m_smallStyle);
            Heightmap.Biome biome = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(place.Pos.x, place.Pos.z) : Heightmap.Biome.None;
            GUILayout.Label(MapBiomes.BiomeName(biome) + "   " + ExploredText(place.Pos), m_smallStyle);
            GUILayout.Label(StatusText(place), m_smallStyle);
            Waypoints.Mark placeMark = MarkAt(place);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(placeMark != null ? Lang.T("★ Мітка стоїть") : Lang.T("☆ Поставити мітку")))
            {
                if (placeMark != null)
                {
                    ShowMessage(Waypoints.Remove(placeMark));
                }
                else
                {
                    ShowMessage(Waypoints.Add(place.Pos, place.Type.Name));
                }
            }
            if (GUILayout.Button((flags & MapData.FlagDone) != 0 ? Lang.T("✓ Виконано") : Lang.T("Позначити виконаним")))
            {
                MapData.ToggleFlag(place, MapData.FlagDone);
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            bool guided = placeMark != null && Waypoints.GuideId == placeMark.Id;
            if (GUILayout.Button(guided ? Lang.T("Веду сюди") : Lang.T("Вести сюди")))
            {
                if (guided)
                {
                    Waypoints.GuideId = 0L;
                }
                else
                {
                    Waypoints.Mark mark = placeMark ?? Waypoints.Create(place.Pos, place.Type.Name);
                    if (mark != null)
                    {
                        Waypoints.GuideId = mark.Id;
                        ShowMessage(Lang.F("Стрілка на краю екрана веде до «{0}».", mark.Name));
                    }
                }
            }
            if (GUILayout.Button(Lang.T("Показати на карті")))
            {
                CenterOn(place.Pos);
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            if (close)
            {
                m_selectedKey = null;
                m_selected = null;
            }
        }

        // The mark standing on a place (put there from its card or by hand), if any.
        private static Waypoints.Mark MarkAt(MapPlace place)
        {
            return Waypoints.Nearest(place.Pos, 4f);
        }

        private static bool HasMark(MapPlace place)
        {
            return MarkAt(place) != null;
        }

        private static bool s_favoritesMoved;

        // Favourites of older versions become marks (the one list of places you keep), once per world.
        private static void MoveFavoritesToMarks()
        {
            if (s_favoritesMoved || MapData.Places.Count == 0 || Player.m_localPlayer == null)
            {
                return;
            }
            s_favoritesMoved = true;
            if (!MapData.IsHost && MapData.MarksShared)
            {
                // the host moves shared favourites for everybody
                return;
            }
            int moved = 0;
            foreach (MapPlace place in MapData.Places)
            {
                if ((MapData.GetFlags(place.Key) & MapData.FlagFavorite) == 0)
                {
                    continue;
                }
                if (MarkAt(place) == null)
                {
                    Waypoints.Create(place.Pos, place.Type.Name);
                }
                moved++;
            }
            if (moved > 0)
            {
                MapData.ClearFlag(MapData.FlagFavorite);
                ShowMessage(Lang.F("Обрані місця ({0}) тепер мітки: вкладка «Мітки».", moved));
            }
        }

        private void Select(MapPlace place, bool center)
        {
            m_selectedKey = place.Key;
            m_selected = place;
            m_selectedVersion = MapData.Version;
            if (center)
            {
                CenterOn(place.Pos);
            }
        }

        private MapPlace GetSelected()
        {
            if (m_selectedKey == null)
            {
                return null;
            }
            if (m_selectedVersion != MapData.Version)
            {
                m_selected = MapData.FindByKey(m_selectedKey);
                m_selectedVersion = MapData.Version;
            }
            return m_selected;
        }

        private void EnsureCounts()
        {
            if (m_countsVersion == MapData.Version && m_countsVegKinds == WorldVegetation.Kinds.Count)
            {
                return;
            }
            m_countsVersion = MapData.Version;
            m_countsVegKinds = WorldVegetation.Kinds.Count;
            m_typeCounts.Clear();
            m_typesByCategory.Clear();
            foreach (MapPlace place in MapData.Places)
            {
                if (!m_typeCounts.TryGetValue(place.Type, out int[] counts))
                {
                    counts = new int[3];
                    m_typeCounts[place.Type] = counts;
                }
                counts[0]++;
                if (place.Placed)
                {
                    counts[1]++;
                }
                if (place.Unique)
                {
                    counts[2] = 1;
                }
            }
            foreach (MapType type in m_typeCounts.Keys)
            {
                if (!m_typesByCategory.TryGetValue(type.Category, out List<MapType> list))
                {
                    list = new List<MapType>();
                    m_typesByCategory[type.Category] = list;
                }
                list.Add(type);
            }
            foreach (List<MapType> list in m_typesByCategory.Values)
            {
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            }
            BuildRows();
        }

        private void BuildRows()
        {
            m_rowsByCategory.Clear();
            foreach (KeyValuePair<MapCategory, List<MapType>> pair in m_typesByCategory)
            {
                Dictionary<string, LayerRow> byName = new Dictionary<string, LayerRow>(StringComparer.CurrentCultureIgnoreCase);
                List<LayerRow> rows = new List<LayerRow>();
                foreach (MapType type in pair.Value)
                {
                    if (!byName.TryGetValue(type.Name, out LayerRow row))
                    {
                        row = new LayerRow { Name = type.Name, Group = Lang.T("Місця з ресурсами"), Icon = GetIcon(type) };
                        byName[type.Name] = row;
                        rows.Add(row);
                    }
                    row.Types.Add(type);
                    int[] counts = m_typeCounts[type];
                    row.Counts[0] += counts[0];
                    row.Counts[1] += counts[1];
                    row.Counts[2] = Math.Max(row.Counts[2], counts[2]);
                }
                if (pair.Key == MapCategory.Resources)
                {
                    AddVegetationRows(rows, byName);
                }
                m_rowsByCategory[pair.Key] = rows;
            }
            if (!m_rowsByCategory.ContainsKey(MapCategory.Resources) && WorldVegetation.Kinds.Count > 0)
            {
                List<LayerRow> rows = new List<LayerRow>();
                AddVegetationRows(rows, new Dictionary<string, LayerRow>(StringComparer.CurrentCultureIgnoreCase));
                m_rowsByCategory[MapCategory.Resources] = rows;
            }
        }

        // Whole-world resources join the rows: into a place row of the same name, or a row of their own under their
        // group (ores and stone, plants, trees); places with resources come last.
        private static void AddVegetationRows(List<LayerRow> rows, Dictionary<string, LayerRow> byName)
        {
            foreach (WorldVegetation.Kind kind in WorldVegetation.Kinds)
            {
                if (!byName.TryGetValue(kind.Name, out LayerRow row))
                {
                    row = new LayerRow { Name = kind.Name, Icon = kind.Icon };
                    byName[kind.Name] = row;
                    rows.Add(row);
                }
                row.Kinds.Add(kind);
                row.Group = Lang.T(kind.Group);
                if (kind.Icon != null)
                {
                    row.Icon = kind.Icon;
                }
            }
            List<string> order = new List<string>();
            foreach (WorldVegetation.Kind kind in WorldVegetation.Kinds)
            {
                string group = Lang.T(kind.Group);
                if (!order.Contains(group))
                {
                    order.Add(group);
                }
            }
            rows.Sort((a, b) =>
            {
                int ga = order.IndexOf(a.Group);
                int gb = order.IndexOf(b.Group);
                ga = ga < 0 ? int.MaxValue : ga;
                gb = gb < 0 ? int.MaxValue : gb;
                return ga != gb ? ga.CompareTo(gb) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
        }

        private void DrawLayers()
        {
            DrawItemShown();
            EnsureCounts();
            if (MapData.Places.Count == 0)
            {
                GUILayout.Label(Lang.T("Поки немає даних про місця світу."), m_smallStyle);
                return;
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Lang.T("Усі")))
            {
                MapSettings.SetTypesShown(m_typeCounts.Keys, true);
                MapSettings.SetVegShown(AllVegIds(), true);
                MapSettings.ShowTamed = true;
                MapSettings.Changed();
            }
            if (GUILayout.Button(Lang.T("Стандартні")))
            {
                HideItem();
                MapSettings.ResetTypes();
                MapSettings.SetVegShown(AllVegIds(), false);
            }
            if (GUILayout.Button(Lang.T("Жодного")))
            {
                HideItem();
                MapSettings.SetTypesShown(m_typeCounts.Keys, false);
                MapSettings.SetVegShown(AllVegIds(), false);
                MapSettings.ShowTamed = false;
                MapSettings.Changed();
            }
            GUILayout.EndHorizontal();
            foreach (MapCategory category in MapCatalog.Categories)
            {
                bool resources = category == MapCategory.Resources;
                if (!m_rowsByCategory.TryGetValue(category, out List<LayerRow> rows))
                {
                    continue;
                }
                // a kind nothing of which is known yet (a dungeon's contents before anybody has been near) has no row
                rows = rows.FindAll(row => row.Types.Count > 0 || row.Count > 0);
                if (rows.Count == 0)
                {
                    continue;
                }
                int total = 0;
                int shown = 0;
                int parts = 0;
                foreach (LayerRow row in rows)
                {
                    total += row.Count;
                    shown += row.Shown();
                    parts += row.Parts;
                }
                bool all = shown == parts;
                string partial = shown > 0 && !all ? Lang.T("  <color=#ffd080>частково</color>") : "";
                GUILayout.BeginHorizontal();
                bool result = GUILayout.Toggle(all, $" <b>{MapCatalog.CategoryName(category)}</b>  <color=#a0a0a0>{total}</color>{partial}");
                if (result != all)
                {
                    foreach (LayerRow row in rows)
                    {
                        row.Set(result);
                    }
                }
                GUILayout.FlexibleSpace();
                bool expanded = m_expanded.Contains(category);
                if (GUILayout.Button(expanded ? "−" : "+", GUILayout.Width(30f)))
                {
                    m_pendingExpand = (int)category;
                }
                GUILayout.EndHorizontal();
                if (!expanded)
                {
                    continue;
                }
                if (resources)
                {
                    DrawVegetationProgress();
                }
                if (!resources)
                {
                    foreach (LayerRow row in rows)
                    {
                        DrawLayerRow(row);
                    }
                    continue;
                }
                // resources come in groups (ores, plants, trees, chests, creatures, places…), each folded with one switch
                for (int start = 0; start < rows.Count;)
                {
                    string group = rows[start].Group;
                    int end = start;
                    while (end < rows.Count && rows[end].Group == group)
                    {
                        end++;
                    }
                    DrawLayerGroup(group, rows.GetRange(start, end - start));
                    start = end;
                }
            }
            bool tamed = GUILayout.Toggle(MapSettings.ShowTamed, Lang.T(" <b>Приручені тварини</b>  <color=#a0a0a0>зелені — ситі, помаранчеві — голодні</color>"));
            if (tamed != MapSettings.ShowTamed)
            {
                MapSettings.ShowTamed = tamed;
                MapSettings.Changed();
            }
        }

        private static List<string> AllVegIds()
        {
            List<string> ids = new List<string>();
            foreach (WorldVegetation.Kind kind in WorldVegetation.Kinds)
            {
                ids.Add(kind.Id);
            }
            return ids;
        }

        // How far the whole-world resources are computed.
        private void DrawVegetationProgress()
        {
            if (WorldVegetation.Kinds.Count == 0)
            {
                return;
            }
            float done = WorldVegetation.ZonesTotal > 0 ? 100f * WorldVegetation.ZonesDone / WorldVegetation.ZonesTotal : 0f;
            GUILayout.Label(done >= 100f
                ? Lang.T("<color=#a0a0a0>Де хтось уже був — справжні об'єкти світу. Решту мод обчислив за сідом: рослинність точно, " +
                    "а вміст ще не відвіданих локацій — у їхніх межах (гра повертає локацію, лише коли хтось підходить).</color>")
                : Lang.F("<color=#ffd080>Обчислюю ресурси всього світу за сідом: {0:0}% ({1}); найближче до тебе вже готове.</color>", done, WorldVegetation.Mode), m_smallStyle);
            if (PlaceContents.Pending > 0)
            {
                GUILayout.Label(Lang.F("<color=#ffd080>Читаю, що лежить у локаціях гри (скрині, спавнери, рослини…): лишилось {0}.</color>", PlaceContents.Pending), m_smallStyle);
            }
        }

        private readonly HashSet<string> m_expandedGroups = new HashSet<string>();
        private string m_pendingGroup;

        // A group of resource rows: on or off together, folded until opened.
        private void DrawLayerGroup(string group, List<LayerRow> rows)
        {
            int total = 0;
            int shown = 0;
            int parts = 0;
            foreach (LayerRow row in rows)
            {
                total += row.Count;
                shown += row.Shown();
                parts += row.Parts;
            }
            bool all = shown == parts;
            string partial = shown > 0 && !all ? Lang.T("  <color=#ffd080>частково</color>") : "";
            GUILayout.BeginHorizontal();
            GUILayout.Space(12f);
            bool result = GUILayout.Toggle(all, $" {group}  <color=#a0a0a0>{total}</color>{partial}");
            if (result != all)
            {
                foreach (LayerRow row in rows)
                {
                    row.Set(result);
                }
            }
            GUILayout.FlexibleSpace();
            bool expanded = m_expandedGroups.Contains(group);
            if (GUILayout.Button(expanded ? "−" : "+", GUILayout.Width(30f)))
            {
                m_pendingGroup = group;
            }
            GUILayout.EndHorizontal();
            if (expanded)
            {
                foreach (LayerRow row in rows)
                {
                    DrawLayerRow(row);
                }
            }
        }

        // One row: the icon, the name, how many (and near how many someone has been), on or off together.
        private void DrawLayerRow(LayerRow row)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(22f);
            Rect icon = GUILayoutUtility.GetRect(20f, 20f, GUILayout.Width(20f), GUILayout.Height(20f));
            if (row.Icon != null && Event.current.type == EventType.Repaint)
            {
                GUI.DrawTextureWithTexCoords(icon, row.Icon.Texture, row.Icon.Uv);
            }
            int shown = row.Shown();
            bool all = shown == row.Parts;
            string count = row.Kinds.Count > 0
                ? Lang.F("{0} по всьому світу", row.Count)
                : TypeCountText(row.Counts);
            string partial = shown > 0 && !all ? Lang.T("  <color=#ffd080>частково</color>") : "";
            bool result = GUILayout.Toggle(all, $" {row.Name}  <color=#a0a0a0>{count}</color>{partial}");
            if (result != all)
            {
                row.Set(result);
            }
            GUILayout.EndHorizontal();
        }

        private static readonly HashSet<long> s_vegCells = new HashSet<long>();
        private static WorldVegetation.Kind s_hoveredVeg;
        private static Vector2 s_hoveredVegPos;
        // Where the hovered one comes from (a location's guess, a dungeon's inside…).
        private static VegStore s_hoveredVegStore;
        private const int MaxVegetationIcons = 6000;

        // Where the map takes a kind's objects from: computed from the seed (vegetation, then location contents),
        // skipped in zones already generated, and the real objects found there.
        private enum VegStore
        {
            Computed,
            Placed,
            Actual,
            Underground,
        }

        private static Dictionary<long, WorldVegetation.Cell> StoreOf(WorldVegetation.Kind kind, VegStore store)
        {
            switch (store)
            {
                case VegStore.Computed:
                    return kind.Cells;
                case VegStore.Placed:
                    return kind.Placed;
                case VegStore.Actual:
                    return kind.Actual;
                default:
                    return kind.Underground;
            }
        }

        // Every kind switched on, from all three stores: only the cells in view; far out one icon per cell and kind,
        // closer in a sample, close up every object. Icons that would cover each other are drawn once.
        private static void DrawVegetation(Minimap map, Rect uv, Rect mapRect, float size, Vector2 pointer, bool hover)
        {
            if (hover)
            {
                s_hoveredVeg = null;
            }
            if (MapSettings.VegShown.Count == 0 || WorldVegetation.Kinds.Count == 0)
            {
                return;
            }
            float half = map.m_textureSize / 2f;
            float minX = (uv.xMin * map.m_textureSize - half) * map.m_pixelSize;
            float maxX = (uv.xMax * map.m_textureSize - half) * map.m_pixelSize;
            float minZ = (uv.yMin * map.m_textureSize - half) * map.m_pixelSize;
            float maxZ = (uv.yMax * map.m_textureSize - half) * map.m_pixelSize;
            float metersPerPixel = Mathf.Max(0.001f, (maxX - minX) / Mathf.Max(1f, mapRect.width));
            float cellPx = WorldVegetation.CellSize / metersPerPixel;
            int x0 = Mathf.FloorToInt(minX / WorldVegetation.CellSize);
            int x1 = Mathf.FloorToInt(maxX / WorldVegetation.CellSize);
            int z0 = Mathf.FloorToInt(minZ / WorldVegetation.CellSize);
            int z1 = Mathf.FloorToInt(maxZ / WorldVegetation.CellSize);
            int perCell = cellPx < size * 2f ? 1 : cellPx < 256f ? 16 : int.MaxValue;
            s_vegCells.Clear();
            float cell = Mathf.Max(6f, size * 0.8f);
            float best = (size * 0.5f + 2f) * (size * 0.5f + 2f);
            Color ring = MapCatalog.CategoryColor(MapCategory.Resources);
            int drawn = 0;
            foreach (WorldVegetation.Kind kind in WorldVegetation.Kinds)
            {
                if (!MapSettings.VegShown.Contains(kind.Id))
                {
                    continue;
                }
                for (VegStore store = VegStore.Computed; store <= VegStore.Underground; store++)
                {
                    Dictionary<long, WorldVegetation.Cell> source = StoreOf(kind, store);
                    if (source.Count == 0)
                    {
                        continue;
                    }
                    // a guess only where nobody has been: the real objects stand there instead
                    bool guess = store == VegStore.Computed || store == VegStore.Placed;
                    bool walkCells = (long)(x1 - x0 + 1) * (z1 - z0 + 1) > source.Count;
                    IEnumerable<WorldVegetation.Cell> cells = walkCells ? (IEnumerable<WorldVegetation.Cell>)source.Values : VisibleCells(source, x0, x1, z0, z1);
                    foreach (WorldVegetation.Cell positions in cells)
                    {
                        if (positions.X0 > maxX || positions.X0 + WorldVegetation.CellSize < minX
                            || positions.Z0 > maxZ || positions.Z0 + WorldVegetation.CellSize < minZ)
                        {
                            continue;
                        }
                        int stride = perCell == int.MaxValue ? 1 : Mathf.Max(1, positions.Count / perCell);
                        for (int i = 0; i < positions.Count; i += stride)
                        {
                            Vector2 position = positions[i];
                            if (guess && WorldObjects.IsGenerated(position.x, position.y))
                            {
                                continue;
                            }
                            if (!WorldToGui(map, uv, mapRect, new Vector3(position.x, 0f, position.y), out Vector2 at))
                            {
                                continue;
                            }
                            long key = ((long)Mathf.FloorToInt(at.x / cell) << 32) ^ (uint)Mathf.FloorToInt(at.y / cell);
                            if (!s_vegCells.Add(key))
                            {
                                continue;
                            }
                            if (kind.Icon == null || size < 12f)
                            {
                                GUI.color = new Color(0f, 0f, 0f, 0.8f);
                                GUI.DrawTexture(Square(at, Mathf.Max(6f, size * 0.5f) + 3f), HelperSkin.Circle);
                                GUI.color = ring;
                                GUI.DrawTexture(Square(at, Mathf.Max(6f, size * 0.5f)), HelperSkin.Circle);
                            }
                            else
                            {
                                GUI.color = new Color(0.06f, 0.05f, 0.04f, 0.85f);
                                GUI.DrawTexture(Square(at, size), HelperSkin.Circle);
                                GUI.color = ring;
                                GUI.DrawTexture(Square(at, size), HelperSkin.Ring);
                                GUI.color = Color.white;
                                GUI.DrawTextureWithTexCoords(Square(at, size * 0.7f), kind.Icon.Texture, kind.Icon.Uv);
                            }
                            if (hover)
                            {
                                float distance = (at - pointer).sqrMagnitude;
                                if (distance < best)
                                {
                                    best = distance;
                                    s_hoveredVeg = kind;
                                    s_hoveredVegPos = position;
                                    s_hoveredVegStore = store;
                                }
                            }
                            if (++drawn >= MaxVegetationIcons)
                            {
                                GUI.color = Color.white;
                                return;
                            }
                        }
                    }
                }
            }
            GUI.color = Color.white;
        }

        private static IEnumerable<WorldVegetation.Cell> VisibleCells(Dictionary<long, WorldVegetation.Cell> source, int x0, int x1, int z0, int z1)
        {
            for (int cx = x0; cx <= x1; cx++)
            {
                for (int cz = z0; cz <= z1; cz++)
                {
                    if (source.TryGetValue(WorldVegetation.ZoneKey(cx, cz), out WorldVegetation.Cell positions))
                    {
                        yield return positions;
                    }
                }
            }
        }

        private void DrawVegetationTooltip()
        {
            string item = s_hoveredVeg.MainItem != null ? Localization.instance.Localize(s_hoveredVeg.MainItem) : Lang.T(s_hoveredVeg.Group);
            string where = s_hoveredVegStore == VegStore.Placed
                ? "\n" + Lang.T("<color=#ffd080>У цій локації, місце приблизне: гра поверне її, коли хтось підійде.</color>")
                : s_hoveredVegStore == VegStore.Underground
                    ? "\n" + Lang.T("<color=#c0a0ff>Усередині підземелля, під цим місцем.</color>")
                    : "";
            GUIContent content = new GUIContent($"<b>{s_hoveredVeg.Name}</b>\n{item} · {DistanceText(new Vector3(s_hoveredVegPos.x, 0f, s_hoveredVegPos.y))}{where}");
            Vector2 size = m_tooltipStyle.CalcSize(content);
            Vector2 mouse = Event.current.mousePosition;
            Rect rect = new Rect(mouse.x + 18f, mouse.y + 18f, size.x, size.y);
            if (rect.xMax > Screen.width)
            {
                rect.x = mouse.x - size.x - 12f;
            }
            if (rect.yMax > Screen.height)
            {
                rect.y = mouse.y - size.y - 12f;
            }
            GUI.color = Color.white;
            GUI.Box(rect, content, m_tooltipStyle);
        }

        private static string TypeCountText(int[] counts)
        {
            if (counts[2] != 0)
            {
                return counts[1] > 0 ? Lang.T("закріплено") : Lang.F("{0} можливих місць", counts[0]);
            }
            return counts[1] > 0 ? Lang.F("{0}, біля {1} уже були", counts[0], counts[1]) : counts[0].ToString();
        }

        private void DrawSearch(ref bool searchFocused)
        {
            PlaceContents.Request();
            if (Event.current.type == EventType.Layout)
            {
                UpdateSearch();
            }
            GUI.SetNextControlName(SearchControl);
            m_search = GUILayout.TextField(m_search ?? "");
            searchFocused = GUI.GetNameOfFocusedControl() == SearchControl;
            if (m_lastSearch.Length == 0)
            {
                GUILayout.Label(Lang.T("Введи, що шукаєш: річ («мідь», «морошка», «Дирнвін»), істоту («троль»), місце («поховальні») чи торговця («продає»). " +
                    "Для речі мод покаже всі місця, де її взяти, по всьому світу; для місця — найближче.") +
                    (PlaceContents.Ready ? "" : Lang.T(" <color=#ffb040>Мод у фоні вивчає вміст місць (лише перший раз, близько хвилини, без гальм), " +
                    "пошук за предметами запрацює сам.</color>")), m_smallStyle);
                return;
            }
            foreach (ItemRow row in m_itemRows)
            {
                bool shown = s_shownItem == row.Item;
                string state = !shown ? "" : ItemSources.Searching ? Lang.T("  <color=#ffd070>шукаю…</color>") : Lang.T("  <color=#80ff80>на карті</color>");
                GUILayout.BeginHorizontal();
                Rect icon = GUILayoutUtility.GetRect(26f, 26f, GUILayout.Width(26f), GUILayout.Height(26f));
                if (row.Icon != null && Event.current.type == EventType.Repaint)
                {
                    GUI.DrawTextureWithTexCoords(icon, row.Icon.Texture, row.Icon.Uv);
                }
                if (GUILayout.Button($"<b>{row.Name}</b>{state}\n<color=#c0c0c0>{row.Where}</color>", m_leftButton))
                {
                    ToggleItem(row);
                }
                GUILayout.EndHorizontal();
            }
            DrawItemShown();
            if (m_itemRows.Count > 0 || m_searchGroups.Count > 0)
            {
                GUILayout.Label(m_itemRows.Count > 0 ? Lang.T("Натисни на річ — на карті з'являться всі місця, де її взяти; ще раз — сховати.") : "", m_smallStyle);
            }
            if (m_searchGroups.Count > 0)
            {
                GUILayout.Label(Lang.F("<b>Місця</b> ({0}): натисни — покаже найближче, ще раз — наступне.", m_searchTotal), m_smallStyle);
            }
            else if (m_itemRows.Count == 0)
            {
                GUILayout.Label(Lang.T("Нічого не знайдено."), m_smallStyle);
            }
            foreach (SearchGroup group in m_searchGroups)
            {
                string why = group.Why != null ? $" — <color=#ffd070>{group.Why}</color>" : "";
                string count = group.Places.Count > 1 ? Lang.F("{0}, найближче ", group.Places.Count) : "";
                string current = group.Next > 0 ? $" <color=#ffd070>[{group.Next}/{group.Places.Count}]</color>" : "";
                if (GUILayout.Button($"{group.Name}{why}  <color=#a0a0a0>{count}{DistanceText(group.Places[0].Pos)}</color>{current}", m_leftButton))
                {
                    MapPlace place = group.Places[group.Next % group.Places.Count];
                    group.Next = group.Next % group.Places.Count + 1;
                    SetSearchFocus(group.Name);
                    Select(place, center: true);
                }
            }
        }

        // What the search shows on the map now, and a way to hide it; also on the layers tab.
        private void DrawItemShown()
        {
            if (s_focusName != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Lang.F("Підсвічено: <b>{0}</b>", s_focusName), m_smallStyle);
                if (GUILayout.Button(Lang.T("Сховати"), GUILayout.Width(90f)))
                {
                    SetSearchFocus(null);
                }
                GUILayout.EndHorizontal();
            }
            if (s_shownItem == null)
            {
                return;
            }
            int total = s_shownWorldCount + (ItemSources.ResultsFor == s_shownItem ? ItemSources.ResultsTotal : 0);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.F("На карті: <b>{0}</b> — {1}", Localization.instance.Localize(s_shownItem), total), m_smallStyle);
            if (GUILayout.Button(Lang.T("Сховати"), GUILayout.Width(90f)))
            {
                HideItem();
            }
            GUILayout.EndHorizontal();
        }

        // One click shows every place an item comes from: its resource layers over the whole world and, for what
        // the layers do not cover (creatures, barrels…), the sources in the explored zones.
        private void ToggleItem(ItemRow row)
        {
            if (s_shownItem == row.Item)
            {
                HideItem();
                return;
            }
            HideItem();
            s_shownItem = row.Item;
            s_shownWorldCount = row.WorldCount;
            foreach (WorldVegetation.Kind kind in row.Kinds)
            {
                if (!MapSettings.VegShown.Contains(kind.Id))
                {
                    s_shownKinds.Add(kind.Id);
                }
            }
            MapSettings.SetVegShown(s_shownKinds, true);
            bool other = row.Sources.Exists(source => !WorldVegetation.Covers(source.Prefab));
            if (other)
            {
                ItemSources.RequestNearest(row.Item);
            }
            ShowMessage(row.WorldCount > 0
                ? Lang.F("«{0}»: {1} місць по всьому світу", row.Name, row.WorldCount) + (other ? Lang.T(", інші джерела з'являться за мить.") : ".")
                : other ? Lang.F("«{0}»: шукаю джерела в розвіданих зонах…", row.Name) : Lang.F("«{0}»: джерел на карті немає.", row.Name));
        }

        private static void HideItem()
        {
            if (s_shownKinds.Count > 0)
            {
                MapSettings.SetVegShown(s_shownKinds, false);
                s_shownKinds.Clear();
            }
            if (ItemSources.ResultsFor != null)
            {
                ItemSources.Clear();
            }
            s_shownItem = null;
            s_shownWorldCount = 0;
        }

        private sealed class SearchGroup
        {
            public string Name;
            public string Why;
            public readonly List<MapPlace> Places = new List<MapPlace>();
            public int Next;
        }

        private readonly List<SearchGroup> m_searchGroups = new List<SearchGroup>();
        // Places of this name are shown even when their layer is off (the kind picked in the search).
        private static string s_focusName;
        private static int s_focusVersion;

        private static void SetSearchFocus(string name)
        {
            if (s_focusName != name)
            {
                s_focusName = name;
                s_focusVersion++;
            }
        }

        // Items matching the query by their own name or by what gives them ("поклад олова" → tin ore), one row each.
        private void BuildItemRows(string query)
        {
            m_itemRows.Clear();
            if (query.Length < 2)
            {
                return;
            }
            Dictionary<string, ItemRow> rows = new Dictionary<string, ItemRow>();
            void AddRow(string item, List<ItemSources.Source> sources)
            {
                if (string.IsNullOrEmpty(item) || rows.Count >= MaxItemRows && !rows.ContainsKey(item))
                {
                    return;
                }
                if (!rows.TryGetValue(item, out ItemRow row))
                {
                    row = new ItemRow { Item = item, Name = Localization.instance.Localize(item) };
                    rows[item] = row;
                    m_itemRows.Add(row);
                }
                if (sources != null && row.Sources.Count == 0)
                {
                    row.Sources = sources;
                }
            }
            foreach (WorldVegetation.Kind kind in WorldVegetation.Kinds)
            {
                if (kind.Name.ToLowerInvariant().Contains(query))
                {
                    AddRow(kind.MainItem, null);
                }
            }
            foreach (KeyValuePair<string, List<ItemSources.Source>> pair in ItemSources.Matching(query, MaxItemRows))
            {
                AddRow(pair.Key, pair.Value);
            }
            foreach (WorldVegetation.Kind kind in WorldVegetation.Kinds)
            {
                foreach (string item in kind.Items)
                {
                    if (Localization.instance.Localize(item).ToLowerInvariant().Contains(query))
                    {
                        AddRow(item, null);
                    }
                }
            }
            foreach (ItemRow row in m_itemRows)
            {
                if (row.Sources.Count == 0)
                {
                    row.Sources = ItemSources.SourcesOf(row.Item);
                }
                row.Kinds = WorldVegetation.KindsGiving(row.Item);
                row.Icon = ItemSources.IconOf(row.Item);
                List<string> parts = new List<string>();
                List<string> kinds = new List<string>();
                foreach (WorldVegetation.Kind kind in row.Kinds)
                {
                    row.WorldCount += kind.Total;
                    if (kinds.Count < 4)
                    {
                        kinds.Add(kind.Name);
                    }
                }
                if (row.Kinds.Count > 0)
                {
                    parts.Add(string.Join(", ", kinds.ToArray()) + (row.Kinds.Count > 4 ? Lang.F(" і ще {0}", row.Kinds.Count - 4) : "") +
                        Lang.F(" — {0} по всьому світу", row.WorldCount));
                }
                string other = ItemSources.Describe(row.Sources);
                if (other.Length > 0)
                {
                    parts.Add(other);
                }
                row.Where = parts.Count > 0 ? string.Join("; ", parts.ToArray()) : Lang.T("джерел поза місцями немає");
            }
        }

        private void UpdateSearch()
        {
            string query = (m_search ?? "").Trim().ToLowerInvariant();
            if (query == m_lastSearch && m_searchDataVersion == MapData.Version && m_searchContentsVersion == PlaceContents.Version
                && Time.unscaledTime < m_searchRefreshAt)
            {
                return;
            }
            m_lastSearch = query;
            m_searchDataVersion = MapData.Version;
            m_searchContentsVersion = PlaceContents.Version;
            m_searchRefreshAt = Time.unscaledTime + 1f;
            m_searchResults.Clear();
            m_searchMatches.Clear();
            BuildItemRows(query);
            m_searchTotal = 0;
            if (query.Length == 0)
            {
                m_searchGroups.Clear();
                SetSearchFocus(null);
                return;
            }
            foreach (MapPlace place in MapData.Places)
            {
                if (place.Type.SearchKey.Contains(query))
                {
                    m_searchResults.Add(place);
                }
                else if (PlaceContents.SearchText(place.Type.Prefab).Contains(query))
                {
                    m_searchResults.Add(place);
                    if (!m_searchMatches.ContainsKey(place.Type))
                    {
                        m_searchMatches[place.Type] = PlaceContents.MatchFor(place.Type.Prefab, query);
                    }
                }
            }
            m_searchTotal = m_searchResults.Count;
            SortByDistance(m_searchResults);
            // One row per kind (the same name), nearest kinds first; a kind keeps its place in the cycle when the list refreshes.
            Dictionary<string, int> next = new Dictionary<string, int>();
            foreach (SearchGroup old in m_searchGroups)
            {
                next[old.Name] = old.Next;
            }
            m_searchGroups.Clear();
            Dictionary<string, SearchGroup> byName = new Dictionary<string, SearchGroup>();
            foreach (MapPlace place in m_searchResults)
            {
                string name = place.Type.Name;
                if (!byName.TryGetValue(name, out SearchGroup group))
                {
                    group = new SearchGroup { Name = name, Next = next.TryGetValue(name, out int n) ? n : 0 };
                    m_searchMatches.TryGetValue(place.Type, out group.Why);
                    byName[name] = group;
                    m_searchGroups.Add(group);
                }
                group.Places.Add(place);
            }
            if (m_searchGroups.Count > MaxSearchResults)
            {
                m_searchGroups.RemoveRange(MaxSearchResults, m_searchGroups.Count - MaxSearchResults);
            }
        }

        private void DrawCursor()
        {
            if (!m_cursorValid || WorldGenerator.instance == null)
            {
                GUILayout.Label(Lang.T("<color=#a0a0a0>Наведи курсор на карту, щоб побачити координати</color>"), m_smallStyle);
                return;
            }
            Vector3 point = m_cursorWorld;
            string text = Lang.F("Курсор: X {0:0}  Z {1:0}", point.x, point.z);
            if (MapSettings.ShowHeight)
            {
                text += $"  Y {WorldGenerator.instance.GetHeight(point.x, point.z):0}";
            }
            text += "   " + MapBiomes.BiomeName(WorldGenerator.instance.GetBiome(point.x, point.z));
            GUILayout.Label(text, m_smallStyle);
        }

        // "Вигляд": how the map looks. The only place these settings are.
        private void DrawSettings()
        {
            bool sharedHidden = MapSettings.ShowUnexplored && Minimap.instance != null && !s_showSharedMap(Minimap.instance);
            bool unexplored = GUILayout.Toggle(MapSettings.ShowUnexplored, Lang.T(" Показувати недосліджене (як дані з мапознавчого столу)"));
            if (unexplored != MapSettings.ShowUnexplored)
            {
                MapSettings.SetShowUnexplored(unexplored);
            }
            if (sharedHidden)
            {
                GUILayout.Label(Lang.T("<color=#ffb040>На карті вимкнено показ спільних даних. Увімкни його кнопкою на самій карті, інакше недосліджене не буде видно.</color>"));
            }
            Toggle(ref MapSettings.UseDots, Lang.T(" Кольорові крапки замість іконок"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.F("Розмір значків: {0:0}", MapSettings.IconSize), GUILayout.Width(150f));
            float size = Mathf.Round(GUILayout.HorizontalSlider(MapSettings.IconSize, 14f, 48f, GUILayout.Width(190f)));
            if (size != MapSettings.IconSize)
            {
                MapSettings.IconSize = size;
                MapSettings.Changed();
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Lang.T("<color=#c0c0c0>Значки місць більшають, коли наближуєш карту, і меншають, коли віддаляєш.</color>"));
            Toggle(ref MapSettings.ShowOnMinimap, Lang.T(" Показувати значки й на мінімапі в куті екрана"));
            if (MapSettings.ShowOnMinimap)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Lang.F("Значки на мінімапі: {0:0}", MapSettings.MinimapIconSize), GUILayout.Width(150f));
                float miniSize = Mathf.Round(GUILayout.HorizontalSlider(MapSettings.MinimapIconSize, MapSettings.MinMinimapIconSize, MapSettings.MaxMinimapIconSize, GUILayout.Width(190f)));
                if (miniSize != MapSettings.MinimapIconSize)
                {
                    MapSettings.MinimapIconSize = miniSize;
                    MapSettings.Changed();
                }
                GUILayout.EndHorizontal();
            }
            Toggle(ref MapSettings.ShowHeight, Lang.T(" Показувати висоту (Y)"));
            GUILayout.BeginHorizontal();
            Toggle(ref MapSettings.HideDone, Lang.F(" Ховати місця, позначені виконаними ({0})", MapData.CountFlag(MapData.FlagDone)));
            if (ConfirmButton("cleardone", Lang.T("Зняти всі")))
            {
                MapData.ClearFlag(MapData.FlagDone);
                ShowMessage(Lang.T("Позначки «виконано» знято."));
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.Label(Lang.T("<b>Біоми</b>  <color=#c0c0c0>вимкнені зафарбовуються сірим</color>"));
            string status = MapBiomes.StatusText;
            GUILayout.Label(status.Length > 0 ? "<color=#ffd080>" + status + "</color>" : "");
            Heightmap.Biome[] biomes = MapBiomes.Biomes;
            for (int i = 0; i < biomes.Length; i += 3)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < Mathf.Min(i + 3, biomes.Length); j++)
                {
                    Heightmap.Biome biome = biomes[j];
                    bool shown = (MapSettings.HiddenBiomes & (int)biome) == 0;
                    bool result = GUILayout.Toggle(shown, " " + MapBiomes.BiomeName(biome), GUILayout.Width(118f));
                    if (result != shown)
                    {
                        MapSettings.HiddenBiomes ^= (int)biome;
                        MapSettings.Changed();
                    }
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Lang.T("Показати всі біоми")))
            {
                MapSettings.HiddenBiomes = 0;
                MapSettings.Changed();
            }
            if (GUILayout.Button(Lang.T("Сховати всі")))
            {
                MapSettings.HiddenBiomes = MapBiomes.AllMask;
                MapSettings.Changed();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            if (ConfirmButton("resetmap", Lang.T("Скинути налаштування карти")))
            {
                MapSettings.ResetAll();
                ShowMessage(Lang.T("Налаштування карти скинуто."));
            }
        }

        private static void Toggle(ref bool value, string label)
        {
            bool result = GUILayout.Toggle(value, label);
            if (result != value)
            {
                value = result;
                MapSettings.Changed();
            }
        }

        // ---------------------------------------------------------------- texts

        private static string CoordText(Vector3 pos)
        {
            string text = $"X {pos.x:0}  Z {pos.z:0}";
            if (MapSettings.ShowHeight && WorldGenerator.instance != null)
            {
                text += $"  Y {WorldGenerator.instance.GetHeight(pos.x, pos.z):0}";
            }
            return text;
        }

        private static string DistanceText(Vector3 pos)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return "";
            }
            Vector3 delta = pos - player.transform.position;
            float distance = new Vector2(delta.x, delta.z).magnitude;
            return distance < 1000f ? Lang.F("{0:0} м", distance) : Lang.F("{0:0.0} км", distance / 1000f);
        }

        private static string ExploredText(Vector3 pos)
        {
            Minimap map = Minimap.instance;
            if (map == null || s_isExplored == null)
            {
                return "";
            }
            bool explored = (bool)s_isExplored.Invoke(map, new object[] { pos });
            return explored ? Lang.T("<color=#80ff80>відкрито на твоїй карті</color>") : Lang.T("<color=#c0c0c0>ще не відкрито на твоїй карті</color>");
        }

        private static string ShortStatus(MapPlace place)
        {
            if (place.Unique)
            {
                return place.Placed ? Lang.T("<color=#80ff80>закріплене місце</color>") : Lang.T("<color=#ffd080>одне з можливих місць</color>");
            }
            return place.Placed ? Lang.T("<color=#c0c0c0>тут уже хтось був</color>") : "";
        }

        private string StatusText(MapPlace place)
        {
            if (place.Unique)
            {
                if (place.Placed)
                {
                    return Lang.T("Закріплене місце: гра вже обрала його, інших варіантів більше немає.");
                }
                int candidates = m_typeCounts.TryGetValue(place.Type, out int[] counts) ? counts[0] : 0;
                return Lang.F("Одне з {0} можливих місць. Коли хтось із гравців уперше підійде до одного з них, гра закріпить його, а решта зникне з карти.", candidates);
            }
            return place.Placed
                ? Lang.T("Зону вже згенеровано: хтось із гравців був поруч.")
                : Lang.T("Поруч ще ніхто не був: зону не згенеровано.");
        }

        private static void SortByDistance(List<MapPlace> places)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return;
            }
            Vector3 origin = player.transform.position;
            places.Sort((a, b) => DistanceSq(a.Pos, origin).CompareTo(DistanceSq(b.Pos, origin)));
        }

        private static float DistanceSq(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x;
            float z = a.z - b.z;
            return x * x + z * z;
        }

        // ---------------------------------------------------------------- helpers

        private void EnsureStyles(GUISkin skin)
        {
            if (m_tooltipStyle != null)
            {
                return;
            }
            m_tooltipStyle = new GUIStyle(HelperSkin.Tooltip) { fontSize = Mathf.RoundToInt(14f * UiScale) };
            m_smallStyle = new GUIStyle(skin.label) { fontSize = 13, wordWrap = true, richText = true };
            m_badgeStyle = new GUIStyle(skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, wordWrap = false };
            m_captionStyle = new GUIStyle(skin.label) { alignment = TextAnchor.UpperCenter, fontStyle = FontStyle.Bold, wordWrap = false, richText = false };
            m_leftButton = new GUIStyle(skin.button) { alignment = TextAnchor.MiddleLeft, richText = true };
        }

        // Destructive actions need a second click within a few seconds.
        private static bool ConfirmButton(string id, string label)
        {
            bool waiting = s_confirmId == id && Time.unscaledTime < s_confirmUntil;
            if (!GUILayout.Button(waiting ? "<color=#ffb040>" + label + Lang.T(" — натисни ще раз</color>") : label))
            {
                return false;
            }
            if (waiting)
            {
                s_confirmId = null;
                return true;
            }
            s_confirmId = id;
            s_confirmUntil = Time.unscaledTime + 4f;
            return false;
        }

        internal static void ShowMessage(string message)
        {
            s_message = message;
            s_messageUntil = Time.unscaledTime + 6f;
        }

        private void LogOnce(Exception e)
        {
            if (!m_loggedError)
            {
                m_loggedError = true;
                Log.Error(e);
            }
        }
    }
}
