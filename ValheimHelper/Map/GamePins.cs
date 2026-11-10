using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper.Map
{
    // The game's own map pins (the fire, house, hammer, dot and portal icons placed with a double click, bosses found
    // by vegvisirs…) seen by friends with the mod. Each player who switches it on sends the host the pins of their own
    // map; the host keeps them per world and sends everybody's to everybody. A friend's pins are added the way the game
    // adds pins from a cartography table (not saved, owned by their author), so the game's own "shared" switch on the
    // large map hides them, and nothing gets into anybody's saved map.
    internal static class GamePins
    {
        private const string RpcPut = "ValheimHelper_PinsPut";
        private const string RpcAll = "ValheimHelper_PinsAll";
        private const float CheckEverySec = 1f;
        private const float SaveDelaySec = 5f;
        private const int MaxPins = 2000;

        private struct Pin
        {
            public int Type;
            public Vector3 Pos;
            public string Name;
            public bool Checked;
        }

        private sealed class Author
        {
            public string Name = "";
            public readonly List<Pin> Pins = new List<Pin>();
        }

        // Pins a player places or finds, not the game's own marks of the moment (death, bed, pings, events).
        private static readonly Minimap.PinType[] s_kinds =
        {
            Minimap.PinType.Icon0, Minimap.PinType.Icon1, Minimap.PinType.Icon2, Minimap.PinType.Icon3, Minimap.PinType.Icon4,
            Minimap.PinType.Boss, Minimap.PinType.Hildir1, Minimap.PinType.Hildir2, Minimap.PinType.Hildir3, Minimap.PinType.Memorial,
        };

        private static readonly AccessTools.FieldRef<Minimap, List<Minimap.PinData>> s_pins = AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins");

        // Everybody's shared pins: on the host the kept ones, on a client what the host sent.
        private static readonly Dictionary<long, Author> s_authors = new Dictionary<long, Author>();
        // Friends' pins this mod put on the map, so they can be taken off again.
        private static readonly List<Minimap.PinData> s_added = new List<Minimap.PinData>();
        private static long s_worldUid;
        private static Minimap s_map;
        private static bool s_applyNeeded;
        private static string s_sent;
        private static float s_checkAt;
        private static bool s_dirty;
        private static float s_saveAt;

        private static string FilePath => Path.Combine(Path.Combine(Loader.Dir ?? ".", "maps"), s_worldUid.ToString(CultureInfo.InvariantCulture) + ".shared_pins.txt");

        private static long MyId => Game.instance != null ? Game.instance.GetPlayerProfile().GetPlayerID() : 0L;

        private static string MyName => Game.instance != null ? Game.instance.GetPlayerProfile().GetName() : "";

        internal static void Register(ZRoutedRpc rpc)
        {
            rpc.Register<ZPackage>(RpcPut, OnPut);
            rpc.Register<ZPackage>(RpcAll, OnAll);
        }

        internal static void OnSession(long worldUid)
        {
            s_worldUid = worldUid;
            s_authors.Clear();
            s_added.Clear();
            s_map = null;
            s_sent = null;
            if (MapData.IsHost)
            {
                Load();
            }
            s_applyNeeded = true;
        }

        internal static void Reset()
        {
            Flush();
            s_authors.Clear();
            s_added.Clear();
            s_map = null;
            s_sent = null;
        }

        internal static void Tick()
        {
            Minimap map = Minimap.instance;
            if (map == null || Game.instance == null || Player.m_localPlayer == null)
            {
                return;
            }
            if (!ReferenceEquals(map, s_map))
            {
                s_map = map;
                s_added.Clear();
                s_applyNeeded = true;
            }
            if (Time.unscaledTime >= s_checkAt)
            {
                s_checkAt = Time.unscaledTime + CheckEverySec;
                SendMine();
            }
            if (s_applyNeeded)
            {
                Apply(map);
            }
            if (s_dirty && Time.unscaledTime >= s_saveAt)
            {
                Flush();
            }
        }

        // ---------------------------------------------------------------- my pins

        // My pins as the game keeps them (saved, my own, of the kinds above); none while sharing is off, which takes
        // the ones shared before away from friends.
        private static List<Pin> Mine()
        {
            List<Pin> pins = new List<Pin>();
            if (!MapSettings.ShareGamePins)
            {
                return pins;
            }
            foreach (Minimap.PinData pin in s_pins(Minimap.instance))
            {
                if (pin == null || !pin.m_save || pin.m_ownerID != 0L || Array.IndexOf(s_kinds, pin.m_type) < 0)
                {
                    continue;
                }
                pins.Add(new Pin { Type = (int)pin.m_type, Pos = pin.m_pos, Name = pin.m_name ?? "", Checked = pin.m_checked });
                if (pins.Count >= MaxPins)
                {
                    break;
                }
            }
            return pins;
        }

        private static string Signature(List<Pin> pins)
        {
            StringBuilder text = new StringBuilder();
            foreach (Pin pin in pins)
            {
                text.Append(pin.Type).Append(',').Append(Mathf.RoundToInt(pin.Pos.x)).Append(',').Append(Mathf.RoundToInt(pin.Pos.z)).Append(',')
                    .Append(pin.Checked ? 1 : 0).Append(',').Append(pin.Name).Append(';');
            }
            return text.ToString();
        }

        private static void SendMine()
        {
            // a client sends only to a host that has the mod (it answered the map data)
            if (!MapData.IsHost && (MapData.Source != MapData.SourceKind.Client || ZRoutedRpc.instance == null))
            {
                return;
            }
            List<Pin> pins = Mine();
            string signature = Signature(pins);
            if (signature == s_sent)
            {
                return;
            }
            s_sent = signature;
            if (MapData.IsHost)
            {
                Store(MyId, MyName, pins);
                return;
            }
            ZPackage package = new ZPackage();
            package.Write(MyId);
            package.Write(MyName);
            WritePins(package, pins);
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcPut, package);
        }

        // ---------------------------------------------------------------- host

        private static void Store(long author, string name, List<Pin> pins)
        {
            if (pins.Count == 0)
            {
                if (!s_authors.Remove(author))
                {
                    return;
                }
            }
            else
            {
                Author entry = new Author { Name = name ?? "" };
                entry.Pins.AddRange(pins);
                s_authors[author] = entry;
            }
            s_dirty = true;
            s_saveAt = Time.unscaledTime + SaveDelaySec;
            s_applyNeeded = true;
            if (ZRoutedRpc.instance != null)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcAll, AllPackage());
            }
        }

        private static void OnPut(long sender, ZPackage package)
        {
            if (!MapData.IsHost)
            {
                return;
            }
            long author = package.ReadLong();
            string name = package.ReadString();
            Store(author, name, ReadPins(package));
        }

        // Host: everybody's pins to a player who asked for the map data.
        internal static void SendAll(long target)
        {
            if (MapData.IsHost && ZRoutedRpc.instance != null)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcAll, AllPackage());
            }
        }

        private static ZPackage AllPackage()
        {
            ZPackage package = new ZPackage();
            package.Write(s_authors.Count);
            foreach (KeyValuePair<long, Author> pair in s_authors)
            {
                package.Write(pair.Key);
                package.Write(pair.Value.Name);
                WritePins(package, pair.Value.Pins);
            }
            return package;
        }

        // ---------------------------------------------------------------- client

        private static void OnAll(long sender, ZPackage package)
        {
            if (MapData.IsHost)
            {
                return;
            }
            s_authors.Clear();
            int count = Mathf.Clamp(package.ReadInt(), 0, 1000);
            for (int i = 0; i < count; i++)
            {
                long author = package.ReadLong();
                Author entry = new Author { Name = package.ReadString() };
                entry.Pins.AddRange(ReadPins(package));
                s_authors[author] = entry;
            }
            s_applyNeeded = true;
        }

        // ---------------------------------------------------------------- the map

        // Friends' pins on this map: the old ones off, the current ones on; a pin already there (the same boss found
        // by both, the same cartography table) is not doubled.
        private static void Apply(Minimap map)
        {
            s_applyNeeded = false;
            List<Minimap.PinData> pins = s_pins(map);
            foreach (Minimap.PinData pin in s_added)
            {
                if (pins.Contains(pin))
                {
                    map.RemovePin(pin);
                }
            }
            s_added.Clear();
            long me = MyId;
            foreach (KeyValuePair<long, Author> pair in s_authors)
            {
                if (pair.Key == me)
                {
                    continue;
                }
                foreach (Pin pin in pair.Value.Pins)
                {
                    if (Exists(pins, pin))
                    {
                        continue;
                    }
                    Minimap.PinData added = map.AddPin(pin.Pos, (Minimap.PinType)pin.Type, pin.Name, false, pin.Checked, pair.Key);
                    if (added != null)
                    {
                        s_added.Add(added);
                    }
                }
            }
        }

        private static bool Exists(List<Minimap.PinData> pins, Pin wanted)
        {
            foreach (Minimap.PinData pin in pins)
            {
                if (pin != null && (int)pin.m_type == wanted.Type && (pin.m_pos - wanted.Pos).sqrMagnitude < 1f)
                {
                    return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- packages and file

        private static void WritePins(ZPackage package, List<Pin> pins)
        {
            package.Write(pins.Count);
            foreach (Pin pin in pins)
            {
                package.Write(pin.Type);
                package.Write(pin.Pos);
                package.Write(pin.Name ?? "");
                package.Write(pin.Checked);
            }
        }

        private static List<Pin> ReadPins(ZPackage package)
        {
            int count = Mathf.Clamp(package.ReadInt(), 0, MaxPins);
            List<Pin> pins = new List<Pin>(count);
            for (int i = 0; i < count; i++)
            {
                pins.Add(new Pin { Type = package.ReadInt(), Pos = package.ReadVector3(), Name = package.ReadString(), Checked = package.ReadBool() });
            }
            return pins;
        }

        internal static void Flush()
        {
            if (!s_dirty || s_worldUid == 0L)
            {
                return;
            }
            s_dirty = false;
            try
            {
                List<string> lines = new List<string>();
                foreach (KeyValuePair<long, Author> pair in s_authors)
                {
                    lines.Add("A\t" + pair.Key.ToString(CultureInfo.InvariantCulture) + "\t" + Clean(pair.Value.Name));
                    foreach (Pin pin in pair.Value.Pins)
                    {
                        lines.Add("P\t" + pin.Type.ToString(CultureInfo.InvariantCulture) + "\t" + Num(pin.Pos.x) + "\t" + Num(pin.Pos.y) + "\t" + Num(pin.Pos.z)
                            + "\t" + (pin.Checked ? "1" : "0") + "\t" + Clean(pin.Name));
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, lines.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Log.Warn("Shared pins: " + e.Message);
            }
        }

        private static void Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return;
                }
                Author current = null;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string[] parts = line.Split('\t');
                    if (parts[0] == "A" && parts.Length >= 3 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long author))
                    {
                        current = new Author { Name = parts[2] };
                        s_authors[author] = current;
                    }
                    else if (parts[0] == "P" && parts.Length >= 7 && current != null)
                    {
                        current.Pins.Add(new Pin
                        {
                            Type = int.Parse(parts[1], CultureInfo.InvariantCulture),
                            Pos = new Vector3(Float(parts[2]), Float(parts[3]), Float(parts[4])),
                            Checked = parts[5] == "1",
                            Name = parts[6],
                        });
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("Shared pins: " + e.Message);
            }
        }

        private static string Clean(string text) => (text ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

        private static string Num(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static float Float(string text) => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
