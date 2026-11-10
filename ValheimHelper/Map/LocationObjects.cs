using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimHelper.Map
{
    // What the locations of the world hold (chests, pots, spawners, nests, plants, ore, bones, scrap…) where nobody has
    // been yet. ZoneSystem.SpawnLocation seeds Unity's generator with the world seed and the zone, then every
    // RandomSpawn and RandomObject of the prefab draws in a fixed order to decide what stays; replaying that gives the
    // very set of objects the game will make. Only the location's turn stays unknown: the game draws it from whatever
    // state the generator was left in by the zone generated before, i.e. from the way players walked, so no seed map
    // can know it. The objects are shown turned as in the prefab (up to the location's size off); once somebody has
    // been there, WorldObjects shows the real ones instead.
    internal static class LocationObjects
    {
        private const float RebuildPauseSec = 20f;

        internal static int Version;

        private static int s_mapVersion = -1;
        private static int s_contentsVersion = -1;
        private static int s_session = -1;
        private static float s_builtAt = -100f;
        private static readonly Dictionary<string, WorldVegetation.Kind> s_kinds = new Dictionary<string, WorldVegetation.Kind>();

        internal static void Tick(bool mapOpen)
        {
            if (!MapData.InWorld || WorldGenerator.instance == null || ZNetScene.instance == null || WorldVegetation.Kinds.Count == 0
                || !Perf.PerfSettings.MapBackground)
            {
                return;
            }
            // the whole-world work costs frames, so it waits for the big map (and for the first look at it)
            if (!mapOpen && s_session == WorldVegetation.Session)
            {
                return;
            }
            PlaceContents.Request();
            if (!PlaceContents.Ready || MapData.Places.Count == 0)
            {
                return;
            }
            if (s_mapVersion == MapData.Version && s_contentsVersion == PlaceContents.Version && s_session == WorldVegetation.Session)
            {
                return;
            }
            if (Time.unscaledTime - s_builtAt < RebuildPauseSec)
            {
                return;
            }
            s_builtAt = Time.unscaledTime;
            if (s_session != WorldVegetation.Session)
            {
                s_kinds.Clear();
            }
            s_mapVersion = MapData.Version;
            s_contentsVersion = PlaceContents.Version;
            s_session = WorldVegetation.Session;
            try
            {
                Build();
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private static WorldVegetation.Kind KindOf(string prefab)
        {
            if (s_kinds.TryGetValue(prefab, out WorldVegetation.Kind kind))
            {
                return kind;
            }
            kind = WorldVegetation.KindOf(ZNetScene.instance.GetPrefab(prefab));
            s_kinds[prefab] = kind;
            return kind;
        }

        private static void Build()
        {
            // every prefab any location holds gets its kind first, so the real objects of visited places are known too
            foreach (PlaceContents.Layout layout in PlaceContents.Layouts())
            {
                foreach (PlaceContents.Point point in layout.Points)
                {
                    KindOf(point.Prefab);
                }
            }
            // dungeon and camp rooms are laid out only when somebody comes near (their generator's seed depends on the
            // location's turn); their objects get kinds now and are shown as found
            foreach (string prefab in PlaceContents.RoomPrefabs())
            {
                KindOf(prefab);
            }
            foreach (WorldVegetation.Kind kind in WorldVegetation.Kinds)
            {
                kind.Placed.Clear();
                kind.PlacedCount = 0;
            }
            int seed = WorldGenerator.instance.GetSeed();
            int places = 0;
            int objects = 0;
            // locations standing on the water are placed at its level (ZoneSystem.PlaceLocations)
            HashSet<string> onWater = new HashSet<string>();
            foreach (ZoneSystem.ZoneLocation location in ZoneSystem.instance.m_locations)
            {
                if (location != null && location.m_snapToWater && !string.IsNullOrEmpty(location.m_prefabName))
                {
                    onWater.Add(location.m_prefabName);
                }
            }
            UnityEngine.Random.State saved = UnityEngine.Random.state;
            try
            {
                foreach (MapPlace place in MapData.Places)
                {
                    // where somebody has been, the real objects are shown; a place kept for its resource is its own marker;
                    // a unique place (a trader) is only one of its candidates until the game picks it
                    if (place.Placed || place.Unique || place.Type.Category == MapCategory.Resources)
                    {
                        continue;
                    }
                    PlaceContents.Layout layout = PlaceContents.LayoutOf(place.Type.Prefab);
                    if (layout == null || layout.Points.Count == 0)
                    {
                        continue;
                    }
                    places++;
                    Vector3 pos = place.Pos;
                    if (onWater.Contains(place.Type.Prefab))
                    {
                        pos.y = 30f;
                    }
                    objects += Place(pos, layout, seed);
                }
            }
            finally
            {
                UnityEngine.Random.state = saved;
            }
            Version++;
            WorldVegetation.Version++;
            Log.Info($"Location objects: {objects} in {places} places nobody has been to");
        }

        // One location: the game's draws with its seed, then every object that stays.
        private static int Place(Vector3 pos, PlaceContents.Layout layout, int seed)
        {
            Vector2s zone = ZoneSystem.GetZone(pos);
            UnityEngine.Random.InitState(seed + zone.x * 4271 + zone.y * 9187);
            Heightmap.Biome biome = WorldGenerator.instance.GetBiome(pos);
            bool[] kept = new bool[layout.Spawns.Count];
            for (int i = 0; i < kept.Length; i++)
            {
                PlaceContents.Chance chance = layout.Spawns[i];
                bool spawned = UnityEngine.Random.Range(0f, 100f) <= chance.Percent;
                kept[i] = spawned && Allowed(pos + chance.Local, biome, chance.Biome, chance.NoLava, chance.MinY, chance.MaxY);
            }
            int[] chosen = new int[layout.Choices.Count];
            for (int i = 0; i < chosen.Length; i++)
            {
                PlaceContents.Choice choice = layout.Choices[i];
                int pick = Weighted(choice.Weights);
                chosen[i] = Allowed(pos + choice.Local, biome, choice.Biome, choice.NoLava, choice.MinY, choice.MaxY) ? pick : -1;
            }
            int added = 0;
            foreach (PlaceContents.Point point in layout.Points)
            {
                if (!Stays(point, kept, chosen))
                {
                    continue;
                }
                WorldVegetation.Kind kind = KindOf(point.Prefab);
                if (kind == null)
                {
                    continue;
                }
                WorldVegetation.AddTo(kind.Placed, new Vector2(pos.x + point.Local.x, pos.z + point.Local.z));
                kind.PlacedCount++;
                added++;
            }
            return added;
        }

        // RandomSpawn / RandomObject conditions; the location's biome is taken at its centre.
        private static bool Allowed(Vector3 at, Heightmap.Biome biome, int requireBiome, bool noLava, int minY, int maxY)
        {
            if (requireBiome != 0 && (requireBiome & (int)biome) == 0)
            {
                return false;
            }
            if (noLava && ZoneSystem.IsLavaPreHeightmap(at))
            {
                return false;
            }
            return at.y >= minY && at.y <= maxY;
        }

        // RandomObject.GetWeightedObject.
        private static int Weighted(float[] weights)
        {
            float total = 0f;
            foreach (float weight in weights)
            {
                total += weight;
            }
            float roll = UnityEngine.Random.Range(0f, total);
            float sum = 0f;
            for (int i = 0; i < weights.Length; i++)
            {
                sum += weights[i];
                if (roll <= sum)
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool Stays(PlaceContents.Point point, bool[] kept, int[] chosen)
        {
            foreach (int spawn in point.Spawns)
            {
                if (spawn >= kept.Length || !kept[spawn])
                {
                    return false;
                }
            }
            for (int i = 0; i < point.Choices.Length; i++)
            {
                int choice = point.Choices[i];
                if (choice >= chosen.Length || chosen[choice] < 0 || (point.Entries[i] >= 0 && chosen[choice] != point.Entries[i]))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
