using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace ValheimHelper.Map
{
    // Map preferences, kept between game launches in ValheimHelper/map_settings.txt.
    internal static class MapSettings
    {
        // Diameter of the round marker at the default map zoom.
        internal const float DefaultIconSize = 32f;

        internal static bool Enabled = true;
        internal static bool PanelCollapsed;
        // Width of the map panel, dragged by its left edge.
        internal static float PanelWidth = 390f;
        internal static bool ShowUnexplored;
        internal static bool UseDots;
        internal static float IconSize = DefaultIconSize;
        internal const float DefaultMinimapIconSize = 18f;
        internal const float MinMinimapIconSize = 10f;
        internal const float MaxMinimapIconSize = 32f;
        // The same markers on the small minimap in the corner of the screen.
        internal static bool ShowOnMinimap = true;
        // Tamed animals as circles on the large map (TamedAnimals).
        internal static bool ShowTamed = true;
        // Waypoints: new ones shared with friends, and shown in the 3D world.
        internal static bool WaypointsShared = true;
        internal static bool WaypointsIn3D = true;
        // The game's own pins (fire, house, hammer, dot, portal, bosses found…) are seen by friends with the mod.
        internal static bool ShareGamePins;
        internal static float MinimapIconSize = DefaultMinimapIconSize;
        // Map drawing: which layer the pen draws into, colour, width and what is shown.
        internal static bool SketchShared;
        internal static int SketchColor;
        internal static int WaypointColor;
        internal static float SketchWidth = 4f;
        internal static bool SketchShowMine = true;
        internal static bool SketchShowShared = true;
        internal static bool SketchOnMinimap = true;
        internal static bool ShowHeight = true;
        internal static bool HideDone;
        internal static int HiddenBiomes;
        // Increments on every change, so views can cache their filtered lists.
        internal static int Version;

        // null means "default layers"
        private static HashSet<string> s_shownTypes;

        // Kinds of world vegetation shown (WorldVegetation.Kind.Id); none until the resources layer is switched on.
        internal static readonly HashSet<string> VegShown = new HashSet<string>();

        internal static void SetVegShown(IEnumerable<string> ids, bool shown)
        {
            foreach (string id in ids)
            {
                if (shown)
                {
                    VegShown.Add(id);
                }
                else
                {
                    VegShown.Remove(id);
                }
            }
            Changed();
        }
        private static bool s_dirty;
        private static float s_saveAt;

        private static string FilePath => Path.Combine(Loader.Dir ?? ".", "map_settings.txt");

        internal static bool IsTypeShown(MapType type)
        {
            return s_shownTypes != null ? s_shownTypes.Contains(type.Prefab) : MapCatalog.DefaultVisible(type.Category);
        }

        internal static void SetTypesShown(IEnumerable<MapType> types, bool shown)
        {
            if (s_shownTypes == null)
            {
                s_shownTypes = new HashSet<string>();
                foreach (MapType type in new List<MapType>(MapCatalog.Known))
                {
                    if (MapCatalog.DefaultVisible(type.Category))
                    {
                        s_shownTypes.Add(type.Prefab);
                    }
                }
            }
            foreach (MapType type in types)
            {
                if (shown)
                {
                    s_shownTypes.Add(type.Prefab);
                }
                else
                {
                    s_shownTypes.Remove(type.Prefab);
                }
            }
            Changed();
        }

        internal static void ResetTypes()
        {
            s_shownTypes = null;
            Changed();
        }

        internal static void SetEnabled(bool value)
        {
            Enabled = value;
            Changed();
        }

        internal static void SetShowUnexplored(bool value)
        {
            ShowUnexplored = value;
            Changed();
        }

        internal static void ResetAll()
        {
            Enabled = true;
            PanelCollapsed = false;
            PanelWidth = 390f;
            ShowUnexplored = false;
            UseDots = false;
            IconSize = DefaultIconSize;
            ShowOnMinimap = true;
            MinimapIconSize = DefaultMinimapIconSize;
            SketchShared = false;
            SketchColor = 0;
            WaypointColor = 0;
            SketchWidth = 4f;
            SketchShowMine = true;
            SketchShowShared = true;
            SketchOnMinimap = true;
            ShowTamed = true;
            WaypointsShared = true;
            WaypointsIn3D = true;
            ShareGamePins = false;
            ShowHeight = true;
            HideDone = false;
            HiddenBiomes = 0;
            s_shownTypes = null;
            Changed();
        }

        internal static void Changed()
        {
            Version++;
            s_dirty = true;
            s_saveAt = Time.unscaledTime + 1f;
        }

        internal static void SaveIfDirty()
        {
            if (s_dirty && Time.unscaledTime >= s_saveAt)
            {
                Save();
            }
        }

        internal static void Flush()
        {
            if (s_dirty)
            {
                Save();
            }
        }

        internal static void Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return;
                }
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        continue;
                    }
                    string key = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).Trim();
                    switch (key)
                    {
                        case "enabled": Enabled = value == "1"; break;
                        case "collapsed": PanelCollapsed = value == "1"; break;
                        case "panelwidth":
                            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float panelWidth))
                            {
                                PanelWidth = Mathf.Clamp(panelWidth, 320f, 2000f);
                            }
                            break;
                        case "unexplored": ShowUnexplored = value == "1"; break;
                        case "dots": UseDots = value == "1"; break;
                        case "height": ShowHeight = value == "1"; break;
                        case "hidedone": HideDone = value == "1"; break;
                        case "iconsize":
                            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float size))
                            {
                                IconSize = Mathf.Clamp(size, 14f, 48f);
                            }
                            break;
                        case "minimap": ShowOnMinimap = value == "1"; break;
                        case "tamed": ShowTamed = value == "1"; break;
                        case "wpshared": WaypointsShared = value == "1"; break;
                        case "wp3d": WaypointsIn3D = value == "1"; break;
                        case "sharepins": ShareGamePins = value == "1"; break;
                        case "sketchshared": SketchShared = value == "1"; break;
                        case "sketchcolor": int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out SketchColor); break;
                        case "waypointcolor": int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out WaypointColor); break;
                        case "sketchwidth":
                            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float sketchWidth))
                            {
                                SketchWidth = Mathf.Clamp(sketchWidth, SketchRules.MinWidth, SketchRules.MaxWidth);
                            }
                            break;
                        case "sketchmine": SketchShowMine = value == "1"; break;
                        case "sketchsharedshow": SketchShowShared = value == "1"; break;
                        case "sketchminimap": SketchOnMinimap = value == "1"; break;
                        case "minimapiconsize":
                            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float miniSize))
                            {
                                MinimapIconSize = Mathf.Clamp(miniSize, MinMinimapIconSize, MaxMinimapIconSize);
                            }
                            break;
                        case "hiddenbiomes":
                            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out HiddenBiomes);
                            break;
                        case "types":
                            s_shownTypes = new HashSet<string>(value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                            break;
                        case "veg":
                            VegShown.Clear();
                            VegShown.UnionWith(value.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
                            break;
                        case "style":
                            s_roundMarkersLoaded = value == RoundMarkersStyle;
                            break;
                    }
                }
                // Markers became round badges with the icon inside; a size saved before that is scaled up once.
                if (!s_roundMarkersLoaded)
                {
                    IconSize = Mathf.Clamp(Mathf.Round(IconSize * DefaultIconSize / 26f), 14f, 48f);
                    Changed();
                }
                Version++;
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private const string RoundMarkersStyle = "2";
        private static bool s_roundMarkersLoaded;

        private static void Save()
        {
            s_dirty = false;
            try
            {
                List<string> lines = new List<string>
                {
                    "style=" + RoundMarkersStyle,
                    "enabled=" + Bit(Enabled),
                    "collapsed=" + Bit(PanelCollapsed),
                    "panelwidth=" + Mathf.Round(PanelWidth).ToString(CultureInfo.InvariantCulture),
                    "unexplored=" + Bit(ShowUnexplored),
                    "dots=" + Bit(UseDots),
                    "height=" + Bit(ShowHeight),
                    "hidedone=" + Bit(HideDone),
                    "iconsize=" + IconSize.ToString(CultureInfo.InvariantCulture),
                    "minimap=" + Bit(ShowOnMinimap),
                    "sketchshared=" + Bit(SketchShared),
                    "sketchcolor=" + SketchColor.ToString(CultureInfo.InvariantCulture),
                    "waypointcolor=" + WaypointColor.ToString(CultureInfo.InvariantCulture),
                    "sketchwidth=" + SketchWidth.ToString(CultureInfo.InvariantCulture),
                    "sketchmine=" + Bit(SketchShowMine),
                    "sketchsharedshow=" + Bit(SketchShowShared),
                    "sketchminimap=" + Bit(SketchOnMinimap),
                    "tamed=" + Bit(ShowTamed),
                    "wpshared=" + Bit(WaypointsShared),
                    "wp3d=" + Bit(WaypointsIn3D),
                    "sharepins=" + Bit(ShareGamePins),
                    "minimapiconsize=" + MinimapIconSize.ToString(CultureInfo.InvariantCulture),
                    "hiddenbiomes=" + HiddenBiomes.ToString(CultureInfo.InvariantCulture),
                };
                if (s_shownTypes != null)
                {
                    lines.Add("types=" + string.Join(",", new List<string>(s_shownTypes).ToArray()));
                }
                if (VegShown.Count > 0)
                {
                    lines.Add("veg=" + string.Join("|", new List<string>(VegShown).ToArray()));
                }
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private static string Bit(bool value)
        {
            return value ? "1" : "0";
        }
    }
}
