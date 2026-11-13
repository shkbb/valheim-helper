using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ValheimHelper.Wiki
{
    // Keeps the guide's articles up to date from the wiki itself, without a new build of the mod. While the guide is
    // open, and at most every 15 minutes, the mod asks the wiki's API for the revision of every article (a couple of requests for the whole wiki), takes only
    // the ones that changed or are new, turns their HTML into the guide's own format with the very same converter the
    // pack was built with (WikiConvert), and writes them beside the pack as updates.txt; their new pictures are
    // fetched too. Everything runs on a background thread, one request a second, and the guide reads the new articles
    // the next time it opens a page.
    internal static class WikiUpdate
    {
        private const string UserAgent = "ValheimHelper/1.1 (in-game Valheim wiki reader; github.com/shkbb/valheim-helper)";
        private const int RequestPauseMs = 1000;
        private const int MaxPagesPerRun = 30;
        // A pack that was just opened (a new language on Weird Gloop) fills faster, still one request a second.
        private const int MaxPagesForNewPack = 300;
        private const int MaxPicturesPerRun = 60;
        private const int TimeoutMs = 20000;

        internal static string Status = "";
        internal static int Changed;
        internal static bool Running { get; private set; }
        // Set when new articles are on disk: the guide reloads its pack.
        internal static bool PackChanged;
        // Set when a new language of a wiki got its own (empty) pack: the guide looks at its packs again.
        internal static volatile bool NewPacks;
        private static bool s_discovered;

        // When each wiki was last asked: a new article published while the game is open is picked up on the next
        // look into the guide, not only after a restart.
        private static readonly Dictionary<string, DateTime> s_asked = new Dictionary<string, DateTime>();
        private static readonly TimeSpan AskAgainAfter = TimeSpan.FromMinutes(15);
        // Two wikis are kept fresh: the chosen language and English, which articles fall back to. They are done one
        // after another, so the wiki is never asked for two things at once.
        private static readonly Queue<KeyValuePair<string, string>> s_queue = new Queue<KeyValuePair<string, string>>();
        private static readonly HashSet<string> s_changed = new HashSet<string>();
        private static readonly object s_lock = new object();

        internal static bool Enabled => State.WikiAutoUpdate;

        // How each of the two wikis is doing, in the order they were asked: language → what is happening there.
        private static readonly List<KeyValuePair<string, string>> s_status = new List<KeyValuePair<string, string>>();

        internal static List<KeyValuePair<string, string>> StatusLines()
        {
            lock (s_lock)
            {
                return new List<KeyValuePair<string, string>>(s_status);
            }
        }

        private static void Say(string language, string text)
        {
            Status = text;
            lock (s_lock)
            {
                for (int i = 0; i < s_status.Count; i++)
                {
                    if (s_status[i].Key == language)
                    {
                        s_status[i] = new KeyValuePair<string, string>(language, text);
                        return;
                    }
                }
                s_status.Add(new KeyValuePair<string, string>(language, text));
            }
        }

        // The languages whose articles changed on disk since the last look (the guide reloads those packs).
        internal static List<string> TakeChanged()
        {
            lock (s_lock)
            {
                List<string> changed = new List<string>(s_changed);
                s_changed.Clear();
                return changed;
            }
        }

        // Called when a pack is ready (and by the button on the guide's home page).
        internal static void Start(string language, string dir, bool byHand)
        {
            if (string.IsNullOrEmpty(dir) || (!Enabled && !byHand))
            {
                return;
            }
            bool startThread;
            lock (s_lock)
            {
                if (!byHand && s_asked.TryGetValue(language, out DateTime asked) && DateTime.UtcNow - asked < AskAgainAfter)
                {
                    return;
                }
                s_asked[language] = DateTime.UtcNow;
                foreach (KeyValuePair<string, string> waiting in s_queue)
                {
                    if (waiting.Key == language)
                    {
                        return;
                    }
                }
                s_queue.Enqueue(new KeyValuePair<string, string>(language, dir));
                startThread = !Running;
                Running = true;
            }
            Say(language, Lang.T("Перевіряю, чи є нові статті…"));
            if (!startThread)
            {
                return;
            }
            Thread thread = new Thread(Run) { IsBackground = true, Name = "VH wiki update" };
            thread.Start();
        }

        private static void Run()
        {
            try
            {
                while (true)
                {
                    KeyValuePair<string, string> job;
                    lock (s_lock)
                    {
                        if (s_queue.Count == 0)
                        {
                            return;
                        }
                        job = s_queue.Dequeue();
                    }
                    try
                    {
                        Work(job.Key, job.Value);
                    }
                    catch (Exception e)
                    {
                        Say(job.Key, Lang.F("Оновлення довідника не вдалося: {0}", e.Message));
                        Log.Warn("Wiki update: " + e);
                    }
                }
            }
            finally
            {
                lock (s_lock)
                {
                    Running = false;
                }
            }
        }

        private static void Work(string language, string dir)
        {
            string api = WikiSources.Api(language);
            if (WikiSources.IsWeirdGloop(language) && !s_discovered)
            {
                s_discovered = true;
                Discover(Path.GetDirectoryName(dir));
            }
            Dictionary<string, long> known = ReadRevisions(dir);
            Dictionary<string, long> live = ListRevisions(api);
            if (live.Count == 0)
            {
                Say(language, Lang.T("Вікі не відповіла: довідник лишається таким, як був."));
                return;
            }
            List<string> wanted = new List<string>();
            foreach (KeyValuePair<string, long> page in live)
            {
                if (!known.TryGetValue(page.Key, out long have) || have < page.Value)
                {
                    wanted.Add(page.Key);
                }
            }
            if (wanted.Count == 0)
            {
                Say(language, Lang.T("Довідник свіжий: на вікі нічого нового."));
                return;
            }
            wanted.Sort(StringComparer.Ordinal);
            int take = Math.Min(wanted.Count, known.Count == 0 ? MaxPagesForNewPack : MaxPagesPerRun);
            Say(language, Lang.F("Беру з вікі нові статті: {0}…", take));
            Dictionary<string, string> updates = ReadUpdates(dir);
            Dictionary<string, WikiConvert.Picture> pictures = new Dictionary<string, WikiConvert.Picture>();
            int done = 0;
            for (int i = 0; i < take; i++)
            {
                string title = wanted[i];
                string block = Page(api, title, language, pictures);
                if (block != null)
                {
                    updates[title] = block;
                    done++;
                    Say(language, Lang.F("Беру з вікі нові статті: {0} з {1}…", done, take));
                }
                Thread.Sleep(RequestPauseMs);
            }
            if (done == 0)
            {
                Say(language, Lang.T("Нових статей не вдалося взяти."));
                return;
            }
            WriteUpdates(dir, updates);
            int images = Pictures(dir, pictures);
            Changed += done;
            PackChanged = true;
            lock (s_lock)
            {
                s_changed.Add(language);
            }
            int left = wanted.Count - take;
            Say(language, left > 0
                ? Lang.F("Довідник оновлено: статей {0}, картинок {1}. Ще {2} — наступного разу.", done, images, left)
                : Lang.F("Довідник оновлено: статей {0}, картинок {1}.", done, images));
            Log.Info($"Wiki update ({language}): {done} articles, {images} pictures, {left} left");
        }

        // Weird Gloop's languages come from its interwiki map. A language the guide has no pack for gets an empty one,
        // which the updates then fill. Russian is never taken.
        private static void Discover(string wikiDir)
        {
            JObject json = Json(WikiSources.Api(WikiSources.WeirdGloopPrefix + "en")
                + "?action=query&meta=siteinfo&siprop=interwikimap&sifilteriw=local&format=json&formatversion=2");
            JToken map = json?["query"]?["interwikimap"];
            if (map == null)
            {
                return;
            }
            foreach (JToken entry in map)
            {
                string prefix = (string)entry["prefix"];
                string url = (string)entry["url"] ?? "";
                if (entry["language"] == null || prefix == null || prefix == "en" || !WikiSources.Allowed(prefix)
                    || url.IndexOf(WikiSources.WeirdGloopHost, StringComparison.OrdinalIgnoreCase) < 0
                    || prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    continue;
                }
                string code = WikiSources.WeirdGloopPrefix + prefix.ToLowerInvariant();
                string packDir = Path.Combine(wikiDir, code);
                if (Directory.Exists(packDir))
                {
                    continue;
                }
                try
                {
                    Directory.CreateDirectory(packDir);
                    File.WriteAllText(Path.Combine(packDir, "pages.txt"), "", new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(packDir, "meta.txt"),
                        "format 1\nlang " + prefix + "\nsite Weird Gloop\nsource " + WikiSources.Site(code) + "/w/\nlicense CC BY-NC-SA 3.0\npages 0\n",
                        new UTF8Encoding(false));
                    NewPacks = true;
                    Log.Info("Wiki update: new language on Weird Gloop: " + prefix);
                }
                catch (Exception e)
                {
                    Log.Warn("Wiki update: " + e.Message);
                }
            }
        }

        // ---------------------------------------------------------------- the wiki's API

        // Every article of the wiki with the revision it is at now: two or three requests for the whole wiki.
        private static Dictionary<string, long> ListRevisions(string api)
        {
            Dictionary<string, long> pages = new Dictionary<string, long>();
            string continueFrom = null;
            for (int request = 0; request < 30; request++)
            {
                string url = api + "?action=query&generator=allpages&gaplimit=500&gapfilterredir=nonredirects&gapnamespace=0"
                    + "&prop=revisions&rvprop=ids&format=json&formatversion=2";
                if (continueFrom != null)
                {
                    url += "&gapcontinue=" + Uri.EscapeDataString(continueFrom);
                }
                JObject json = Json(url);
                if (json == null)
                {
                    break;
                }
                JToken list = json["query"]?["pages"];
                if (list != null)
                {
                    foreach (JToken page in list)
                    {
                        string title = (string)page["title"];
                        JToken revision = page["revisions"]?[0]?["revid"];
                        if (title != null && revision != null)
                        {
                            pages[title] = (long)revision;
                        }
                    }
                }
                continueFrom = (string)json["continue"]?["gapcontinue"];
                if (continueFrom == null)
                {
                    break;
                }
                Thread.Sleep(RequestPauseMs);
            }
            return pages;
        }

        // One article, as the block lines of the guide.
        private static string Page(string api, string title, string language, Dictionary<string, WikiConvert.Picture> pictures)
        {
            string url = api + "?action=parse&page=" + Uri.EscapeDataString(title)
                + "&prop=text%7Ccategories%7Crevid&disableeditsection=1&disabletoc=1&format=json&formatversion=2";
            JObject json = Json(url);
            JToken parse = json?["parse"];
            string html = (string)parse?["text"];
            if (html == null)
            {
                return null;
            }
            WikiConvert.Result result = WikiConvert.Convert(html, language);
            List<string> categories = new List<string>();
            JToken list = parse["categories"];
            if (list != null)
            {
                foreach (JToken category in list)
                {
                    if (category["hidden"] == null)
                    {
                        string name = (string)category["category"];
                        if (!string.IsNullOrEmpty(name))
                        {
                            categories.Add(name.Replace('_', ' '));
                        }
                    }
                }
            }
            foreach (KeyValuePair<string, WikiConvert.Picture> picture in result.Pictures)
            {
                if (!pictures.TryGetValue(picture.Key, out WikiConvert.Picture known) || known.Width < picture.Value.Width)
                {
                    pictures[picture.Key] = picture.Value;
                }
            }
            StringBuilder text = new StringBuilder();
            text.Append("=== ").Append(WikiConvert.Field(title)).Append('\n');
            text.Append("rev ").Append(((long?)parse["revid"] ?? 0L).ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append("cat ").Append(WikiConvert.Field(string.Join("|", categories.ToArray()))).Append('\n');
            foreach (string line in result.Lines)
            {
                text.Append(line).Append('\n');
            }
            return text.ToString();
        }

        private static JObject Json(string url)
        {
            string body = Get(url);
            if (body == null)
            {
                return null;
            }
            try
            {
                return JObject.Parse(body);
            }
            catch (Exception e)
            {
                Log.Warn("Wiki update: " + e.Message);
                return null;
            }
        }

        private static string Get(string url)
        {
            byte[] bytes = Download(url);
            return bytes == null ? null : Encoding.UTF8.GetString(bytes);
        }

        private static byte[] Download(string url)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.UserAgent = UserAgent;
                request.Timeout = TimeoutMs;
                request.ReadWriteTimeout = TimeoutMs;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (WebResponse response = request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (MemoryStream memory = new MemoryStream())
                {
                    byte[] buffer = new byte[16384];
                    int read;
                    while (stream != null && (read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        memory.Write(buffer, 0, read);
                    }
                    return memory.ToArray();
                }
            }
            catch (Exception e)
            {
                Log.Warn("Wiki update: " + url + ": " + e.Message);
                return null;
            }
        }

        // ---------------------------------------------------------------- pictures

        private static int Pictures(string dir, Dictionary<string, WikiConvert.Picture> pictures)
        {
            string imageDir = Path.GetFullPath(Path.Combine(dir, "..", "img"));
            string indexPath = Path.Combine(imageDir, "index.tsv");
            Dictionary<string, string[]> index = new Dictionary<string, string[]>();
            try
            {
                Directory.CreateDirectory(imageDir);
                if (File.Exists(indexPath))
                {
                    foreach (string line in File.ReadAllLines(indexPath, Encoding.UTF8))
                    {
                        string[] parts = line.Split('\t');
                        if (parts.Length >= 3)
                        {
                            index[parts[0]] = new[] { parts[1], parts[2] };
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("Wiki update: " + e.Message);
                return 0;
            }
            int added = 0;
            foreach (KeyValuePair<string, WikiConvert.Picture> pair in pictures)
            {
                if (added >= MaxPicturesPerRun)
                {
                    break;
                }
                if (index.TryGetValue(pair.Key, out string[] known)
                    && int.TryParse(known[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width)
                    && width >= pair.Value.Width && File.Exists(Path.Combine(imageDir, known[0])))
                {
                    continue;
                }
                byte[] bytes = Download(pair.Value.Url);
                Thread.Sleep(RequestPauseMs);
                string extension = Extension(bytes);
                if (extension == null && bytes != null && pair.Value.Url.Contains("format=original"))
                {
                    // WebP and GIF originals: the picture server turns them into PNG on request
                    bytes = Download(pair.Value.Url.Replace("format=original", "format=png"));
                    Thread.Sleep(RequestPauseMs);
                    extension = Extension(bytes);
                }
                if (extension == null)
                {
                    continue;
                }
                string file = SafeName(pair.Key) + extension;
                try
                {
                    File.WriteAllBytes(Path.Combine(imageDir, file), bytes);
                }
                catch (Exception e)
                {
                    Log.Warn("Wiki update: " + e.Message);
                    continue;
                }
                index[pair.Key] = new[] { file, pair.Value.Width.ToString(CultureInfo.InvariantCulture) };
                added++;
            }
            if (added > 0)
            {
                try
                {
                    List<string> lines = new List<string>();
                    foreach (KeyValuePair<string, string[]> entry in index)
                    {
                        lines.Add(entry.Key + "\t" + entry.Value[0] + "\t" + entry.Value[1]);
                    }
                    File.WriteAllLines(indexPath, lines.ToArray(), new UTF8Encoding(false));
                }
                catch (Exception e)
                {
                    Log.Warn("Wiki update: " + e.Message);
                }
            }
            return added;
        }

        // PNG and JPEG are what the game can load; anything else is not kept.
        private static string Extension(byte[] bytes)
        {
            if (bytes == null)
            {
                return null;
            }
            if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G')
            {
                return ".png";
            }
            if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8)
            {
                return ".jpg";
            }
            return null;
        }

        private static string SafeName(string key)
        {
            int dot = key.LastIndexOf('.');
            string name = dot > 0 ? key.Substring(0, dot) : key;
            StringBuilder safe = new StringBuilder();
            foreach (char c in name)
            {
                safe.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '~' ? c : '_');
            }
            string text = safe.ToString();
            return text.Length > 80 ? text.Substring(0, 80) : text;
        }

        // ---------------------------------------------------------------- files beside the pack

        internal static string UpdatesPath(string dir)
        {
            return Path.Combine(dir, "updates.txt");
        }

        // A new build of the mod brings a newer pack: articles taken from the wiki before it are then old news and
        // must not stand in front of it any more.
        internal static void Prune(string dir)
        {
            string path = UpdatesPath(dir);
            if (!File.Exists(path))
            {
                return;
            }
            Dictionary<string, long> pack = new Dictionary<string, long>();
            ReadRevisions(Path.Combine(dir, "pages.txt"), pack);
            Dictionary<string, long> mine = new Dictionary<string, long>();
            ReadRevisions(path, mine);
            Dictionary<string, string> updates = ReadUpdates(dir);
            List<string> stale = new List<string>();
            foreach (KeyValuePair<string, long> page in mine)
            {
                if (pack.TryGetValue(page.Key, out long packed) && packed >= page.Value)
                {
                    stale.Add(page.Key);
                }
            }
            if (stale.Count == 0)
            {
                return;
            }
            foreach (string title in stale)
            {
                updates.Remove(title);
            }
            WriteUpdates(dir, updates);
            Log.Info($"Wiki update: {stale.Count} articles of the pack are newer, the fetched copies are dropped");
        }

        // Revisions of what the guide has now: the pack and the updates taken before.
        private static Dictionary<string, long> ReadRevisions(string dir)
        {
            Dictionary<string, long> revisions = new Dictionary<string, long>();
            ReadRevisions(Path.Combine(dir, "pages.txt"), revisions);
            ReadRevisions(UpdatesPath(dir), revisions);
            return revisions;
        }

        private static void ReadRevisions(string path, Dictionary<string, long> into)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }
                string title = null;
                foreach (string line in File.ReadLines(path, Encoding.UTF8))
                {
                    if (line.StartsWith("=== ", StringComparison.Ordinal))
                    {
                        title = line.Substring(4);
                    }
                    else if (title != null && line.StartsWith("rev ", StringComparison.Ordinal)
                        && long.TryParse(line.Substring(4).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long revision))
                    {
                        into[title] = revision;
                        title = null;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("Wiki update: " + e.Message);
            }
        }

        private static Dictionary<string, string> ReadUpdates(string dir)
        {
            Dictionary<string, string> updates = new Dictionary<string, string>();
            string path = UpdatesPath(dir);
            try
            {
                if (!File.Exists(path))
                {
                    return updates;
                }
                string title = null;
                StringBuilder block = new StringBuilder();
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (line.StartsWith("=== ", StringComparison.Ordinal))
                    {
                        if (title != null)
                        {
                            updates[title] = block.ToString();
                        }
                        title = line.Substring(4);
                        block.Length = 0;
                    }
                    if (title != null)
                    {
                        block.Append(line).Append('\n');
                    }
                }
                if (title != null)
                {
                    updates[title] = block.ToString();
                }
            }
            catch (Exception e)
            {
                Log.Warn("Wiki update: " + e.Message);
            }
            return updates;
        }

        private static void WriteUpdates(string dir, Dictionary<string, string> updates)
        {
            try
            {
                StringBuilder text = new StringBuilder();
                foreach (KeyValuePair<string, string> page in updates)
                {
                    text.Append(page.Value);
                }
                File.WriteAllText(UpdatesPath(dir), text.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Log.Warn("Wiki update: " + e.Message);
            }
        }
    }
}
