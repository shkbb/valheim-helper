using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ValheimHelper
{
    internal sealed class BindAction
    {
        public string Id;
        // The menu section it belongs to, for the keys page.
        public string Section;
        public string Label;
        // null for one-shot actions
        public Func<bool> IsOn;
        // Returns the message shown to the player, or null.
        public Func<string> Run;
    }

    // A key with the modifiers held with it: "Ctrl+Shift+F9", or a side mouse button.
    internal struct KeyChord
    {
        public KeyCode Key;
        public bool Ctrl;
        public bool Shift;
        public bool Alt;

        public KeyChord(KeyCode key, bool ctrl = false, bool shift = false, bool alt = false)
        {
            Key = key;
            Ctrl = ctrl;
            Shift = shift;
            Alt = alt;
        }

        public bool IsNone => Key == KeyCode.None;

        public bool Same(KeyChord other)
        {
            return Key == other.Key && Ctrl == other.Ctrl && Shift == other.Shift && Alt == other.Alt;
        }

        // Stored form: enum names, so it always reads back.
        public string Save()
        {
            return (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "") + Key;
        }

        internal static bool TryParse(string text, out KeyChord chord)
        {
            chord = default(KeyChord);
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            string[] parts = text.Split('+');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].Trim())
                {
                    case "Ctrl": chord.Ctrl = true; break;
                    case "Shift": chord.Shift = true; break;
                    case "Alt": chord.Alt = true; break;
                    default: return false;
                }
            }
            if (!Enum.TryParse(parts[parts.Length - 1].Trim(), out KeyCode key))
            {
                return false;
            }
            chord.Key = key;
            return true;
        }

        // What the player reads: "Ctrl+1", "Num5", "Миша 4".
        public override string ToString()
        {
            return (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "") + KeyName(Key);
        }

        internal static string KeyName(KeyCode key)
        {
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
            {
                return ((int)(key - KeyCode.Alpha0)).ToString();
            }
            if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9)
            {
                return "Num" + (int)(key - KeyCode.Keypad0);
            }
            if (key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6)
            {
                return Lang.F("Миша {0}", (int)(key - KeyCode.Mouse0) + 1);
            }
            return key.ToString();
        }
    }

    // Keys for the mod's functions: every checkbox and button of the menu that makes sense without choosing something
    // first has an action here. A key is assigned on the keys page or with a right click on the control itself;
    // nothing is bound by default. Stored in ValheimHelper/binds.txt as "id=Ctrl+F9".
    internal static class Binds
    {
        internal static readonly List<BindAction> All = new List<BindAction>();
        internal static string CapturingId;

        private static readonly Dictionary<string, KeyChord> s_keys = new Dictionary<string, KeyChord>();
        private static readonly Dictionary<string, float> s_lastMultiplier = new Dictionary<string, float>();
        private static readonly List<string> s_pressed = new List<string>();
        private static string s_section = "";

        private static string FilePath => Path.Combine(Loader.Dir ?? ".", "binds.txt");

        static Binds()
        {
#if EDITION_FULL
            s_section = Lang.T("Персонаж");
            Toggle("god", Lang.T("Безсмертя"), () => State.GodMode, v => State.GodMode = v);
            Toggle("stamina", Lang.T("Нескінченна витривалість"), () => State.InfiniteStamina, v => State.InfiniteStamina = v);
            Toggle("eitr", Lang.T("Нескінченний ейтр"), () => State.InfiniteEitr, v => State.InfiniteEitr = v);
            Toggle("food", Lang.T("Їжа не закінчується"), () => State.FoodNeverExpires, v => State.FoodNeverExpires = v);
            Toggle("weight", Lang.T("Без ліміту ваги"), () => State.NoWeightLimit, v => State.NoWeightLimit = v);
            Toggle("durability", Lang.T("Речі не ламаються"), () => State.NoDurabilityLoss, Actions.SetNoDurabilityLoss);
            Command("repair", Lang.T("Полагодити всі речі"), () =>
            {
                int repaired = Actions.RepairAll();
                return repaired < 0 ? null : Lang.F("Полагоджено речей: {0}", repaired);
            });
            Command("heal", Lang.T("Повністю вилікувати"), () => Actions.Heal() ? Lang.T("Здоров'я відновлено") : null);
            Multiplier("damage", Lang.T("Множник урону"), () => State.DamageMultiplier, v => State.DamageMultiplier = v, 5f);
            Toggle("fly", Lang.T("Політ"), () => State.Fly, Actions.SetFly);
            Toggle("underwater", Lang.T("Ходити по дну під водою"), () => State.WalkUnderwater, v => State.WalkUnderwater = v);
            Multiplier("run", Lang.T("Швидкий біг"), () => State.RunSpeedMultiplier, v => State.RunSpeedMultiplier = v, 2f);
            Toggle("portals", Lang.T("Портали пропускають руду"), () => State.TeleportAnything, v => State.TeleportAnything = v);
            Command("skills100", Lang.T("Усі навички до 100"), Actions.AllSkillsTo100);
#endif

#if EDITION_FULL
            s_section = Lang.T("Друзі");
            Command("effectsclear", Lang.T("Зняти всі ефекти з себе"), () =>
            {
                List<PlayerTools.Target> targets = PlayerTools.Targets();
                return targets.Count > 0 ? PlayerTools.Remove(targets[0], null) : null;
            });
            Toggle("acceptgifts", Lang.T("Приймати речі, які надсилають друзі"), () => Gifts.AcceptGifts, v => Gifts.AcceptGifts = v);
#endif

#if EDITION_HELPER
            s_section = Lang.T("Предмети й крафт");
            StorageToggle("craftamount", Lang.T("Вибір кількості біля вікна крафту"), () => Storage.StorageSettings.CraftAmountPanel, v => Storage.StorageSettings.CraftAmountPanel = v);
            StorageToggle("craftchests", Lang.T("Матеріали зі скринь поруч, коли крафтиш"), () => Storage.StorageSettings.UseChestsForCrafting, v => Storage.StorageSettings.UseChestsForCrafting = v);
            StorageToggle("buildchests", Lang.T("Матеріали зі скринь поруч, коли будуєш"), () => Storage.StorageSettings.UseChestsForBuilding, v => Storage.StorageSettings.UseChestsForBuilding = v);
            Command("pickupall", Lang.T("Підібрати все з землі довкола"), () => Player.m_localPlayer != null ? Storage.RemotePickup.Start(Player.m_localPlayer, null) : null);
#endif

            s_section = Lang.T("Будівництво");
#if EDITION_FULL
            Toggle("freebuild", Lang.T("Будувати все без ресурсів"), () => State.FreeBuild, Actions.SetFreeBuild);
            Toggle("nosupport", Lang.T("Будівлі без опори"), () => State.NoSupportLimit, v => State.NoSupportLimit = v);
#endif
            Command("builderview", Lang.T("Будівельник: наступний режим перегляду (усе → каркас → … → усе)"), BuilderView.NextPreset);
            Command("bv_all", Lang.T("Режим перегляду: усе"), () => BuilderView.Preset(Lang.T("усе")));
            Command("bv_frame", Lang.T("Режим перегляду: лише каркас"), () => BuilderView.Preset(Lang.T("лише каркас"), BuilderView.Kind.Frame));
            Command("bv_floors", Lang.T("Режим перегляду: каркас і підлоги"), () => BuilderView.Preset(Lang.T("каркас і підлоги"), BuilderView.Kind.Frame, BuilderView.Kind.Floors));
            Command("bv_noroofs", Lang.T("Режим перегляду: без дахів"), () => BuilderView.Preset(Lang.T("без дахів"), BuilderView.Kind.Frame,
                BuilderView.Kind.Walls, BuilderView.Kind.Floors, BuilderView.Kind.Openings, BuilderView.Kind.Other));
            Command("bv_walls", Lang.T("Режим перегляду: лише стіни"), () => BuilderView.Preset(Lang.T("лише стіни"), BuilderView.Kind.Walls, BuilderView.Kind.Openings));
            for (int i = 0; i < BuilderView.KindNames.Length; i++)
            {
                BuilderView.Kind kind = (BuilderView.Kind)i;
                Toggle("bvk" + i, Lang.F("Показувати: {0}", BuilderView.KindNames[i]), () => BuilderView.Shown[(int)kind], v => BuilderView.Set(kind, v));
            }

#if EDITION_HELPER
            s_section = Lang.T("База");
            Command("stash", Lang.T("Скласти речі в скрині поруч"), Storage.StorageSorter.StartStash);
            Command("stashhighlight", Lang.T("Сховати підсвітку скринь"), () =>
            {
                Storage.StorageOverlay.ClearHighlight();
                return null;
            });
            StorageToggle("useempty", Lang.T("Розкладати по порожніх скринях те, чому немає місця"), () => Storage.StorageSettings.UseEmptyChests, v => Storage.StorageSettings.UseEmptyChests = v);
            StorageToggle("remember", Lang.T("Скрині запам'ятовують, що в них кладуть руками"), () => Storage.StorageSettings.RememberHandPlacement, v => Storage.StorageSettings.RememberHandPlacement = v);
            StorageToggle("chesttags", Lang.T("Показувати призначення, коли дивишся на скриню"), () => Storage.StorageSettings.ShowChestTags, v => Storage.StorageSettings.ShowChestTags = v);
            Command("storagecenter", Lang.T("Тут центр складу"), () =>
            {
                if (Player.m_localPlayer == null)
                {
                    return null;
                }
                Storage.RemoteStorage.SetBase(Player.m_localPlayer.transform.position);
                string message = Storage.RemoteStorage.RequestCatalog();
                return string.IsNullOrEmpty(message) ? Lang.T("Центр складу тут.") : message;
            });
            Command("keepitem", Lang.T("Не чіпати: предмет під курсором в інвентарі"), Storage.InventoryMarks.ToggleHovered);
            Command("furnaces", Lang.T("Завантажити печі поруч"), Storage.FurnaceLoader.Start);
            StorageToggle("smelteroutput", Lang.T("Готове з печей — одразу в скриню"), () => Storage.StorageSettings.SmelterToChests, v => Storage.StorageSettings.SmelterToChests = v);
            Command("fires", Lang.T("Заправити факели й вогнища поруч"), () => Storage.Fires.Fill(null));
            Toggle("lowfires", Lang.T("Підсвітити порожні факели й вогнища"), () => Storage.StorageOverlay.LowFiresActive, Storage.StorageOverlay.ShowLowFires);
#endif

            s_section = Lang.T("Світ");
#if EDITION_FULL
            Command("time06", Lang.T("Перемотати час світу до 06:00"), () => WorldTools.SkipWorldClockTo(6f));
            Command("time12", Lang.T("Перемотати час світу до 12:00"), () => WorldTools.SkipWorldClockTo(12f));
            Command("time18", Lang.T("Перемотати час світу до 18:00"), () => WorldTools.SkipWorldClockTo(18f));
            Command("time00", Lang.T("Перемотати час світу до 00:00"), () => WorldTools.SkipWorldClockTo(0f));
            Command("timereal", Lang.T("Повернути справжній час"), () =>
            {
                WorldTools.SetLocalTime(-1f);
                return Lang.T("Час доби знову йде як у світі.");
            });
            Multiplier("worldclock", Lang.T("Прискорення часу світу"), () => State.WorldClockMultiplier, v => State.WorldClockMultiplier = v, 10f);
            Multiplier("gamespeed", "Speedhack", () => State.GameSpeed, v => State.GameSpeed = v, 2f);
            Command("weather", Lang.T("Звичайна погода гри"), () =>
            {
                WorldTools.SetWeather("");
                return Lang.T("Погода: як задумано грою");
            });
            foreach (string[] weather in new[]
            {
                new[] { "Clear", Lang.T("Ясно") }, new[] { "Twilight_Clear", Lang.T("Ясна ніч") }, new[] { "LightRain", Lang.T("Легкий дощ") },
                new[] { "Rain", Lang.T("Дощ") }, new[] { "ThunderStorm", Lang.T("Гроза") }, new[] { "Misty", Lang.T("Туман") },
                new[] { "Snow", Lang.T("Сніг") }, new[] { "SnowStorm", Lang.T("Хуртовина") },
            })
            {
                string name = weather[0];
                string label = weather[1];
                Command("weather_" + name, Lang.F("Погода: {0}", label), () =>
                {
                    WorldTools.SetWeather(name);
                    return Lang.F("Погода: {0}", label);
                });
            }
            Multiplier("loot", Lang.T("Множник луту"), () => State.LootMultiplier, WorldTools.SetLootMultiplier, 2f);
            Toggle("sync", Lang.T("Синхронізація з другом (погода, час, лут)"), () => State.SyncWithFriends, v => State.SyncWithFriends = v);
            Command("despawn", Lang.T("Прибрати мобів, створених модом"), () => Lang.F("Прибрано мобів: {0}.", Spawner.RemoveSpawned()));
#endif
            Command("freecam", Lang.T("Вільна камера для знімків (увімкнути / вимкнути)"), FreeCamera.Toggle);
            Command("photomode", Lang.T("Фоторежим (увімкнути / вимкнути)"), PhotoMode.Toggle);
            Command("photoshot", Lang.T("Фоторежим: зробити знімок"), PhotoMode.Shot);
            Command("photofreeze", Lang.T("Фоторежим: застигнути в позі / відпустити"), () =>
            {
                if (!PhotoMode.Active)
                {
                    return Lang.T("Спершу увімкни фоторежим.");
                }
                PhotoMode.SetFrozen(!PhotoMode.Frozen);
                return PhotoMode.Frozen ? Lang.T("Вікінг застиг.") : Lang.T("Вікінг рухається.");
            });

            s_section = Lang.T("Карта");
            Toggle("mapoverlay", Lang.T("Карта світу на великій карті"), () => Map.MapSettings.Enabled, Map.MapSettings.SetEnabled);
            Toggle("unexplored", Lang.T("Показувати недосліджене на карті"), () => Map.MapSettings.ShowUnexplored, Map.MapSettings.SetShowUnexplored);
            MapToggle("maptamed", Lang.T("Приручені тварини на карті"), () => Map.MapSettings.ShowTamed, v => Map.MapSettings.ShowTamed = v);
            MapToggle("minimapicons", Lang.T("Значки на мінімапі"), () => Map.MapSettings.ShowOnMinimap, v => Map.MapSettings.ShowOnMinimap = v);
            MapToggle("sketchminimap", Lang.T("Малюнки на мінімапі"), () => Map.MapSettings.SketchOnMinimap, v => Map.MapSettings.SketchOnMinimap = v);
            MapToggle("hidedone", Lang.T("Ховати місця, позначені виконаними"), () => Map.MapSettings.HideDone, v => Map.MapSettings.HideDone = v);
            MapToggle("sharemarks", Lang.T("Друзі бачать мої мітки"), () => Map.MapSettings.WaypointsShared, v =>
            {
                Map.MapSettings.WaypointsShared = v;
                Map.Waypoints.SetMineShared(v);
            });
            MapToggle("sharepins", Lang.T("Друзі бачать мої значки гри на карті"), () => Map.MapSettings.ShareGamePins, v => Map.MapSettings.ShareGamePins = v);
            Command("waypoint", Lang.T("Мітка в світі там, куди дивишся"), Map.Waypoints.AddWhereLooking);
#if EDITION_FULL
            Toggle("mapteleport", Lang.T("Телепорт з карти (Ctrl + ПКМ)"), () => State.MapTeleport, v => State.MapTeleport = v);
            Command("exploreall", Lang.T("Відкрити всю карту (назавжди для цього персонажа)"), () =>
            {
                if (Minimap.instance == null)
                {
                    return null;
                }
                Minimap.instance.ExploreAll();
                return Lang.T("Карту відкрито.");
            });
#endif

            s_section = Lang.T("Налаштування");
            Command("imagereset", Lang.T("Зображення як у грі"), () =>
            {
                ImageTuning.Reset();
                return Lang.T("Зображення як у грі");
            });
            Toggle("perflayer", Lang.T("Шар оптимізації Valheim Helper"), () => Perf.PerfSettings.Enabled, v =>
            {
                Perf.PerfSettings.Enabled = v;
                Perf.PerfSettings.Changed();
            });

            s_section = Lang.T("Меню");
            string[] sections = { "Персонаж", "Друзі", "Предмети й крафт", "Будівництво", "База", "Світ", "Карта", "Довідник", "Налаштування" };
            for (int i = 0; i < sections.Length; i++)
            {
                int index = i;
                Command("open" + i, Lang.F("Відкрити меню на розділі «{0}»", Lang.T(sections[i])), () =>
                {
                    CheatMenu.OpenSection(index);
                    return null;
                });
            }
        }

        private static void Toggle(string id, string label, Func<bool> get, Action<bool> set)
        {
            All.Add(new BindAction
            {
                Id = id,
                Section = s_section,
                Label = label,
                IsOn = get,
                Run = () =>
                {
                    bool on = !get();
                    set(on);
                    return label + (on ? Lang.T(": увімкнено") : Lang.T(": вимкнено"));
                },
            });
        }

#if EDITION_HELPER
        private static void StorageToggle(string id, string label, Func<bool> get, Action<bool> set)
        {
            Toggle(id, label, get, v =>
            {
                set(v);
                Storage.StorageSettings.Changed();
            });
        }
#endif

        private static void MapToggle(string id, string label, Func<bool> get, Action<bool> set)
        {
            Toggle(id, label, get, v =>
            {
                set(v);
                Map.MapSettings.Changed();
            });
        }

        // Switches a multiplier between x1 and the last value used (or a sensible default).
        private static void Multiplier(string id, string label, Func<float> get, Action<float> set, float fallback)
        {
            All.Add(new BindAction
            {
                Id = id,
                Section = s_section,
                Label = label + Lang.T(" (×1 ↔ останнє значення)"),
                IsOn = () => get() != 1f,
                Run = () =>
                {
                    float current = get();
                    if (current != 1f)
                    {
                        s_lastMultiplier[id] = current;
                        set(1f);
                        return label + Lang.T(": вимкнено");
                    }
                    float value = s_lastMultiplier.TryGetValue(id, out float last) ? last : fallback;
                    set(value);
                    return $"{label}: ×{value:0.##}";
                },
            });
        }

        private static void Command(string id, string label, Func<string> run)
        {
            All.Add(new BindAction { Id = id, Section = s_section, Label = label, Run = run });
        }

        internal static BindAction Find(string id)
        {
            return All.Find(action => action.Id == id);
        }

        internal static bool TryGetKey(string id, out KeyChord chord)
        {
            return s_keys.TryGetValue(id, out chord) && !chord.IsNone;
        }

        internal static bool IsBound(string id)
        {
            return TryGetKey(id, out _);
        }

        // After a control's label: the key it has, or the prompt while one is being chosen for it.
        internal static string Hint(string id)
        {
            if (CapturingId == id)
            {
                return Lang.T("  <color=#ffd080>[натисни клавішу…]</color>");
            }
            return TryGetKey(id, out KeyChord chord) ? "  <color=#e8c88a>[" + chord + "]</color>" : "";
        }

        internal static string CapturePrompt
        {
            get
            {
                string name = IsMenuId(CapturingId) ? Lang.T("Відкрити / сховати меню мода") : Find(CapturingId)?.Label ?? CapturingId;
                return Lang.F("<color=#ffd080>Натисни клавішу для «{0}» (можна з Ctrl, Shift, Alt або бічну кнопку миші). Esc — скасувати, Backspace — прибрати клавішу.</color>", name);
            }
        }

        // ---------------------------------------------------------------- the menu's own keys

        internal const string MenuId = "menu";
        internal const string MenuAltId = "menualt";

        // Opens and hides the menu; F7 unless changed. Cannot be left without a key.
        internal static KeyCode MenuKey => s_keys.TryGetValue(MenuId, out KeyChord chord) && !chord.IsNone ? chord.Key : KeyCode.F7;

        // Second key for the same; Insert unless changed or removed (stored as None).
        internal static KeyCode MenuAltKey => s_keys.TryGetValue(MenuAltId, out KeyChord chord) ? chord.Key : KeyCode.Insert;

        internal static string MenuKeysText => MenuAltKey != KeyCode.None && MenuAltKey != MenuKey
            ? $"{KeyChord.KeyName(MenuKey)} / {KeyChord.KeyName(MenuAltKey)}" : KeyChord.KeyName(MenuKey);

        internal static bool IsMenuId(string id)
        {
            return id == MenuId || id == MenuAltId;
        }

        internal static bool MenuKeyPressed()
        {
            return MenuKeyDown(MenuKey) || (MenuAltKey != MenuKey && MenuKeyDown(MenuAltKey));
        }

        // A letter or digit opens the menu only while nothing is being typed; F-keys and the like work always.
        private static bool MenuKeyDown(KeyCode key)
        {
            return key != KeyCode.None && ZInput.GetKeyDown(key) && (!IsPrintable(key) || !IsTextFocused());
        }

        private static bool IsPrintable(KeyCode key)
        {
            return (key >= KeyCode.A && key <= KeyCode.Z) || (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
                || (key >= KeyCode.Keypad0 && key <= KeyCode.KeypadEquals) || (key >= KeyCode.Space && key <= KeyCode.BackQuote);
        }

        private static bool IsTextFocused()
        {
            return (Chat.instance != null && Chat.instance.HasFocus())
                || global::Console.IsVisible()
                || TextInput.IsVisible()
                || Minimap.InTextInput()
                || State.GuiTextFocus
                || Map.MapOverlay.SearchFocused
                || (Hud.instance != null && Hud.instance.m_buildUi != null && Hud.instance.m_buildUi.SearchFieldFocused);
        }

        internal static bool IsModifier(KeyCode key)
        {
            return key == KeyCode.LeftControl || key == KeyCode.RightControl || key == KeyCode.LeftShift || key == KeyCode.RightShift
                || key == KeyCode.LeftAlt || key == KeyCode.RightAlt || key == KeyCode.AltGr || key == KeyCode.LeftCommand || key == KeyCode.RightCommand;
        }

        // Returns a message: what was assigned, or why not.
        internal static string SetKey(string id, KeyChord chord)
        {
            if (chord.IsNone || chord.Key == KeyCode.Escape || chord.Key == KeyCode.Mouse0 || chord.Key == KeyCode.Mouse1)
            {
                return Lang.T("Esc закриває меню, обери іншу клавішу.");
            }
            if (IsMenuId(id))
            {
                // the menu opens with a plain key
                chord = new KeyChord(chord.Key);
            }
            else if (!chord.Ctrl && !chord.Shift && !chord.Alt && (chord.Key == MenuKey || chord.Key == MenuAltKey))
            {
                return Lang.F("Клавіша {0} відкриває меню мода. Обери іншу або зміни клавішу меню вгорі списку.", KeyChord.KeyName(chord.Key));
            }
            string moved = null;
            foreach (string other in new List<string>(s_keys.Keys))
            {
                if (other != id && s_keys[other].Same(chord))
                {
                    moved = Find(other)?.Label;
                    s_keys.Remove(other);
                }
            }
            s_keys[id] = chord;
            Save();
            string label = IsMenuId(id) ? Lang.T("Відкрити / сховати меню мода") : Find(id)?.Label ?? id;
            return moved != null
                ? Lang.F("«{0}» — {1}. Цю клавішу знято з «{2}».", label, chord, moved)
                : Lang.F("«{0}» — {1}.", label, chord);
        }

        internal static void Clear(string id)
        {
            if (id == MenuAltId)
            {
                // Removing the second menu key is remembered; otherwise Insert would come back.
                s_keys[MenuAltId] = new KeyChord(KeyCode.None);
                Save();
                return;
            }
            // For the main menu key this means "back to F7".
            if (s_keys.Remove(id))
            {
                Save();
            }
        }

        internal static void Load()
        {
            s_keys.Clear();
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
                    string id = line.Substring(0, separator).Trim();
                    if (KeyChord.TryParse(line.Substring(separator + 1).Trim(), out KeyChord chord) && (IsMenuId(id) || All.Exists(a => a.Id == id)))
                    {
                        s_keys[id] = chord;
                    }
                }
                Log.Info($"Key binds loaded: {s_keys.Count}");
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private static void Save()
        {
            try
            {
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, KeyChord> pair in s_keys)
                {
                    lines.Add(pair.Key + "=" + pair.Value.Save());
                }
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        internal static void Tick()
        {
            if (CapturingId != null || s_keys.Count == 0 || Player.m_localPlayer == null || IsTyping())
            {
                return;
            }
            bool ctrl = ZInput.GetKey(KeyCode.LeftControl) || ZInput.GetKey(KeyCode.RightControl);
            bool shift = ZInput.GetKey(KeyCode.LeftShift) || ZInput.GetKey(KeyCode.RightShift);
            bool alt = ZInput.GetKey(KeyCode.LeftAlt) || ZInput.GetKey(KeyCode.RightAlt) || ZInput.GetKey(KeyCode.AltGr);
            s_pressed.Clear();
            foreach (KeyValuePair<string, KeyChord> pair in s_keys)
            {
                KeyChord chord = pair.Value;
                // the modifiers must be exactly these: F9 and Ctrl+F9 are two different keys
                if (!IsMenuId(pair.Key) && !chord.IsNone && chord.Ctrl == ctrl && chord.Shift == shift && chord.Alt == alt && ZInput.GetKeyDown(chord.Key))
                {
                    s_pressed.Add(pair.Key);
                }
            }
            foreach (string id in s_pressed)
            {
                BindAction action = Find(id);
                string message = action?.Run();
                if (!string.IsNullOrEmpty(message) && Player.m_localPlayer != null)
                {
                    Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, message);
                }
            }
        }

        private static bool IsTyping()
        {
            return (Chat.instance != null && Chat.instance.HasFocus())
                || global::Console.IsVisible()
                || TextInput.IsVisible()
                || Minimap.InTextInput()
                || Menu.IsVisible()
                || State.GuiTextFocus
                || Map.MapOverlay.SearchFocused
                || (Hud.instance != null && Hud.instance.m_buildUi != null && Hud.instance.m_buildUi.SearchFieldFocused);
        }
    }

    internal sealed class BindRunner : MonoBehaviour
    {
        private bool m_loggedError;

        private void Update()
        {
            try
            {
                Binds.Tick();
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
    }
}
