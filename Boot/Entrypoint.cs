using System;
using System.IO;
using System.Reflection;

namespace Doorstop
{
    // Doorstop calls this before the game's own code runs. If the mod downloaded an update during the last session
    // (ValheimHelper\update\ready next to the files in update\files), the files are copied over the mod's folder now,
    // while none of them is loaded and locked yet. Then the mod's DLL is loaded and started as Doorstop would start it.
    public static class Entrypoint
    {
        public static void Start()
        {
            string dir = Path.GetDirectoryName(typeof(Entrypoint).Assembly.Location) ?? ".";
            string log = Path.Combine(dir, "update.log");
            try
            {
                ApplyUpdate(dir, log);
            }
            catch (Exception e)
            {
                Write(log, "update not applied: " + e);
            }
            try
            {
                Assembly mod = Assembly.LoadFrom(Path.Combine(dir, "ValheimHelper.dll"));
                mod.GetType("Doorstop.Entrypoint", true).GetMethod("Start", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            }
            catch (Exception e)
            {
                Write(log, "the mod did not start: " + e);
            }
        }

        private static void ApplyUpdate(string dir, string log)
        {
            string update = Path.Combine(dir, "update");
            string ready = Path.Combine(update, "ready");
            string files = Path.Combine(update, "files");
            if (!File.Exists(ready) || !Directory.Exists(files))
            {
                return;
            }
            string version = File.ReadAllText(ready).Trim();
            int copied = 0;
            foreach (string source in Directory.GetFiles(files, "*", SearchOption.AllDirectories))
            {
                string relative = source.Substring(files.Length).TrimStart('\\', '/');
                // the boot DLL itself is loaded right now and cannot be replaced; the next full install brings it
                if (relative.Equals("ValheimHelper.Boot.dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string target = Path.Combine(dir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(source, target, true);
                copied++;
            }
            Directory.Delete(update, true);
            Write(log, "update " + version + " applied: " + copied + " files");
        }

        private static void Write(string log, string line)
        {
            try
            {
                File.AppendAllText(log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + Environment.NewLine);
            }
            catch (Exception)
            {
                // nowhere to report
            }
        }
    }
}
