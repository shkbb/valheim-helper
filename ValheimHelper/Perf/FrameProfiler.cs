using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.LowLevel;

namespace ValheimHelper.Perf
{
    // "Заміряти кадр": for a few seconds every stage of Unity's frame (physics, scripts, animation, particles,
    // rendering, UI, audio) and the main game systems are timed, then everything is put back. While nothing is being
    // measured the profiler is not installed at all and costs nothing.
    internal static class FrameProfiler
    {
        internal sealed class Line
        {
            public string Name;
            public double Ms;
            public double Calls;
        }

        internal const float Seconds = 8f;
        private const string HarmonyId = "valheimhelper.profiler";

        internal static bool Running { get; private set; }
        internal static float Progress => Running ? Mathf.Clamp01((Time.unscaledTime - s_startedAt) / Seconds) : 0f;
        internal static readonly List<Line> Stages = new List<Line>();
        internal static readonly List<Line> Systems = new List<Line>();
        internal static readonly List<Line> Mod = new List<Line>();
        internal static double FrameMs;
        internal static double Fps;
        internal static double ModMs;
        internal static string Summary = "";

        private static PlayerLoopSystem s_original;
        private static bool s_loopInstalled;
        private static bool s_loopInstalledOnce;
        private static readonly List<string> s_slotNames = new List<string>();
        private static long[] s_slotTicks = new long[0];
        private static int s_current;
        private static long s_last;
        private static int s_frames;
        private static int s_tickFrames;
        private static float s_startedAt;
        private static float s_frameSeconds;

        private sealed class Probe
        {
            public string Name;
            public bool IsMod;
            public long Ticks;
            public long Calls;
        }

        private static readonly Dictionary<MethodBase, Probe> s_probes = new Dictionary<MethodBase, Probe>();
        private static Harmony s_harmony;

        // Game systems worth naming, with what they do. Nested ones are marked with "  └".
        private static readonly string[][] s_gameMethods =
        {
            new[] { "WearNTearUpdater", "Update", Lang.T("Будівлі: знос, опора, дах (WearNTear)") },
            new[] { "ZNet", "Update", Lang.T("Мережа (ZNet)") },
            new[] { "ZDOMan", "Update", Lang.T("  └ синхронізація об'єктів з друзями (ZDOMan)") },
            new[] { "ZNetScene", "Update", Lang.T("Появи й зникнення об'єктів навколо (ZNetScene)") },
            new[] { "ZoneSystem", "Update", Lang.T("Зони світу (ZoneSystem)") },
            new[] { "MonoUpdaters", "FixedUpdate", Lang.T("Персонажі, тварини, ШІ, кораблі (FixedUpdate)") },
            new[] { "Character", "CustomFixedUpdate", Lang.T("  └ персонажі й тварини") },
            new[] { "MonsterAI", "UpdateAI", Lang.T("  └ ШІ мобів і приручених") },
            new[] { "AnimalAI", "UpdateAI", Lang.T("  └ ШІ звірів") },
            new[] { "MonoUpdaters", "Update", Lang.T("Дим, звуки, мерехтіння, верстаки (Update)") },
            new[] { "Smoke", "CustomUpdate", Lang.T("  └ дим") },
            new[] { "SmokeSpawner", "CustomUpdate", Lang.T("  └ джерела диму") },
            new[] { "ZSFX", "CustomUpdate", Lang.T("  └ звуки") },
            new[] { "LightFlicker", "CustomUpdate", Lang.T("  └ мерехтіння світла") },
            new[] { "CraftingStation", "CustomUpdate", Lang.T("  └ верстаки") },
            new[] { "InstanceRenderer", "CustomUpdate", Lang.T("  └ трава й кущі") },
            new[] { "MonoUpdaters", "LateUpdate", Lang.T("Ландшафт, рух об'єктів (LateUpdate)") },
            new[] { "Heightmap", "CustomLateUpdate", Lang.T("  └ ландшафт") },
            new[] { "SmokeRenderer", "LateUpdate", Lang.T("Малювання диму") },
            new[] { "Fireplace", "UpdateFireplace", Lang.T("Вогнища й факели (перевірки)") },
            new[] { "Smelter", "UpdateSmelter", Lang.T("Печі й переробка") },
            new[] { "EnvMan", "Update", Lang.T("Погода, небо, освітлення (EnvMan)") },
            new[] { "Player", "Update", Lang.T("Гравець (Update)") },
            new[] { "Player", "FixedUpdate", Lang.T("Гравець (FixedUpdate)") },
            new[] { "Player", "LateUpdate", Lang.T("Гравець (LateUpdate)") },
            new[] { "GameCamera", "LateUpdate", Lang.T("Камера") },
            new[] { "Game", "Update", "Game" },
            new[] { "Hud", "Update", Lang.T("Інтерфейс: HUD") },
            new[] { "InventoryGui", "Update", Lang.T("Інтерфейс: інвентар") },
            new[] { "Minimap", "Update", Lang.T("Інтерфейс: карта й мінімапа") },
            new[] { "Chat", "Update", Lang.T("Чат") },
            new[] { "AudioMan", "Update", Lang.T("Звук (скрипти)") },
            new[] { "MusicMan", "Update", Lang.T("Музика") },
        };

        // Names of Unity's own frame stages.
        private static readonly Dictionary<string, string> s_stageNames = new Dictionary<string, string>
        {
            ["FixedUpdate.PhysicsFixedUpdate"] = Lang.T("Фізика"),
            ["FixedUpdate.ScriptRunBehaviourFixedUpdate"] = Lang.T("Скрипти FixedUpdate"),
            ["Update.ScriptRunBehaviourUpdate"] = Lang.T("Скрипти Update (гра й мод)"),
            ["Update.ScriptRunDelayedDynamicFrameRate"] = Lang.T("Корутини (LOD світла гри)"),
            ["PreLateUpdate.ScriptRunBehaviourLateUpdate"] = Lang.T("Скрипти LateUpdate"),
            ["PreLateUpdate.DirectorUpdateAnimationBegin"] = Lang.T("Анімації"),
            ["PreLateUpdate.DirectorUpdateAnimationEnd"] = Lang.T("Анімації (кінець)"),
            ["PreLateUpdate.ParticleSystemBeginUpdateAll"] = Lang.T("Частинки: симуляція"),
            ["PostLateUpdate.ParticleSystemEndUpdateAll"] = Lang.T("Частинки: завершення"),
            ["PostLateUpdate.FinishFrameRendering"] = Lang.T("Рендер: підготовка кадру процесором (об'єкти, тіні, світло)"),
            ["PostLateUpdate.UpdateAllRenderers"] = Lang.T("Оновлення рендерерів"),
            ["PostLateUpdate.UpdateAllSkinnedMeshes"] = Lang.T("Скінінг (персонажі, тканина)"),
            ["PostLateUpdate.PlayerUpdateCanvases"] = Lang.T("Інтерфейс гри (розкладка)"),
            ["PostLateUpdate.PlayerEmitCanvasGeometry"] = Lang.T("Інтерфейс гри (геометрія)"),
            ["PostLateUpdate.UpdateAudio"] = Lang.T("Звук (рушій)"),
            ["PostLateUpdate.PresentAfterDraw"] = Lang.T("Показ кадру (очікування відеокарти)"),
            ["PostLateUpdate.PlayerSendFrameComplete"] = Lang.T("Кінець кадру"),
            ["PreUpdate.PhysicsUpdate"] = Lang.T("Фізика (синхронізація)"),
            ["EarlyUpdate.UpdatePreloading"] = Lang.T("Завантаження ресурсів"),
            ["Initialization.PlayerUpdateTime"] = Lang.T("Очікування наступного кадру"),
            ["EarlyUpdate.GpuTimestamp"] = Lang.T("Очікування відеокарти"),
        };

        internal static void Start()
        {
            if (Running)
            {
                return;
            }
            Running = true;
            s_startedAt = Time.unscaledTime;
            s_frames = 0;
            s_tickFrames = 0;
            s_frameSeconds = 0f;
            try
            {
                InstallLoop();
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
            try
            {
                InstallProbes();
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        // Called every frame by the render layer.
        internal static void Tick()
        {
            if (!Running)
            {
                return;
            }
            s_frameSeconds += Time.unscaledDeltaTime;
            s_tickFrames++;
            if (Time.unscaledTime - s_startedAt >= Seconds)
            {
                Stop();
            }
        }

        private static void Stop()
        {
            Running = false;
            try
            {
                if (s_loopInstalled)
                {
                    PlayerLoop.SetPlayerLoop(s_original);
                    s_loopInstalled = false;
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
            try
            {
                s_harmony?.UnpatchSelf();
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
            BuildResults();
        }

        // ---------------------------------------------------------------- frame stages

        private static void InstallLoop()
        {
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            s_original = loop;
            s_slotNames.Clear();
            s_slotNames.Add(Lang.T("Поза кадром Unity"));
            PlayerLoopSystem copy = loop;
            PlayerLoopSystem[] phases = loop.subSystemList ?? new PlayerLoopSystem[0];
            copy.subSystemList = new PlayerLoopSystem[phases.Length];
            for (int p = 0; p < phases.Length; p++)
            {
                PlayerLoopSystem phase = phases[p];
                PlayerLoopSystem[] leaves = phase.subSystemList ?? new PlayerLoopSystem[0];
                List<PlayerLoopSystem> timed = new List<PlayerLoopSystem>();
                string phaseName = phase.type != null ? phase.type.Name : "Phase" + p;
                foreach (PlayerLoopSystem leaf in leaves)
                {
                    int slot = s_slotNames.Count;
                    s_slotNames.Add(phaseName + "." + (leaf.type != null ? leaf.type.Name : "?"));
                    bool frameStart = s_slotNames.Count == 1;
                    timed.Add(Marker(slot, frameStart));
                    timed.Add(leaf);
                }
                // After the last stage of a phase the time goes to "outside".
                timed.Add(Marker(0, false));
                phase.subSystemList = timed.ToArray();
                copy.subSystemList[p] = phase;
            }
            s_slotTicks = new long[s_slotNames.Count];
            s_current = 0;
            s_last = Stopwatch.GetTimestamp();
            PlayerLoop.SetPlayerLoop(copy);
            s_loopInstalled = true;
            s_loopInstalledOnce = true;
        }

        private static PlayerLoopSystem Marker(int slot, bool frameStart)
        {
            return new PlayerLoopSystem
            {
                type = typeof(FrameProfiler),
                updateDelegate = () =>
                {
                    long now = Stopwatch.GetTimestamp();
                    s_slotTicks[s_current] += now - s_last;
                    s_last = now;
                    s_current = slot;
                    if (frameStart)
                    {
                        s_frames++;
                    }
                },
            };
        }

        // ---------------------------------------------------------------- game systems and the mod

        private static void InstallProbes()
        {
            s_probes.Clear();
            s_harmony = s_harmony ?? new Harmony(HarmonyId);
            HarmonyMethod prefix = new HarmonyMethod(AccessTools.Method(typeof(FrameProfiler), nameof(ProbePrefix)));
            HarmonyMethod postfix = new HarmonyMethod(AccessTools.Method(typeof(FrameProfiler), nameof(ProbePostfix)));
            foreach (string[] target in s_gameMethods)
            {
                Type type = AccessTools.TypeByName(target[0]);
                MethodInfo method = type != null ? AccessTools.DeclaredMethod(type, target[1]) : null;
                AddProbe(method, target[2], false, prefix, postfix);
            }
            // Every per-frame method of the mod's own components.
            foreach (Type type in AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly()))
            {
                if (!typeof(MonoBehaviour).IsAssignableFrom(type))
                {
                    continue;
                }
                foreach (string name in new[] { "Update", "LateUpdate", "FixedUpdate", "OnGUI" })
                {
                    AddProbe(AccessTools.DeclaredMethod(type, name), $"{type.Name}.{name}", true, prefix, postfix);
                }
            }
        }

        private static void AddProbe(MethodInfo method, string name, bool isMod, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            if (method == null || method.IsAbstract || s_probes.ContainsKey(method))
            {
                return;
            }
            try
            {
                s_probes[method] = new Probe { Name = name, IsMod = isMod };
                s_harmony.Patch(method, prefix, postfix);
            }
            catch (Exception e)
            {
                s_probes.Remove(method);
                Log.Info($"Profiler: {name} is not measured ({e.Message})");
            }
        }

        private static void ProbePrefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        private static void ProbePostfix(long __state, MethodBase __originalMethod)
        {
            if (s_probes.TryGetValue(__originalMethod, out Probe probe))
            {
                probe.Ticks += Stopwatch.GetTimestamp() - __state;
                probe.Calls++;
            }
        }

        // ---------------------------------------------------------------- results

        private static double ToMs(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        private static void BuildResults()
        {
            Stages.Clear();
            Systems.Clear();
            Mod.Clear();
            // Frames seen by the loop markers; the component tick is the fallback if the loop could not be timed.
            int frames = Math.Max(1, s_loopInstalledOnce && s_frames > 0 ? s_frames : s_tickFrames);
            FrameMs = s_frameSeconds > 0 ? s_frameSeconds * 1000.0 / Math.Max(1, s_tickFrames) : 0;
            Fps = s_frameSeconds > 0 ? s_tickFrames / s_frameSeconds : 0;

            Dictionary<string, Line> stages = new Dictionary<string, Line>();
            for (int i = 0; i < s_slotTicks.Length && i < s_slotNames.Count; i++)
            {
                string raw = s_slotNames[i];
                string name = s_stageNames.TryGetValue(raw, out string known) ? known : raw;
                if (!stages.TryGetValue(name, out Line line))
                {
                    line = new Line { Name = name };
                    stages[name] = line;
                }
                line.Ms += ToMs(s_slotTicks[i]) / frames;
            }
            Stages.AddRange(stages.Values);
            Stages.RemoveAll(line => line.Ms < 0.05);
            Stages.Sort((a, b) => b.Ms.CompareTo(a.Ms));

            ModMs = 0;
            foreach (Probe probe in s_probes.Values)
            {
                if (probe.Calls == 0)
                {
                    continue;
                }
                Line line = new Line { Name = probe.Name, Ms = ToMs(probe.Ticks) / frames, Calls = (double)probe.Calls / frames };
                if (probe.IsMod)
                {
                    ModMs += line.Ms;
                    Mod.Add(line);
                }
                else
                {
                    Systems.Add(line);
                }
            }
            // Game systems keep their logical order (a nested one stays under its parent); the mod is sorted by cost.
            Systems.Sort((a, b) => Order(a.Name).CompareTo(Order(b.Name)));
            Mod.Sort((a, b) => b.Ms.CompareTo(a.Ms));
            Summary = Lang.F("Кадр: {0:0.0} мс ({1:0} FPS), замір {2} кадрів. Мод Valheim Helper: {3:0.00} мс на кадр.", FrameMs, Fps, s_tickFrames, ModMs);
            Log.Info("Profiler: " + Summary);
            foreach (Line line in Stages)
            {
                Log.Info($"  stage {line.Name}: {line.Ms:0.00} ms");
            }
            foreach (Line line in Systems)
            {
                Log.Info($"  game {line.Name}: {line.Ms:0.00} ms, {line.Calls:0.#} calls");
            }
            foreach (Line line in Mod)
            {
                Log.Info($"  mod {line.Name}: {line.Ms:0.000} ms");
            }
        }

        private static int Order(string name)
        {
            for (int i = 0; i < s_gameMethods.Length; i++)
            {
                if (s_gameMethods[i][2] == name)
                {
                    return i;
                }
            }
            return int.MaxValue;
        }
    }
}
