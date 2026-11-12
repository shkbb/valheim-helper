using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper.Storage
{
    // Picking up items lying around from the menu, as if walking up to each and pressing E: the game's own
    // Humanoid.Pickup does it, with its checks for room and quest items. Items owned by another client are asked for
    // first (the same RequestOwn the game's auto pickup uses) and taken once they arrive.
    internal static class RemotePickup
    {
        internal const float MinRadius = 5f;
        internal const float MaxRadius = 60f;
        private const float WaitSeconds = 3f;
        private const int PerFrame = 20;

        internal sealed class Group
        {
            public string Name;
            public string SharedName;
            public int Amount;
            public int Piles;
            public float Nearest;
            public Texture2D Icon;
            public Rect IconUv;
        }

        private static readonly System.Reflection.FieldInfo s_instances = AccessTools.Field(typeof(ItemDrop), "s_instances");
        private static readonly List<ItemDrop> s_pending = new List<ItemDrop>();
        private static float s_until;
        private static int s_taken;
        private static int s_takenAmount;
        private static int s_noRoom;

        internal static float Radius = 20f;
        internal static bool Busy => s_pending.Count > 0;

        private static List<ItemDrop> Instances()
        {
            return s_instances?.GetValue(null) as List<ItemDrop> ?? new List<ItemDrop>();
        }

        private static bool Pickable(ItemDrop item, Vector3 center, float radius)
        {
            if (item == null || item.m_itemData == null || item.IsPiece() || item.m_itemData.m_shared == null)
            {
                return false;
            }
            ZNetView view = item.GetComponent<ZNetView>();
            if (view == null || !view.IsValid())
            {
                return false;
            }
            return (item.transform.position - center).sqrMagnitude <= radius * radius;
        }

        // What lies around, grouped by item, nearest first.
        internal static List<Group> Around(Player player)
        {
            Dictionary<string, Group> groups = new Dictionary<string, Group>();
            Vector3 center = player.transform.position;
            foreach (ItemDrop item in Instances())
            {
                if (!Pickable(item, center, Radius))
                {
                    continue;
                }
                ItemDrop.ItemData data = item.m_itemData;
                string key = data.m_shared.m_name + "|" + data.m_quality;
                float distance = Vector3.Distance(item.transform.position, center);
                if (!groups.TryGetValue(key, out Group group))
                {
                    group = new Group
                    {
                        SharedName = data.m_shared.m_name,
                        Name = Localization.instance.Localize(data.m_shared.m_name) + (data.m_quality > 1 ? $" ({data.m_quality}★)" : ""),
                        Nearest = distance,
                    };
                    Sprite icon = data.GetIcon();
                    if (icon != null && icon.texture != null)
                    {
                        Rect rect = icon.textureRect;
                        group.Icon = icon.texture;
                        group.IconUv = new Rect(rect.x / icon.texture.width, rect.y / icon.texture.height, rect.width / icon.texture.width, rect.height / icon.texture.height);
                    }
                    groups[key] = group;
                }
                group.Amount += Mathf.Max(1, data.m_stack);
                group.Piles++;
                group.Nearest = Mathf.Min(group.Nearest, distance);
            }
            List<Group> result = new List<Group>(groups.Values);
            result.Sort((a, b) => a.Nearest.CompareTo(b.Nearest));
            return result;
        }

        // sharedName null: everything in the radius.
        internal static string Start(Player player, string sharedName)
        {
            if (player == null)
            {
                return Lang.T("Зайди у світ.");
            }
            s_pending.Clear();
            s_taken = 0;
            s_takenAmount = 0;
            s_noRoom = 0;
            Vector3 center = player.transform.position;
            foreach (ItemDrop item in Instances())
            {
                if (Pickable(item, center, Radius) && (sharedName == null || item.m_itemData.m_shared.m_name == sharedName))
                {
                    s_pending.Add(item);
                }
            }
            if (s_pending.Count == 0)
            {
                return Lang.T("Поруч нічого не лежить.");
            }
            s_pending.Sort((a, b) => (a.transform.position - center).sqrMagnitude.CompareTo((b.transform.position - center).sqrMagnitude));
            s_until = Time.unscaledTime + WaitSeconds;
            return Lang.T("Підбираю…");
        }

        internal static void Tick()
        {
            if (s_pending.Count == 0)
            {
                return;
            }
            Player player = Player.m_localPlayer;
            if (player == null || player.IsDead() || player.IsTeleporting())
            {
                s_pending.Clear();
                return;
            }
            Inventory inventory = player.GetInventory();
            int done = 0;
            for (int i = s_pending.Count - 1; i >= 0 && done < PerFrame; i--)
            {
                ItemDrop item = s_pending[i];
                ZNetView view = item != null ? item.GetComponent<ZNetView>() : null;
                if (item == null || view == null || !view.IsValid())
                {
                    s_pending.RemoveAt(i);
                    continue;
                }
                if (!item.CanPickup(false))
                {
                    // Another client owns it: ask once in a while, take it when it comes.
                    item.RequestOwn();
                    continue;
                }
                done++;
                item.Load();
                s_pending.RemoveAt(i);
                if (!inventory.CanAddItem(item.m_itemData) || item.m_itemData.GetWeight() + inventory.GetTotalWeight() > player.GetMaxCarryWeight())
                {
                    s_noRoom++;
                    continue;
                }
                int stack = Mathf.Max(1, item.m_itemData.m_stack);
                if (player.Pickup(item.gameObject, false, false))
                {
                    s_taken++;
                    s_takenAmount += stack;
                }
            }
            if (s_pending.Count > 0 && Time.unscaledTime < s_until)
            {
                return;
            }
            int left = s_pending.Count;
            s_pending.Clear();
            string message = s_taken > 0 ? Lang.F("Підібрано: {0} шт. ({1} купок).", s_takenAmount, s_taken) : Lang.T("Нічого не підібрано.");
            if (s_noRoom > 0)
            {
                message += Lang.F(" Не влізло (місце або вага): {0} купок.", s_noRoom);
            }
            if (left > 0)
            {
                message += Lang.F(" Не віддав власник (інший гравець поруч?): {0}.", left);
            }
#if EDITION_FULL
            PlayerTools.Report(message);
#else
            if (!string.IsNullOrEmpty(message))
            {
                Log.Info(message);
                Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, message);
            }
#endif
        }
    }
}
