using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper
{
    // Which friends have the mod (all editions: the statistics compare with them), and, in the full edition only,
    // sending items straight into a friend's inventory. The game keeps every inventory on its owner's client,
    // so the friend's Valheim Helper receives the request and adds the item itself, exactly like the local
    // "Дати" button (cheated: false, the same item as a crafted or picked up one). Friends without the mod
    // never answer the ping, and the menu tells so instead of sending anything.
    internal static class Gifts
    {
        internal enum ModState
        {
            Checking,
            HasMod,
            NoMod,
            OtherVersion,
        }

        private sealed class PendingGift
        {
            public long Target;
            public string Name;
            public float SentAt;
        }

        private const string RpcPing = "ValheimHelper_Ping";
        private const string RpcPong = "ValheimHelper_Pong";
        private const string RpcGift = "ValheimHelper_Gift";
        private const string RpcGiftResult = "ValheimHelper_GiftResult";
        private const int ProtocolVersion = 3;
        private const float PingTimeoutSec = 4f;
        private const float PingIntervalSec = 15f;
        private const float GiftTimeoutSec = 6f;
        // Inventory.Load marks items above this damage as cheated, so they are never sent or accepted.
        private const float MaxSafeItemDamage = 10000f;

        internal static bool AcceptGifts = true;
        internal static string LastResult = "";
        internal static float LastResultAt = -1f;

        private static readonly AccessTools.FieldRef<ZRoutedRpc, long> s_rpcOwnId = AccessTools.FieldRefAccess<ZRoutedRpc, long>("m_id");
        private static readonly Dictionary<long, int> s_modPeers = new Dictionary<long, int>();
        private static readonly List<PendingGift> s_pending = new List<PendingGift>();
        private static ZRoutedRpc s_rpc;
        private static float s_pingAt = -100f;

        internal static void Tick()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null)
            {
                if (s_rpc != null)
                {
                    s_rpc = null;
                    s_modPeers.Clear();
                    s_pending.Clear();
                }
                return;
            }
            if (!ReferenceEquals(rpc, s_rpc))
            {
                rpc.Register<int>(RpcPing, OnPing);
                rpc.Register<int>(RpcPong, OnPong);
#if EDITION_FULL
                rpc.Register<string, int, int>(RpcGift, OnGift);
                rpc.Register<string>(RpcGiftResult, OnGiftResult);
#endif
                s_rpc = rpc;
                s_modPeers.Clear();
                s_pending.Clear();
                s_pingAt = -100f;
            }
#if EDITION_FULL
            for (int i = s_pending.Count - 1; i >= 0; i--)
            {
                if (Time.unscaledTime - s_pending[i].SentAt > GiftTimeoutSec)
                {
                    Report(Lang.F("{0} не відповів: схоже, у нього немає Valheim Helper цієї версії. Предмет не передано.", s_pending[i].Name));
                    s_pending.RemoveAt(i);
                }
            }
#endif
        }

        // Asks every player with the mod to answer; players without it stay silent.
        internal static void RequestCheck(bool force)
        {
            if (ZRoutedRpc.instance == null || (!force && Time.unscaledTime - s_pingAt < PingIntervalSec))
            {
                return;
            }
            Tick();
            s_pingAt = Time.unscaledTime;
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcPing, ProtocolVersion);
        }

        internal static ModState GetState(long uid)
        {
            if (s_modPeers.TryGetValue(uid, out int version))
            {
                return version == ProtocolVersion ? ModState.HasMod : ModState.OtherVersion;
            }
            return Time.unscaledTime - s_pingAt < PingTimeoutSec ? ModState.Checking : ModState.NoMod;
        }

        internal static List<ZNet.PlayerInfo> Friends()
        {
            List<ZNet.PlayerInfo> result = new List<ZNet.PlayerInfo>();
            if (ZNet.instance == null || ZRoutedRpc.instance == null)
            {
                return result;
            }
            long own = s_rpcOwnId(ZRoutedRpc.instance);
            foreach (ZNet.PlayerInfo info in ZNet.instance.GetPlayerList())
            {
                if (info.m_characterID != ZDOID.None && info.m_characterID.UserID != own)
                {
                    result.Add(info);
                }
            }
            return result;
        }

#if EDITION_FULL
        internal static void Send(long target, string friendName, string prefab, int amount, int quality)
        {
            if (ZRoutedRpc.instance == null)
            {
                return;
            }
            Tick();
            s_pending.Add(new PendingGift { Target = target, Name = friendName, SentAt = Time.unscaledTime });
            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcGift, prefab, amount, quality);
        }
#endif

        private static void OnPing(long sender, int version)
        {
            if (ZRoutedRpc.instance == null || sender == s_rpcOwnId(ZRoutedRpc.instance))
            {
                return;
            }
            s_modPeers[sender] = version;
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcPong, ProtocolVersion);
        }

        private static void OnPong(long sender, int version)
        {
            s_modPeers[sender] = version;
        }

#if EDITION_FULL
        private static void OnGift(long sender, string prefab, int amount, int quality)
        {
            string from = PlayerName(sender);
            string reply = ReceiveGift(from, prefab, amount, quality);
            if (ZRoutedRpc.instance != null)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcGiftResult, reply);
            }
        }

        private static string ReceiveGift(string from, string prefab, int amount, int quality)
        {
            Player player = Player.m_localPlayer;
            string me = player != null ? player.GetPlayerName() : Lang.T("Друг");
            if (player == null)
            {
                return Lang.F("{0} зараз не в грі, предмет не передано.", me);
            }
            if (!AcceptGifts)
            {
                return Lang.F("{0} вимкнув отримання предметів, предмет не передано.", me);
            }
            GameObject itemPrefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
            ItemDrop drop = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
            if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null)
            {
                return Lang.F("У {0} немає такого предмета в грі, предмет не передано.", me);
            }
            if (drop.m_itemData.GetDamage().GetTotalDamage() > MaxSafeItemDamage)
            {
                return Lang.T("Цей предмет гра завжди позначає як читерський, тому його не передано.");
            }
            amount = Mathf.Clamp(amount, 1, 9999);
            quality = Mathf.Clamp(quality, 1, Mathf.Max(1, drop.m_itemData.m_shared.m_maxQuality));
            bool equipment = drop.m_itemData.m_shared.m_maxStackSize <= 1;
            string name = Localization.instance.Localize(drop.m_itemData.m_shared.m_name);
            ItemDrop.ItemData added = player.GetInventory().AddItem(prefab, amount, quality, 0,
                equipment ? player.GetPlayerID() : 0L, equipment ? player.GetPlayerName() : "", cheated: false);
            if (added == null)
            {
                player.Message(MessageHud.MessageType.TopLeft, Lang.F("{0} хотів передати тобі {1}, але в інвентарі немає місця", from, name));
                return Lang.F("У {0} немає вільного місця в інвентарі, предмет не передано.", me);
            }
            player.Message(MessageHud.MessageType.TopLeft, Lang.F("{0} передав тобі: {1} ×{2}", from, name, amount));
            Log.Info($"Gift received from {from}: {prefab} x{amount} q{quality}");
            return Lang.F("{0} отримав: {1} ×{2} (якість {3}). Що не влізло — лежить у нього під ногами.", me, name, amount, quality);
        }

        private static void OnGiftResult(long sender, string message)
        {
            int index = s_pending.FindIndex(gift => gift.Target == sender);
            if (index >= 0)
            {
                s_pending.RemoveAt(index);
            }
            Report(message);
        }
#endif

        private static void Report(string message)
        {
            LastResult = message;
            LastResultAt = Time.unscaledTime;
            Log.Info(message);
            if (Player.m_localPlayer != null)
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, message);
            }
        }

        private static string PlayerName(long uid)
        {
            if (ZNet.instance != null)
            {
                foreach (ZNet.PlayerInfo info in ZNet.instance.GetPlayerList())
                {
                    if (info.m_characterID.UserID == uid)
                    {
                        return info.m_name;
                    }
                }
            }
            return Lang.T("Друг");
        }
    }
}
