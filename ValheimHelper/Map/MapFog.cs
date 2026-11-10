using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper.Map
{
    // "Show unexplored": paints every unexplored fog pixel as "explored by others" (green channel), the
    // same look as map data copied from a cartography table. Only the texture changes; Minimap.m_explored
    // and m_exploredOthers — what the game saves to the character and shares at the table — stay untouched.
    internal static class MapFog
    {
        private static readonly AccessTools.FieldRef<Minimap, Texture2D> s_fogTexture = AccessTools.FieldRefAccess<Minimap, Texture2D>("m_fogTexture");
        private static readonly AccessTools.FieldRef<Minimap, BitArray> s_explored = AccessTools.FieldRefAccess<Minimap, BitArray>("m_explored");
        private static readonly AccessTools.FieldRef<Minimap, BitArray> s_exploredOthers = AccessTools.FieldRefAccess<Minimap, BitArray>("m_exploredOthers");

        private static Minimap s_map;
        private static bool s_applied;
        private static bool s_gameReset;

        // Called after the game rebuilt the fog texture from its own arrays (load, reset).
        internal static void OnGameReset()
        {
            s_gameReset = true;
        }

        internal static void Tick()
        {
            Minimap map = Minimap.instance;
            if (!ReferenceEquals(map, s_map))
            {
                s_map = map;
                s_applied = false;
                s_gameReset = false;
            }
            if (map == null)
            {
                return;
            }
            if (s_gameReset)
            {
                s_gameReset = false;
                s_applied = false;
            }
            bool wanted = MapSettings.Enabled && MapSettings.ShowUnexplored;
            if (wanted != s_applied)
            {
                Apply(map, wanted);
            }
        }

        private static void Apply(Minimap map, bool showUnexplored)
        {
            Texture2D fog = s_fogTexture(map);
            BitArray explored = s_explored(map);
            BitArray others = s_exploredOthers(map);
            if (fog == null || explored == null || others == null)
            {
                return;
            }
            Color32[] pixels = fog.GetPixels32();
            if (pixels.Length != explored.Length || pixels.Length != others.Length)
            {
                return;
            }
            for (int i = 0; i < pixels.Length; i++)
            {
                byte red = explored[i] ? (byte)0 : (byte)255;
                byte green = showUnexplored || others[i] ? (byte)0 : (byte)255;
                pixels[i] = new Color32(red, green, 255, 255);
            }
            fog.SetPixels32(pixels);
            fog.Apply();
            s_applied = showUnexplored;
        }
    }
}
