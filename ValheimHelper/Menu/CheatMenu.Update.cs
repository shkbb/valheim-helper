using UnityEngine;

namespace ValheimHelper
{
    // "Оновлення": the version and edition of this build, the update check and where to get another edition.
    internal sealed partial class CheatMenu
    {
        private void DrawUpdateTab()
        {
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Версія"), HelperSkin.Header);
            GUILayout.Label(Lang.F("Valheim Helper {0}, редакція «{1}»", BuildInfo.VersionText, BuildInfo.EditionName));
#if NEXUS
            GUILayout.Label(Lang.T("<color=#c0c0c0>Нові версії — на сторінці мода в Nexus Mods (і в менеджерах модів).</color>"));
#else
            Updater.AutoCheck = GUILayout.Toggle(Updater.AutoCheck, Lang.T(" Перевіряти оновлення під час запуску гри"));
            GUI.enabled = !Updater.Running;
            if (GUILayout.Button(Lang.T("Перевірити зараз")))
            {
                Updater.Start(byHand: true);
            }
            GUI.enabled = true;
            if (!string.IsNullOrEmpty(Updater.Status))
            {
                GUILayout.Label("<color=#c0c0c0>" + Updater.Status + "</color>");
            }
            GUILayout.Label(Lang.T("<color=#c0c0c0>Нова версія завантажується сама й встановлюється під час наступного запуску гри. " +
                "Довідник оновлюється окремо, просто з вікі.</color>"));
#endif
            GUILayout.EndVertical();

            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Інша редакція"), HelperSkin.Header);
            GUILayout.Label(Lang.T("<color=#c0c0c0>«Легальна» — карта, довідник, камера, статистика, список досягнень. «Помічник» — ще й скрині, " +
                "печі, факели, крафт зі скринь. «Повна» — ще й чити. Щоб змінити редакцію, завантаж її архів і встанови поверх.</color>"));
            if (GUILayout.Button(Lang.T("Відкрити сторінку випусків")))
            {
                Updater.OpenReleases();
            }
            GUILayout.EndVertical();
        }
    }
}
