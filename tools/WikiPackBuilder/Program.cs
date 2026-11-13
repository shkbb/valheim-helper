using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using ValheimHelper.Wiki;

// dotnet run -- <code|all> <modpack folder>
//   code: a pack code such as wg-en; "all" = every language Weird Gloop has (from its interwiki map), Russian never.
// Writes <modpack>/<code>/pages.txt, redirects.tsv, meta.txt and the pictures into <modpack>/img (index.tsv).
// Parsed pages are cached in <modpack>/../cache-<code>/ so a stopped run goes on where it was.
internal static class Program
{
    private const string UserAgent = "ValheimHelper/1.1 (pack builder for an in-game Valheim wiki reader; github.com/shkbb/valheim-helper)";
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: <code|all> <modpack folder>");
            return 2;
        }
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        if (args[0] == "test")
        {
            // test <code> <title>: one article as the guide would get it
            string code = args.Length > 2 ? args[1] : "wg-en";
            string title = args[args.Length - 1];
            string body = Get(WikiSources.Api(code) + "?action=parse&page=" + Uri.EscapeDataString(title)
                + "&prop=text%7Ccategories%7Crevid&disableeditsection=1&disabletoc=1&format=json&formatversion=2");
            Dictionary<string, WikiConvert.Picture> found = new Dictionary<string, WikiConvert.Picture>();
            Console.Write(Block(body, title, code, found));
            foreach (WikiConvert.Picture picture in found.Values)
            {
                Console.WriteLine("PIC " + picture.Key + " " + picture.Width + " " + picture.Url);
            }
            return 0;
        }
        string root = Path.GetFullPath(args[1]);
        List<string> codes = args[0] == "all" ? WeirdGloopLanguages() : new List<string> { args[0] };
        foreach (string code in codes)
        {
            if (!WikiSources.Allowed(WikiSources.Language(code)))
            {
                Console.WriteLine("skipped " + code);
                continue;
            }
            Build(code, root);
        }
        return 0;
    }

    // en plus every language in the wiki's interwiki map, without Russian
    private static List<string> WeirdGloopLanguages()
    {
        List<string> codes = new List<string> { "wg-en" };
        JsonElement json = Json(WikiSources.Api("wg-en") + "?action=query&meta=siteinfo&siprop=interwikimap&sifilteriw=local&format=json");
        foreach (JsonElement entry in json.GetProperty("query").GetProperty("interwikimap").EnumerateArray())
        {
            if (!entry.TryGetProperty("language", out _))
            {
                continue;
            }
            string prefix = entry.GetProperty("prefix").GetString();
            string url = entry.GetProperty("url").GetString() ?? "";
            if (prefix != "en" && WikiSources.Allowed(prefix) && url.Contains(WikiSources.WeirdGloopHost))
            {
                codes.Add(WikiSources.WeirdGloopPrefix + prefix);
            }
        }
        Console.WriteLine("languages: " + string.Join(", ", codes));
        return codes;
    }

    private static void Build(string code, string root)
    {
        string api = WikiSources.Api(code);
        string dir = Path.Combine(root, code);
        string cache = Path.Combine(root, "..", "cache-" + code);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(cache);

        Dictionary<string, long> pages = ListPages(api);
        Console.WriteLine(code + ": " + pages.Count + " articles");
        Dictionary<string, WikiConvert.Picture> pictures = new Dictionary<string, WikiConvert.Picture>();
        SortedDictionary<string, string> blocks = new SortedDictionary<string, string>(StringComparer.Ordinal);
        int done = 0;
        foreach (KeyValuePair<string, long> page in pages)
        {
            string cached = Path.Combine(cache, Safe(page.Key) + ".json");
            string body = null;
            if (File.Exists(cached))
            {
                body = File.ReadAllText(cached, Encoding.UTF8);
                using JsonDocument old = JsonDocument.Parse(body);
                long rev = old.RootElement.TryGetProperty("parse", out JsonElement p) && p.TryGetProperty("revid", out JsonElement r) ? r.GetInt64() : 0;
                if (rev < page.Value)
                {
                    body = null;
                }
            }
            if (body == null)
            {
                body = Get(api + "?action=parse&page=" + Uri.EscapeDataString(page.Key)
                    + "&prop=text%7Ccategories%7Crevid&disableeditsection=1&disabletoc=1&format=json&formatversion=2");
                Thread.Sleep(700);
                if (body == null)
                {
                    continue;
                }
                File.WriteAllText(cached, body, Encoding.UTF8);
            }
            string block = Block(body, page.Key, code, pictures);
            if (block != null)
            {
                blocks[page.Key] = block;
            }
            done++;
            if (done % 50 == 0)
            {
                Console.WriteLine(code + ": " + done + "/" + pages.Count);
            }
        }
        File.WriteAllText(Path.Combine(dir, "pages.txt"), string.Concat(blocks.Values), new UTF8Encoding(false));
        WriteRedirects(api, dir);
        File.WriteAllText(Path.Combine(dir, "meta.txt"),
            "format 1\nlang " + WikiSources.Language(code) + "\nsite Weird Gloop\nsource " + WikiSources.Site(code) + "/w/\nlicense CC BY-NC-SA 3.0\n"
            + "date " + DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\npages " + blocks.Count + "\n",
            new UTF8Encoding(false));
        int images = Pictures(Path.Combine(root, "img"), pictures);
        Console.WriteLine(code + ": " + blocks.Count + " articles, " + images + " new pictures of " + pictures.Count);
    }

    private static Dictionary<string, long> ListPages(string api)
    {
        Dictionary<string, long> pages = new Dictionary<string, long>();
        string from = null;
        while (true)
        {
            string url = api + "?action=query&generator=allpages&gaplimit=500&gapfilterredir=nonredirects&gapnamespace=0"
                + "&prop=revisions&rvprop=ids&format=json&formatversion=2" + (from != null ? "&gapcontinue=" + Uri.EscapeDataString(from) : "");
            JsonElement json = Json(url);
            if (json.TryGetProperty("query", out JsonElement query))
            {
                foreach (JsonElement page in query.GetProperty("pages").EnumerateArray())
                {
                    if (page.TryGetProperty("revisions", out JsonElement revs))
                    {
                        pages[page.GetProperty("title").GetString()] = revs[0].GetProperty("revid").GetInt64();
                    }
                }
            }
            if (!json.TryGetProperty("continue", out JsonElement next))
            {
                break;
            }
            from = next.GetProperty("gapcontinue").GetString();
            Thread.Sleep(700);
        }
        return pages;
    }

    // Every redirect of the main namespace with where it leads (and the section, if any): the titles first, then
    // their targets fifty at a time.
    private static void WriteRedirects(string api, string dir)
    {
        List<string> titles = new List<string>();
        string from = null;
        while (true)
        {
            string url = api + "?action=query&list=allpages&apfilterredir=redirects&apnamespace=0&aplimit=500&format=json&formatversion=2"
                + (from != null ? "&apcontinue=" + Uri.EscapeDataString(from) : "");
            JsonElement json = Json(url);
            foreach (JsonElement page in json.GetProperty("query").GetProperty("allpages").EnumerateArray())
            {
                titles.Add(page.GetProperty("title").GetString());
            }
            if (!json.TryGetProperty("continue", out JsonElement next))
            {
                break;
            }
            from = next.GetProperty("apcontinue").GetString();
            Thread.Sleep(700);
        }
        StringBuilder text = new StringBuilder();
        for (int i = 0; i < titles.Count; i += 50)
        {
            string batch = string.Join("|", titles.Skip(i).Take(50));
            JsonElement json = Json(api + "?action=query&redirects=1&format=json&formatversion=2&titles=" + Uri.EscapeDataString(batch));
            if (json.GetProperty("query").TryGetProperty("redirects", out JsonElement list))
            {
                foreach (JsonElement r in list.EnumerateArray())
                {
                    string target = r.GetProperty("to").GetString();
                    if (r.TryGetProperty("tofragment", out JsonElement fragment))
                    {
                        target += "#" + fragment.GetString();
                    }
                    text.Append(r.GetProperty("from").GetString()).Append('\t').Append(target).Append('\n');
                }
            }
            Thread.Sleep(700);
        }
        File.WriteAllText(Path.Combine(dir, "redirects.tsv"), text.ToString(), new UTF8Encoding(false));
        Console.WriteLine("redirects: " + titles.Count);
    }

    // The same block WikiUpdate.Page writes.
    private static string Block(string body, string title, string code, Dictionary<string, WikiConvert.Picture> pictures)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("parse", out JsonElement parse))
        {
            return null;
        }
        WikiConvert.Result result = WikiConvert.Convert(parse.GetProperty("text").GetString(), code);
        List<string> categories = new List<string>();
        if (parse.TryGetProperty("categories", out JsonElement list))
        {
            foreach (JsonElement category in list.EnumerateArray())
            {
                if (!category.TryGetProperty("hidden", out _))
                {
                    categories.Add(category.GetProperty("category").GetString().Replace('_', ' '));
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
        text.Append("rev ").Append(parse.GetProperty("revid").GetInt64().ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("cat ").Append(WikiConvert.Field(string.Join("|", categories))).Append('\n');
        foreach (string line in result.Lines)
        {
            text.Append(line).Append('\n');
        }
        return text.ToString();
    }

    // Pictures into the shared folder, listed in its index.tsv (key, file, width), as WikiUpdate.Pictures keeps them.
    private static int Pictures(string imageDir, Dictionary<string, WikiConvert.Picture> pictures)
    {
        Directory.CreateDirectory(imageDir);
        string indexPath = Path.Combine(imageDir, "index.tsv");
        Dictionary<string, string[]> index = new Dictionary<string, string[]>();
        List<string> order = new List<string>();
        if (File.Exists(indexPath))
        {
            foreach (string line in File.ReadAllLines(indexPath, Encoding.UTF8))
            {
                string[] parts = line.Split('\t');
                if (parts.Length >= 3 && !index.ContainsKey(parts[0]))
                {
                    index[parts[0]] = new[] { parts[1], parts[2] };
                    order.Add(parts[0]);
                }
            }
        }
        int added = 0;
        int seen = 0;
        foreach (KeyValuePair<string, WikiConvert.Picture> pair in pictures)
        {
            seen++;
            if (index.TryGetValue(pair.Key, out string[] known) && int.TryParse(known[1], out int width) && width >= pair.Value.Width
                && File.Exists(Path.Combine(imageDir, known[0])))
            {
                continue;
            }
            byte[] bytes = GetBytes(pair.Value.Url);
            Thread.Sleep(250);
            string extension = Extension(bytes);
            if (extension == null)
            {
                Console.WriteLine("picture skipped: " + pair.Value.Url);
                continue;
            }
            string file = SafeName(pair.Key) + extension;
            File.WriteAllBytes(Path.Combine(imageDir, file), bytes);
            if (!index.ContainsKey(pair.Key))
            {
                order.Add(pair.Key);
            }
            index[pair.Key] = new[] { file, pair.Value.Width.ToString(CultureInfo.InvariantCulture) };
            added++;
            if (added % 100 == 0)
            {
                Console.WriteLine("pictures: " + added + " new, " + seen + "/" + pictures.Count);
                Save(indexPath, index, order);
            }
        }
        Save(indexPath, index, order);
        return added;
    }

    private static void Save(string indexPath, Dictionary<string, string[]> index, List<string> order)
    {
        StringBuilder text = new StringBuilder();
        foreach (string key in order)
        {
            text.Append(key).Append('\t').Append(index[key][0]).Append('\t').Append(index[key][1]).Append('\n');
        }
        File.WriteAllText(indexPath, text.ToString(), new UTF8Encoding(false));
    }

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
        return bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? ".jpg" : null;
    }

    // The same file names WikiUpdate.SafeName gives.
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

    private static string Safe(string title)
    {
        // a stable hash (string.GetHashCode changes from run to run)
        uint hash = 2166136261;
        foreach (char c in title)
        {
            hash = (hash ^ c) * 16777619;
        }
        string name = string.Concat(title.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        return (name.Length > 60 ? name.Substring(0, 60) : name) + "_" + hash.ToString("x8");
    }

    private static JsonElement Json(string url)
    {
        string body = Get(url) ?? throw new IOException("no answer: " + url);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string Get(string url)
    {
        byte[] bytes = GetBytes(url);
        return bytes == null ? null : Encoding.UTF8.GetString(bytes);
    }

    private static byte[] GetBytes(string url)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using HttpResponseMessage response = Http.GetAsync(url).GetAwaiter().GetResult();
                if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                {
                    Thread.Sleep(5000 * (attempt + 1));
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine((int)response.StatusCode + " " + url);
                    return null;
                }
                return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Console.WriteLine("retry " + url + ": " + e.Message);
                Thread.Sleep(3000);
            }
        }
        return null;
    }
}
