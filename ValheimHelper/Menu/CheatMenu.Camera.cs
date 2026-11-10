using UnityEngine;

namespace ValheimHelper
{
    // "Камера": the free camera for screenshots.
    internal sealed partial class CheatMenu
    {
        private void DrawCameraTab()
        {
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Вільна камера"), HelperSkin.Header);
            GUILayout.Label(Lang.T("<color=#c0c0c0>Для гарних знімків: персонаж лишається на місці, інтерфейс і мінімапа ховаються, а камера літає. " +
                "Миша — огляд, клавіші ходьби — рух, стрибок і присід — угору й униз, біг — утричі швидше, коліщатко — швидкість. " +
                "F11 — знімок гри (папка screenshots у сейвах). Щоб повернутися — Esc (або та сама клавіша, якщо призначиш її в " +
                "«Налаштування» → «Клавіші» — «Вільна камера»).\n" +
                "Це власна камера мода, а не консольна freefly, тож досягнень не зачіпає. Світ довкола вантажиться там, де стоїть персонаж: " +
                "далеко від нього місцевість буде порожньою. Поки камера літає, персонаж беззахисний — не вмикай її біля ворогів.</color>"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.F("Швидкість: {0:0} м/с", FreeCamera.Speed), GUILayout.Width(160f));
            FreeCamera.Speed = Mathf.Round(GUILayout.HorizontalSlider(FreeCamera.Speed, 1f, 200f, GUILayout.Width(300f)));
            GUILayout.EndHorizontal();
            if (Bindable(GUILayout.Button((FreeCamera.Active ? Lang.T("Вимкнути вільну камеру") : Lang.T("Увімкнути вільну камеру")) + Binds.Hint("freecam"), GUILayout.Height(34f)), "freecam"))
            {
                ShowMessage(FreeCamera.Toggle());
            }
            GUILayout.EndVertical();

            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Фоторежим"), HelperSkin.Header);
            GUILayout.Label(Lang.F("<color=#c0c0c0>Вільна камера з панеллю: вікінг стає в позу чи показує емоцію, бере речі в руки, картина має фільтри, " +
                "розмите тло, віньєтку й своє світло, а знімок зберігається без панелі (можна вдвічі-втричі більший за екран). " +
                "Коли камера літає, фоторежим відкриває клавіша {0}; на самому знімку підказок не видно.</color>", PhotoMode.KeyName()));
            if (Bindable(GUILayout.Button((PhotoMode.Active ? Lang.T("Вийти з фоторежиму") : Lang.T("Увімкнути фоторежим")) + Binds.Hint("photomode"), GUILayout.Height(34f)), "photomode"))
            {
                ShowMessage(PhotoMode.Toggle());
            }
            GUILayout.EndVertical();
        }
    }
}
