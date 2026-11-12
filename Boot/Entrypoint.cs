using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Doorstop
{
    // Doorstop calls this before the game's own code runs, while none of the mod's files is loaded and locked yet.
    // 1. An update the mod downloaded during the last session (ValheimHelper\update\ready + update\files) is put in place.
    // 2. Unless "Перевіряти оновлення" is off, GitHub is asked for the latest release; a newer one is downloaded and put
    //    in place right now, so the game starts with the new version (one start, no second restart).
    // 3. Doorstop is pointed at the newest boot DLL, then the mod's DLL is loaded and started.
    // Whatever fails here, the mod still starts with what it has.
    public static class Entrypoint
    {
#if !NEXUS
        private const string Repository = "shkbb/valheim-helper";
        private const string LatestApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
        private const long MaxArchiveBytes = 50L * 1024 * 1024;
#endif

        public static void Start()
        {
            string dir = Path.GetDirectoryName(typeof(Entrypoint).Assembly.Location) ?? ".";
            string self = Path.GetFileName(typeof(Entrypoint).Assembly.Location);
            string log = Path.Combine(dir, "update.log");
            try
            {
                ApplyStaged(dir, self, log);
            }
            catch (Exception e)
            {
                Write(log, "update not applied: " + e);
            }
#if !NEXUS
            try
            {
                UpdateNow(dir, self, log);
            }
            catch (Exception e)
            {
                Write(log, "update check failed: " + e.Message);
            }
#endif
            try
            {
                ValheimHelper.BootFiles.PointDoorstopAtNewest(dir, self, line => Write(log, line));
            }
            catch (Exception e)
            {
                Write(log, "boot DLL not switched: " + e.Message);
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

        private static void ApplyStaged(string dir, string self, string log)
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
                // the boot DLL that runs now is locked; a new one always comes under a new name
                if (relative.Equals(self, StringComparison.OrdinalIgnoreCase))
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
            Environment.SetEnvironmentVariable("VALHEIMHELPER_UPDATED", version);
        }

#if !NEXUS
        private static void UpdateNow(string dir, string self, string log)
        {
            if (AutoCheckOff(dir))
            {
                return;
            }
            string editionFile = Path.Combine(dir, "edition.txt");
            string modDll = Path.Combine(dir, "ValheimHelper.dll");
            if (!File.Exists(editionFile) || !File.Exists(modDll))
            {
                return;
            }
            string edition = File.ReadAllText(editionFile).Trim().ToLowerInvariant();
            Version installed = AssemblyName.GetAssemblyName(modDll).Version;
            string userAgent = "ValheimHelper/" + installed.ToString(3) + " (" + edition + "; github.com/" + Repository + ")";

            string release = Encoding.UTF8.GetString(Fetch(LatestApi, userAgent, "application/vnd.github+json", 8, 2L * 1024 * 1024));
            Match tag = Regex.Match(release, "\"tag_name\"\\s*:\\s*\"[vV]?([0-9.]+)\"");
            if (!tag.Success || !Version.TryParse(tag.Groups[1].Value, out Version latest))
            {
                throw new InvalidDataException("no version in the release");
            }
            // the mod does not ask again in the game: this check has been done
            Environment.SetEnvironmentVariable("VALHEIMHELPER_BOOT_CHECKED", "1");
            if (Normalize(latest) <= Normalize(installed))
            {
                return;
            }
            Match asset = Regex.Match(release, "\"browser_download_url\"\\s*:\\s*\"(https://github\\.com/[^\"]+/ValheimHelper-updates\\.zip)\"");
            if (!asset.Success)
            {
                Write(log, "release " + latest + " has no update archive");
                return;
            }
            Write(log, "downloading " + latest + " (" + edition + ")");
            byte[] archive = Fetch(asset.Groups[1].Value, userAgent, "application/octet-stream", 120, MaxArchiveBytes);
            Unpack(dir, archive, edition, latest.ToString(3));
            ApplyStaged(dir, self, log);
        }

        private static Version Normalize(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }

        private static bool AutoCheckOff(string dir)
        {
            string settings = Path.Combine(dir, "settings.txt");
            if (!File.Exists(settings))
            {
                return false;
            }
            foreach (string line in File.ReadAllLines(settings))
            {
                if (line.Trim().Replace(" ", "") == "autoupdate=0")
                {
                    return true;
                }
            }
            return false;
        }

        // The same layout the mod itself stages: update\files + update\ready (written last).
        private static void Unpack(string dir, byte[] archive, string edition, string version)
        {
            string update = Path.Combine(dir, "update");
            if (Directory.Exists(update))
            {
                Directory.Delete(update, true);
            }
            string files = Path.Combine(update, "files");
            Directory.CreateDirectory(files);
            string root = Path.GetFullPath(files) + Path.DirectorySeparatorChar;
            string prefix = edition + "/";
            int taken = 0;
            using (ZipArchive zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    if (string.IsNullOrEmpty(entry.Name) || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    string target = Path.GetFullPath(Path.Combine(files, name.Substring(prefix.Length)));
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
                throw new InvalidDataException("the update has nothing for the edition " + edition);
            }
            File.WriteAllText(Path.Combine(update, "ready"), version);
        }

        // Mono's own HTTPS first; if it is not ready this early, Windows' curl.exe does the download.
        private static byte[] Fetch(string url, string userAgent, string accept, int timeoutSeconds, long maxBytes)
        {
            Exception first;
            try
            {
                return FetchMono(url, userAgent, accept, timeoutSeconds, maxBytes);
            }
            catch (Exception e)
            {
                first = e;
            }
            string curl = Path.Combine(Environment.SystemDirectory ?? "", "curl.exe");
            if (!File.Exists(curl))
            {
                throw first;
            }
            string temp = Path.GetTempFileName();
            try
            {
                ProcessStartInfo start = new ProcessStartInfo(curl,
                    "-sSfL --max-time " + timeoutSeconds + " -A \"" + userAgent + "\" -H \"Accept: " + accept + "\" -o \"" + temp + "\" \"" + url + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (Process process = Process.Start(start))
                {
                    if (!process.WaitForExit((timeoutSeconds + 5) * 1000))
                    {
                        process.Kill();
                        throw new TimeoutException("curl: " + url);
                    }
                    if (process.ExitCode != 0)
                    {
                        throw new WebException("curl " + process.ExitCode + " (" + first.Message + ")");
                    }
                }
                if (new FileInfo(temp).Length > maxBytes)
                {
                    throw new InvalidDataException("the download is larger than expected");
                }
                return File.ReadAllBytes(temp);
            }
            finally
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception)
                {
                    // a temp file
                }
            }
        }

        private static byte[] FetchMono(string url, string userAgent, string accept, int timeoutSeconds, long maxBytes)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = userAgent;
            request.Accept = accept;
            request.Timeout = timeoutSeconds * 1000;
            request.ReadWriteTimeout = timeoutSeconds * 1000;
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
#endif

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
