using System.Collections.Generic;
using UnityEngine;

namespace ValheimHelper
{
    // "Статистика": the character's counters kept by the game, or the current world's, side by side with the friends
    // who have the mod, and one button that copies the table for Discord.
    internal sealed partial class CheatMenu
    {
        private const float StatLabelWidth = 250f;
        private const float StatValueWidth = 115f;

        private static readonly string[] s_statScopes = { Lang.T("Персонаж"), Lang.T("Цей світ") };

        // Taken on Layout only, so every event of a frame draws the same controls.
        private List<StatsBook.Column> m_statColumns;
        private List<KeyValuePair<StatsBook.Group, List<StatsBook.Row>>> m_statRows;
        private string m_statAbout;
        private string m_statSilent;

        private void DrawStatsTab()
        {
            if (Game.instance == null || Game.instance.GetPlayerProfile() == null || Player.m_localPlayer == null)
            {
                GUILayout.Label(Lang.T("Зайди у світ, щоб побачити статистику."));
                return;
            }
            GUILayout.BeginHorizontal();
            int scope = GUILayout.Toolbar(StatsBook.Scope, s_statScopes, GUILayout.Width(300f));
            if (scope != StatsBook.Scope && Event.current.type != EventType.Layout)
            {
                StatsBook.Scope = scope;
                StatsBook.AskFriends(force: true);
            }
            GUILayout.EndHorizontal();
            if (Event.current.type == EventType.Layout || m_statColumns == null)
            {
                StatsBook.AskFriends(force: false);
                m_statAbout = StatsBook.Scope == StatsBook.WorldScope
                    ? Lang.F("Світ «{0}»: лічильники ведуться з {1:dd.MM.yyyy}, відколи тут стоїть мод. Час у світі — повний, із даних гри.",
                        StatsBook.WorldName ?? "", StatsBook.Since)
                    : Lang.T("Лічильники, які гра сама веде для персонажа в усіх світах.");
                List<string> silent = StatsBook.Silent();
                m_statSilent = silent.Count > 0 ? Lang.F("Без статистики (немає мода цієї версії): {0}", string.Join(", ", silent)) : null;
                m_statColumns = StatsBook.Columns();
                m_statRows = StatsBook.Visible(m_statColumns, StatsBook.Scope);
            }
            GUILayout.Label("<color=#c0c0c0>" + m_statAbout + "</color>");
            if (m_statSilent != null)
            {
                GUILayout.Label("<color=#c0c0c0>" + m_statSilent + "</color>");
            }

            List<StatsBook.Column> columns = m_statColumns;
            GUILayout.BeginHorizontal();
            GUILayout.Label("", GUILayout.Width(StatLabelWidth));
            foreach (StatsBook.Column column in columns)
            {
                GUILayout.Label("<b>" + column.Name + "</b>", GUILayout.Width(StatValueWidth));
            }
            GUILayout.EndHorizontal();
            foreach (KeyValuePair<StatsBook.Group, List<StatsBook.Row>> pair in m_statRows)
            {
                GUILayout.BeginVertical(HelperSkin.Section);
                GUILayout.Label(pair.Key.Title, HelperSkin.Header);
                foreach (StatsBook.Row row in pair.Value)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(row.Label, GUILayout.Width(StatLabelWidth));
                    foreach (StatsBook.Column column in columns)
                    {
                        GUILayout.Label(StatsBook.Value(column, row), GUILayout.Width(StatValueWidth));
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndVertical();
            }
        }
    }
}
