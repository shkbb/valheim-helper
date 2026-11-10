using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;

namespace ValheimHelper
{
    // World modifiers ("Змінники світу") changed inside a running world, by the host.
    //
    // The game counts a world as cheated only when its option keys contain a value that the world modifier menu
    // itself cannot produce (ServerOptionsGUI.WorldContainsCheatedModifiers), and its own setworldmodifier /
    // setworldpreset console commands are not cheats. So only the options of that menu are offered here, read
    // from the menu's own sliders, toggles and preset buttons, and every key is checked against the menu's list of
    // possible values before it is written. Keys go through ZoneSystem.SetGlobalKey / RemoveGlobalKey, which also
    // update the world's saved option keys and send them to everyone.
    internal static class WorldModifierEditor
    {
        private static readonly FieldInfo s_modifiers = AccessTools.Field(typeof(ServerOptionsGUI), "m_modifiers");
        private static readonly FieldInfo s_presets = AccessTools.Field(typeof(ServerOptionsGUI), "m_presets");
        private static readonly MethodInfo s_possible = AccessTools.Method(typeof(ServerOptionsGUI), "GetPossibleGlobalKeyValues");

        private static readonly Dictionary<string, string> s_toggleNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "nobuildcost", Lang.T("Безоплатне будівництво") },
            { "playerevents", Lang.T("Напади на бази гравців") },
            { "fire", Lang.T("Вогненебезпечно") },
            { "passivemobs", Lang.T("Пасивні вороги") },
            { "nomap", Lang.T("Без мапи") },
        };

        internal sealed class Option
        {
            public string Label;
            public string Tooltip;
            public WorldModifierOption Value;
            public List<string> Keys;
        }

        internal sealed class SliderInfo
        {
            public WorldModifiers Modifier;
            public string Name;
            public string Tooltip;
            public readonly List<Option> Options = new List<Option>();
            public int Current;
        }

        internal sealed class ToggleInfo
        {
            public string Key;
            public string Name;
            public string Tooltip;
            public bool On;
        }

        internal sealed class PresetInfo
        {
            public WorldPresets Preset;
            public string Name;
            public List<string> Keys;
        }

        // null when the editor can be used, otherwise why not.
        internal static string Unavailable()
        {
            if (ZNet.instance == null || ZoneSystem.instance == null || ZNet.World == null)
            {
                return Lang.T("Зайди у світ.");
            }
            if (ServerOptionsGUI.m_instance == null || !(s_modifiers?.GetValue(null) is KeyUI[]) || s_possible == null)
            {
                return Lang.T("Меню змінників гри недоступне в цій версії гри.");
            }
            return null;
        }

        internal static bool IsHost => ZNet.instance != null && ZNet.instance.IsServer();

        private static List<string> CurrentKeys()
        {
            return ZoneSystem.instance != null ? ZoneSystem.instance.GetGlobalKeys() : new List<string>();
        }

        private static bool Contains(IList<string> keys, string key)
        {
            foreach (string candidate in keys)
            {
                if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string KeyName(string key)
        {
            int space = key.IndexOf(' ');
            return (space > 0 ? key.Substring(0, space) : key).ToLowerInvariant();
        }

        private static string Localize(string text)
        {
            return string.IsNullOrEmpty(text) || Localization.instance == null ? text ?? "" : Localization.instance.Localize(text);
        }

        // ---------------------------------------------------------------- reading

        internal static List<SliderInfo> Sliders(IList<string> keys = null)
        {
            keys = keys ?? CurrentKeys();
            List<SliderInfo> result = new List<SliderInfo>();
            if (!(s_modifiers?.GetValue(null) is KeyUI[] modifiers))
            {
                return result;
            }
            foreach (KeyUI ui in modifiers)
            {
                if (!(ui is KeySlider slider) || slider.m_settings == null || slider.m_settings.Count == 0)
                {
                    continue;
                }
                SliderInfo info = new SliderInfo
                {
                    Modifier = slider.m_modifier,
                    Name = Localize(slider.m_modifier.GetDisplayString()),
                    Tooltip = Localize(slider.m_toolTip),
                };
                foreach (KeySlider.SliderSetting setting in slider.m_settings)
                {
                    info.Options.Add(new Option
                    {
                        Label = Localize(setting.m_name),
                        Tooltip = Localize(setting.m_toolTip),
                        Value = setting.m_modifierValue,
                        Keys = new List<string>(setting.m_keys ?? new List<string>()),
                    });
                }
                info.Current = Match(info, keys, slider.m_defaultIndex);
                result.Add(info);
            }
            return result;
        }

        // The option whose keys are all set (the one with most keys wins); otherwise the option without keys.
        private static int Match(SliderInfo slider, IList<string> keys, int fallback)
        {
            int best = -1;
            int bestCount = -1;
            int empty = -1;
            for (int i = 0; i < slider.Options.Count; i++)
            {
                List<string> optionKeys = slider.Options[i].Keys;
                if (optionKeys.Count == 0)
                {
                    empty = empty < 0 ? i : empty;
                    continue;
                }
                bool all = true;
                foreach (string key in optionKeys)
                {
                    all &= Contains(keys, key);
                }
                if (all && optionKeys.Count > bestCount)
                {
                    best = i;
                    bestCount = optionKeys.Count;
                }
            }
            if (best >= 0)
            {
                return best;
            }
            return empty >= 0 ? empty : Math.Max(0, Math.Min(fallback, slider.Options.Count - 1));
        }

        internal static List<ToggleInfo> Toggles(IList<string> keys = null)
        {
            keys = keys ?? CurrentKeys();
            List<ToggleInfo> result = new List<ToggleInfo>();
            if (!(s_modifiers?.GetValue(null) is KeyUI[] modifiers))
            {
                return result;
            }
            foreach (KeyUI ui in modifiers)
            {
                if (!(ui is KeyToggle toggle) || string.IsNullOrEmpty(toggle.m_enabledKey))
                {
                    continue;
                }
                string name = null;
                TMP_Text label = toggle.GetComponentInChildren<TMP_Text>(true);
                if (label != null && !string.IsNullOrEmpty(label.text))
                {
                    name = Localize(label.text);
                }
                if (string.IsNullOrEmpty(name) || name.StartsWith("$") || name.StartsWith("["))
                {
                    name = s_toggleNames.TryGetValue(KeyName(toggle.m_enabledKey), out string known) ? known : toggle.m_enabledKey;
                }
                result.Add(new ToggleInfo
                {
                    Key = toggle.m_enabledKey.ToLowerInvariant(),
                    Name = name,
                    Tooltip = Localize(toggle.m_toolTip),
                    On = Contains(keys, toggle.m_enabledKey),
                });
            }
            return result;
        }

        internal static List<PresetInfo> Presets()
        {
            List<PresetInfo> result = new List<PresetInfo>();
            if (!(s_presets?.GetValue(null) is KeyButton[] presets))
            {
                return result;
            }
            foreach (KeyButton button in presets)
            {
                if (button == null || button.m_keys == null)
                {
                    continue;
                }
                result.Add(new PresetInfo { Preset = button.m_preset, Name = Localize(button.m_preset.GetDisplayString()), Keys = new List<string>(button.m_keys) });
            }
            return result;
        }

        // What a preset button of the menu sets for every slider and toggle.
        internal static void PresetTargets(PresetInfo preset, out Dictionary<WorldModifiers, int> sliders, out Dictionary<string, bool> toggles)
        {
            sliders = new Dictionary<WorldModifiers, int>();
            toggles = new Dictionary<string, bool>();
            foreach (SliderInfo slider in Sliders(preset.Keys))
            {
                sliders[slider.Modifier] = slider.Current;
            }
            foreach (ToggleInfo toggle in Toggles(preset.Keys))
            {
                toggles[toggle.Key] = toggle.On;
            }
        }

        // Option keys in the world that the modifier menu cannot produce: these are what marks the world as cheated.
        internal static List<string> NonMenuKeys()
        {
            List<string> result = new List<string>();
            if (ZNet.World == null || s_possible == null)
            {
                return result;
            }
            List<string> possible = (List<string>)s_possible.Invoke(null, null);
            foreach (string key in ZNet.World.m_startingGlobalKeys)
            {
                if (!key.StartsWith("preset") && !Contains(possible, key))
                {
                    result.Add(key);
                }
            }
            return result;
        }

        // ---------------------------------------------------------------- changing (host only)

        // Applies the chosen slider options and toggle states. Returns the message for the player.
        internal static string Apply(Dictionary<WorldModifiers, int> sliderChanges, Dictionary<string, bool> toggleChanges)
        {
            string unavailable = Unavailable();
            if (unavailable != null)
            {
                return unavailable;
            }
            if (!IsHost)
            {
                return Lang.T("Змінники світу може змінювати лише хост.");
            }
            List<string> possible = (List<string>)s_possible.Invoke(null, null);
            List<SliderInfo> sliders = Sliders();
            List<ToggleInfo> toggles = Toggles();

            // Everything is checked first, so nothing is half-applied.
            foreach (KeyValuePair<WorldModifiers, int> change in sliderChanges)
            {
                SliderInfo slider = sliders.Find(s => s.Modifier == change.Key);
                if (slider == null || change.Value < 0 || change.Value >= slider.Options.Count)
                {
                    return Lang.T("Не знайшов такого змінника, нічого не змінено.");
                }
                foreach (string key in slider.Options[change.Value].Keys)
                {
                    if (!Contains(possible, key))
                    {
                        return Lang.F("Значення «{0}» немає в меню гри, тому нічого не змінено.", key);
                    }
                }
            }
            foreach (KeyValuePair<string, bool> change in toggleChanges)
            {
                if (toggles.Find(t => t.Key == change.Key) == null || !Contains(possible, change.Key))
                {
                    return Lang.F("Перемикача «{0}» немає в меню гри, тому нічого не змінено.", change.Key);
                }
            }

            List<string> changed = new List<string>();
            List<string> current = CurrentKeys();
            foreach (KeyValuePair<WorldModifiers, int> change in sliderChanges)
            {
                SliderInfo slider = sliders.Find(s => s.Modifier == change.Key);
                if (slider.Current == change.Value)
                {
                    continue;
                }
                Option target = slider.Options[change.Value];
                HashSet<string> keep = new HashSet<string>();
                foreach (string key in target.Keys)
                {
                    keep.Add(KeyName(key));
                }
                HashSet<string> removed = new HashSet<string>();
                foreach (Option option in slider.Options)
                {
                    foreach (string key in option.Keys)
                    {
                        string name = KeyName(key);
                        if (!keep.Contains(name) && removed.Add(name) && HasKeyName(current, name))
                        {
                            ZoneSystem.instance.RemoveGlobalKey(name);
                        }
                    }
                }
                foreach (string key in target.Keys)
                {
                    ZoneSystem.instance.SetGlobalKey(key.ToLowerInvariant());
                }
                changed.Add($"{slider.Name}: {slider.Options[slider.Current].Label} → {target.Label}");
            }
            foreach (KeyValuePair<string, bool> change in toggleChanges)
            {
                ToggleInfo toggle = toggles.Find(t => t.Key == change.Key);
                if (toggle.On == change.Value)
                {
                    continue;
                }
                if (change.Value)
                {
                    ZoneSystem.instance.SetGlobalKey(toggle.Key);
                }
                else
                {
                    ZoneSystem.instance.RemoveGlobalKey(KeyName(toggle.Key));
                }
                changed.Add($"{toggle.Name}: {(change.Value ? Lang.T("увімкнено") : Lang.T("вимкнено"))}");
            }
            if (changed.Count == 0)
            {
                return Lang.T("Нічого не змінилося.");
            }
            // A stored preset name would be applied again on the next world load and bring the old values back;
            // the individual keys already describe the world completely.
            if (HasKeyName(CurrentKeys(), "preset"))
            {
                ZoneSystem.instance.RemoveGlobalKey("preset");
            }
            string message = Lang.T("Змінники світу змінено: ") + string.Join("; ", changed.ToArray()) + ".";
            Log.Info(message + " Keys: " + string.Join(", ", ZNet.World.m_startingGlobalKeys.ToArray()));
            return message;
        }

        private static bool HasKeyName(IList<string> keys, string name)
        {
            foreach (string key in keys)
            {
                if (KeyName(key) == name)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
