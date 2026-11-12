using System;
using System.IO;
using System.Text;

namespace ValheimHelper
{
    // The boot DLL cannot replace itself while it is loaded, so every new boot DLL comes under a new name
    // (ValheimHelper.Boot.<n>.dll; the first one was ValheimHelper.Boot.dll). Whoever runs first, the boot DLL or the mod,
    // points Doorstop (doorstop_config.ini next to the game) at the newest one, and boot DLLs older than the running one
    // are deleted. This file is compiled into both.
    internal static class BootFiles
    {
        internal static int Generation(string fileName)
        {
            if (fileName.Equals("ValheimHelper.Boot.dll", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
            const string prefix = "ValheimHelper.Boot.";
            if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }
            string number = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - 4);
            return int.TryParse(number, out int generation) ? generation : 0;
        }

        // modDir: the ValheimHelper folder; running: the file name of the boot DLL loaded now (null when the mod calls).
        internal static void PointDoorstopAtNewest(string modDir, string running, Action<string> log)
        {
            string newest = null;
            int newestGeneration = 0;
            foreach (string path in Directory.GetFiles(modDir, "ValheimHelper.Boot*.dll"))
            {
                string name = Path.GetFileName(path);
                int generation = Generation(name);
                if (generation > newestGeneration)
                {
                    newest = name;
                    newestGeneration = generation;
                }
            }
            if (newest == null)
            {
                return;
            }
            string ini = Path.Combine(Path.GetDirectoryName(modDir) ?? ".", "doorstop_config.ini");
            if (File.Exists(ini))
            {
                string wanted = "target_assembly=" + Path.GetFileName(modDir) + "\\" + newest;
                string[] lines = File.ReadAllLines(ini);
                bool changed = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].TrimStart();
                    if (line.StartsWith("target_assembly", StringComparison.OrdinalIgnoreCase) && lines[i].Trim() != wanted)
                    {
                        lines[i] = wanted;
                        changed = true;
                    }
                }
                if (changed)
                {
                    File.WriteAllLines(ini, lines, new UTF8Encoding(false));
                    log("Doorstop now starts " + newest);
                }
            }
            // older boot DLLs are not needed any more (the running one stays, it is locked)
            if (running == null)
            {
                return;
            }
            int runningGeneration = Generation(running);
            foreach (string path in Directory.GetFiles(modDir, "ValheimHelper.Boot*.dll"))
            {
                string name = Path.GetFileName(path);
                int generation = Generation(name);
                if (generation > 0 && generation < runningGeneration)
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception)
                    {
                        // still in use: next time
                    }
                }
            }
        }
    }
}
