using System;
using System.Collections.Generic;

namespace ValheimHelper.Storage
{
    // What the sorter knows about one kind of item.
    internal sealed class ItemTraits
    {
        public string Id;
        // Group id (wood, stone, trophies...), or null.
        public string Group;
        // For wood, stone and metal the biome (tier) matters inside the group: black marble is not ordinary stone.
        public bool GroupIsTiered;
        // Progression index of the item's biome (0 = Meadows), or -1 when unknown.
        public int Biome = -1;
    }

    // One kind of item inside a chest and how many slots it takes.
    internal struct ChestKind
    {
        public ItemTraits Traits;
        public int Slots;

        public ChestKind(ItemTraits traits, int slots)
        {
            Traits = traits;
            Slots = slots;
        }
    }

    // Where an item with no home chest goes. Pure rules without Unity types, so tools/SortTests can check them.
    //
    // Every item in a chest is compared with the new item:
    //  - same group, any biome, for groups like trophies, hides or seeds;
    //  - same group and same biome for tiered groups (wood, stone, metal); neighbouring tiers count less;
    //  - same biome in another group ("theme" chests like a Mistlands chest), stronger when the two items are
    //    used together in recipes.
    // A chest that stores one material (a single kind, or one group taking most of it) is not a theme chest, so
    // relations that come only from the biome count little there: feathers do not go into the wood chest.
    internal static class SortRules
    {
        internal const float MinAffinity = 0.3f;
        internal const float SameUntieredGroup = 0.9f;
        internal const float SameTieredGroupUnknownBiome = 0.6f;
        internal const float SameBiomeTheme = 0.55f;
        internal const float StoreChestThemeFactor = 0.4f;

        // How much two items are used together in recipes, 0..1. Set by the catalog.
        internal static Func<string, string, float> CoUse = (a, b) => 0f;

        // Groups that are a kind of thing of their own: coins and gems, trophies, food, gear... They keep to their own
        // group and never join a biome's "theme" chest just because they come from that biome (coins found in Black
        // Forest chests are not a Black Forest material). Materials (wood, stone, metal, hides, cores, ungrouped)
        // still follow their biome.
        internal static readonly HashSet<string> OwnKindGroups = new HashSet<string>
        {
            "valuables", "trophies", "fish", "food", "meads", "ammo", "gear", "seeds", "rawfood", "fuel",
        };

        private static bool OwnKind(ItemTraits traits)
        {
            return traits.Group != null && OwnKindGroups.Contains(traits.Group);
        }

        internal static float Relation(ItemTraits item, ItemTraits other, out bool viaGroup)
        {
            viaGroup = false;
            if (item == null || other == null)
            {
                return 0f;
            }
            if (item.Id == other.Id)
            {
                viaGroup = true;
                return 1f;
            }
            bool sameGroup = item.Group != null && item.Group == other.Group;
            bool bothBiomes = item.Biome >= 0 && other.Biome >= 0;
            float co = Math.Max(0f, Math.Min(1f, CoUse(item.Id, other.Id)));
            if (sameGroup)
            {
                viaGroup = true;
                if (!item.GroupIsTiered)
                {
                    return SameUntieredGroup;
                }
                if (!bothBiomes)
                {
                    return SameTieredGroupUnknownBiome;
                }
                int gap = Math.Abs(item.Biome - other.Biome);
                return gap == 0 ? 1f : Math.Max(MinAffinity, 0.6f - 0.05f * gap);
            }
            if (OwnKind(item) || OwnKind(other))
            {
                // gear, trophies, food… keep to their own group: a crossbow is not a black metal thing because it is
                // made of black metal
                return 0f;
            }
            if (bothBiomes)
            {
                // A shared recipe makes a chest a home only within one biome: a Mistlands wisp does not join a chest of
                // Plains metal and Ashlands bones because some recipe uses them together.
                return item.Biome == other.Biome ? SameBiomeTheme + 0.35f * co : 0f;
            }
            // the biome of one of them is unknown: shared recipes are the only hint
            return 0.5f * co;
        }

        private static bool Informative(ItemTraits traits)
        {
            return traits != null && (traits.Group != null || traits.Biome >= 0);
        }

        // 0..1: how well the item fits the chest's current contents.
        internal static float Affinity(ItemTraits item, IList<ChestKind> kinds)
        {
            int total = 0;
            int informative = 0;
            Dictionary<string, int> groupSlots = new Dictionary<string, int>();
            foreach (ChestKind kind in kinds)
            {
                if (!Informative(kind.Traits))
                {
                    // Items of unknown group and biome say nothing about the chest; they neither help nor dilute.
                    continue;
                }
                informative++;
                total += kind.Slots;
                string group = kind.Traits.Group;
                if (group != null)
                {
                    groupSlots[group] = (groupSlots.TryGetValue(group, out int slots) ? slots : 0) + kind.Slots;
                }
            }
            if (total <= 0 || item == null)
            {
                return 0f;
            }
            int largestGroup = 0;
            foreach (int slots in groupSlots.Values)
            {
                largestGroup = Math.Max(largestGroup, slots);
            }
            bool themeChest = informative >= 2 && largestGroup < total * 0.6f;

            float viaGroup = 0f;
            float viaTheme = 0f;
            foreach (ChestKind kind in kinds)
            {
                if (!Informative(kind.Traits))
                {
                    continue;
                }
                float relation = Relation(item, kind.Traits, out bool byGroup);
                if (byGroup)
                {
                    viaGroup += relation * kind.Slots;
                }
                else
                {
                    viaTheme += relation * kind.Slots;
                }
            }
            return (viaGroup + viaTheme * (themeChest ? 1f : StoreChestThemeFactor)) / total;
        }

        // Higher is better, 0 means "not this chest". countHere counts the item's pieces really lying in this chest
        // (not the ones planned in this run). remembered is true for the chest the player most recently put the item
        // into by hand (among the chests around): that is the item's home even when it is empty now, or when some of
        // the item lies elsewhere.
        internal static float Score(ItemTraits item, IList<ChestKind> kinds, string tagGroup, int tagBiome, bool remembered,
            int countHere, int countEverywhere, bool anyChestHasItem)
        {
            if (remembered)
            {
                return 1200f;
            }
            if (countHere > 0)
            {
                // Most of this item lies here: that is its home chest.
                return 1000f + 100f * countHere / Math.Max(1, countEverywhere);
            }
            if (tagGroup != null || tagBiome >= 0)
            {
                if (item != null && tagGroup != null && tagGroup == item.Group)
                {
                    return 900f;
                }
                if (item != null && tagBiome >= 0 && tagBiome == item.Biome)
                {
                    return 850f;
                }
                // A labelled chest takes only what its label says.
                return 0f;
            }
            if (anyChestHasItem || item == null)
            {
                // The item already has a home around; the overflow never spills into other chests.
                return 0f;
            }
            float affinity = Affinity(item, kinds);
            return affinity >= MinAffinity ? 300f + 500f * affinity : 0f;
        }
    }
}
