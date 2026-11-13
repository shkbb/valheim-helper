using System;

namespace ValheimHelper.Wiki
{
    // The wikis the guide reads. A pack's folder name says where it comes from: "en", "uk", "de"… are the Valheim wiki
    // on Fandom; "wg-en" (and "wg-<lang>" when Weird Gloop opens more languages) is the Valheim Wiki on Weird Gloop.
    // Russian versions are never taken, from any wiki.
    internal static class WikiSources
    {
        internal const string WeirdGloopPrefix = "wg-";
        internal const string WeirdGloopHost = "valheim.weirdgloop.org";

        internal static bool IsWeirdGloop(string code)
        {
            return code != null && code.StartsWith(WeirdGloopPrefix, StringComparison.Ordinal);
        }

        // The language of a pack, without the source: "wg-en" → "en".
        internal static string Language(string code)
        {
            return IsWeirdGloop(code) ? code.Substring(WeirdGloopPrefix.Length) : code;
        }

        internal static bool Allowed(string language)
        {
            return !string.IsNullOrEmpty(language) && !language.Equals("ru", StringComparison.OrdinalIgnoreCase);
        }

        internal static string SourceName(string code)
        {
            return IsWeirdGloop(code) ? "Weird Gloop" : "Fandom";
        }

        // The site of a pack: https://valheim.fandom.com[/de], https://valheim.weirdgloop.org, https://de.valheim.weirdgloop.org
        internal static string Site(string code)
        {
            string language = Language(code);
            bool english = string.IsNullOrEmpty(language) || language == "en";
            if (IsWeirdGloop(code))
            {
                return english ? "https://" + WeirdGloopHost : "https://" + language + "." + WeirdGloopHost;
            }
            return english ? "https://valheim.fandom.com" : "https://valheim.fandom.com/" + language;
        }

        internal static string Api(string code)
        {
            return Site(code) + "/api.php";
        }

        // Where an article's address starts, as links in the wiki's HTML write it.
        internal static string ArticlePath(string code)
        {
            if (IsWeirdGloop(code))
            {
                return "/w/";
            }
            string language = Language(code);
            return string.IsNullOrEmpty(language) || language == "en" ? "/wiki/" : "/" + language + "/wiki/";
        }

        internal static string ArticleUrl(string code, string title)
        {
            string host = IsWeirdGloop(code) ? Site(code) : "https://valheim.fandom.com";
            return host + ArticlePath(code) + title.Replace(' ', '_');
        }

        // Picture keys are kept apart per wiki: the same file name can be another picture on another wiki.
        internal static string KeyPrefix(string code)
        {
            if (IsWeirdGloop(code))
            {
                string language = Language(code);
                return language == "en" ? "wg~" : "wg-" + language + "~";
            }
            return string.IsNullOrEmpty(code) || code == "en" ? "" : code + "~";
        }
    }
}
