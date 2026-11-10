using System.Collections.Generic;
using UnityEngine;

namespace ValheimHelper
{
    // "Моби" tab.
    internal sealed partial class CheatMenu
    {
        private static readonly string[] s_mobFilters = { Lang.T("Усі"), Lang.T("Боси"), Lang.T("Приручувані") };
        private static readonly string[] s_mobStars = { "0★", "1★", "2★", "3★", "4★" };

        private List<Spawner.CreatureEntry> m_creatures;
        private readonly List<Spawner.CreatureEntry> m_filteredCreatures = new List<Spawner.CreatureEntry>();
        private string m_mobSearch = "";
        private string m_mobFilteredSearch;
        private int m_mobFilter;
        private int m_mobFilteredFilter = -1;
        private bool m_mobShowDev;
        private bool m_mobFilteredShowDev;
        private string m_mobCountText = "1";
        private int m_mobStars;
        private bool m_mobTamed;
        private int m_mobAlive;

        private void DrawMobsTab()
        {
            Player player = Player.m_localPlayer;
            if (player == null || ZNetScene.instance == null)
            {
                GUILayout.Label(Lang.T("Список мобів з'явиться, коли ти зайдеш у світ."));
                return;
            }
            if (m_creatures == null)
            {
                m_creatures = Spawner.BuildList();
            }
            if (Event.current.type == EventType.Layout)
            {
                m_mobAlive = Spawner.SpawnedAlive();
            }

            GUILayout.Label(Lang.T("<color=#c0c0c0>Моби з'являються перед тобою так само, як їх створює сам світ: без позначки «читерський», " +
                "тож їхній лут, трофеї й убивства рахуються як звичайні. Боси, створені так, поводяться як викликані на вівтарі.</color>"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Пошук:"), GUILayout.Width(60f));
            m_mobSearch = GUILayout.TextField(m_mobSearch ?? "");
            GUILayout.EndHorizontal();
            m_mobFilter = GUILayout.Toolbar(m_mobFilter, s_mobFilters);
            m_mobShowDev = GUILayout.Toggle(m_mobShowDev, Lang.T(" Показувати службових / невикористаних мобів гри"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Кількість:"), GUILayout.Width(85f));
            m_mobCountText = GUILayout.TextField(m_mobCountText ?? "1", GUILayout.Width(50f));
            foreach (string preset in new[] { "1", "5", "10" })
            {
                if (GUILayout.Button(preset, GUILayout.Width(40f)))
                {
                    m_mobCountText = preset;
                }
            }
            GUILayout.Label(Lang.T("  Рівень:"), GUILayout.Width(70f));
            m_mobStars = GUILayout.Toolbar(m_mobStars, s_mobStars, GUILayout.Width(240f));
            GUILayout.EndHorizontal();
            m_mobTamed = GUILayout.Toggle(m_mobTamed, Lang.T(" Одразу приручені (не нападатимуть на гравців)"));

            if (m_mobAlive > 0 && Bindable(ConfirmButton("despawn", Lang.F("Прибрати мобів, створених модом ({0})", m_mobAlive) + Binds.Hint("despawn")), "despawn"))
            {
                ShowMessage(Lang.F("Прибрано мобів: {0}.", Spawner.RemoveSpawned()));
            }

            RefreshCreatureFilter();
            int shown = 0;
            foreach (Spawner.CreatureEntry entry in m_filteredCreatures)
            {
                if (shown++ >= MaxListedItems)
                {
                    GUILayout.Label(Lang.F("<color=#c0c0c0>Показано перших {0} із {1}, уточни пошук.</color>", MaxListedItems, m_filteredCreatures.Count));
                    break;
                }
                GUILayout.BeginHorizontal();
                Rect iconRect = GUILayoutUtility.GetRect(32f, 32f, GUILayout.Width(32f), GUILayout.Height(32f));
                if (entry.IconTexture != null && Event.current.type == EventType.Repaint)
                {
                    GUI.DrawTextureWithTexCoords(iconRect, entry.IconTexture, entry.IconUv);
                }
                string tags = (entry.IsBoss ? Lang.T("  <color=#ff8060>бос</color>") : "") + (entry.Tameable ? Lang.T("  <color=#80ff80>приручується</color>") : "");
                GUILayout.Label($"{entry.Name}{tags}  <color=#909090>{entry.Prefab}</color>", GUILayout.Height(32f));
                if (GUILayout.Button(Lang.T("Створити"), GUILayout.Width(100f), GUILayout.Height(28f)))
                {
                    SpawnCreature(entry);
                }
                GUILayout.EndHorizontal();
            }
        }

        private void RefreshCreatureFilter()
        {
            string search = (m_mobSearch ?? "").Trim().ToLowerInvariant();
            if (search == m_mobFilteredSearch && m_mobFilter == m_mobFilteredFilter && m_mobShowDev == m_mobFilteredShowDev)
            {
                return;
            }
            m_mobFilteredSearch = search;
            m_mobFilteredFilter = m_mobFilter;
            m_mobFilteredShowDev = m_mobShowDev;
            m_filteredCreatures.Clear();
            foreach (Spawner.CreatureEntry entry in m_creatures)
            {
                if ((!m_mobShowDev && entry.IsDev) || (m_mobFilter == 1 && !entry.IsBoss) || (m_mobFilter == 2 && !entry.Tameable))
                {
                    continue;
                }
                if (search.Length == 0 || entry.SearchKey.Contains(search))
                {
                    m_filteredCreatures.Add(entry);
                }
            }
        }

        private void SpawnCreature(Spawner.CreatureEntry entry)
        {
            if (!int.TryParse(m_mobCountText, out int count) || count <= 0)
            {
                ShowMessage(Lang.T("Вкажи кількість числом."));
                return;
            }
            count = Mathf.Min(count, 50);
            int spawned = Spawner.Spawn(entry, count, m_mobStars + 1, m_mobTamed);
            if (spawned == 0)
            {
                ShowMessage(Lang.T("Не вдалося створити цього моба."));
                return;
            }
            string stars = m_mobStars > 0 ? $", {m_mobStars}★" : "";
            string tamed = m_mobTamed ? Lang.T(", приручені") : "";
            ShowMessage(Lang.F("Створено: {0} ×{1}{2}{3}.", entry.Name, spawned, stars, tamed));
        }
    }
}
