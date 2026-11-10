using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace ValheimHelper.Wiki
{
    // Pictures of the wiki (ValheimHelper/wiki/img, index.tsv: key → file): read from disk on a worker thread, a few
    // decoded per frame, and the least recently shown ones dropped when they take more than the memory budget.
    internal static class WikiImages
    {
        private sealed class Entry
        {
            public Texture2D Texture;
            public int LastFrame;
            public long Bytes;
        }

        private const long Budget = 80L << 20;
        private const int DecodesPerFrame = 3;

        private static string s_dir;
        private static Dictionary<string, string> s_files = new Dictionary<string, string>();
        private static readonly Dictionary<string, Entry> s_loaded = new Dictionary<string, Entry>();
        private static readonly HashSet<string> s_pending = new HashSet<string>();
        private static readonly HashSet<string> s_failed = new HashSet<string>();
        private static readonly ConcurrentQueue<KeyValuePair<string, byte[]>> s_read = new ConcurrentQueue<KeyValuePair<string, byte[]>>();
        private static long s_bytes;
        private static int s_frame;
        private static int s_decoded;

        internal static void SetDir(string dir)
        {
            if (dir == s_dir)
            {
                return;
            }
            s_dir = dir;
            Dictionary<string, string> files = new Dictionary<string, string>();
            try
            {
                string index = Path.Combine(dir, "index.tsv");
                if (File.Exists(index))
                {
                    foreach (string line in File.ReadAllLines(index, Encoding.UTF8))
                    {
                        string[] parts = line.Split('\t');
                        if (parts.Length >= 2)
                        {
                            files[parts[0]] = parts[1];
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
            s_files = files;
        }

        // Reads the index again after new pictures were fetched.
        internal static void Reload()
        {
            string dir = s_dir;
            s_dir = null;
            SetDir(dir);
        }

        internal static bool Has(string key)
        {
            return key != null && s_files.ContainsKey(key);
        }

        // The picture if it is loaded; otherwise starts loading it and returns null (a placeholder is drawn meanwhile).
        internal static Texture2D Get(string key)
        {
            if (key == null)
            {
                return null;
            }
            if (s_loaded.TryGetValue(key, out Entry entry))
            {
                entry.LastFrame = Time.frameCount;
                return entry.Texture;
            }
            if (s_failed.Contains(key) || s_pending.Contains(key) || !s_files.TryGetValue(key, out string file))
            {
                return null;
            }
            s_pending.Add(key);
            string path = Path.Combine(s_dir, file);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                byte[] bytes = null;
                try
                {
                    bytes = File.ReadAllBytes(path);
                }
                catch
                {
                    // missing or locked: stays a placeholder
                }
                s_read.Enqueue(new KeyValuePair<string, byte[]>(key, bytes));
            });
            return null;
        }

        // Once a frame: decodes what was read and keeps the memory within the budget.
        internal static void Update()
        {
            if (s_frame != Time.frameCount)
            {
                s_frame = Time.frameCount;
                s_decoded = 0;
            }
            while (s_decoded < DecodesPerFrame && s_read.TryDequeue(out KeyValuePair<string, byte[]> read))
            {
                s_pending.Remove(read.Key);
                if (read.Value == null)
                {
                    s_failed.Add(read.Key);
                    continue;
                }
                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (!texture.LoadImage(read.Value, true))
                {
                    UnityEngine.Object.Destroy(texture);
                    s_failed.Add(read.Key);
                    continue;
                }
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Trilinear;
                texture.anisoLevel = 1;
                long bytes = (long)texture.width * texture.height * 4 * 4 / 3;
                s_loaded[read.Key] = new Entry { Texture = texture, LastFrame = Time.frameCount, Bytes = bytes };
                s_bytes += bytes;
                s_decoded++;
            }
            if (s_bytes > Budget)
            {
                Trim();
            }
        }

        private static void Trim()
        {
            List<KeyValuePair<string, Entry>> entries = new List<KeyValuePair<string, Entry>>(s_loaded);
            entries.Sort((a, b) => a.Value.LastFrame.CompareTo(b.Value.LastFrame));
            foreach (KeyValuePair<string, Entry> pair in entries)
            {
                if (s_bytes <= Budget * 3 / 4 || pair.Value.LastFrame >= Time.frameCount - 1)
                {
                    break;
                }
                UnityEngine.Object.Destroy(pair.Value.Texture);
                s_loaded.Remove(pair.Key);
                s_bytes -= pair.Value.Bytes;
            }
        }

        internal static void Clear()
        {
            foreach (Entry entry in s_loaded.Values)
            {
                UnityEngine.Object.Destroy(entry.Texture);
            }
            s_loaded.Clear();
            s_bytes = 0;
        }
    }
}
