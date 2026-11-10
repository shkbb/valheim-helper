using System.Collections.Generic;
using UnityEngine;
using ValheimHelper.Perf;

namespace ValheimHelper
{
    // "Швидкодія" tab: the render layer, A/B tests, the frame profiler and what is loaded around.
    internal sealed partial class CheatMenu
    {
        private static readonly string[] s_presetNames = { LodRules.Presets[0].Name, LodRules.Presets[1].Name, LodRules.Presets[2].Name };
        private float m_fpsSmoothed;
        private string m_perfLive = "";
        private string m_layerStats = "";
        private bool m_profilerShown;
        private bool m_benchShown;
        private string[] m_sceneLines = new string[0];
        private string[] m_featureLines = new string[0];

        private static readonly string[] s_frameLimits = { "—", "30", "60", "90", "120", "144", "240" };
        private static readonly int[] s_frameLimitValues = { 0, 30, 60, 90, 120, 144, 240 };

        // What the mod counts in the background, and a frame limit: the two things that change frames the most.
        private void DrawHeavyWork()
        {
            bool background = GUILayout.Toggle(PerfSettings.MapBackground, Lang.T(" Рахувати мапу у фоні (ресурси світу, вміст локацій, об'єкти світу)"));
            if (background != PerfSettings.MapBackground)
            {
                PerfSettings.MapBackground = background;
                PerfSettings.Changed();
            }
            GUILayout.Label(Lang.T("<color=#c0c0c0>Найважче, що робить мод. Вимкни, якщо ловиш просідання: мапа лишиться з тим, що вже пораховано. " +
                "Обчислення все одно йдуть лише тоді, коли відкрита велика мапа.</color>"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Межа кадрів:"), GUILayout.Width(130f));
            int current = System.Array.IndexOf(s_frameLimitValues, PerfSettings.FrameLimit);
            int chosen = GUILayout.Toolbar(current < 0 ? 0 : current, s_frameLimits, GUILayout.Width(330f));
            if (chosen != current)
            {
                PerfSettings.FrameLimit = s_frameLimitValues[Mathf.Clamp(chosen, 0, s_frameLimitValues.Length - 1)];
                PerfSettings.Changed();
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Lang.T("<color=#c0c0c0>Рівний кадр замість стрибків: відеокарта менше гріється, а картинка плавніша. " +
                "Діє, коли вертикальна синхронізація в налаштуваннях гри вимкнена.</color>"));
        }

        private void DrawPerfTab()
        {
            LodPreset preset = PerfSettings.Current;
            if (Event.current.type == EventType.Layout)
            {
                float dt = Mathf.Max(0.0001f, Time.unscaledDeltaTime);
                m_fpsSmoothed = m_fpsSmoothed <= 0f ? 1f / dt : Mathf.Lerp(m_fpsSmoothed, 1f / dt, 0.05f);
                m_perfLive = Lang.F("Зараз: <b>{0:0} FPS</b> ({1:0.0} мс на кадр). " +
                    "Поки це меню відкрите, воно саме трохи гальмує гру, тож тести закривають його.", m_fpsSmoothed, 1000f / Mathf.Max(1f, m_fpsSmoothed));
                m_layerStats = Lang.F("Частин будівель у шарі: {0}. Не малюються (менші за кілька пікселів): {1}, без тіні: {2}.\n" +
                    "Приспано вогнів: {3} (систем частинок на паузі: {4}), заморожене мерехтіння: {5}. " +
                    "Звуків на паузі: {6}.\n" +
                    "Світла, які гра тримала б увімкненими: {7}, вимкнено шаром: {8}, з тінню: {9}. " +
                    "Матеріалів з інстансингом: {10}. Шар коштує: {11:0.00} мс на кадр.", RenderLayer.PieceCount, RenderLayer.HiddenCount, RenderLayer.NoShadowCount, RenderLayer.SleepingFires, RenderLayer.PausedSystems, RenderLayer.FrozenFlickers, RenderLayer.SilencedSounds, RenderLayer.LightsConsidered, RenderLayer.LightsOff, RenderLayer.ShadowLightsKept, RenderLayer.InstancedMaterials, RenderLayer.LastCostMs);
                m_profilerShown = FrameProfiler.Stages.Count > 0 || FrameProfiler.Running;
                m_benchShown = Bench.Running;
                m_sceneLines = SceneStats.Lines.ToArray();
                m_featureLines = Bench.FeatureResult.ToArray();
            }

            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Шар оптимізації Valheim Helper"), HelperSkin.Header);
            GUILayout.Label(Lang.T("<color=#c0c0c0>Власний шар мода поверх налаштувань гри. Він змінює лише те, <i>як</i> база малюється й звучить, а не те, що в ній відбувається: " +
                "будівлі, опора, знос, вогонь, дим і печі працюють точно як у грі, тож досягнення й «чесність» світу не зачіпаються.\n" +
                "• <b>Інстансинг</b>: однакові стіни, балки, підлоги йдуть на відеокарту пачками.\n" +
                "• <b>Дрібниці далеко</b>: те, що на екрані менше кількох пікселів, не малюється, а дрібне не кидає тінь.\n" +
                "• <b>Вогонь поза кадром</b>: частинки невидимих вогнів на паузі, далекі світла не мерехтять.\n" +
                "• <b>Звук</b>: тріск вогнів і гул станцій, яких уже не чути, стоїть на паузі.\n" +
                "• <b>Бюджет світла</b>: коли світел більше за межу, гаснуть найменш помітні (спершу поза кадром), близькі до тебе горять завжди; тінь — лише в найпомітніших.</color>"));
            GUILayout.Label(m_perfLive);
            DrawHeavyWork();
            bool enabled = GUILayout.Toggle(PerfSettings.Enabled, Lang.T(" Увімкнути шар оптимізації") + Binds.Hint("perflayer"));
            Bindable("perflayer");
            if (enabled != PerfSettings.Enabled)
            {
                PerfSettings.Enabled = enabled;
                PerfSettings.Changed();
            }
            PerfToggle(ref PerfSettings.Instancing, Lang.T(" Інстансинг матеріалів будівель"));
            PerfToggle(ref PerfSettings.DetailLod, Lang.T(" Не малювати дрібниці далеко й прибирати їм тіні"));
            PerfToggle(ref PerfSettings.FireLod, Lang.T(" Приспати вогонь поза кадром, далекі світла без мерехтіння"));
            PerfToggle(ref PerfSettings.SoundLod, Lang.T(" Пауза для звуків, яких не чути"));
            PerfToggle(ref PerfSettings.LightBudget, Lang.F(" Бюджет світла: до {0} світел (у радіусі {1:0} м — завжди), тіні до {2}", preset.Lights, preset.NearLights, preset.ShadowLights));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Сила:"), GUILayout.Width(60f));
            int presetIndex = GUILayout.SelectionGrid(PerfSettings.Preset, s_presetNames, 3);
            if (presetIndex != PerfSettings.Preset)
            {
                PerfSettings.Preset = presetIndex;
                PerfSettings.Changed();
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("<color=#c0c0c0>" + m_layerStats + "</color>");
            GUILayout.EndVertical();

            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Перевірити на своїй базі"), HelperSkin.Header);
            GUILayout.Label(Lang.T("<color=#c0c0c0>Стань там, де гальмує, і не рухайся: меню закриється, результат з'явиться зверху зліва й тут.\n" +
                "• <b>Детальний тест</b> (≈55 с): FPS без шару, потім кожна функція окремо, усе разом і знову без шару. Видно, що саме дає приріст, а що ні.\n" +
                "• <b>Сам мод</b> (≈26 с): вимикає компоненти мода (меню, карту, склад, клавіші); патчі гри — однорядкові перевірки, вони лишаються.</color>"));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(m_benchShown ? Lang.F("Тест… {0:0}%", Bench.Progress * 100f) : Lang.T("Детальний тест"), GUILayout.Height(30f)) && !m_benchShown)
            {
                ShowPerfMessage(Bench.Start(Bench.Kind.Features));
            }
            if (GUILayout.Button(m_benchShown ? Lang.T("Зачекай…") : Lang.T("Скільки забирає сам мод"), GUILayout.Height(30f)) && !m_benchShown)
            {
                ShowPerfMessage(Bench.Start(Bench.Kind.Mod));
            }
            GUILayout.EndHorizontal();
            if (m_featureLines.Length > 0)
            {
                GUILayout.Label(Lang.T("<color=#e8d8b0>Детальний тест:</color>"));
                foreach (string line in m_featureLines)
                {
                    GUILayout.Label("  " + line);
                }
            }
            GUILayout.Label(string.IsNullOrEmpty(Bench.ModResult) ? Lang.T("<color=#909090>Мод ще не перевіряли.</color>") : Lang.T("<color=#e8d8b0>Мод:</color> ") + Bench.ModResult);
            GUILayout.EndVertical();

            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Що саме гальмує: замір кадру"), HelperSkin.Header);
            GUILayout.Label(Lang.T("<color=#c0c0c0>8 секунд міряється кожен етап кадру Unity (фізика, скрипти, частинки, рендер, інтерфейс) і головні системи гри: " +
                "будівлі, мережа, персонажі, дим, звук. Меню закриється на час заміру. Результат також пишеться у ValheimHelper.log.</color>"));
            if (GUILayout.Button(FrameProfiler.Running ? Lang.F("Міряю… {0:0}%", FrameProfiler.Progress * 100f) : Lang.T("Заміряти кадр (8 с)"), GUILayout.Height(30f))
                && !FrameProfiler.Running && !Bench.Running && Player.m_localPlayer != null)
            {
                FrameProfiler.Start();
                State.MenuOpen = false;
            }
            if (m_profilerShown && !FrameProfiler.Running)
            {
                GUILayout.Label("<b>" + FrameProfiler.Summary + "</b>");
                GUILayout.Label(Lang.T("<color=#e8d8b0>Етапи кадру (мс на кадр):</color>"));
                PerfLines(FrameProfiler.Stages, 14, "0.00");
                GUILayout.Label(Lang.T("<color=#e8d8b0>Системи гри:</color>"));
                PerfLines(FrameProfiler.Systems, 40, "0.00");
                GUILayout.Label(Lang.T("<color=#e8d8b0>Компоненти мода:</color>"));
                PerfLines(FrameProfiler.Mod, 8, "0.000");
            }
            GUILayout.EndVertical();

            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Що завантажено навколо"), HelperSkin.Header);
            if (GUILayout.Button(Lang.T("Порахувати"), GUILayout.Height(28f)) && Player.m_localPlayer != null)
            {
                SceneStats.Scan();
            }
            foreach (string line in m_sceneLines)
            {
                GUILayout.Label(line);
            }
            if (m_sceneLines.Length > 0)
            {
                GUILayout.Label(Lang.F("<color=#909090>Підрахунок зайняв {0:0} мс.</color>", SceneStats.ScanMs));
            }
            GUILayout.EndVertical();
        }

        private static void PerfToggle(ref bool value, string label)
        {
            bool result = GUILayout.Toggle(value, label);
            if (result != value)
            {
                value = result;
                PerfSettings.Changed();
            }
        }

        private static void PerfLines(List<FrameProfiler.Line> lines, int max, string format)
        {
            int shown = 0;
            foreach (FrameProfiler.Line line in lines)
            {
                if (shown++ >= max)
                {
                    break;
                }
                string calls = line.Calls >= 2 ? $"  <color=#909090>×{line.Calls:0}</color>" : "";
                GUILayout.Label(Lang.F("  {0} мс — {1}{2}", line.Ms.ToString(format), line.Name, calls));
            }
            if (shown == 0)
            {
                GUILayout.Label(Lang.T("  <color=#909090>нічого помітного</color>"));
            }
        }

        private void ShowPerfMessage(string message)
        {
            if (!string.IsNullOrEmpty(message))
            {
                ShowMessage(message);
            }
        }
    }
}
