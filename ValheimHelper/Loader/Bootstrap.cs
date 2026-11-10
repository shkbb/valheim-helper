using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ValheimHelper
{
    // Runs on the Unity main thread once the first scene is loaded, when it is safe to patch
    // game methods and create GameObjects. Game.isModded is intentionally never touched.
    internal static class Bootstrap
    {
        private static bool s_done;
        internal static GameObject Root;

        internal static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (s_done)
            {
                return;
            }
            s_done = true;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            try
            {
                // the menu language first: texts of the whole mod are read in it from now on
                Lang.Chosen = Settings.Peek("uilang") ?? "";
                string gameLanguage = "English";
                try
                {
                    gameLanguage = Localization.instance.GetSelectedLanguage();
                }
                catch (Exception)
                {
                    // the game's localization is not up yet: English
                }
                Lang.Start(gameLanguage);
                Log.Info($"Menu language: {Lang.Code} (game: {gameLanguage})");
                ApplyPatches();
                Map.MapSettings.Load();
                Binds.Load();
                Settings.Load();
                Storage.StorageSettings.Load();
                Perf.PerfSettings.Load();
                GameObject go = new GameObject("ValheimHelper");
                Root = go;
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<CheatMenu>();
                go.AddComponent<WorldTicker>();
                go.AddComponent<BindRunner>();
                go.AddComponent<Map.MapOverlay>();
                go.AddComponent<Storage.StorageOverlay>();
                go.AddComponent<Storage.CraftAmountPanel>();
                go.AddComponent<Map.WaypointOverlay>();
                go.AddComponent<PhotoPanel>();
                go.AddComponent<Perf.RenderLayer>();
                Log.Info($"Initialized in scene '{scene.name}', Game.isModded={Game.isModded}");
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        // Each patch class is applied separately so one outdated signature after a game update
        // disables only that feature instead of the whole mod.
        private static void ApplyPatches()
        {
            Harmony harmony = new Harmony("valheimhelper");
            int ok = 0;
            int failed = 0;
            foreach (Type type in AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly()))
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), inherit: false).Length == 0)
                {
                    continue;
                }
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    ok++;
                }
                catch (Exception e)
                {
                    failed++;
                    Log.Error($"Patch {type.Name} failed: {e}");
                }
            }
            Log.Info($"Patches applied: {ok}, failed: {failed}");
        }
    }
}
