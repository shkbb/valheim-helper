using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace ValheimHelper.Map
{
    // Every resource the world generator places as vegetation (ore veins, rocks, berries, mushrooms, trees, roots,
    // leviathans…), for the whole world, visited or not. The game places them zone by zone when a zone is first
    // generated, but deterministically: ZoneSystem.PlaceVegetation seeds Unity's random generator with the world seed,
    // the zone and the prefab, and checks the terrain the world generator describes. This class repeats that for every
    // zone without generating anything: the zone heightmap is rebuilt from the same WorldGenerator calls
    // HeightmapBuilder makes, and the same random numbers are drawn in the same order.
    //
    // The work runs on background threads with an exact copy of Unity's random generator (Xorshift128), checked
    // against UnityEngine.Random when it starts; if they ever disagree it runs on the main thread with the real one, a
    // few milliseconds a frame. The nearest zones come first; the finished world is cached per seed.
    //
    // Not repeated exactly: collisions with objects placed earlier in the same zone and with location buildings
    // (approximated by the locations' areas), and objects snapped onto rocks. So a few positions may be off; the kinds
    // and their places are those the game will create.
    internal static class WorldVegetation
    {
        internal sealed class Kind
        {
            public string Id;
            public string Name;
            public string Group;
            public MapIcon Icon;
            public int Priority;
            public int Count;
            // lowercase names of the kind and of what it gives, for the map search
            public string SearchText = "";
            // the item that tells the kind apart (ore of a vein, seeds of a tree) and everything it gives (shared names)
            public string MainItem;
            public readonly HashSet<string> Items = new HashSet<string>();
            public readonly List<Spec> Specs = new List<Spec>();
            // cell (CellSize × CellSize m) → positions (x, z); the map looks only at the cells in view
            public readonly Dictionary<long, Cell> Cells = new Dictionary<long, Cell>();
            // The same kind inside locations of zones nobody has been to (LocationObjects): where the location puts it,
            // turned as the location is not known yet, so up to its size off.
            public readonly Dictionary<long, Cell> Placed = new Dictionary<long, Cell>();
            public int PlacedCount;
            // The real objects of zones already generated (WorldObjects): these replace both guesses there.
            public Dictionary<long, Cell> Actual = new Dictionary<long, Cell>();
            public int ActualCount;
            // Real objects inside dungeons (their rooms hang far above the ground, under the same map point).
            public Dictionary<long, Cell> Underground = new Dictionary<long, Cell>();
            public int UndergroundCount;
            // Prefabs whose objects are of this kind (stable hashes), vegetation or not.
            public readonly HashSet<int> Prefabs = new HashSet<int>();

            // How many there are in the world, as far as the mod knows.
            public int Total => Count + PlacedCount > 0 ? Count + PlacedCount : ActualCount + UndergroundCount;
        }

        // What PlaceVegetation reads from a ZoneVegetation, copied so worker threads never touch Unity objects.
        internal sealed class Spec
        {
            public Kind Kind;
            public string Name;
            public int PrefabHash;
            public string AltBiomeParent;
            public Heightmap.Biome Biome;
            public Heightmap.BiomeArea BiomeArea;
            public float Min, Max;
            public bool ForcePlacement;
            public float ScaleMin, ScaleMax, RandTilt, ChanceToUseGroundTilt;
            public bool BlockCheck;
            public float MinAltitude, MaxAltitude, MinVegetation, MaxVegetation;
            public bool SurroundCheck;
            public float SurroundDistance;
            public int SurroundLayers;
            public float SurroundBetterThanAverage;
            public float MinOceanDepth, MaxOceanDepth, MinTilt, MaxTilt;
            public float TerrainDeltaRadius, MaxTerrainDelta, MinTerrainDelta;
            public int GroupSizeMin, GroupSizeMax;
            public float GroupRadius;
            public float MinDistanceFromCenter, MaxDistanceFromCenter;
            public bool InForest;
            public float ForestMin, ForestMax;
        }

        // A zone's positions of one kind, handed from a worker to the main thread (only it touches the kinds' tables).
        private struct Result
        {
            public Kind Kind;
            public int X;
            public int Y;
            public Vector2[] Positions;
            public int Generation;
        }

        private static readonly System.Collections.Concurrent.ConcurrentQueue<Result> s_results = new System.Collections.Concurrent.ConcurrentQueue<Result>();

        private struct Blocker
        {
            public Vector3 Center;
            public float Radius;
            public bool Clears;
        }

        internal const int ZoneSize = 64;
        private const float WorldRadius = 10500f;
        private const int CacheFormat = 3;

        internal static readonly List<Kind> Kinds = new List<Kind>();
        // The kinds the world generator places as vegetation: the worker threads look only at these (Kinds grows on
        // the main thread with what locations hold).
        private static Kind[] s_vegKinds = new Kind[0];
        private static readonly HashSet<string> s_prefabNames = new HashSet<string>();
        // Prefab (stable hash) → its kind, or null when it gives nothing worth a marker.
        private static readonly Dictionary<int, Kind> s_kindByPrefab = new Dictionary<int, Kind>();
        private static readonly Dictionary<string, Kind> s_kindByName = new Dictionary<string, Kind>();
        // How many vegetation kinds give an item: the rarest item names a kind.
        private static readonly Dictionary<string, int> s_itemSources = new Dictionary<string, int>();
        // Grows whenever Kinds changes (a new world, a kind found in a location).
        internal static int KindsVersion;

        // The kinds that give an item (its shared name).
        internal static List<Kind> KindsGiving(string item)
        {
            List<Kind> result = new List<Kind>();
            foreach (Kind kind in Kinds)
            {
                if (kind.Items.Contains(item))
                {
                    result.Add(kind);
                }
            }
            return result;
        }

        // Whether objects of this prefab are on the map already as a kind (vegetation, location contents): the prefab
        // itself or its broken form (a deposit becomes "…_frac" when hit), so nothing gets two markers.
        internal static bool Covers(string prefab)
        {
            if (string.IsNullOrEmpty(prefab) || Kinds.Count == 0)
            {
                return false;
            }
            if (s_prefabNames.Contains(prefab) || KindOfHash(prefab.GetStableHashCode()) != null)
            {
                return true;
            }
            int cut = prefab.IndexOf("_frac", StringComparison.Ordinal);
            return cut > 0 && (s_prefabNames.Contains(prefab.Substring(0, cut)) || KindOfHash(prefab.Substring(0, cut).GetStableHashCode()) != null);
        }
        internal static int Version;
        // A new world (or the same one again): the kinds are new objects.
        internal static int Session;
        internal static int ZonesDone;
        internal static int ZonesTotal;
        internal static bool Running => s_generation != 0 && ZonesDone < ZonesTotal;
        internal static string Mode = "";

        private static readonly object s_lock = new object();
        private static Vector2s[] s_order = new Vector2s[0];
        private static int s_next = -1;
        private static int s_generation;
        private static int s_seed;
        private static string s_seedName;
        private static int s_width = 64;
        private static float s_scale = 1f;
        private static float s_waterLevel = 30f;
        private static Dictionary<long, List<Blocker>> s_blockers = new Dictionary<long, List<Blocker>>();
        private static readonly List<Thread> s_threads = new List<Thread>();
        private static bool s_mainThreadMode;
        private static string s_threadError;
        private static bool s_saved;
        // Zones finished and kept (a zone's positions are added only when all of it is done, so a saved world can
        // go on from where it stopped), and the positions of zones still coming in.
        private static readonly HashSet<long> s_doneZones = new HashSet<long>();
        private static readonly Dictionary<long, List<Result>> s_pendingZones = new Dictionary<long, List<Result>>();
        private static int s_savedZones;
        private static float s_saveAt;
        private static volatile bool s_writing;
        private const float SaveEverySec = 60f;
        private static WorldGenerator s_generator;
        private static ZoneSystem s_zoneSystem;
        private static bool s_verified;
        private const int VerifyAfterZones = 3000;

        private static readonly AccessTools_Sectors s_sectorAccess = new AccessTools_Sectors();

        // m_objectsBySector, read for the accuracy check (the game's real objects of generated zones).
        private sealed class AccessTools_Sectors
        {
            private readonly HarmonyLib.AccessTools.FieldRef<ZDOMan, List<ZDO>[]> m_field =
                HarmonyLib.AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");
            private readonly HarmonyLib.AccessTools.FieldRef<ZoneSystem, HashSet<Vector2s>> m_generated =
                HarmonyLib.AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2s>>("m_generatedZones");

            internal HashSet<Vector2s> Generated(ZoneSystem zones)
            {
                return m_generated(zones);
            }

            internal List<ZDO> Objects(Vector3 point)
            {
                List<ZDO>[] sectors = ZDOMan.instance != null ? m_field(ZDOMan.instance) : null;
                uint index = ZoneSystem.GetSectorIndex(point).Sector;
                return sectors != null && index < sectors.Length ? sectors[index] : null;
            }
        }

        // How well the computed positions match the game's own objects in zones already generated near the player
        // (the host has them all). Objects cut down, mined or picked up are gone from the world, so a kind the player
        // harvests matches less; untouched kinds show the real accuracy. Written to the log.
        private static void Verify()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }
            int zonesChecked = 0;
            int actualTotal = 0;
            int matchedTotal = 0;
            Dictionary<Kind, int[]> byKind = new Dictionary<Kind, int[]>();
            Dictionary<int, Kind> kindByPrefab = new Dictionary<int, Kind>();
            foreach (Kind kind in Kinds)
            {
                foreach (Spec spec in kind.Specs)
                {
                    kindByPrefab[spec.PrefabHash] = kind;
                }
            }
            for (int i = 0; i < s_order.Length && zonesChecked < 150; i++)
            {
                Vector2s zone = s_order[i];
                if (!s_sectorAccess.Generated(s_zoneSystem).Contains(zone))
                {
                    continue;
                }
                zonesChecked++;
                Vector3 center = new Vector3(zone.x * ZoneSize, 0f, zone.y * ZoneSize);
                List<ZDO> objects = s_sectorAccess.Objects(center);
                if (objects == null)
                {
                    continue;
                }
                foreach (ZDO zdo in objects)
                {
                    if (zdo == null || !kindByPrefab.TryGetValue(zdo.GetPrefab(), out Kind kind))
                    {
                        continue;
                    }
                    Vector3 position = zdo.GetPosition();
                    if (Mathf.Abs(position.x - center.x) > ZoneSize / 2f || Mathf.Abs(position.z - center.z) > ZoneSize / 2f)
                    {
                        continue;
                    }
                    if (!byKind.TryGetValue(kind, out int[] counts))
                    {
                        counts = new int[2];
                        byKind[kind] = counts;
                    }
                    counts[0]++;
                    actualTotal++;
                    if (kind.Cells.TryGetValue(CellKey(position.x, position.z), out Cell predicted))
                    {
                        for (int p = 0; p < predicted.Count; p++)
                        {
                            if ((predicted[p] - new Vector2(position.x, position.z)).sqrMagnitude < 1.5f * 1.5f)
                            {
                                counts[1]++;
                                matchedTotal++;
                                break;
                            }
                        }
                    }
                }
            }
            List<string> lines = new List<string>();
            foreach (KeyValuePair<Kind, int[]> pair in byKind)
            {
                lines.Add($"{pair.Key.Name} {pair.Value[1]}/{pair.Value[0]}");
            }
            Log.Info($"World vegetation check: {zonesChecked} generated zones, real objects {actualTotal}, found at the computed place {matchedTotal} " +
                $"({(actualTotal > 0 ? 100f * matchedTotal / actualTotal : 0f):0.0}%). By kind: {string.Join("; ", lines.ToArray())}");
        }

        internal static long ZoneKey(int x, int y)
        {
            return ((long)x << 32) ^ (uint)y;
        }

        internal const float CellSize = ZoneSize * 8f;

        internal static long CellKey(float x, float z)
        {
            return ZoneKey(Mathf.FloorToInt(x / CellSize), Mathf.FloorToInt(z / CellSize));
        }

        internal static void AddTo(Dictionary<long, Cell> cells, Vector2 position)
        {
            long key = CellKey(position.x, position.y);
            if (!cells.TryGetValue(key, out Cell cell))
            {
                cell = new Cell(Mathf.Floor(position.x / CellSize) * CellSize, Mathf.Floor(position.y / CellSize) * CellSize);
                cells[key] = cell;
            }
            cell.Add(position);
        }

        private static void Add(Kind kind, Vector2[] positions)
        {
            foreach (Vector2 position in positions)
            {
                long key = CellKey(position.x, position.y);
                if (!kind.Cells.TryGetValue(key, out Cell cell))
                {
                    cell = new Cell(Mathf.Floor(position.x / CellSize) * CellSize, Mathf.Floor(position.y / CellSize) * CellSize);
                    kind.Cells[key] = cell;
                }
                cell.Add(position);
            }
            kind.Count += positions.Length;
        }

        // Positions of one cell, 4 bytes each: x and z as 16-bit offsets from the cell corner (steps of 8 mm).
        // The whole world holds millions of trees and stones, so this keeps the memory small.
        internal sealed class Cell
        {
            private const float Step = CellSize / 65536f;
            public readonly float X0;
            public readonly float Z0;
            private uint[] m_items = new uint[4];
            public int Count;

            public Cell(float x0, float z0)
            {
                X0 = x0;
                Z0 = z0;
            }

            public Vector2 this[int index]
            {
                get
                {
                    uint item = m_items[index];
                    return new Vector2(X0 + (item >> 16) * Step, Z0 + (item & 0xFFFF) * Step);
                }
            }

            public void Add(Vector2 position)
            {
                uint x = (uint)Mathf.Clamp(Mathf.RoundToInt((position.x - X0) / Step), 0, 65535);
                uint z = (uint)Mathf.Clamp(Mathf.RoundToInt((position.y - Z0) / Step), 0, 65535);
                AddPacked((x << 16) | z);
            }

            public void AddPacked(uint item)
            {
                if (Count == m_items.Length)
                {
                    Array.Resize(ref m_items, Count < 64 ? Count * 2 : Count + Count / 2);
                }
                m_items[Count++] = item;
            }

            public uint Packed(int index)
            {
                return m_items[index];
            }

            public uint[] CopyItems()
            {
                uint[] copy = new uint[Count];
                Array.Copy(m_items, copy, Count);
                return copy;
            }

            public void Trim()
            {
                if (m_items.Length != Count)
                {
                    Array.Resize(ref m_items, Math.Max(1, Count));
                }
            }
        }

        // ---------------------------------------------------------------- start and stop

        // Keeps what is computed so far (leaving the world or the game).
        internal static void SaveNow()
        {
            for (int i = 0; i < 300 && s_writing; i++)
            {
                // a save of a minute ago is still being written
                Thread.Sleep(10);
            }
            if (!s_saved && s_doneZones.Count > s_savedZones && Kinds.Count > 0)
            {
                SaveCache(complete: false, background: false);
            }
        }

        internal static void Tick()
        {
            if (!Perf.PerfSettings.MapBackground)
            {
                // the whole-world work is switched off in "Швидкодія": what is computed stays, nothing new is counted
                if (s_zoneSystem != null)
                {
                    SaveNow();
                    Stop();
                    s_zoneSystem = null;
                    s_generator = null;
                }
                return;
            }
            ZoneSystem zones = ZoneSystem.instance;
            WorldGenerator generator = WorldGenerator.instance;
            if ((zones == null || generator == null) && s_zoneSystem != null)
            {
                SaveNow();
                Stop();
                s_zoneSystem = null;
                s_generator = null;
                return;
            }
            if (zones == null || generator == null || ObjectDB.instance == null || ZNetScene.instance == null || Localization.instance == null
                || Player.m_localPlayer == null)
            {
                return;
            }
            if (!ReferenceEquals(zones, s_zoneSystem) || !ReferenceEquals(generator, s_generator))
            {
                SaveNow();
                Stop();
                s_zoneSystem = zones;
                s_generator = generator;
                try
                {
                    Start(zones, generator);
                }
                catch (Exception e)
                {
                    Log.Error(e);
                }
            }
            while (s_results.TryDequeue(out Result result))
            {
                if (result.Generation != s_generation)
                {
                    continue;
                }
                long zoneKey = ZoneKey(result.X, result.Y);
                if (result.Kind != null)
                {
                    if (!s_pendingZones.TryGetValue(zoneKey, out List<Result> pending))
                    {
                        pending = new List<Result>();
                        s_pendingZones[zoneKey] = pending;
                    }
                    pending.Add(result);
                    continue;
                }
                // the zone is finished: its positions go in together
                if (s_pendingZones.TryGetValue(zoneKey, out List<Result> finished))
                {
                    foreach (Result part in finished)
                    {
                        Add(part.Kind, part.Positions);
                    }
                    s_pendingZones.Remove(zoneKey);
                    Version++;
                }
                s_doneZones.Add(zoneKey);
            }
            if (!s_saved && !s_writing && s_doneZones.Count > s_savedZones && Time.unscaledTime >= s_saveAt)
            {
                // what is done so far is kept: leaving the game does not lose it
                s_saveAt = Time.unscaledTime + SaveEverySec;
                SaveCache(complete: false, background: true);
            }
            if (s_threadError != null)
            {
                Log.Error("World vegetation: " + s_threadError);
                s_threadError = null;
            }
            if (!s_verified && ZonesTotal > 0 && (ZonesDone >= Math.Min(ZonesTotal, VerifyAfterZones)) && s_results.IsEmpty)
            {
                s_verified = true;
                try
                {
                    Verify();
                }
                catch (Exception e)
                {
                    Log.Error(e);
                }
            }
            if (s_mainThreadMode && ZonesDone < ZonesTotal)
            {
                RunOnMainThread(3.0);
            }
            if (!s_saved && ZonesTotal > 0 && ZonesDone >= ZonesTotal && s_results.IsEmpty && s_pendingZones.Count == 0 && !s_writing)
            {
                s_saved = true;
                foreach (Kind kind in Kinds)
                {
                    foreach (Cell cell in kind.Cells.Values)
                    {
                        cell.Trim();
                    }
                }
                SaveCache(complete: true, background: true);
                Log.Info($"World vegetation: whole world done, {TotalCount()} objects of {Kinds.Count} kinds");
            }
        }

        private static void Stop()
        {
            s_generation++;
            foreach (Thread thread in s_threads)
            {
                thread.Join(200);
            }
            s_threads.Clear();
            lock (s_lock)
            {
                Kinds.Clear();
                s_vegKinds = new Kind[0];
                s_kindByPrefab.Clear();
                s_kindByName.Clear();
                s_itemSources.Clear();
                ZonesDone = 0;
                ZonesTotal = 0;
                Version++;
                KindsVersion++;
            }
            s_doneZones.Clear();
            s_pendingZones.Clear();
            s_savedZones = 0;
            while (s_results.TryDequeue(out _))
            {
            }
        }

        private static void Start(ZoneSystem zones, WorldGenerator generator)
        {
            s_seed = generator.GetSeed();
            s_seedName = ZNet.World != null ? ZNet.World.m_seedName : s_seed.ToString();
            Heightmap heightmap = zones.m_zonePrefab != null ? zones.m_zonePrefab.GetComponentInChildren<Heightmap>() : null;
            s_width = heightmap != null ? heightmap.m_width : 64;
            s_scale = heightmap != null ? heightmap.m_scale : 1f;
            s_waterLevel = zones.m_waterLevel;
            BuildKinds(zones);
            s_vegKinds = Kinds.ToArray();
            KindsVersion++;
            Session++;
            BuildBlockers(zones);
            BuildOrder(Player.m_localPlayer.transform.position);
            s_saved = false;
            s_verified = false;
            s_saveAt = Time.unscaledTime + SaveEverySec;
            bool complete;
            if (LoadCache(out complete))
            {
                Version++;
                if (complete)
                {
                    ZonesDone = ZonesTotal;
                    s_saved = true;
                    Mode = Lang.T("з кешу");
                    return;
                }
                // a world started before: only the zones not done yet
                List<Vector2s> left = new List<Vector2s>();
                foreach (Vector2s zone in s_order)
                {
                    if (!s_doneZones.Contains(ZoneKey(zone.x, zone.y)))
                    {
                        left.Add(zone);
                    }
                }
                s_order = left.ToArray();
                ZonesDone = ZonesTotal - left.Count;
                s_savedZones = s_doneZones.Count;
                Log.Info($"World vegetation: continuing, {ZonesDone} of {ZonesTotal} zones done before");
            }
            s_mainThreadMode = !XorRandom.MatchesUnity();
            int generation = ++s_generation;
            if (s_mainThreadMode)
            {
                Mode = Lang.T("в основному потоці");
                Log.Info("World vegetation: Unity random copy does not match, computing on the main thread");
                return;
            }
            int threads = Mathf.Clamp(Environment.ProcessorCount - 2, 1, 4);
            Mode = Lang.F("у фоні, потоків: {0}", threads);
            for (int i = 0; i < threads; i++)
            {
                Thread thread = new Thread(() => Work(generation)) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "VH vegetation" };
                s_threads.Add(thread);
                thread.Start();
            }
            Log.Info($"World vegetation: {Kinds.Count} kinds, {ZonesTotal} zones, {threads} threads");
        }

        // Zones of the world, nearest to the player first.
        private static void BuildOrder(Vector3 player)
        {
            int range = Mathf.CeilToInt(WorldRadius / ZoneSize) + 1;
            List<Vector2s> zones = new List<Vector2s>();
            for (int x = -range; x <= range; x++)
            {
                for (int y = -range; y <= range; y++)
                {
                    if (new Vector2(x * ZoneSize, y * ZoneSize).magnitude <= WorldRadius + ZoneSize)
                    {
                        zones.Add(new Vector2s(x, y));
                    }
                }
            }
            Vector2 me = new Vector2(player.x, player.z);
            zones.Sort((a, b) => (new Vector2(a.x * ZoneSize, a.y * ZoneSize) - me).sqrMagnitude.CompareTo((new Vector2(b.x * ZoneSize, b.y * ZoneSize) - me).sqrMagnitude));
            s_order = zones.ToArray();
            s_next = -1;
            ZonesTotal = s_order.Length;
            ZonesDone = 0;
        }

        // Locations of the world (the host knows all): their areas clear vegetation, their buildings block it.
        private static void BuildBlockers(ZoneSystem zones)
        {
            Dictionary<long, List<Blocker>> blockers = new Dictionary<long, List<Blocker>>();
            foreach (KeyValuePair<Vector2s, ZoneSystem.LocationInstance> pair in zones.m_locationInstances)
            {
                ZoneSystem.ZoneLocation location = pair.Value.m_location;
                if (location == null)
                {
                    continue;
                }
                long key = ZoneKey(pair.Key.x, pair.Key.y);
                if (!blockers.TryGetValue(key, out List<Blocker> list))
                {
                    list = new List<Blocker>();
                    blockers[key] = list;
                }
                list.Add(new Blocker { Center = pair.Value.m_position, Radius = location.m_exteriorRadius, Clears = location.m_clearArea });
            }
            s_blockers = blockers;
        }

        // ---------------------------------------------------------------- kinds

        private static void BuildKinds(ZoneSystem zones)
        {
            s_prefabNames.Clear();
            s_kindByPrefab.Clear();
            s_kindByName.Clear();
            s_itemSources.Clear();
            Dictionary<string, int> itemSources = s_itemSources;
            List<KeyValuePair<ZoneSystem.ZoneVegetation, List<string>>> found = new List<KeyValuePair<ZoneSystem.ZoneVegetation, List<string>>>();
            foreach (ZoneSystem.ZoneVegetation veg in zones.m_vegetation)
            {
                if (veg == null || !veg.m_enable || veg.m_prefab == null)
                {
                    continue;
                }
                List<string> yields = new List<string>();
                Yields(veg.m_prefab, yields, 0);
                if (yields.Count == 0)
                {
                    continue;
                }
                foreach (string item in new HashSet<string>(yields))
                {
                    itemSources[item] = (itemSources.TryGetValue(item, out int count) ? count : 0) + 1;
                }
                found.Add(new KeyValuePair<ZoneSystem.ZoneVegetation, List<string>>(veg, yields));
            }
            Dictionary<string, Kind> byName = s_kindByName;
            foreach (KeyValuePair<ZoneSystem.ZoneVegetation, List<string>> pair in found)
            {
                ZoneSystem.ZoneVegetation veg = pair.Key;
                // the item fewest places give identifies the kind best: seeds for a tree, the ore for a vein
                string rarest = pair.Value[0];
                foreach (string item in pair.Value)
                {
                    if (itemSources[item] < itemSources[rarest])
                    {
                        rarest = item;
                    }
                }
                string name = DisplayName(veg.m_prefab, rarest);
                string group = GroupOf(veg.m_prefab);
                if (!byName.TryGetValue(name, out Kind kind))
                {
                    kind = new Kind
                    {
                        Id = "veg:" + name,
                        Name = name,
                        Group = group,
                        Icon = IconOf(rarest),
                        MainItem = rarest,
                        Priority = group == GroupTrees ? 2 : group == GroupRocks && rarest == "$item_stone" ? 1 : 0,
                    };
                    byName[name] = kind;
                    Kinds.Add(kind);
                }
                kind.Specs.Add(Copy(veg, kind));
                kind.Items.UnionWith(pair.Value);
                s_prefabNames.Add(veg.m_prefab.name);
                AddPrefab(kind, veg.m_prefab);
                foreach (string item in pair.Value)
                {
                    string itemName = Localization.instance.Localize(item).ToLowerInvariant();
                    if (!kind.SearchText.Contains(itemName))
                    {
                        kind.SearchText += " " + itemName;
                    }
                }
                if (!kind.SearchText.Contains(kind.Name.ToLowerInvariant()))
                {
                    kind.SearchText += " " + kind.Name.ToLowerInvariant();
                }
            }
            SortKinds();
        }

        private static void SortKinds()
        {
            Kinds.Sort((a, b) =>
            {
                int byGroup = GroupOrder(a.Group).CompareTo(GroupOrder(b.Group));
                return byGroup != 0 ? byGroup : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
        }

        private static int GroupOrder(string group)
        {
            int index = Array.IndexOf(s_groupOrder, group);
            return index < 0 ? s_groupOrder.Length : index;
        }

        // A prefab and what it turns into when hit (a deposit becomes "…_frac") are one kind.
        private static void AddPrefab(Kind kind, GameObject prefab)
        {
            int hash = prefab.name.GetStableHashCode();
            kind.Prefabs.Add(hash);
            s_kindByPrefab[hash] = kind;
            Destructible destructible = prefab.GetComponent<Destructible>();
            GameObject next = destructible != null ? destructible.m_spawnWhenDestroyed : null;
            if (next != null && (next.GetComponent<MineRock5>() != null || next.GetComponent<MineRock>() != null))
            {
                int nextHash = next.name.GetStableHashCode();
                kind.Prefabs.Add(nextHash);
                s_kindByPrefab[nextHash] = kind;
            }
        }

        // The kind of any prefab (from a location, among the world's objects): the vegetation kind it is, a kind of
        // the same name, or a new one; null for what gives nothing (walls, decorations, creatures themselves).
        // Main thread only.
        internal static Kind KindOf(GameObject prefab)
        {
            if (prefab == null || Localization.instance == null || ObjectDB.instance == null)
            {
                return null;
            }
            int hash = prefab.name.GetStableHashCode();
            if (s_kindByPrefab.TryGetValue(hash, out Kind known))
            {
                return known;
            }
            Kind kind = null;
            try
            {
                kind = Classify(prefab);
            }
            catch (Exception e)
            {
                Log.Warn("World objects: " + prefab.name + ": " + e.Message);
            }
            s_kindByPrefab[hash] = kind;
            if (kind != null)
            {
                AddPrefab(kind, prefab);
                KindsVersion++;
            }
            return kind;
        }

        internal static Kind KindOfHash(int prefabHash)
        {
            return s_kindByPrefab.TryGetValue(prefabHash, out Kind kind) ? kind : null;
        }

        private static Kind Classify(GameObject prefab)
        {
            if (prefab.GetComponent<Character>() != null)
            {
                return null;
            }
            Container container = prefab.GetComponent<Container>();
            if (prefab.GetComponent<Piece>() != null && container == null && !IsStack(prefab))
            {
                return null;
            }
            List<string> yields = new List<string>();
            string name;
            MapIcon icon = null;
            string main = null;
            CreatureSpawner spawner = prefab.GetComponent<CreatureSpawner>();
            SpawnArea area = prefab.GetComponent<SpawnArea>();
            GameObject creature = spawner != null ? spawner.m_creaturePrefab
                : area != null && area.m_prefabs != null && area.m_prefabs.Count > 0 ? area.m_prefabs[0]?.m_prefab : null;
            if (creature != null && creature.GetComponent<Character>() != null)
            {
                // a spawner is its creature (with what it drops); a nest is named after what comes out of it
                string creatureName = Localization.instance.Localize(creature.GetComponent<Character>().m_name);
                if (string.IsNullOrEmpty(creatureName) || creatureName.StartsWith("[") || creatureName.StartsWith("$"))
                {
                    return null;
                }
                CharacterDrop drops = creature.GetComponent<CharacterDrop>();
                if (drops != null)
                {
                    foreach (CharacterDrop.Drop drop in drops.m_drops)
                    {
                        ItemDrop item = drop?.m_prefab != null ? drop.m_prefab.GetComponent<ItemDrop>() : null;
                        if (item == null)
                        {
                            continue;
                        }
                        yields.Add(item.m_itemData.m_shared.m_name);
                        if (item.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trophy)
                        {
                            icon = IconOf(item.m_itemData.m_shared.m_name);
                        }
                    }
                }
                Yields(prefab, yields, 0);
                name = area != null ? Lang.F("Гніздо: {0}", creatureName) : creatureName;
            }
            else
            {
                Yields(prefab, yields, 0);
                if (yields.Count == 0)
                {
                    return null;
                }
                main = yields[0];
                foreach (string item in yields)
                {
                    if (Sources(item) < Sources(main))
                    {
                        main = item;
                    }
                }
                name = container != null ? ContainerName(container) : null;
                if (string.IsNullOrEmpty(name) && IsStack(prefab))
                {
                    // "Купа каміння", not the stone it gives (that is the rocks' row)
                    name = Readable(prefab.GetComponent<Piece>().m_name);
                }
                if (string.IsNullOrEmpty(name))
                {
                    name = DisplayName(prefab, main);
                }
                icon = IconOf(main);
            }
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            if (s_kindByName.TryGetValue(name, out Kind same))
            {
                same.Items.UnionWith(yields);
                return same;
            }
            Kind kind = new Kind
            {
                Id = "veg:" + name,
                Name = name,
                Group = GroupOf(prefab),
                Icon = icon,
                MainItem = main,
            };
            kind.Items.UnionWith(yields);
            kind.SearchText = " " + name.ToLowerInvariant();
            foreach (string item in new HashSet<string>(yields))
            {
                string itemName = Localization.instance.Localize(item).ToLowerInvariant();
                if (!kind.SearchText.Contains(itemName))
                {
                    kind.SearchText += " " + itemName;
                }
            }
            s_kindByName[name] = kind;
            Kinds.Add(kind);
            SortKinds();
            KindsVersion++;
            Version++;
            return kind;
        }

        // Stacks and piles (coins, stone, wood, bones, black marble, skulls): building pieces that places hold as loot.
        internal static bool IsStack(GameObject prefab)
        {
            string name = prefab.name;
            return prefab.GetComponent<Piece>() != null
                && (name.IndexOf("stack", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("pile", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static int Sources(string item)
        {
            return s_itemSources.TryGetValue(item, out int count) ? count : 0;
        }

        private static string ContainerName(Container container)
        {
            return Readable(container.m_name);
        }

        private static string Readable(string key)
        {
            string name = string.IsNullOrEmpty(key) ? null : Localization.instance.Localize(key);
            return string.IsNullOrEmpty(name) || name.StartsWith("[") || name.StartsWith("$") ? null : name;
        }

        internal const string GroupRocks = "Руди й каміння";
        internal const string GroupPlants = "Ягоди, гриби й рослини";
        internal const string GroupTrees = "Дерева";
        internal const string GroupTreasure = "Скрині, горщики й знахідки";
        internal const string GroupCreatures = "Істоти й гнізда";
        internal const string GroupOther = "Інше";

        private static readonly string[] s_groupOrder = { GroupRocks, GroupPlants, GroupTrees, GroupTreasure, GroupCreatures, GroupOther };

        private static string GroupOf(GameObject prefab)
        {
            if (prefab.GetComponent<CreatureSpawner>() != null || prefab.GetComponent<SpawnArea>() != null)
            {
                return GroupCreatures;
            }
            if (prefab.GetComponent<Container>() != null || prefab.GetComponent<ItemDrop>() != null || prefab.GetComponent<PickableItem>() != null
                || prefab.GetComponent<LootSpawner>() != null || IsStack(prefab))
            {
                return GroupTreasure;
            }
            if (prefab.GetComponent<TreeBase>() != null)
            {
                return GroupTrees;
            }
            if (prefab.GetComponent<Pickable>() != null)
            {
                return GroupPlants;
            }
            if (prefab.GetComponent<MineRock>() != null || prefab.GetComponent<MineRock5>() != null)
            {
                return GroupRocks;
            }
            Destructible destructible = prefab.GetComponent<Destructible>();
            if (destructible != null && destructible.m_spawnWhenDestroyed != null
                && (destructible.m_spawnWhenDestroyed.GetComponent<MineRock5>() != null || destructible.m_spawnWhenDestroyed.GetComponent<MineRock>() != null))
            {
                return GroupRocks;
            }
            // pots, urns and piles that break into loot
            if (destructible != null && prefab.GetComponent<DropOnDestroyed>() != null)
            {
                return GroupTreasure;
            }
            return GroupOther;
        }

        // Items a vegetation prefab gives, following what it turns into (a tree into logs, a rock into fragments).
        private static void Yields(GameObject prefab, List<string> into, int depth)
        {
            if (prefab == null || depth > 3)
            {
                return;
            }
            ItemDrop item = prefab.GetComponent<ItemDrop>();
            if (item != null)
            {
                into.Add(item.m_itemData.m_shared.m_name);
                return;
            }
            Pickable pickable = prefab.GetComponent<Pickable>();
            if (pickable != null)
            {
                Yields(pickable.m_itemPrefab, into, depth + 1);
                Table(pickable.m_extraDrops, into, depth);
            }
            MineRock rock = prefab.GetComponent<MineRock>();
            if (rock != null)
            {
                Table(rock.m_dropItems, into, depth);
            }
            MineRock5 rock5 = prefab.GetComponent<MineRock5>();
            if (rock5 != null)
            {
                Table(rock5.m_dropItems, into, depth);
            }
            DropOnDestroyed drop = prefab.GetComponent<DropOnDestroyed>();
            if (drop != null)
            {
                Table(drop.m_dropWhenDestroyed, into, depth);
            }
            TreeBase tree = prefab.GetComponent<TreeBase>();
            if (tree != null)
            {
                Table(tree.m_dropWhenDestroyed, into, depth);
                Yields(tree.m_logPrefab, into, depth + 1);
            }
            TreeLog log = prefab.GetComponent<TreeLog>();
            if (log != null)
            {
                Table(log.m_dropWhenDestroyed, into, depth);
                Yields(log.m_subLogPrefab, into, depth + 1);
            }
            Destructible destructible = prefab.GetComponent<Destructible>();
            if (destructible != null)
            {
                Yields(destructible.m_spawnWhenDestroyed, into, depth + 1);
            }
            Container container = prefab.GetComponent<Container>();
            if (container != null)
            {
                Table(container.m_defaultItems, into, depth);
            }
            Beehive hive = prefab.GetComponent<Beehive>();
            if (hive != null && hive.m_honeyItem != null)
            {
                into.Add(hive.m_honeyItem.m_itemData.m_shared.m_name);
            }
            // a find that is one of several items (a dolmen's treasure, remains in a crypt)
            PickableItem find = prefab.GetComponent<PickableItem>();
            if (find != null)
            {
                if (find.m_itemPrefab != null)
                {
                    into.Add(find.m_itemPrefab.m_itemData.m_shared.m_name);
                }
                foreach (PickableItem.RandomItem random in find.m_randomItemPrefabs)
                {
                    if (random.m_itemPrefab != null)
                    {
                        into.Add(random.m_itemPrefab.m_itemData.m_shared.m_name);
                    }
                }
            }
            LootSpawner loot = prefab.GetComponent<LootSpawner>();
            if (loot != null)
            {
                Table(loot.m_items, into, depth);
            }
            // a stack or pile standing in a place gives what it is made of
            Piece piece = prefab.GetComponent<Piece>();
            if (piece != null && IsStack(prefab))
            {
                foreach (Piece.Requirement requirement in piece.m_resources)
                {
                    if (requirement?.m_resItem != null)
                    {
                        into.Add(requirement.m_resItem.m_itemData.m_shared.m_name);
                    }
                }
            }
            // an ancient root gives sap through the sap extractor built on it
            if (prefab.GetComponent<ResourceRoot>() != null)
            {
                GameObject sap = ObjectDB.instance.GetItemPrefab("Sap");
                ItemDrop sapItem = sap != null ? sap.GetComponent<ItemDrop>() : null;
                if (sapItem != null)
                {
                    into.Add(sapItem.m_itemData.m_shared.m_name);
                }
            }
        }

        private static void Table(DropTable table, List<string> into, int depth)
        {
            if (table?.m_drops == null)
            {
                return;
            }
            foreach (DropTable.DropData drop in table.m_drops)
            {
                Yields(drop.m_item, into, depth + 1);
            }
        }

        // The name the game itself shows: the pickable's or vein's name, a tree's hover text; else the item's name.
        private static string DisplayName(GameObject prefab, string rarestItem)
        {
            string key = null;
            Pickable pickable = prefab.GetComponent<Pickable>();
            if (pickable != null)
            {
                key = !string.IsNullOrEmpty(pickable.m_overrideName) ? pickable.m_overrideName
                    : pickable.m_itemPrefab != null && pickable.m_itemPrefab.GetComponent<ItemDrop>() != null
                        ? pickable.m_itemPrefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_name : null;
            }
            if (key == null)
            {
                MineRock rock = prefab.GetComponent<MineRock>();
                MineRock5 rock5 = prefab.GetComponent<MineRock5>();
                key = rock != null && !string.IsNullOrEmpty(rock.m_name) ? rock.m_name : rock5 != null && !string.IsNullOrEmpty(rock5.m_name) ? rock5.m_name : null;
            }
            if (key == null)
            {
                ResourceRoot root = prefab.GetComponent<ResourceRoot>();
                key = root != null && !string.IsNullOrEmpty(root.m_name) ? root.m_name : null;
            }
            if (key == null)
            {
                HoverText hover = prefab.GetComponent<HoverText>();
                key = hover != null && !string.IsNullOrEmpty(hover.m_text) ? hover.m_text : null;
            }
            if (key == null)
            {
                Destructible destructible = prefab.GetComponent<Destructible>();
                GameObject next = destructible != null ? destructible.m_spawnWhenDestroyed : null;
                if (next != null)
                {
                    MineRock5 rock5 = next.GetComponent<MineRock5>();
                    MineRock rock = next.GetComponent<MineRock>();
                    key = rock5 != null && !string.IsNullOrEmpty(rock5.m_name) ? rock5.m_name : rock != null && !string.IsNullOrEmpty(rock.m_name) ? rock.m_name : null;
                }
            }
            string name = key != null ? Localization.instance.Localize(key) : null;
            if (string.IsNullOrEmpty(name) || name.StartsWith("[") || name.StartsWith("$"))
            {
                name = Localization.instance.Localize(rarestItem);
            }
            return name;
        }

        private static MapIcon IconOf(string sharedName)
        {
            foreach (GameObject prefab in ObjectDB.instance.m_items)
            {
                ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (drop == null || drop.m_itemData.m_shared.m_name != sharedName)
                {
                    continue;
                }
                Sprite sprite = drop.m_itemData.GetIcon();
                if (sprite == null || sprite.texture == null)
                {
                    return null;
                }
                Rect rect = sprite.textureRect;
                return new MapIcon
                {
                    Texture = sprite.texture,
                    Uv = new Rect(rect.x / sprite.texture.width, rect.y / sprite.texture.height, rect.width / sprite.texture.width, rect.height / sprite.texture.height),
                };
            }
            return null;
        }

        private static Spec Copy(ZoneSystem.ZoneVegetation veg, Kind kind)
        {
            return new Spec
            {
                Kind = kind,
                Name = veg.m_name,
                PrefabHash = veg.m_prefab.name.GetStableHashCode(),
                AltBiomeParent = veg.AltBiomeParent,
                Biome = veg.m_biome,
                BiomeArea = veg.m_biomeArea,
                Min = veg.m_min,
                Max = veg.m_max,
                ForcePlacement = veg.m_forcePlacement,
                ScaleMin = veg.m_scaleMin,
                ScaleMax = veg.m_scaleMax,
                RandTilt = veg.m_randTilt,
                ChanceToUseGroundTilt = veg.m_chanceToUseGroundTilt,
                BlockCheck = veg.m_blockCheck,
                MinAltitude = veg.m_minAltitude,
                MaxAltitude = veg.m_maxAltitude,
                MinVegetation = veg.m_minVegetation,
                MaxVegetation = veg.m_maxVegetation,
                SurroundCheck = veg.m_surroundCheckVegetation,
                SurroundDistance = veg.m_surroundCheckDistance,
                SurroundLayers = veg.m_surroundCheckLayers,
                SurroundBetterThanAverage = veg.m_surroundBetterThanAverage,
                MinOceanDepth = veg.m_minOceanDepth,
                MaxOceanDepth = veg.m_maxOceanDepth,
                MinTilt = veg.m_minTilt,
                MaxTilt = veg.m_maxTilt,
                TerrainDeltaRadius = veg.m_terrainDeltaRadius,
                MaxTerrainDelta = veg.m_maxTerrainDelta,
                MinTerrainDelta = veg.m_minTerrainDelta,
                GroupSizeMin = veg.m_groupSizeMin,
                GroupSizeMax = veg.m_groupSizeMax,
                GroupRadius = veg.m_groupRadius,
                MinDistanceFromCenter = veg.m_minDistanceFromCenter,
                MaxDistanceFromCenter = veg.m_maxDistanceFromCenter,
                InForest = veg.m_inForest,
                ForestMin = veg.m_forestTresholdMin,
                ForestMax = veg.m_forestTresholdMax,
            };
        }

        // ---------------------------------------------------------------- running

        private static void Work(int generation)
        {
            ZoneTerrain terrain = new ZoneTerrain();
            XorRandom random = new XorRandom();
            List<Vector2> positions = new List<Vector2>();
            try
            {
                while (generation == s_generation)
                {
                    int index = Interlocked.Increment(ref s_next);
                    if (index >= s_order.Length)
                    {
                        return;
                    }
                    Zone(s_order[index], terrain, random, positions, generation);
                    Interlocked.Increment(ref ZonesDone);
                }
            }
            catch (Exception e)
            {
                s_threadError = e.ToString();
            }
        }

        // The fallback: the real UnityEngine.Random, a few milliseconds a frame.
        private static ZoneTerrain s_mainTerrain;

        private static void RunOnMainThread(double budgetMs)
        {
            if (s_mainTerrain == null)
            {
                s_mainTerrain = new ZoneTerrain();
            }
            UnityRandom random = new UnityRandom();
            UnityEngine.Random.State saved = UnityEngine.Random.state;
            List<Vector2> positions = new List<Vector2>();
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                while (watch.Elapsed.TotalMilliseconds < budgetMs)
                {
                    int index = ++s_next;
                    if (index >= s_order.Length)
                    {
                        return;
                    }
                    Zone(s_order[index], s_mainTerrain, random, positions, s_generation);
                    ZonesDone++;
                }
            }
            finally
            {
                UnityEngine.Random.state = saved;
            }
        }

        private static void Zone(Vector2s zone, ZoneTerrain terrain, IRandom random, List<Vector2> positions, int generation)
        {
            Vector3 center = new Vector3(zone.x * ZoneSize, 0f, zone.y * ZoneSize);
            terrain.Reset(s_generator, center, s_width, s_scale, s_waterLevel);
            long key = ZoneKey(zone.x, zone.y);
            s_blockers.TryGetValue(key, out List<Blocker> blockers);
            foreach (Kind kind in s_vegKinds)
            {
                positions.Clear();
                foreach (Spec spec in kind.Specs)
                {
                    if (terrain.HaveBiome(spec.Biome))
                    {
                        Place(zone, center, spec, terrain, random, blockers, positions);
                    }
                }
                if (positions.Count == 0)
                {
                    continue;
                }
                if (generation != s_generation)
                {
                    return;
                }
                s_results.Enqueue(new Result { Kind = kind, X = zone.x, Y = zone.y, Positions = positions.ToArray(), Generation = generation });
            }
            if (generation == s_generation)
            {
                s_results.Enqueue(new Result { Kind = null, X = zone.x, Y = zone.y, Generation = generation });
            }
        }

        // ZoneSystem.PlaceVegetation for one kind of vegetation in one zone, without creating anything.
        private static void Place(Vector2s zone, Vector3 center, Spec veg, ZoneTerrain terrain, IRandom random, List<Blocker> blockers, List<Vector2> into)
        {
            random.InitState(s_seed + zone.x * 4271 + zone.y * 9187 + veg.PrefabHash);
            int wanted = 1;
            if (veg.Max < 1f)
            {
                if (random.Value() > veg.Max)
                {
                    return;
                }
            }
            else
            {
                wanted = random.Range((int)veg.Min, (int)veg.Max + 1);
            }
            float maxTiltCos = Mathf.Cos(MathF.PI / 180f * veg.MaxTilt);
            float minTiltCos = Mathf.Cos(MathF.PI / 180f * veg.MinTilt);
            float half = 32f - veg.GroupRadius;
            List<float> surround = veg.SurroundCheck ? new List<float>() : null;
            int attempts = veg.ForcePlacement ? wanted * 50 : wanted;
            int placedGroups = 0;
            for (int i = 0; i < attempts; i++)
            {
                Vector3 vector = new Vector3(random.Range(center.x - half, center.x + half), 0f, random.Range(center.z - half, center.z + half));
                if (AltBiomeBlocks(veg, vector))
                {
                    continue;
                }
                int group = random.Range(veg.GroupSizeMin, veg.GroupSizeMax + 1);
                bool any = false;
                for (int g = 0; g < group; g++)
                {
                    Vector3 p = g == 0 ? vector : RandomPointInRadius(random, vector, veg.GroupRadius);
                    random.Range(0, 360);
                    random.Range(veg.ScaleMin, veg.ScaleMax);
                    random.Range(0f - veg.RandTilt, veg.RandTilt);
                    random.Range(0f - veg.RandTilt, veg.RandTilt);
                    if (veg.BlockCheck && Blocked(blockers, p))
                    {
                        continue;
                    }
                    terrain.Ground(ref p, out Vector3 normal, out Heightmap.Biome biome, out Heightmap.BiomeArea biomeArea);
                    if ((veg.Biome & biome) == 0 || (veg.BiomeArea & biomeArea) == 0)
                    {
                        continue;
                    }
                    float altitude = p.y - 30f;
                    if (altitude < veg.MinAltitude || altitude > veg.MaxAltitude)
                    {
                        continue;
                    }
                    if (!veg.MinVegetation.Equals(veg.MaxVegetation))
                    {
                        float mask = terrain.VegetationMask(p);
                        if (mask > veg.MaxVegetation || mask < veg.MinVegetation)
                        {
                            continue;
                        }
                    }
                    if (!veg.MinOceanDepth.Equals(veg.MaxOceanDepth))
                    {
                        float depth = terrain.OceanDepth(p);
                        if (depth < veg.MinOceanDepth || depth > veg.MaxOceanDepth)
                        {
                            continue;
                        }
                    }
                    if (normal.y < maxTiltCos || normal.y > minTiltCos)
                    {
                        continue;
                    }
                    if (veg.TerrainDeltaRadius > 0f)
                    {
                        float delta = TerrainDelta(random, terrain, p, veg.TerrainDeltaRadius);
                        if (delta > veg.MaxTerrainDelta || delta < veg.MinTerrainDelta)
                        {
                            continue;
                        }
                    }
                    if (veg.MinDistanceFromCenter > 0f || veg.MaxDistanceFromCenter > 0f)
                    {
                        float fromCenter = Utils.LengthXZ(p);
                        if ((veg.MinDistanceFromCenter > 0f && fromCenter < veg.MinDistanceFromCenter) || (veg.MaxDistanceFromCenter > 0f && fromCenter > veg.MaxDistanceFromCenter))
                        {
                            continue;
                        }
                    }
                    if (veg.InForest)
                    {
                        float forest = WorldGenerator.GetForestFactor(p);
                        if (forest < veg.ForestMin || forest > veg.ForestMax)
                        {
                            continue;
                        }
                    }
                    if (surround != null)
                    {
                        float score = 0f;
                        for (int layer = 0; layer < veg.SurroundLayers; layer++)
                        {
                            float distance = (float)(layer + 1) / (float)veg.SurroundLayers * veg.SurroundDistance;
                            for (int step = 0; step < 6; step++)
                            {
                                float f = (float)step / 6f * MathF.PI * 2f;
                                float mask = terrain.VegetationMask(p + new Vector3(Mathf.Sin(f) * distance, 0f, Mathf.Cos(f) * distance));
                                float weight = (1f - distance) / (veg.SurroundDistance * 2f);
                                score += mask * weight;
                            }
                        }
                        surround.Add(score);
                        if (surround.Count < 10)
                        {
                            continue;
                        }
                        float max = float.MinValue;
                        float sum = 0f;
                        foreach (float value in surround)
                        {
                            max = Mathf.Max(max, value);
                            sum += value;
                        }
                        float average = sum / surround.Count;
                        if (score < average + (max - average) * veg.SurroundBetterThanAverage)
                        {
                            continue;
                        }
                    }
                    if (InsideClearArea(blockers, p))
                    {
                        continue;
                    }
                    if (veg.ChanceToUseGroundTilt > 0f)
                    {
                        random.Value();
                    }
                    into.Add(new Vector2(p.x, p.z));
                    any = true;
                }
                if (any)
                {
                    placedGroups++;
                }
                if (placedGroups >= wanted)
                {
                    break;
                }
            }
        }

        private static bool AltBiomeBlocks(Spec veg, Vector3 point)
        {
            BiomeSector sector = s_generator.GetBiomeSector(point);
            List<AltBiome> alt = sector.AltBiomes;
            if (veg.AltBiomeParent != null)
            {
                bool found = false;
                if (alt != null)
                {
                    foreach (AltBiome biome in alt)
                    {
                        if (biome.m_name == veg.AltBiomeParent)
                        {
                            found = true;
                            break;
                        }
                    }
                }
                if (!found)
                {
                    return true;
                }
            }
            if (alt != null)
            {
                foreach (AltBiome biome in alt)
                {
                    if (biome.m_blockVegetationNames != null && biome.m_blockVegetationNames.Contains(veg.Name))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static Vector3 RandomPointInRadius(IRandom random, Vector3 center, float radius)
        {
            float f = random.Value() * MathF.PI * 2f;
            float distance = random.Range(0f, radius);
            return center + new Vector3(Mathf.Sin(f) * distance, 0f, Mathf.Cos(f) * distance);
        }

        private static float TerrainDelta(IRandom random, ZoneTerrain terrain, Vector3 center, float radius)
        {
            float highest = -999999f;
            float lowest = 999999f;
            for (int i = 0; i < 10; i++)
            {
                Vector2 offset = random.InsideUnitCircle() * radius;
                float height = terrain.HeightAt(center.x + offset.x, center.z + offset.y);
                lowest = Mathf.Min(lowest, height);
                highest = Mathf.Max(highest, height);
            }
            return highest - lowest;
        }

        private static bool Blocked(List<Blocker> blockers, Vector3 point)
        {
            if (blockers == null)
            {
                return false;
            }
            foreach (Blocker blocker in blockers)
            {
                float dx = point.x - blocker.Center.x;
                float dz = point.z - blocker.Center.z;
                if (dx * dx + dz * dz < blocker.Radius * blocker.Radius * 0.6f)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool InsideClearArea(List<Blocker> blockers, Vector3 point)
        {
            if (blockers == null)
            {
                return false;
            }
            foreach (Blocker area in blockers)
            {
                if (area.Clears && point.x > area.Center.x - area.Radius && point.x < area.Center.x + area.Radius
                    && point.z > area.Center.z - area.Radius && point.z < area.Center.z + area.Radius)
                {
                    return true;
                }
            }
            return false;
        }

        internal static int TotalCount()
        {
            int total = 0;
            foreach (Kind kind in Kinds)
            {
                total += kind.Count;
            }
            return total;
        }

        // ---------------------------------------------------------------- cache

        private static string CachePath => Path.Combine(Path.Combine(Loader.Dir ?? ".", "cache"), "vegetation_" + Safe(s_seedName) + ".bin");

        private static string Safe(string text)
        {
            char[] chars = (text ?? "world").ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]))
                {
                    chars[i] = '_';
                }
            }
            return new string(chars);
        }

        private static string Stamp()
        {
            try
            {
                FileInfo file = new FileInfo(typeof(ZoneSystem).Assembly.Location);
                return $"{CacheFormat}|{s_seed}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            }
            catch
            {
                return $"{CacheFormat}|{s_seed}";
            }
        }

        private sealed class CellCopy
        {
            public long Key;
            public float X0;
            public float Z0;
            public uint[] Items;
        }

        private static void SaveCache(bool complete, bool background)
        {
            // a copy of what is done (quick, the positions are packed), written away from the main thread
            string path = CachePath;
            string stamp = Stamp();
            long[] done = new long[s_doneZones.Count];
            s_doneZones.CopyTo(done);
            List<KeyValuePair<string, List<CellCopy>>> kinds = new List<KeyValuePair<string, List<CellCopy>>>();
            foreach (Kind kind in Kinds)
            {
                List<CellCopy> cells = new List<CellCopy>(kind.Cells.Count);
                foreach (KeyValuePair<long, Cell> cell in kind.Cells)
                {
                    cells.Add(new CellCopy { Key = cell.Key, X0 = cell.Value.X0, Z0 = cell.Value.Z0, Items = cell.Value.CopyItems() });
                }
                kinds.Add(new KeyValuePair<string, List<CellCopy>>(kind.Id, cells));
            }
            s_savedZones = done.Length;
            s_writing = true;
            void Write()
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    string temp = path + ".tmp";
                    using (BinaryWriter writer = new BinaryWriter(File.Create(temp)))
                    {
                        writer.Write(stamp);
                        writer.Write(complete);
                        writer.Write(done.Length);
                        foreach (long zone in done)
                        {
                            writer.Write(zone);
                        }
                        writer.Write(kinds.Count);
                        foreach (KeyValuePair<string, List<CellCopy>> kind in kinds)
                        {
                            writer.Write(kind.Key);
                            writer.Write(kind.Value.Count);
                            foreach (CellCopy cell in kind.Value)
                            {
                                writer.Write(cell.Key);
                                writer.Write(cell.X0);
                                writer.Write(cell.Z0);
                                writer.Write(cell.Items.Length);
                                foreach (uint item in cell.Items)
                                {
                                    writer.Write(item);
                                }
                            }
                        }
                    }
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    File.Move(temp, path);
                }
                catch (Exception e)
                {
                    Log.Error(e);
                }
                finally
                {
                    s_writing = false;
                }
            }
            if (background)
            {
                new Thread(Write) { IsBackground = false, Name = "VH vegetation save" }.Start();
            }
            else
            {
                Write();
            }
        }

        // False when there is no cache for this world (or it is from another game version); complete tells whether the
        // whole world is in it or only the zones done before (s_doneZones).
        private static bool LoadCache(out bool complete)
        {
            complete = false;
            try
            {
                if (!File.Exists(CachePath))
                {
                    return false;
                }
                using (BinaryReader reader = new BinaryReader(File.OpenRead(CachePath)))
                {
                    if (reader.ReadString() != Stamp())
                    {
                        return false;
                    }
                    complete = reader.ReadBoolean();
                    int doneCount = reader.ReadInt32();
                    for (int i = 0; i < doneCount; i++)
                    {
                        s_doneZones.Add(reader.ReadInt64());
                    }
                    Dictionary<string, Kind> byId = new Dictionary<string, Kind>();
                    foreach (Kind kind in Kinds)
                    {
                        byId[kind.Id] = kind;
                    }
                    int kinds = reader.ReadInt32();
                    for (int k = 0; k < kinds; k++)
                    {
                        string id = reader.ReadString();
                        byId.TryGetValue(id, out Kind kind);
                        int cells = reader.ReadInt32();
                        for (int c = 0; c < cells; c++)
                        {
                            long key = reader.ReadInt64();
                            Cell cell = new Cell(reader.ReadSingle(), reader.ReadSingle());
                            int count = reader.ReadInt32();
                            for (int i = 0; i < count; i++)
                            {
                                cell.AddPacked(reader.ReadUInt32());
                            }
                            if (kind != null)
                            {
                                if (complete)
                                {
                                    cell.Trim();
                                }
                                kind.Cells[key] = cell;
                                kind.Count += count;
                            }
                        }
                    }
                }
                Log.Info($"World vegetation: loaded from cache, {TotalCount()} objects" + (complete ? "" : $", {s_doneZones.Count} zones done"));
                return true;
            }
            catch (Exception e)
            {
                Log.Error(e);
                foreach (Kind kind in Kinds)
                {
                    kind.Cells.Clear();
                    kind.Count = 0;
                }
                s_doneZones.Clear();
                complete = false;
                return false;
            }
        }

        // ---------------------------------------------------------------- terrain of one zone

        // The heightmap HeightmapBuilder would build for a zone, computed only where it is looked at: corner biomes,
        // vertex heights and the vegetation mask, and what Heightmap and a terrain raycast report from them.
        internal sealed class ZoneTerrain
        {
            private WorldGenerator m_generator;
            private Vector3 m_center;
            private int m_width;
            private float m_scale;
            private float m_water;
            private float m_originX;
            private float m_originZ;
            private readonly Heightmap.Biome[] m_sector = new Heightmap.Biome[4];
            private readonly Heightmap.Biome[] m_corner = new Heightmap.Biome[4];
            private bool m_sameBiome;
            private float[] m_heights = new float[0];
            private float[] m_mask = new float[0];
            private bool[] m_known = new bool[0];
            private readonly float[] m_depth = new float[4];
            private bool m_depthKnown;

            internal void Reset(WorldGenerator generator, Vector3 center, int width, float scale, float water)
            {
                m_generator = generator;
                m_center = center;
                m_width = width;
                m_scale = scale;
                m_water = water;
                m_originX = center.x - width * scale * 0.5f;
                m_originZ = center.z - width * scale * 0.5f;
                int count = (width + 1) * (width + 1);
                if (m_heights.Length != count)
                {
                    m_heights = new float[count];
                    m_mask = new float[count];
                    m_known = new bool[count];
                }
                else
                {
                    Array.Clear(m_known, 0, count);
                }
                float far = (float)((double)width * (double)scale);
                m_sector[0] = generator.GetBiomeSector(m_originX, m_originZ).Biome;
                m_sector[1] = generator.GetBiomeSector((float)((double)m_originX + far), m_originZ).Biome;
                m_sector[2] = generator.GetBiomeSector(m_originX, (float)((double)m_originZ + far)).Biome;
                m_sector[3] = generator.GetBiomeSector((float)((double)m_originX + far), (float)((double)m_originZ + far)).Biome;
                m_corner[0] = generator.GetBiome(m_originX, m_originZ);
                m_corner[1] = generator.GetBiome((float)((double)m_originX + far), m_originZ);
                m_corner[2] = generator.GetBiome(m_originX, (float)((double)m_originZ + far));
                m_corner[3] = generator.GetBiome((float)((double)m_originX + far), (float)((double)m_originZ + far));
                m_sameBiome = m_corner[1] == m_corner[0] && m_corner[2] == m_corner[0] && m_corner[3] == m_corner[0];
                m_depthKnown = false;
            }

            internal bool HaveBiome(Heightmap.Biome biome)
            {
                return (m_sector[0] & biome) != 0 || (m_sector[1] & biome) != 0 || (m_sector[2] & biome) != 0 || (m_sector[3] & biome) != 0;
            }

            // HeightmapBuilder.Build for one vertex (column l, row k).
            private int Vertex(int l, int k)
            {
                int n = m_width + 1;
                int index = k * n + l;
                if (m_known[index])
                {
                    return index;
                }
                float wx = (float)((double)m_originX + (double)l * (double)m_scale);
                float wy = (float)((double)m_originZ + (double)k * (double)m_scale);
                Color mask;
                float height;
                if (m_sameBiome)
                {
                    height = m_generator.GetBiomeHeight(m_corner[0], wx, wy, out mask);
                }
                else
                {
                    float t = SmoothStep((float)((double)k / (double)m_width));
                    float t2 = SmoothStep((float)((double)l / (double)m_width));
                    float h0 = m_generator.GetBiomeHeight(m_corner[0], wx, wy, out Color m0);
                    float h1 = m_generator.GetBiomeHeight(m_corner[1], wx, wy, out Color m1);
                    float h2 = m_generator.GetBiomeHeight(m_corner[2], wx, wy, out Color m2);
                    float h3 = m_generator.GetBiomeHeight(m_corner[3], wx, wy, out Color m3);
                    float a = Lerp(h0, h1, t2);
                    float b = Lerp(h2, h3, t2);
                    height = Lerp(a, b, t);
                    Color a2 = Color.Lerp(m0, m1, t2);
                    Color b2 = Color.Lerp(m2, m3, t2);
                    mask = Color.Lerp(a2, b2, t);
                }
                m_heights[index] = height;
                m_mask[index] = mask.a;
                m_known[index] = true;
                return index;
            }

            private static float SmoothStep(float x)
            {
                float num = (float)Math.Max(0.0, Math.Min(1.0, (double)x));
                return (float)((double)num * (double)num * (3.0 - 2.0 * (double)num));
            }

            // DUtils.Lerp: t outside 0..1 gives the end.
            private static float Lerp(float a, float b, float t)
            {
                if (t <= 0f)
                {
                    return a;
                }
                if (t >= 1f)
                {
                    return b;
                }
                return (float)((double)a + ((double)b - (double)a) * (double)t);
            }

            private bool Inside(float x, float z)
            {
                return x >= m_originX && z >= m_originZ && x < m_originX + m_width * m_scale && z < m_originZ + m_width * m_scale;
            }

            // What a raycast down on the zone's collision mesh finds: the height and the normal of the triangle.
            internal void Ground(ref Vector3 p, out Vector3 normal, out Heightmap.Biome biome, out Heightmap.BiomeArea area)
            {
                if (!Inside(p.x, p.z))
                {
                    // Beyond this zone (a group spilling over its edge): the world generator's own terrain there.
                    p.y = m_generator.GetHeight(p.x, p.z);
                    float step = 0.5f;
                    float hx = m_generator.GetHeight(p.x + step, p.z) - m_generator.GetHeight(p.x - step, p.z);
                    float hz = m_generator.GetHeight(p.x, p.z + step) - m_generator.GetHeight(p.x, p.z - step);
                    normal = new Vector3(-hx, 2f * step, -hz).normalized;
                    biome = m_generator.GetBiome(p);
                    area = m_generator.GetBiomeArea(p);
                    return;
                }
                p.y = Triangle(p.x, p.z, out normal);
                biome = BiomeAt(p);
                area = IsEdge() ? Heightmap.BiomeArea.Edge : Heightmap.BiomeArea.Median;
            }

            internal float HeightAt(float x, float z)
            {
                return Inside(x, z) ? Triangle(x, z, out _) : m_generator.GetHeight(x, z);
            }

            private float Triangle(float x, float z, out Vector3 normal)
            {
                float fx = (x - m_originX) / m_scale;
                float fz = (z - m_originZ) / m_scale;
                int l = Mathf.Clamp(Mathf.FloorToInt(fx), 0, m_width - 1);
                int k = Mathf.Clamp(Mathf.FloorToInt(fz), 0, m_width - 1);
                float u = fx - l;
                float v = fz - k;
                float h00 = m_heights[Vertex(l, k)];
                float h10 = m_heights[Vertex(l + 1, k)];
                float h01 = m_heights[Vertex(l, k + 1)];
                float h11 = m_heights[Vertex(l + 1, k + 1)];
                // Triangles as Heightmap builds the collision mesh: (k,l) (k+1,l) (k,l+1) and (k,l+1) (k+1,l) (k+1,l+1).
                Vector3 a, b, c;
                float height;
                if (u + v <= 1f)
                {
                    height = h00 + (h10 - h00) * u + (h01 - h00) * v;
                    a = new Vector3(0f, h00, 0f);
                    b = new Vector3(0f, h01, m_scale);
                    c = new Vector3(m_scale, h10, 0f);
                }
                else
                {
                    height = h11 + (h01 - h11) * (1f - u) + (h10 - h11) * (1f - v);
                    a = new Vector3(m_scale, h10, 0f);
                    b = new Vector3(0f, h01, m_scale);
                    c = new Vector3(m_scale, h11, m_scale);
                }
                normal = Vector3.Cross(b - a, c - a).normalized;
                if (normal.y < 0f)
                {
                    normal = -normal;
                }
                return height;
            }

            private bool IsEdge()
            {
                return !(m_sector[0] == m_sector[1] && m_sector[0] == m_sector[2] && m_sector[0] == m_sector[3]);
            }

            // Heightmap.GetBiome: the corner biome weighted by closeness.
            private Heightmap.Biome BiomeAt(Vector3 point)
            {
                if (!IsEdge())
                {
                    return m_sector[0];
                }
                float size = m_width * m_scale;
                float x = (point.x - m_center.x) / size + 0.5f;
                float y = (point.z - m_center.z) / size + 0.5f;
                float[] weights = { Distance(x, y, 0f, 0f), Distance(x, y, 1f, 0f), Distance(x, y, 0f, 1f), Distance(x, y, 1f, 1f) };
                Heightmap.Biome best = Heightmap.Biome.None;
                float bestWeight = -99999f;
                // the game sums per biome and takes the first largest in biome order
                List<Heightmap.Biome> biomes = new List<Heightmap.Biome>(4);
                foreach (Heightmap.Biome biome in m_sector)
                {
                    if (!biomes.Contains(biome))
                    {
                        biomes.Add(biome);
                    }
                }
                biomes.Sort((p, q) => ((int)p).CompareTo((int)q));
                foreach (Heightmap.Biome biome in biomes)
                {
                    float sum = 0f;
                    for (int i = 0; i < 4; i++)
                    {
                        if (m_sector[i] == biome)
                        {
                            sum += weights[i];
                        }
                    }
                    if (sum > bestWeight)
                    {
                        best = biome;
                        bestWeight = sum;
                    }
                }
                return best;
            }

            private static float Distance(float x, float y, float rx, float ry)
            {
                float dx = (float)((double)x - (double)rx);
                float dy = (float)((double)y - (double)ry);
                float d = Mathf.Sqrt((float)((double)dx * (double)dx + (double)dy * (double)dy));
                float w = (float)(1.4140000343322754 - (double)d);
                return (float)((double)w * (double)w * (double)w);
            }

            // Heightmap.GetVegetationMask: the paint mask's alpha at the vertex under the point.
            internal float VegetationMask(Vector3 worldPos)
            {
                worldPos.x -= 0.5f;
                worldPos.z -= 0.5f;
                int half = (m_width + 1) / 2;
                int x = Mathf.FloorToInt((worldPos.x - m_center.x) / m_scale + 0.5f + half);
                int y = Mathf.FloorToInt((worldPos.z - m_center.z) / m_scale + 0.5f + half);
                x = Mathf.Clamp(x, 0, m_width);
                y = Mathf.Clamp(y, 0, m_width);
                return m_mask[Vertex(x, y)];
            }

            // Heightmap.GetOceanDepth: water depth at the corners, blended.
            internal float OceanDepth(Vector3 worldPos)
            {
                if (!m_depthKnown)
                {
                    m_depth[0] = Mathf.Max(0f, m_water - m_heights[Vertex(0, m_width)]);
                    m_depth[1] = Mathf.Max(0f, m_water - m_heights[Vertex(m_width, m_width)]);
                    m_depth[2] = Mathf.Max(0f, m_water - m_heights[Vertex(m_width, 0)]);
                    m_depth[3] = Mathf.Max(0f, m_water - m_heights[Vertex(0, 0)]);
                    m_depthKnown = true;
                }
                int half = m_width / 2;
                int x = Mathf.FloorToInt((worldPos.x - m_center.x) / m_scale + 0.5f) + half;
                int y = Mathf.FloorToInt((worldPos.z - m_center.z) / m_scale + 0.5f) + half;
                float t = (float)((double)x / (double)(float)m_width);
                float t2 = (float)y / (float)m_width;
                float a = Lerp(m_depth[3], m_depth[2], t);
                float b = Lerp(m_depth[0], m_depth[1], t);
                return Lerp(a, b, t2);
            }
        }

        // ---------------------------------------------------------------- random numbers

        internal interface IRandom
        {
            void InitState(int seed);
            float Value();
            float Range(float min, float max);
            int Range(int min, int max);
            Vector2 InsideUnitCircle();
        }

        private sealed class UnityRandom : IRandom
        {
            public void InitState(int seed) => UnityEngine.Random.InitState(seed);
            public float Value() => UnityEngine.Random.value;
            public float Range(float min, float max) => UnityEngine.Random.Range(min, max);
            public int Range(int min, int max) => UnityEngine.Random.Range(min, max);
            public Vector2 InsideUnitCircle() => UnityEngine.Random.insideUnitCircle;
        }

        // Unity's generator (Xorshift128, seeded like MT19937), so worker threads draw exactly what the game drew.
        // Which of the known ways Unity maps its numbers to floats, ranges and circle points is found by comparing
        // with UnityEngine.Random once (MatchesUnity); nothing runs on threads unless everything matches.
        internal sealed class XorRandom : IRandom
        {
            private uint m_s0, m_s1, m_s2, m_s3;
            internal static int FloatVariant = -1;
            internal static int IntVariant = -1;
            internal static int CircleVariant = -1;

            public void InitState(int seed)
            {
                m_s0 = (uint)seed;
                m_s1 = m_s0 * 1812433253u + 1u;
                m_s2 = m_s1 * 1812433253u + 1u;
                m_s3 = m_s2 * 1812433253u + 1u;
            }

            private uint Next()
            {
                uint t = m_s0 ^ (m_s0 << 11);
                m_s0 = m_s1;
                m_s1 = m_s2;
                m_s2 = m_s3;
                m_s3 = m_s3 ^ (m_s3 >> 19) ^ t ^ (t >> 8);
                return m_s3;
            }

            public float Value()
            {
                return (Next() & 0x7FFFFFu) * (1f / 8388607f);
            }

            public float Range(float min, float max)
            {
                float t = Value();
                switch (FloatVariant)
                {
                    case 1: return t * max + (1f - t) * min;
                    case 2: return t * min + (1f - t) * max;
                    default: return min + (max - min) * t;
                }
            }

            public int Range(int min, int max)
            {
                if (min == max)
                {
                    return min;
                }
                uint next = Next();
                if (IntVariant == 1)
                {
                    return min < max ? min + (int)(((ulong)next * (ulong)(uint)(max - min)) >> 32) : min - (int)(((ulong)next * (ulong)(uint)(min - max)) >> 32);
                }
                return min < max ? min + (int)(next % (uint)(max - min)) : min - (int)(next % (uint)(min - max));
            }

            public Vector2 InsideUnitCircle()
            {
                switch (CircleVariant)
                {
                    case 1:
                    {
                        float angle = Range(0f, MathF.PI * 2f);
                        float radius = Mathf.Sqrt(Range(0f, 1f));
                        return new Vector2(radius * Mathf.Sin(angle), radius * Mathf.Cos(angle));
                    }
                    case 3:
                    {
                        float radius = Mathf.Sqrt(Range(0f, 1f));
                        float angle = Range(0f, MathF.PI * 2f);
                        return new Vector2(radius * Mathf.Cos(angle), radius * Mathf.Sin(angle));
                    }
                    case 4:
                    {
                        float radius = Mathf.Sqrt(Range(0f, 1f));
                        float angle = Range(0f, MathF.PI * 2f);
                        return new Vector2(radius * Mathf.Sin(angle), radius * Mathf.Cos(angle));
                    }
                    case 2:
                    {
                        float x, y;
                        do
                        {
                            x = Range(-1f, 1f);
                            y = Range(-1f, 1f);
                        }
                        while (x * x + y * y > 1f);
                        return new Vector2(x, y);
                    }
                    default:
                    {
                        float angle = Range(0f, MathF.PI * 2f);
                        float radius = Mathf.Sqrt(Range(0f, 1f));
                        return new Vector2(radius * Mathf.Cos(angle), radius * Mathf.Sin(angle));
                    }
                }
            }

            // Compares with UnityEngine.Random on the main thread and picks the matching variants.
            internal static bool MatchesUnity()
            {
                UnityEngine.Random.State saved = UnityEngine.Random.state;
                try
                {
                    XorRandom mine = new XorRandom();
                    int[] seeds = { 12345, -987654, 42, 2000000000, 7 };
                    foreach (int seed in seeds)
                    {
                        UnityEngine.Random.InitState(seed);
                        mine.InitState(seed);
                        for (int i = 0; i < 20; i++)
                        {
                            if (UnityEngine.Random.value != mine.Value())
                            {
                                Log.Info("World vegetation: Random.value differs");
                                return false;
                            }
                        }
                    }
                    FloatVariant = Pick(3, variant =>
                    {
                        FloatVariant = variant;
                        return Same(seed => UnityEngine.Random.Range(-3.5f, 17.25f), (mine, seed) => mine.Range(-3.5f, 17.25f));
                    });
                    IntVariant = Pick(2, variant =>
                    {
                        IntVariant = variant;
                        return Same(seed => UnityEngine.Random.Range(-5, 1000) + UnityEngine.Random.Range(3, 4) * 10000, (mine, seed) => mine.Range(-5, 1000) + mine.Range(3, 4) * 10000);
                    });
                    CircleVariant = Pick(5, variant =>
                    {
                        CircleVariant = variant;
                        return Same(seed =>
                        {
                            Vector2 v = UnityEngine.Random.insideUnitCircle;
                            return v.x * 1000f + v.y + UnityEngine.Random.value;
                        }, (mine, seed) =>
                        {
                            Vector2 v = mine.InsideUnitCircle();
                            return v.x * 1000f + v.y + mine.Value();
                        });
                    });
                    Log.Info($"World vegetation: random variants float {FloatVariant}, int {IntVariant}, circle {CircleVariant}");
                    return FloatVariant >= 0 && IntVariant >= 0 && CircleVariant >= 0;
                }
                catch (Exception e)
                {
                    Log.Error(e);
                    return false;
                }
                finally
                {
                    UnityEngine.Random.state = saved;
                }
            }

            private static int Pick(int variants, Func<int, bool> test)
            {
                for (int variant = 0; variant < variants; variant++)
                {
                    if (test(variant))
                    {
                        return variant;
                    }
                }
                return -1;
            }

            private static bool Same(Func<int, float> unity, Func<XorRandom, int, float> copy)
            {
                XorRandom mine = new XorRandom();
                foreach (int seed in new[] { 1, 99991, -31337, 123456789, 555 })
                {
                    UnityEngine.Random.InitState(seed);
                    mine.InitState(seed);
                    for (int i = 0; i < 30; i++)
                    {
                        float a = unity(seed);
                        float b = copy(mine, seed);
                        if (Math.Abs(a - b) > 1e-4f * Math.Max(1f, Math.Abs(a)))
                        {
                            return false;
                        }
                    }
                }
                return true;
            }
        }
    }
}
