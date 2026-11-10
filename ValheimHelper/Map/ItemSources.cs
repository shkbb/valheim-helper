using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper.Map
{
    // Where an item comes from outside the places (see PlaceContents): creatures that drop it, ore veins and rocks
    // that give it, plants and pickables, trees. Read from every prefab of the game, so nothing is listed by hand.
    // "Показати найближчі" looks for such objects among the ZDOs of the world (the host has all generated zones;
    // a friend asks the host) and shows the nearest ones on the map.
    internal static class ItemSources
    {
        internal sealed class Source
        {
            public string Prefab;
            public string Name;
            public bool Creature;
        }

        internal struct Found
        {
            public Vector3 Pos;
            public string Name;
        }

        private const string RpcFind = "ValheimHelper_FindSources";
        private const string RpcFound = "ValheimHelper_SourcesFound";
        // All sources in the generated part of the world, nearest first; a cap only for very common things (trees).
        private const int MaxFound = 5000;
        private const int SectorsPerFrame = 8000;

        private static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> s_sectors = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");

        private static Dictionary<string, List<Source>> s_byItem;
        private static ZNetScene s_scene;

        internal static readonly List<Found> Results = new List<Found>();
        internal static string ResultsFor;
        // How many were found in all (Results keeps the nearest MaxFound).
        internal static int ResultsTotal;
        // The item's own icon, drawn for every source on the maps.
        internal static MapIcon Icon;
        internal static int Version;
        internal static bool Searching => s_scan != null || Time.unscaledTime < s_waitUntil;

        private sealed class Scan
        {
            public long Requester;
            public Vector3 Center;
            public string Item;
            public Dictionary<int, string> Prefabs;
            public int Cursor;
            public readonly List<Found> Found = new List<Found>();
        }

        private static Scan s_scan;
        private static float s_waitUntil;

        private static void Ensure()
        {
            ZNetScene scene = ZNetScene.instance;
            if (scene == null || (s_byItem != null && ReferenceEquals(scene, s_scene)))
            {
                return;
            }
            s_scene = scene;
            s_byItem = new Dictionary<string, List<Source>>();
            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (prefab == null)
                {
                    continue;
                }
                Character character = prefab.GetComponent<Character>();
                if (character != null)
                {
                    CharacterDrop drops = prefab.GetComponent<CharacterDrop>();
                    if (drops != null)
                    {
                        foreach (CharacterDrop.Drop drop in drops.m_drops)
                        {
                            string creature = Localization.instance.Localize(character.m_name);
                            Add(ItemOf(drop?.m_prefab), prefab.name, Readable(creature) ? creature : null, true);
                        }
                    }
                    continue;
                }
                Pickable pickable = prefab.GetComponent<Pickable>();
                if (pickable != null)
                {
                    string item = ItemOf(pickable.m_itemPrefab);
                    string name = !string.IsNullOrEmpty(pickable.m_overrideName) ? Localization.instance.Localize(pickable.m_overrideName) : Localization.instance.Localize(item ?? prefab.name);
                    name = Readable(name) ? name : null;
                    Add(item, prefab.name, name, false);
                    AddTable(pickable.m_extraDrops, prefab.name, name);
                }
                MineRock rock = prefab.GetComponent<MineRock>();
                if (rock != null)
                {
                    AddTable(rock.m_dropItems, prefab.name, NameOr(rock.m_name, null));
                }
                MineRock5 rock5 = prefab.GetComponent<MineRock5>();
                if (rock5 != null)
                {
                    AddTable(rock5.m_dropItems, prefab.name, NameOr(rock5.m_name, null));
                }
                TreeBase tree = prefab.GetComponent<TreeBase>();
                if (tree != null)
                {
                    AddTable(tree.m_dropWhenDestroyed, prefab.name, HoverName(prefab));
                }
                DropOnDestroyed destroyed = prefab.GetComponent<DropOnDestroyed>();
                if (destroyed != null)
                {
                    AddTable(destroyed.m_dropWhenDestroyed, prefab.name, HoverName(prefab));
                }
            }
        }

        private static string NameOr(string key, string fallback)
        {
            string name = string.IsNullOrEmpty(key) ? "" : Localization.instance.Localize(key);
            return Readable(name) ? name : Readable(fallback) ? fallback : null;
        }

        // A name the player can read in the game's language: not a prefab name or an untranslated English one
        // ("barrell", "Copper vein") when the game is set to another language.
        internal static bool Readable(string name)
        {
            if (string.IsNullOrEmpty(name) || name.StartsWith("[") || name.Contains("$"))
            {
                return false;
            }
            if (Localization.instance == null || Localization.instance.GetSelectedLanguage() == "English")
            {
                return name.IndexOf('_') < 0;
            }
            foreach (char c in name)
            {
                if (c > 127 && char.IsLetter(c))
                {
                    return true;
                }
            }
            return false;
        }

        private static string HoverName(GameObject prefab)
        {
            Hoverable hoverable = prefab.GetComponent<Hoverable>();
            string name = null;
            try
            {
                name = hoverable?.GetHoverName();
            }
            catch
            {
                // Some hover names need a live object.
            }
            name = string.IsNullOrEmpty(name) ? null : Localization.instance.Localize(name);
            return Readable(name) ? name : null;
        }

        private static string ItemOf(GameObject prefab)
        {
            if (prefab == null)
            {
                return null;
            }
            ItemDrop item = prefab.GetComponent<ItemDrop>();
            if (item != null)
            {
                return item.m_itemData.m_shared.m_name;
            }
            Pickable pickable = prefab.GetComponent<Pickable>();
            ItemDrop inner = pickable != null && pickable.m_itemPrefab != null ? pickable.m_itemPrefab.GetComponent<ItemDrop>() : null;
            return inner != null ? inner.m_itemData.m_shared.m_name : null;
        }

        private static void AddTable(DropTable table, string prefab, string name)
        {
            if (table?.m_drops == null)
            {
                return;
            }
            foreach (DropTable.DropData drop in table.m_drops)
            {
                Add(ItemOf(drop.m_item), prefab, name, false);
            }
        }

        private static void Add(string item, string prefab, string name, bool creature)
        {
            if (string.IsNullOrEmpty(item))
            {
                return;
            }
            if (!s_byItem.TryGetValue(item, out List<Source> sources))
            {
                sources = new List<Source>();
                s_byItem[item] = sources;
            }
            if (!sources.Exists(source => source.Prefab == prefab))
            {
                sources.Add(new Source { Prefab = prefab, Name = name, Creature = creature });
            }
        }

        internal static List<Source> SourcesOf(string item)
        {
            Ensure();
            return s_byItem != null && item != null && s_byItem.TryGetValue(item, out List<Source> sources) ? sources : new List<Source>();
        }

        // Items whose name contains the query, with their sources outside places.
        internal static List<KeyValuePair<string, List<Source>>> Matching(string query, int max)
        {
            List<KeyValuePair<string, List<Source>>> result = new List<KeyValuePair<string, List<Source>>>();
            Ensure();
            if (s_byItem == null || string.IsNullOrEmpty(query))
            {
                return result;
            }
            foreach (KeyValuePair<string, List<Source>> pair in s_byItem)
            {
                string name = Localization.instance.Localize(pair.Key).ToLowerInvariant();
                bool bySource = pair.Value.Exists(source => source.Name != null && source.Name.ToLowerInvariant().Contains(query));
                if (name.Contains(query) || bySource)
                {
                    result.Add(pair);
                    if (result.Count >= max)
                    {
                        break;
                    }
                }
            }
            return result;
        }

        // The sources in words; those already shown as whole-world resources are left to the caller.
        internal static string Describe(List<Source> sources)
        {
            List<string> creatures = new List<string>();
            List<string> nodes = new List<string>();
            foreach (Source source in sources)
            {
                if (source.Name == null || WorldVegetation.Covers(source.Prefab))
                {
                    continue;
                }
                List<string> into = source.Creature ? creatures : nodes;
                if (!into.Contains(source.Name))
                {
                    into.Add(source.Name);
                }
            }
            List<string> parts = new List<string>();
            if (creatures.Count > 0)
            {
                parts.Add(Lang.T("випадає з: ") + Short(creatures));
            }
            if (nodes.Count > 0)
            {
                parts.Add(Lang.T("ще з: ") + Short(nodes));
            }
            return string.Join("; ", parts.ToArray());
        }

        private static string Short(List<string> names)
        {
            return names.Count <= 6 ? string.Join(", ", names.ToArray()) : string.Join(", ", names.GetRange(0, 6).ToArray()) + Lang.F(" і ще {0}", names.Count - 6);
        }

        // ---------------------------------------------------------------- positions

        internal static void Register(ZRoutedRpc rpc)
        {
            rpc.Register<string, Vector3>(RpcFind, OnFind);
            rpc.Register<ZPackage>(RpcFound, OnFound);
        }

        internal static string RequestNearest(string item)
        {
            Player player = Player.m_localPlayer;
            if (player == null || ZRoutedRpc.instance == null)
            {
                return Lang.T("Зайди у світ.");
            }
            Results.Clear();
            ResultsFor = item;
            Version++;
            if (MapData.IsHost)
            {
                StartScan(ZDOMan.GetSessionID(), item, player.transform.position);
            }
            else
            {
                s_waitUntil = Time.unscaledTime + 15f;
                ZRoutedRpc.instance.InvokeRoutedRPC(RpcFind, item, player.transform.position);
            }
            return "";
        }

        internal static void Clear()
        {
            Results.Clear();
            ResultsFor = null;
            ResultsTotal = 0;
            Icon = null;
            Version++;
        }

        private static void OnFind(long sender, string item, Vector3 center)
        {
            if (MapData.IsHost)
            {
                StartScan(sender, item, center);
            }
        }

        private static void StartScan(long requester, string item, Vector3 center)
        {
            Ensure();
            Dictionary<int, string> prefabs = new Dictionary<int, string>();
            if (s_byItem != null && s_byItem.TryGetValue(item, out List<Source> sources))
            {
                foreach (Source source in sources)
                {
                    if (!WorldVegetation.Covers(source.Prefab))
                    {
                        prefabs[source.Prefab.GetStableHashCode()] = source.Name;
                    }
                }
            }
            s_scan = new Scan { Requester = requester, Item = item, Center = center, Prefabs = prefabs };
        }

        internal static void Tick()
        {
            Scan scan = s_scan;
            ZDOMan zdoMan = ZDOMan.instance;
            if (scan == null || zdoMan == null)
            {
                return;
            }
            List<ZDO>[] sectors = s_sectors(zdoMan);
            if (sectors == null || scan.Prefabs.Count == 0)
            {
                Deliver(scan);
                return;
            }
            int end = Mathf.Min(sectors.Length, scan.Cursor + SectorsPerFrame);
            for (; scan.Cursor < end; scan.Cursor++)
            {
                List<ZDO> zdos = sectors[scan.Cursor];
                if (zdos == null)
                {
                    continue;
                }
                foreach (ZDO zdo in zdos)
                {
                    if (zdo != null && scan.Prefabs.TryGetValue(zdo.GetPrefab(), out string name))
                    {
                        scan.Found.Add(new Found { Pos = zdo.GetPosition(), Name = name });
                    }
                }
            }
            if (scan.Cursor >= sectors.Length)
            {
                Deliver(scan);
            }
        }

        private static void Deliver(Scan scan)
        {
            s_scan = null;
            Vector3 center = scan.Center;
            int total = scan.Found.Count;
            scan.Found.Sort((a, b) => (a.Pos - center).sqrMagnitude.CompareTo((b.Pos - center).sqrMagnitude));
            if (scan.Found.Count > MaxFound)
            {
                scan.Found.RemoveRange(MaxFound, scan.Found.Count - MaxFound);
            }
            if (scan.Requester == ZDOMan.GetSessionID())
            {
                Show(scan.Item, scan.Found, total);
                return;
            }
            List<string> names = new List<string>();
            Dictionary<string, int> index = new Dictionary<string, int>();
            foreach (Found found in scan.Found)
            {
                string name = found.Name ?? "";
                if (!index.ContainsKey(name))
                {
                    index[name] = names.Count;
                    names.Add(name);
                }
            }
            ZPackage inner = new ZPackage();
            inner.Write(scan.Item);
            inner.Write(total);
            inner.Write(names.Count);
            foreach (string name in names)
            {
                inner.Write(name);
            }
            inner.Write(scan.Found.Count);
            foreach (Found found in scan.Found)
            {
                inner.Write(found.Pos);
                inner.Write(index[found.Name ?? ""]);
            }
            ZPackage package = new ZPackage();
            package.WriteCompressed(inner);
            ZRoutedRpc.instance?.InvokeRoutedRPC(scan.Requester, RpcFound, package);
        }

        private static void OnFound(long sender, ZPackage package)
        {
            ZPackage inner = package.ReadCompressedPackage();
            string item = inner.ReadString();
            int total = inner.ReadInt();
            int nameCount = Mathf.Clamp(inner.ReadInt(), 0, 10000);
            string[] names = new string[nameCount];
            for (int i = 0; i < nameCount; i++)
            {
                names[i] = inner.ReadString();
            }
            int count = Mathf.Clamp(inner.ReadInt(), 0, MaxFound);
            List<Found> found = new List<Found>(count);
            for (int i = 0; i < count; i++)
            {
                Vector3 pos = inner.ReadVector3();
                int name = inner.ReadInt();
                found.Add(new Found { Pos = pos, Name = name >= 0 && name < names.Length ? names[name] : "" });
            }
            s_waitUntil = 0f;
            if (item == ResultsFor)
            {
                Show(item, found, total);
            }
        }

        private static void Show(string item, List<Found> found, int total)
        {
            Results.Clear();
            Results.AddRange(found);
            ResultsFor = item;
            ResultsTotal = total;
            Icon = IconOf(item);
            Version++;
            string more = total > found.Count ? Lang.F(" (показано найближчі {0})", found.Count) : "";
            if (found.Count > 0)
            {
                MapOverlay.ShowMessage(Lang.F("«{0}»: ще {1} джерел у розвіданих зонах{2}.", Localization.instance.Localize(item), total, more));
            }
        }

        internal static MapIcon IconOf(string sharedName)
        {
            if (ObjectDB.instance == null)
            {
                return null;
            }
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
    }
}
