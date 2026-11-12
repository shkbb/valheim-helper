using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper
{
    internal sealed partial class CheatMenu : MonoBehaviour
    {
        private const int WindowId = 0x56484C50;
        private const float BaseScreenHeight = 1080f;
        private const int MaxListedItems = 150;
        // Inventory.Load marks any item above this damage as cheated, so such items are never offered.
        private const float MaxSafeItemDamage = 10000f;

        private const float SidebarWidth = 200f;

        // One page of a section: a name for the section's page bar and what it draws.
        private sealed class MenuPage
        {
            public readonly string Name;
            public readonly Action Draw;
            public readonly string Id;
            // Takes the whole page area and scrolls by itself (the wiki), instead of the page's scroll view.
            public readonly bool FullSize;

            public MenuPage(string name, Action draw, string id = null, bool fullSize = false)
            {
                Name = name;
                Draw = draw;
                Id = id;
                FullSize = fullSize;
            }
        }

        // A section of the menu: the column on the left lists them, its pages share one bar on top.
        private sealed class MenuSection
        {
            public readonly string Name;
            public readonly MenuPage[] Pages;
            public readonly string[] PageNames;

            public MenuSection(string name, params MenuPage[] pages)
            {
                Name = name;
                Pages = pages;
                PageNames = Array.ConvertAll(pages, page => page.Name);
            }
        }

        // The old tabs (0..14, as saved by earlier versions) → section and page of the new menu.
        private static readonly int[,] s_legacyTabs =
        {
            { 0, 0 }, { 0, 1 }, { 3, 0 }, { 2, 0 }, { 0, 2 }, { 8, 1 }, { 5, 0 }, { 6, 0 }, { 8, 0 }, { 5, 2 }, { 4, 0 }, { 4, 3 }, { 5, 1 }, { 8, 3 }, { 1, 0 },
        };
        private static readonly string[] s_itemFilters = { Lang.T("Усе"), Lang.T("Матеріали"), Lang.T("Їжа/зілля"), Lang.T("Зброя"), Lang.T("Броня"), Lang.T("Інше") };
        private static readonly string[] s_qualities = { "1", "2", "3", "4" };

        private static readonly MethodInfo s_screenToWorldPoint = AccessTools.Method(typeof(Minimap), "ScreenToWorldPoint");
        private static readonly MethodInfo s_debugTeleport = AccessTools.Method(typeof(Minimap), "DebugTeleport");
        private static readonly FieldInfo s_allSkills = AccessTools.Field(typeof(Skills), "s_allSkills");

        private sealed class ItemEntry
        {
            public string Prefab;
            public string DisplayName;
            public string SearchKey;
            public int Filter;
            public int MaxQuality;
            public bool IsEquipment;
            public Texture2D IconTexture;
            public Rect IconUv;
            // Untranslated or test/unused content left in the game data.
            public bool IsDev;
        }

        private readonly HashSet<string> m_loggedErrors = new HashSet<string>();

        private GUISkin m_skin;
        private Rect m_window = new Rect(60f, 60f, 880f, 720f);
        private MenuSection[] m_sections;
        private int m_section;
        private int m_page;
        // The page last open in every section, so coming back to a section shows it again.
        private int[] m_sectionPages;
        private bool m_windowRestored;
        // A section or page click is applied on the next Layout event so the controls of one frame stay consistent.
        private int m_pendingSection = -1;
        private int m_pendingPage = -1;
        private Vector2 m_scroll;
        private string m_message = "";
        private string m_bottomLine;
        private float m_messageUntil;
        private string m_confirmId;
        private float m_confirmUntil;

        private List<ItemEntry> m_items;
        private readonly List<ItemEntry> m_filteredItems = new List<ItemEntry>();
        private string m_search = "";
        private string m_filteredSearch;
        private int m_itemFilter;
        private int m_filteredFilter = -1;
        private bool m_showDevItems;
        private bool m_filteredShowDev;
        private string m_amountText = "1";
        private int m_quality = 1;

        private SaveCleaner.CharacterStatus m_characterStatus;
        private SaveCleaner.WorldStatus m_worldStatus;
        private bool m_removeCheatedWorldKeys = true;
        private float m_nextStatusRefresh;

        private static CheatMenu s_instance;

        // Opens the menu on a section (a key can be bound to it).
        internal static void OpenSection(int index)
        {
            State.MenuOpen = true;
            if (s_instance != null && index >= 0 && index < s_instance.Sections.Length)
            {
                s_instance.m_pendingSection = index;
            }
        }

        private void Awake()
        {
            s_instance = this;
        }

        private void Update()
        {
            try
            {
                bool escapeHandled = Patches.EscapeClosesMenu.TryHandle();
                if (!escapeHandled && Binds.CapturingId == null && Binds.MenuKeyPressed())
                {
                    State.MenuOpen = !State.MenuOpen;
                }

                Player player = Player.m_localPlayer;
                if (player == null)
                {
                    return;
                }
#if EDITION_FULL
                if (State.InfiniteStamina)
                {
                    player.AddStamina(player.GetMaxStamina());
                }
                if (State.InfiniteEitr)
                {
                    player.AddEitr(player.GetMaxEitr());
                }
                if (State.MapTeleport && Minimap.IsOpen() && ZInput.GetKeyDown(KeyCode.Mouse1)
                    && (ZInput.GetKey(KeyCode.LeftControl) || ZInput.GetKey(KeyCode.RightControl)))
                {
                    TeleportToMapCursor();
                }
#endif
                if (State.MenuOpen && CurrentPage.Id == "achievements" && Time.unscaledTime >= m_nextStatusRefresh)
                {
                    m_nextStatusRefresh = Time.unscaledTime + 1f;
                    m_characterStatus = SaveCleaner.ScanCharacter();
                }
            }
            catch (Exception e)
            {
                LogOnce(e);
            }
        }

        private void OnGUI()
        {
            HandleBindCapture();
            if (!State.MenuOpen)
            {
                if (State.GuiTextFocus && !Map.MapOverlay.SearchFocused)
                {
                    // Closing the menu drops the text field focus, otherwise it would come back on reopen.
                    GUIUtility.keyboardControl = 0;
                }
                State.GuiTextFocus = false;
                return;
            }
            EnsureSkin();
            GUI.skin = m_skin;
            // Above everything else the mod draws (the map's markers and panel): lower depth is drawn on top.
            GUI.depth = -10;
            float scale = Mathf.Max(1f, Screen.height / BaseScreenHeight);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            if (!m_windowRestored)
            {
                // The saved position is kept on screen even if the resolution got smaller since.
                m_windowRestored = true;
                m_window.x = Mathf.Clamp(State.MenuX, 0f, Mathf.Max(0f, Screen.width / scale - 200f));
                m_window.y = Mathf.Clamp(State.MenuY, 0f, Mathf.Max(0f, Screen.height / scale - 120f));
                m_window.width = Mathf.Clamp(State.MenuW, MinWindowWidth, Mathf.Max(MinWindowWidth, Screen.width / scale));
                m_window.height = Mathf.Clamp(State.MenuH, MinWindowHeight, Mathf.Max(MinWindowHeight, Screen.height / scale));
                RestoreSection();
            }
            m_window = GUILayout.Window(WindowId, m_window, DrawWindow, Lang.F("Valheim Helper   ({0} / Esc — сховати)", Binds.MenuKeysText),
                GUILayout.Width(m_window.width), GUILayout.Height(m_window.height));
            if (m_resizeTo.x > 0f)
            {
                m_window.width = m_resizeTo.x;
                m_window.height = m_resizeTo.y;
                if (GUIUtility.hotControl == 0)
                {
                    m_resizeTo = Vector2.zero;
                }
            }
            State.MenuX = m_window.x;
            State.MenuY = m_window.y;
            State.MenuW = m_window.width;
            State.MenuH = m_window.height;
            State.MenuSection = m_section;
            State.MenuPage = m_page;
            State.MenuScreenRect = new Rect(m_window.x * scale, m_window.y * scale, m_window.width * scale, m_window.height * scale);
            State.GuiTextFocus = GUIUtility.keyboardControl != 0;
        }

        // Shared themed skin: the game's own serif font and drawn wood/amber controls (see HelperSkin).
        private void EnsureSkin()
        {
            if (m_skin == null)
            {
                m_skin = HelperSkin.Get();
            }
        }

        private MenuSection[] Sections => m_sections ?? (m_sections = BuildSections());

        private MenuPage CurrentPage => Sections[m_section].Pages[m_page];

        // The whole menu in one place: sections in the order they are listed, pages in the order of their bar.
        private MenuSection[] BuildSections()
        {
            return new[]
            {
#if EDITION_FULL
                new MenuSection(Lang.T("Персонаж"),
                    new MenuPage(Lang.T("Стан"), DrawPlayerTab),
                    new MenuPage(Lang.T("Рух"), () => { DrawMovementTab(); DrawSpeedSection(); }),
                    new MenuPage(Lang.T("Навички"), DrawSkillsTab),
                    new MenuPage(Lang.T("Статистика"), DrawStatsTab)),
                new MenuSection(Lang.T("Друзі"),
                    new MenuPage(Lang.T("Гравці"), DrawPlayersTab)),
                new MenuSection(Lang.T("Предмети й крафт"),
                    new MenuPage(Lang.T("Видача"), DrawItemsTab),
                    new MenuPage(Lang.T("Крафт"), () => DrawStoragePage(DrawCraftAmountSection, DrawCraftSection)),
                    new MenuPage(Lang.T("Підібрати"), DrawPickupTab)),
#else
                new MenuSection(Lang.T("Персонаж"),
                    new MenuPage(Lang.T("Статистика"), DrawStatsTab)),
#endif
#if EDITION_HELPER && !EDITION_FULL
                new MenuSection(Lang.T("Предмети й крафт"),
                    new MenuPage(Lang.T("Крафт"), () => DrawStoragePage(DrawCraftAmountSection, DrawCraftSection)),
                    new MenuPage(Lang.T("Підібрати"), DrawPickupTab)),
#endif
                new MenuSection(Lang.T("Будівництво"),
                    new MenuPage(Lang.T("Будівництво"), DrawBuildTab)),
#if EDITION_HELPER
                new MenuSection(Lang.T("База"),
                    new MenuPage(Lang.T("Склад"), () => DrawStoragePage(DrawStashSection, DrawStockSection, DrawTagSection)),
                    new MenuPage(Lang.T("Не чіпати"), () => DrawStoragePage(DrawKeepSection)),
                    new MenuPage(Lang.T("Печі"), () => DrawStoragePage(DrawFurnaceSection, DrawSmelterOutputSection)),
                    new MenuPage(Lang.T("Факели"), DrawFiresTab),
                    new MenuPage(Lang.T("Тварини"), DrawAnimalsTab)),
#endif
#if EDITION_FULL
                new MenuSection(Lang.T("Світ"),
                    new MenuPage(Lang.T("Час і погода"), DrawWorldTab),
                    new MenuPage(Lang.T("Змінники"), DrawModifiersTab),
                    new MenuPage(Lang.T("Моби"), DrawMobsTab)),
#elif EDITION_HELPER
                new MenuSection(Lang.T("Світ"),
                    new MenuPage(Lang.T("Змінники"), DrawModifiersTab)),
#endif
                new MenuSection(Lang.T("Камера"),
                    new MenuPage(Lang.T("Камера"), DrawCameraTab)),
                new MenuSection(Lang.T("Карта"),
                    new MenuPage(Lang.T("Карта"), DrawMapTab)),
                new MenuSection(Lang.T("Довідник"),
                    new MenuPage(Lang.T("Довідник"), DrawWikiTab, "wiki", fullSize: true)),
                new MenuSection(Lang.T("Налаштування"),
                    new MenuPage(Lang.T("Клавіші"), DrawBindsTab),
                    new MenuPage(Lang.T("Досягнення"), DrawAchievementsTab, "achievements"),
                    new MenuPage(Lang.T("Зображення"), DrawImageTab),
                    new MenuPage(Lang.T("Швидкодія"), DrawPerfTab),
                    new MenuPage(Lang.T("Мова"), DrawLanguageTab)
#if EDITION_FULL
                    , new MenuPage(Lang.T("Скидання"), DrawResetSettings)
#endif
                    ),
            };
        }

        // The section and page open last time; settings saved by an older version remember a tab number instead.
        private void RestoreSection()
        {
            MenuSection[] sections = Sections;
            m_sectionPages = new int[sections.Length];
            int section = State.MenuSection;
            int page = State.MenuPage;
            if (section < 0 && State.MenuTab >= 0 && State.MenuTab < s_legacyTabs.GetLength(0))
            {
                section = s_legacyTabs[State.MenuTab, 0];
                page = s_legacyTabs[State.MenuTab, 1];
            }
            m_section = Mathf.Clamp(section, 0, sections.Length - 1);
            m_page = Mathf.Clamp(page, 0, sections[m_section].Pages.Length - 1);
            m_sectionPages[m_section] = m_page;
        }

        private const float MinWindowWidth = 700f;
        private const float MinWindowHeight = 420f;
        private const float GripSize = 22f;
        private Vector2 m_resizeOffset;
        private Vector2 m_resizeTo;

        // The bottom-right corner of the window is a grip: dragging it resizes the window, and the size is saved.
        private void ResizeWindow()
        {
            Rect grip = new Rect(m_window.width - GripSize, m_window.height - GripSize, GripSize, GripSize);
            int id = GUIUtility.GetControlID(FocusType.Passive);
            Event current = Event.current;
            switch (current.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (current.button == 0 && grip.Contains(current.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        m_resizeOffset = new Vector2(m_window.width - current.mousePosition.x, m_window.height - current.mousePosition.y);
                        current.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        float scale = Mathf.Max(1f, Screen.height / BaseScreenHeight);
                        // Applied after GUILayout.Window returns, which would otherwise give back the old size.
                        m_resizeTo = new Vector2(
                            Mathf.Clamp(current.mousePosition.x + m_resizeOffset.x, MinWindowWidth, Mathf.Max(MinWindowWidth, Screen.width / scale - m_window.x)),
                            Mathf.Clamp(current.mousePosition.y + m_resizeOffset.y, MinWindowHeight, Mathf.Max(MinWindowHeight, Screen.height / scale - m_window.y)));
                        current.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        current.Use();
                    }
                    break;
                case EventType.Repaint:
                    GUI.color = grip.Contains(current.mousePosition) || GUIUtility.hotControl == id ? new Color(1f, 0.8f, 0.4f, 1f) : new Color(1f, 1f, 1f, 0.45f);
                    for (int i = 0; i < 3; i++)
                    {
                        float step = 5f * i;
                        GUI.DrawTexture(new Rect(m_window.width - 6f - step, m_window.height - 6f, 3f, 3f), HelperSkin.White);
                        GUI.DrawTexture(new Rect(m_window.width - 6f, m_window.height - 6f - step, 3f, 3f), HelperSkin.White);
                    }
                    GUI.DrawTexture(new Rect(m_window.width - 11f, m_window.height - 11f, 3f, 3f), HelperSkin.White);
                    GUI.color = Color.white;
                    break;
            }
        }

        private void DrawWindow(int id)
        {
            ResizeWindow();
            // A click anywhere in the window drops text field focus; a click on a field focuses it again.
            if (Event.current.type == EventType.MouseDown)
            {
                GUIUtility.keyboardControl = 0;
            }
            MenuSection[] sections = Sections;
            if (m_sectionPages == null)
            {
                m_sectionPages = new int[sections.Length];
            }
            if ((m_pendingSection >= 0 || m_pendingPage >= 0) && Event.current.type == EventType.Layout)
            {
                if (m_pendingSection >= 0)
                {
                    m_section = m_pendingSection;
                    m_page = m_sectionPages[m_section];
                }
                if (m_pendingPage >= 0)
                {
                    m_page = m_pendingPage;
                }
                m_sectionPages[m_section] = m_page;
                m_pendingSection = -1;
                m_pendingPage = -1;
                m_scroll = Vector2.zero;
                Binds.CapturingId = null;
            }

            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(SidebarWidth));
            string[] names = Array.ConvertAll(sections, section => section.Name);
            int picked = GUILayout.SelectionGrid(m_section, names, 1, GUILayout.Width(SidebarWidth));
            if (picked != m_section)
            {
                m_pendingSection = picked;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();

            GUILayout.BeginVertical();
            MenuSection current = sections[m_section];
            if (current.Pages.Length > 1)
            {
                int page = GUILayout.Toolbar(m_page, current.PageNames);
                if (page != m_page)
                {
                    m_pendingPage = page;
                }
            }
            MenuPage shown = current.Pages[m_page];
            if (!shown.FullSize)
            {
                m_scroll = GUILayout.BeginScrollView(m_scroll);
            }
            try
            {
                shown.Draw();
            }
            catch (Exception e)
            {
                LogOnce(e);
                GUILayout.Label(Lang.T("<color=#ff6060>Помилка, деталі у ValheimHelper.log</color>"));
            }
            if (!shown.FullSize)
            {
                GUILayout.EndScrollView();
            }

            if (Event.current.type == EventType.Layout)
            {
                m_bottomLine = Binds.CapturingId != null ? Binds.CapturePrompt : Time.unscaledTime < m_messageUntil ? m_message : null;
            }
            if (m_bottomLine != null)
            {
                GUILayout.Label(m_bottomLine);
            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }

#if EDITION_FULL
        private void DrawPlayerTab()
        {
            State.GodMode = GUILayout.Toggle(State.GodMode, Lang.T(" Безсмертя (моби, падіння, холод, лава, отрута)") + Binds.Hint("god"));
            Bindable("god");
            State.InfiniteStamina = GUILayout.Toggle(State.InfiniteStamina, Lang.T(" Нескінченна витривалість") + Binds.Hint("stamina"));
            Bindable("stamina");
            State.InfiniteEitr = GUILayout.Toggle(State.InfiniteEitr, Lang.T(" Нескінченний ейтр") + Binds.Hint("eitr"));
            Bindable("eitr");
            State.FoodNeverExpires = GUILayout.Toggle(State.FoodNeverExpires, Lang.T(" Їжа не закінчується") + Binds.Hint("food"));
            Bindable("food");
            State.NoWeightLimit = GUILayout.Toggle(State.NoWeightLimit, Lang.T(" Без ліміту ваги") + Binds.Hint("weight"));
            Bindable("weight");
            bool noDurability = GUILayout.Toggle(State.NoDurabilityLoss, Lang.T(" Речі не ламаються (зброя, інструменти, броня, щити, факели)") + Binds.Hint("durability"));
            Bindable("durability");
            if (noDurability != State.NoDurabilityLoss)
            {
                Actions.SetNoDurabilityLoss(noDurability);
            }

            Player player = Player.m_localPlayer;
            GUILayout.Space(8f);
            State.DamageMultiplier = MultiplierSlider(Lang.T("Урон"), State.DamageMultiplier, 1f, 50f, 1f, "damage");
            ItemDrop.ItemData weapon = player != null ? player.GetCurrentWeapon() : null;
            string weaponCap = weapon != null
                ? Lang.F(" Для зброї в руках — до ~{0:0} за удар.", Patches.DamageMultiplier.MaxSafeHit(weapon.m_shared.m_backstabBonus))
                : "";
            GUILayout.Label(Lang.T("<color=#c0c0c0>Діє на удари зброєю, стріли й заклинання. Один удар обмежено так, щоб навіть з ударом у спину,\n" +
                "оглушенням і вразливістю моба лишитись нижче за поріг гри 99 999, після якого моб стає «читерським».") + weaponCap + "</color>");
            GUILayout.Space(8f);
            if (player != null)
            {
                GUILayout.BeginHorizontal();
                if (Bindable(GUILayout.Button(Lang.T("Повністю вилікувати") + Binds.Hint("heal")), "heal"))
                {
                    Actions.Heal();
                }
                if (Bindable(GUILayout.Button(Lang.T("Полагодити всі речі") + Binds.Hint("repair")), "repair"))
                {
                    int repaired = Actions.RepairAll();
                    ShowMessage(repaired > 0 ? Lang.F("Полагоджено речей: {0}.", repaired) : Lang.T("Усі речі й так цілі."));
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(8f);
            }
        }
#endif

        // "Мова": the language of the menu (the articles of the guide have their own switch there).
        private void DrawLanguageTab()
        {
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Мова меню"), HelperSkin.Header);
            string[] options = new string[Lang.Codes.Length + 1];
            options[0] = Lang.T("Як мова гри");
            System.Array.Copy(Lang.Names, 0, options, 1, Lang.Names.Length);
            int current = System.Array.IndexOf(Lang.Codes, Lang.Chosen) + 1;
            int chosen = GUILayout.SelectionGrid(current, options, 5, GUILayout.Height(68f));
            if (chosen != current)
            {
                Lang.Chosen = chosen == 0 ? "" : Lang.Codes[chosen - 1];
            }
            string game = Localization.instance != null ? Localization.instance.GetSelectedLanguage() : "English";
            string next = Lang.Resolve(Lang.Chosen, game);
            if (next != Lang.Code)
            {
                GUILayout.Label(Lang.T("<color=#ffd080>Мова меню зміниться після перезапуску гри.</color>"));
            }
            GUILayout.Label(Lang.T("<color=#c0c0c0>«Як мова гри» — мова гри, якщо мод її знає, інакше англійська. " +
                "Мову статей довідника перемикає окрема кнопка вгорі праворуч у «Довідник».</color>"));
            GUILayout.EndVertical();
        }

#if EDITION_FULL
        private void DrawResetSettings()
        {
            GUILayout.Label(Lang.T("<color=#c0c0c0>Усі галочки й повзунки меню зберігаються між запусками гри.</color>"));
            if (ConfirmButton("resetall", Lang.T("Вимкнути все й повернути множники до ×1")))
            {
                Settings.ResetAll();
                ShowMessage(Lang.T("Усі функції вимкнено, множники ×1, погода й час як у грі."));
            }
        }
#endif

#if EDITION_FULL
        private void DrawMovementTab()
        {
            bool fly = GUILayout.Toggle(State.Fly, Lang.T(" Політ (Пробіл — вгору, Ctrl — вниз, Shift — швидше)") + Binds.Hint("fly"));
            Bindable("fly");
            if (fly != State.Fly)
            {
                Actions.SetFly(fly);
            }
            State.TeleportAnything = GUILayout.Toggle(State.TeleportAnything, Lang.T(" Портали пропускають руду і метали") + Binds.Hint("portals"));
            Bindable("portals");
        }
#endif

        private void DrawBuildTab()
        {
#if EDITION_FULL
            bool freeBuild = GUILayout.Toggle(State.FreeBuild, Lang.T(" Будувати все без ресурсів і без верстака поруч") + Binds.Hint("freebuild"));
            Bindable("freebuild");
            if (freeBuild != State.FreeBuild)
            {
                Actions.SetFreeBuild(freeBuild);
            }
            State.NoSupportLimit = GUILayout.Toggle(State.NoSupportLimit, Lang.T(" Будівлі не руйнуються без опори") + Binds.Hint("nosupport"));
            Bindable("nosupport");
            GUILayout.Label(Lang.T("<color=#c0c0c0>Поки режим увімкнено, у меню молота відкриті всі будівлі, як у debugmode,\n" +
                "а розбирати будівлі можна без станції поруч. Після вимкнення лишаються тільки вивчені будівлі.\n" +
                "Якщо вимкнути «без опори», непідперті конструкції одразу впадуть.\n" +
                "Коли будуєте разом, вмикайте це обом: руйнування рахує гра того, хто «володіє» ділянкою.</color>"));
#endif
            DrawBuilderViewSection();
        }

        // "Зображення": brightness, gamma and glow on top of the game's picture.
        private void DrawImageTab()
        {
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Зображення"), HelperSkin.Header);
            GUILayout.Label(Lang.T("<color=#c0c0c0>Налаштування, яких немає в самій грі. Змінюється лише те, як картинка малюється у тебе; " +
                "на гру, друзів і досягнення не впливає. «Сяйво» — ореол довкола вогню, лави й магії: якщо ефекти сліплять, зменш його.</color>"));
            State.ImageBrightness = ImageSlider(Lang.T("Яскравість"), State.ImageBrightness, ImageTuning.MinBrightness, ImageTuning.MaxBrightness, 0f,
                value => value == 0f ? Lang.T("як у грі") : (value > 0f ? "+" : "") + value.ToString("0.0"));
            State.ImageGamma = ImageSlider(Lang.T("Гамма (середні тони)"), State.ImageGamma, -1f, 1f, 0f,
                value => value == 0f ? Lang.T("як у грі") : (value > 0f ? "+" : "") + value.ToString("0.0"));
            State.ImageGlow = ImageSlider(Lang.T("Сяйво від вогню й ефектів"), State.ImageGlow, 0f, 1f, 1f,
                value => Mathf.RoundToInt(value * 100f) + "%");
            if (!ImageTuning.IsDefault && Bindable(GUILayout.Button(Lang.T("Повернути як у грі") + Binds.Hint("imagereset"), GUILayout.Height(30f)), "imagereset"))
            {
                ImageTuning.Reset();
            }
            GUILayout.EndVertical();
        }

        private static float ImageSlider(string label, float value, float min, float max, float neutral, System.Func<float, string> text)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{label}: {text(value)}", GUILayout.Width(300f));
            float next = GUILayout.HorizontalSlider(value, min, max, GUILayout.MinWidth(200f));
            // Snaps to steps of 0.05 and to the game's own value near it, so "as in the game" is easy to hit.
            next = Mathf.Round(next * 20f) / 20f;
            if (Mathf.Abs(next - neutral) < 0.03f)
            {
                next = neutral;
            }
            GUILayout.EndHorizontal();
            return next;
        }

        private void DrawBuilderViewSection()
        {
            GUILayout.Space(10f);
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Режим перегляду"), HelperSkin.Header);
            GUILayout.Label(Lang.T("<color=#c0c0c0>Сховай частину будівлі, щоб бачити, що всередині й як тримається конструкція. Приховане лише не малюється " +
                "в тебе: воно на місці, тверде (об нього можна вдаритися), і нові деталі до нього так само чіпляються. Друзі бачать усе як завжди. " +
                "Клавішу для перемикання режимів можна призначити в «Налаштування» → «Клавіші».</color>"));
            GUILayout.BeginHorizontal();
            if (Bindable(GUILayout.Button(Lang.T("Усе") + Binds.Hint("bv_all")), "bv_all"))
            {
                ShowMessage(BuilderView.Preset(Lang.T("усе")));
            }
            if (Bindable(GUILayout.Button(Lang.T("Лише каркас") + Binds.Hint("bv_frame")), "bv_frame"))
            {
                ShowMessage(BuilderView.Preset(Lang.T("лише каркас"), BuilderView.Kind.Frame));
            }
            if (Bindable(GUILayout.Button(Lang.T("Каркас і підлоги") + Binds.Hint("bv_floors")), "bv_floors"))
            {
                ShowMessage(BuilderView.Preset(Lang.T("каркас і підлоги"), BuilderView.Kind.Frame, BuilderView.Kind.Floors));
            }
            if (Bindable(GUILayout.Button(Lang.T("Без дахів") + Binds.Hint("bv_noroofs")), "bv_noroofs"))
            {
                ShowMessage(BuilderView.Preset(Lang.T("без дахів"), BuilderView.Kind.Frame, BuilderView.Kind.Walls, BuilderView.Kind.Floors,
                    BuilderView.Kind.Openings, BuilderView.Kind.Other));
            }
            if (Bindable(GUILayout.Button(Lang.T("Лише стіни") + Binds.Hint("bv_walls")), "bv_walls"))
            {
                ShowMessage(BuilderView.Preset(Lang.T("лише стіни"), BuilderView.Kind.Walls, BuilderView.Kind.Openings));
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            for (int i = 0; i < BuilderView.KindNames.Length; i++)
            {
                bool shown = GUILayout.Toggle(BuilderView.Shown[i], " " + BuilderView.KindNames[i] + Binds.Hint("bvk" + i));
                Bindable("bvk" + i);
                if (shown != BuilderView.Shown[i])
                {
                    BuilderView.Set((BuilderView.Kind)i, shown);
                }
                if (i == 2)
                {
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                }
            }
            GUILayout.EndHorizontal();
            if (BuilderView.Active)
            {
                GUILayout.Label(Lang.F("<color=#ffd070>Приховано деталей: {0}</color>", BuilderView.HiddenCount));
            }
            GUILayout.EndVertical();
        }

#if EDITION_FULL
        private void DrawItemsTab()
        {
            Player player = Player.m_localPlayer;
            if (player == null || !EnsureItems())
            {
                GUILayout.Label(Lang.T("Список предметів з'явиться, коли ти зайдеш у світ."));
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Пошук:"), GUILayout.Width(60f));
            m_search = GUILayout.TextField(m_search ?? "");
            GUILayout.EndHorizontal();
            m_itemFilter = GUILayout.Toolbar(m_itemFilter, s_itemFilters);
            m_showDevItems = GUILayout.Toggle(m_showDevItems, Lang.T(" Показувати службові / невикористані предмети гри"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Кількість:"), GUILayout.Width(75f));
            m_amountText = GUILayout.TextField(m_amountText ?? "1", GUILayout.Width(60f));
            foreach (string preset in new[] { "1", "10", "50", "100" })
            {
                if (GUILayout.Button(preset, GUILayout.Width(45f)))
                {
                    m_amountText = preset;
                }
            }
            GUILayout.Label(Lang.T("  Якість:"), GUILayout.Width(60f));
            m_quality = GUILayout.Toolbar(m_quality - 1, s_qualities, GUILayout.Width(140f)) + 1;
            GUILayout.EndHorizontal();
            DrawGiftTarget();

            RefreshItemFilter();
            int shown = 0;
            foreach (ItemEntry entry in m_filteredItems)
            {
                if (shown++ >= MaxListedItems)
                {
                    GUILayout.Label(Lang.F("<color=#c0c0c0>Показано перші {0} із {1}, уточни пошук.</color>", MaxListedItems, m_filteredItems.Count));
                    break;
                }
                GUILayout.BeginHorizontal();
                DrawIcon(entry, 32f);
                GUILayout.Label($"{entry.DisplayName}  <color=#909090>{entry.Prefab}</color>", GUILayout.Height(32f));
                if (GUILayout.Button(Lang.T("Дати"), GUILayout.Width(70f), GUILayout.Height(28f)))
                {
                    GiveItem(player, entry);
                }
                GUILayout.EndHorizontal();
            }
        }
#endif

#if EDITION_FULL
        private void DrawSkillsTab()
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                GUILayout.Label(Lang.T("Зайди у світ, щоб змінювати навички."));
                return;
            }
            Skills skills = player.GetSkills();
            Skills.SkillType[] types = GetSkillTypes();
            if (Bindable(ConfirmButton("allskills", Lang.T("Усі навички до 100") + Binds.Hint("skills100")), "skills100"))
            {
                foreach (Skills.SkillType type in types)
                {
                    skills.CheatRaiseSkill(type.ToString(), 100f, showMessage: false);
                }
                ShowMessage(Lang.T("Усі навички піднято до 100."));
            }
            GUILayout.Space(6f);
            foreach (Skills.SkillType type in types)
            {
                GUILayout.BeginHorizontal();
                string name = Localization.instance.Localize("$skill_" + type.ToString().ToLowerInvariant());
                GUILayout.Label($"{name}: {skills.GetSkillLevel(type):F0}", GUILayout.Width(330f));
                if (GUILayout.Button("+10", GUILayout.Width(60f)))
                {
                    skills.CheatRaiseSkill(type.ToString(), 10f, showMessage: false);
                }
                if (GUILayout.Button("100", GUILayout.Width(60f)))
                {
                    skills.CheatRaiseSkill(type.ToString(), 100f, showMessage: false);
                }
                GUILayout.EndHorizontal();
            }
        }
#endif

        private void DrawAchievementsTab()
        {
            DrawAchievementList();
#if EDITION_FULL
            GUILayout.Label(Lang.T("<b>Що зараз блокує досягнення</b>"));
            GUILayout.Label(Lang.T("Гра позначена як модифікована (isModded): ") + YesNo(Game.isModded));
            SaveCleaner.CharacterStatus character = m_characterStatus;
            if (character != null)
            {
                GUILayout.Label(Lang.T("Персонаж використовував консольні чити: ") + YesNo(character.UsedCheats));
                GUILayout.Label(Lang.T("Позначені предмети в інвентарі: ") + CountText(character.CheatedItems));
                if (character.UncleanableItems.Count > 0)
                {
                    GUILayout.Label(Lang.T("<color=#ffb040>Гра завжди позначає ці предмети (урон > 10000), їх треба викинути: ") +
                        string.Join(", ", character.UncleanableItems) + "</color>");
                }
            }
            WorldStatus();

            GUILayout.Space(8f);
            GUILayout.Label(Lang.T("<b>Очищення сейвів</b>\n<color=#c0c0c0>Спершу зроби копію сейвів. Прогрес, який гра не зарахувала, поки сейв був позначений, " +
                "не повернеться: після очищення зараховуються лише нові дії.</color>"));

            if (ConfirmButton("cleanchar", Lang.T("Очистити персонажа (кожен гравець у своїй грі)")))
            {
                ShowMessage(SaveCleaner.CleanCharacter());
                m_nextStatusRefresh = 0f;
            }

            GUILayout.Space(4f);
            if (GUILayout.Button(Lang.T("Перевірити світ")))
            {
                m_worldStatus = SaveCleaner.ScanWorld();
            }
            m_removeCheatedWorldKeys = GUILayout.Toggle(m_removeCheatedWorldKeys,
                Lang.T(" Також прибрати «читерські» модифікатори світу (їхній вплив на гру зникне)"));
            string blocker = SaveCleaner.WhyWorldCannotBeCleaned();
            if (blocker != null)
            {
                GUILayout.Label(Lang.T("<color=#ffb040>Очистити світ зараз не можна: ") + blocker + "</color>");
            }
            else if (ConfirmButton("cleanworld", Lang.T("Очистити світ і зберегти")))
            {
                ShowMessage(SaveCleaner.CleanWorld(m_removeCheatedWorldKeys));
                m_worldStatus = SaveCleaner.ScanWorld();
            }

            GUILayout.Space(8f);
            GUILayout.Label(Lang.T("<b>Обхід перевірок</b>"));
            State.BypassCheatChecks = GUILayout.Toggle(State.BypassCheatChecks,
                Lang.T(" Ігнорувати позначки гри (діє, поки гра запущена з цим модом)"));
#endif
        }

#if EDITION_FULL
        private void WorldStatus()
        {
            SaveCleaner.WorldStatus world = m_worldStatus;
            if (world == null)
            {
                GUILayout.Label(Lang.T("Світ: <color=#c0c0c0>натисни «Перевірити світ»</color>"));
                return;
            }
            if (world.Error != null)
            {
                GUILayout.Label(Lang.T("Світ: <color=#ff6060>") + world.Error + "</color>");
                return;
            }
            if (world.KeysError != null)
            {
                GUILayout.Label(Lang.T("«Читерські» модифікатори світу: <color=#ffb040>") + world.KeysError + "</color>");
            }
            else
            {
                GUILayout.Label(Lang.T("«Читерські» модифікатори світу: ") +
                    (world.CheatedWorldKeys.Count == 0 ? Lang.T("<color=#80ff80>немає</color>") : "<color=#ff8060>" + string.Join(", ", world.CheatedWorldKeys) + "</color>"));
            }
            GUILayout.Label(Lang.T("Позначені об'єкти (будівлі, моби, станції): ") + CountText(world.FlaggedObjects));
            GUILayout.Label(Lang.T("Позначені предмети на землі/стійках: ") + CountText(world.FlaggedItemDrops));
            GUILayout.Label(Lang.T("Позначені предмети у скринях: ") + CountText(world.FlaggedContainerItems) + Lang.F(" (скринь: {0})", world.FlaggedContainers));
            if (world.UncleanableItems.Count > 0)
            {
                GUILayout.Label(Lang.T("<color=#ffb040>У скринях є предмети, які гра завжди позначає (урон > 10000): ") +
                    string.Join(", ", world.UncleanableItems) + "</color>");
            }
            if (world.UnreadableRecords > 0)
            {
                GUILayout.Label(Lang.F("<color=#c0c0c0>Записів, які не вдалося прочитати (пропущені без змін): {0}</color>", world.UnreadableRecords));
            }
        }
#endif

#if EDITION_FULL
        private void TeleportToMapCursor()
        {
            Vector3 pointer = ZInput.pointerPosition;
            Vector3 worldPoint = (Vector3)s_screenToWorldPoint.Invoke(Minimap.instance, new object[] { pointer });
            if (worldPoint == Vector3.zero)
            {
                return;
            }
            s_debugTeleport.Invoke(Minimap.instance, new object[] { worldPoint });
        }
#endif

#if EDITION_FULL
        private bool EnsureItems()
        {
            if (m_items != null)
            {
                return true;
            }
            if (ObjectDB.instance == null || ObjectDB.instance.m_items == null || ObjectDB.instance.m_items.Count == 0)
            {
                return false;
            }
            List<ItemEntry> items = new List<ItemEntry>();
            foreach (GameObject prefab in ObjectDB.instance.m_items)
            {
                ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (drop == null || drop.m_itemData?.m_shared == null)
                {
                    continue;
                }
                ItemDrop.ItemData data = drop.m_itemData;
                if (data.m_shared.m_icons == null || data.m_shared.m_icons.Length == 0)
                {
                    continue;
                }
                if (data.GetDamage().GetTotalDamage() > MaxSafeItemDamage)
                {
                    continue;
                }
                string display = Localization.instance.Localize(data.m_shared.m_name);
                ItemEntry entry = new ItemEntry
                {
                    Prefab = prefab.name,
                    DisplayName = display,
                    SearchKey = (display + " " + prefab.name).ToLowerInvariant(),
                    Filter = ClassifyItem(data.m_shared.m_itemType.ToString()),
                    MaxQuality = Mathf.Max(1, data.m_shared.m_maxQuality),
                    IsEquipment = data.m_shared.m_maxStackSize <= 1,
                    IsDev = display.StartsWith("[") || display.Contains("TEST") || display.Contains("UNUSED"),
                };
                SetIcon(entry, data.m_shared.m_icons[0]);
                items.Add(entry);
            }
            items.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
            m_items = items;
            Log.Info($"Item list built: {items.Count} items");
            return true;
        }
#endif

#if EDITION_FULL
        private static int ClassifyItem(string itemType)
        {
            switch (itemType)
            {
                case "Material":
                    return 1;
                case "Consumable":
                    return 2;
                case "OneHandedWeapon":
                case "TwoHandedWeapon":
                case "TwoHandedWeaponLeft":
                case "Bow":
                case "Shield":
                case "Ammo":
                case "AmmoNonEquipable":
                case "Torch":
                case "Tool":
                case "Attach_Atgeir":
                    return 3;
                case "Helmet":
                case "Chest":
                case "Legs":
                case "Hands":
                case "Shoulder":
                case "Utility":
                case "Trinket":
                    return 4;
                default:
                    return 5;
            }
        }
#endif

#if EDITION_FULL
        private void RefreshItemFilter()
        {
            string search = (m_search ?? "").Trim().ToLowerInvariant();
            if (search == m_filteredSearch && m_itemFilter == m_filteredFilter && m_showDevItems == m_filteredShowDev)
            {
                return;
            }
            m_filteredSearch = search;
            m_filteredFilter = m_itemFilter;
            m_filteredShowDev = m_showDevItems;
            m_filteredItems.Clear();
            foreach (ItemEntry entry in m_items)
            {
                if ((m_showDevItems || !entry.IsDev) && (m_itemFilter == 0 || entry.Filter == m_itemFilter)
                    && (search.Length == 0 || entry.SearchKey.Contains(search)))
                {
                    m_filteredItems.Add(entry);
                }
            }
        }
#endif

#if EDITION_FULL
        private void GiveItem(Player player, ItemEntry entry)
        {
            if (!int.TryParse(m_amountText, out int amount) || amount <= 0)
            {
                ShowMessage(Lang.T("Вкажи кількість числом."));
                return;
            }
            amount = Mathf.Min(amount, 9999);
            int quality = Mathf.Clamp(m_quality, 1, entry.MaxQuality);
            if (TrySendToFriend(entry, amount, quality))
            {
                return;
            }
            long crafterId = entry.IsEquipment ? player.GetPlayerID() : 0L;
            string crafterName = entry.IsEquipment ? player.GetPlayerName() : "";
            // cheated: false — the item is indistinguishable from a picked up or crafted one.
            ItemDrop.ItemData added = player.GetInventory().AddItem(entry.Prefab, amount, quality, 0, crafterId, crafterName, cheated: false);
            ShowMessage(added != null
                ? Lang.F("Видано: {0} ×{1} (якість {2}). Що не влізло — лежить під ногами.", entry.DisplayName, amount, quality)
                : Lang.T("Немає вільного місця в інвентарі."));
        }
#endif

#if EDITION_FULL
        private static void SetIcon(ItemEntry entry, Sprite sprite)
        {
            try
            {
                if (sprite == null || sprite.texture == null)
                {
                    return;
                }
                Texture2D texture = sprite.texture;
                Rect rect = sprite.textureRect;
                entry.IconTexture = texture;
                entry.IconUv = new Rect(rect.x / texture.width, rect.y / texture.height, rect.width / texture.width, rect.height / texture.height);
            }
            catch
            {
                entry.IconTexture = null;
            }
        }
#endif

#if EDITION_FULL
        private static void DrawIcon(ItemEntry entry, float size)
        {
            Rect rect = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
            if (entry.IconTexture != null && Event.current.type == EventType.Repaint)
            {
                GUI.DrawTextureWithTexCoords(rect, entry.IconTexture, entry.IconUv);
            }
        }
#endif

#if EDITION_FULL
        private static Skills.SkillType[] GetSkillTypes()
        {
            List<Skills.SkillType> result = new List<Skills.SkillType>();
            Array source = (s_allSkills?.GetValue(null) as Array) ?? Enum.GetValues(typeof(Skills.SkillType));
            foreach (object value in source)
            {
                Skills.SkillType type = (Skills.SkillType)value;
                if (type != Skills.SkillType.None && type != Skills.SkillType.All)
                {
                    result.Add(type);
                }
            }
            return result.ToArray();
        }
#endif

        // Destructive actions need a second click within a few seconds.
        private bool ConfirmButton(string id, string label)
        {
            bool waiting = m_confirmId == id && Time.unscaledTime < m_confirmUntil;
            if (!GUILayout.Button(waiting ? "<color=#ffb040>" + label + Lang.T(" — натисни ще раз</color>") : label))
            {
                return false;
            }
            if (waiting)
            {
                m_confirmId = null;
                return true;
            }
            m_confirmId = id;
            m_confirmUntil = Time.unscaledTime + 4f;
            return false;
        }

        private void ShowMessage(string message)
        {
            m_message = message;
            m_messageUntil = Time.unscaledTime + 10f;
            Log.Info(message);
        }

        private static string YesNo(bool value)
        {
            return value ? Lang.T("<color=#ff8060>так</color>") : Lang.T("<color=#80ff80>ні</color>");
        }

        private static string CountText(int count)
        {
            return count == 0 ? "<color=#80ff80>0</color>" : $"<color=#ff8060>{count}</color>";
        }

        private void LogOnce(Exception e)
        {
            if (m_loggedErrors.Add(e.GetType().Name + e.Message))
            {
                Log.Error(e);
            }
        }
    }
}
