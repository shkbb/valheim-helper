using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimHelper
{
    // Creates creatures the way the world itself does: an Instantiate of the network prefab. Unlike the console
    // "spawn" command it does not set the ZDO "cheated" flag, so their drops, trophies and kills count as normal.
    internal static class Spawner
    {
        internal sealed class CreatureEntry
        {
            public string Prefab;
            public string Name;
            public string SearchKey;
            public bool IsBoss;
            public bool Tameable;
            // Untranslated test or unused creature left in the game data.
            public bool IsDev;
            public Texture2D IconTexture;
            public Rect IconUv;
        }

        private const float SpawnDistance = 4f;
        private const float SpreadRadius = 3f;

        private static readonly List<ZNetView> s_spawned = new List<ZNetView>();

        internal static List<CreatureEntry> BuildList()
        {
            List<CreatureEntry> result = new List<CreatureEntry>();
            foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
            {
                if (prefab == null)
                {
                    continue;
                }
                Character character = prefab.GetComponent<Character>();
                if (character == null || prefab.GetComponent<Player>() != null)
                {
                    continue;
                }
                string name = string.IsNullOrEmpty(character.m_name) ? "" : Localization.instance.Localize(character.m_name);
                bool dev = name.Length == 0 || name.StartsWith("[") || name.StartsWith("$");
                CreatureEntry entry = new CreatureEntry
                {
                    Prefab = prefab.name,
                    Name = dev ? prefab.name : name,
                    IsBoss = character.m_boss,
                    Tameable = prefab.GetComponent<Tameable>() != null,
                    IsDev = dev,
                };
                entry.SearchKey = (entry.Name + " " + prefab.name).ToLowerInvariant();
                SetTrophyIcon(entry, prefab);
                result.Add(entry);
            }
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            Log.Info($"Creature list built: {result.Count} creatures");
            return result;
        }

        // The creature's trophy is the most recognisable icon the game has for it.
        private static void SetTrophyIcon(CreatureEntry entry, GameObject prefab)
        {
            CharacterDrop drops = prefab.GetComponent<CharacterDrop>();
            if (drops == null || drops.m_drops == null)
            {
                return;
            }
            foreach (CharacterDrop.Drop drop in drops.m_drops)
            {
                if (drop == null || drop.m_prefab == null || !drop.m_prefab.name.StartsWith("Trophy"))
                {
                    continue;
                }
                ItemDrop item = drop.m_prefab.GetComponent<ItemDrop>();
                Sprite[] icons = item != null && item.m_itemData != null && item.m_itemData.m_shared != null ? item.m_itemData.m_shared.m_icons : null;
                if (icons == null || icons.Length == 0 || icons[0] == null || icons[0].texture == null)
                {
                    continue;
                }
                Texture2D texture = icons[0].texture;
                Rect rect = icons[0].textureRect;
                entry.IconTexture = texture;
                entry.IconUv = new Rect(rect.x / texture.width, rect.y / texture.height, rect.width / texture.width, rect.height / texture.height);
                return;
            }
        }

        // level: 1 = no stars, 2 = one star, ... Returns how many creatures were created.
        internal static int Spawn(CreatureEntry entry, int count, int level, bool tamed)
        {
            Player player = Player.m_localPlayer;
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(entry.Prefab) : null;
            if (player == null || prefab == null)
            {
                return 0;
            }
            Vector3 forward = player.transform.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 0.001f ? forward.normalized : Vector3.forward;
            Vector3 center = player.transform.position + forward * SpawnDistance + Vector3.up;
            Quaternion rotation = Quaternion.LookRotation(-forward);
            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                Vector3 offset = count == 1 ? Vector3.zero : UnityEngine.Random.insideUnitSphere * SpreadRadius;
                offset.y = 0f;
                GameObject instance = UnityEngine.Object.Instantiate(prefab, center + offset, rotation);
                Character character = instance.GetComponent<Character>();
                ZNetView view = instance.GetComponent<ZNetView>();
                if (character == null || view == null || !view.IsValid())
                {
                    continue;
                }
                if (level > 1)
                {
                    character.SetLevel(level);
                }
                if (tamed)
                {
                    MonsterAI ai = instance.GetComponent<MonsterAI>();
                    if (ai != null)
                    {
                        ai.MakeTame();
                    }
                    else
                    {
                        character.SetTamed(true);
                    }
                }
                s_spawned.Add(view);
                spawned++;
            }
            return spawned;
        }

        internal static int SpawnedAlive()
        {
            s_spawned.RemoveAll(view => view == null || !view.IsValid());
            return s_spawned.Count;
        }

        internal static int RemoveSpawned()
        {
            int removed = 0;
            foreach (ZNetView view in s_spawned)
            {
                if (view == null || !view.IsValid())
                {
                    continue;
                }
                if (!view.IsOwner())
                {
                    view.ClaimOwnership();
                }
                ZNetScene.instance.Destroy(view.gameObject);
                removed++;
            }
            s_spawned.Clear();
            return removed;
        }
    }
}
