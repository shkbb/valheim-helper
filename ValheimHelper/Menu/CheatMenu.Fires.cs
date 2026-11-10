using System.Collections.Generic;
using UnityEngine;
using ValheimHelper.Storage;

namespace ValheimHelper
{
    // "Факели" tab.
    internal sealed partial class CheatMenu
    {
        // Refreshed on Layout only, so every event of a frame draws the same controls.
        private List<Fires.FuelStat> m_fireStats = new List<Fires.FuelStat>();
        private float m_fireStatsAt = -10f;
        private string m_fireReport = "";
        private bool m_lowFiresShown;

        private void DrawFiresTab()
        {
            Player player = Player.m_localPlayer;
            if (player == null || !StorageCatalog.EnsureBuilt())
            {
                GUILayout.Label(Lang.T("Факели з'являться, коли ти зайдеш у світ."));
                return;
            }
            if (Event.current.type == EventType.Layout)
            {
                m_fireReport = string.IsNullOrEmpty(Fires.LastReport) ? "" : Lang.T("<color=#e8d8b0>Останній раз:</color> ") + Fires.LastReport;
                m_lowFiresShown = StorageOverlay.LowFiresActive;
                if (Time.unscaledTime - m_fireStatsAt > 1f)
                {
                    m_fireStatsAt = Time.unscaledTime;
                    m_fireStats = Fires.Scan(player, StorageSettings.FireRadius);
                }
            }

            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Факели й вогнища бази"), HelperSkin.Header);
            GUILayout.Label(Lang.T("<color=#c0c0c0>Тут видно, скільки довкола факелів, бра, жаровень і вогнищ, скільки палива треба, щоб заповнити їх до повного, " +
                "і скільки його в тебе є. Заправка бере паливо з інвентаря й розносить його спершу в найпорожніші, тож навіть коли живиці мало, " +
                "погасне якомога менше. Факели в підземеллях і вічні вогні не рахуються. Речі з позначкою «не чіпати» не витрачаються.\n" +
                "Гра підвантажує будівлі лише довкола гравця (приблизно на 100–150 м), тож для великої бази стань ближче до її центру.</color>"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.F("Шукати в радіусі: {0:0} м", StorageSettings.FireRadius), GUILayout.Width(200f));
            float radius = Mathf.Round(GUILayout.HorizontalSlider(StorageSettings.FireRadius, StorageSettings.MinFireRadius, StorageSettings.MaxFireRadius, GUILayout.Width(300f)));
            if (radius != StorageSettings.FireRadius)
            {
                StorageSettings.FireRadius = radius;
                StorageSettings.Changed();
                m_fireStatsAt = -10f;
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (Bindable(GUILayout.Button(Lang.T("Заправити все з інвентаря") + Binds.Hint("fires"), GUILayout.Height(34f)), "fires"))
            {
                string message = Fires.Fill(null);
                if (!string.IsNullOrEmpty(message))
                {
                    ShowMessage(message);
                }
                m_fireStatsAt = -10f;
            }
            if (Bindable(GUILayout.Button((m_lowFiresShown ? Lang.T("Сховати підсвітку порожніх") : Lang.T("Підсвітити порожні")) + Binds.Hint("lowfires"), GUILayout.Width(230f), GUILayout.Height(34f)), "lowfires"))
            {
                StorageOverlay.ShowLowFires(!m_lowFiresShown);
                if (!m_lowFiresShown)
                {
                    ShowMessage(Lang.T("Порожні факели й вогнища підсвічено на 3 хвилини. Закрий меню й подивись довкола."));
                }
            }
            GUILayout.EndHorizontal();
            if (m_fireReport.Length > 0)
            {
                GUILayout.Label(m_fireReport);
            }
            GUILayout.EndVertical();

            if (m_fireStats.Count == 0)
            {
                GUILayout.Label(Lang.T("<color=#909090>У цьому радіусі немає збудованих факелів чи вогнищ, які можна заправляти.</color>"));
            }
            foreach (Fires.FuelStat stat in m_fireStats)
            {
                DrawFuelStat(stat);
            }
        }

        private void DrawFuelStat(Fires.FuelStat stat)
        {
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.BeginHorizontal();
            Rect icon = GUILayoutUtility.GetRect(32f, 32f, GUILayout.Width(32f), GUILayout.Height(32f));
            if (stat.IconTexture != null && Event.current.type == EventType.Repaint)
            {
                GUI.DrawTextureWithTexCoords(icon, stat.IconTexture, stat.IconUv);
            }
            GUILayout.Label(Lang.F("<b>На паливі «{0}»: {1}</b>", stat.DisplayName, stat.Count), HelperSkin.Header, GUILayout.Height(32f));
            GUILayout.EndHorizontal();

            string states = Lang.F("горять: {0}", stat.Burning);
            if (stat.Empty > 0)
            {
                states += Lang.F(", <color=#ff9060>погасли без палива: {0}</color>", stat.Empty);
            }
            if (stat.Off > 0)
            {
                states += Lang.F(", вимкнені: {0}", stat.Off);
            }
            GUILayout.Label(states);
            string enough = stat.Needed == 0 ? Lang.T("<color=#80ff80>усі повні</color>")
                : stat.Available >= stat.Needed ? Lang.F("<color=#80ff80>у тебе є {0} — вистачить</color>", stat.Available)
                : Lang.F("<color=#ffb040>у тебе є {0} — бракує {1}</color>", stat.Available, stat.Needed - stat.Available);
            GUILayout.Label(Lang.F("Щоб заповнити всі до повного, треба: <b>{0}</b> ({1})", stat.Needed, enough));
            if (stat.SoonestOutSeconds < float.MaxValue)
            {
                GUILayout.Label(Lang.F("Найшвидше погасне через: {0}", Fires.FormatDuration(stat.SoonestOutSeconds)));
            }
            foreach (Fires.KindStat kind in stat.Kinds)
            {
                GUILayout.Label(Lang.F("<color=#c0c0c0>• {0} ×{1} — повного вистачає на {2}</color>", kind.Name, kind.Count, Fires.FormatDuration(kind.FullSeconds)));
            }
            if (stat.Needed > 0 && GUILayout.Button(Lang.F("Заправити «{0}»: {1} із {2}", stat.DisplayName, Mathf.Min(stat.Needed, stat.Available), stat.Needed)))
            {
                string message = Fires.Fill(stat.FuelName);
                if (!string.IsNullOrEmpty(message))
                {
                    ShowMessage(message);
                }
                m_fireStatsAt = -10f;
            }
            GUILayout.EndVertical();
        }
    }
}
