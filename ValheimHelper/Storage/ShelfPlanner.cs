using System;
using System.Collections.Generic;

namespace ValheimHelper.Storage
{
    internal struct Spot
    {
        public float X;
        public float Y;
        public float Z;

        public Spot(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float DistanceSqr(Spot other)
        {
            float dx = X - other.X;
            float dy = Y - other.Y;
            float dz = Z - other.Z;
            return dx * dx + dy * dy + dz * dz;
        }
    }

    // Lays items out over empty chests, the way a player sets up a new storage: one shelf (a group, like wood or
    // trophies, or a biome for loose materials) per chest, shelves in a fixed order along a walk from chest to the
    // nearest next one, so related shelves stand side by side. Used for items that no chest takes yet (a new
    // storage, or a new kind of item) and for the overflow of full home chests, which opens an empty chest next to
    // its home. Pure rules without Unity types, so tools/SortTests can check them.
    internal static class ShelfPlanner
    {
        internal sealed class Entry
        {
            public ItemTraits Traits;
            // Chest slots the item needs.
            public int Slots;
            // Overflow of a full home chest: the empty chest nearest to this spot is used.
            public bool HasHome;
            public Spot Home;
        }

        internal sealed class EmptyChest
        {
            public int Slots;
            public Spot Position;
        }

        internal struct Placement
        {
            public int Entry;
            public int Chest;
            public int Slots;
        }

        // Position of a group in the storage order (wood, stone, metal...). Set by the catalog.
        internal static Func<string, int> GroupOrder = group => 0;

        internal const string LooseKey = "loose";

        internal static string ShelfKey(ItemTraits traits)
        {
            if (traits?.Group != null)
            {
                return "g:" + traits.Group;
            }
            if (traits != null && traits.Biome >= 0)
            {
                return "b:" + traits.Biome;
            }
            return LooseKey;
        }

        // Groups first, then biome shelves in progression order, then items nothing is known about.
        internal static int ShelfOrder(ItemTraits traits)
        {
            if (traits?.Group != null)
            {
                return GroupOrder(traits.Group);
            }
            if (traits != null && traits.Biome >= 0)
            {
                return 1000 + traits.Biome;
            }
            return 2000;
        }

        // Chests in walking order: the nearest one to the start, then each time the nearest one to the previous chest.
        internal static List<int> WalkOrder(IList<EmptyChest> chests, Spot start)
        {
            List<int> order = new List<int>();
            bool[] visited = new bool[chests.Count];
            Spot at = start;
            for (int step = 0; step < chests.Count; step++)
            {
                int best = -1;
                float bestDistance = float.MaxValue;
                for (int i = 0; i < chests.Count; i++)
                {
                    float distance = chests[i].Position.DistanceSqr(at);
                    if (!visited[i] && distance < bestDistance)
                    {
                        best = i;
                        bestDistance = distance;
                    }
                }
                visited[best] = true;
                order.Add(best);
                at = chests[best].Position;
            }
            return order;
        }

        internal static List<Placement> Plan(IList<Entry> entries, IList<EmptyChest> chests, Spot start)
        {
            List<Placement> placements = new List<Placement>();
            int[] used = new int[chests.Count];
            string[] shelf = new string[chests.Count];

            // Overflow first: its chest must stand next to the full home chest.
            List<int> overflow = new List<int>();
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].HasHome && entries[i].Slots > 0)
                {
                    overflow.Add(i);
                }
            }
            overflow.Sort((a, b) => CompareEntries(entries[a], entries[b], a, b));
            foreach (int index in overflow)
            {
                Entry entry = entries[index];
                string key = ShelfKey(entry.Traits);
                int left = entry.Slots;
                while (left > 0)
                {
                    int chest = OpenShelf(chests, used, shelf, key);
                    if (chest < 0)
                    {
                        chest = NearestFree(chests, shelf, entry.Home);
                    }
                    if (chest < 0)
                    {
                        break;
                    }
                    shelf[chest] = key;
                    left -= Put(placements, index, chest, left, chests, used);
                }
            }

            // Everything else as shelves along the walk.
            List<List<int>> shelves = new List<List<int>>();
            Dictionary<string, List<int>> byKey = new Dictionary<string, List<int>>();
            List<int> rest = new List<int>();
            for (int i = 0; i < entries.Count; i++)
            {
                if (!entries[i].HasHome && entries[i].Slots > 0)
                {
                    rest.Add(i);
                }
            }
            rest.Sort((a, b) => CompareEntries(entries[a], entries[b], a, b));
            foreach (int index in rest)
            {
                string key = ShelfKey(entries[index].Traits);
                if (!byKey.TryGetValue(key, out List<int> members))
                {
                    members = new List<int>();
                    byKey[key] = members;
                    shelves.Add(members);
                }
                members.Add(index);
            }

            List<int> walk = WalkOrder(chests, start);
            int current = -1;
            for (int s = 0; s < shelves.Count; s++)
            {
                string key = ShelfKey(entries[shelves[s][0]].Traits);
                int shelfSlots = 0;
                foreach (int index in shelves[s])
                {
                    shelfSlots += entries[index].Slots;
                }
                // A shelf gets its own chest while there are enough of them; otherwise it shares the current one.
                if (current >= 0)
                {
                    int freeChests = 0;
                    foreach (int chest in walk)
                    {
                        freeChests += shelf[chest] == null ? 1 : 0;
                    }
                    bool fitsHere = chests[current].Slots - used[current] >= shelfSlots;
                    if (freeChests >= shelves.Count - s || (!fitsHere && freeChests > 0))
                    {
                        current = -1;
                    }
                }
                foreach (int index in shelves[s])
                {
                    int left = entries[index].Slots;
                    while (left > 0)
                    {
                        int chest = OpenShelf(chests, used, shelf, key);
                        if (chest < 0 && current >= 0 && used[current] < chests[current].Slots)
                        {
                            chest = current;
                        }
                        if (chest < 0)
                        {
                            chest = NextFree(walk, shelf);
                        }
                        if (chest < 0)
                        {
                            break;
                        }
                        shelf[chest] = shelf[chest] ?? key;
                        current = chest;
                        left -= Put(placements, index, chest, left, chests, used);
                    }
                }
            }
            return placements;
        }

        // Tiers in order inside a shelf, then by id so the layout does not depend on the inventory order.
        private static int CompareEntries(Entry a, Entry b, int indexA, int indexB)
        {
            int order = ShelfOrder(a.Traits).CompareTo(ShelfOrder(b.Traits));
            if (order != 0)
            {
                return order;
            }
            order = string.CompareOrdinal(ShelfKey(a.Traits), ShelfKey(b.Traits));
            if (order != 0)
            {
                return order;
            }
            order = (a.Traits?.Biome ?? -1).CompareTo(b.Traits?.Biome ?? -1);
            if (order != 0)
            {
                return order;
            }
            order = string.CompareOrdinal(a.Traits?.Id, b.Traits?.Id);
            return order != 0 ? order : indexA.CompareTo(indexB);
        }

        private static int Put(List<Placement> placements, int entry, int chest, int wanted, IList<EmptyChest> chests, int[] used)
        {
            int take = Math.Min(wanted, chests[chest].Slots - used[chest]);
            if (take <= 0)
            {
                return 0;
            }
            used[chest] += take;
            placements.Add(new Placement { Entry = entry, Chest = chest, Slots = take });
            return take;
        }

        // A chest already given to this shelf that still has room.
        private static int OpenShelf(IList<EmptyChest> chests, int[] used, string[] shelf, string key)
        {
            for (int i = 0; i < chests.Count; i++)
            {
                if (shelf[i] == key && used[i] < chests[i].Slots)
                {
                    return i;
                }
            }
            return -1;
        }

        private static int NearestFree(IList<EmptyChest> chests, string[] shelf, Spot near)
        {
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < chests.Count; i++)
            {
                float distance = chests[i].Position.DistanceSqr(near);
                if (shelf[i] == null && chests[i].Slots > 0 && distance < bestDistance)
                {
                    best = i;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private static int NextFree(List<int> walk, string[] shelf)
        {
            foreach (int chest in walk)
            {
                if (shelf[chest] == null)
                {
                    return chest;
                }
            }
            return -1;
        }
    }
}
