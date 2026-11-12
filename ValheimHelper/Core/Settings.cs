using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace ValheimHelper
{
    // Menu toggles, multipliers, the locally fixed weather and time of day, and the menu window, kept between
    // game launches in ValheimHelper/settings.txt. Changes are noticed by comparing a snapshot twice a second
    // and written once they stop changing, so the menu, key binds and friend sync need no save calls of their own.
    // (Key binds, the map and the storage have their own files.)
    internal static class Settings
    {
        private const float CheckInterval = 0.5f;
        private const float SaveDelay = 1f;

        private static string FilePath => Path.Combine(Loader.Dir ?? ".", "settings.txt");

        private static string s_saved = "";
        private static string s_pending;
        private static float s_pendingSince;
        private static float s_checkAt;

        // Weather and fixed time live in EnvMan, which exists only inside a world; the stored values are put back
        // into every new EnvMan once, and only then read from it again.
        private static string s_weather = "";
        private static float s_fixedHours = -1f;
        private static EnvMan s_envRestored;
        private static Player s_playerSeen;

        // One value of the settings file before they are loaded (the menu language is needed first).
        internal static string Peek(string key)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        int separator = line.IndexOf('=');
                        if (separator > 0 && line.Substring(0, separator).Trim() == key)
                        {
                            return line.Substring(separator + 1).Trim();
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
            return null;
        }

        internal static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        int separator = line.IndexOf('=');
                        if (separator > 0)
                        {
                            Apply(line.Substring(0, separator).Trim(), line.Substring(separator + 1).Trim());
                        }
                    }
                    Log.Info("Settings loaded");
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
            s_saved = Snapshot();
        }

        private static void Apply(string key, string value)
        {
            bool on = value == "1";
            switch (key)
            {
#if EDITION_FULL
                case "god": State.GodMode = on; break;
                case "stamina": State.InfiniteStamina = on; break;
                case "eitr": State.InfiniteEitr = on; break;
                case "food": State.FoodNeverExpires = on; break;
                case "weight": State.NoWeightLimit = on; break;
                case "durability": State.NoDurabilityLoss = on; break;
                case "fly": State.Fly = on; break;
                case "mapteleport": State.MapTeleport = on; break;
                case "portals": State.TeleportAnything = on; break;
                case "underwater": State.WalkUnderwater = on; break;
                case "freebuild": State.FreeBuild = on; break;
                case "nosupport": State.NoSupportLimit = on; break;
                case "bypasscheats": State.BypassCheatChecks = on; break;
#endif
                case "sync": State.SyncWithFriends = on; break;
#if EDITION_FULL
                case "acceptgifts": Gifts.AcceptGifts = on; break;
                case "run": State.RunSpeedMultiplier = Number(value, 1f, 5f); break;
                case "damage": State.DamageMultiplier = Number(value, 1f, 50f); break;
                case "loot": State.LootMultiplier = Number(value, 1f, 10f); break;
                case "worldclock": State.WorldClockMultiplier = Number(value, 1f, 30f); break;
                case "gamespeed": State.GameSpeed = Number(value, 1f, 5f); break;
                case "weather": s_weather = value; break;
                case "fixedtime": s_fixedHours = value.Length == 0 ? -1f : Number(value, -1f, 24f); break;
#endif
                case "menux": State.MenuX = Number(value, -10000f, 10000f); break;
                case "menuy": State.MenuY = Number(value, -10000f, 10000f); break;
                case "menuw": State.MenuW = Number(value, 600f, 10000f); break;
                case "menuh": State.MenuH = Number(value, 400f, 10000f); break;
                case "brightness": State.ImageBrightness = Number(value, ImageTuning.MinBrightness, ImageTuning.MaxBrightness); break;
                case "gamma": State.ImageGamma = Number(value, -1f, 1f); break;
                case "glow": State.ImageGlow = Number(value, 0f, 1f); break;
                case "menutab": State.MenuTab = Mathf.RoundToInt(Number(value, 0f, 100f)); break;
                case "menusection": State.MenuSection = Mathf.RoundToInt(Number(value, 0f, 100f)); break;
                case "menupage": State.MenuPage = Mathf.RoundToInt(Number(value, 0f, 100f)); break;
                case "wikilang": State.WikiLanguage = value; break;
                case "uilang": Lang.Chosen = value; break;
                case "wikipage": State.WikiPage = value; break;
                case "wikiupdate": State.WikiAutoUpdate = value != "0"; break;
                case "autoupdate": Updater.AutoCheck = value != "0"; break;
                case "effectduration": State.EffectDuration = Mathf.RoundToInt(Number(value, 0f, 100f)); break;
            }
        }

        private static float Number(string value, float min, float max)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number)
                ? Mathf.Clamp(number, min, max)
                : min;
        }

        // Called every frame.
        internal static void Tick()
        {
            RestoreInWorld();
            float now = Time.unscaledTime;
            if (now < s_checkAt)
            {
                return;
            }
            s_checkAt = now + CheckInterval;
            string snapshot = Snapshot();
            if (snapshot == s_saved)
            {
                s_pending = null;
                return;
            }
            if (snapshot != s_pending)
            {
                s_pending = snapshot;
                s_pendingSince = now;
                return;
            }
            if (now - s_pendingSince >= SaveDelay)
            {
                Write(snapshot);
            }
        }

        internal static void Flush()
        {
            string snapshot = Snapshot();
            if (snapshot != s_saved)
            {
                Write(snapshot);
            }
        }

        // Puts back what only a running world can hold, once per world / character.
        private static void RestoreInWorld()
        {
#if EDITION_FULL
            EnvMan env = EnvMan.instance;
            if (env != null && !ReferenceEquals(env, s_envRestored))
            {
                s_envRestored = env;
                WorldTools.RestoreLocal(s_weather, s_fixedHours);
            }
            Player player = Player.m_localPlayer;
            if (player != null && !ReferenceEquals(player, s_playerSeen))
            {
                s_playerSeen = player;
                // A saved "build everything" needs the cached build menu refreshed, loot and durability the world rates.
                if (State.FreeBuild)
                {
                    Actions.RefreshBuildPieces();
                }
                if (State.NoDurabilityLoss || State.LootMultiplier != 1f)
                {
                    Actions.RefreshWorldRates();
                }
            }
#endif
        }

        private static string Snapshot()
        {
#if EDITION_FULL
            EnvMan env = EnvMan.instance;
            if (env != null && ReferenceEquals(env, s_envRestored))
            {
                s_weather = WorldTools.WeatherOverride ?? "";
                s_fixedHours = WorldTools.IsLocalTimeFixed ? WorldTools.LocalFixedHours : -1f;
            }
#endif
            List<string> lines = new List<string>
            {
#if EDITION_FULL
                Bit("god", State.GodMode),
                Bit("stamina", State.InfiniteStamina),
                Bit("eitr", State.InfiniteEitr),
                Bit("food", State.FoodNeverExpires),
                Bit("weight", State.NoWeightLimit),
                Bit("durability", State.NoDurabilityLoss),
                Bit("fly", State.Fly),
                Bit("mapteleport", State.MapTeleport),
                Bit("portals", State.TeleportAnything),
                Bit("underwater", State.WalkUnderwater),
                Bit("freebuild", State.FreeBuild),
                Bit("nosupport", State.NoSupportLimit),
                Bit("bypasscheats", State.BypassCheatChecks),
                Bit("sync", State.SyncWithFriends),
                Bit("acceptgifts", Gifts.AcceptGifts),
                Num("run", State.RunSpeedMultiplier),
                Num("damage", State.DamageMultiplier),
                Num("loot", State.LootMultiplier),
                Num("worldclock", State.WorldClockMultiplier),
                Num("gamespeed", State.GameSpeed),
                "weather=" + s_weather,
                "fixedtime=" + (s_fixedHours < 0f ? "" : s_fixedHours.ToString("0.###", CultureInfo.InvariantCulture)),
#endif
                Num("menux", Mathf.Round(State.MenuX)),
                Num("menuy", Mathf.Round(State.MenuY)),
                Num("menuw", Mathf.Round(State.MenuW)),
                Num("menuh", Mathf.Round(State.MenuH)),
                Num("brightness", State.ImageBrightness),
                Num("gamma", State.ImageGamma),
                Num("glow", State.ImageGlow),
                Bit("autoupdate", Updater.AutoCheck),
                "menusection=" + State.MenuSection.ToString(CultureInfo.InvariantCulture),
                "menupage=" + State.MenuPage.ToString(CultureInfo.InvariantCulture),
                "wikilang=" + (State.WikiLanguage ?? ""),
                "uilang=" + (Lang.Chosen ?? ""),
                "wikipage=" + (State.WikiPage ?? "").Replace("\n", " "),
                "wikiupdate=" + (State.WikiAutoUpdate ? "1" : "0"),
                "effectduration=" + State.EffectDuration.ToString(CultureInfo.InvariantCulture),
            };
            StringBuilder text = new StringBuilder();
            foreach (string line in lines)
            {
                text.Append(line).Append('\n');
            }
            return text.ToString();
        }

        private static string Bit(string key, bool value)
        {
            return key + "=" + (value ? "1" : "0");
        }

        private static string Num(string key, float value)
        {
            return key + "=" + value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static void Write(string snapshot)
        {
            s_saved = snapshot;
            s_pending = null;
            try
            {
                File.WriteAllText(FilePath, snapshot);
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        // "Скинути все": every toggle off, every multiplier x1, the game's own weather and time.
        internal static void ResetAll()
        {
#if EDITION_FULL
            State.GodMode = false;
            State.InfiniteStamina = false;
            State.InfiniteEitr = false;
            State.FoodNeverExpires = false;
            State.NoWeightLimit = false;
            Actions.SetNoDurabilityLoss(false);
            Actions.SetFly(false);
            State.MapTeleport = false;
            State.TeleportAnything = false;
            State.WalkUnderwater = false;
            Actions.SetFreeBuild(false);
            State.NoSupportLimit = false;
            State.BypassCheatChecks = false;
            State.RunSpeedMultiplier = 1f;
            State.DamageMultiplier = 1f;
            State.WorldClockMultiplier = 1f;
            State.GameSpeed = 1f;
            State.LootMultiplier = 1f;
            Actions.RefreshWorldRates();
            s_weather = "";
            s_fixedHours = -1f;
            WorldTools.RestoreLocal("", -1f);
#endif
        }
    }
}
