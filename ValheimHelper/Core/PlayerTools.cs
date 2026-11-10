using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimHelper
{
    // "Гравці" tab: status effects on yourself and on friends, and teleporting to a friend.
    //  - An effect is added through the game's own SEMan (for another player through its vanilla RPC_AddStatusEffect,
    //    handled by that player's game, so it works even without the mod there). Nothing is marked as cheated.
    //  - The game has no RPC to remove an effect from someone else, so removing from a friend needs the mod there.
    //  - Teleport uses Player.TeleportTo, the same call as the map teleport. The friend's position comes from the
    //    loaded character, the world data (the host has it), the position shared on the map, or the friend's mod.
    internal static class PlayerTools
    {
        internal sealed class Target
        {
            public string Name;
            public long PeerId;
            public ZDOID CharacterId;
            public bool IsMe;
        }

        internal sealed class EffectEntry
        {
            public int Hash;
            public string Prefab;
            public string DisplayName;
            public string Category;
            public string Tooltip;
            public float Duration;
            public Texture2D IconTexture;
            public Rect IconUv;
            public string SearchKey;
        }

        private const string RpcRemoveEffect = "ValheimHelper_RemoveEffect";
        private const string RpcAddEffect = "ValheimHelper_AddEffect";
        private const string RpcWhere = "ValheimHelper_Where";
        private const string RpcHere = "ValheimHelper_Here";
        private const float WhereTimeoutSec = 5f;
        private const float KeepCheckSec = 1f;
        // An effect kept "always on" is refreshed when less than this is left.
        private const float KeepRefreshSec = 3f;

        internal static readonly string[] Categories = { Lang.T("Усі"), Lang.T("Сили Форсаків"), Lang.T("Зілля й меди"), Lang.T("Бонуси комплектів"), Lang.T("Відпочинок і середовище"), Lang.T("Негативні"), Lang.T("Інші") };

        // How long "Накласти" makes an effect last: the game's own time, a fixed time, or until removed (-1).
        internal static readonly string[] DurationNames = { Lang.T("Як у грі"), Lang.T("1 хв"), Lang.T("5 хв"), Lang.T("15 хв"), Lang.T("30 хв"), Lang.T("1 год"), Lang.T("Поки не зніму") };
        internal static readonly float[] DurationSeconds = { 0f, 60f, 300f, 900f, 1800f, 3600f, -1f };

        internal static float ChosenDuration => DurationSeconds[Mathf.Clamp(State.EffectDuration, 0, DurationSeconds.Length - 1)];

        internal static string LastResult = "";
        internal static float LastResultAt = -1f;

        private static readonly HashSet<string> s_negative = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Burning", "Frost", "Poison", "Lightning", "Spirit", "Wet", "Smoked", "Tared", "Slimed", "Harpooned", "Encumbered", "Cold", "Freezing",
            "Staggered", "Immobilized", "Ashlands_Burning", "Hungry",
        };
        private static readonly HashSet<string> s_rest = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Rested", "Resting", "Sheltered", "Campfire", "Warm", "Cozy", "CorpseRun", "SoftDeath", "Guardian", "Shelter",
        };
        private static readonly HashSet<int> s_kept = new HashSet<int>();

        private static List<EffectEntry> s_effects;
        private static ZRoutedRpc s_rpc;
        private static float s_keepAt;
        private static long s_whereTarget;
        private static string s_whereName;
        private static float s_whereAt = -100f;

        internal static bool IsKept(int hash)
        {
            return s_kept.Contains(hash);
        }

        // ---------------------------------------------------------------- targets

        internal static List<Target> Targets()
        {
            List<Target> result = new List<Target>();
            Player me = Player.m_localPlayer;
            if (me == null)
            {
                return result;
            }
            result.Add(new Target { Name = Lang.T("Я"), IsMe = true, CharacterId = me.GetZDOID() });
            foreach (ZNet.PlayerInfo info in Gifts.Friends())
            {
                result.Add(new Target { Name = info.m_name, PeerId = info.m_characterID.UserID, CharacterId = info.m_characterID });
            }
            return result;
        }

        // ---------------------------------------------------------------- effects

        internal static List<EffectEntry> Effects()
        {
            if (s_effects != null || ObjectDB.instance == null || ObjectDB.instance.m_StatusEffects == null || ObjectDB.instance.m_StatusEffects.Count == 0)
            {
                return s_effects ?? new List<EffectEntry>();
            }
            List<EffectEntry> effects = new List<EffectEntry>();
            HashSet<int> seen = new HashSet<int>();
            foreach (StatusEffect effect in ObjectDB.instance.m_StatusEffects)
            {
                if (effect == null || !seen.Add(effect.NameHash()))
                {
                    continue;
                }
                string prefab = effect.name;
                string localized = string.IsNullOrEmpty(effect.m_name) ? "" : Localization.instance.Localize(effect.m_name);
                EffectEntry entry = new EffectEntry
                {
                    Hash = effect.NameHash(),
                    Prefab = prefab,
                    DisplayName = string.IsNullOrEmpty(localized) || localized.StartsWith("[") ? prefab : localized,
                    Category = CategoryOf(prefab),
                    Tooltip = string.IsNullOrEmpty(effect.m_tooltip) ? "" : Localization.instance.Localize(effect.m_tooltip),
                    Duration = effect.m_ttl,
                };
                if (effect.m_icon != null && effect.m_icon.texture != null)
                {
                    Texture2D texture = effect.m_icon.texture;
                    Rect rect = effect.m_icon.textureRect;
                    entry.IconTexture = texture;
                    entry.IconUv = new Rect(rect.x / texture.width, rect.y / texture.height, rect.width / texture.width, rect.height / texture.height);
                }
                entry.SearchKey = (entry.DisplayName + " " + prefab + " " + entry.Tooltip).ToLowerInvariant();
                effects.Add(entry);
            }
            effects.Sort((a, b) =>
            {
                int byCategory = Array.IndexOf(Categories, a.Category).CompareTo(Array.IndexOf(Categories, b.Category));
                return byCategory != 0 ? byCategory : string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
            });
            s_effects = effects;
            Log.Info($"Status effects listed: {effects.Count}");
            return effects;
        }

        private static string CategoryOf(string prefab)
        {
            if (prefab.StartsWith("GP_", StringComparison.OrdinalIgnoreCase))
            {
                return Lang.T("Сили Форсаків");
            }
            if (prefab.StartsWith("Potion_", StringComparison.OrdinalIgnoreCase) || prefab.IndexOf("Mead", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Lang.T("Зілля й меди");
            }
            if (prefab.StartsWith("SetEffect_", StringComparison.OrdinalIgnoreCase))
            {
                return Lang.T("Бонуси комплектів");
            }
            if (s_rest.Contains(prefab))
            {
                return Lang.T("Відпочинок і середовище");
            }
            if (s_negative.Contains(prefab))
            {
                return Lang.T("Негативні");
            }
            return Lang.T("Інші");
        }

        internal static bool HasOnMe(int hash)
        {
            Player me = Player.m_localPlayer;
            return me != null && me.GetSEMan().HaveStatusEffect(hash);
        }

        // seconds: 0 — as long as the game makes it last, above 0 — that long, below 0 — until removed.
        internal static string Apply(Target target, EffectEntry effect, float seconds)
        {
            if (target == null || effect == null || ZRoutedRpc.instance == null)
            {
                return Lang.T("Зайди у світ.");
            }
            if (target.IsMe)
            {
                if (Player.m_localPlayer == null)
                {
                    return Lang.T("Зайди у світ.");
                }
                return ApplyLocal(effect.Hash, seconds)
                    ? Lang.F("Ефект «{0}» накладено{1}.", effect.DisplayName, DurationText(seconds))
                    : Lang.F("Гра не прийняла ефект «{0}»: він діє лише за певних умов (наприклад, погода чи вогонь поруч).", effect.DisplayName);
            }
            if (Gifts.GetState(target.PeerId) == Gifts.ModState.HasMod)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(target.PeerId, RpcAddEffect, effect.Hash, seconds);
                return Lang.F("«{0}» надіслано гравцю {1}{2}. Якщо ефект діє лише за певних умов, його гра може одразу зняти.", effect.DisplayName, target.Name, DurationText(seconds));
            }
            // The vanilla RPC of the friend's character: the friend's game adds the effect itself, for the game's own time.
            ZRoutedRpc.instance.InvokeRoutedRPC(target.PeerId, target.CharacterId, "RPC_AddStatusEffect", effect.Hash, true, 0, 0f, -1);
            string note = seconds != 0f ? Lang.F(" Тривалість — як у грі: інакше задати можна, лише якщо в {0} теж є Valheim Helper цієї версії.", target.Name) : "";
            return Lang.F("«{0}» надіслано гравцю {1}. Якщо ефект діє лише за певних умов, його гра може одразу зняти.{2}", effect.DisplayName, target.Name, note);
        }

        private static string DurationText(float seconds)
        {
            if (seconds < 0f)
            {
                return Lang.T(", поки не знімеш");
            }
            return seconds > 0f ? Lang.T(" на ") + FormatSeconds(seconds) : "";
        }

        internal static string FormatSeconds(float seconds)
        {
            int total = Mathf.RoundToInt(seconds);
            if (total >= 3600 && total % 3600 == 0)
            {
                return Lang.F("{0} год", total / 3600);
            }
            if (total >= 60 && total % 60 == 0)
            {
                return Lang.F("{0} хв", total / 60);
            }
            return total >= 60 ? $"{total / 60}:{total % 60:00}" : Lang.F("{0} с", total);
        }

        // Adds the effect to the local player (again, from the start, if it is already on) and sets how long it lasts.
        private static bool ApplyLocal(int hash, float seconds)
        {
            Player me = Player.m_localPlayer;
            if (me == null)
            {
                return false;
            }
            if (seconds < 0f)
            {
                s_kept.Add(hash);
                s_keepAt = 0f;
            }
            else
            {
                s_kept.Remove(hash);
            }
            SEMan seman = me.GetSEMan();
            seman.AddStatusEffect(hash, resetTime: true);
            StatusEffect active = seman.GetStatusEffect(hash);
            if (active == null)
            {
                return false;
            }
            // The effect on the player is a copy, so its time can be changed without touching anybody else's.
            StatusEffect original = ObjectDB.instance != null ? ObjectDB.instance.GetStatusEffect(hash) : null;
            if (seconds > 0f)
            {
                active.m_ttl = seconds;
            }
            else if (original != null)
            {
                active.m_ttl = original.m_ttl;
            }
            active.ResetTime();
            ArmDamage(active, seconds);
            return true;
        }

        // Fire, spirit and poison hurt by the damage the source of them puts in (a torch, a blob), and an effect
        // added by itself carries none — it would only glow. So the mod fills it with damage for its time.
        private static void ArmDamage(StatusEffect effect, float seconds)
        {
            float total = Mathf.Clamp(seconds > 0f ? seconds * 4f : 60f, 20f, 400f);
            if (effect is SE_Burning burning)
            {
                if (effect.name.IndexOf("spirit", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    burning.AddSpiritDamage(total);
                }
                else
                {
                    burning.AddFireDamage(total);
                }
            }
            else if (effect is SE_Poison poison)
            {
                poison.AddDamage(total);
            }
        }

        // Seconds left of the effect on the local player; 0 when it has no end of its own.
        internal static float RemainingOnMe(int hash)
        {
            Player me = Player.m_localPlayer;
            StatusEffect active = me != null ? me.GetSEMan().GetStatusEffect(hash) : null;
            return active != null && active.m_ttl > 0f ? Mathf.Max(0f, active.GetRemaningTime()) : 0f;
        }

        internal static string Remove(Target target, EffectEntry effect)
        {
            int hash = effect != null ? effect.Hash : 0;
            if (target == null || ZRoutedRpc.instance == null)
            {
                return Lang.T("Зайди у світ.");
            }
            if (target.IsMe)
            {
                RemoveLocal(hash);
                return effect != null ? Lang.F("Ефект «{0}» знято.", effect.DisplayName) : Lang.T("Усі ефекти знято.");
            }
            if (Gifts.GetState(target.PeerId) != Gifts.ModState.HasMod)
            {
                Gifts.RequestCheck(force: true);
                return Lang.F("Зняти ефект з {0} можна лише, якщо в нього є Valheim Helper цієї версії: гра не має для цього власної команди.", target.Name);
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(target.PeerId, RpcRemoveEffect, hash);
            return effect != null ? Lang.F("Прошу гру {0} зняти «{1}».", target.Name, effect.DisplayName) : Lang.F("Прошу гру {0} зняти всі ефекти.", target.Name);
        }

        private static void RemoveLocal(int hash)
        {
            Player me = Player.m_localPlayer;
            if (me == null)
            {
                return;
            }
            if (hash == 0)
            {
                s_kept.Clear();
                me.GetSEMan().RemoveAllStatusEffects();
            }
            else
            {
                s_kept.Remove(hash);
                me.GetSEMan().RemoveStatusEffect(hash);
            }
        }

        // ---------------------------------------------------------------- teleport

        internal static string TeleportTo(Target target)
        {
            Player me = Player.m_localPlayer;
            if (me == null || target == null || target.IsMe)
            {
                return Lang.T("Обери гравця, до якого телепортуватися.");
            }
            if (me.IsTeleporting() || me.IsDead())
            {
                return Lang.T("Зараз телепортуватися не можна.");
            }
            if (TryFindPosition(target, out Vector3 position))
            {
                return Go(target.Name, position);
            }
            if (Gifts.GetState(target.PeerId) == Gifts.ModState.HasMod && ZRoutedRpc.instance != null)
            {
                s_whereTarget = target.PeerId;
                s_whereName = target.Name;
                s_whereAt = Time.unscaledTime;
                ZRoutedRpc.instance.InvokeRoutedRPC(target.PeerId, RpcWhere);
                return Lang.F("Питаю в гри {0}, де він…", target.Name);
            }
            Gifts.RequestCheck(force: true);
            return Lang.F("Не знаю, де {0}: він далеко, не ділиться позицією на карті, і в нього немає Valheim Helper цієї версії.", target.Name);
        }

        private static bool TryFindPosition(Target target, out Vector3 position)
        {
            foreach (Player player in Player.GetAllPlayers())
            {
                if (player != null && player != Player.m_localPlayer && player.GetZDOID() == target.CharacterId)
                {
                    position = player.transform.position;
                    return true;
                }
            }
            // The host keeps every object of the world, the friend's character included.
            ZDO zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(target.CharacterId) : null;
            if (zdo != null)
            {
                position = zdo.GetPosition();
                return true;
            }
            if (ZNet.instance != null)
            {
                foreach (ZNet.PlayerInfo info in ZNet.instance.GetPlayerList())
                {
                    if (info.m_characterID == target.CharacterId && info.m_publicPosition)
                    {
                        position = info.m_position;
                        return true;
                    }
                }
            }
            position = Vector3.zero;
            return false;
        }

        private static string Go(string name, Vector3 position)
        {
            Player me = Player.m_localPlayer;
            if (me == null)
            {
                return Lang.T("Зайди у світ.");
            }
            me.TeleportTo(position + Vector3.up * 0.5f, me.transform.rotation, distantTeleport: true);
            Log.Info($"Teleport to {name} at {position}");
            return Lang.F("Телепортуюся до {0}.", name);
        }

        // ---------------------------------------------------------------- network and tick

        internal static void Tick()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null)
            {
                s_rpc = null;
                s_kept.Clear();
                s_effects = null;
                return;
            }
            if (!ReferenceEquals(rpc, s_rpc))
            {
                rpc.Register<int>(RpcRemoveEffect, OnRemoveEffect);
                rpc.Register<int, float>(RpcAddEffect, OnAddEffect);
                rpc.Register(RpcWhere, new Action<long>(OnWhere));
                rpc.Register<Vector3>(RpcHere, OnHere);
                s_rpc = rpc;
            }
            if (s_whereTarget != 0 && Time.unscaledTime - s_whereAt > WhereTimeoutSec)
            {
                Report(Lang.F("{0} не відповів, телепорт скасовано.", s_whereName));
                s_whereTarget = 0;
            }
            if (s_kept.Count > 0 && Time.unscaledTime >= s_keepAt)
            {
                s_keepAt = Time.unscaledTime + KeepCheckSec;
                Keep();
            }
        }

        private static void Keep()
        {
            Player me = Player.m_localPlayer;
            if (me == null || me.IsDead())
            {
                return;
            }
            SEMan seman = me.GetSEMan();
            foreach (int hash in s_kept)
            {
                StatusEffect active = null;
                foreach (StatusEffect effect in seman.GetStatusEffects())
                {
                    if (effect.NameHash() == hash)
                    {
                        active = effect;
                        break;
                    }
                }
                if (active == null || (active.m_ttl > 0f && active.GetRemaningTime() < KeepRefreshSec))
                {
                    seman.AddStatusEffect(hash, resetTime: true);
                }
            }
        }

        private static void OnRemoveEffect(long sender, int hash)
        {
            if (Player.m_localPlayer == null)
            {
                return;
            }
            RemoveLocal(hash);
            Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, hash == 0
                ? Lang.F("{0} зняв з тебе всі ефекти", PlayerName(sender))
                : Lang.F("{0} зняв з тебе ефект", PlayerName(sender)));
        }

        private static void OnAddEffect(long sender, int hash, float seconds)
        {
            if (Player.m_localPlayer == null)
            {
                return;
            }
            StatusEffect effect = ObjectDB.instance != null ? ObjectDB.instance.GetStatusEffect(hash) : null;
            string name = effect != null && !string.IsNullOrEmpty(effect.m_name) ? Localization.instance.Localize(effect.m_name) : Lang.T("ефект");
            if (ApplyLocal(hash, seconds))
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, Lang.F("{0} наклав на тебе «{1}»{2}", PlayerName(sender), name, DurationText(seconds)));
            }
        }

        private static void OnWhere(long sender)
        {
            Player me = Player.m_localPlayer;
            if (me != null && ZRoutedRpc.instance != null)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcHere, me.transform.position);
            }
        }

        private static void OnHere(long sender, Vector3 position)
        {
            if (sender != s_whereTarget)
            {
                return;
            }
            s_whereTarget = 0;
            Report(Go(s_whereName, position));
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

        internal static void Report(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }
            LastResult = message;
            LastResultAt = Time.unscaledTime;
            Log.Info(message);
            if (Player.m_localPlayer != null)
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, message);
            }
        }
    }
}
