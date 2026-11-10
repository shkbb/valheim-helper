using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper
{
    // "Досягнення" (the top of the page): every achievement the game has, with its state, and a button that gives it through
    // the game's own Achievements.AchievementEvent, the same call the game makes when the achievement is really earned.
    // The list is read from the game while it runs (the achievements are assets, not code): Achievements.m_instance →
    // m_achievementLists → AchievementList.m_achievements.
    internal sealed partial class CheatMenu
    {
        private sealed class AchievementRow
        {
            public Achievement Source;
            public string Name;
            public string Description;
            public bool Unlocked;
            // Steam's own state of the achievement: null when Steam cannot be asked (not running, no id).
            public bool? Steam;
        }

        // Taken on Layout only, so every event of a frame draws the same controls; refreshed once a second.
        private List<AchievementRow> m_achievementRows;
        private string m_achievementProblem;
        private float m_nextAchievementRead;

        private static System.Reflection.MethodInfo s_platformId;
        private static System.Reflection.MethodInfo s_steamGetAchievement;
        private static bool s_steamLooked;

        // Steamworks.SteamUserStats.GetAchievement(id, out bool) through reflection: the mod does not reference Steamworks.
        private static bool? SteamState(Achievement achievement)
        {
            try
            {
                if (!s_steamLooked)
                {
                    s_steamLooked = true;
                    s_platformId = AccessTools.Method(typeof(Achievements), "GetAchievementInternalPlatformId");
                    System.Type stats = AccessTools.TypeByName("Steamworks.SteamUserStats");
                    s_steamGetAchievement = stats?.GetMethod("GetAchievement", new[] { typeof(string), typeof(bool).MakeByRefType() });
                }
                if (s_platformId == null || s_steamGetAchievement == null)
                {
                    return null;
                }
                string id = s_platformId.Invoke(null, new object[] { achievement }) as string;
                if (string.IsNullOrEmpty(id))
                {
                    return null;
                }
                object[] args = { id, false };
                return (bool)s_steamGetAchievement.Invoke(null, args) ? (bool)args[1] : (bool?)null;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        private static List<Achievement> ReadGameAchievements(out string problem)
        {
            problem = null;
            List<Achievement> result = new List<Achievement>();
            object instance = AccessTools.Field(typeof(Achievements), "m_instance")?.GetValue(null);
            if (instance == null)
            {
                problem = Lang.T("Зайди у світ: гра створює список досягнень разом із ним.");
                return result;
            }
            IEnumerable lists = AccessTools.Field(typeof(Achievements), "m_achievementLists")?.GetValue(instance) as IEnumerable;
            if (lists == null)
            {
                problem = Lang.T("Не вдалося прочитати список досягнень цієї версії гри.");
                return result;
            }
            foreach (object list in lists)
            {
                IEnumerable items = AccessTools.Field(list.GetType(), "m_achievements")?.GetValue(list) as IEnumerable;
                if (items == null)
                {
                    continue;
                }
                foreach (object item in items)
                {
                    if (item is Achievement achievement && !result.Contains(achievement))
                    {
                        result.Add(achievement);
                    }
                }
            }
            return result;
        }

        private static void GiveAchievement(Achievement achievement, bool showPopup)
        {
            // A tainted character or world makes the game ignore the call, so the bypass is on for the moment of the call.
            bool before = State.BypassCheatChecks;
            State.BypassCheatChecks = true;
            try
            {
                AccessTools.Method(typeof(Achievements), "AchievementEvent")?.Invoke(null, new object[] { achievement, true, showPopup });
            }
            finally
            {
                State.BypassCheatChecks = before;
            }
        }

        private void DrawAchievementList()
        {
            if (m_achievementRows == null || (Event.current.type == EventType.Layout && Time.unscaledTime >= m_nextAchievementRead))
            {
                m_nextAchievementRead = Time.unscaledTime + 1f;
                List<Achievement> all = ReadGameAchievements(out m_achievementProblem);
                m_achievementRows = new List<AchievementRow>();
                foreach (Achievement achievement in all)
                {
                    m_achievementRows.Add(new AchievementRow
                    {
                        Source = achievement,
                        Name = Localization.instance.Localize(achievement.m_name),
                        Description = Localization.instance.Localize(achievement.m_description),
                        Unlocked = achievement.m_unlocked,
                        Steam = SteamState(achievement),
                    });
                }
            }
            GUILayout.Label(Lang.T("<b>Список досягнень</b>"));
            if (m_achievementProblem != null)
            {
                GUILayout.Label("<color=#c0c0c0>" + m_achievementProblem + "</color>");
                GUILayout.Space(8f);
                return;
            }
            int done = 0, steamDone = 0, steamKnown = 0;
            foreach (AchievementRow row in m_achievementRows)
            {
                done += row.Unlocked ? 1 : 0;
                steamKnown += row.Steam.HasValue ? 1 : 0;
                steamDone += row.Steam == true ? 1 : 0;
            }
            GUILayout.Label(Lang.F("Виконано {0} з {1}", done, m_achievementRows.Count) +
                (steamKnown > 0 ? Lang.F(" · у Steam: {0} з {1}", steamDone, m_achievementRows.Count)
                    : Lang.T(" · <color=#c0c0c0>Steam не відповідає</color>")));
            int missing = 0;
            foreach (AchievementRow row in m_achievementRows)
            {
                missing += !row.Unlocked || row.Steam == false ? 1 : 0;
            }
            if (missing > 0 && ConfirmButton("giveallachievements", Lang.T("Виконати всі, що ще не виконані")))
            {
                int given = 0;
                foreach (AchievementRow row in m_achievementRows)
                {
                    if (!row.Unlocked || row.Steam == false)
                    {
                        GiveAchievement(row.Source, showPopup: false);
                        given++;
                    }
                }
                ShowMessage(Lang.F("Видано досягнень: {0}. Якщо якесь не з'явилось у списку, гра його не прийняла.", given));
                m_nextAchievementRead = 0f;
            }
            foreach (AchievementRow row in m_achievementRows)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label((row.Unlocked ? "<color=#60d060>✔</color> " : "<color=#808080>○</color> ") + row.Name, GUILayout.Width(300f));
                GUILayout.Label("<color=#c0c0c0>" + row.Description + "</color>", GUILayout.Width(360f));
                GUILayout.Label(row.Steam == true ? "<color=#60d060>Steam ✔</color>"
                    : row.Steam == false ? "<color=#808080>Steam ○</color>" : "", GUILayout.Width(80f));
                if (!(row.Unlocked && row.Steam != false) && GUILayout.Button(Lang.T("Виконати")))
                {
                    GiveAchievement(row.Source, showPopup: true);
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(8f);
        }
    }
}
