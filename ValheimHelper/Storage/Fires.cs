using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ValheimHelper.Storage
{
    // Torches, sconces, braziers and fires of the base: how many there are, how much fuel they need, and
    // refilling all of them from the inventory at once. Fuel goes in through the game's own
    // Fireplace.AddFuel (RPC_AddFuelAmount to the owner) after the pieces are taken from the inventory,
    // so it works with fires a friend without the mod owns.
    internal static class Fires
    {
        internal sealed class KindStat
        {
            public string Name;
            public int Count;
            public float FullSeconds;
        }

        internal sealed class FuelStat
        {
            public string FuelName;
            public string DisplayName;
            public Texture2D IconTexture;
            public Rect IconUv;
            public int Count;
            public int Burning;
            public int Empty;
            public int Off;
            public int Needed;
            public int Available;
            public float SoonestOutSeconds = float.MaxValue;
            public readonly List<KindStat> Kinds = new List<KindStat>();
        }

        private sealed class Target
        {
            public Fireplace Fire;
            public ZNetView View;
            public float Fuel;
            public int Room;
            public int Planned;
        }

        internal static string LastReport = "";

        // Refillable fires built by players; dungeon torches and ever-burning decorations are left out.
        private static bool IsRefillable(Fireplace fire, out ZNetView view)
        {
            view = fire != null ? fire.GetComponent<ZNetView>() : null;
            if (fire == null || fire.m_fuelItem == null || fire.m_infiniteFuel || !fire.m_canRefill || fire.m_secPerFuel <= 0f || fire.m_maxFuel < 1f
                || view == null || !view.IsValid())
            {
                return false;
            }
            Piece piece = fire.GetComponent<Piece>() ?? fire.GetComponentInParent<Piece>();
            return piece != null && piece.IsPlacedByPlayer();
        }

        private static List<KeyValuePair<Fireplace, ZNetView>> Around(Vector3 center, float radius)
        {
            List<KeyValuePair<Fireplace, ZNetView>> result = new List<KeyValuePair<Fireplace, ZNetView>>();
            float radiusSqr = radius * radius;
            foreach (Fireplace fire in Object.FindObjectsByType<Fireplace>(FindObjectsSortMode.None))
            {
                if (fire != null && (fire.transform.position - center).sqrMagnitude <= radiusSqr && IsRefillable(fire, out ZNetView view))
                {
                    result.Add(new KeyValuePair<Fireplace, ZNetView>(fire, view));
                }
            }
            return result;
        }

        private static float Fuel(ZNetView view)
        {
            return Mathf.Max(0f, view.GetZDO().GetFloat(ZDOVars.s_fuel));
        }

        // How many more pieces the game's own refill would accept.
        private static int Room(Fireplace fire, float fuel)
        {
            return Mathf.Max(0, (int)fire.m_maxFuel - Mathf.CeilToInt(fuel));
        }

        internal static List<FuelStat> Scan(Player player, float radius)
        {
            Dictionary<string, FuelStat> stats = new Dictionary<string, FuelStat>();
            Dictionary<string, KindStat> kinds = new Dictionary<string, KindStat>();
            foreach (KeyValuePair<Fireplace, ZNetView> pair in Around(player.transform.position, radius))
            {
                Fireplace fire = pair.Key;
                string fuelName = fire.m_fuelItem.m_itemData.m_shared.m_name;
                if (!stats.TryGetValue(fuelName, out FuelStat stat))
                {
                    StorageItem info = StorageCatalog.Get(fuelName);
                    stat = new FuelStat
                    {
                        FuelName = fuelName,
                        DisplayName = info != null ? info.DisplayName : Localization.instance.Localize(fuelName),
                        IconTexture = info?.IconTexture,
                        IconUv = info != null ? info.IconUv : default(Rect),
                        Available = CountAvailable(player, fuelName),
                    };
                    stats[fuelName] = stat;
                }
                float fuel = Fuel(pair.Value);
                stat.Count++;
                if (pair.Value.GetZDO().GetInt(ZDOVars.s_state, 1) != 1)
                {
                    stat.Off++;
                }
                else if (fuel <= 0f)
                {
                    stat.Empty++;
                }
                else
                {
                    stat.Burning++;
                    stat.SoonestOutSeconds = Mathf.Min(stat.SoonestOutSeconds, fuel * fire.m_secPerFuel);
                }
                stat.Needed += Room(fire, fuel);

                string kindName = Localization.instance.Localize(fire.m_name);
                string key = fuelName + "|" + kindName;
                if (!kinds.TryGetValue(key, out KindStat kind))
                {
                    kind = new KindStat { Name = kindName, FullSeconds = fire.m_maxFuel * fire.m_secPerFuel };
                    kinds[key] = kind;
                    stat.Kinds.Add(kind);
                }
                kind.Count++;
            }
            List<FuelStat> result = new List<FuelStat>(stats.Values);
            result.Sort((a, b) => b.Count.CompareTo(a.Count));
            foreach (FuelStat stat in result)
            {
                stat.Kinds.Sort((a, b) => b.Count.CompareTo(a.Count));
            }
            return result;
        }

        // Fires that are on and have less than one piece of fuel left, for the overlay.
        internal static void LowFires(Player player, float radius, List<Fireplace> result)
        {
            result.Clear();
            foreach (KeyValuePair<Fireplace, ZNetView> pair in Around(player.transform.position, radius))
            {
                if (pair.Value.GetZDO().GetInt(ZDOVars.s_state, 1) == 1 && Fuel(pair.Value) < 1f)
                {
                    result.Add(pair.Key);
                }
            }
        }

        private static int CountAvailable(Player player, string fuelName)
        {
            int count = 0;
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems())
            {
                if (item.m_shared.m_name == fuelName && !StorageSorter.IsKept(player, item))
                {
                    count += item.m_stack;
                }
            }
            return count;
        }

        // fuelName null fills every kind of fire. Returns the message for the player.
        internal static string Fill(string fuelName)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return Lang.T("Зайди у світ.");
            }
            if (!StorageCatalog.EnsureBuilt())
            {
                return Lang.T("Дані предметів ще не готові, спробуй за мить.");
            }
            Dictionary<string, List<Target>> byFuel = new Dictionary<string, List<Target>>();
            foreach (KeyValuePair<Fireplace, ZNetView> pair in Around(player.transform.position, StorageSettings.FireRadius))
            {
                string name = pair.Key.m_fuelItem.m_itemData.m_shared.m_name;
                if (fuelName != null && name != fuelName)
                {
                    continue;
                }
                float fuel = Fuel(pair.Value);
                int room = Room(pair.Key, fuel);
                if (room <= 0)
                {
                    continue;
                }
                if (!byFuel.TryGetValue(name, out List<Target> targets))
                {
                    targets = new List<Target>();
                    byFuel[name] = targets;
                }
                targets.Add(new Target { Fire = pair.Key, View = pair.Value, Fuel = fuel, Room = room });
            }
            if (byFuel.Count == 0)
            {
                return Report(Lang.T("Усі факели й вогнища поруч уже повні."));
            }

            List<string> used = new List<string>();
            List<string> missing = new List<string>();
            int filled = 0;
            foreach (KeyValuePair<string, List<Target>> pair in byFuel)
            {
                List<Target> targets = pair.Value;
                int available = CountAvailable(player, pair.Key);
                int needed = 0;
                foreach (Target target in targets)
                {
                    needed += target.Room;
                }
                // Emptiest first, one piece at a time, so a short supply keeps as many fires burning as possible.
                for (int piece = 0; piece < available; piece++)
                {
                    Target best = null;
                    foreach (Target target in targets)
                    {
                        if (target.Planned < target.Room && (best == null || target.Fuel + target.Planned < best.Fuel + best.Planned))
                        {
                            best = target;
                        }
                    }
                    if (best == null)
                    {
                        break;
                    }
                    best.Planned++;
                }
                int given = 0;
                foreach (Target target in targets)
                {
                    if (target.Planned <= 0 || target.Fire == null || !target.View.IsValid())
                    {
                        continue;
                    }
                    int taken = Take(player, pair.Key, target.Planned);
                    if (taken <= 0)
                    {
                        break;
                    }
                    if (!target.View.HasOwner())
                    {
                        // The same as the game does when a fire is used.
                        target.View.ClaimOwnership();
                    }
                    target.Fire.AddFuel(taken);
                    given += taken;
                    filled++;
                }
                StorageItem info = StorageCatalog.Get(pair.Key);
                string display = info != null ? info.DisplayName : Localization.instance.Localize(pair.Key);
                if (given > 0)
                {
                    used.Add($"{display} ×{given}");
                }
                if (needed > given)
                {
                    missing.Add($"{display} ×{needed - given}");
                }
            }

            StringBuilder text = new StringBuilder();
            text.Append(filled > 0 ? Lang.F("Заправлено факелів і вогнищ: {0}. Витрачено: {1}.", filled, string.Join(", ", used.ToArray())) : Lang.T("Нічого не заправлено."));
            if (missing.Count > 0)
            {
                text.Append(Lang.T(" Не вистачило: ")).Append(string.Join(", ", missing.ToArray())).Append('.');
            }
            return Report(text.ToString());
        }

        // Removes up to amount pieces of fuel, smallest stacks first. Returns how many were removed.
        private static int Take(Player player, string fuelName, int amount)
        {
            Inventory inventory = player.GetInventory();
            int taken = 0;
            while (taken < amount)
            {
                ItemDrop.ItemData smallest = null;
                foreach (ItemDrop.ItemData item in inventory.GetAllItems())
                {
                    if (item.m_shared.m_name == fuelName && !StorageSorter.IsKept(player, item) && (smallest == null || item.m_stack < smallest.m_stack))
                    {
                        smallest = item;
                    }
                }
                if (smallest == null)
                {
                    break;
                }
                int part = Mathf.Min(amount - taken, smallest.m_stack);
                inventory.RemoveItem(smallest, part);
                taken += part;
            }
            return taken;
        }

        internal static string FormatDuration(float seconds)
        {
            int minutes = Mathf.Max(1, Mathf.RoundToInt(seconds / 60f));
            string real = minutes < 60 ? Lang.F("{0} хв", minutes) : (minutes % 60 == 0 ? Lang.F("{0} год", minutes / 60) : Lang.F("{0} год {1} хв", minutes / 60, minutes % 60));
            long dayLength = EnvMan.instance != null ? EnvMan.instance.m_dayLengthSec : 1800L;
            float days = seconds / Mathf.Max(1f, dayLength);
            return days >= 1f ? Lang.F("{0} (≈{1:0.#} ігрових дн.)", real, days) : real;
        }

        private static string Report(string message)
        {
            LastReport = message;
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
