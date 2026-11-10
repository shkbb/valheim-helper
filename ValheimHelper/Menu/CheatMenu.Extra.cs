using UnityEngine;

namespace ValheimHelper
{
    // The map, key bindings and wiki pages.
    internal sealed partial class CheatMenu
    {
        // "Карта": switching the map overlay on, and what acts on the map itself; everything else is in the panel on the map.
        private void DrawMapTab()
        {
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Карта світу"), HelperSkin.Header);
            bool enabled = GUILayout.Toggle(Map.MapSettings.Enabled, Lang.T(" Карта світу: панель і значки на великій карті (M) та на мінімапі") + Binds.Hint("mapoverlay"));
            Bindable("mapoverlay");
            if (enabled != Map.MapSettings.Enabled)
            {
                Map.MapSettings.SetEnabled(enabled);
            }
            GUILayout.Label(Lang.T("<color=#c0c0c0>Відкрий велику карту (M): праворуч панель «Карта світу» з вкладками «Шари» (що показувати: місця, ресурси всього світу, " +
                "тварини), «Пошук», «Мітки», «Малюнки» й «Вигляд». Усі налаштування карти — там, на самій карті.\n" +
                "Місця беруться прямо з гри, тож вони точні для твого світу. Повний список місць знає лише хост: другові значки прийдуть, " +
                "якщо в хоста теж стоїть Valheim Helper. Мод лише читає дані світу: нічого не записує в сейв і не ставить позначок читів.</color>"));
            GUILayout.EndVertical();

            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Дії з картою"), HelperSkin.Header);
            State.MapTeleport = GUILayout.Toggle(State.MapTeleport, Lang.T(" Телепорт з карти: відкрий карту, Ctrl + права кнопка по точці") + Binds.Hint("mapteleport"));
            Bindable("mapteleport");
            if (Minimap.instance != null && Bindable(ConfirmButton("explore", Lang.T("Відкрити всю карту (назавжди для цього персонажа)") + Binds.Hint("exploreall")), "exploreall"))
            {
                Minimap.instance.ExploreAll();
                ShowMessage(Lang.T("Карту відкрито."));
            }
            GUILayout.EndVertical();
        }

        private Wiki.WikiView m_wiki;

        // "Довідник": the wiki in the window, filling the page area and following the window size.
        private void DrawWikiTab()
        {
            if (m_wiki == null)
            {
                m_wiki = new Wiki.WikiView(ShowMessage);
            }
            Rect area = GUILayoutUtility.GetRect(10f, 10f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            m_wiki.Draw(area);
        }

        private string m_bindSearch = "";
        private bool m_bindOnlyAssigned;

        // "Клавіші": the menu keys, then every function by the menu section it is in, with a search.
        private void DrawBindsTab()
        {
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Клавіші"), HelperSkin.Header);
            GUILayout.Label(Lang.T("<color=#c0c0c0>Клавішу можна призначити будь-якій функції — тут або прямо на її сторінці: клацни правою кнопкою миші " +
                "по галочці чи кнопці й натисни клавішу. Можна з Ctrl, Shift, Alt, а також бічні кнопки миші. Призначену клавішу видно сірим біля назви. " +
                "Обирай ті, якими гра не користується (F9–F12, цифровий блок, Ctrl + цифри). Поки ти пишеш у чаті чи полі пошуку, клавіші не спрацьовують.</color>"));
            MenuKeyRow(Binds.MenuId, Lang.T("Відкрити / сховати меню мода"), Binds.MenuKey, Lang.T("Типова"), Lang.T("Повернути F7"));
            MenuKeyRow(Binds.MenuAltId, Lang.T("Друга клавіша для меню"), Binds.MenuAltKey, "✕", Lang.T("Прибрати другу клавішу"));
            GUILayout.EndVertical();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Пошук:"), GUILayout.Width(60f));
            m_bindSearch = GUILayout.TextField(m_bindSearch ?? "");
            m_bindOnlyAssigned = GUILayout.Toggle(m_bindOnlyAssigned, Lang.T(" Лише призначені"), GUILayout.Width(170f));
            GUILayout.EndHorizontal();
            string search = (m_bindSearch ?? "").Trim().ToLowerInvariant();
            string section = null;
            bool any = false;
            foreach (BindAction action in Binds.All)
            {
                bool assigned = Binds.TryGetKey(action.Id, out KeyChord chord);
                if ((m_bindOnlyAssigned && !assigned) || (search.Length > 0 && !action.Label.ToLowerInvariant().Contains(search)
                    && !action.Section.ToLowerInvariant().Contains(search)))
                {
                    continue;
                }
                any = true;
                if (action.Section != section)
                {
                    section = action.Section;
                    GUILayout.Space(4f);
                    GUILayout.Label("<b><color=#ffcc6a>" + section + "</color></b>");
                }
                GUILayout.BeginHorizontal();
                string state = action.IsOn == null ? "" : action.IsOn() ? Lang.T("  <color=#80ff80>увімк.</color>") : Lang.T("  <color=#909090>вимк.</color>");
                GUILayout.Label(action.Label + state, GUILayout.MinWidth(260f));
                string key = Binds.CapturingId == action.Id ? Lang.T("<color=#ffd080>натисни клавішу…</color>")
                    : assigned ? "<b>" + chord + "</b>" : "<color=#909090>—</color>";
                GUILayout.Label(key, GUILayout.Width(140f));
                if (GUILayout.Button(Lang.T("Призначити"), GUILayout.MinWidth(120f)))
                {
                    Binds.CapturingId = action.Id;
                }
                GUI.enabled = assigned;
                if (GUILayout.Button("✕", GUILayout.Width(34f)))
                {
                    Binds.Clear(action.Id);
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            if (!any)
            {
                GUILayout.Label(Lang.T("<color=#909090>Нічого не знайдено.</color>"));
            }
        }

        private void MenuKeyRow(string id, string label, KeyCode current, string resetLabel, string resetMessage)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>" + label + "</b>", GUILayout.MinWidth(260f));
            string key = Binds.CapturingId == id ? Lang.T("<color=#ffd080>натисни клавішу…</color>")
                : current == KeyCode.None ? "<color=#909090>—</color>" : "<b>" + KeyChord.KeyName(current) + "</b>";
            GUILayout.Label(key, GUILayout.Width(140f));
            if (GUILayout.Button(Lang.T("Призначити"), GUILayout.MinWidth(120f)))
            {
                Binds.CapturingId = id;
            }
            if (GUILayout.Button(resetLabel, GUILayout.Width(id == Binds.MenuId ? 70f : 34f)))
            {
                Binds.Clear(id);
                ShowMessage(resetMessage + ".");
            }
            GUILayout.EndHorizontal();
        }

        // A right click on a control of the menu starts choosing a key for its function; the key it has shows after
        // its label (Binds.Hint). Called right after the control is drawn.
        private static bool Bindable(bool clicked, string id)
        {
            Bindable(id);
            return clicked;
        }

        // The same for controls drawn outside the menu (the map's panel).
        internal static void BindableControl(string id)
        {
            Bindable(id);
        }

        private static void Bindable(string id)
        {
            Event current = Event.current;
            if (current.type == EventType.MouseDown && current.button == 1 && GUILayoutUtility.GetLastRect().Contains(current.mousePosition))
            {
                Binds.CapturingId = id;
                current.Use();
            }
        }

        // Runs at the start of OnGUI: the next key (with the modifiers held) pressed while waiting becomes the bind.
        private void HandleBindCapture()
        {
            if (Binds.CapturingId == null)
            {
                return;
            }
            if (!State.MenuOpen)
            {
                Binds.CapturingId = null;
                return;
            }
            Event current = Event.current;
            KeyCode key = KeyCode.None;
            if (current.type == EventType.KeyDown)
            {
                key = current.keyCode;
                if (key == KeyCode.None || Binds.IsModifier(key))
                {
                    // Ctrl, Shift and Alt alone wait for the key pressed with them
                    return;
                }
                if (key == KeyCode.Escape)
                {
                    Binds.CapturingId = null;
                    current.Use();
                    return;
                }
                if (key == KeyCode.Backspace || key == KeyCode.Delete)
                {
                    Binds.Clear(Binds.CapturingId);
                    ShowMessage(Lang.T("Клавішу прибрано."));
                    Binds.CapturingId = null;
                    current.Use();
                    return;
                }
            }
            else if (current.type == EventType.MouseDown && current.button >= 2 && current.button <= 6)
            {
                key = KeyCode.Mouse0 + current.button;
            }
            if (key == KeyCode.None)
            {
                return;
            }
            ShowMessage(Binds.SetKey(Binds.CapturingId, new KeyChord(key, current.control, current.shift, current.alt)));
            Binds.CapturingId = null;
            current.Use();
        }
    }
}
