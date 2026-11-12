using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ValheimHelper
{
    // Exports what the game itself knows about its content into ValheimHelper/wiki/game_data.json, for the wiki
    // articles written from the game's own numbers: every item with its stats per quality, every recipe, every
    // building piece with its cost and station, every creature with its health, resistances, drops and where it
    // spawns, every place of the world generator, every loot table (chests, things that drop items when broken),
    // every status effect. Names are the game's localization keys ($item_…); glossary_game.tsv, written
    // by the same button, turns them into the official English and Ukrainian names.
    // The fields are read by reflection, so what a later game version adds is exported without a new mod build.
    // Only reads; nothing in the game changes.
    internal static class GameDataExport
    {
        internal static string FilePath => Path.Combine(Path.Combine(Loader.Dir ?? ".", "wiki"), "game_data.json");

        private static readonly HashSet<string> s_attackFields = new HashSet<string>
        {
            "m_attackType", "m_attackStamina", "m_attackEitr", "m_attackHealth", "m_attackHealthPercentage",
            "m_damageMultiplier", "m_forceMultiplier", "m_staggerMultiplier", "m_attackRange", "m_attackAngle",
            "m_attackAdrenaline", "m_attackUseAdrenaline", "m_drawDurationMin", "m_projectileVel", "m_projectiles",
        };

        // The one button of the guide: the names (glossary), the data and the pictures of creatures and buildings,
        // all needed to write an article.
        internal static string ExportAll()
        {
            string glossary = GameGlossary.Export();
            string data = Export();
#if EDITION_FULL
            return glossary + "\n" + data + "\n" + WikiStudio.StartModels();
#else
            return glossary + "\n" + data;
#endif
        }

        internal static string Export()
        {
            ObjectDB db = ObjectDB.instance;
            ZNetScene scene = ZNetScene.instance;
            if (db == null || scene == null || db.m_items == null || db.m_items.Count == 0)
            {
                return Lang.T("Дані гри доступні лише у світі: зайди у світ і натисни ще раз.");
            }
            try
            {
                JObject root = new JObject
                {
                    ["game"] = global::Version.GetVersionString(),
                    ["exported"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["items"] = Items(db),
                    ["recipes"] = Recipes(db),
                    ["pieces"] = Pieces(db),
                    ["creatures"] = Creatures(scene),
                    ["spawns"] = Spawns(),
                    ["locations"] = Locations(),
                    ["loot"] = Loot(scene),
                    ["statusEffects"] = StatusEffects(db),
                };
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, root.ToString(Formatting.Indented), new UTF8Encoding(false));
                int icons = GameIconExport.Export(db);
                string counts = $"{((JArray)root["items"]).Count} / {((JArray)root["recipes"]).Count} / {((JArray)root["pieces"]).Count} / "
                    + $"{((JArray)root["creatures"]).Count} / {((JArray)root["spawns"]).Count} / {((JArray)root["locations"]).Count} / "
                    + $"{((JArray)root["loot"]).Count} / {((JArray)root["statusEffects"]).Count} / {icons}";
                Log.Info("Game data exported (items / recipes / pieces / creatures / spawns / locations / loot / effects / icons): " + counts);
                return Lang.F("Дані гри збережено (предмети / рецепти / будівлі / істоти / появи / місця / здобич / ефекти / іконки: {0}). Файл: {1}", counts, FilePath);
            }
            catch (Exception e)
            {
                Log.Error(e);
                return Lang.T("Не вдалося вивантажити дані гри, деталі у ValheimHelper.log");
            }
        }

        // ---------------------------------------------------------------- content

        private static JArray Items(ObjectDB db)
        {
            JArray items = new JArray();
            foreach (GameObject prefab in db.m_items)
            {
                ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (drop == null || drop.m_itemData?.m_shared == null)
                {
                    continue;
                }
                JObject item = new JObject { ["prefab"] = prefab.name };
                Fields(drop.m_itemData.m_shared, item);
                items.Add(item);
            }
            return items;
        }

        private static JArray Recipes(ObjectDB db)
        {
            JArray recipes = new JArray();
            foreach (Recipe recipe in db.m_recipes)
            {
                if (recipe == null || recipe.m_item == null)
                {
                    continue;
                }
                JObject row = new JObject
                {
                    ["item"] = recipe.m_item.gameObject.name,
                    ["recipe"] = recipe.name,
                    ["amount"] = recipe.m_amount,
                    ["enabled"] = recipe.m_enabled,
                    ["station"] = recipe.m_craftingStation != null ? recipe.m_craftingStation.m_name : null,
                    ["repairStation"] = recipe.m_repairStation != null ? recipe.m_repairStation.m_name : null,
                    ["minStationLevel"] = recipe.m_minStationLevel,
                    ["qualityResultAmountMultiplier"] = recipe.m_qualityResultAmountMultiplier,
                    ["requireOnlyOneIngredient"] = recipe.m_requireOnlyOneIngredient,
                    // the recipe only upgrades an item one already has, it does not make a new one
                    ["noCraftOnlyUpgrade"] = recipe.m_noCraftOnlyUpgrade,
                    ["resources"] = Requirements(recipe.m_resources),
                };
                recipes.Add(row);
            }
            return recipes;
        }

        private static JArray Pieces(ObjectDB db)
        {
            JArray pieces = new JArray();
            HashSet<string> seen = new HashSet<string>();
            foreach (GameObject prefab in db.m_items)
            {
                PieceTable table = prefab != null ? prefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces : null;
                if (table == null || table.m_pieces == null)
                {
                    continue;
                }
                foreach (GameObject piecePrefab in table.m_pieces)
                {
                    Piece piece = piecePrefab != null ? piecePrefab.GetComponent<Piece>() : null;
                    if (piece == null || !seen.Add(piecePrefab.name))
                    {
                        continue;
                    }
                    JObject row = new JObject { ["prefab"] = piecePrefab.name, ["tool"] = prefab.name };
                    Fields(piece, row, declaredOnly: true);
                    row["craftingStation"] = piece.m_craftingStation != null ? piece.m_craftingStation.m_name : null;
                    row["resources"] = Requirements(piece.m_resources);
                    WearNTear wear = piecePrefab.GetComponent<WearNTear>();
                    if (wear != null)
                    {
                        row["health"] = wear.m_health;
                        row["material"] = wear.m_materialType.ToString();
                    }
                    CraftingStation station = piecePrefab.GetComponentInChildren<CraftingStation>(true);
                    if (station != null)
                    {
                        row["isStation"] = station.m_name;
                    }
                    StationExtension extension = piecePrefab.GetComponentInChildren<StationExtension>(true);
                    if (extension != null && extension.m_craftingStation != null)
                    {
                        row["extends"] = extension.m_craftingStation.m_name;
                    }
                    Container container = piecePrefab.GetComponentInChildren<Container>(true);
                    if (container != null)
                    {
                        row["slots"] = container.m_width * container.m_height;
                    }
                    // what a station burns and how fast it works: the frigid kiln's only input is its fuel
                    Smelter smelter = piecePrefab.GetComponentInChildren<Smelter>(true);
                    if (smelter != null)
                    {
                        row["smelter"] = new JObject
                        {
                            ["fuel"] = smelter.m_fuelItem != null ? smelter.m_fuelItem.gameObject.name : null,
                            ["maxOre"] = smelter.m_maxOre,
                            ["maxFuel"] = smelter.m_maxFuel,
                            ["fuelPerProduct"] = smelter.m_fuelPerProduct,
                            ["secPerProduct"] = smelter.m_secPerProduct,
                        };
                    }
                    CookingStation cooking = piecePrefab.GetComponentInChildren<CookingStation>(true);
                    if (cooking != null && cooking.m_useFuel && cooking.m_fuelItem != null)
                    {
                        row["cookingFuel"] = new JObject
                        {
                            ["fuel"] = cooking.m_fuelItem.gameObject.name,
                            ["maxFuel"] = cooking.m_maxFuel,
                            ["secPerFuel"] = cooking.m_secPerFuel,
                        };
                    }
                    Fermenter fermenter = piecePrefab.GetComponentInChildren<Fermenter>(true);
                    if (fermenter != null)
                    {
                        row["fermentSeconds"] = fermenter.m_fermentationDuration;
                    }
                    JArray conversions = Conversions(piecePrefab);
                    if (conversions.Count > 0)
                    {
                        row["conversions"] = conversions;
                    }
                    pieces.Add(row);
                }
            }
            return pieces;
        }

        private static JArray Creatures(ZNetScene scene)
        {
            JArray creatures = new JArray();
            foreach (GameObject prefab in scene.m_prefabs)
            {
                Character character = prefab != null ? prefab.GetComponent<Character>() : null;
                if (character == null || character is Player)
                {
                    continue;
                }
                JObject row = new JObject { ["prefab"] = prefab.name, ["kind"] = character.GetType().Name };
                Fields(character, row, declaredOnly: false, onlySimple: true);
                row["damageModifiers"] = Modifiers(character.m_damageModifiers);
                if (character is Humanoid humanoid)
                {
                    JObject gear = new JObject();
                    AddNames(gear, "default", humanoid.m_defaultItems);
                    AddNames(gear, "randomWeapon", humanoid.m_randomWeapon);
                    AddNames(gear, "randomArmor", humanoid.m_randomArmor);
                    AddNames(gear, "randomShield", humanoid.m_randomShield);
                    if (gear.Count > 0)
                    {
                        row["items"] = gear;
                    }
                }
                CharacterDrop drops = prefab.GetComponent<CharacterDrop>();
                if (drops != null && drops.m_drops != null)
                {
                    JArray list = new JArray();
                    foreach (CharacterDrop.Drop drop in drops.m_drops)
                    {
                        if (drop?.m_prefab == null)
                        {
                            continue;
                        }
                        list.Add(new JObject
                        {
                            ["item"] = drop.m_prefab.name,
                            ["min"] = drop.m_amountMin,
                            ["max"] = drop.m_amountMax,
                            ["chance"] = drop.m_chance,
                            ["onePerPlayer"] = drop.m_onePerPlayer,
                            ["levelMultiplier"] = drop.m_levelMultiplier,
                        });
                    }
                    row["drops"] = list;
                }
                Tameable tameable = prefab.GetComponent<Tameable>();
                if (tameable != null)
                {
                    row["tameable"] = new JObject
                    {
                        ["fedDuration"] = tameable.m_fedDuration,
                        ["tamingTime"] = tameable.m_tamingTime,
                        ["commandable"] = tameable.m_commandable,
                    };
                }
                // which AI it has says how it meets a player: an AnimalAI flees, a MonsterAI attacks (BaseAI.IsEnemy)
                BaseAI baseAi = prefab.GetComponent<BaseAI>();
                if (baseAi != null)
                {
                    JObject brain = new JObject { ["kind"] = baseAi.GetType().Name };
                    Fields(baseAi, brain, declaredOnly: false, onlySimple: true);
                    row["ai"] = brain;
                }
                MonsterAI ai = prefab.GetComponent<MonsterAI>();
                if (ai != null && ai.m_consumeItems != null && ai.m_consumeItems.Count > 0)
                {
                    JArray eats = new JArray();
                    foreach (ItemDrop food in ai.m_consumeItems)
                    {
                        if (food != null)
                        {
                            eats.Add(food.gameObject.name);
                        }
                    }
                    row["eats"] = eats;
                }
                creatures.Add(row);
            }
            return creatures;
        }

        private static JArray Spawns()
        {
            JArray spawns = new JArray();
            SpawnSystem system = ZoneSystem.instance != null && ZoneSystem.instance.m_zoneCtrlPrefab != null
                ? ZoneSystem.instance.m_zoneCtrlPrefab.GetComponent<SpawnSystem>()
                : null;
            if (system == null || system.m_spawnLists == null)
            {
                return spawns;
            }
            foreach (SpawnSystemList list in system.m_spawnLists)
            {
                if (list == null || list.m_spawners == null)
                {
                    continue;
                }
                foreach (SpawnSystem.SpawnData data in list.m_spawners)
                {
                    if (data?.m_prefab == null)
                    {
                        continue;
                    }
                    JObject row = new JObject { ["creature"] = data.m_prefab.name, ["list"] = list.name };
                    Fields(data, row);
                    spawns.Add(row);
                }
            }
            return spawns;
        }

        private static JArray StatusEffects(ObjectDB db)
        {
            JArray effects = new JArray();
            foreach (StatusEffect effect in db.m_StatusEffects)
            {
                if (effect == null)
                {
                    continue;
                }
                JObject row = new JObject { ["prefab"] = effect.name, ["kind"] = effect.GetType().Name };
                Fields(effect, row);
                effects.Add(row);
            }
            return effects;
        }

        // Every place the world generator puts: which biome, how many, how high, how far from the centre.
        private static JArray Locations()
        {
            JArray locations = new JArray();
            if (ZoneSystem.instance == null || ZoneSystem.instance.m_locations == null)
            {
                return locations;
            }
            foreach (ZoneSystem.ZoneLocation location in ZoneSystem.instance.m_locations)
            {
                if (location == null || string.IsNullOrEmpty(location.m_prefabName))
                {
                    continue;
                }
                JObject row = new JObject();
                Fields(location, row, onlySimple: true);
                locations.Add(row);
            }
            return locations;
        }

        // Every loot table of the game: chests, what a thing drops when destroyed, loot spawners. Found by the field's
        // type, so a table on a component this code does not know is exported too.
        private static JArray Loot(ZNetScene scene)
        {
            JArray loot = new JArray();
            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (prefab == null)
                {
                    continue;
                }
                foreach (MonoBehaviour component in prefab.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (component == null)
                    {
                        continue;
                    }
                    foreach (FieldInfo field in component.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (field.FieldType != typeof(DropTable) || !(field.GetValue(component) is DropTable table) || table.m_drops == null)
                        {
                            continue;
                        }
                        JArray drops = new JArray();
                        foreach (DropTable.DropData drop in table.m_drops)
                        {
                            if (drop.m_item != null)
                            {
                                drops.Add(new JObject
                                {
                                    ["item"] = drop.m_item.name,
                                    ["stackMin"] = drop.m_stackMin,
                                    ["stackMax"] = drop.m_stackMax,
                                    ["weight"] = drop.m_weight,
                                });
                            }
                        }
                        if (drops.Count == 0)
                        {
                            continue;
                        }
                        loot.Add(new JObject
                        {
                            ["prefab"] = prefab.name,
                            ["component"] = component.GetType().Name,
                            ["field"] = field.Name,
                            ["dropMin"] = table.m_dropMin,
                            ["dropMax"] = table.m_dropMax,
                            ["dropChance"] = table.m_dropChance,
                            ["oneOfEach"] = table.m_oneOfEach,
                            ["drops"] = drops,
                        });
                    }
                }
            }
            return loot;
        }

        // ---------------------------------------------------------------- pieces of the data

        private static JArray Requirements(Piece.Requirement[] requirements)
        {
            JArray list = new JArray();
            if (requirements == null)
            {
                return list;
            }
            foreach (Piece.Requirement requirement in requirements)
            {
                if (requirement?.m_resItem == null)
                {
                    continue;
                }
                list.Add(new JObject
                {
                    ["item"] = requirement.m_resItem.gameObject.name,
                    ["amount"] = requirement.m_amount,
                    ["amountPerLevel"] = requirement.m_amountPerLevel,
                    ["extraAmountOnlyOneIngredient"] = requirement.m_extraAmountOnlyOneIngredient,
                    ["recover"] = requirement.m_recover,
                    // an idol at the Forge of Potential: the only thing spent there
                    ["upgraderResource"] = requirement.m_upgraderResource,
                });
            }
            return list;
        }

        // What a smelter, a cooking station or a fermenter turns into what (read by name, the same for all three).
        private static JArray Conversions(GameObject prefab)
        {
            JArray list = new JArray();
            foreach (MonoBehaviour component in prefab.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!(component is Smelter) && !(component is CookingStation) && !(component is Fermenter))
                {
                    continue;
                }
                FieldInfo field = component.GetType().GetField("m_conversion", BindingFlags.Public | BindingFlags.Instance);
                if (!(field?.GetValue(component) is IList conversions))
                {
                    continue;
                }
                foreach (object conversion in conversions)
                {
                    if (conversion == null)
                    {
                        continue;
                    }
                    JObject row = new JObject { ["station"] = component.GetType().Name };
                    Fields(conversion, row);
                    list.Add(row);
                }
            }
            return list;
        }

        private static void AddNames(JObject into, string key, GameObject[] prefabs)
        {
            if (prefabs == null || prefabs.Length == 0)
            {
                return;
            }
            JArray names = new JArray();
            foreach (GameObject prefab in prefabs)
            {
                if (prefab != null)
                {
                    names.Add(prefab.name);
                }
            }
            if (names.Count > 0)
            {
                into[key] = names;
            }
        }

        // ---------------------------------------------------------------- reflection

        // Every public field of an object that means something outside the game: numbers, flags, names, damage
        // tables, and the names of what it refers to. Pictures, sounds, effects and scene objects are left out.
        private static void Fields(object source, JObject into, bool declaredOnly = false, bool onlySimple = false)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | (declaredOnly ? BindingFlags.DeclaredOnly : 0);
            foreach (FieldInfo field in source.GetType().GetFields(flags))
            {
                if (field.IsDefined(typeof(NonSerializedAttribute), false) || into.ContainsKey(field.Name))
                {
                    continue;
                }
                object value;
                try
                {
                    value = field.GetValue(source);
                }
                catch (Exception)
                {
                    continue;
                }
                JToken token = onlySimple ? Simple(value) : Value(field.Name, value);
                if (token != null)
                {
                    into[field.Name] = token;
                }
            }
        }

        private static JToken Simple(object value)
        {
            switch (value)
            {
                case null:
                    return null;
                case string text:
                    return text.Length > 0 ? new JValue(text) : null;
                case bool flag:
                    return new JValue(flag);
                case float number:
                    return float.IsNaN(number) || float.IsInfinity(number) ? null : new JValue(number);
                case double number:
                    return new JValue(number);
                case int or short or long or byte:
                    return new JValue(Convert.ToInt64(value));
                case Enum:
                    return new JValue(value.ToString());
                default:
                    return null;
            }
        }

        private static JToken Value(string name, object value)
        {
            JToken simple = Simple(value);
            if (simple != null || value == null)
            {
                return simple;
            }
            switch (value)
            {
                case HitData.DamageTypes damages:
                    return Damages(damages);
                case HitData.DamageModifiers modifiers:
                    return Modifiers(modifiers);
                case StatusEffect effect:
                    return new JObject { ["prefab"] = effect.name, ["name"] = effect.m_name, ["tooltip"] = effect.m_tooltip };
                case ItemDrop item:
                    return item.gameObject.name;
                case GameObject gameObject:
                    return name == "m_spawnOnHit" || name == "m_prefab" ? gameObject.name : null;
                case PieceTable table:
                    return table.name;
                case Attack attack:
                    JObject row = new JObject();
                    foreach (FieldInfo field in typeof(Attack).GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (s_attackFields.Contains(field.Name))
                        {
                            JToken token = Simple(field.GetValue(attack));
                            if (token != null)
                            {
                                row[field.Name] = token;
                            }
                        }
                    }
                    return row;
                case IList list when name == "m_damageModifiers" || name == "m_mods":
                    return ModPairs(list);
                case IList list when list.Count > 0 && list[0] is string:
                    return new JArray(list);
                default:
                    return null;
            }
        }

        private static JObject Damages(HitData.DamageTypes damages)
        {
            JObject row = new JObject();
            object boxed = damages;
            foreach (FieldInfo field in typeof(HitData.DamageTypes).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.FieldType == typeof(float) && (float)field.GetValue(boxed) != 0f)
                {
                    row[field.Name] = (float)field.GetValue(boxed);
                }
            }
            return row;
        }

        private static JObject Modifiers(HitData.DamageModifiers modifiers)
        {
            JObject row = new JObject();
            object boxed = modifiers;
            foreach (FieldInfo field in typeof(HitData.DamageModifiers).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object value = field.GetValue(boxed);
                if (value is Enum && value.ToString() != "Normal")
                {
                    row[field.Name] = value.ToString();
                }
            }
            return row;
        }

        // [(slash, resistant), (fire, weak)] → {"slash": "Resistant", "fire": "Weak"}
        private static JObject ModPairs(IList pairs)
        {
            JObject row = new JObject();
            foreach (object pair in pairs)
            {
                if (pair == null)
                {
                    continue;
                }
                Type type = pair.GetType();
                object damage = type.GetField("m_type")?.GetValue(pair);
                object modifier = type.GetField("m_modifier")?.GetValue(pair);
                if (damage != null && modifier != null)
                {
                    row[damage.ToString()] = modifier.ToString();
                }
            }
            return row;
        }
    }
}
