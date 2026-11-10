using System;
using System.Collections.Generic;
using ValheimHelper.Map;
using ValheimHelper.Perf;
using ValheimHelper.Storage;

// Scenarios modelled on a real storage: material chests (wood, stone, metals, hides, trophies) next to biome chests.
// Biomes: 0 Meadows, 1 Black Forest, 2 Swamp, 3 Mountain, 4 Plains, 5 Mistlands, 6 Ashlands.
internal static class Program
{
    private static int s_failed;

    private static ItemTraits T(string id, string group, int biome)
    {
        bool tiered = group == "wood" || group == "stone" || group == "metal";
        return new ItemTraits { Id = id, Group = group, GroupIsTiered = tiered, Biome = biome };
    }

    private sealed class Chest
    {
        public string Name;
        public int TagBiome = -1;
        public readonly List<ChestKind> Kinds = new List<ChestKind>();
        public readonly Dictionary<string, int> Real = new Dictionary<string, int>();
        public readonly Dictionary<string, long> Memory = new Dictionary<string, long>();

        public Chest Add(ItemTraits traits, int slots)
        {
            Kinds.Add(new ChestKind(traits, slots));
            Real[traits.Id] = slots * 50;
            return this;
        }

        public Chest Remember(ItemTraits traits, long time)
        {
            Memory[traits.Id] = time;
            return this;
        }
    }

    private static readonly ItemTraits Wood = T("Wood", "wood", 0);
    private static readonly ItemTraits FineWood = T("FineWood", "wood", 0);
    private static readonly ItemTraits RoundLog = T("RoundLog", "wood", 1);
    private static readonly ItemTraits ElderBark = T("ElderBark", "wood", 2);
    private static readonly ItemTraits YggdrasilWood = T("YggdrasilWood", "wood", 5);
    private static readonly ItemTraits Blackwood = T("Blackwood", "wood", 6);
    private static readonly ItemTraits Stone = T("Stone", "stone", 0);
    private static readonly ItemTraits Flint = T("Flint", "stone", 0);
    private static readonly ItemTraits BlackMarble = T("BlackMarble", "stone", 5);
    private static readonly ItemTraits Grausten = T("Grausten", "stone", 6);
    private static readonly ItemTraits SulfurStone = T("SulfurStone", "stone", 6);
    private static readonly ItemTraits Copper = T("Copper", "metal", 1);
    private static readonly ItemTraits TinOre = T("TinOre", "metal", 1);
    private static readonly ItemTraits Iron = T("Iron", "metal", 2);
    private static readonly ItemTraits IronScrap = T("IronScrap", "metal", 2);
    private static readonly ItemTraits Silver = T("Silver", "metal", 3);
    private static readonly ItemTraits BlackMetal = T("BlackMetal", "metal", 4);
    private static readonly ItemTraits FlametalOre = T("FlametalOre", "metal", 6);
    private static readonly ItemTraits Carapace = T("Carapace", null, 5);
    private static readonly ItemTraits Mandible = T("Mandible", null, 5);
    private static readonly ItemTraits BlackCore = T("BlackCore", "cores", 5);
    private static readonly ItemTraits JuteBlue = T("JuteBlue", null, 5);
    private static readonly ItemTraits Sap = T("Sap", null, 5);
    private static readonly ItemTraits Eitr = T("Eitr", null, 5);
    private static readonly ItemTraits Softtissue = T("Softtissue", null, 5);
    private static readonly ItemTraits Guck = T("Guck", null, 2);
    private static readonly ItemTraits Entrails = T("Entrails", null, 2);
    private static readonly ItemTraits Root = T("Root", null, 2);
    private static readonly ItemTraits TrophyBoar = T("TrophyBoar", "trophies", 0);
    private static readonly ItemTraits TrophyTroll = T("TrophyTroll", "trophies", 1);
    private static readonly ItemTraits TrophySeeker = T("TrophySeeker", "trophies", 5);
    private static readonly ItemTraits TrophyBlob = T("TrophyBlob", "trophies", 2);
    private static readonly ItemTraits DeerHide = T("DeerHide", "hides", 0);
    private static readonly ItemTraits LoxPelt = T("LoxPelt", "hides", 4);
    private static readonly ItemTraits ScaleHide = T("ScaleHide", "hides", 5);
    private static readonly ItemTraits Feathers = T("Feathers", null, 0);
    private static readonly ItemTraits Resin = T("Resin", null, 0);
    private static readonly ItemTraits Mystery = T("Mystery", null, -1);
    private static readonly ItemTraits Coins = T("Coins", "valuables", 1);
    private static readonly ItemTraits Amber = T("Amber", "valuables", 0);
    private static readonly ItemTraits GreydwarfEye = T("GreydwarfEye", null, 1);
    private static readonly ItemTraits AncientSeed = T("AncientSeed", null, 1);
    private static readonly ItemTraits BoneFragments = T("BoneFragments", null, 1);

    private static List<Chest> Storage()
    {
        return new List<Chest>
        {
            new Chest { Name = "Деревина" }.Add(Wood, 10).Add(FineWood, 3),
            new Chest { Name = "Кора та міцна деревина" }.Add(ElderBark, 4).Add(RoundLog, 4),
            new Chest { Name = "Іґдрасілева" }.Add(YggdrasilWood, 5),
            new Chest { Name = "Камінь" }.Add(Stone, 6).Add(Flint, 1),
            new Chest { Name = "Мідь/залізо" }.Add(Copper, 2).Add(Iron, 2).Add(IronScrap, 2),
            new Chest { Name = "Чорний метал/срібло" }.Add(BlackMetal, 2).Add(Silver, 2),
            new Chest { Name = "Туманки" }.Add(Carapace, 3).Add(BlackCore, 1).Add(Mandible, 1).Add(JuteBlue, 1),
            new Chest { Name = "Болото" }.Add(Guck, 2).Add(Entrails, 2),
            new Chest { Name = "Трофеї" }.Add(TrophyBoar, 1).Add(TrophyTroll, 1),
            new Chest { Name = "Шкіра" }.Add(DeerHide, 3).Add(LoxPelt, 2),
        };
    }

    private static string Best(List<Chest> chests, ItemTraits item)
    {
        int everywhere = 0;
        List<Dictionary<string, long>> memories = new List<Dictionary<string, long>>();
        foreach (Chest chest in chests)
        {
            everywhere += chest.Real.TryGetValue(item.Id, out int count) ? count : 0;
            memories.Add(chest.Memory);
        }
        int latest = ChestMemoryRules.Latest(memories, item.Id);
        string best = null;
        float bestScore = 0f;
        for (int i = 0; i < chests.Count; i++)
        {
            Chest chest = chests[i];
            chest.Real.TryGetValue(item.Id, out int here);
            float score = SortRules.Score(item, chest.Kinds, null, chest.TagBiome, i == latest, here, everywhere, everywhere > 0 || latest >= 0);
            if (score > bestScore)
            {
                bestScore = score;
                best = chest.Name;
            }
        }
        return best;
    }

    private static void Expect(string title, List<Chest> chests, ItemTraits item, string expected)
    {
        string actual = Best(chests, item);
        Check($"{title}: {item.Id} -> {actual ?? "(лишається в інвентарі)"}" + (actual == expected ? "" : $"   очікувалось: {expected ?? "(лишається в інвентарі)"}"),
            actual == expected);
    }

    private static void Check(string title, bool ok)
    {
        s_failed += ok ? 0 : 1;
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {title}");
    }

    // A friend's storage: coins went into the Black Forest chest because coins lie in Black Forest treasure chests.
    private static void OwnKindScenarios()
    {
        List<Chest> forest = Storage();
        forest.Add(new Chest { Name = "Чорнолісся" }.Add(GreydwarfEye, 3).Add(AncientSeed, 1).Add(BoneFragments, 2).Add(Resin, 1));
        Expect("Монети не йдуть у скриню біому, де немає нічого цінного", forest, Coins, null);
        Expect("Трофей троля не йде в скриню Чорнолісся, коли є трофеї", forest, TrophyTroll, "Трофеї");
        Expect("Матеріал біому й далі йде в скриню свого біому", forest, T("TrollHide", null, 1), "Чорнолісся");

        List<Chest> valuables = Storage();
        valuables.Add(new Chest { Name = "Чорнолісся" }.Add(GreydwarfEye, 3).Add(AncientSeed, 1).Add(BoneFragments, 2));
        valuables.Add(new Chest { Name = "Цінності" }.Add(Amber, 1));
        Expect("Монети йдуть до цінностей", valuables, Coins, "Цінності");

        List<Chest> remembered = Storage();
        remembered.Add(new Chest { Name = "Чорнолісся, монети клали сюди руками" }.Add(GreydwarfEye, 3).Remember(Coins, 100));
        remembered.Add(new Chest { Name = "Цінності" }.Add(Amber, 1));
        Expect("Ручна дія й далі важливіша: монети туди, куди їх клали", remembered, Coins, "Чорнолісся, монети клали сюди руками");
    }

    // A friend's storage, 19.09: a crossbow and a wisp went into the chest of black metal and Ashlands materials.
    // Neither belongs there: gear keeps to gear, and a Mistlands material is not Ashlands or Plains just because
    // some recipe uses them together.
    private static void AshlandsScenarios()
    {
        ItemTraits wisp = T("Wisp", null, 5);
        ItemTraits crossbow = T("CrossbowArbalest", "gear", 5);
        ItemTraits charredBone = T("CharredBone", null, 6);
        ItemTraits askHide = T("AskHide", "hides", 6);
        Func<string, string, float> before = SortRules.CoUse;
        SortRules.CoUse = (a, b) =>
        {
            string pair = string.CompareOrdinal(a, b) < 0 ? a + "+" + b : b + "+" + a;
            return pair == "BlackMetal+Wisp" || pair == "CharredBone+Wisp" || pair == "BlackMetal+CrossbowArbalest" || pair == "CharredBone+CrossbowArbalest" ? 1f : 0f;
        };
        List<Chest> storage = Storage();
        storage.RemoveAll(c => c.Name == "Туманки");
        storage.Add(new Chest { Name = "Чорний метал і Попелисті" }.Add(BlackMetal, 3).Add(FlametalOre, 2).Add(charredBone, 2).Add(Grausten, 2).Add(askHide, 1));
        Expect("Візп не йде до чорного металу й Попелистих через спільні рецепти", storage, wisp, null);
        Expect("Арбалет не йде в скриню матеріалів", storage, crossbow, null);
        List<Chest> half = Storage();
        half.RemoveAll(c => c.Name == "Туманки");
        half.Add(new Chest { Name = "Навпіл: чорний метал і кістки" }.Add(BlackMetal, 5).Add(charredBone, 5));
        Expect("Візп не йде в скриню, де навпіл чорний метал і кістки Попелистих", half, wisp, null);
        Expect("Арбалет не йде в скриню, де навпіл чорний метал і кістки Попелистих", half, crossbow, null);
        Expect("Кістки Попелистих і далі йдуть у свою скриню", storage, T("AshlandsCore", null, 6), "Чорний метал і Попелисті");

        List<Chest> withMist = Storage();
        withMist.Add(new Chest { Name = "Чорний метал і Попелисті" }.Add(BlackMetal, 3).Add(FlametalOre, 2).Add(charredBone, 2).Add(Grausten, 2));
        Expect("Візп іде до Туманок, коли така скриня є", withMist, wisp, "Туманки");
        SortRules.CoUse = before;
    }

    private static void MemoryScenarios()
    {
        List<Chest> marble = Storage();
        marble.Add(new Chest { Name = "Порожній, сюди клали мармур" }.Remember(BlackMarble, 100));
        Expect("Пам'ять: мармур іде в порожній сундук, куди його клали руками", marble, BlackMarble, "Порожній, сюди клали мармур");
        Expect("Пам'ять не тягне в сундук інші предмети", marble, Softtissue, "Туманки");

        List<Chest> scrap = Storage();
        scrap.Add(new Chest { Name = "Брухт" }.Remember(IronScrap, 100));
        Expect("Пам'ять сильніша за те, де предмет зараз лежить", scrap, IronScrap, "Брухт");

        List<Chest> twice = Storage();
        twice.Add(new Chest { Name = "Колись корінь" }.Remember(Root, 100));
        twice.Add(new Chest { Name = "Тепер корінь" }.Remember(Root, 200));
        Expect("Перемагає остання ручна дія", twice, Root, "Тепер корінь");

        List<Chest> tagged = Storage();
        tagged.Add(new Chest { Name = "Мітка Туманок, але корінь клали сюди", TagBiome = 5 }.Remember(Root, 100));
        Expect("Ручна дія важливіша за мітку", tagged, Root, "Мітка Туманок, але корінь клали сюди");

        List<Chest> friend = Storage();
        Expect("Склад друга без пам'яті: предмет іде туди, де друг його поклав", friend, Copper, "Мідь/залізо");
    }

    private static void MemoryFormatScenarios()
    {
        Dictionary<string, long> parsed = ChestMemoryRules.Parse("$item_wood|120;сміття;$item_stone|x;$item_resin|90;$item_wood|80");
        Check("Пам'ять читається, сміття пропускається", parsed.Count == 2 && parsed["$item_wood"] == 120 && parsed["$item_resin"] == 90);
        Check("Пам'ять записується й читається без втрат",
            ChestMemoryRules.Format(ChestMemoryRules.Parse(ChestMemoryRules.Format(parsed))) == ChestMemoryRules.Format(parsed));

        Dictionary<string, long> entries = new Dictionary<string, long> { ["a"] = 50 };
        ChestMemoryRules.Remember(entries, new[] { "a" }, 10);
        Check("Старіша дія не перезаписує новішу", entries["a"] == 50);

        for (int i = 0; i < ChestMemoryRules.MaxEntries + 5; i++)
        {
            ChestMemoryRules.Remember(entries, new[] { "item" + i }, 100 + i);
        }
        Check("Пам'ять обмежена й забуває найстаріше", entries.Count == ChestMemoryRules.MaxEntries && !entries.ContainsKey("a")
            && entries.ContainsKey("item" + (ChestMemoryRules.MaxEntries + 4)));
    }

    private static readonly string[] s_groupOrder = { "wood", "stone", "metal", "fuel", "cores", "hides", "seeds", "rawfood", "valuables", "trophies", "fish", "food", "meads", "ammo", "gear" };

    // Chests in a row along x (2 m apart), listed out of order.
    private static List<ShelfPlanner.EmptyChest> Row(int count, int slots)
    {
        List<ShelfPlanner.EmptyChest> chests = new List<ShelfPlanner.EmptyChest>();
        for (int i = 0; i < count; i++)
        {
            int x = (i * 3) % count;
            chests.Add(new ShelfPlanner.EmptyChest { Slots = slots, Position = new Spot(x * 2f, 0f, 0f) });
        }
        return chests;
    }

    // Row positions (x index) the entry landed in.
    private static List<int> Where(List<ShelfPlanner.Placement> placements, List<ShelfPlanner.EmptyChest> chests, int entry)
    {
        List<int> result = new List<int>();
        foreach (ShelfPlanner.Placement placement in placements)
        {
            int x = (int)(chests[placement.Chest].Position.X / 2f);
            if (placement.Entry == entry && !result.Contains(x))
            {
                result.Add(x);
            }
        }
        return result;
    }

    private static int First(List<ShelfPlanner.Placement> placements, List<ShelfPlanner.EmptyChest> chests, int entry)
    {
        List<int> where = Where(placements, chests, entry);
        return where.Count > 0 ? where[0] : -1;
    }

    private static ShelfPlanner.Entry E(ItemTraits traits, int slots)
    {
        return new ShelfPlanner.Entry { Traits = traits, Slots = slots };
    }

    private static void ShelfScenarios()
    {
        ShelfPlanner.GroupOrder = group => Array.IndexOf(s_groupOrder, group);
        Spot start = new Spot(-2f, 0f, 0f);

        // A new storage: all chests are empty.
        List<ShelfPlanner.Entry> items = new List<ShelfPlanner.Entry>
        {
            E(Feathers, 1), E(Stone, 2), E(Wood, 3), E(TrophyBoar, 1), E(Sap, 1), E(Flint, 1), E(FineWood, 1), E(Copper, 1), E(Resin, 1), E(Mystery, 1),
        };
        List<ShelfPlanner.EmptyChest> row = Row(10, 18);
        List<ShelfPlanner.Placement> plan = ShelfPlanner.Plan(items, row, start);
        string layout = "";
        for (int i = 0; i < items.Count; i++)
        {
            layout += $" {items[i].Traits.Id}@{string.Join("+", Where(plan, row, i))}";
        }
        Console.WriteLine("     Розкладка нового складу:" + layout);
        Check("Новий склад: усе дерево в першому сундуку", Where(plan, row, 2).Count == 1 && First(plan, row, 2) == 0 && First(plan, row, 6) == 0);
        Check("Новий склад: камінь і кремінь разом у наступному", First(plan, row, 1) == 1 && First(plan, row, 5) == 1);
        Check("Новий склад: метал далі", First(plan, row, 7) == 2);
        Check("Новий склад: трофеї окремо", First(plan, row, 3) == 3);
        Check("Новий склад: пір'я і смола разом (Луки)", First(plan, row, 0) == 4 && First(plan, row, 8) == 4);
        Check("Новий склад: сік окремо (Туманки)", First(plan, row, 4) == 5);
        Check("Новий склад: невідоме в кінці", First(plan, row, 9) == 6);

        // Only two chests: shelves share them and nothing is lost.
        List<ShelfPlanner.EmptyChest> two = Row(2, 18);
        List<ShelfPlanner.Placement> packed = ShelfPlanner.Plan(items, two, start);
        int placed = 0;
        foreach (ShelfPlanner.Placement placement in packed)
        {
            placed += placement.Slots;
        }
        Check("Мало сундуків: полиці ділять сундуки, усе влізло", placed == 13);
        Check("Мало сундуків: дерево й камінь у першому", First(packed, two, 2) == 0 && First(packed, two, 1) == 0);

        // A shelf bigger than a chest goes on into the neighbouring chest.
        List<ShelfPlanner.EmptyChest> small = Row(4, 4);
        List<ShelfPlanner.Placement> big = ShelfPlanner.Plan(new List<ShelfPlanner.Entry> { E(Wood, 3), E(FineWood, 3), E(Stone, 2) }, small, start);
        Check("Велика полиця переходить у сусідній сундук", First(big, small, 1) == 0 && Where(big, small, 0).Contains(0) && Where(big, small, 0).Contains(1) && First(big, small, 2) == 2);

        // Overflow of a full chest standing next to x = 7 goes into the empty chest nearest to it.
        List<ShelfPlanner.EmptyChest> spare = Row(8, 18);
        List<ShelfPlanner.Placement> overflow = ShelfPlanner.Plan(new List<ShelfPlanner.Entry>
        {
            new ShelfPlanner.Entry { Traits = Wood, Slots = 2, HasHome = true, Home = new Spot(15f, 0f, 1f) },
            new ShelfPlanner.Entry { Traits = FineWood, Slots = 1, HasHome = true, Home = new Spot(15f, 0f, 1f) },
            E(Stone, 1),
        }, spare, start);
        Check("Переповнення йде в порожній сундук біля свого", First(overflow, spare, 0) == 7 && First(overflow, spare, 1) == 7);
        Check("Нова полиця не займає сундук переповнення", First(overflow, spare, 2) == 0);

        Check("Порожній список сундуків нічого не ламає", ShelfPlanner.Plan(items, new List<ShelfPlanner.EmptyChest>(), start).Count == 0);
    }


    private static void LodScenarios()
    {
        float pixels = LodRules.ProjectedPixels(1f, 100f, 1080f, (float)Math.Tan(32.5 * Math.PI / 180.0));
        Check($"Розмір на екрані: стіна з радіусом 1 м за 100 м ≈ {pixels:0.0} px", Math.Abs(pixels - 16.95f) < 0.2f);
        Check("Камера всередині об'єкта: він завжди видимий", LodRules.ProjectedPixels(2f, 1f, 1080f, 0.6f) == float.MaxValue);
        Check("Менше межі ховається", LodRules.Below(false, 9f, 10f));
        Check("Трохи більше межі ще видно", !LodRules.Below(false, 10.5f, 10f));
        Check("Схований не блимає біля межі", LodRules.Below(true, 12f, 10f));
        Check("Схований повертається, коли помітно більший", !LodRules.Below(true, 14f, 10f));
        Check("Межа 0 вимикає правило", !LodRules.Below(false, 0.1f, 0f));

        bool[] chosen = LodRules.ShadowBudget(new List<float> { 0.5f, 2f, 0f, 1.9f, 1f }, new List<bool> { false, false, false, false, false }, 2);
        Check("Тіні отримують найпомітніші світла", chosen[1] && chosen[3] && !chosen[0] && !chosen[2] && !chosen[4]);
        bool[] sticky = LodRules.ShadowBudget(new List<float> { 2f, 1.8f }, new List<bool> { false, true }, 1);
        Check("Тінь не перестрибує між схожими світлами", sticky[1] && !sticky[0]);
        bool[] unseen = LodRules.ShadowBudget(new List<float> { 0f, 0f }, new List<bool> { true, true }, 4);
        Check("Невидимі світла тіней не отримують", !unseen[0] && !unseen[1]);
        Check("Бюджет 0 — без тіней", !LodRules.ShadowBudget(new List<float> { 5f }, new List<bool> { false }, 0)[0]);

        Check("Звук: лінійне затухання ставиться на паузу трохи далі межі", Math.Abs(LodRules.AudibleDistance(20f, false) - 22f) < 0.01f);
        Check("Звук: логарифмічне затухання має ширший запас", LodRules.AudibleDistance(20f, true) > LodRules.AudibleDistance(20f, false) + 5f);

        float nearBehind = LodRules.LightScore(10f, 8f, seen: false, near: true);
        float farSeen = LodRules.LightScore(10f, 30f, seen: true, near: false);
        float closeSeen = LodRules.LightScore(10f, 12f, seen: true, near: false);
        float behind = LodRules.LightScore(10f, 12f, seen: false, near: false);
        Check("Світло: близьке до гравця важливіше за будь-яке в кадрі", nearBehind > closeSeen && nearBehind > farSeen);
        Check("Світло: у кадрі ближче важливіше за далеке", closeSeen > farSeen);
        Check("Світло: поза кадром гасне першим, але не нульове", behind > 0f && behind < farSeen);
        List<float> lightScores = new List<float> { behind, farSeen, nearBehind, closeSeen, behind };
        bool[] lit = LodRules.ShadowBudget(lightScores, new List<bool> { true, true, true, true, true }, 3);
        Check("Світло: при бюджеті 3 лишаються близьке, ближче в кадрі й далеке в кадрі", lit[2] && lit[3] && lit[1] && !lit[0] && !lit[4]);
        bool[] allLit = LodRules.ShadowBudget(lightScores, new List<bool> { true, true, true, true, true }, 10);
        Check("Світло: коли світел менше за бюджет, не гасне жодне", Array.TrueForAll(allLit, on => on));

        List<float> frames = new List<float>();
        for (int i = 0; i < 99; i++)
        {
            frames.Add(0.01f);
        }
        frames.Add(0.1f);
        LodRules.FrameStats(frames, out float fps, out float low, out float ms);
        Check($"Статистика кадрів: {fps:0.0} FPS, 1% найгірших {low:0.0} FPS, {ms:0.0} мс", Math.Abs(fps - 91.7f) < 0.2f && Math.Abs(low - 10f) < 0.01f && Math.Abs(ms - 10.9f) < 0.05f);
    }

    // Speed of the mod's own algorithms on a big storage and base, so a change that makes them slow fails the build.
    private static void PerformanceScenarios()
    {
        Random random = new Random(7);
        ItemTraits[] pool = { Wood, FineWood, RoundLog, ElderBark, YggdrasilWood, Blackwood, Stone, Flint, BlackMarble, Grausten, SulfurStone, Copper, TinOre, Iron,
            IronScrap, Silver, BlackMetal, FlametalOre, Carapace, Mandible, BlackCore, JuteBlue, Sap, Eitr, Softtissue, Guck, Entrails, Root, TrophyBoar, TrophyTroll,
            TrophySeeker, TrophyBlob, DeerHide, LoxPelt, ScaleHide, Feathers, Resin, Mystery };
        List<Chest> chests = new List<Chest>();
        for (int c = 0; c < 120; c++)
        {
            Chest chest = new Chest { Name = "c" + c };
            for (int k = 0; k < 8; k++)
            {
                ItemTraits traits = pool[random.Next(pool.Length)];
                if (!chest.Real.ContainsKey(traits.Id))
                {
                    chest.Add(traits, 1 + random.Next(4));
                }
            }
            if (random.Next(3) == 0)
            {
                chest.Remember(pool[random.Next(pool.Length)], random.Next(1000));
            }
            chests.Add(chest);
        }
        Best(chests, Wood);

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 400; i++)
        {
            Best(chests, pool[i % pool.Length]);
        }
        double stashMs = watch.Elapsed.TotalMilliseconds;
        Check($"Швидкодія: розкладка 400 предметів по 120 сундуках — {stashMs:0.0} мс (межа 250)", stashMs < 250);

        List<ShelfPlanner.Entry> entries = new List<ShelfPlanner.Entry>();
        for (int i = 0; i < 300; i++)
        {
            entries.Add(new ShelfPlanner.Entry
            {
                Traits = new ItemTraits { Id = "item" + i, Group = i % 3 == 0 ? null : pool[i % pool.Length].Group, Biome = i % 9 },
                Slots = 1 + i % 3,
                HasHome = i % 10 == 0,
                Home = new Spot(i % 40, 0f, i % 7),
            });
        }
        List<ShelfPlanner.EmptyChest> empty = new List<ShelfPlanner.EmptyChest>();
        for (int i = 0; i < 150; i++)
        {
            empty.Add(new ShelfPlanner.EmptyChest { Slots = 18, Position = new Spot(random.Next(60), random.Next(3), random.Next(60)) });
        }
        watch.Restart();
        List<ShelfPlanner.Placement> placements = ShelfPlanner.Plan(entries, empty, new Spot(0f, 0f, 0f));
        double shelfMs = watch.Elapsed.TotalMilliseconds;
        int slots = 0;
        foreach (ShelfPlanner.Placement placement in placements)
        {
            slots += placement.Slots;
        }
        Check($"Швидкодія: 300 нових предметів на 150 порожніх сундуків — {shelfMs:0.0} мс (межа 200), розкладено слотів {slots}", shelfMs < 200 && slots == 600);

        watch.Restart();
        Dictionary<string, long> memory = new Dictionary<string, long>();
        for (int round = 0; round < 150; round++)
        {
            ChestMemoryRules.Remember(memory, new[] { "item" + round % 60 }, round);
            memory = ChestMemoryRules.Parse(ChestMemoryRules.Format(memory));
        }
        double memoryMs = watch.Elapsed.TotalMilliseconds;
        Check($"Швидкодія: 150 записів пам'яті сундука — {memoryMs:0.0} мс (межа 100)", memoryMs < 100);

        float tan = (float)Math.Tan(32.5 * Math.PI / 180.0);
        bool[] hidden = new bool[20000];
        bool[] shadowless = new bool[20000];
        watch.Restart();
        for (int frame = 0; frame < 12; frame++)
        {
            for (int i = 0; i < hidden.Length; i++)
            {
                float distance = 5f + (i * 37 + frame * 11) % 300;
                float px = LodRules.ProjectedPixels(0.2f + i % 5 * 0.4f, distance, 1080f, tan);
                hidden[i] = LodRules.Below(hidden[i], px, 2.5f);
                shadowless[i] = LodRules.Below(shadowless[i], px, 14f);
            }
        }
        double lodMs = watch.Elapsed.TotalMilliseconds / 12.0;
        Check($"Швидкодія: правила шару для 20 000 деталей — {lodMs:0.00} мс за повний обхід (межа 5; у грі обхід розкладено на ~12 кадрів)", lodMs < 5);

        List<float> scores = new List<float>();
        List<bool> had = new List<bool>();
        for (int i = 0; i < 400; i++)
        {
            scores.Add((i * 7919 % 100) / 50f);
            had.Add(i % 5 == 0);
        }
        watch.Restart();
        for (int i = 0; i < 20; i++)
        {
            LodRules.ShadowBudget(scores, had, 4);
        }
        double budgetMs = watch.Elapsed.TotalMilliseconds / 20.0;
        Check($"Швидкодія: вибір тіней серед 400 світел — {budgetMs:0.00} мс (межа 5, раз на 0,5 с)", budgetMs < 5);
    }

    private static void SketchScenarios()
    {
        List<P2> straight = new List<P2>();
        for (int i = 0; i <= 100; i++)
        {
            straight.Add(new P2(i, 0.01f * (i % 2)));
        }
        Check("Малюнок: пряма з 101 точки спрощується до 2", SketchRules.Simplify(straight, 0.5f).Count == 2);
        List<P2> corner = new List<P2>();
        for (int i = 0; i <= 50; i++)
        {
            corner.Add(new P2(i, 0f));
        }
        for (int i = 1; i <= 50; i++)
        {
            corner.Add(new P2(50f, i));
        }
        List<P2> simpleCorner = SketchRules.Simplify(corner, 0.5f);
        Check("Малюнок: кут лишається кутом", simpleCorner.Count == 3 && Math.Abs(simpleCorner[1].X - 50f) < 0.01f && Math.Abs(simpleCorner[1].Y) < 0.01f);

        SketchStroke line = new SketchStroke { Id = 1, Width = 4f };
        line.Points.Add(new P2(0f, 0f));
        line.Points.Add(new P2(10f, 0f));
        Check("Гумка: торкається лінії поруч", SketchRules.Touches(line, new P2(5f, 1.5f), 2f));
        Check("Гумка: не чіпає далеку лінію", !SketchRules.Touches(line, new P2(5f, 3f), 2f));
        Check("Гумка: кінець лінії рахується", SketchRules.Touches(line, new P2(11f, 0f), 1.5f));
        Check("Малюнок: лінія поза видимою частиною карти пропускається", !SketchRules.Intersects(line, 20f, -5f, 30f, 5f, 1f)
            && SketchRules.Intersects(line, 9f, -5f, 30f, 5f, 0f) && SketchRules.Intersects(line, 10.5f, -5f, 30f, 5f, 1f));

        P2 a = new P2(-10f, 5f);
        P2 b = new P2(20f, 5f);
        Check("Обрізання: лінія через карту обрізається по краях", SketchRules.Clip(ref a, ref b, 0f, 0f, 10f, 10f) && Math.Abs(a.X) < 0.01f && Math.Abs(b.X - 10f) < 0.01f);
        P2 c = new P2(-10f, -5f);
        P2 d = new P2(-1f, -1f);
        Check("Обрізання: лінія поза картою не малюється", !SketchRules.Clip(ref c, ref d, 0f, 0f, 10f, 10f));
        P2 e = new P2(2f, 3f);
        P2 f = new P2(4f, 6f);
        Check("Обрізання: лінія всередині не змінюється", SketchRules.Clip(ref e, ref f, 0f, 0f, 10f, 10f) && e.X == 2f && f.Y == 6f);

        SketchStroke stroke = new SketchStroke { Id = 123456789012345, Owner = -42, OwnerName = "Кір|ушка;", Color = 3, Width = 5f };
        stroke.Points.Add(new P2(-1234.56f, 987.61f));
        stroke.Points.Add(new P2(10f, 20f));
        SketchStroke parsed = SketchRules.Parse(SketchRules.Format(stroke));
        Check("Малюнок: запис і читання без втрат", parsed != null && parsed.Id == stroke.Id && parsed.Owner == -42 && parsed.OwnerName == "Кірушка"
            && parsed.Color == 3 && parsed.Points.Count == 2 && Math.Abs(parsed.Points[0].X + 1234.6f) < 0.06f && Math.Abs(parsed.Points[1].Y - 20f) < 0.01f);
        Check("Малюнок: сміття не читається", SketchRules.Parse("abc|1|x|2|3|") == null && SketchRules.Parse("") == null && SketchRules.Parse("1|2|n|0|4|NaN,1") == null);
        SketchStroke clamped = SketchRules.Parse("1|2|n|99|500|1,1;2,2");
        Check("Малюнок: колір і товщина обмежуються", clamped != null && clamped.Color == SketchRules.Palette.Length - 1 && clamped.Width == SketchRules.MaxWidth);

        List<P2> scribble = new List<P2>();
        Random random = new Random(3);
        for (int i = 0; i < SketchRules.MaxPoints; i++)
        {
            scribble.Add(new P2(i * 0.5f, (float)random.NextDouble() * 3f));
        }
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        SketchRules.Simplify(scribble, 0.5f);
        double simplifyMs = watch.Elapsed.TotalMilliseconds;
        Check($"Швидкодія: спрощення лінії з {SketchRules.MaxPoints} точок — {simplifyMs:0.0} мс (межа 50)", simplifyMs < 50);

        List<SketchStroke> strokes = new List<SketchStroke>();
        for (int s = 0; s < 3000; s++)
        {
            SketchStroke many = new SketchStroke { Id = s };
            for (int i = 0; i < 50; i++)
            {
                many.Points.Add(new P2(s * 3f + i, (float)random.NextDouble() * 50f));
            }
            strokes.Add(many);
        }
        watch.Restart();
        int touched = 0;
        foreach (SketchStroke many in strokes)
        {
            touched += SketchRules.Touches(many, new P2(4500f, 25f), 5f) ? 1 : 0;
        }
        double eraseMs = watch.Elapsed.TotalMilliseconds;
        Check($"Швидкодія: гумка по 3000 лініях — {eraseMs:0.0} мс на кадр (межа 15), зачепила {touched}", eraseMs < 15 && touched > 0);
    }

    private static int Main()
    {
        List<Chest> storage = Storage();
        Expect("Предмет іде туди, де вже лежить", storage, IronScrap, "Мідь/залізо");
        Expect("Чорний мармур до Туманок, а не до звичайного каменю", storage, BlackMarble, "Туманки");
        Expect("Сік до Туманок", storage, Sap, "Туманки");
        Expect("Очищений ейтир до Туманок", storage, Eitr, "Туманки");
        Expect("М'які тканини до Туманок", storage, Softtissue, "Туманки");
        Expect("Олов'яна руда до міді/заліза", storage, TinOre, "Мідь/залізо");
        Expect("Полум'яна руда до пізнішого металу", storage, FlametalOre, "Чорний метал/срібло");
        Expect("Чорне дерево до найближчої за тиром деревини", storage, Blackwood, "Іґдрасілева");
        Expect("Трофей шукача до трофеїв, а не до Туманок", storage, TrophySeeker, "Трофеї");
        Expect("Трофей слизня до трофеїв, а не до Болота", storage, TrophyBlob, "Трофеї");
        Expect("Луската шкура до шкір", storage, ScaleHide, "Шкіра");
        Expect("Корінь до Болота", storage, Root, "Болото");
        Expect("Пір'я не йде в сундук деревини", storage, Feathers, null);
        Expect("Невідомий предмет лишається в інвентарі", storage, Mystery, null);

        List<Chest> withMarble = Storage();
        withMarble.Add(new Chest { Name = "Мрамор та сірокамінь" }.Add(BlackMarble, 2).Add(Grausten, 2));
        Expect("Мармур іде туди, де вже лежить мармур", withMarble, BlackMarble, "Мрамор та сірокамінь");
        Expect("Сірчаний камінь до мармуру та сірокаменю", withMarble, SulfurStone, "Мрамор та сірокамінь");

        List<Chest> noMist = Storage();
        noMist.RemoveAll(c => c.Name == "Туманки");
        Expect("Без Туманок чорний мармур іде до каменю", noMist, BlackMarble, "Камінь");

        List<Chest> meadows = Storage();
        meadows.Add(new Chest { Name = "Луки" }.Add(Resin, 2).Add(Flint, 1).Add(TrophyBoar, 1));
        Expect("Пір'я до сундука Лук, коли такий є", meadows, Feathers, "Луки");

        List<Chest> unknownInside = Storage();
        unknownInside.RemoveAll(c => c.Name == "Туманки");
        unknownInside.Add(new Chest { Name = "Туманки з невідомими" }.Add(Carapace, 3).Add(Mystery, 4).Add(BlackCore, 1));
        Expect("Невідомі предмети в сундуку біома не заважають", unknownInside, Softtissue, "Туманки з невідомими");

        List<Chest> tagged = Storage();
        tagged.Add(new Chest { Name = "Мітка: Туманні землі", TagBiome = 5 });
        Expect("Сундук з міткою біома бере свій біом", tagged, Sap, "Мітка: Туманні землі");
        Expect("Сундук з міткою не бере чуже", tagged, Root, "Болото");

        Check("Переповнене не розсипається в інші сундуки",
            SortRules.Score(Stone, storage[4].Kinds, null, -1, false, 0, 300, anyChestHasItem: true) == 0f);

        SortRules.CoUse = (a, b) => (a == "Sap" && b == "Carapace") || (a == "Carapace" && b == "Sap") ? 1f : 0f;
        Check("Спільні рецепти посилюють зв'язок", SortRules.Relation(Sap, Carapace, out _) > SortRules.Relation(Sap, Mandible, out _));

        MemoryScenarios();
        OwnKindScenarios();
        AshlandsScenarios();
        MemoryFormatScenarios();
        ShelfScenarios();
        LodScenarios();
        PerformanceScenarios();
        SketchScenarios();
        WikiTests.Run(Check);
        LangTests.Run(Check);
        // the converter inside the mod must make exactly what the pack was built with
        if (!SortTests.ConvertTests.Run(System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../wiki-localizer/data/modpack"))))
        {
            s_failed++;
        }

        Console.WriteLine(s_failed == 0 ? "Усі перевірки пройдено." : $"Не пройдено: {s_failed}");
        return s_failed == 0 ? 0 : 1;
    }
}
