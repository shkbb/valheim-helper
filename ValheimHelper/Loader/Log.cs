using System;
using System.IO;

namespace ValheimHelper
{
    // File logger that only depends on mscorlib, so it is usable from the Doorstop entrypoint.
    internal static class Log
    {
        private static readonly object s_lock = new object();
        private static string s_path;

        internal static void Init(string path)
        {
            s_path = path;
            try
            {
                // BOM so Notepad/PowerShell 5.1 show the Ukrainian messages correctly.
                File.WriteAllText(s_path, "", new System.Text.UTF8Encoding(true));
            }
            catch
            {
                s_path = null;
            }
        }

        internal static void Info(string message) => Write("INFO ", message);

        internal static void Warn(string message) => Write("WARN ", message);

        internal static void Error(Exception e) => Write("ERROR", e.ToString());

        internal static void Error(string message) => Write("ERROR", message);

        private static void Write(string level, string message)
        {
            if (s_path == null)
            {
                return;
            }
            lock (s_lock)
            {
                try
                {
                    File.AppendAllText(s_path, $"{DateTime.Now:HH:mm:ss} [{level}] {message}{Environment.NewLine}");
                }
                catch
                {
                }
            }
        }
    }
}
