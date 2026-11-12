using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ValheimHelper
{
    // Keeps the mod up to date from its GitHub releases. At start (when "Перевіряти оновлення" is on) or by hand, a
    // background thread asks GitHub for the latest release; if its version is newer than this build, the update archive
    // (ValheimHelper-updates.zip: a folder per edition, full/ helper/ legal/, with the DLLs and languages, not the guide,
    // which updates itself) is downloaded and this edition's folder
    // is unpacked into ValheimHelper\update\files, and update\ready is written. The files are put in place
    // by ValheimHelper.Boot.dll at the next start of the game, before the mod's own DLL is loaded.
    internal static class Updater
    {
        internal const string Repository = "shkbb/valheim-helper";
        internal const string ReleasesPage = "https://github.com/" + Repository + "/releases";
        private const string LatestApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
        private const int TimeoutMs = 30000;
        private const long MaxArchiveBytes = 50L * 1024 * 1024;

        internal static bool AutoCheck = true;
        internal static string Status = "";
        internal static bool Running { get; private set; }
        // Set by the thread when there is something to tell the player in the game.
        private static string s_announce;

        private static string UpdateDir => Path.Combine(Loader.Dir ?? ".", "update");

        internal static void Start(bool byHand)
        {
            if (Running || (!byHand && !AutoCheck))
            {
                return;
            }
            Running = true;
            Status = Lang.T("Перевіряю оновлення…");
            Thread thread = new Thread(() => Run(byHand)) { IsBackground = true, Name = "ValheimHelper update" };
            thread.Start();
        }

        // Called every frame from the main thread: shows what the background thread found.
        internal static void Tick()
        {
            string message = s_announce;
            if (message != null && Player.m_localPlayer != null)
            {
                s_announce = null;
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, message);
            }
        }

        private static void Run(bool byHand)
        {
            try
            {
                JObject release = JObject.Parse(Encoding.UTF8.GetString(Download(LatestApi, 2L * 1024 * 1024)));
                string tag = ((string)release["tag_name"] ?? "").TrimStart('v', 'V');
                if (!System.Version.TryParse(tag, out System.Version latest))
                {
                    Status = Lang.F("Не вдалося прочитати версію випуску «{0}».", tag);
                    return;
                }
                if (latest <= BuildInfo.Version)
                {
                    Status = Lang.F("Встановлено найновішу версію ({0}).", BuildInfo.VersionText);
                    return;
                }
                string ready = Path.Combine(UpdateDir, "ready");
                if (File.Exists(ready) && File.ReadAllText(ready).Trim() == latest.ToString(3))
                {
                    Status = Lang.F("Версію {0} уже завантажено: вона встановиться, коли перезапустиш гру.", latest.ToString(3));
                    return;
                }
                const string wanted = "ValheimHelper-updates.zip";
                string url = null;
                long size = 0;
                foreach (JToken asset in release["assets"] ?? new JArray())
                {
                    if (string.Equals((string)asset["name"], wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        url = (string)asset["browser_download_url"];
                        size = (long?)asset["size"] ?? 0;
                    }
                }
                if (url == null || !url.StartsWith("https://github.com/", StringComparison.Ordinal))
                {
                    Status = Lang.F("Вийшла версія {0}, але в ній немає оновлення для цієї редакції. Завантаж її вручну.", latest.ToString(3));
                    return;
                }
                Status = Lang.F("Завантажую версію {0}…", latest.ToString(3));
                byte[] archive = Download(url, MaxArchiveBytes);
                if (size > 0 && archive.Length != size)
                {
                    Status = Lang.T("Оновлення завантажилось не повністю. Спробую наступного разу.");
                    return;
                }
                Unpack(archive, latest.ToString(3));
                Status = Lang.F("Версію {0} завантажено: вона встановиться, коли перезапустиш гру.", latest.ToString(3));
                s_announce = Lang.F("Valheim Helper {0} завантажено. Він встановиться під час наступного запуску гри.", latest.ToString(3));
                Log.Info("Update " + latest + " downloaded");
            }
            catch (Exception e)
            {
                Status = byHand ? Lang.T("Не вдалося перевірити оновлення: немає зв'язку з GitHub.") : "";
                Log.Warn("Update check: " + e.Message);
            }
            finally
            {
                Running = false;
            }
        }

        private static void Unpack(byte[] archive, string version)
        {
            string dir = UpdateDir;
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
            string files = Path.Combine(dir, "files");
            Directory.CreateDirectory(files);
            string root = Path.GetFullPath(files) + Path.DirectorySeparatorChar;
            string prefix = BuildInfo.Edition + "/";
            int taken = 0;
            using (ZipArchive zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    // only this edition's folder, without the folder itself in the path
                    string name = entry.FullName.Replace('\\', '/');
                    if (string.IsNullOrEmpty(entry.Name) || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    string target = Path.GetFullPath(Path.Combine(files, name.Substring(prefix.Length)));
                    // nothing may land outside the update folder
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("bad path in the update: " + entry.FullName);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (Stream source = entry.Open())
                    using (FileStream output = File.Create(target))
                    {
                        source.CopyTo(output);
                    }
                    taken++;
                }
            }
            if (taken == 0)
            {
                throw new InvalidDataException("the update has nothing for the edition " + BuildInfo.Edition);
            }
            // written last: the boot DLL only acts on a complete update
            File.WriteAllText(Path.Combine(dir, "ready"), version);
        }

        private static byte[] Download(string url, long maxBytes)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "ValheimHelper/" + BuildInfo.VersionText + " (" + BuildInfo.Edition + "; github.com/" + Repository + ")";
            request.Accept = url.StartsWith("https://api.github.com/", StringComparison.Ordinal) ? "application/vnd.github+json" : "application/octet-stream";
            request.Timeout = TimeoutMs;
            request.ReadWriteTimeout = TimeoutMs;
            request.AllowAutoRedirect = true;
            using (WebResponse response = request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (MemoryStream memory = new MemoryStream())
            {
                byte[] buffer = new byte[65536];
                int read;
                while (stream != null && (read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    memory.Write(buffer, 0, read);
                    if (memory.Length > maxBytes)
                    {
                        throw new InvalidDataException("the download is larger than expected");
                    }
                }
                return memory.ToArray();
            }
        }

        // Opens the releases page in the browser: there the player picks another edition or the full package.
        internal static void OpenReleases()
        {
            Application.OpenURL(ReleasesPage);
        }
    }
}
