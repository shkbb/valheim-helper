using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimHelper.Storage;

namespace ValheimHelper
{
    // "Склад": putting things away, what the storage holds (with orders from afar), the purpose of a chest; also the
    // craft, furnace and "не чіпати" sections of other pages, which share the refresh of what is around.
    internal sealed partial class CheatMenu
    {
        private const int MaxFoundItems = 300;
        private const int MaxChestViews = 30;
        private const float TagChestRange = 10f;
        // Chests of the storage are highlighted only when they are loaded, that is when the player is not far.
        private const float HighlightReach = 120f;
        private static readonly string[] s_storageViews = { Lang.T("Усе разом"), Lang.T("По скринях поруч") };

        private sealed class FoundItem
        {
            public StorageItem Info;
            public int Total;
            public int Chests;
            public float Nearest;
            // Drawn above this row when the list moves on to another group.
            public string GroupHeader;
            // The same thing in the storage as seen from afar (null when there is no storage centre).
            public RemoteStorage.Entry Order;
            public int Quality;
        }

        private sealed class ChestView
        {
            public string Title;
            public readonly List<FoundItem> Items = new List<FoundItem>();
        }

        private int m_storageView;
        private int m_pendingStorageView = -1;
        private readonly List<ChestView> m_storageChestViews = new List<ChestView>();
        private string m_storageSummary = "";
        private bool m_totalsTruncated;
        private bool m_chestsTruncated;

        // Everything below is refreshed on Layout only, so every event of a frame draws the same controls.
        private int m_storageChests;
        private int m_storageStations;
        private string m_storageReport = "";
        private string m_furnaceReport = "";
        private float m_storageRefreshAt = -10f;
        private float m_stationsAt = -100f;
        private string m_storageSearch = "";
        private string m_storageSearched;
        private int m_storageCatalogVersion = -1;
        private bool m_storageFromBase;
        private bool m_storageNearBase;
        private readonly List<FoundItem> m_storageFound = new List<FoundItem>();
        private readonly List<StorageItem> m_storageKeepList = new List<StorageItem>();
        private readonly Dictionary<string, string> m_orderAmounts = new Dictionary<string, string>();
        private Container m_tagChest;
        private bool m_hasTagChest;
        private string m_tagChestText = "";
        private string m_tagChestMemory = "";
        private bool m_tagChestRemembers;
        private int m_tagChestIndex;
        private string m_storageHighlight;
        private string[] m_tagLabels;
        private string[] m_tagIds;

        // A page made of storage sections (the stash, the furnaces, "do not touch"…): they share one refresh of
        // what is around, and all need the player in the world.
        private void DrawStoragePage(params Action[] sections)
        {
            Player player = Player.m_localPlayer;
            if (player == null || !StorageCatalog.EnsureBuilt())
            {
                GUILayout.Label(Lang.T("Це з'явиться, коли ти зайдеш у світ."));
                return;
            }
            if (Event.current.type == EventType.Layout)
            {
                if (m_pendingStorageView >= 0)
                {
                    m_storageView = m_pendingStorageView;
                    m_pendingStorageView = -1;
                }
                RefreshStorage(player);
            }
            foreach (Action section in sections)
            {
                section();
            }
        }

        private void DrawStashSection()
        {
            Player player = Player.m_localPlayer;
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Склад"), HelperSkin.Header);
            GUILayout.Label(Lang.T("<color=#c0c0c0>Стань посеред складу й натисни «Скласти» (або признач клавішу в «Налаштування» → «Клавіші»). Кожна річ іде туди, " +
                "куди ти (або друг з модом) востаннє клав її руками, навіть якщо та скриня зараз порожня. Далі — у скриню, де вона вже лежить: " +
                "спершу доповнить неповні стаки, потім ляже поруч у тому ж ряду. Нова річ іде до своєї групи (сорти дерева до дерева, руда до металів) " +
                "або в скриню свого біома; скриня з призначенням бере лише своє.\n" +
                "Чому місця не знайшлося, те мод розкладає по порожніх скринях полицями: дерево, камінь, метали, трофеї, матеріали біомів — " +
                "кожна полиця в окрему скриню, сусідні полиці поруч. Не подобається, куди лягло? Переклади руками — скриня це запам'ятає. " +
                "Речі з позначкою «не чіпати» лишаються з тобою.</color>"));
            if (Bindable(GUILayout.Button((StorageSorter.Busy ? Lang.T("Розкладаю…") : Lang.T("Скласти речі з інвентаря в скрині поруч")) + Binds.Hint("stash"), GUILayout.Height(34f)), "stash"))
            {
                string message = StorageSorter.StartStash();
                if (!string.IsNullOrEmpty(message))
                {
                    ShowMessage(message);
                }
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.F("Радіус складу: {0:0} м", StorageSettings.Radius), GUILayout.Width(170f));
            float radius = Mathf.Round(GUILayout.HorizontalSlider(StorageSettings.Radius, StorageSettings.MinRadius, StorageSettings.MaxRadius, GUILayout.Width(260f)));
            if (radius != StorageSettings.Radius)
            {
                StorageSettings.Radius = radius;
                StorageSettings.Changed();
            }
            GUILayout.Label(Lang.F("скринь поруч: {0}", m_storageChests));
            GUILayout.EndHorizontal();
            GUILayout.Label(Lang.T("<color=#909090>Цей радіус — для всього «поруч»: складання, печей, крафту й будівництва зі скринь, " +
                "а від центру складу — для замовлень здалеку.</color>"));
            bool useEmpty = GUILayout.Toggle(StorageSettings.UseEmptyChests, Lang.T(" Розкладати по порожніх скринях те, чому немає місця") + Binds.Hint("useempty"));
            Bindable("useempty");
            if (useEmpty != StorageSettings.UseEmptyChests)
            {
                StorageSettings.UseEmptyChests = useEmpty;
                StorageSettings.Changed();
            }
            bool remember = GUILayout.Toggle(StorageSettings.RememberHandPlacement, Lang.T(" Скрині запам'ятовують, що в них кладуть руками") + Binds.Hint("remember"));
            Bindable("remember");
            if (remember != StorageSettings.RememberHandPlacement)
            {
                StorageSettings.RememberHandPlacement = remember;
                StorageSettings.Changed();
            }
            GUILayout.Label(m_storageReport);
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            GUILayout.Label(RemoteStorage.HasBase
                ? Lang.F("Центр складу: {0:0}, {1:0} — {2:0} м від тебе, доставка звідси йде {3}", RemoteStorage.BasePos.x, RemoteStorage.BasePos.z,
                    Vector3.Distance(player.transform.position, RemoteStorage.BasePos), RemoteStorage.Clock(RemoteStorage.SecondsTo(player.transform.position)))
                : Lang.T("<color=#ffb040>Центр складу не задано: без нього не видно, що є на складі, коли ти далеко, і не можна замовляти.</color>"));
            if (Bindable(GUILayout.Button(Lang.T("Тут центр складу") + Binds.Hint("storagecenter"), GUILayout.Width(170f), GUILayout.Height(30f)), "storagecenter"))
            {
                RemoteStorage.SetBase(player.transform.position);
                ShowMessage(RemoteStorage.RequestCatalog());
                m_storageRefreshAt = -10f;
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        // What the storage holds: one list, with a highlight of the chests when you are there and an order from anywhere.
        private void DrawStockSection()
        {
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Що є на складі"), HelperSkin.Header);
            GUILayout.Label(Lang.T("<color=#c0c0c0>«Показати» підсвічує скрині з цією річчю (коли ти поруч). «Доставити» — річ злетить до тебе, де б ти не був: " +
                "чим далі від складу, тим довше: 3 с біля скринь, 1 км — 1,5 хв, край світу — до 10 хв; політ видно на карті; що не влізе в інвентар, ляже поруч. " +
                "Скрині, які хтось відкрив, біля яких стоїть друг, або чужі особисті не чіпаються. " +
                "Склад бачить і замовлення виконує хост, тож у нього має бути Valheim Helper цієї версії.</color>"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Пошук:"), GUILayout.Width(60f));
            m_storageSearch = GUILayout.TextField(m_storageSearch ?? "");
            if (RemoteStorage.HasBase)
            {
                GUI.enabled = !RemoteStorage.Waiting;
                if (GUILayout.Button(RemoteStorage.Waiting ? Lang.T("Чекаю…") : Lang.T("Оновити"), GUILayout.Width(100f)))
                {
                    ShowMessage(RemoteStorage.RequestCatalog());
                }
                GUI.enabled = true;
            }
            GUILayout.EndHorizontal();
            if (m_storageHighlight != null && Bindable(GUILayout.Button(Lang.T("Сховати підсвітку скринь") + Binds.Hint("stashhighlight")), "stashhighlight"))
            {
                StorageOverlay.ClearHighlight();
            }
            foreach (RemoteStorage.Delivery delivery in RemoteStorage.Deliveries)
            {
                GUILayout.Label(Lang.F("<color=#ffd080>Летить до тебе:</color> {0} — ще {1}", delivery.Summary, RemoteStorage.Clock(delivery.Left)));
            }
            if (!string.IsNullOrEmpty(RemoteStorage.LastMessage))
            {
                GUILayout.Label("<color=#c0c0c0>" + RemoteStorage.LastMessage + "</color>");
            }
            int view = GUILayout.Toolbar(m_storageView, s_storageViews);
            if (view != m_storageView)
            {
                m_pendingStorageView = view;
            }
            GUILayout.Label(m_storageSummary);
            if (m_storageView == 0)
            {
                DrawFoundTotals();
            }
            else
            {
                DrawFoundByChest();
            }
            if (m_storageView == 0 ? m_totalsTruncated : m_chestsTruncated)
            {
                GUILayout.Label(Lang.T("<color=#909090>Список задовгий, показано не все — уточни пошук.</color>"));
            }
            GUILayout.EndVertical();
        }

        private void DrawCraftAmountSection()
        {
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Кількість за раз"), HelperSkin.Header);
            bool show = GUILayout.Toggle(StorageSettings.CraftAmountPanel, Lang.T(" Показувати вибір кількості біля вікна крафту") + Binds.Hint("craftamount"));
            Bindable("craftamount");
            if (show != StorageSettings.CraftAmountPanel)
            {
                StorageSettings.CraftAmountPanel = show;
                StorageSettings.Changed();
            }
            GUILayout.Label(Lang.T("<color=#c0c0c0>Праворуч від вікна крафту, на рівні кнопки «Виготовити», з'являються «−», кількість, «+» і «Макс» (нічого не закривають). " +
                "«+» і «−» змінюють на 1, із Shift — на 10; коліщатко миші над ними теж працює. «Макс» — скільки вистачає матеріалів " +
                "(разом зі скринями поруч, якщо це ввімкнено нижче) і місця в інвентарі. Натисни на число, щоб повернути 1. " +
                "Крафтить сама гра, як із затиснутим Shift (там завжди 5): матеріали, досвід навички й бонусні предмети — за кожну штуку. " +
                "Для покращення предметів кількість не показується. Інший рецепт або закрите вікно — знову 1.</color>"));
            GUILayout.EndVertical();
        }

        private void DrawCraftSection()
        {
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Крафт і будівництво зі скринь"), HelperSkin.Header);
            bool use = GUILayout.Toggle(StorageSettings.UseChestsForCrafting, Lang.T(" Рахувати матеріали зі скринь поруч, коли крафтиш") + Binds.Hint("craftchests"));
            Bindable("craftchests");
            if (use != StorageSettings.UseChestsForCrafting)
            {
                StorageSettings.UseChestsForCrafting = use;
                StorageSettings.Changed();
            }
            bool build = GUILayout.Toggle(StorageSettings.UseChestsForBuilding, Lang.T(" Рахувати матеріали зі скринь поруч, коли будуєш молотом (і садиш культиватором)") + Binds.Hint("buildchests"));
            Bindable("buildchests");
            if (build != StorageSettings.UseChestsForBuilding)
            {
                StorageSettings.UseChestsForBuilding = build;
                StorageSettings.Changed();
            }
            GUILayout.Label(Lang.F("<color=#c0c0c0>«Поруч» — у радіусі складу, зараз {0:0} м (змінюється в «База» → «Склад»).\n" +
                "У вікні крафту (будь-яка станція або крафт руками) і з молотом у руках матеріали зі скринь довкола тебе рахуються як твої: " +
                "рецепти й будівлі світяться доступними, а на іконці матеріалу написано «треба/є», наприклад 8/135, де «є» — інвентар разом зі скринями. " +
                "Коли натискаєш «Виготовити» чи ставиш будівлю, мод бере з найближчих скринь лише те, чого бракує в інвентарі, і гра витрачає це як завжди. " +
                "Які рецепти й будівлі відкриті, вирішує тільки гра: мод нічого не відкриває. Якщо скриню відкрив інший гравець, з неї нічого не береться.</color>", StorageSettings.Radius));
            GUILayout.EndVertical();
        }

        private void DrawSmelterOutputSection()
        {
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Готове — одразу в скриню"), HelperSkin.Header);
            bool on = GUILayout.Toggle(StorageSettings.SmelterToChests, Lang.T(" Те, що виготовили печі, вугільні печі, млин, прядка й очисник, класти в скриню поруч") + Binds.Hint("smelteroutput"));
            Bindable("smelteroutput");
            if (on != StorageSettings.SmelterToChests)
            {
                StorageSettings.SmelterToChests = on;
                StorageSettings.Changed();
            }
            GUILayout.Label(Lang.F("<color=#c0c0c0>Злиток (вугілля, борошно…) іде в скриню в радіусі складу ({0:0} м від печі), " +
                "куди ти востаннє клав таку річ руками, а якщо такої немає — у скриню, де вона вже лежить. " +
                "Немає такої скрині або в ній немає місця — випадає з печі, як завжди. " +
                "Працює, коли піччю «керує» твоя гра (ти поруч або хост без інших гравців біля неї); у друга без мода все випадає як завжди.</color>", StorageSettings.Radius));
            GUILayout.EndVertical();
        }

        private void DrawFurnaceSection()
        {
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Завантажити печі поруч"), HelperSkin.Header);
            GUILayout.Label(Lang.F("<color=#c0c0c0>Кладе в усі печі в радіусі складу ({0:0} м) усе, що вони переробляють, і паливо до них: " +
                "спершу з інвентаря, а чого бракує — зі скринь поруч. Які печі заправляти, вирішують галочки нижче. Кожна піч бере лише стільки, скільки влізе, " +
                "а з кількох матеріалів — те, чого в тебе найбільше (звичайне дерево раніше за цінне). Руда розходиться порівну, " +
                "щоб працювали всі печі. Палива кладеться рівно стільки, скільки треба на руду в печі (скажімо, 2 вугілля на 1 руду заліза), без запасу. " +
                "Речі з позначкою «не чіпати» не беруться, тож позначене дерево не згорить у вугільній печі.</color>", StorageSettings.Radius));
            if (Bindable(GUILayout.Button((FurnaceLoader.Busy ? Lang.T("Завантажую…") : Lang.T("Завантажити печі поруч")) + Binds.Hint("furnaces"), GUILayout.Height(34f)), "furnaces"))
            {
                string message = FurnaceLoader.Start();
                if (!string.IsNullOrEmpty(message))
                {
                    ShowMessage(message);
                }
            }
            DrawStationSwitches();
            GUILayout.Label(Lang.F("печей поруч: {0}", m_storageStations));
            GUILayout.Label(m_furnaceReport);
            GUILayout.EndVertical();
        }

        private static readonly FurnaceLoader.Kind[] s_stationKinds =
        {
            FurnaceLoader.Kind.Smelter, FurnaceLoader.Kind.Kiln, FurnaceLoader.Kind.Food, FurnaceLoader.Kind.Other,
        };

        private static string StationGroupName(FurnaceLoader.Kind kind)
        {
            switch (kind)
            {
                case FurnaceLoader.Kind.Smelter: return Lang.T("Плавлять і відливають");
                case FurnaceLoader.Kind.Kiln: return Lang.T("Роблять паливо");
                case FurnaceLoader.Kind.Food: return Lang.T("Готують їжу");
                default: return Lang.T("Інші станції");
            }
        }

        // Every kind of station of the game with a switch of its own, by what it does. The names, what goes in and
        // what comes out are the game's own, so stations of new biomes show up here by themselves.
        private void DrawStationSwitches()
        {
            List<FurnaceLoader.StationKind> stations = FurnaceLoader.Stations();
            if (stations.Count == 0)
            {
                GUILayout.Label(Lang.T("<color=#909090>Печі з'являться, коли зайдеш у світ.</color>"));
                return;
            }
            foreach (FurnaceLoader.Kind kind in s_stationKinds)
            {
                List<FurnaceLoader.StationKind> group = stations.FindAll(station => station.Kind == kind);
                if (group.Count == 0)
                {
                    continue;
                }
                GUILayout.BeginHorizontal();
                int on = group.FindAll(station => StorageSettings.IsStationOn(station.Prefab)).Count;
                bool all = on == group.Count;
                string partial = on > 0 && !all ? Lang.T("  <color=#ffd080>частково</color>") : "";
                bool result = GUILayout.Toggle(all, $" <b>{StationGroupName(kind)}</b>  <color=#a0a0a0>{on}/{group.Count}</color>{partial}");
                if (result != all)
                {
                    foreach (FurnaceLoader.StationKind station in group)
                    {
                        StorageSettings.SetStationOn(station.Prefab, result);
                    }
                }
                GUILayout.EndHorizontal();
                foreach (FurnaceLoader.StationKind station in group)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(22f);
                    bool shown = StorageSettings.IsStationOn(station.Prefab);
                    string what = station.Takes.Length > 0 ? Lang.F("  <color=#a0a0a0>бере: {0}</color>", station.Takes) : "";
                    string makes = station.Makes.Length > 0 ? Lang.F("  <color=#a0a0a0>· робить: {0}</color>", station.Makes) : "";
                    bool value = GUILayout.Toggle(shown, " " + station.Name + what + makes);
                    if (value != shown)
                    {
                        StorageSettings.SetStationOn(station.Prefab, value);
                    }
                    GUILayout.EndHorizontal();
                }
            }
        }

        private void DrawKeepSection()
        {
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Не чіпати"), HelperSkin.Header);
            GUILayout.Label(Lang.T("<color=#c0c0c0>Познач речі, які мод не має забирати з інвентаря ні в скрині, ні в печі, ні у факели: інструменти, їжу, що завгодно. " +
                "Решта переноситься. Вдягнене й те, що в руках, не чіпається ніколи.\n" +
                "Позначати можна й прямо в інвентарі: признач клавішу «Не чіпати: предмет під курсором» в «Налаштування» → «Клавіші», відкрий інвентар, " +
                "наведи на річ і натисни її. Над позначеними речами в інвентарі видно замочок.</color>"));
            if (m_storageKeepList.Count == 0)
            {
                GUILayout.Label(Lang.T("<color=#909090>Інвентар порожній.</color>"));
            }
            for (int i = 0; i < m_storageKeepList.Count; i += 2)
            {
                GUILayout.BeginHorizontal();
                KeepItemToggle(m_storageKeepList[i]);
                if (i + 1 < m_storageKeepList.Count)
                {
                    KeepItemToggle(m_storageKeepList[i + 1]);
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
        }

        private static void KeepItemToggle(StorageItem item)
        {
            GUILayout.BeginHorizontal(GUILayout.Width(318f));
            Rect icon = GUILayoutUtility.GetRect(24f, 24f, GUILayout.Width(24f), GUILayout.Height(24f));
            if (item.IconTexture != null && Event.current.type == EventType.Repaint)
            {
                GUI.DrawTextureWithTexCoords(icon, item.IconTexture, item.IconUv);
            }
            bool kept = StorageSettings.IsItemKept(item.Prefab);
            bool value = GUILayout.Toggle(kept, " " + item.DisplayName);
            if (value != kept)
            {
                StorageSettings.SetItemKept(item.Prefab, value);
            }
            GUILayout.EndHorizontal();
        }

        private void DrawTagSection()
        {
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Призначення скрині"), HelperSkin.Header);
            GUILayout.Label(Lang.T("<color=#c0c0c0>Порожній скрині можна наперед сказати, що в ній лежатиме. Скриня з призначенням бере тільки свою групу чи свій біом " +
                "(і те, що в ній уже лежить). Призначення бачать друзі з Valheim Helper; у грі без мода це звичайна скриня.</color>"));
            if (!m_hasTagChest)
            {
                GUILayout.Label(Lang.T("<color=#ffd080>Подивись на скриню (приціл на ній) і відкрий меню — її можна буде налаштувати тут.</color>"));
            }
            else
            {
                GUILayout.Label(m_tagChestText);
                int index = GUILayout.SelectionGrid(m_tagChestIndex, m_tagLabels, 4);
                if (index != m_tagChestIndex && m_tagChest != null)
                {
                    m_tagChestIndex = index;
                    ShowMessage(StorageSorter.SetTag(m_tagChest, m_tagIds[index]));
                }
                GUILayout.BeginHorizontal();
                GUILayout.Label(m_tagChestMemory);
                if (m_tagChestRemembers && GUILayout.Button(Lang.T("Забути"), GUILayout.Width(90f)))
                {
                    ShowMessage(ChestMemory.Forget(m_tagChest));
                }
                GUILayout.EndHorizontal();
            }
            bool showTags = GUILayout.Toggle(StorageSettings.ShowChestTags, Lang.T(" Показувати призначення, коли дивишся на скриню") + Binds.Hint("chesttags"));
            Bindable("chesttags");
            if (showTags != StorageSettings.ShowChestTags)
            {
                StorageSettings.ShowChestTags = showTags;
                StorageSettings.Changed();
            }
            GUILayout.EndVertical();
        }

        private void DrawFoundByChest()
        {
            foreach (ChestView chest in m_storageChestViews)
            {
                GUILayout.Space(4f);
                GUILayout.Label(chest.Title);
                for (int i = 0; i < chest.Items.Count; i += 2)
                {
                    GUILayout.BeginHorizontal();
                    FoundChip(chest.Items[i]);
                    if (i + 1 < chest.Items.Count)
                    {
                        FoundChip(chest.Items[i + 1]);
                    }
                    GUILayout.EndHorizontal();
                }
            }
        }

        private static void FoundChip(FoundItem item)
        {
            GUILayout.BeginHorizontal(GUILayout.Width(318f));
            Rect icon = GUILayoutUtility.GetRect(24f, 24f, GUILayout.Width(24f), GUILayout.Height(24f));
            if (item.Info.IconTexture != null && Event.current.type == EventType.Repaint)
            {
                GUI.DrawTextureWithTexCoords(icon, item.Info.IconTexture, item.Info.IconUv);
            }
            GUILayout.Label($"{item.Info.DisplayName} <color=#e8d8b0>×{item.Total}</color>", GUILayout.Height(24f));
            GUILayout.EndHorizontal();
        }

        private Vector2 m_stockScroll;
        private int m_stockFirst;
        private int m_stockLast;
        private const float StockRowHeight = 36f;
        private const float StockHeaderHeight = 26f;
        private const float StockViewHeight = 430f;

        // Hundreds of rows of controls cost a frame each, so the list has a window of its own and only the rows
        // inside it are drawn; the empty space above and below keeps the scrollbar honest.
        private void DrawFoundTotals()
        {
            if (Event.current.type == EventType.Layout)
            {
                float y = 0f;
                m_stockFirst = 0;
                m_stockLast = m_storageFound.Count;
                for (int i = 0; i < m_storageFound.Count; i++)
                {
                    float height = (m_storageFound[i].GroupHeader != null ? StockHeaderHeight : 0f) + StockRowHeight;
                    if (y + height < m_stockScroll.y)
                    {
                        m_stockFirst = i + 1;
                    }
                    else if (y > m_stockScroll.y + StockViewHeight)
                    {
                        m_stockLast = i;
                        break;
                    }
                    y += height;
                }
                m_stockFirst = Mathf.Clamp(m_stockFirst, 0, m_storageFound.Count);
                m_stockLast = Mathf.Clamp(m_stockLast, m_stockFirst, m_storageFound.Count);
            }
            float above = 0f;
            float below = 0f;
            for (int i = 0; i < m_storageFound.Count; i++)
            {
                float height = (m_storageFound[i].GroupHeader != null ? StockHeaderHeight : 0f) + StockRowHeight;
                if (i < m_stockFirst)
                {
                    above += height;
                }
                else if (i >= m_stockLast)
                {
                    below += height;
                }
            }
            m_stockScroll = GUILayout.BeginScrollView(m_stockScroll, GUILayout.Height(Mathf.Min(StockViewHeight, above + below + (m_stockLast - m_stockFirst) * StockRowHeight + 40f)));
            GUILayout.Space(above);
            for (int index = m_stockFirst; index < m_stockLast; index++)
            {
                FoundItem found = m_storageFound[index];
                if (found.GroupHeader != null)
                {
                    GUILayout.Space(4f);
                    GUILayout.Label("<b><color=#ffcc6a>" + found.GroupHeader + "</color></b>");
                }
                GUILayout.BeginHorizontal();
                Rect icon = GUILayoutUtility.GetRect(32f, 32f, GUILayout.Width(32f), GUILayout.Height(32f));
                if (found.Info.IconTexture != null && Event.current.type == EventType.Repaint)
                {
                    GUI.DrawTextureWithTexCoords(icon, found.Info.IconTexture, found.Info.IconUv);
                }
                string quality = found.Quality > 1 ? $" ({found.Quality}★)" : "";
                string where = found.Chests > 0 ? Lang.F("  <color=#c0c0c0>у скринях: {0}, найближча за {1:0} м</color>", found.Chests, found.Nearest) : "";
                GUILayout.Label($"{found.Info.DisplayName}{quality}  <color=#e8d8b0>×{found.Total}</color>{where}", GUILayout.MinHeight(32f));
                bool shown = m_storageHighlight == found.Info.SharedName;
                GUI.enabled = shown || found.Chests > 0 || (m_storageFromBase && m_storageNearBase);
                if (GUILayout.Button(shown ? Lang.T("Сховати") : Lang.T("Показати"), GUILayout.Width(90f), GUILayout.Height(28f)))
                {
                    if (shown)
                    {
                        StorageOverlay.ClearHighlight();
                    }
                    else
                    {
                        StorageOverlay.Highlight(found.Info.SharedName);
                        ShowMessage(Lang.T("Скрині з «") + found.Info.DisplayName + Lang.T("» підсвічено на 10 секунд. Закрий меню й подивись довкола."));
                    }
                }
                GUI.enabled = true;
                if (found.Order != null)
                {
                    DrawOrderControls(found);
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(below);
            GUILayout.EndScrollView();
        }

        // Amount and "Доставити" of a row: "−"/"+" by one (Shift — by ten), a stack, all.
        private void DrawOrderControls(FoundItem found)
        {
            RemoteStorage.Entry entry = found.Order;
            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(entry.Prefab) : null;
            ItemDrop item = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            int stack = item != null ? Mathf.Max(1, item.m_itemData.m_shared.m_maxStackSize) : 1;
            string key = entry.Prefab + "|" + entry.Quality;
            if (!m_orderAmounts.TryGetValue(key, out string typed))
            {
                typed = "1";
            }
            int amount = int.TryParse(typed, out int parsed) ? Mathf.Clamp(parsed, 0, entry.Amount) : 0;
            if (GUILayout.Button("−", GUILayout.Width(28f), GUILayout.Height(28f)))
            {
                amount = Mathf.Max(1, amount - (Event.current.shift ? 10 : 1));
                typed = amount.ToString();
            }
            typed = GUILayout.TextField(typed, 5, GUILayout.Width(52f), GUILayout.Height(28f));
            if (GUILayout.Button("+", GUILayout.Width(28f), GUILayout.Height(28f)))
            {
                amount = Mathf.Min(entry.Amount, amount + (Event.current.shift ? 10 : 1));
                typed = amount.ToString();
            }
            if (stack > 1 && GUILayout.Button(Lang.T("стак"), GUILayout.Width(50f), GUILayout.Height(28f)))
            {
                typed = Mathf.Min(stack, entry.Amount).ToString();
            }
            if (GUILayout.Button(Lang.T("усі"), GUILayout.Width(40f), GUILayout.Height(28f)))
            {
                typed = entry.Amount.ToString();
            }
            m_orderAmounts[key] = typed;
            amount = int.TryParse(typed, out parsed) ? Mathf.Clamp(parsed, 0, entry.Amount) : 0;
            GUI.enabled = !RemoteStorage.Waiting && amount > 0;
            if (GUILayout.Button(Lang.T("Доставити"), GUILayout.Width(96f), GUILayout.Height(28f)))
            {
                ShowMessage(RemoteStorage.Order(entry, amount));
            }
            GUI.enabled = true;
        }

        private void RefreshStorage(Player player)
        {
            m_storageHighlight = StorageOverlay.HighlightActive ? StorageOverlay.HighlightName : null;
            m_storageReport = string.IsNullOrEmpty(StorageSorter.LastReport)
                ? Lang.T("<color=#909090>Ще нічого не складали.</color>")
                : Lang.T("<color=#e8d8b0>Останній раз:</color> ") + StorageSorter.LastReport;
            m_furnaceReport = string.IsNullOrEmpty(FurnaceLoader.LastReport)
                ? Lang.T("<color=#909090>Ще нічого не завантажували.</color>")
                : Lang.T("<color=#e8d8b0>Останній раз:</color> ") + FurnaceLoader.LastReport;
            RefreshTagChest(player);
            if (RemoteStorage.HasBase && !RemoteStorage.Waiting && Time.unscaledTime - RemoteStorage.CatalogAt > 15f && CurrentPage.Name == Lang.T("Склад"))
            {
                RemoteStorage.RequestCatalog();
            }

            string search = (m_storageSearch ?? "").Trim().ToLowerInvariant();
            if (search == m_storageSearched && Time.unscaledTime < m_storageRefreshAt && m_storageCatalogVersion == RemoteStorage.Version)
            {
                return;
            }
            m_storageRefreshAt = Time.unscaledTime + 1f;
            m_storageSearched = search;
            m_storageCatalogVersion = RemoteStorage.Version;
            m_storageChests = StorageSorter.ChestsAround(player.transform.position, StorageSettings.Radius).Count;
            if (Time.unscaledTime - m_stationsAt > 5f)
            {
                m_stationsAt = Time.unscaledTime;
                m_storageStations = FurnaceLoader.CountAround(player.transform.position, StorageSettings.Radius);
            }
            RefreshKeepList(player);
            RefreshFound(player, search);
        }

        private void RefreshTagChest(Player player)
        {
            Container hovered = StorageSorter.HoveredChest();
            if (hovered != null)
            {
                m_tagChest = hovered;
            }
            if (m_tagChest != null && (!StorageSorter.IsStorageChest(m_tagChest)
                || Vector3.Distance(player.transform.position, m_tagChest.transform.position) > TagChestRange))
            {
                m_tagChest = null;
            }
            if (m_tagLabels == null)
            {
                List<string> labels = new List<string> { Lang.T("Будь-що") };
                List<string> ids = new List<string> { "" };
                foreach (StorageGroup group in StorageCatalog.Groups)
                {
                    labels.Add(group.Label);
                    ids.Add(group.Id);
                }
                foreach (StorageGroup biome in StorageCatalog.Biomes)
                {
                    labels.Add(biome.DisplayName);
                    ids.Add(biome.Id);
                }
                m_tagLabels = labels.ToArray();
                m_tagIds = ids.ToArray();
            }
            m_hasTagChest = m_tagChest != null;
            if (!m_hasTagChest)
            {
                return;
            }
            string tag = StorageSorter.GetTag(m_tagChest);
            m_tagChestIndex = Math.Max(0, Array.IndexOf(m_tagIds, tag));
            float distance = Vector3.Distance(player.transform.position, m_tagChest.transform.position);
            string name = Localization.instance.Localize(m_tagChest.m_name);
            int items = m_tagChest.GetInventory().NrOfItems();
            m_tagChestText = Lang.F("<b>{0}</b> за {1:0} м, ", name, distance) + (items == 0 ? Lang.T("порожня") : Lang.F("зайнято слотів: {0}", items));

            List<KeyValuePair<string, long>> memory = new List<KeyValuePair<string, long>>(ChestMemory.Read(m_tagChest));
            memory.Sort((a, b) => b.Value.CompareTo(a.Value));
            m_tagChestRemembers = memory.Count > 0;
            if (!m_tagChestRemembers)
            {
                m_tagChestMemory = Lang.T("<color=#909090>Пам'ять: руками сюди ще нічого не клали.</color>");
                return;
            }
            List<string> names = new List<string>();
            foreach (KeyValuePair<string, long> entry in memory)
            {
                if (names.Count == 12)
                {
                    names.Add(Lang.F("і ще {0}", memory.Count - 12));
                    break;
                }
                StorageItem info = StorageCatalog.Get(entry.Key);
                names.Add(info != null ? info.DisplayName : Localization.instance.Localize(entry.Key));
            }
            m_tagChestMemory = Lang.T("<color=#e8d8b0>Пам'ять (клали руками):</color> ") + string.Join(", ", names.ToArray());
        }

        // Every kind of item in the inventory, and only those: an item put away leaves the list. Its mark stays in
        // the settings without being shown, so taking it again keeps it safe (the padlock in the inventory shows it).
        private void RefreshKeepList(Player player)
        {
            m_storageKeepList.Clear();
            HashSet<string> seen = new HashSet<string>();
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItemsInGridOrder())
            {
                StorageItem info = StorageCatalog.Get(item);
                if (info != null && seen.Add(info.Prefab))
                {
                    m_storageKeepList.Add(info);
                }
            }
        }

        // Both views of "Що є на складі": totals per item (grouped) and the contents of every chest around. With a
        // storage centre the totals come from the whole storage (the host reads it, from anywhere), otherwise from
        // the chests loaded around the player.
        private void RefreshFound(Player player, string search)
        {
            m_storageFound.Clear();
            m_storageChestViews.Clear();
            m_totalsTruncated = false;
            m_chestsTruncated = false;
            List<Container> chests = new List<Container>();
            foreach (Container container in StorageSorter.LoadedContainers())
            {
                Inventory inventory = container.GetInventory();
                if (inventory != null && inventory.NrOfItems() > 0 && StorageSorter.IsStorageChest(container))
                {
                    chests.Add(container);
                }
            }
            Vector3 origin = player.transform.position;
            chests.Sort((a, b) => (a.transform.position - origin).sqrMagnitude.CompareTo((b.transform.position - origin).sqrMagnitude));

            Dictionary<string, FoundItem> totals = new Dictionary<string, FoundItem>();
            int pieces = 0;
            int chestsWithMatches = 0;
            foreach (Container container in chests)
            {
                Inventory inventory = container.GetInventory();
                float distance = Vector3.Distance(origin, container.transform.position);
                ChestView chestView = null;
                Dictionary<string, FoundItem> inChest = new Dictionary<string, FoundItem>();
                foreach (ItemDrop.ItemData item in inventory.GetAllItemsInGridOrder())
                {
                    StorageItem info = StorageCatalog.Get(item);
                    if (info == null || (search.Length > 0 && !info.SearchKey.Contains(search)))
                    {
                        continue;
                    }
                    pieces += item.m_stack;
                    if (!totals.TryGetValue(info.SharedName, out FoundItem total))
                    {
                        total = new FoundItem { Info = info, Nearest = float.MaxValue };
                        totals[info.SharedName] = total;
                    }
                    total.Total += item.m_stack;
                    if (chestView == null)
                    {
                        chestView = new ChestView { Title = ChestTitle(container, distance) };
                        chestsWithMatches++;
                    }
                    if (!inChest.TryGetValue(info.SharedName, out FoundItem here))
                    {
                        here = new FoundItem { Info = info };
                        inChest[info.SharedName] = here;
                        chestView.Items.Add(here);
                        total.Chests++;
                        total.Nearest = Mathf.Min(total.Nearest, distance);
                    }
                    here.Total += item.m_stack;
                }
                if (chestView != null)
                {
                    if (m_storageChestViews.Count < MaxChestViews)
                    {
                        m_storageChestViews.Add(chestView);
                    }
                    else
                    {
                        m_chestsTruncated = true;
                    }
                }
            }

            m_storageFromBase = RemoteStorage.HasBase && RemoteStorage.Catalog.Count > 0;
            m_storageNearBase = RemoteStorage.HasBase && Vector3.Distance(origin, RemoteStorage.BasePos) < StorageSettings.Radius + HighlightReach;
            if (m_storageFromBase)
            {
                // the whole storage: amounts from the host, chest counts from what is loaded around (when you are there)
                Dictionary<string, FoundItem> nearby = totals;
                totals = new Dictionary<string, FoundItem>();
                pieces = 0;
                foreach (RemoteStorage.Entry entry in RemoteStorage.Catalog)
                {
                    StorageItem info = StorageCatalog.Get(entry.SharedName);
                    string name = info != null ? info.DisplayName : Localization.instance.Localize(entry.SharedName);
                    if (search.Length > 0 && !(info != null ? info.SearchKey : name.ToLowerInvariant() + " " + entry.Prefab.ToLowerInvariant()).Contains(search))
                    {
                        continue;
                    }
                    if (info == null)
                    {
                        continue;
                    }
                    string key = entry.SharedName + "|" + entry.Quality;
                    FoundItem found = new FoundItem { Info = info, Total = entry.Amount, Order = entry, Quality = entry.Quality, Nearest = float.MaxValue };
                    if (nearby.TryGetValue(entry.SharedName, out FoundItem here) && !totals.ContainsKey(entry.SharedName))
                    {
                        found.Chests = here.Chests;
                        found.Nearest = here.Nearest;
                    }
                    totals[key] = found;
                    pieces += entry.Amount;
                }
            }

            m_storageFound.AddRange(totals.Values);
            m_storageFound.Sort((a, b) =>
            {
                int order = GroupOrder(a.Info).CompareTo(GroupOrder(b.Info));
                if (order != 0)
                {
                    return order;
                }
                order = string.Compare(a.Info.DisplayName, b.Info.DisplayName, StringComparison.CurrentCultureIgnoreCase);
                return order != 0 ? order : a.Quality.CompareTo(b.Quality);
            });
            if (m_storageFound.Count > MaxFoundItems)
            {
                m_storageFound.RemoveRange(MaxFoundItems, m_storageFound.Count - MaxFoundItems);
                m_totalsTruncated = true;
            }
            string previous = null;
            foreach (FoundItem found in m_storageFound)
            {
                string header = GroupHeader(found.Info);
                found.GroupHeader = header != previous ? header : null;
                previous = header;
            }

            if (totals.Count == 0)
            {
                m_storageSummary = search.Length > 0 ? Lang.T("<color=#909090>Такого немає.</color>")
                    : RemoteStorage.HasBase && RemoteStorage.Waiting ? Lang.T("<color=#909090>Питаю склад…</color>")
                    : Lang.T("<color=#909090>Поруч немає скринь із речами.</color>");
            }
            else if (m_storageFromBase)
            {
                m_storageSummary = Lang.F("Увесь склад: різних речей <b>{0}</b>, усього штук <b>{1}</b>", totals.Count, pieces);
            }
            else
            {
                m_storageSummary = Lang.F("Скрині поруч: <b>{0}</b>, різних речей <b>{1}</b>, усього штук <b>{2}</b>", chestsWithMatches, totals.Count, pieces) +
                    (RemoteStorage.HasBase ? "" : Lang.T("  <color=#909090>(задай центр складу, щоб бачити весь склад і замовляти здалеку)</color>"));
            }
        }

        private static string ChestTitle(Container container, float distance)
        {
            Inventory inventory = container.GetInventory();
            StorageGroup tag = StorageCatalog.FindGroup(StorageSorter.GetTag(container));
            string tagText = tag != null ? $"  <color=#ffcc6a>[{tag.DisplayName}]</color>" : "";
            return Lang.F("<b>{0}</b>{1}  <color=#c0c0c0>{2:0} м, " +
                "слотів {3}/{4}</color>", Localization.instance.Localize(container.m_name), tagText, distance, inventory.NrOfItems(), inventory.GetWidth() * inventory.GetHeight());
        }

        // Groups in catalog order; items without a group follow, by biome.
        private static int GroupOrder(StorageItem item)
        {
            if (item.Group != null)
            {
                return Array.IndexOf(StorageCatalog.Groups, item.Group);
            }
            int biome = item.BiomeGroup != null ? Array.IndexOf(StorageCatalog.Biomes, item.BiomeGroup) : StorageCatalog.Biomes.Length;
            return StorageCatalog.Groups.Length + biome;
        }

        private static string GroupHeader(StorageItem item)
        {
            if (item.Group != null)
            {
                return item.Group.Label;
            }
            return item.BiomeGroup != null ? Lang.T("Інше · ") + item.BiomeGroup.DisplayName : Lang.T("Інше");
        }
    }
}
