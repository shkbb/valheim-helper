using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper.Storage
{
    // Crafting and building with materials from the chests around.
    //
    // While the crafting window is open or a building tool is in hand, the game's requirement checks
    // (Player.HaveRequirementItems, Player.HaveRequirements for pieces and the requirement icons) also count what
    // lies in accessible chests nearby, so recipes and pieces show as available. Which recipes and pieces are known
    // is never touched. When "Craft" is pressed or a piece is placed, only the missing pieces are moved from those
    // chests into the inventory, and the game then consumes them from the inventory as usual. Chests are taken over
    // with the game's own request first; the real checks (InventoryGui.DoCrafting, Player.UpdatePlacement) always
    // look at the real inventory only, so nothing is ever made or built without its materials being spent.
    internal static class CraftFromChests
    {
        private const float RetrySeconds = 3f;
        private const float PreRequestInterval = 5f;

        private static readonly AccessTools.FieldRef<InventoryGui, bool> s_multiCrafting = AccessTools.FieldRefAccess<InventoryGui, bool>("m_multiCrafting");
        private static readonly AccessTools.FieldRef<InventoryGui, bool> s_touchMultiCrafting = AccessTools.FieldRefAccess<InventoryGui, bool>("m_touchMultiCrafting");

        // Chest counting is switched on only inside the game methods that merely show requirements, never around
        // the checks right before something is crafted or built:
        // > 0 inside InventoryGui.UpdateCraftingPanel and the crafting requirement icons (display only; the panel is
        //   also rebuilt right after a craft, which is why a general "crafting" flag cannot be used for it);
        internal static int CraftDisplay;
        // > 0 inside the build menu buttons, the HUD piece status, the piece requirement icons and PieceTable.UpdateAvailable;
        internal static int BuildDisplay;
        // > 0 inside Player.HaveRequirementItems, which the open crafting window also calls every frame for the
        //   "Craft" button; counted only while the window is visible and no craft is being carried out.
        internal static int VirtualScope;
        // True while InventoryGui.DoCrafting runs its own check and spends the materials.
        internal static bool Crafting;
        // Player.UpdatePlacement: the place where a piece is actually placed (and where the build menu is also opened).
        internal static bool Placing;
        internal static bool InBuildGui;

        private static readonly List<Container> s_sources = new List<Container>();
        private static float s_sourcesAt = -10f;
        private static readonly Dictionary<string, int> s_counts = new Dictionary<string, int>();
        private static int s_countsFrame = -1;
        private static float s_preRequestAt = -100f;
        private static bool s_wasVisible;

        private sealed class Retry
        {
            public Recipe Recipe;
            public ItemDrop.ItemData UpgradeItem;
            public bool Multi;
            public float Until;
        }

        private static Retry s_retry;
        // Set while a retried press runs, so the original multi-craft choice is kept.
        private static Retry s_retrying;

        internal static bool CraftEnabled => StorageSettings.UseChestsForCrafting && Player.m_localPlayer != null;

        // Free build needs no materials at all.
        internal static bool BuildEnabled => StorageSettings.UseChestsForBuilding && Player.m_localPlayer != null && !State.FreeBuild;

        // Whether the local player's inventory count should include the chests right now.
        internal static bool Counting
        {
            get
            {
                if (CraftDisplay > 0 && CraftEnabled)
                {
                    return true;
                }
                if (BuildDisplay > 0 && BuildEnabled)
                {
                    return true;
                }
                return VirtualScope > 0 && CraftEnabled && !Crafting && InventoryGui.IsVisible();
            }
        }

        // ---------------------------------------------------------------- sources

        internal static List<Container> Sources()
        {
            Player player = Player.m_localPlayer;
            if (Time.unscaledTime - s_sourcesAt > 0.25f)
            {
                s_sourcesAt = Time.unscaledTime;
                s_sources.Clear();
                if (player != null)
                {
                    s_sources.AddRange(StorageSorter.ChestsAround(player.transform.position, StorageSettings.Radius));
                }
            }
            s_sources.RemoveAll(c => c == null);
            return s_sources;
        }

        // Pieces in usable chests around, cached per frame; the same filters as Inventory.CountItems.
        internal static int CountInChests(string name, int quality, bool matchWorldLevel)
        {
            if (s_countsFrame != Time.frameCount)
            {
                s_countsFrame = Time.frameCount;
                s_counts.Clear();
            }
            string key = name + "|" + quality + "|" + (matchWorldLevel ? 1 : 0);
            if (s_counts.TryGetValue(key, out int cached))
            {
                return cached;
            }
            int total = 0;
            foreach (Container chest in Sources())
            {
                if (!StorageSorter.InUseByOther(chest))
                {
                    total += chest.GetInventory().CountItems(name, quality, matchWorldLevel);
                }
            }
            s_counts[key] = total;
            return total;
        }

        // ---------------------------------------------------------------- every frame

        internal static void Tick()
        {
            Player localPlayer = Player.m_localPlayer;
            bool visible = StorageSettings.UseChestsForCrafting && localPlayer != null && InventoryGui.IsVisible();
            bool building = StorageSettings.UseChestsForBuilding && localPlayer != null && localPlayer.InPlaceMode() && !State.FreeBuild;
            if ((visible || building) && (!s_wasVisible || Time.unscaledTime - s_preRequestAt > PreRequestInterval))
            {
                // Chests with something in them are taken over as soon as the window opens or a building tool is
                // taken, so pressing "Craft" or placing a piece usually finds them ready.
                s_preRequestAt = Time.unscaledTime;
                List<Container> wanted = new List<Container>();
                foreach (Container chest in Sources())
                {
                    if (chest.GetInventory().NrOfItems() > 0 && !StorageSorter.InUseByOther(chest))
                    {
                        wanted.Add(chest);
                    }
                }
                StorageSorter.RequestOwnership(wanted);
            }
            s_wasVisible = visible || building;

            Retry retry = s_retry;
            if (retry == null)
            {
                return;
            }
            InventoryGui gui = InventoryGui.instance;
            if (!visible || gui == null)
            {
                s_retry = null;
                return;
            }
            bool pending = false;
            foreach (Container chest in Sources())
            {
                pending |= StorageSorter.IsRequested(chest);
            }
            if (pending && Time.unscaledTime < retry.Until)
            {
                return;
            }
            s_retry = null;
            Traverse selected = Traverse.Create(gui).Field("m_selectedRecipe");
            if (selected.Property("Recipe").GetValue<Recipe>() != retry.Recipe || selected.Property("ItemData").GetValue<ItemDrop.ItemData>() != retry.UpgradeItem)
            {
                return;
            }
            s_retrying = retry;
            try
            {
                Traverse.Create(gui).Method("OnCraftPressed").GetValue();
            }
            finally
            {
                s_retrying = null;
            }
        }

        // ---------------------------------------------------------------- pressing "Craft"

        // Prefix of InventoryGui.OnCraftPressed. Returns false to skip the press for now.
        internal static bool BeforeCraftPressed(InventoryGui gui)
        {
            Player player = Player.m_localPlayer;
            if (!StorageSettings.UseChestsForCrafting || player == null || gui == null)
            {
                return true;
            }
            Traverse selected = Traverse.Create(gui).Field("m_selectedRecipe");
            Recipe recipe = selected.Property("Recipe").GetValue<Recipe>();
            ItemDrop.ItemData upgradeItem = selected.Property("ItemData").GetValue<ItemDrop.ItemData>();
            if (recipe == null)
            {
                return true;
            }
            bool multi = s_retrying != null ? s_retrying.Multi
                : upgradeItem == null && (ZInput.GetButton("AltPlace") || ZInput.GetButton("JoyLStick") || s_touchMultiCrafting(gui));
            int quality = upgradeItem == null ? 1 : upgradeItem.m_quality + 1;
            int multiplier = multi ? gui.m_multiCraftAmount : 1;

            List<KeyValuePair<Piece.Requirement, int>> missing = Missing(player, recipe, quality, multiplier);
            if (missing.Count == 0)
            {
                return true;
            }
            // All or nothing: materials are taken only when the chests around hold everything that is missing, so a
            // craft that cannot happen never leaves pulled materials in the inventory.
            bool covered = true;
            foreach (KeyValuePair<Piece.Requirement, int> need in missing)
            {
                covered &= CountUsableChests(need.Key.m_resItem.m_itemData.m_shared.m_name) >= need.Value;
            }
            if (covered)
            {
                foreach (KeyValuePair<Piece.Requirement, int> need in missing)
                {
                    Pull(player, need.Key.m_resItem.m_itemData.m_shared.m_name, need.Value);
                }
                missing = Missing(player, recipe, quality, multiplier);
                if (missing.Count == 0)
                {
                    return true;
                }
            }
            List<string> names = new List<string>();
            foreach (KeyValuePair<Piece.Requirement, int> need in missing)
            {
                names.Add($"{Localization.instance.Localize(need.Key.m_resItem.m_itemData.m_shared.m_name)} ×{need.Value}");
            }
            player.Message(MessageHud.MessageType.Center, Lang.T("Не вдалося взяти зі скринь: ") + string.Join(", ", names.ToArray()) +
                Lang.T(". Можливо, скриню відкрив інший гравець або в інвентарі немає місця."));
            return false;
        }

        // Postfix of InventoryGui.OnCraftPressed: a retried press keeps the multi-craft choice of the original one.
        internal static void AfterCraftPressed(InventoryGui gui)
        {
            if (s_retrying != null && gui != null)
            {
                s_multiCrafting(gui) = s_retrying.Multi;
            }
        }

        // ---------------------------------------------------------------- placing a piece

        // Called right before the game checks the requirements of the selected piece while it is being placed
        // (inside Player.UpdatePlacement, outside its build menu handling), with chest counting already off:
        // brings the missing materials over, all or nothing.
        internal static void BeforePlacementCheck(Player player, Piece piece)
        {
            if (!StorageSettings.UseChestsForBuilding || State.FreeBuild || player == null || piece?.m_resources == null
                || (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey())))
            {
                return;
            }
            Inventory inventory = player.GetInventory();
            List<KeyValuePair<string, int>> lacking = new List<KeyValuePair<string, int>>();
            foreach (Piece.Requirement requirement in piece.m_resources)
            {
                if (requirement?.m_resItem == null || requirement.m_amount <= 0)
                {
                    continue;
                }
                string name = requirement.m_resItem.m_itemData.m_shared.m_name;
                int amount = requirement.m_amount - inventory.CountItems(name);
                if (amount > 0)
                {
                    lacking.Add(new KeyValuePair<string, int>(name, amount));
                }
            }
            if (lacking.Count == 0)
            {
                return;
            }
            bool covered = true;
            foreach (KeyValuePair<string, int> need in lacking)
            {
                covered &= CountUsableChests(need.Key) >= need.Value;
            }
            if (!covered)
            {
                // Not enough anywhere: nothing is taken, the game shows the missing materials as usual.
                return;
            }
            foreach (KeyValuePair<string, int> need in lacking)
            {
                Pull(player, need.Key, need.Value);
            }
        }

        // What can be taken right now: every chest around except one another player has open. Ownership of a chest
        // is claimed on the spot (ZNetView.ClaimOwnership, the same thing the game does when a chest is opened), so
        // nothing has to be waited for.
        private static int CountUsableChests(string name)
        {
            int total = 0;
            foreach (Container chest in Sources())
            {
                ZNetView view = StorageSorter.View(chest);
                if (view != null && view.IsValid() && !StorageSorter.InUseByOther(chest))
                {
                    total += chest.GetInventory().CountItems(name);
                }
            }
            return total;
        }

        // Requirements the real inventory cannot cover, with the missing amount; mirrors
        // Player.HaveRequirementItems and ConsumeResources.
        private static List<KeyValuePair<Piece.Requirement, int>> Missing(Player player, Recipe recipe, int quality, int multiplier)
        {
            List<KeyValuePair<Piece.Requirement, int>> result = new List<KeyValuePair<Piece.Requirement, int>>();
            CraftingStation station = player.GetCurrentCraftingStation();
            Inventory inventory = player.GetInventory();
            foreach (Piece.Requirement requirement in recipe.m_resources)
            {
                if (requirement == null || !requirement.m_resItem
                    || (station != null && station.m_upgrader != requirement.m_upgraderResource) || (station == null && requirement.m_upgraderResource))
                {
                    continue;
                }
                int amount = requirement.GetAmount(quality) * multiplier;
                if (amount <= 0)
                {
                    continue;
                }
                ItemDrop.ItemData.SharedData shared = requirement.m_resItem.m_itemData.m_shared;
                int have = 0;
                for (int q = 1; q <= shared.m_maxQuality; q++)
                {
                    have = Mathf.Max(have, inventory.CountItems(shared.m_name, q));
                }
                if (recipe.m_requireOnlyOneIngredient)
                {
                    if (have >= amount)
                    {
                        result.Clear();
                        return result;
                    }
                    // The first ingredient the chests can complete is the one the game will use.
                    if (result.Count == 0 && have + CountRealChests(shared.m_name) >= amount)
                    {
                        result.Add(new KeyValuePair<Piece.Requirement, int>(requirement, amount - have));
                    }
                    continue;
                }
                if (have < amount)
                {
                    result.Add(new KeyValuePair<Piece.Requirement, int>(requirement, amount - have));
                }
            }
            return result;
        }

        private static int CountRealChests(string name)
        {
            int total = 0;
            foreach (Container chest in Sources())
            {
                if (!StorageSorter.InUseByOther(chest))
                {
                    total += chest.GetInventory().CountItems(name);
                }
            }
            return total;
        }

        // Moves up to amount pieces from chests this client owns into the inventory, nearest chests first.
        private static int Pull(Player player, string name, int amount)
        {
            Inventory inventory = player.GetInventory();
            int pulled = 0;
            foreach (Container chest in Sources())
            {
                if (pulled >= amount)
                {
                    break;
                }
                ZNetView view = StorageSorter.View(chest);
                if (view == null || !view.IsValid() || StorageSorter.InUseByOther(chest))
                {
                    continue;
                }
                if (!view.IsOwner())
                {
                    view.ClaimOwnership();
                }
                StorageSorter.Reload(chest);
                Inventory source = chest.GetInventory();
                foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(source.GetAllItems()))
                {
                    if (pulled >= amount)
                    {
                        break;
                    }
                    if (item.m_shared.m_name != name || item.m_worldLevel < Game.m_worldLevel)
                    {
                        continue;
                    }
                    int take = Mathf.Min(amount - pulled, item.m_stack);
                    ItemDrop.ItemData part = item.Clone();
                    part.m_stack = take;
                    if (!inventory.CanAddItem(part, take) || !inventory.AddItem(part))
                    {
                        return pulled;
                    }
                    source.RemoveItem(item, take);
                    pulled += take;
                }
            }
            s_counts.Clear();
            return pulled;
        }
    }
}
