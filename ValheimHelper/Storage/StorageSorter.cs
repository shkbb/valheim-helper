using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper.Storage
{
    // "Скласти все поруч": moves items from the player's inventory into the right chests around.
    //
    // Chests are taken over exactly like the game's own "place stacks" does it: RPC_RequestStack goes to whoever
    // owns the chest (also a friend without the mod), the owner hands the chest over unless someone has it open,
    // and only after the chest data has arrived and this client owns it are the items moved. Items are moved
    // with the game's inventory methods, so they stay ordinary items (nothing is marked as cheated).
    internal static class StorageSorter
    {
        internal const string TagKey = "ValheimHelper_chestTag";
        private const float ResponseTimeoutSec = 4f;

        private static readonly AccessTools.FieldRef<Container, ZNetView> s_nview = AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");
        private static readonly MethodInfo s_load = AccessTools.Method(typeof(Container), "Load");
        private static readonly AccessTools.FieldRef<InventoryGui, ItemDrop.ItemData> s_dragItem = AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_dragItem");

        private sealed class Planned
        {
            public ItemDrop.ItemData Item;
            public int Amount;
        }

        private sealed class Job
        {
            public Container Container;
            public readonly List<Planned> Items = new List<Planned>();
            // Set for a chest data change (label, memory) instead of moving items.
            public string ZdoKey;
            public string ZdoValue;
            // Only takes the chest over (crafting from chests uses it right after).
            public bool OwnershipOnly;
            public bool Requested;
            public float RequestedAt;
            public bool Answered;
            public bool Granted;
            public bool Done;
        }

        private sealed class Run
        {
            public readonly List<Job> Jobs = new List<Job>();
            public int Eligible;
            public int Moved;
            public readonly HashSet<Container> Filled = new HashSet<Container>();
            public int Busy;
            // Empty chests planned for new shelves.
            public readonly HashSet<Container> Shelves = new HashSet<Container>();
            public readonly Dictionary<string, int> LeftNoChest = new Dictionary<string, int>();
            public readonly Dictionary<string, int> LeftNoRoom = new Dictionary<string, int>();
            public bool Silent;
        }

        private static Run s_run;
        private static readonly Dictionary<Container, Job> s_waiting = new Dictionary<Container, Job>();
        private static readonly List<Container> s_found = new List<Container>();
        private static float s_foundAt = -10f;

        internal static bool Busy => s_run != null && !s_run.Silent;
        internal static string LastReport = "";
        internal static float LastReportAt = -1f;

        // ---------------------------------------------------------------- chests

        internal static ZNetView View(Container container)
        {
            return container != null ? s_nview(container) : null;
        }

        internal static string GetTag(Container container)
        {
            ZNetView view = View(container);
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            return zdo != null ? zdo.GetString(TagKey, "") : "";
        }

        // Loaded containers, refreshed at most twice a second.
        internal static List<Container> LoadedContainers()
        {
            if (Time.unscaledTime - s_foundAt > 0.5f)
            {
                s_foundAt = Time.unscaledTime;
                s_found.Clear();
                s_found.AddRange(UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None));
            }
            s_found.RemoveAll(c => c == null);
            return s_found;
        }

        // Player-built storage the local player may open. Carts, ships, graves, dungeon chests and the
        // obliterator are never touched.
        internal static bool IsStorageChest(Container container)
        {
            if (container == null || container.GetInventory() == null)
            {
                return false;
            }
            ZNetView view = View(container);
            if (view == null || !view.IsValid())
            {
                return false;
            }
            if (container.m_wagon != null || container.GetComponentInParent<Vagon>() != null || container.GetComponentInParent<Ship>() != null
                || container.GetComponent<TombStone>() != null || container.GetComponentInParent<Incinerator>() != null
                || container.transform.root.GetComponentInChildren<Incinerator>(true) != null)
            {
                return false;
            }
            Piece piece = container.GetComponent<Piece>() ?? container.GetComponentInParent<Piece>();
            if (piece == null || !piece.IsPlacedByPlayer())
            {
                return false;
            }
            long playerId = Game.instance != null ? Game.instance.GetPlayerProfile().GetPlayerID() : 0L;
            switch (container.m_privacy)
            {
                case Container.PrivacySetting.Private when piece.GetCreator() != playerId:
                case Container.PrivacySetting.Group:
                    return false;
            }
            return !container.m_checkGuardStone || PrivateArea.CheckAccess(container.transform.position, 0f, flash: false);
        }

        internal static List<Container> ChestsAround(Vector3 center, float radius)
        {
            List<Container> result = new List<Container>();
            float radiusSqr = radius * radius;
            foreach (Container container in LoadedContainers())
            {
                if ((container.transform.position - center).sqrMagnitude <= radiusSqr && IsStorageChest(container))
                {
                    result.Add(container);
                }
            }
            result.Sort((a, b) => (a.transform.position - center).sqrMagnitude.CompareTo((b.transform.position - center).sqrMagnitude));
            return result;
        }

        internal static Container HoveredChest()
        {
            Player player = Player.m_localPlayer;
            GameObject hover = player != null ? player.GetHoverObject() : null;
            Container container = hover != null ? hover.GetComponentInParent<Container>() : null;
            return container != null && IsStorageChest(container) ? container : null;
        }

        // ---------------------------------------------------------------- what stays in the inventory

        // Items marked "не чіпати", plus what is worn or held, quest items and the item being dragged.
        internal static bool IsKept(Player player, ItemDrop.ItemData item)
        {
            if (item?.m_shared == null || item.m_equipped || player.IsItemEquiped(item) || item.m_shared.m_questItem)
            {
                return true;
            }
            if (InventoryGui.instance != null && ReferenceEquals(s_dragItem(InventoryGui.instance), item))
            {
                return true;
            }
            StorageItem info = StorageCatalog.Get(item);
            return info == null || StorageSettings.IsItemKept(info.Prefab);
        }

        // ---------------------------------------------------------------- planning

        private sealed class Snapshot
        {
            public Container Container;
            public float Distance;
            public string TagGroup;
            public int TagBiome = -1;
            public int EmptySlots;
            // Pieces really lying in the chest, by shared name (planned ones are not counted: they do not make a home).
            public readonly Dictionary<string, int> RealCount = new Dictionary<string, int>();
            public readonly Dictionary<string, int> FreeInStacks = new Dictionary<string, int>();
            // What the chest holds, including kinds planned in this run, so similar new items end up together.
            public readonly List<ChestKind> Kinds = new List<ChestKind>();
            private readonly Dictionary<string, int> m_kindIndex = new Dictionary<string, int>();

            // Nothing lies here, nothing is planned here, no label and no memory: a place for a new shelf.
            public bool IsFree => RealCount.Count == 0 && Kinds.Count == 0 && TagGroup == null && TagBiome < 0 && EmptySlots > 0;

            // An item remembered by hand for this chest counts as its content, even when none of it lies here now.
            public void Remember(string name)
            {
                if (!m_kindIndex.ContainsKey(name))
                {
                    AddSlots(name, 1);
                }
            }

            public Snapshot(Container container, Vector3 from)
            {
                Container = container;
                Distance = Vector3.Distance(container.transform.position, from);
                StorageGroup tag = StorageCatalog.FindGroup(GetTag(container));
                if (tag != null && tag.IsBiome)
                {
                    TagBiome = Array.IndexOf(StorageCatalog.Biomes, tag);
                }
                else if (tag != null)
                {
                    TagGroup = tag.Id;
                }
                Inventory inventory = container.GetInventory();
                EmptySlots = inventory.GetWidth() * inventory.GetHeight();
                foreach (ItemDrop.ItemData item in inventory.GetAllItems())
                {
                    EmptySlots--;
                    string name = item.m_shared.m_name;
                    RealCount[name] = Get(RealCount, name) + item.m_stack;
                    FreeInStacks[name] = Get(FreeInStacks, name) + Mathf.Max(0, item.m_shared.m_maxStackSize - item.m_stack);
                    AddSlots(name, 1);
                }
                EmptySlots = Mathf.Max(0, EmptySlots);
            }

            private void AddSlots(string name, int slots)
            {
                if (slots <= 0)
                {
                    return;
                }
                if (m_kindIndex.TryGetValue(name, out int index))
                {
                    Kinds[index] = new ChestKind(Kinds[index].Traits, Kinds[index].Slots + slots);
                    return;
                }
                StorageItem info = StorageCatalog.Get(name);
                m_kindIndex[name] = Kinds.Count;
                Kinds.Add(new ChestKind(info?.Traits ?? new ItemTraits { Id = name }, slots));
            }

            public int Capacity(ItemDrop.ItemData item)
            {
                return Get(FreeInStacks, item.m_shared.m_name) + EmptySlots * Mathf.Max(1, item.m_shared.m_maxStackSize);
            }

            public void Reserve(ItemDrop.ItemData item, int amount)
            {
                string name = item.m_shared.m_name;
                int stackSize = Mathf.Max(1, item.m_shared.m_maxStackSize);
                int fromStacks = Mathf.Min(amount, Get(FreeInStacks, name));
                int rest = amount - fromStacks;
                int newSlots = (rest + stackSize - 1) / stackSize;
                EmptySlots -= newSlots;
                FreeInStacks[name] = Get(FreeInStacks, name) - fromStacks + (newSlots * stackSize - rest);
                AddSlots(name, Mathf.Max(newSlots, m_kindIndex.ContainsKey(name) ? 0 : 1));
            }

            private static int Get(Dictionary<string, int> map, string key)
            {
                return map.TryGetValue(key, out int value) ? value : 0;
            }
        }

        // The rule behind a score of SortRules.Score, for the log.
        private static string Reason(float score)
        {
            if (score >= 1200f)
            {
                return "put there by hand last time";
            }
            if (score >= 1000f)
            {
                return "already lies there";
            }
            if (score >= 900f)
            {
                return "the chest's purpose is its group";
            }
            if (score >= 850f)
            {
                return "the chest's purpose is its biome";
            }
            return $"fits the contents (affinity {(score - 300f) / 500f:0.00})";
        }

        // Higher is better, 0 means "not this chest". The rules themselves are in SortRules.
        private static float Score(Snapshot chest, ItemDrop.ItemData item, StorageItem info, bool remembered, int totalOfItem, bool hasHome)
        {
            chest.RealCount.TryGetValue(item.m_shared.m_name, out int here);
            return SortRules.Score(info?.Traits, chest.Kinds, chest.TagGroup, chest.TagBiome, remembered, here, totalOfItem, hasHome);
        }

        // Part of an inventory stack no chest could take in the first pass.
        private sealed class Leftover
        {
            public ItemDrop.ItemData Item;
            public StorageItem Info;
            public int Amount;
            // The best chest for the item, full already; null when no chest takes the item at all.
            public Snapshot Home;
        }

        private static Spot ToSpot(Vector3 position)
        {
            return new Spot(position.x, position.y, position.z);
        }

        private static Job JobFor(Dictionary<Container, Job> jobs, Container container)
        {
            if (!jobs.TryGetValue(container, out Job job))
            {
                job = new Job { Container = container };
                jobs[container] = job;
            }
            return job;
        }

        // Starts moving the inventory into chests around. Returns the message for the player.
        internal static string StartStash()
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return Lang.T("Зайди у світ.");
            }
            if (s_run != null && !s_run.Silent)
            {
                return Lang.T("Уже розкладаю речі, зачекай секунду.");
            }
            if (player.IsTeleporting() || player.IsDead())
            {
                return null;
            }
            if (!StorageCatalog.EnsureBuilt())
            {
                return Lang.T("Дані предметів ще не готові, спробуй за мить.");
            }
            List<Container> chests = ChestsAround(player.transform.position, StorageSettings.Radius);
            if (chests.Count == 0)
            {
                return Report(Lang.F("У радіусі {0:0} м немає скринь, у які можна скласти речі.", StorageSettings.Radius));
            }

            List<Snapshot> snapshots = new List<Snapshot>();
            List<Dictionary<string, long>> memories = new List<Dictionary<string, long>>();
            foreach (Container chest in chests)
            {
                snapshots.Add(new Snapshot(chest, player.transform.position));
                memories.Add(StorageSettings.RememberHandPlacement ? ChestMemory.Read(chest) : new Dictionary<string, long>());
            }
            // Where each remembered item was put by hand most recently. That chest is the item's home even when it is
            // empty, so it also attracts similar items and is not used for new shelves.
            Dictionary<string, int> latest = new Dictionary<string, int>();
            foreach (Dictionary<string, long> memory in memories)
            {
                foreach (string name in memory.Keys)
                {
                    if (!latest.ContainsKey(name))
                    {
                        int index = ChestMemoryRules.Latest(memories, name);
                        latest[name] = index;
                        snapshots[index].Remember(name);
                    }
                }
            }

            Run run = new Run();
            Dictionary<Container, Job> jobs = new Dictionary<Container, Job>();
            List<Leftover> leftovers = new List<Leftover>();
            Inventory inventory = player.GetInventory();
            foreach (ItemDrop.ItemData item in inventory.GetAllItemsInGridOrder())
            {
                if (IsKept(player, item))
                {
                    continue;
                }
                run.Eligible++;
                StorageItem info = StorageCatalog.Get(item);
                string name = item.m_shared.m_name;
                int total = 0;
                foreach (Snapshot chest in snapshots)
                {
                    total += chest.RealCount.TryGetValue(name, out int count) ? count : 0;
                }
                int remembered = latest.TryGetValue(name, out int index) ? index : -1;
                List<KeyValuePair<float, Snapshot>> ranked = new List<KeyValuePair<float, Snapshot>>();
                for (int i = 0; i < snapshots.Count; i++)
                {
                    float score = Score(snapshots[i], item, info, i == remembered, total, total > 0 || remembered >= 0);
                    if (score > 0f)
                    {
                        ranked.Add(new KeyValuePair<float, Snapshot>(score, snapshots[i]));
                    }
                }
                ranked.Sort((a, b) => a.Key != b.Key ? b.Key.CompareTo(a.Key) : a.Value.Distance.CompareTo(b.Value.Distance));
                if (ranked.Count > 0)
                {
                    // why an item went where it went, for questions like "why is the crossbow in the metal chest?"
                    Vector3 at = ranked[0].Value.Container.transform.position;
                    Log.Info($"Stash: {item.m_shared.m_name} x{item.m_stack} -> chest at {at.x:0},{at.y:0},{at.z:0}: {Reason(ranked[0].Key)}");
                }

                int remaining = item.m_stack;
                foreach (KeyValuePair<float, Snapshot> candidate in ranked)
                {
                    int part = Mathf.Min(remaining, candidate.Value.Capacity(item));
                    if (part <= 0)
                    {
                        continue;
                    }
                    candidate.Value.Reserve(item, part);
                    JobFor(jobs, candidate.Value.Container).Items.Add(new Planned { Item = item, Amount = part });
                    remaining -= part;
                    if (remaining <= 0)
                    {
                        break;
                    }
                }
                if (remaining > 0)
                {
                    leftovers.Add(new Leftover { Item = item, Info = info, Amount = remaining, Home = ranked.Count > 0 ? ranked[0].Value : null });
                }
            }

            if (StorageSettings.UseEmptyChests && leftovers.Count > 0)
            {
                PlanEmptyChests(run, jobs, snapshots, leftovers, player.transform.position);
            }
            foreach (Leftover leftover in leftovers)
            {
                if (leftover.Amount <= 0)
                {
                    continue;
                }
                string display = leftover.Info != null ? leftover.Info.DisplayName : Localization.instance.Localize(leftover.Item.m_shared.m_name);
                Dictionary<string, int> left = leftover.Home != null ? run.LeftNoRoom : run.LeftNoChest;
                left[display] = (left.TryGetValue(display, out int already) ? already : 0) + leftover.Amount;
            }

            foreach (Container chest in chests)
            {
                if (jobs.TryGetValue(chest, out Job job))
                {
                    run.Jobs.Add(job);
                }
            }
            if (run.Jobs.Count == 0)
            {
                return Report(FinishText(run));
            }
            if (s_run != null)
            {
                // Label changes still waiting for their chest continue as part of this run.
                run.Jobs.AddRange(s_run.Jobs.FindAll(job => !job.Done));
            }
            s_run = run;
            Tick();
            return s_run != null ? Lang.F("Розкладаю речі по скринях ({0})…", run.Jobs.Count) : null;
        }

        // Second pass: what no chest took goes to empty chests as new shelves (see ShelfPlanner).
        private static void PlanEmptyChests(Run run, Dictionary<Container, Job> jobs, List<Snapshot> snapshots, List<Leftover> leftovers, Vector3 from)
        {
            List<Snapshot> empty = snapshots.FindAll(chest => chest.IsFree);
            if (empty.Count == 0)
            {
                return;
            }
            List<ShelfPlanner.EmptyChest> spots = new List<ShelfPlanner.EmptyChest>();
            foreach (Snapshot chest in empty)
            {
                spots.Add(new ShelfPlanner.EmptyChest { Slots = chest.EmptySlots, Position = ToSpot(chest.Container.transform.position) });
            }
            // One entry per kind of item; its stacks share the chest slots it gets.
            List<ShelfPlanner.Entry> entries = new List<ShelfPlanner.Entry>();
            List<List<Leftover>> parts = new List<List<Leftover>>();
            Dictionary<string, int> entryOf = new Dictionary<string, int>();
            foreach (Leftover leftover in leftovers)
            {
                string name = leftover.Item.m_shared.m_name;
                if (!entryOf.TryGetValue(name, out int index))
                {
                    index = entries.Count;
                    entryOf[name] = index;
                    entries.Add(new ShelfPlanner.Entry
                    {
                        Traits = leftover.Info?.Traits ?? new ItemTraits { Id = name },
                        HasHome = leftover.Home != null,
                        Home = leftover.Home != null ? ToSpot(leftover.Home.Container.transform.position) : default,
                    });
                    parts.Add(new List<Leftover>());
                }
                parts[index].Add(leftover);
            }
            for (int i = 0; i < entries.Count; i++)
            {
                int amount = 0;
                foreach (Leftover part in parts[i])
                {
                    amount += part.Amount;
                }
                int stackSize = Mathf.Max(1, parts[i][0].Item.m_shared.m_maxStackSize);
                entries[i].Slots = (amount + stackSize - 1) / stackSize;
            }

            foreach (ShelfPlanner.Placement placement in ShelfPlanner.Plan(entries, spots, ToSpot(from)))
            {
                Snapshot chest = empty[placement.Chest];
                List<Leftover> stacks = parts[placement.Entry];
                int room = placement.Slots * Mathf.Max(1, stacks[0].Item.m_shared.m_maxStackSize);
                Vector3 at = chest.Container.transform.position;
                Log.Info($"Stash: {stacks[0].Item.m_shared.m_name} -> empty chest at {at.x:0},{at.y:0},{at.z:0}: a new shelf ({placement.Slots} slots)");
                foreach (Leftover part in stacks)
                {
                    int amount = Mathf.Min(Mathf.Min(part.Amount, room), chest.Capacity(part.Item));
                    if (amount <= 0)
                    {
                        continue;
                    }
                    chest.Reserve(part.Item, amount);
                    JobFor(jobs, chest.Container).Items.Add(new Planned { Item = part.Item, Amount = amount });
                    part.Amount -= amount;
                    room -= amount;
                    run.Shelves.Add(chest.Container);
                    if (room <= 0)
                    {
                        break;
                    }
                }
            }
        }

        // ---------------------------------------------------------------- chest labels

        internal static string SetTag(Container container, string tagId)
        {
            if (container == null || !IsStorageChest(container))
            {
                return Lang.T("Ця скриня недоступна.");
            }
            SetZdoString(container, TagKey, tagId ?? "");
            StorageGroup group = StorageCatalog.FindGroup(tagId);
            return group != null ? Lang.T("Призначення скрині: ") + group.DisplayName : Lang.T("Скриня тепер для будь-чого.");
        }

        // Writes a value into the chest's data once the chest is handed over (only the owner may change it).
        internal static void SetZdoString(Container container, string key, string value)
        {
            if (s_run == null)
            {
                s_run = new Run { Silent = true };
            }
            s_run.Jobs.Add(new Job { Container = container, ZdoKey = key, ZdoValue = value });
            Tick();
        }

        // Asks the owners to hand these chests over, the same request as "place stacks". Chests this client
        // already owns or has already asked for are skipped.
        internal static void RequestOwnership(IEnumerable<Container> containers)
        {
            bool added = false;
            foreach (Container container in containers)
            {
                ZNetView view = View(container);
                if (view == null || !view.IsValid() || view.IsOwner() || IsRequested(container))
                {
                    continue;
                }
                if (s_run == null)
                {
                    s_run = new Run { Silent = true };
                }
                s_run.Jobs.Add(new Job { Container = container, OwnershipOnly = true });
                added = true;
            }
            if (added)
            {
                Tick();
            }
        }

        internal static bool IsRequested(Container container)
        {
            if (s_run == null || container == null)
            {
                return false;
            }
            foreach (Job job in s_run.Jobs)
            {
                if (!job.Done && job.Container == container)
                {
                    return true;
                }
            }
            return false;
        }

        // Someone else has the chest open, so it cannot be handed over.
        internal static bool InUseByOther(Container container)
        {
            ZNetView view = View(container);
            return view != null && view.IsValid() && !view.IsOwner() && view.GetZDO().GetInt(ZDOVars.s_inUse) == 1;
        }

        internal static void Reload(Container container)
        {
            try
            {
                s_load?.Invoke(container, null);
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        // ---------------------------------------------------------------- execution

        // Called every frame. All chests are requested at once; each one is filled as soon as it is handed over.
        internal static void Tick()
        {
            StorageSettings.SaveIfDirty();
            ChestMemory.Tick();
            Run run = s_run;
            if (run == null)
            {
                return;
            }
            Player player = Player.m_localPlayer;
            if (player == null || ZNet.instance == null || Game.instance == null)
            {
                s_run = null;
                s_waiting.Clear();
                return;
            }
            bool pending = false;
            foreach (Job job in run.Jobs)
            {
                if (job.Done)
                {
                    continue;
                }
                ZNetView view = View(job.Container);
                if (job.Container == null || view == null || !view.IsValid())
                {
                    Finish(job);
                    continue;
                }
                if (!job.Requested)
                {
                    if (s_waiting.ContainsKey(job.Container))
                    {
                        // Another request for the same chest (a label change) is still out.
                        pending = true;
                        continue;
                    }
                    job.Requested = true;
                    job.RequestedAt = Time.unscaledTime;
                    s_waiting[job.Container] = job;
                    // A chest this client already owns answers right inside this call.
                    view.InvokeRPC("RPC_RequestStack", Game.instance.GetPlayerProfile().GetPlayerID());
                }
                bool counts = job.ZdoKey == null && !job.OwnershipOnly;
                if (job.Answered && !job.Granted)
                {
                    run.Busy += counts ? 1 : 0;
                    Finish(job);
                    continue;
                }
                // The chest data, with this client as the new owner, arrives right before or after the answer.
                if (job.Answered && view.IsOwner())
                {
                    if (job.ZdoKey != null)
                    {
                        view.GetZDO().Set(job.ZdoKey, job.ZdoValue);
                    }
                    else if (!job.OwnershipOnly)
                    {
                        Fill(run, job, player);
                    }
                    Finish(job);
                    continue;
                }
                if (Time.unscaledTime - job.RequestedAt > ResponseTimeoutSec)
                {
                    run.Busy += counts ? 1 : 0;
                    Finish(job);
                    continue;
                }
                pending = true;
            }
            if (!pending)
            {
                s_run = null;
                if (!run.Silent)
                {
                    Report(FinishText(run));
                }
            }
        }

        private static void Finish(Job job)
        {
            job.Done = true;
            if (job.Container != null)
            {
                s_waiting.Remove(job.Container);
            }
        }

        // The routed answer to our RPC_RequestStack; the game's own handler would run "place stacks".
        internal static bool OnStackResponse(Container container, bool granted)
        {
            if (container == null || !s_waiting.TryGetValue(container, out Job job))
            {
                return false;
            }
            s_waiting.Remove(container);
            job.Answered = true;
            job.Granted = granted;
            return true;
        }

        private static void Fill(Run run, Job job, Player player)
        {
            try
            {
                // Picks up changes the previous owner made right before handing the chest over.
                s_load?.Invoke(job.Container, null);
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
            Inventory from = player.GetInventory();
            Inventory to = job.Container.GetInventory();
            int movedHere = 0;
            foreach (Planned planned in job.Items)
            {
                if (!from.ContainsItem(planned.Item) || IsKept(player, planned.Item))
                {
                    continue;
                }
                int moved = SlotPlanner.Deposit(from, to, planned.Item, Mathf.Min(planned.Amount, planned.Item.m_stack));
                movedHere += moved;
                if (moved < planned.Amount && from.ContainsItem(planned.Item))
                {
                    StorageItem info = StorageCatalog.Get(planned.Item);
                    string display = info != null ? info.DisplayName : planned.Item.m_shared.m_name;
                    run.LeftNoRoom[display] = (run.LeftNoRoom.TryGetValue(display, out int already) ? already : 0) + planned.Amount - moved;
                }
            }
            if (movedHere > 0)
            {
                run.Moved += movedHere;
                run.Filled.Add(job.Container);
                if (InventoryGui.instance != null)
                {
                    InventoryGui.instance.m_moveItemEffects.Create(job.Container.transform.position, Quaternion.identity);
                }
            }
        }

        private static string FinishText(Run run)
        {
            StringBuilder text = new StringBuilder();
            if (run.Eligible == 0)
            {
                return Lang.T("В інвентарі немає нічого, що треба складати: решта захищена позначкою «Не чіпати» («База»).");
            }
            text.Append(run.Moved > 0 ? Lang.F("Складено речей: {0} (скринь: {1}).", run.Moved, run.Filled.Count) : Lang.T("Нічого не складено."));
            int shelves = 0;
            foreach (Container chest in run.Shelves)
            {
                shelves += run.Filled.Contains(chest) ? 1 : 0;
            }
            if (shelves > 0)
            {
                text.Append(Lang.F(" Нових полиць у порожніх скринях: {0}.", shelves));
            }
            if (run.Busy > 0)
            {
                text.Append(Lang.F(" Пропущено скринь, відкритих іншими гравцями: {0}.", run.Busy));
            }
            AppendLeft(text, Lang.T("\nНемає підхожої скрині: "), run.LeftNoChest);
            AppendLeft(text, Lang.T("\nНе влізло: "), run.LeftNoRoom);
            return text.ToString();
        }

        private static void AppendLeft(StringBuilder text, string title, Dictionary<string, int> left)
        {
            if (left.Count == 0)
            {
                return;
            }
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> pair in left)
            {
                parts.Add($"{pair.Key} ×{pair.Value}");
                if (parts.Count == 8)
                {
                    parts.Add("…");
                    break;
                }
            }
            text.Append(title).Append(string.Join(", ", parts.ToArray()));
        }

        private static string Report(string message)
        {
            LastReport = message;
            LastReportAt = Time.unscaledTime;
            Log.Info(message);
            Player player = Player.m_localPlayer;
            if (player != null)
            {
                player.Message(MessageHud.MessageType.TopLeft, message);
            }
            return null;
        }
    }
}
