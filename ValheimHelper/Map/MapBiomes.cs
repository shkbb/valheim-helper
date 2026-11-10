using System;
using System.Threading;
using UnityEngine;

namespace ValheimHelper.Map
{
    // Hides biomes on the map by greying their pixels in Minimap.m_mapTexture. Runtime only: the minimap
    // cache files on disk are not rewritten. The biome of every map pixel is computed once on a background
    // thread with the same WorldGenerator call the game uses when it draws the map.
    internal static class MapBiomes
    {
        internal static readonly Heightmap.Biome[] Biomes =
        {
            Heightmap.Biome.Meadows,
            Heightmap.Biome.BlackForest,
            Heightmap.Biome.Swamp,
            Heightmap.Biome.Mountain,
            Heightmap.Biome.Plains,
            Heightmap.Biome.Mistlands,
            Heightmap.Biome.AshLands,
            Heightmap.Biome.DeepNorth,
            Heightmap.Biome.Ocean,
        };

        internal static readonly int AllMask;

        private static readonly Color32 s_hiddenColor = new Color32(45, 45, 48, 255);

        private sealed class GridJob
        {
            public ushort[] Grid;
            public int Size;
            public int RowsDone;
            public volatile bool Failed;
        }

        private static Minimap s_map;
        private static GridJob s_job;
        private static Color32[] s_original;
        private static int s_appliedMask;

        static MapBiomes()
        {
            foreach (Heightmap.Biome biome in Biomes)
            {
                AllMask |= (int)biome;
            }
        }

        internal static string StatusText
        {
            get
            {
                GridJob job = s_job;
                if (job == null)
                {
                    return "";
                }
                if (job.Failed)
                {
                    return Lang.T("Не вдалося порахувати біоми для цієї карти.");
                }
                return job.RowsDone < job.Size ? Lang.F("Рахую біоми карти: {0}%", job.RowsDone * 100 / Math.Max(1, job.Size)) : "";
            }
        }

        internal static string BiomeName(Heightmap.Biome biome)
        {
            if (biome == Heightmap.Biome.None)
            {
                return "";
            }
            string text = Localization.instance != null ? Localization.instance.Localize("$biome_" + biome.ToString().ToLowerInvariant()) : null;
            return string.IsNullOrEmpty(text) || text.StartsWith("[") || text.StartsWith("$") ? biome.ToString() : text;
        }

        internal static void Tick()
        {
            Minimap map = Minimap.instance;
            if (!ReferenceEquals(map, s_map))
            {
                s_map = map;
                s_job = null;
                s_original = null;
                s_appliedMask = 0;
            }
            if (map == null || map.m_mapTexture == null || WorldGenerator.instance == null)
            {
                return;
            }
            int mask = MapSettings.Enabled ? MapSettings.HiddenBiomes : 0;
            if (mask == s_appliedMask)
            {
                return;
            }
            if (mask != 0)
            {
                if (s_job == null)
                {
                    StartGrid(map);
                }
                if (s_job.Failed || s_job.RowsDone < s_job.Size)
                {
                    return;
                }
            }
            Apply(map, mask);
        }

        private static void StartGrid(Minimap map)
        {
            GridJob job = new GridJob { Size = map.m_textureSize, Grid = new ushort[map.m_textureSize * map.m_textureSize] };
            s_job = job;
            float pixelSize = map.m_pixelSize;
            WorldGenerator generator = WorldGenerator.instance;
            Thread thread = new Thread(() =>
            {
                try
                {
                    int size = job.Size;
                    int half = size / 2;
                    float halfPixel = pixelSize / 2f;
                    for (int y = 0; y < size; y++)
                    {
                        float wy = (y - half) * pixelSize + halfPixel;
                        for (int x = 0; x < size; x++)
                        {
                            float wx = (x - half) * pixelSize + halfPixel;
                            job.Grid[y * size + x] = (ushort)generator.GetBiome(wx, wy);
                        }
                        Interlocked.Increment(ref job.RowsDone);
                    }
                }
                catch (Exception e)
                {
                    job.Failed = true;
                    Log.Error(e);
                }
            })
            {
                IsBackground = true,
                Name = "ValheimHelper biome grid",
            };
            thread.Start();
        }

        private static void Apply(Minimap map, int mask)
        {
            Texture2D texture = map.m_mapTexture;
            if (s_original == null)
            {
                if (mask == 0)
                {
                    s_appliedMask = 0;
                    return;
                }
                s_original = texture.GetPixels32();
            }
            Color32[] pixels = (Color32[])s_original.Clone();
            if (mask != 0)
            {
                ushort[] grid = s_job.Grid;
                int count = Math.Min(grid.Length, pixels.Length);
                for (int i = 0; i < count; i++)
                {
                    if ((grid[i] & mask) != 0)
                    {
                        pixels[i] = s_hiddenColor;
                    }
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            s_appliedMask = mask;
        }
    }
}
