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
    // Keeps the mod up to date from its GitHub releases. Normally the boot DLL does it before the game starts
    // (Boot\Entrypoint.cs), so the new version runs at once. This class covers the rest: the button, and the start
    // check when the boot DLL could not reach GitHub or is an old one. Then a background thread asks GitHub for the latest release; if its version is newer than this build, the update archive
    // (ValheimHelper-updates.zip: a folder per edition, full/ helper/ legal/, with the DLLs and languages, not the guide,
    // which updates itself) is downloaded and this edition's folder
    // is unpacked into ValheimHelper\update\files, and update\ready is written. The files are put in place
    // by ValheimHelper.Boot.dll at the next start of the game, before the mod's own DLL is loaded.
    internal static class Updater
    {
        internal const string Repository = "shkbb/valheim-helper";
#if NEXUS
        internal const string ReleasesPage = "https://www.nexusmods.com/valheim/search/?gsearch=Valheim%20Helper";
#else
        internal const string ReleasesPage = "https://github.com/" + Repository + "/releases";
#endif
#if !NEXUS
        private const string LatestApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
#endif
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
#if NEXUS
            // the Nexus Mods build does not update itself: Nexus and the mod managers tell about new versions
            return;
#else
            if (Running || (!byHand && !AutoCheck))
            {
                return;
            }
            if (!byHand)
            {
                FetchMissingPacks();
                // the boot DLL already updated the mod (or found it up to date) before the game started
                string updated = Environment.GetEnvironmentVariable("VALHEIMHELPER_UPDATED");
                if (!string.IsNullOrEmpty(updated))
                {
                    Status = Lang.F("Версію {0} встановлено під час запуску гри.", BuildInfo.VersionText);
                    s_announce = Lang.F("Valheim Helper оновлено до версії {0}.", BuildInfo.VersionText);
                    Log.Info("Updated to " + BuildInfo.VersionText + " at start");
                }
                if (Environment.GetEnvironmentVariable("VALHEIMHELPER_BOOT_CHECKED") == "1")
                {
                    if (string.IsNullOrEmpty(updated))
                    {
                        Status = Lang.F("Встановлено найновішу версію ({0}).", BuildInfo.VersionText);
                    }
                    return;
                }
            }
            Running = true;
            Status = Lang.T("Перевіряю оновлення…");
            Thread thread = new Thread(() => Run(byHand)) { IsBackground = true, Name = "ValheimHelper update" };
            thread.Start();
#endif
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

#if !NEXUS
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

        // ---------------------------------------------------------------- wiki packs

        // The updates archive carries no wiki (it would be huge), so a wiki pack added in a later version (Weird Gloop)
        // would never reach an install that only updates itself. Each release has such packs as wiki-<code>.zip; one whose
        // folder is missing (or still empty) is fetched once, on a background thread, and the guide picks it up.
        private static bool s_packsAsked;

        private static void FetchMissingPacks()
        {
            if (s_packsAsked)
            {
                return;
            }
            s_packsAsked = true;
            Thread thread = new Thread(() =>
            {
                try
                {
                    JObject release = JObject.Parse(Encoding.UTF8.GetString(Download(LatestApi, 2L * 1024 * 1024)));
                    foreach (JToken asset in release["assets"] ?? new JArray())
                    {
                        string name = (string)asset["name"] ?? "";
                        string url = (string)asset["browser_download_url"] ?? "";
                        if (!name.StartsWith("wiki-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                            || !url.StartsWith("https://github.com/", StringComparison.Ordinal))
                        {
                            continue;
                        }
                        string code = name.Substring(5, name.Length - 9).ToLowerInvariant();
                        if (code.Length == 0 || code.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || code == "img"
                            || !Wiki.WikiSources.Allowed(Wiki.WikiSources.Language(code)))
                        {
                            continue;
                        }
                        string pages = Path.Combine(Loader.Dir ?? ".", "wiki", code, "pages.txt");
                        if (File.Exists(pages) && new FileInfo(pages).Length > 0)
                        {
                            continue;
                        }
                        Log.Info("Wiki pack " + code + ": fetching " + name);
                        UnpackPack(Download(url, 200L * 1024 * 1024), code);
                        Log.Info("Wiki pack " + code + ": in place");
                        Wiki.WikiUpdate.NewPacks = true;
                        Wiki.WikiUpdate.PackChanged = true;
                    }
                }
                catch (Exception e)
                {
                    Log.Warn("Wiki packs: " + e.Message);
                }
            }) { IsBackground = true, Name = "ValheimHelper wiki packs" };
            thread.Start();
        }

        // <code>/… goes to wiki/<code>/, img/… to wiki/img/ and the lines of img/index.part.tsv join wiki/img/index.tsv.
        private static void UnpackPack(byte[] archive, string code)
        {
            string wiki = Path.GetFullPath(Path.Combine(Loader.Dir ?? ".", "wiki"));
            string root = wiki + Path.DirectorySeparatorChar;
            string indexPart = null;
            using (ZipArchive zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        continue;
                    }
                    if (name.Equals("img/index.part.tsv", StringComparison.OrdinalIgnoreCase))
                    {
                        using (StreamReader reader = new StreamReader(entry.Open(), Encoding.UTF8))
                        {
                            indexPart = reader.ReadToEnd();
                        }
                        continue;
                    }
                    if (!name.StartsWith(code + "/", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("img/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    string target = Path.GetFullPath(Path.Combine(wiki, name));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("bad path in the wiki pack: " + entry.FullName);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (Stream source = entry.Open())
                    using (FileStream output = File.Create(target))
                    {
                        source.CopyTo(output);
                    }
                }
            }
            if (!string.IsNullOrEmpty(indexPart))
            {
                string indexPath = Path.Combine(wiki, "img", "index.tsv");
                System.Collections.Generic.HashSet<string> known = new System.Collections.Generic.HashSet<string>();
                if (File.Exists(indexPath))
                {
                    foreach (string line in File.ReadAllLines(indexPath, Encoding.UTF8))
                    {
                        int tab = line.IndexOf('\t');
                        if (tab > 0)
                        {
                            known.Add(line.Substring(0, tab));
                        }
                    }
                }
                StringBuilder added = new StringBuilder();
                foreach (string line in indexPart.Split('\n'))
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0 && !known.Contains(line.Substring(0, tab)))
                    {
                        added.Append(line.TrimEnd('\r')).Append('\n');
                    }
                }
                File.AppendAllText(indexPath, added.ToString(), new UTF8Encoding(false));
            }
        }

        // Opens the releases page in the browser: there the player picks another edition or the full package.
#endif
        internal static void OpenReleases()
        {
            Application.OpenURL(ReleasesPage);
        }
    }
}
