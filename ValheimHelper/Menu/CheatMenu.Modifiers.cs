using System.Collections.Generic;
using UnityEngine;

namespace ValheimHelper
{
    // "Змінники" tab: the world modifier menu of the game, inside a running world.
    internal sealed partial class CheatMenu
    {
        // Refreshed on Layout only, so every event of a frame draws the same controls.
        private string m_modUnavailable;
        private bool m_modIsHost;
        private List<WorldModifierEditor.SliderInfo> m_modSliders = new List<WorldModifierEditor.SliderInfo>();
        private List<WorldModifierEditor.ToggleInfo> m_modToggles = new List<WorldModifierEditor.ToggleInfo>();
        private List<WorldModifierEditor.PresetInfo> m_modPresets = new List<WorldModifierEditor.PresetInfo>();
        private List<string> m_modNonMenuKeys = new List<string>();
        private readonly List<string> m_modPendingText = new List<string>();
        private float m_modReadAt = -10f;

        // Chosen but not applied yet.
        private readonly Dictionary<WorldModifiers, int> m_modPendingSliders = new Dictionary<WorldModifiers, int>();
        private readonly Dictionary<string, bool> m_modPendingToggles = new Dictionary<string, bool>();

        private void DrawModifiersTab()
        {
            if (Event.current.type == EventType.Layout)
            {
                RefreshModifiers();
            }
            if (m_modUnavailable != null)
            {
                GUILayout.Label(m_modUnavailable);
                return;
            }

            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Змінники світу"), HelperSkin.Header);
            GUILayout.Label(Lang.T("<color=#c0c0c0>Ті самі налаштування, що й у меню «Змінники світу» при виборі світу, але прямо в грі. " +
                "Гра вважає світ «читерським» лише тоді, коли в ньому є значення, яких у тому меню немає (їх ставлять тільки консоллю). " +
                "Тут можна обрати тільки варіанти з меню гри, і мод ще раз звіряє кожне значення з її списком, тож досягнення не блокуються. " +
                "Сама гра теж дозволяє так міняти змінники командами setworldmodifier / setworldpreset без позначки читів.</color>"));
            GUILayout.Label(Lang.T("<color=#ffb040>Зверни увагу: досягнення, прив'язані до складності бою (за гру на складній чи дуже складній), " +
                "зараховуються лише поки стоїть така складність. Якщо знизити «Бій», прогрес саме цих досягнень у цей час не йде.</color>"));
            if (m_modNonMenuKeys.Count == 0)
            {
                GUILayout.Label(Lang.T("<color=#80ff80>Зараз у світі лише значення з меню гри — за змінниками світ не «читерський».</color>"));
            }
            else
            {
                GUILayout.Label(Lang.T("<color=#ff8060>У світі вже є значення не з меню гри, через них він «читерський»: ") + string.Join(", ", m_modNonMenuKeys.ToArray()) +
                    Lang.T(". Прибрати їх можна в «Налаштування» → «Досягнення» → «Очистити світ». Тут вони не змінюються.</color>"));
            }
            if (!m_modIsHost)
            {
                GUILayout.Label(Lang.T("<color=#ffd080>Змінювати може лише хост світу, тобі видно поточні значення.</color>"));
            }
            GUILayout.EndVertical();

#if EDITION_FULL
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Лут"), HelperSkin.Header);
            float loot = MultiplierSlider(Lang.T("Множник луту мода (моби, скрині, дерева, руда)"), State.LootMultiplier, 1f, 10f, 0.5f, "loot");
            if (loot != State.LootMultiplier)
            {
                WorldTools.SetLootMultiplier(loot);
            }
            GUILayout.Label(Lang.T("<color=#c0c0c0>Діє поверх змінника «Ресурси» нижче: той — для всього світу й лише з варіантів гри, а цей — у твоїй грі, до ×10, " +
                "і самі змінники світу не чіпає, тож досягнення теж. Лут рахує гра того, хто вбив моба чи відкрив скриню, тому з синхронізацією " +
                "(«Час і погода») множник діє в обох.</color>"));
            GUILayout.EndVertical();
#endif

            if (m_modIsHost && m_modPresets.Count > 0)
            {
                GUILayout.BeginVertical(HelperSkin.Section);
                GUILayout.Label(Lang.T("Шаблони"), HelperSkin.Header);
                GUILayout.Label(Lang.T("<color=#c0c0c0>Шаблон лише заповнює вибір нижче; нічого не зміниться, поки не натиснеш «Застосувати».</color>"));
                for (int i = 0; i < m_modPresets.Count; i += 3)
                {
                    GUILayout.BeginHorizontal();
                    for (int j = i; j < Mathf.Min(i + 3, m_modPresets.Count); j++)
                    {
                        if (GUILayout.Button(m_modPresets[j].Name))
                        {
                            ChoosePreset(m_modPresets[j]);
                        }
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndVertical();
            }

            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Налаштувати"), HelperSkin.Header);
            foreach (WorldModifierEditor.SliderInfo slider in m_modSliders)
            {
                int shown = m_modPendingSliders.TryGetValue(slider.Modifier, out int pending) ? pending : slider.Current;
                GUILayout.Label(Lang.F("<b>{0}</b>: зараз «{1}»", slider.Name, slider.Options[slider.Current].Label));
                string[] labels = new string[slider.Options.Count];
                for (int i = 0; i < labels.Length; i++)
                {
                    labels[i] = slider.Options[i].Label;
                }
                int chosen = GUILayout.SelectionGrid(shown, labels, Mathf.Min(labels.Length, 5));
                if (m_modIsHost && chosen != shown)
                {
                    if (chosen == slider.Current)
                    {
                        m_modPendingSliders.Remove(slider.Modifier);
                    }
                    else
                    {
                        m_modPendingSliders[slider.Modifier] = chosen;
                    }
                }
                // Always one label, so choosing an option in a click never changes the number of controls.
                GUILayout.Label("<color=#a8a8a8>" + (slider.Options[shown].Tooltip ?? "") + "</color>");
                GUILayout.Space(4f);
            }
            for (int i = 0; i < m_modToggles.Count; i += 2)
            {
                GUILayout.BeginHorizontal();
                ModifierToggle(m_modToggles[i]);
                if (i + 1 < m_modToggles.Count)
                {
                    ModifierToggle(m_modToggles[i + 1]);
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();

            if (!m_modIsHost)
            {
                return;
            }
            GUILayout.BeginVertical(HelperSkin.Section);
            if (m_modPendingText.Count == 0)
            {
                GUILayout.Label(Lang.T("<color=#909090>Нічого не обрано для зміни.</color>"));
            }
            else
            {
                GUILayout.Label(Lang.T("<b>Буде змінено:</b>\n") + string.Join("\n", m_modPendingText.ToArray()));
                GUILayout.BeginHorizontal();
                if (ConfirmButton("applymodifiers", Lang.T("Застосувати для всього світу")))
                {
                    ShowMessage(WorldModifierEditor.Apply(new Dictionary<WorldModifiers, int>(m_modPendingSliders), new Dictionary<string, bool>(m_modPendingToggles)));
                    m_modPendingSliders.Clear();
                    m_modPendingToggles.Clear();
                    m_modReadAt = -10f;
                }
                if (GUILayout.Button(Lang.T("Скасувати вибір"), GUILayout.Width(180f)))
                {
                    m_modPendingSliders.Clear();
                    m_modPendingToggles.Clear();
                }
                GUILayout.EndHorizontal();
                GUILayout.Label(Lang.T("<color=#c0c0c0>Зміни діють одразу для всіх гравців і зберігаються разом зі світом.</color>"));
            }
            GUILayout.EndVertical();
        }

        private void ModifierToggle(WorldModifierEditor.ToggleInfo toggle)
        {
            bool shown = m_modPendingToggles.TryGetValue(toggle.Key, out bool pending) ? pending : toggle.On;
            string label = " " + toggle.Name + (shown != toggle.On ? " <color=#ffd080>*</color>" : "");
            bool value = GUILayout.Toggle(shown, label, GUILayout.Width(318f));
            if (m_modIsHost && value != shown)
            {
                if (value == toggle.On)
                {
                    m_modPendingToggles.Remove(toggle.Key);
                }
                else
                {
                    m_modPendingToggles[toggle.Key] = value;
                }
            }
        }

        private void ChoosePreset(WorldModifierEditor.PresetInfo preset)
        {
            WorldModifierEditor.PresetTargets(preset, out Dictionary<WorldModifiers, int> sliders, out Dictionary<string, bool> toggles);
            m_modPendingSliders.Clear();
            m_modPendingToggles.Clear();
            foreach (WorldModifierEditor.SliderInfo slider in m_modSliders)
            {
                if (sliders.TryGetValue(slider.Modifier, out int index) && index != slider.Current)
                {
                    m_modPendingSliders[slider.Modifier] = index;
                }
            }
            foreach (WorldModifierEditor.ToggleInfo toggle in m_modToggles)
            {
                bool on = toggles.TryGetValue(toggle.Key, out bool value) && value;
                if (on != toggle.On)
                {
                    m_modPendingToggles[toggle.Key] = on;
                }
            }
            ShowMessage(Lang.F("Шаблон «{0}» обрано. Перевір зміни внизу й натисни «Застосувати».", preset.Name));
        }

        private void RefreshModifiers()
        {
            m_modUnavailable = WorldModifierEditor.Unavailable();
            if (m_modUnavailable != null)
            {
                m_modPendingSliders.Clear();
                m_modPendingToggles.Clear();
                return;
            }
            m_modIsHost = WorldModifierEditor.IsHost;
            if (Time.unscaledTime - m_modReadAt > 1f)
            {
                m_modReadAt = Time.unscaledTime;
                m_modSliders = WorldModifierEditor.Sliders();
                m_modToggles = WorldModifierEditor.Toggles();
                m_modPresets = WorldModifierEditor.Presets();
                m_modNonMenuKeys = WorldModifierEditor.NonMenuKeys();
            }
            m_modPendingText.Clear();
            foreach (WorldModifierEditor.SliderInfo slider in m_modSliders)
            {
                if (m_modPendingSliders.TryGetValue(slider.Modifier, out int index) && index >= 0 && index < slider.Options.Count && index != slider.Current)
                {
                    m_modPendingText.Add($"• {slider.Name}: {slider.Options[slider.Current].Label} → {slider.Options[index].Label}");
                }
            }
            foreach (WorldModifierEditor.ToggleInfo toggle in m_modToggles)
            {
                if (m_modPendingToggles.TryGetValue(toggle.Key, out bool on) && on != toggle.On)
                {
                    m_modPendingText.Add($"• {toggle.Name}: {(on ? Lang.T("увімкнути") : Lang.T("вимкнути"))}");
                }
            }
        }
    }
}
