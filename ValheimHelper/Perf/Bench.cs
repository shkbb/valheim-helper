using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ValheimHelper.Perf
{
    // A/B tests right in the game, while the player stands still. Every window starts with a pause so the switch
    // itself (and the game fading lights) is not counted.
    //  - Layer: "without, with, without, with" the render layer as set in the tab.
    //  - Mod: the same with the mod's own components paused.
    //  - Features: nothing, then each feature of the layer alone, then all of them, then nothing again.
    internal static class Bench
    {
        internal enum Kind
        {
            Layer,
            Mod,
            Features,
        }

        private sealed class Window
        {
            public string Name;
            // Layer windows: -1 = as set in the tab, 0 = off; feature windows: exactly these features.
            public int Mask;
            public bool ModPaused;
            public float Settle;
            public float Length;
            public readonly List<float> Frames = new List<float>();
        }

        internal static bool Running { get; private set; }
        internal static string LayerResult = "";
        internal static string ModResult = "";
        internal static readonly List<string> FeatureResult = new List<string>();
        internal static float Progress
        {
            get
            {
                if (!Running || s_windows.Count == 0)
                {
                    return 0f;
                }
                Window window = s_windows[Mathf.Min(s_index, s_windows.Count - 1)];
                float inWindow = Mathf.Clamp01((Time.unscaledTime - s_windowStart) / (window.Settle + window.Length));
                return Mathf.Clamp01((s_index + inWindow) / s_windows.Count);
            }
        }

        private static Kind s_kind;
        private static int s_index;
        private static float s_windowStart;
        private static readonly List<Window> s_windows = new List<Window>();
        private static readonly List<Behaviour> s_paused = new List<Behaviour>();

        internal static string Start(Kind kind)
        {
            if (Running || FrameProfiler.Running)
            {
                return Lang.T("Уже триває замір, зачекай.");
            }
            if (Player.m_localPlayer == null)
            {
                return Lang.T("Зайди у світ.");
            }
            if (kind == Kind.Layer && !PerfSettings.Enabled)
            {
                return Lang.T("Спершу увімкни шар оптимізації.");
            }
            s_windows.Clear();
            switch (kind)
            {
                case Kind.Layer:
                    for (int i = 0; i < 2; i++)
                    {
                        s_windows.Add(new Window { Name = Lang.T("без шару"), Mask = 0, Settle = 2.5f, Length = 5f });
                        s_windows.Add(new Window { Name = Lang.T("з шаром"), Mask = -1, Settle = 2.5f, Length = 5f });
                    }
                    break;
                case Kind.Mod:
                    for (int i = 0; i < 2; i++)
                    {
                        s_windows.Add(new Window { Name = Lang.T("без мода"), Mask = -1, ModPaused = true, Settle = 1.5f, Length = 5f });
                        s_windows.Add(new Window { Name = Lang.T("з модом"), Mask = -1, Settle = 1.5f, Length = 5f });
                    }
                    break;
                case Kind.Features:
                    s_windows.Add(new Window { Name = Lang.T("без шару"), Mask = 0, Settle = 2.5f, Length = 4f });
                    s_windows.Add(new Window { Name = Lang.T("інстансинг"), Mask = RenderLayer.FeatureInstancing, Settle = 2.5f, Length = 4f });
                    s_windows.Add(new Window { Name = Lang.T("дрібниці й тіні"), Mask = RenderLayer.FeatureDetail, Settle = 2.5f, Length = 4f });
                    s_windows.Add(new Window { Name = Lang.T("вогонь і мерехтіння"), Mask = RenderLayer.FeatureFire, Settle = 2.5f, Length = 4f });
                    s_windows.Add(new Window { Name = Lang.T("звук"), Mask = RenderLayer.FeatureSound, Settle = 2.5f, Length = 4f });
                    s_windows.Add(new Window { Name = Lang.T("бюджет світла"), Mask = RenderLayer.FeatureLights, Settle = 3f, Length = 4f });
                    s_windows.Add(new Window { Name = Lang.T("усе разом"), Mask = RenderLayer.AllFeatures, Settle = 3f, Length = 4f });
                    s_windows.Add(new Window { Name = Lang.T("без шару"), Mask = 0, Settle = 3f, Length = 4f });
                    break;
            }
            Running = true;
            s_kind = kind;
            s_index = 0;
            State.MenuOpen = false;
            BeginWindow();
            float total = 0f;
            foreach (Window window in s_windows)
            {
                total += window.Settle + window.Length;
            }
            Message(Lang.F("Тест швидкодії ≈ {0:0} с: стій на місці й не крути камеру.", total));
            return null;
        }

        // Called every frame by the render layer, which itself is never paused by a test.
        internal static void Tick()
        {
            FrameProfiler.Tick();
            if (!Running)
            {
                return;
            }
            if (Player.m_localPlayer == null)
            {
                Finish(aborted: true);
                return;
            }
            Window window = s_windows[s_index];
            float elapsed = Time.unscaledTime - s_windowStart;
            if (elapsed >= window.Settle)
            {
                window.Frames.Add(Time.unscaledDeltaTime);
            }
            if (elapsed >= window.Settle + window.Length)
            {
                s_index++;
                if (s_index >= s_windows.Count)
                {
                    Finish(aborted: false);
                    return;
                }
                BeginWindow();
            }
        }

        private static void BeginWindow()
        {
            s_windowStart = Time.unscaledTime;
            Window window = s_windows[s_index];
            if (s_kind == Kind.Features)
            {
                RenderLayer.BenchMask = window.Mask;
            }
            else
            {
                RenderLayer.ForceOff = window.Mask == 0;
            }
            PauseMod(window.ModPaused);
        }

        private static void PauseMod(bool pause)
        {
            if (pause)
            {
                if (s_paused.Count > 0 || Bootstrap.Root == null)
                {
                    return;
                }
                foreach (MonoBehaviour behaviour in Bootstrap.Root.GetComponents<MonoBehaviour>())
                {
                    if (!(behaviour is RenderLayer) && behaviour.enabled)
                    {
                        behaviour.enabled = false;
                        s_paused.Add(behaviour);
                    }
                }
                return;
            }
            foreach (Behaviour behaviour in s_paused)
            {
                if (behaviour != null)
                {
                    behaviour.enabled = true;
                }
            }
            s_paused.Clear();
        }

        private static List<float> Frames(string name)
        {
            List<float> frames = new List<float>();
            foreach (Window window in s_windows)
            {
                if (window.Name == name)
                {
                    frames.AddRange(window.Frames);
                }
            }
            return frames;
        }

        private static void Finish(bool aborted)
        {
            Running = false;
            PauseMod(false);
            RenderLayer.ForceOff = false;
            RenderLayer.BenchMask = -1;
            if (aborted)
            {
                return;
            }
            string text;
            switch (s_kind)
            {
                case Kind.Layer:
                {
                    LodRules.FrameStats(Frames(Lang.T("без шару")), out float withoutFps, out float withoutLow, out float withoutMs);
                    LodRules.FrameStats(Frames(Lang.T("з шаром")), out float withFps, out float withLow, out float withMs);
                    float change = withoutFps > 0f ? (withFps - withoutFps) / withoutFps * 100f : 0f;
                    text = Lang.F("Без шару: {0:0.0} FPS (1% найгірших {1:0}), {2:0.0} мс. " +
                           "З шаром: {3:0.0} FPS (1% найгірших {4:0}), {5:0.0} мс. Різниця: {6:+0;-0}%.", withoutFps, withoutLow, withoutMs, withFps, withLow, withMs, change);
                    LayerResult = text;
                    break;
                }
                case Kind.Mod:
                {
                    LodRules.FrameStats(Frames(Lang.T("без мода")), out float withoutFps, out _, out float withoutMs);
                    LodRules.FrameStats(Frames(Lang.T("з модом")), out float withFps, out _, out float withMs);
                    float cost = withoutFps > 0f ? (withoutFps - withFps) / withoutFps * 100f : 0f;
                    string verdict = Mathf.Abs(cost) < 2f ? Lang.T("у межах похибки") : cost > 0f ? Lang.T("мод забирає частину кадру") : Lang.T("з модом навіть швидше, тобто це шум");
                    text = Lang.F("Без компонентів мода: {0:0.0} FPS ({1:0.00} мс). З модом: {2:0.0} FPS ({3:0.00} мс). " +
                           "Мод забирає: {4:+0.0;-0.0}% — {5}.", withoutFps, withoutMs, withFps, withMs, cost, verdict);
                    ModResult = text;
                    break;
                }
                default:
                {
                    FeatureResult.Clear();
                    LodRules.FrameStats(Frames(Lang.T("без шару")), out float baseFps, out float baseLow, out float baseMs);
                    // The two "without" windows show how much the scene itself drifted during the test.
                    LodRules.FrameStats(s_windows[0].Frames, out float firstFps, out _, out _);
                    LodRules.FrameStats(s_windows[s_windows.Count - 1].Frames, out float lastFps, out _, out _);
                    float noise = baseFps > 0f ? Mathf.Abs(firstFps - lastFps) / baseFps * 100f : 0f;
                    FeatureResult.Add(Lang.F("Без шару: {0:0.0} FPS ({1:0.0} мс, 1% найгірших {2:0}). Розкид між початком і кінцем тесту: {3:0.0}%.", baseFps, baseMs, baseLow, noise));
                    StringBuilder log = new StringBuilder($"base {baseFps:0.0} fps, noise {noise:0.0}%");
                    for (int i = 1; i < s_windows.Count - 1; i++)
                    {
                        Window window = s_windows[i];
                        LodRules.FrameStats(window.Frames, out float fps, out float low, out float ms);
                        float change = baseFps > 0f ? (fps - baseFps) / baseFps * 100f : 0f;
                        FeatureResult.Add(Lang.F("{0}: {1:0.0} FPS ({2:+0.0;-0.0}%), {3:0.0} мс ({4:+0.00;-0.00}), 1% найгірших {5:0}.", window.Name, fps, change, ms, ms - baseMs, low));
                        log.Append($"; {window.Name} {fps:0.0} fps ({change:+0.0;-0.0}%)");
                    }
                    text = log.ToString();
                    break;
                }
            }
            Log.Info("Bench " + s_kind + ": " + text);
            Message(s_kind == Kind.Features ? Lang.T("Детальний тест готовий, результат у «Налаштування» → «Швидкодія».") : text);
        }

        private static void Message(string text)
        {
            Player player = Player.m_localPlayer;
            if (player != null)
            {
                player.Message(MessageHud.MessageType.TopLeft, text);
            }
        }
    }
}
