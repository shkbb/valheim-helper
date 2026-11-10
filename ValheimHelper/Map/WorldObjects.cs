using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper.Map
{
    // The real objects of every kind on the map in zones already generated (somebody has been near). The game keeps
    // each as a ZDO and the host has all ZDOs of the world, so these places are exact, and whatever was cut, mined,
    // picked or broken is gone; in those zones they replace what the mod computes (vegetation, location contents).
    // The host goes through its objects a slice per frame; a client asks the host for the kinds it shows, by prefab,
    // so the names in each player's language do not matter.
    internal static class WorldObjects
    {
        private const string RpcAsk = "ValheimHelper_ObjectsAsk";
        private const string RpcData = "ValheimHelper_Objects";
        private const int ProtocolVersion = 2;
        // Dungeon rooms stand this high above the world.
        private const float UndergroundY = 1000f;
        // Sector slots of ZDOMan looked at per frame (the array has 512 × 512, most of them empty).
        private const int SectorsPerFrame = 8000;
        private const float RescanSec = 12f;
        private const float AskEverySec = 60f;
        private const float AskAgainSec = 10f;

        internal static int Version;

        private static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> s_sectors = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");
        private static readonly AccessTools.FieldRef<ZoneSystem, HashSet<Vector2s>> s_generatedZones =
            AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2s>>("m_generatedZones");

        // Zones generated: there only the real objects are shown.
        private static HashSet<long> s_generated = new HashSet<long>();

        // host: prefab → kind, the pass going on and the last complete one (packed x/z per prefab)
        private static Dictionary<int, WorldVegetation.Kind> s_table;
        private static int s_tableKinds = -1;
        private static int s_wildSession = -1;
        private static Dictionary<int, List<uint>> s_pass;
        private static Dictionary<int, List<uint>> s_passInside;
        private static Dictionary<int, List<uint>> s_found = new Dictionary<int, List<uint>>();
        private static Dictionary<int, List<uint>> s_foundInside = new Dictionary<int, List<uint>>();
        private static int s_cursor;
        private static float s_lastPassAt = -1000f;
        private static bool s_scanned;
        private static ZDOMan s_zdoMan;

        // client
        private static float s_askAt = -1000f;
        private static float s_askPause = AskEverySec;
        private static string s_askedFor;

        internal static bool IsGenerated(float x, float z)
        {
            if (s_generated.Count == 0)
            {
                return false;
            }
            Vector2s zone = ZoneSystem.GetZone(new Vector3(x, 0f, z));
            return s_generated.Contains(WorldVegetation.ZoneKey(zone.x, zone.y));
        }

        internal static void Register(ZRoutedRpc rpc)
        {
            rpc.Register<ZPackage>(RpcAsk, OnAsk);
            rpc.Register<ZPackage>(RpcData, OnData);
        }

        internal static void Reset()
        {
            s_generated = new HashSet<long>();
            s_table = null;
            s_tableKinds = -1;
            s_wildSession = -1;
            s_pass = null;
            s_passInside = null;
            s_found = new Dictionary<int, List<uint>>();
            s_foundInside = new Dictionary<int, List<uint>>();
            s_cursor = 0;
            s_lastPassAt = -1000f;
            s_scanned = false;
            s_zdoMan = null;
            s_askAt = -1000f;
            s_askedFor = null;
            Version++;
        }

        // ---------------------------------------------------------------- host

        // A pass runs once after loading (again when new kinds appear), then every 30 s while the large map is open.
        internal static void TickHost(bool mapOpen)
        {
            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null || ZNetScene.instance == null || ZoneSystem.instance == null || WorldVegetation.Kinds.Count == 0
                || !Perf.PerfSettings.MapBackground)
            {
                return;
            }
            if (!ReferenceEquals(zdoMan, s_zdoMan))
            {
                Reset();
                s_zdoMan = zdoMan;
            }
            if (s_table == null || s_tableKinds != WorldVegetation.KindsVersion)
            {
                BuildTable();
                s_pass = null;
                s_scanned = false;
            }
            if (s_pass == null)
            {
                if (s_scanned && !(mapOpen && Time.unscaledTime - s_lastPassAt > RescanSec))
                {
                    return;
                }
                s_pass = new Dictionary<int, List<uint>>();
                s_passInside = new Dictionary<int, List<uint>>();
                s_cursor = 0;
            }
            List<ZDO>[] sectors = s_sectors(zdoMan);
            if (sectors == null)
            {
                return;
            }
            int end = Mathf.Min(sectors.Length, s_cursor + SectorsPerFrame);
            for (; s_cursor < end; s_cursor++)
            {
                List<ZDO> list = sectors[s_cursor];
                if (list == null)
                {
                    continue;
                }
                for (int i = 0; i < list.Count; i++)
                {
                    ZDO zdo = list[i];
                    if (zdo == null)
                    {
                        continue;
                    }
                    int prefab = zdo.GetPrefab();
                    if (!s_table.ContainsKey(prefab))
                    {
                        continue;
                    }
                    Vector3 pos = zdo.GetPosition();
                    Dictionary<int, List<uint>> into = pos.y > UndergroundY ? s_passInside : s_pass;
                    if (!into.TryGetValue(prefab, out List<uint> positions))
                    {
                        positions = new List<uint>();
                        into[prefab] = positions;
                    }
                    positions.Add(Pack(pos.x, pos.z));
                }
            }
            if (s_cursor < sectors.Length)
            {
                return;
            }
            s_found = s_pass;
            s_foundInside = s_passInside;
            s_pass = null;
            s_passInside = null;
            s_lastPassAt = Time.unscaledTime;
            s_scanned = true;
            HashSet<long> generated = new HashSet<long>();
            foreach (Vector2s zone in s_generatedZones(ZoneSystem.instance))
            {
                generated.Add(WorldVegetation.ZoneKey(zone.x, zone.y));
            }
            s_generated = generated;
            int count = Apply(s_found, s_foundInside);
            Log.Info($"World objects: {count} real objects of {s_found.Count + s_foundInside.Count} prefabs in {generated.Count} generated zones");
        }

        // Which prefabs to look for: everything of the map's kinds, and wild things of the few kinds that are neither
        // vegetation nor in a location (none known today; the rule catches new ones).
        private static void BuildTable()
        {
            if (s_wildSession != WorldVegetation.Session)
            {
                s_wildSession = WorldVegetation.Session;
                foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
                {
                    if (prefab != null && Wild(prefab))
                    {
                        WorldVegetation.KindOf(prefab);
                    }
                }
            }
            Dictionary<int, WorldVegetation.Kind> table = new Dictionary<int, WorldVegetation.Kind>();
            foreach (WorldVegetation.Kind kind in WorldVegetation.Kinds)
            {
                foreach (int prefab in kind.Prefabs)
                {
                    table[prefab] = kind;
                }
            }
            s_table = table;
            s_tableKinds = WorldVegetation.KindsVersion;
        }

        private static bool Wild(GameObject prefab)
        {
            if (prefab.GetComponent<Piece>() != null)
            {
                return false;
            }
            if (prefab.GetComponent<ResourceRoot>() != null)
            {
                return true;
            }
            // the lava leviathan of the Ashlands is a location already
            if (prefab.GetComponent<Leviathan>() != null)
            {
                return prefab.name.IndexOf("Lava", StringComparison.OrdinalIgnoreCase) < 0;
            }
            // only wild crops: the same plants grown by players would fill the map around every farm
            return prefab.GetComponent<Pickable>() != null && prefab.name.IndexOf("Wild", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void OnAsk(long sender, ZPackage package)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZRoutedRpc.instance == null)
            {
                return;
            }
            if (package.ReadInt() != ProtocolVersion)
            {
                return;
            }
            int count = Mathf.Clamp(package.ReadInt(), 0, 100000);
            List<int> wanted = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                wanted.Add(package.ReadInt());
            }
            ZPackage inner = new ZPackage();
            inner.Write(ProtocolVersion);
            inner.Write(s_scanned);
            inner.Write(s_generated.Count);
            foreach (long zone in s_generated)
            {
                inner.Write((short)(int)(zone >> 32));
                inner.Write((short)(int)zone);
            }
            int objects = WriteFound(inner, wanted, s_found) + WriteFound(inner, wanted, s_foundInside);
            ZPackage reply = new ZPackage();
            reply.WriteCompressed(inner);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcData, reply);
            Log.Info($"World objects sent to peer {sender}: {objects}");
        }

        private static int WriteFound(ZPackage package, List<int> wanted, Dictionary<int, List<uint>> found)
        {
            List<int> present = wanted.FindAll(hash => found.ContainsKey(hash));
            package.Write(present.Count);
            int objects = 0;
            foreach (int hash in present)
            {
                List<uint> positions = found[hash];
                package.Write(hash);
                package.Write(positions.Count);
                foreach (uint packed in positions)
                {
                    package.Write(packed);
                }
                objects += positions.Count;
            }
            return objects;
        }

        private static Dictionary<int, List<uint>> ReadFound(ZPackage package)
        {
            int prefabs = package.ReadInt();
            Dictionary<int, List<uint>> found = new Dictionary<int, List<uint>>();
            for (int p = 0; p < prefabs; p++)
            {
                int hash = package.ReadInt();
                int count = package.ReadInt();
                List<uint> positions = new List<uint>(count);
                for (int i = 0; i < count; i++)
                {
                    positions.Add(package.ReadUInt());
                }
                found[hash] = positions;
            }
            return found;
        }

        // ---------------------------------------------------------------- client

        // Asks the host for the real objects of the kinds shown: when the large map opens with other kinds, and
        // every minute while it stays open.
        internal static void TickClient(bool mapOpen)
        {
            if (!mapOpen || ZRoutedRpc.instance == null || MapData.Source != MapData.SourceKind.Client)
            {
                return;
            }
            List<int> hashes = new List<int>();
            foreach (WorldVegetation.Kind kind in WorldVegetation.Kinds)
            {
                if (MapSettings.VegShown.Contains(kind.Id))
                {
                    hashes.AddRange(kind.Prefabs);
                }
            }
            if (hashes.Count == 0)
            {
                return;
            }
            hashes.Sort();
            string key = string.Join(",", hashes.ConvertAll(hash => hash.ToString()).ToArray());
            if (key == s_askedFor && Time.unscaledTime - s_askAt < s_askPause)
            {
                return;
            }
            s_askedFor = key;
            s_askAt = Time.unscaledTime;
            s_askPause = AskEverySec;
            ZPackage package = new ZPackage();
            package.Write(ProtocolVersion);
            package.Write(hashes.Count);
            foreach (int hash in hashes)
            {
                package.Write(hash);
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcAsk, package);
        }

        private static void OnData(long sender, ZPackage package)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer())
            {
                return;
            }
            try
            {
                ZPackage inner = package.ReadCompressedPackage();
                if (inner.ReadInt() != ProtocolVersion)
                {
                    return;
                }
                bool complete = inner.ReadBool();
                int zones = inner.ReadInt();
                HashSet<long> generated = new HashSet<long>();
                for (int i = 0; i < zones; i++)
                {
                    int x = inner.ReadShort();
                    int y = inner.ReadShort();
                    generated.Add(WorldVegetation.ZoneKey(x, y));
                }
                Dictionary<int, List<uint>> found = ReadFound(inner);
                Dictionary<int, List<uint>> inside = ReadFound(inner);
                s_generated = generated;
                int objects = Apply(found, inside);
                if (!complete)
                {
                    // the host has not gone through its objects yet
                    s_askPause = AskAgainSec;
                }
                Log.Info($"World objects received from host: {objects} in {generated.Count} generated zones");
            }
            catch (Exception e)
            {
                Log.Warn("World objects from host: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- both

        // The found objects become the kinds' real positions, on the ground and inside dungeons.
        private static int Apply(Dictionary<int, List<uint>> found, Dictionary<int, List<uint>> inside)
        {
            Dictionary<WorldVegetation.Kind, int> counts = new Dictionary<WorldVegetation.Kind, int>();
            Dictionary<WorldVegetation.Kind, int> insideCounts = new Dictionary<WorldVegetation.Kind, int>();
            Dictionary<WorldVegetation.Kind, Dictionary<long, WorldVegetation.Cell>> cells = Collect(found, counts);
            Dictionary<WorldVegetation.Kind, Dictionary<long, WorldVegetation.Cell>> insideCells = Collect(inside, insideCounts);
            int total = 0;
            foreach (WorldVegetation.Kind kind in WorldVegetation.Kinds)
            {
                kind.Actual = cells.TryGetValue(kind, out Dictionary<long, WorldVegetation.Cell> mine) ? mine : new Dictionary<long, WorldVegetation.Cell>();
                kind.ActualCount = counts.TryGetValue(kind, out int count) ? count : 0;
                kind.Underground = insideCells.TryGetValue(kind, out Dictionary<long, WorldVegetation.Cell> below) ? below : new Dictionary<long, WorldVegetation.Cell>();
                kind.UndergroundCount = insideCounts.TryGetValue(kind, out int belowCount) ? belowCount : 0;
                total += kind.ActualCount + kind.UndergroundCount;
            }
            Version++;
            WorldVegetation.Version++;
            return total;
        }

        private static Dictionary<WorldVegetation.Kind, Dictionary<long, WorldVegetation.Cell>> Collect(Dictionary<int, List<uint>> found,
            Dictionary<WorldVegetation.Kind, int> counts)
        {
            Dictionary<WorldVegetation.Kind, Dictionary<long, WorldVegetation.Cell>> cells = new Dictionary<WorldVegetation.Kind, Dictionary<long, WorldVegetation.Cell>>();
            foreach (KeyValuePair<int, List<uint>> pair in found)
            {
                WorldVegetation.Kind kind = WorldVegetation.KindOfHash(pair.Key);
                if (kind == null && ZNetScene.instance != null)
                {
                    kind = WorldVegetation.KindOf(ZNetScene.instance.GetPrefab(pair.Key));
                }
                if (kind == null)
                {
                    continue;
                }
                if (!cells.TryGetValue(kind, out Dictionary<long, WorldVegetation.Cell> into))
                {
                    into = new Dictionary<long, WorldVegetation.Cell>();
                    cells[kind] = into;
                    counts[kind] = 0;
                }
                foreach (uint packed in pair.Value)
                {
                    WorldVegetation.AddTo(into, Unpack(packed));
                }
                counts[kind] += pair.Value.Count;
            }
            return cells;
        }

        // x and z to the metre (the world is ±10.5 km, well within 16 bits).
        private static uint Pack(float x, float z)
        {
            ushort px = (ushort)(short)Mathf.Clamp(Mathf.RoundToInt(x), short.MinValue, short.MaxValue);
            ushort pz = (ushort)(short)Mathf.Clamp(Mathf.RoundToInt(z), short.MinValue, short.MaxValue);
            return ((uint)px << 16) | pz;
        }

        private static Vector2 Unpack(uint packed)
        {
            return new Vector2((short)(packed >> 16), (short)(packed & 0xFFFF));
        }
    }
}
