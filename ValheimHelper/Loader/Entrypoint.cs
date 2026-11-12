using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

// Unity Doorstop 4 calls `static void Doorstop.Entrypoint.Start()` in the target assembly
// while the Mono domain is being created — before Unity and the game scripts are ready.
// Nothing here may touch UnityEngine or assembly_valheim types directly.
namespace Doorstop
{
    public static class Entrypoint
    {
        public static void Start()
        {
            ValheimHelper.Loader.Init();
        }
    }
}

namespace ValheimHelper
{
    internal static class Loader
    {
        internal static string Dir;
        private static bool s_hooked;

        internal static void Init()
        {
            try
            {
                Dir = Path.GetDirectoryName(typeof(Loader).Assembly.Location);
                Log.Init(Path.Combine(Dir, "ValheimHelper.log"));
                Log.Info("Doorstop entrypoint reached");
                try
                {
                    // an update put in place by an older boot DLL may have brought a newer one: Doorstop starts that next time
                    BootFiles.PointDoorstopAtNewest(Dir, null, Log.Info);
                }
                catch (Exception e)
                {
                    Log.Warn("Boot DLL not switched: " + e.Message);
                }

                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    TryHook(assembly);
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        // Harmony and its MonoMod/Cecil dependencies live next to our DLL, not in Managed.
        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            string path = Path.Combine(Dir, new AssemblyName(args.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            TryHook(args.LoadedAssembly);
        }

        private static void TryHook(Assembly assembly)
        {
            if (s_hooked || assembly.GetName().Name != "assembly_valheim")
            {
                return;
            }
            s_hooked = true;
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            try
            {
                SubscribeToFirstScene();
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        // Kept in a separate non-inlined method so UnityEngine.CoreModule is only resolved
        // once the game scripts are loaded. Subscribing to the event is purely managed.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SubscribeToFirstScene()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += Bootstrap.OnSceneLoaded;
            Log.Info("Waiting for the first scene");
        }
    }
}
