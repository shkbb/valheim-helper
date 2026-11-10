using System.Collections.Generic;
using UnityEngine;

namespace ValheimHelper
{
    // "Світ і час" tab and the speed section of the movement tab.
    internal sealed partial class CheatMenu
    {
        private static readonly string[][] s_quickWeathers =
        {
            new[] { "Clear", Lang.T("Ясно") },
            new[] { "Twilight_Clear", Lang.T("Ясна ніч") },
            new[] { "LightRain", Lang.T("Легкий дощ") },
            new[] { "Rain", Lang.T("Дощ") },
            new[] { "ThunderStorm", Lang.T("Гроза") },
            new[] { "Misty", Lang.T("Туман") },
            new[] { "Snow", Lang.T("Сніг") },
            new[] { "SnowStorm", Lang.T("Хуртовина") },
        };

        private static readonly float[] s_clockPresets = { 6f, 12f, 18f, 0f };
        private static readonly string[] s_clockPresetLabels = { Lang.T("06:00 ранок"), Lang.T("12:00 день"), Lang.T("18:00 вечір"), Lang.T("00:00 ніч") };

        private float m_clockHours = 12f;
        private bool m_showAllWeathers;

        private void DrawSpeedSection()
        {
            GUILayout.Space(10f);
            GUILayout.Label(Lang.T("<b>Швидкість</b>"));
            State.RunSpeedMultiplier = MultiplierSlider(Lang.T("Біг, ходьба, плавання"), State.RunSpeedMultiplier, 1f, 5f, 0.25f, "run");

            GUILayout.Space(10f);
            GUILayout.Label(Lang.T("<b>Під водою</b>"));
            State.WalkUnderwater = GUILayout.Toggle(State.WalkUnderwater, Lang.T(" Ходити по дну: не спливати й тримати в руках кайло, молот, мотику чи зброю") + Binds.Hint("underwater"));
            Bindable("underwater");
            GUILayout.Label(Lang.T("<color=#c0c0c0>Під водою можна копати кайлом, будувати, рівняти землю, рубати й битися, як на суші. Щоб спливти, вимкни режим.\n" +
                "Гра дозволяє змінювати землю лише на ±8 м від її початкової висоти, тож глибше за це не прокопаєш.</color>"));
        }

        private void DrawWorldTab()
        {
            if (ZNet.instance == null || EnvMan.instance == null)
            {
                GUILayout.Label(Lang.T("Зайди у світ, щоб керувати часом, погодою і лутом."));
                return;
            }
            bool isHost = ZNet.instance.IsServer();

            GUILayout.Label(Lang.T("<b>Час доби</b>"));
            if (WorldTools.TryGetWorldClock(out float now, out int day))
            {
                string pinned = WorldTools.IsLocalTimeFixed
                    ? Lang.F("   <color=#ffd080>(у тебе зафіксовано {0})</color>", WorldTools.FormatClock(WorldTools.LocalFixedHours))
                    : "";
                GUILayout.Label(Lang.F("Зараз у світі: {0}, день {1}{2}", WorldTools.FormatClock(now), day, pinned));
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.F("Обраний час: {0}", WorldTools.FormatClock(m_clockHours)), GUILayout.Width(180f));
            m_clockHours = Mathf.Round(GUILayout.HorizontalSlider(m_clockHours, 0f, 23.75f, GUILayout.Width(300f)) * 4f) / 4f;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            for (int i = 0; i < s_clockPresets.Length; i++)
            {
                if (GUILayout.Button(s_clockPresetLabels[i]))
                {
                    m_clockHours = s_clockPresets[i];
                }
            }
            GUILayout.EndHorizontal();

            if (isHost)
            {
                if (GUILayout.Button(Lang.T("Перемотати час світу до обраного (для всіх, як сон)")))
                {
                    ShowMessage(WorldTools.SkipWorldClockTo(m_clockHours));
                }
            }
            else
            {
                GUILayout.Label(Lang.T("<color=#c0c0c0>Перемотувати справжній час світу може лише хост.</color>"));
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Lang.T("Зафіксувати обраний час лише в мене")))
            {
                WorldTools.SetLocalTime(m_clockHours);
                ShowMessage(Lang.T("Час зафіксовано: ") + WorldTools.FormatClock(m_clockHours));
            }
            if (Bindable(GUILayout.Button(Lang.T("Повернути справжній час") + Binds.Hint("timereal")), "timereal"))
            {
                WorldTools.SetLocalTime(-1f);
                ShowMessage(Lang.T("Час доби знову йде як у світі."));
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Lang.T("<color=#c0c0c0>«Перемотати» змінює справжній час світу: минає доба, ростуть рослини, доплавляється руда.\n" +
                "«Зафіксувати» змінює лише освітлення і день/ніч у твоїй грі, а світ живе далі.</color>"));

            GUILayout.Space(10f);
            GUILayout.Label(Lang.T("<b>Прискорення часу</b>"));
            if (isHost)
            {
                State.WorldClockMultiplier = MultiplierSlider(Lang.T("Час світу (печі, грядки, доба)"), State.WorldClockMultiplier, 1f, 30f, 1f, "worldclock");
            }
            else
            {
                GUILayout.Label(Lang.T("<color=#c0c0c0>Прискорювати час світу може лише хост.</color>"));
            }
            State.GameSpeed = MultiplierSlider(Lang.T("Уся гра, як speedhack"), State.GameSpeed, 1f, 5f, 0.25f, "gamespeed");
            GUILayout.Label(Lang.T("<color=#c0c0c0>Час світу прискорює печі, ферментери, коптильні, грядки і зміну доби, а ти рухаєшся як звичайно.\n" +
                "Speedhack прискорює все у твоїй грі, і тебе теж. Великі значення навантажують процесор.</color>"));

            GUILayout.Space(10f);
            GUILayout.Label(Lang.T("<b>Погода</b>"));
            string forced = WorldTools.WeatherOverride;
            GUILayout.Label(Lang.F("Зараз: {0}", WorldTools.CurrentWeather) + (string.IsNullOrEmpty(forced)
                ? Lang.T("  <color=#c0c0c0>(як задумано грою)</color>")
                : Lang.T("  <color=#ffd080>(зафіксовано модом)</color>")));
            List<string> names = WorldTools.GetWeatherNames();
            int column = 0;
            GUILayout.BeginHorizontal();
            foreach (string[] weather in s_quickWeathers)
            {
                if (!names.Contains(weather[0]))
                {
                    continue;
                }
                if (column > 0 && column % 4 == 0)
                {
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                }
                column++;
                if (Bindable(GUILayout.Button(weather[1] + Binds.Hint("weather_" + weather[0])), "weather_" + weather[0]))
                {
                    WorldTools.SetWeather(weather[0]);
                }
            }
            GUILayout.EndHorizontal();
            if (Bindable(GUILayout.Button(Lang.T("Звичайна погода гри") + Binds.Hint("weather")), "weather"))
            {
                WorldTools.SetWeather("");
            }
            m_showAllWeathers = GUILayout.Toggle(m_showAllWeathers, Lang.T(" Показати всі погоди гри"));
            if (m_showAllWeathers)
            {
                for (int i = 0; i < names.Count; i += 3)
                {
                    GUILayout.BeginHorizontal();
                    for (int j = i; j < Mathf.Min(i + 3, names.Count); j++)
                    {
                        if (GUILayout.Button(names[j]))
                        {
                            WorldTools.SetWeather(names[j]);
                        }
                    }
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.Label(Lang.T("<color=#c0c0c0>Погоду кожна гра рахує сама, за біомом, де стоїть гравець. Обрана погода діє в тебе по всій карті\n" +
                "(і в підземеллях), поки не натиснеш «Звичайна погода гри».</color>"));

            GUILayout.Space(10f);
            GUILayout.Label(Lang.T("<b>Синхронізація з другом</b>"));
            State.SyncWithFriends = GUILayout.Toggle(State.SyncWithFriends,
                Lang.T(" Передавати другу погоду, зафіксований час і множник луту («Змінники»), якщо в нього теж стоїть мод") + Binds.Hint("sync"));
            Bindable("sync");
            if (State.SyncWithFriends && GUILayout.Button(Lang.T("Надіслати другу поточні налаштування")))
            {
                WorldTools.SendCurrentSettings();
                ShowMessage(Lang.T("Налаштування надіслано."));
            }
        }

        private static float MultiplierSlider(string label, float value, float min, float max, float step, string bindId = null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{label}: ×{value:0.##}" + (bindId != null ? Binds.Hint(bindId) : ""), GUILayout.Width(330f));
            if (bindId != null)
            {
                Bindable(bindId);
            }
            float result = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(230f));
            if (GUILayout.Button("×1", GUILayout.Width(45f)))
            {
                result = 1f;
            }
            GUILayout.EndHorizontal();
            return Mathf.Clamp(Mathf.Round(result / step) * step, min, max);
        }
    }
}
