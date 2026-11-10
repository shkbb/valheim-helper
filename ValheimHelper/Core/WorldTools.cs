using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper
{
    // Time of day, weather, loot rate, world clock speed and the optional sync with friends who
    // also run the mod. None of this goes through Terminal, so nothing is marked as cheated.
    internal static class WorldTools
    {
        private const string SyncRpc = "ValheimHelper_Sync";
        private const float SkipDurationSec = 12f; // same as EnvMan.SkipToMorning (sleeping)

        private static readonly AccessTools.FieldRef<EnvMan, bool> s_skipTime = AccessTools.FieldRefAccess<EnvMan, bool>("m_skipTime");
        private static readonly AccessTools.FieldRef<EnvMan, double> s_skipToTime = AccessTools.FieldRefAccess<EnvMan, double>("m_skipToTime");
        private static readonly AccessTools.FieldRef<EnvMan, double> s_timeSkipSpeed = AccessTools.FieldRefAccess<EnvMan, double>("m_timeSkipSpeed");
        private static readonly AccessTools.FieldRef<ZRoutedRpc, long> s_rpcOwnId = AccessTools.FieldRefAccess<ZRoutedRpc, long>("m_id");

        private static ZRoutedRpc s_registeredOn;

        // ---------------------------------------------------------------- clock

        // EnvMan.RescaleDayFraction stretches the real day 0.15..0.85 over 06:00..18:00.
        internal static float RealFractionToClockHours(float real)
        {
            float visual;
            if (real >= 0.15f && real <= 0.85f)
            {
                visual = 0.25f + (real - 0.15f) / 0.7f * 0.5f;
            }
            else if (real < 0.5f)
            {
                visual = real / 0.15f * 0.25f;
            }
            else
            {
                visual = 0.75f + (real - 0.85f) / 0.15f * 0.25f;
            }
            return visual * 24f;
        }

        internal static float ClockHoursToRealFraction(float hours)
        {
            float visual = Mathf.Repeat(hours, 24f) / 24f;
            if (visual >= 0.25f && visual <= 0.75f)
            {
                return 0.15f + (visual - 0.25f) / 0.5f * 0.7f;
            }
            if (visual < 0.25f)
            {
                return visual / 0.25f * 0.15f;
            }
            return 0.85f + (visual - 0.75f) / 0.25f * 0.15f;
        }

        internal static string FormatClock(float hours)
        {
            int minutes = Mathf.FloorToInt(Mathf.Repeat(hours, 24f) * 60f);
            return $"{minutes / 60:00}:{minutes % 60:00}";
        }

        internal static bool TryGetWorldClock(out float hours, out int day)
        {
            hours = 0f;
            day = 0;
            if (ZNet.instance == null || EnvMan.instance == null)
            {
                return false;
            }
            double seconds = ZNet.instance.GetTimeSeconds();
            long length = EnvMan.instance.m_dayLengthSec;
            day = (int)(seconds / length);
            hours = RealFractionToClockHours((float)(seconds % length / length));
            return true;
        }

        // Same mechanism as sleeping: the host fast-forwards the shared clock over a few seconds.
        // Time only moves forward, so an hour that already passed today means that hour tomorrow.
        internal static string SkipWorldClockTo(float hours)
        {
            if (ZNet.instance == null || EnvMan.instance == null)
            {
                return Lang.T("Зайди у світ.");
            }
            if (!ZNet.instance.IsServer())
            {
                return Lang.T("Перемотати час світу може лише хост.");
            }
            EnvMan envMan = EnvMan.instance;
            if (envMan.IsTimeSkipping())
            {
                return Lang.T("Час уже перемотується.");
            }
            double now = ZNet.instance.GetTimeSeconds();
            long length = envMan.m_dayLengthSec;
            double target = Math.Floor(now / length) * length + ClockHoursToRealFraction(hours) * length;
            if (target <= now + 1.0)
            {
                target += length;
            }
            s_skipToTime(envMan) = target;
            s_timeSkipSpeed(envMan) = (target - now) / SkipDurationSec;
            s_skipTime(envMan) = true;
            return Lang.F("Перемотую час світу до {0}.", FormatClock(hours));
        }

        // Host only: adds extra seconds to the shared clock every frame. Smelters, kilns, fermenters,
        // cooking stations and plants all measure progress against this clock.
        internal static void TickWorldClock()
        {
            if (State.WorldClockMultiplier <= 1f || ZNet.instance == null || !ZNet.instance.IsServer()
                || EnvMan.instance == null || EnvMan.instance.IsTimeSkipping() || Time.timeScale <= 0f)
            {
                return;
            }
            ZNet.instance.SetNetTime(ZNet.instance.GetTimeSeconds() + Time.deltaTime * (State.WorldClockMultiplier - 1f));
        }

        // Local only: EnvMan.m_debugTimeOfDay pins lighting and day/night logic in this game.
        internal static bool IsLocalTimeFixed => EnvMan.instance != null && EnvMan.instance.m_debugTimeOfDay;

        internal static float LocalFixedHours => EnvMan.instance != null ? EnvMan.instance.m_debugTime * 24f : 12f;

        internal static void SetLocalTime(float hours)
        {
            ApplyLocalTime(hours);
            Send("time", hours.ToString(CultureInfo.InvariantCulture));
        }

        // Restores saved local weather and fixed time without sending them to friends.
        internal static void RestoreLocal(string weather, float hours)
        {
            if (EnvMan.instance == null)
            {
                return;
            }
            ApplyWeather(weather ?? "");
            ApplyLocalTime(hours);
        }

        private static void ApplyLocalTime(float hours)
        {
            if (EnvMan.instance == null)
            {
                return;
            }
            if (hours < 0f)
            {
                EnvMan.instance.m_debugTimeOfDay = false;
                return;
            }
            EnvMan.instance.m_debugTime = Mathf.Repeat(hours, 24f) / 24f;
            EnvMan.instance.m_debugTimeOfDay = true;
        }

        // ---------------------------------------------------------------- weather

        internal static string WeatherOverride => EnvMan.instance != null ? EnvMan.instance.m_debugEnv : "";

        internal static string CurrentWeather
        {
            get
            {
                EnvSetup env = EnvMan.instance != null ? EnvMan.instance.GetCurrentEnvironment() : null;
                return env != null ? env.m_name : "";
            }
        }

        internal static List<string> GetWeatherNames()
        {
            List<string> names = new List<string>();
            if (EnvMan.instance == null)
            {
                return names;
            }
            foreach (EnvSetup env in EnvMan.instance.m_environments)
            {
                if (env != null && !string.IsNullOrEmpty(env.m_name) && !names.Contains(env.m_name))
                {
                    names.Add(env.m_name);
                }
            }
            return names;
        }

        // EnvMan.m_debugEnv has the highest priority of all environment overrides and applies
        // everywhere this game goes, until it is reset to "".
        internal static void SetWeather(string name)
        {
            ApplyWeather(name ?? "");
            Send("weather", name ?? "");
        }

        private static void ApplyWeather(string name)
        {
            if (EnvMan.instance == null || (name.Length > 0 && !GetWeatherNames().Contains(name)))
            {
                return;
            }
            EnvMan.instance.m_debugEnv = name;
        }

        // ---------------------------------------------------------------- loot

        internal static void SetLootMultiplier(float value)
        {
            ApplyLoot(value);
            Send("loot", State.LootMultiplier.ToString(CultureInfo.InvariantCulture));
        }

        private static void ApplyLoot(float value)
        {
            State.LootMultiplier = Mathf.Clamp(value, 1f, 10f);
            if (ZoneSystem.instance != null && Game.instance != null)
            {
                ZoneSystem.instance.UpdateWorldRates();
            }
        }

        // ---------------------------------------------------------------- sync with friends

        internal static void SendCurrentSettings()
        {
            Send("weather", WeatherOverride);
            Send("time", IsLocalTimeFixed ? LocalFixedHours.ToString(CultureInfo.InvariantCulture) : "-1");
            Send("loot", State.LootMultiplier.ToString(CultureInfo.InvariantCulture));
        }

        // A new ZRoutedRpc is created for every session, so registration is repeated per session.
        internal static void EnsureRpcRegistered()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(rpc, s_registeredOn))
            {
                return;
            }
            rpc.Register<string, string>(SyncRpc, OnSync);
            s_registeredOn = rpc;
        }

        // Players without the mod have no handler for this RPC and simply ignore it.
        private static void Send(string kind, string value)
        {
            if (!State.SyncWithFriends || ZRoutedRpc.instance == null)
            {
                return;
            }
            EnsureRpcRegistered();
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, SyncRpc, kind, value);
        }

        private static void OnSync(long sender, string kind, string value)
        {
            // A broadcast is also delivered locally; it has already been applied.
            if (ZRoutedRpc.instance == null || sender == s_rpcOwnId(ZRoutedRpc.instance) || !State.SyncWithFriends)
            {
                return;
            }
            string message;
            switch (kind)
            {
                case "weather":
                    ApplyWeather(value ?? "");
                    message = string.IsNullOrEmpty(value) ? Lang.T("Друг повернув звичайну погоду") : Lang.T("Друг змінив погоду: ") + value;
                    break;
                case "time":
                    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float hours))
                    {
                        return;
                    }
                    ApplyLocalTime(hours);
                    message = hours < 0f ? Lang.T("Друг повернув справжній час доби") : Lang.T("Друг зафіксував час: ") + FormatClock(hours);
                    break;
                case "loot":
                    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float loot))
                    {
                        return;
                    }
                    ApplyLoot(loot);
                    message = Lang.F("Друг змінив множник луту: ×{0:0.#}", State.LootMultiplier);
                    break;
                default:
                    return;
            }
            Log.Info(message);
            if (Player.m_localPlayer != null)
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, message);
            }
        }
    }

    // Per-frame work that must run even while the menu is closed.
    internal sealed class WorldTicker : MonoBehaviour
    {
        private bool m_loggedError;

        private void Update()
        {
            try
            {
                Settings.Tick();
                Perf.PerfSettings.ApplyFrameLimit();
                WorldTools.EnsureRpcRegistered();
                WorldTools.TickWorldClock();
                Gifts.Tick();
                StatsBook.Tick();
                PlayerTools.Tick();
                Storage.StorageSorter.Tick();
                Storage.FurnaceLoader.Tick();
                Storage.CraftFromChests.Tick();
                Storage.RemotePickup.Tick();
                TamedAnimals.Tick();
                BuilderView.Tick();
                ImageTuning.Tick();
                Map.PlaceContents.Tick();
                Map.ItemSources.Tick();
                Map.WorldVegetation.Tick();
                Map.Waypoints.Tick();
                Storage.RemoteStorage.Tick(Time.deltaTime);
            }
            catch (Exception e)
            {
                if (!m_loggedError)
                {
                    m_loggedError = true;
                    Log.Error(e);
                }
            }
        }

        // Settings that wait a moment before being written are written right away when the game closes.
        private void OnApplicationQuit()
        {
            Settings.Flush();
            Storage.StorageSettings.Flush();
            Map.MapSettings.Flush();
            Map.MapSketch.Flush();
            Map.Waypoints.Flush();
            Map.GamePins.Flush();
            Storage.RemoteStorage.SaveNow();
            Map.WorldVegetation.SaveNow();
            StatsBook.SaveNow();
        }
    }
}
