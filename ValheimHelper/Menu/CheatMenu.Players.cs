using System.Collections.Generic;
using UnityEngine;

namespace ValheimHelper
{
    // "Гравці" tab: teleport to a friend and status effects on yourself or a friend.
    internal sealed partial class CheatMenu
    {
        private const int MaxEffectRows = 120;

        private readonly List<PlayerTools.Target> m_playerTargets = new List<PlayerTools.Target>();
        private readonly List<PlayerTools.EffectEntry> m_effectRows = new List<PlayerTools.EffectEntry>();
        // Effects on me and the seconds left (0 = no end of their own).
        private readonly Dictionary<int, float> m_effectsOnMe = new Dictionary<int, float>();
        private string[] m_playerNames = { Lang.T("Я") };
        private int m_playerTarget;
        private int m_effectCategory;
        private string m_effectSearch = "";
        private string m_playerStatus = "";
        private int m_effectTotal;
        private float m_playerResultSeen = -1f;

        private void DrawPlayersTab()
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                GUILayout.Label(Lang.T("Зайди у світ."));
                return;
            }
            if (Event.current.type == EventType.Layout)
            {
                RefreshPlayersTab(player);
            }

            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Кому"), HelperSkin.Header);
            int target = GUILayout.Toolbar(m_playerTarget, m_playerNames);
            if (target != m_playerTarget)
            {
                m_playerTarget = target;
                Gifts.RequestCheck(force: true);
            }
            GUILayout.Label(m_playerStatus);
            PlayerTools.Target selected = m_playerTarget < m_playerTargets.Count ? m_playerTargets[m_playerTarget] : null;
            if (selected != null && !selected.IsMe)
            {
                if (GUILayout.Button(Lang.F("Телепортуватися до {0}", selected.Name), GUILayout.Height(32f)))
                {
                    ShowMessage(PlayerTools.TeleportTo(selected));
                }
            }
            GUILayout.EndVertical();

            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Ефекти"), HelperSkin.Header);
            GUILayout.Label(Lang.T("<color=#c0c0c0>Усі статус-ефекти гри: сили Форсаків, зілля й меди, бонуси комплектів, відпочинок, негативні. Ефект накладається " +
                "звичайним механізмом гри, тож досягнень не зачіпає. Деякі ефекти (мокрий, холод, укриття) гра одразу знімає, якщо немає умови. " +
                "Тривалість вибирається перед «Накласти»: «Поки не зніму» оновлює таймер, доки не натиснеш «Зняти». " +
                "Другові тривалість задається, якщо в нього теж є Valheim Helper цієї версії, інакше — як у грі; зняти з друга теж можна лише тоді.</color>"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("На скільки:"), GUILayout.Width(90f));
            int durationChoice = GUILayout.SelectionGrid(Mathf.Clamp(State.EffectDuration, 0, PlayerTools.DurationNames.Length - 1), PlayerTools.DurationNames, 4);
            if (durationChoice != State.EffectDuration)
            {
                State.EffectDuration = durationChoice;
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Пошук:"), GUILayout.Width(60f));
            m_effectSearch = GUILayout.TextField(m_effectSearch ?? "");
            if ((selected != null && selected.IsMe ? Bindable(GUILayout.Button(Lang.T("Зняти всі з себе") + Binds.Hint("effectsclear"), GUILayout.Width(150f)), "effectsclear") : GUILayout.Button(Lang.T("Зняти всі"), GUILayout.Width(150f))) && selected != null)
            {
                ShowMessage(PlayerTools.Remove(selected, null));
            }
            GUILayout.EndHorizontal();
            int category = GUILayout.SelectionGrid(m_effectCategory, PlayerTools.Categories, 4);
            if (category != m_effectCategory)
            {
                m_effectCategory = category;
            }
            GUILayout.Label(Lang.F("<color=#909090>Показано {0} з {1}</color>", m_effectRows.Count, m_effectTotal));
            foreach (PlayerTools.EffectEntry effect in m_effectRows)
            {
                GUILayout.BeginHorizontal();
                Rect icon = GUILayoutUtility.GetRect(28f, 28f, GUILayout.Width(28f), GUILayout.Height(28f));
                if (effect.IconTexture != null && Event.current.type == EventType.Repaint)
                {
                    GUI.DrawTextureWithTexCoords(icon, effect.IconTexture, effect.IconUv);
                }
                string active = "";
                if (selected != null && selected.IsMe && m_effectsOnMe.TryGetValue(effect.Hash, out float left))
                {
                    active = PlayerTools.IsKept(effect.Hash) ? Lang.T("  <color=#80ff80>діє, поки не знімеш</color>")
                        : left > 0f ? Lang.F("  <color=#80ff80>діє ще {0}</color>", PlayerTools.FormatSeconds(Mathf.Ceil(left))) : Lang.T("  <color=#80ff80>діє</color>");
                }
                string duration = effect.Duration > 0f ? Lang.F("  <color=#909090>у грі {0}</color>", PlayerTools.FormatSeconds(effect.Duration)) : "";
                GUILayout.Label($"{effect.DisplayName}{active}{duration}\n<color=#909090>{effect.Prefab}</color>", RowLabel);
                if (GUILayout.Button(Lang.T("Накласти"), GUILayout.Width(90f), GUILayout.Height(28f)) && selected != null)
                {
                    ShowMessage(PlayerTools.Apply(selected, effect, PlayerTools.ChosenDuration));
                }
                if (GUILayout.Button(Lang.T("Зняти"), GUILayout.Width(70f), GUILayout.Height(28f)) && selected != null)
                {
                    ShowMessage(PlayerTools.Remove(selected, effect));
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
        }

        // A label of two lines that takes the height it needs, whatever the font size is.
        private static GUIStyle s_rowLabel;

        internal static GUIStyle RowLabel
        {
            get
            {
                if (s_rowLabel == null)
                {
                    s_rowLabel = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = false, alignment = TextAnchor.MiddleLeft };
                }
                return s_rowLabel;
            }
        }

        // The players to choose from ("Я" and the friends in the world), shared by this page and the items page.
        private void RefreshTargets()
        {
            m_playerTargets.Clear();
            m_playerTargets.AddRange(PlayerTools.Targets());
            if (m_playerTargets.Count == 0)
            {
                m_playerTargets.Add(new PlayerTools.Target { Name = Lang.T("Я"), IsMe = true });
            }
            if (m_playerNames.Length != m_playerTargets.Count)
            {
                m_playerNames = new string[m_playerTargets.Count];
            }
            for (int i = 0; i < m_playerTargets.Count; i++)
            {
                m_playerNames[i] = m_playerTargets[i].Name;
            }
            if (m_playerTarget >= m_playerTargets.Count)
            {
                m_playerTarget = 0;
            }
        }

        private PlayerTools.Target SelectedTarget()
        {
            return m_playerTarget < m_playerTargets.Count ? m_playerTargets[m_playerTarget] : null;
        }

        // Lists change only on Layout, so every event of a frame draws the same controls.
        private void RefreshPlayersTab(Player player)
        {
            Gifts.RequestCheck(force: false);
            RefreshTargets();
            PlayerTools.Target selected = SelectedTarget();
            m_playerStatus = PlayerTargetStatus(selected);

            m_effectsOnMe.Clear();
            foreach (StatusEffect effect in player.GetSEMan().GetStatusEffects())
            {
                m_effectsOnMe[effect.NameHash()] = effect.m_ttl > 0f ? Mathf.Max(0f, effect.GetRemaningTime()) : 0f;
            }
            List<PlayerTools.EffectEntry> all = PlayerTools.Effects();
            m_effectTotal = all.Count;
            m_effectRows.Clear();
            string search = (m_effectSearch ?? "").Trim().ToLowerInvariant();
            string category = PlayerTools.Categories[Mathf.Clamp(m_effectCategory, 0, PlayerTools.Categories.Length - 1)];
            foreach (PlayerTools.EffectEntry effect in all)
            {
                if (m_effectCategory > 0 && effect.Category != category)
                {
                    continue;
                }
                if (search.Length > 0 && !effect.SearchKey.Contains(search))
                {
                    continue;
                }
                m_effectRows.Add(effect);
                if (m_effectRows.Count >= MaxEffectRows)
                {
                    break;
                }
            }
            if (PlayerTools.LastResultAt > m_playerResultSeen)
            {
                m_playerResultSeen = PlayerTools.LastResultAt;
                ShowMessage(PlayerTools.LastResult);
            }
        }

        private static string PlayerTargetStatus(PlayerTools.Target target)
        {
            if (target == null || target.IsMe)
            {
                return Lang.T("<color=#c0c0c0>Ефекти накладаються на тебе. Обери друга вгорі, щоб накласти на нього або телепортуватися до нього.</color>");
            }
            switch (Gifts.GetState(target.PeerId))
            {
                case Gifts.ModState.HasMod:
                    return Lang.F("<color=#80ff80>У {0} є Valheim Helper:</color> <color=#c0c0c0>можна накладати й знімати ефекти та телепортуватися, де б він не був.</color>", target.Name);
                case Gifts.ModState.Checking:
                    return Lang.F("<color=#c0c0c0>Перевіряю, чи є в {0} Valheim Helper…</color>", target.Name);
                default:
                    return Lang.F("<color=#ffb040>У {0} немає Valheim Helper цієї версії:</color> <color=#c0c0c0>ефекти накладати можна (це команда самої гри), " +
                        "а знімати — ні. Телепорт спрацює, якщо він поруч, ти хост або він показує себе на карті.</color>", target.Name);
            }
        }
    }
}
