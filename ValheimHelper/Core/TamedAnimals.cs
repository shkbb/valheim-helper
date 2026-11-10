using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper
{
    // Tamed animals of the world, read from their ZDOs (the same data the game keeps for them): name, kind, stars,
    // health, hunger, who they follow, pregnancy. The host holds every ZDO of the world and sees all of them;
    // a friend sees the ones around him. Only reads; nothing about the animals is changed.
    internal static class TamedAnimals
    {
        internal sealed class Animal
        {
            public ZDOID Id;
            public string Prefab;
            public string Kind;
            public string Name;
            public int Stars;
            public float Health;
            public float MaxHealth;
            public Vector3 Pos;
            public bool Hungry;
            public bool Eats;
            public bool Pregnant;
            // Skeletons and the like called by a staff: the game counts them as tamed, but they are not pets.
            public bool Summon;
            public string Following;
            public Texture2D Icon;
            public Rect IconUv;

            public string Title => string.IsNullOrEmpty(Name) ? Kind : $"{Name} ({Kind})";
        }

        private sealed class KindInfo
        {
            public string Prefab;
            public string Name;
            public float FedDuration;
            // Summoned skeletons and the like eat nothing, so they are never hungry.
            public bool Eats;
            public bool Summon;
            public float BaseHealth;
            public Texture2D Icon;
            public Rect IconUv;
        }

        private const int SectorsPerFrame = 8000;
        private const float RescanSec = 5f;

        private static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> s_sectors = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");

        internal static List<Animal> Animals = new List<Animal>();
        internal static int Version;
        // Set by whoever shows the animals (the menu page, the map layer); the scan runs only while someone looks.
        internal static float WantedUntil;

        private static Dictionary<int, KindInfo> s_kinds;
        private static ZNetScene s_kindsScene;
        private static List<Animal> s_pass;
        private static int s_cursor;
        private static float s_passDoneAt = -1000f;
        private static ZDOMan s_zdoMan;

        internal static void Want()
        {
            WantedUntil = Time.unscaledTime + 1f;
        }

        internal static void Tick()
        {
            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null || ZNet.instance == null || !EnsureKinds())
            {
                if (Animals.Count > 0)
                {
                    Animals = new List<Animal>();
                    Version++;
                }
                return;
            }
            if (!ReferenceEquals(zdoMan, s_zdoMan))
            {
                s_zdoMan = zdoMan;
                s_pass = null;
                s_passDoneAt = -1000f;
                Animals = new List<Animal>();
                Version++;
            }
            if (s_pass == null)
            {
                if (Time.unscaledTime > WantedUntil || Time.unscaledTime - s_passDoneAt < RescanSec)
                {
                    return;
                }
                s_pass = new List<Animal>();
                s_cursor = 0;
            }
            List<ZDO>[] sectors = s_sectors(zdoMan);
            if (sectors == null)
            {
                return;
            }
            DateTime now = ZNet.instance.GetTime();
            int end = Mathf.Min(sectors.Length, s_cursor + SectorsPerFrame);
            for (; s_cursor < end; s_cursor++)
            {
                List<ZDO> zdos = sectors[s_cursor];
                if (zdos == null)
                {
                    continue;
                }
                foreach (ZDO zdo in zdos)
                {
                    if (zdo == null || !s_kinds.TryGetValue(zdo.GetPrefab(), out KindInfo kind) || !zdo.GetBool(ZDOVars.s_tamed))
                    {
                        continue;
                    }
                    s_pass.Add(Read(zdo, kind, now));
                }
            }
            if (s_cursor >= sectors.Length)
            {
                Animals = s_pass;
                s_pass = null;
                s_passDoneAt = Time.unscaledTime;
                Version++;
            }
        }

        private static Animal Read(ZDO zdo, KindInfo kind, DateTime now)
        {
            int level = Mathf.Max(1, zdo.GetInt(ZDOVars.s_level, 1));
            float max = zdo.GetFloat(ZDOVars.s_maxHealth, kind.BaseHealth * level);
            long fed = zdo.GetLong(ZDOVars.s_tameLastFeeding, 0L);
            return new Animal
            {
                Id = zdo.m_uid,
                Prefab = kind.Prefab,
                Kind = kind.Name,
                Name = zdo.GetString(ZDOVars.s_tamedName, ""),
                Stars = level - 1,
                MaxHealth = max,
                Health = zdo.GetFloat(ZDOVars.s_health, max),
                Pos = zdo.GetPosition(),
                Hungry = kind.Eats && kind.FedDuration > 0f && (now - new DateTime(fed)).TotalSeconds > kind.FedDuration,
                Eats = kind.Eats,
                Pregnant = zdo.GetLong(ZDOVars.s_pregnant, 0L) != 0L,
                Summon = kind.Summon,
                Following = zdo.GetString(ZDOVars.s_follow, ""),
                Icon = kind.Icon,
                IconUv = kind.IconUv,
            };
        }

        // Every prefab that can be tamed, with its name, hunger time and an icon (its trophy, if it drops one).
        private static bool EnsureKinds()
        {
            ZNetScene scene = ZNetScene.instance;
            if (scene == null || Localization.instance == null)
            {
                return false;
            }
            if (s_kinds != null && ReferenceEquals(scene, s_kindsScene))
            {
                return true;
            }
            Dictionary<int, KindInfo> kinds = new Dictionary<int, KindInfo>();
            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (prefab == null)
                {
                    continue;
                }
                Tameable tameable = prefab.GetComponent<Tameable>();
                Character character = prefab.GetComponent<Character>();
                if (tameable == null || character == null)
                {
                    continue;
                }
                KindInfo kind = new KindInfo
                {
                    Prefab = prefab.name,
                    Name = Localization.instance.Localize(character.m_name),
                    FedDuration = tameable.m_fedDuration,
                    Eats = prefab.GetComponent<MonsterAI>() is MonsterAI ai && ai.m_consumeItems != null && ai.m_consumeItems.Count > 0,
                    // what a staff calls up carries the game's own timer that kills it later
                    Summon = prefab.GetComponent<CharacterTimedDestruction>() != null,
                    BaseHealth = character.m_health,
                };
                CharacterDrop drops = prefab.GetComponent<CharacterDrop>();
                if (drops != null && drops.m_drops != null)
                {
                    foreach (CharacterDrop.Drop drop in drops.m_drops)
                    {
                        ItemDrop item = drop?.m_prefab != null ? drop.m_prefab.GetComponent<ItemDrop>() : null;
                        if (item != null && item.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trophy)
                        {
                            Sprite icon = item.m_itemData.GetIcon();
                            if (icon != null && icon.texture != null)
                            {
                                Rect rect = icon.textureRect;
                                kind.Icon = icon.texture;
                                kind.IconUv = new Rect(rect.x / icon.texture.width, rect.y / icon.texture.height, rect.width / icon.texture.width, rect.height / icon.texture.height);
                            }
                            break;
                        }
                    }
                }
                kinds[prefab.name.GetStableHashCode()] = kind;
            }
            s_kinds = kinds;
            s_kindsScene = scene;
            return true;
        }

        internal static string TeleportTo(Animal animal)
        {
            Player me = Player.m_localPlayer;
            if (me == null || animal == null)
            {
                return Lang.T("Зайди у світ.");
            }
            me.TeleportTo(animal.Pos + Vector3.up * 0.5f, me.transform.rotation, distantTeleport: true);
            return Lang.F("Телепортуюся до: {0}.", animal.Title);
        }
    }
}
