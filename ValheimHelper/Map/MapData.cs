using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper.Map
{
    internal sealed class MapPlace
    {
        public MapType Type;
        public Vector3 Pos;
        // The zone was generated, i.e. somebody has been near.
        public bool Placed;
        // Only one instance of this location may exist (traders): until one is placed, all candidates are listed.
        public bool Unique;
        public string Key;
    }

    // Location data of the current world. The host reads ZoneSystem.m_locationInstances directly; the game
    // never sends that table to clients, so a host running this mod sends it on request.
    // The table already follows the game's own rules: when a unique location is placed, the game removes
    // every other candidate, so the map shows all candidates first and only the fixed place afterwards.
    // Player marks (favorite / done) are kept per world by the host and shared with players running the mod.
    internal static class MapData
    {
        internal const int FlagDone = 1;
        internal const int FlagFavorite = 2;

        internal enum SourceKind
        {
            None,
            Host,
            Client,
            Waiting,
            HostWithoutMod,
            OtherVersion,
        }

        private const string RpcRequest = "ValheimHelper_MapRequest";
        private const string RpcData = "ValheimHelper_MapData";
        private const string RpcMark = "ValheimHelper_MapMark";
        private const string RpcMarks = "ValheimHelper_MapMarks";
        private const string ClearPrefix = "*clear:";
        // 3: locations only; the world's objects come separately (WorldObjects).
        private const int ProtocolVersion = 3;
        private const float HostRefreshSec = 2f;
        private const float ClientRefreshSec = 60f;
        private const float ClientTimeoutSec = 8f;

        internal static readonly List<MapPlace> Places = new List<MapPlace>();
        internal static SourceKind Source;
        internal static int Version;
        internal static int MarksVersion;
        internal static float UpdatedAt;
        // Marks come from the host instead of a local file.
        internal static bool MarksShared;

        private static readonly Dictionary<string, MapPlace> s_byKey = new Dictionary<string, MapPlace>();
        private static readonly Dictionary<string, int> s_marks = new Dictionary<string, int>();
        private static readonly AccessTools.FieldRef<ZoneSystem, Dictionary<int, ZoneSystem.ZoneLocation>> s_locationsByHash =
            AccessTools.FieldRefAccess<ZoneSystem, Dictionary<int, ZoneSystem.ZoneLocation>>("m_locationsByHash");

        private static ZNet s_net;
        private static ZRoutedRpc s_rpc;
        private static long s_worldUid;
        private static float s_requestedAt;
        private static long s_hostSignature = -1;

        internal static bool InWorld => ZNet.instance != null && ZNet.World != null && ZoneSystem.instance != null && Player.m_localPlayer != null;

        internal static bool IsHost => ZNet.instance != null && ZNet.instance.IsServer();

        internal static void Tick(bool mapOpen)
        {
            if (ZNet.instance == null)
            {
                if (s_net != null)
                {
                    ResetSession();
                }
                return;
            }
            if (!InWorld)
            {
                return;
            }
            if (!ReferenceEquals(s_net, ZNet.instance))
            {
                ResetSession();
                s_net = ZNet.instance;
                s_worldUid = ZNet.World.m_uid;
                LoadLocalMarks();
                MapSketch.OnSession(s_worldUid);
                Waypoints.OnSession(s_worldUid);
                GamePins.OnSession(s_worldUid);
            }
            EnsureRpc();
            float now = Time.unscaledTime;
            LocationObjects.Tick(mapOpen);
            GamePins.Tick();
            if (IsHost)
            {
                WorldObjects.TickHost(mapOpen);
                if (Places.Count == 0 || (mapOpen && now - UpdatedAt > HostRefreshSec))
                {
                    RebuildFromZoneSystem();
                }
                return;
            }
            WorldObjects.TickClient(mapOpen);
            if (Source == SourceKind.Waiting)
            {
                if (now - s_requestedAt > ClientTimeoutSec)
                {
                    Source = SourceKind.HostWithoutMod;
                    Version++;
                }
                return;
            }
            if (Source == SourceKind.None || (mapOpen && Source == SourceKind.Client && now - UpdatedAt > ClientRefreshSec))
            {
                RequestFromHost();
            }
        }

        internal static void RequestFromHost()
        {
            if (IsHost || ZRoutedRpc.instance == null)
            {
                return;
            }
            EnsureRpc();
            s_requestedAt = Time.unscaledTime;
            UpdatedAt = s_requestedAt;
            if (Places.Count == 0)
            {
                Source = SourceKind.Waiting;
                Version++;
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcRequest);
        }

        internal static MapPlace FindByKey(string key)
        {
            return key != null && s_byKey.TryGetValue(key, out MapPlace place) ? place : null;
        }

        private static void ResetSession()
        {
            Places.Clear();
            s_byKey.Clear();
            s_marks.Clear();
            Source = SourceKind.None;
            MarksShared = false;
            s_net = null;
            s_hostSignature = -1;
            WorldObjects.Reset();
            GamePins.Reset();
            MapSketch.Reset();
            Waypoints.Reset();
            Version++;
            MarksVersion++;
        }

        private static void EnsureRpc()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(rpc, s_rpc))
            {
                return;
            }
            rpc.Register(RpcRequest, new Action<long>(OnRequest));
            rpc.Register<ZPackage>(RpcData, OnData);
            rpc.Register<string, int>(RpcMark, OnMark);
            rpc.Register<ZPackage>(RpcMarks, OnMarks);
            MapSketch.Register(rpc);
            ItemSources.Register(rpc);
            WorldObjects.Register(rpc);
            GamePins.Register(rpc);
            Waypoints.Register(rpc);
#if EDITION_HELPER
            Storage.RemoteStorage.Register(rpc);
#endif
            s_rpc = rpc;
        }

        private static void RebuildFromZoneSystem()
        {
            Dictionary<Vector2s, ZoneSystem.LocationInstance> instances = ZoneSystem.instance.m_locationInstances;
            long placed = 0;
            foreach (ZoneSystem.LocationInstance instance in instances.Values)
            {
                if (instance.m_placed)
                {
                    placed++;
                }
            }
            long signature = instances.Count * 1000003L + placed;
            UpdatedAt = Time.unscaledTime;
            if (signature == s_hostSignature && Places.Count > 0)
            {
                return;
            }
            Places.Clear();
            s_byKey.Clear();
            foreach (ZoneSystem.LocationInstance instance in instances.Values)
            {
                ZoneSystem.ZoneLocation location = instance.m_location;
                if (location != null && !string.IsNullOrEmpty(location.m_prefabName))
                {
                    Add(location.m_prefabName, instance.m_position, instance.m_placed, location.m_unique);
                }
            }
            s_hostSignature = signature;
            Source = SourceKind.Host;
            Version++;
        }

        private static void Add(string prefab, Vector3 pos, bool placed, bool unique)
        {
            string key = prefab + "@" + Mathf.RoundToInt(pos.x).ToString(CultureInfo.InvariantCulture)
                + "," + Mathf.RoundToInt(pos.z).ToString(CultureInfo.InvariantCulture);
            MapPlace place = new MapPlace { Type = MapCatalog.Get(prefab), Pos = pos, Placed = placed, Unique = unique, Key = key };
            Places.Add(place);
            s_byKey[key] = place;
        }

        private static void OnRequest(long sender)
        {
            if (!IsHost || ZoneSystem.instance == null)
            {
                return;
            }
            RebuildFromZoneSystem();
            ZPackage inner = new ZPackage();
            inner.Write(ProtocolVersion);
            inner.Write(Places.Count);
            foreach (MapPlace place in Places)
            {
                inner.Write(StableHash(place.Type.Prefab));
                inner.Write(place.Pos);
                inner.Write(place.Placed);
            }
            ZPackage package = new ZPackage();
            package.WriteCompressed(inner);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcData, package);
            SendMarks(sender);
            MapSketch.SendAll(sender);
            Waypoints.SendAll(sender);
            GamePins.SendAll(sender);
            Log.Info($"Map data sent to peer {sender}: {Places.Count} places");
        }

        private static void OnData(long sender, ZPackage package)
        {
            if (IsHost || ZoneSystem.instance == null)
            {
                return;
            }
            ZPackage inner = package.ReadCompressedPackage();
            if (inner.ReadInt() != ProtocolVersion)
            {
                Source = SourceKind.OtherVersion;
                Version++;
                return;
            }
            int count = inner.ReadInt();
            Dictionary<int, ZoneSystem.ZoneLocation> byHash = s_locationsByHash(ZoneSystem.instance);
            Places.Clear();
            s_byKey.Clear();
            for (int i = 0; i < count; i++)
            {
                int hash = inner.ReadInt();
                Vector3 pos = inner.ReadVector3();
                bool placed = inner.ReadBool();
                if (byHash != null && byHash.TryGetValue(hash, out ZoneSystem.ZoneLocation location) && !string.IsNullOrEmpty(location.m_prefabName))
                {
                    Add(location.m_prefabName, pos, placed, location.m_unique);
                }
                else
                {
                    Add("#" + hash.ToString(CultureInfo.InvariantCulture), pos, placed, false);
                }
            }
            Source = SourceKind.Client;
            UpdatedAt = Time.unscaledTime;
            Version++;
            Log.Info($"Map data received from host: {count} places");
        }

        // Same algorithm as the game's string.GetStableHashCode, which ZoneLocation.Hash is based on.
        private static int StableHash(string text)
        {
            unchecked
            {
                int a = 5381;
                int b = a;
                for (int i = 0; i < text.Length && text[i] != '\0'; i += 2)
                {
                    a = ((a << 5) + a) ^ text[i];
                    if (i == text.Length - 1 || text[i + 1] == '\0')
                    {
                        break;
                    }
                    b = ((b << 5) + b) ^ text[i + 1];
                }
                return a + b * 1566083941;
            }
        }

        // ---------------------------------------------------------------- marks

        internal static int GetFlags(string key)
        {
            return key != null && s_marks.TryGetValue(key, out int flags) ? flags : 0;
        }

        internal static void ToggleFlag(MapPlace place, int flag)
        {
            SetFlags(place.Key, GetFlags(place.Key) ^ flag);
        }

        internal static void ClearFlag(int flag)
        {
            SetFlags(ClearPrefix + flag.ToString(CultureInfo.InvariantCulture), 0);
        }

        internal static int CountFlag(int flag)
        {
            int count = 0;
            foreach (int flags in s_marks.Values)
            {
                if ((flags & flag) != 0)
                {
                    count++;
                }
            }
            return count;
        }

        private static void SetFlags(string key, int flags)
        {
            ApplyMark(key, flags);
            if (!IsHost && MarksShared && ZRoutedRpc.instance != null)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(RpcMark, key, flags);
                return;
            }
            SaveLocalMarks();
            if (IsHost)
            {
                SendMarks(ZRoutedRpc.Everybody);
            }
        }

        private static void ApplyMark(string key, int flags)
        {
            if (key.StartsWith(ClearPrefix))
            {
                if (!int.TryParse(key.Substring(ClearPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int flag))
                {
                    return;
                }
                foreach (string existing in new List<string>(s_marks.Keys))
                {
                    int remaining = s_marks[existing] & ~flag;
                    if (remaining == 0)
                    {
                        s_marks.Remove(existing);
                    }
                    else
                    {
                        s_marks[existing] = remaining;
                    }
                }
            }
            else if (flags == 0)
            {
                s_marks.Remove(key);
            }
            else
            {
                s_marks[key] = flags;
            }
            MarksVersion++;
        }

        private static void OnMark(long sender, string key, int flags)
        {
            if (!IsHost || string.IsNullOrEmpty(key))
            {
                return;
            }
            ApplyMark(key, flags);
            SaveLocalMarks();
            SendMarks(ZRoutedRpc.Everybody);
        }

        private static void SendMarks(long target)
        {
            if (ZRoutedRpc.instance == null)
            {
                return;
            }
            ZPackage package = new ZPackage();
            package.Write(s_marks.Count);
            foreach (KeyValuePair<string, int> pair in s_marks)
            {
                package.Write(pair.Key);
                package.Write(pair.Value);
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcMarks, package);
        }

        private static void OnMarks(long sender, ZPackage package)
        {
            if (IsHost)
            {
                return;
            }
            s_marks.Clear();
            int count = package.ReadInt();
            for (int i = 0; i < count; i++)
            {
                string key = package.ReadString();
                s_marks[key] = package.ReadInt();
            }
            MarksShared = true;
            MarksVersion++;
        }

        private static string MarksPath => Path.Combine(Path.Combine(Loader.Dir ?? ".", "maps"), s_worldUid.ToString(CultureInfo.InvariantCulture) + ".txt");

        private static void LoadLocalMarks()
        {
            s_marks.Clear();
            try
            {
                if (File.Exists(MarksPath))
                {
                    foreach (string line in File.ReadAllLines(MarksPath))
                    {
                        int tab = line.LastIndexOf('\t');
                        if (tab > 0 && int.TryParse(line.Substring(tab + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int flags) && flags != 0)
                        {
                            s_marks[line.Substring(0, tab)] = flags;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
            MarksVersion++;
        }

        private static void SaveLocalMarks()
        {
            if (!IsHost && MarksShared)
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MarksPath));
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, int> pair in s_marks)
                {
                    lines.Add(pair.Key + "\t" + pair.Value.ToString(CultureInfo.InvariantCulture));
                }
                File.WriteAllLines(MarksPath, lines.ToArray());
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }
    }
}
