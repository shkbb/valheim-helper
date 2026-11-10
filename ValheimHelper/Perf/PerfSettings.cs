using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace ValheimHelper.Perf
{
    // Render layer preferences, kept between game launches in ValheimHelper/perf_settings.txt.
    internal static class PerfSettings
    {
        internal static bool Enabled = true;
        // GPU instancing for the materials of building pieces: identical pieces are drawn in one call.
        internal static bool Instancing = true;
        // Tiny details far away are not drawn and stop casting shadows.
        internal static bool DetailLod = true;
        // Fires out of view or tiny on screen stop simulating particles; far lights stop flickering.
        internal static bool FireLod = true;
        // Looping sounds farther than they can be heard are paused.
        internal static bool SoundLod = true;
        // Too many point lights: the least visible ones go off, and only the most visible ones keep shadows.
        internal static bool LightBudget = true;
        internal static int Preset = 1;
        // The map's whole-world work (computing resources, reading locations, scanning the world's objects).
        // Off: nothing of it runs, the map shows only what it already has.
        internal static bool MapBackground = true;
        // 0 — as the game (vsync), otherwise the frames a second the game is held to.
        internal static int FrameLimit;

        private static bool s_dirty;
        private static float s_saveAt;

        private static string FilePath => Path.Combine(Loader.Dir ?? ".", "perf_settings.txt");

        internal static LodPreset Current => LodRules.Presets[Mathf.Clamp(Preset, 0, LodRules.Presets.Length - 1)];

        private static int s_frameLimit = -1;

        // The frames a second the game is held to (the game itself only offers vsync).
        internal static void ApplyFrameLimit()
        {
            if (s_frameLimit == FrameLimit)
            {
                return;
            }
            s_frameLimit = FrameLimit;
            if (FrameLimit > 0)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = FrameLimit;
            }
            else
            {
                Application.targetFrameRate = -1;
            }
        }

        internal static void Changed()
        {
            s_dirty = true;
            s_saveAt = Time.unscaledTime + 1f;
        }

        internal static void SaveIfDirty()
        {
            if (s_dirty && Time.unscaledTime >= s_saveAt)
            {
                Save();
            }
        }

        internal static void Flush()
        {
            if (s_dirty)
            {
                Save();
            }
        }

        internal static void Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return;
                }
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        continue;
                    }
                    string key = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).Trim();
                    switch (key)
                    {
                        case "enabled": Enabled = value == "1"; break;
                        case "instancing": Instancing = value == "1"; break;
                        case "detail": DetailLod = value == "1"; break;
                        case "fire": FireLod = value == "1"; break;
                        case "sound": SoundLod = value == "1"; break;
                        case "lights": LightBudget = value == "1"; break;
                        case "mapbackground": MapBackground = value == "1"; break;
                        case "framelimit": int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out FrameLimit); break;
                        case "preset":
                            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int preset))
                            {
                                Preset = Mathf.Clamp(preset, 0, LodRules.Presets.Length - 1);
                            }
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private static void Save()
        {
            s_dirty = false;
            try
            {
                File.WriteAllLines(FilePath, new[]
                {
                    "enabled=" + Bit(Enabled),
                    "instancing=" + Bit(Instancing),
                    "detail=" + Bit(DetailLod),
                    "fire=" + Bit(FireLod),
                    "sound=" + Bit(SoundLod),
                    "lights=" + Bit(LightBudget),
                    "preset=" + Preset.ToString(CultureInfo.InvariantCulture),
                    "mapbackground=" + Bit(MapBackground),
                    "framelimit=" + FrameLimit.ToString(CultureInfo.InvariantCulture),
                });
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private static string Bit(bool value)
        {
            return value ? "1" : "0";
        }
    }
}
