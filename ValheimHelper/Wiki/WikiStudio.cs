using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper
{
    // Pictures for the wiki made by the game itself, for what the wikis have no picture of yet.
    // Models: every creature (with its starred looks) and every building piece is drawn alone on a transparent
    // background — a copy of its prefab without its scripts, far above the world, lit by lights of its own, seen by a
    // camera of its own — into ValheimHelper/wiki/renders/{creatures,pieces}/<prefab>.png ("<prefab>_1star.png" for
    // the stars), and the viking's head with each hair and beard into renders/hair/<item>.png. Pictures made before are
    // kept; nothing of the world changes.
    // Places: the viking is carried to the nearest place of each kind the wiki wants (a test world is the right one
    // for it), made unhurtable and unseen meanwhile, and the free camera takes a picture of the place at noon without
    // the HUD, into renders/places/<prefab>.png; then on a meadow near the start the ground is raised, levelled, tilled
    // and sown with grass as the hoe and the cultivator do it, into renders/ground/<prefab>.png (this changes the
    // world). The viking comes back where it stood.
    internal static class WikiStudio
    {
        internal static string Folder => Path.Combine(Path.Combine(Loader.Dir ?? ".", "wiki"), "renders");

        // the places the wiki wants pictures of: wiki/places_wanted.txt (a prefab per line), or those of the Deep North
        private static readonly string[] s_defaultPlaces =
        {
            "DN_Bossroom", "TheHole01", "NorthVillage", "MorkBorg", "NorthMemorialPlace", "DN_hut01", "ShipSetting02",
            "LumberCamp", "ShipWreck01_DN", "DN_gammeltrollFrac01",
        };

        // things no tool builds that the wiki has articles about: wiki/renders_wanted.txt (a prefab per line), or these
        private static readonly string[] s_defaultThings =
        {
            "dvergrprops_barrel", "dvergrprops_crate", "dvergrprops_crate_long", "dvergrprops_crate_ashlands",
            "Catapult", "BatteringRam", "Player_tombstone", "Bush01", "RaspberryBush", "BlueberryBush",
        };

        // creatures that glow, drawn on a dark background: wiki/renders_dark.txt (a prefab per line), or these
        private static readonly string[] s_defaultDark = { "FallenWarrior" };

        // a layer the game draws nothing on: the models are drawn by our camera alone
        private const int StudioLayer = 31;
        private const int Size = 1024;
        private static readonly Vector3 s_far = new Vector3(0f, 9000f, 0f);

        private static readonly AccessTools.FieldRef<LevelEffects, Character> s_levelCharacter =
            AccessTools.FieldRefAccess<LevelEffects, Character>("m_character");

        internal static bool Busy { get; private set; }

        // ---------------------------------------------------------------- models

        internal static string StartModels()
        {
            if (Busy)
            {
                return Lang.T("Знімки для вікі вже робляться.");
            }
            if (ZNetScene.instance == null || ObjectDB.instance == null || Player.m_localPlayer == null)
            {
                return Lang.T("Знімки для вікі робляться у світі.");
            }
            Runner().StartCoroutine(Models());
            return Lang.T("Знімки істот і споруд для вікі робляться — кілька хвилин; готові лягають у wiki/renders.");
        }

        private static IEnumerator Models()
        {
            Busy = true;
            List<KeyValuePair<GameObject, string>> work = new List<KeyValuePair<GameObject, string>>();
            HashSet<string> seen = new HashSet<string>();
            foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
            {
                Character character = prefab != null ? prefab.GetComponent<Character>() : null;
                if (character != null && !(character is Player) && !string.IsNullOrEmpty(character.m_name) && seen.Add(prefab.name))
                {
                    work.Add(new KeyValuePair<GameObject, string>(prefab, "creatures"));
                }
            }
            foreach (GameObject item in ObjectDB.instance.m_items)
            {
                PieceTable table = item != null ? item.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces : null;
                if (table?.m_pieces == null)
                {
                    continue;
                }
                foreach (GameObject piece in table.m_pieces)
                {
                    if (piece != null && piece.GetComponent<Piece>() != null && seen.Add(piece.name))
                    {
                        work.Add(new KeyValuePair<GameObject, string>(piece, "pieces"));
                    }
                }
            }
            // things of the world no tool builds (the dvergr's crates, the siege machines): by their prefab name
            foreach (string name in Wanted("renders_wanted.txt", s_defaultThings))
            {
                GameObject prefab = ZNetScene.instance.GetPrefab(name);
                if (prefab != null && seen.Add(prefab.name))
                {
                    work.Add(new KeyValuePair<GameObject, string>(prefab, "pieces"));
                }
            }

            HashSet<string> dark = new HashSet<string>(Wanted("renders_dark.txt", s_defaultDark));
            Studio studio = new Studio();
            int made = 0;
            try
            {
                for (int i = 0; i < work.Count; i++)
                {
                    GameObject prefab = work[i].Key;
                    string kind = work[i].Value;
                    string dir = Path.Combine(Folder, kind);
                    LevelEffects stars = kind == "creatures" ? prefab.GetComponentInChildren<LevelEffects>(true) : null;
                    int levels = stars != null && stars.m_levelSetups != null ? Mathf.Min(3, stars.m_levelSetups.Count + 1) : 1;
                    // a creature with what it holds; one armed at random has a picture with each of its weapons
                    // ("<prefab>__<weapon>.png"), the first of them is its picture, with its stars
                    List<List<GameObject>> loadouts = kind == "creatures" ? Loadouts(prefab) : new List<List<GameObject>>();
                    int shots = Mathf.Max(1, loadouts.Count);
                    for (int loadout = 0; loadout < shots; loadout++)
                    {
                        List<GameObject> items = loadouts.Count > 0 ? loadouts[loadout] : null;
                        for (int level = 1; level <= (loadout == 0 ? levels : 1); level++)
                        {
                            string stem = Safe(prefab.name) + (level > 1 ? "_" + (level - 1) + "star" : "");
                            string file = Path.Combine(dir, stem + ".png");
                            string variant = loadouts.Count > 1 && level == 1 ? Path.Combine(dir, stem + "__" + Safe(WeaponOf(items)) + ".png") : null;
                            if (File.Exists(loadout == 0 ? file : variant) && (variant == null || File.Exists(variant)))
                            {
                                continue;
                            }
                            GameObject model = studio.Place(prefab, level, items);
                            if (model == null)
                            {
                                continue;
                            }
                            // a frame for the animator's pose and the skinned meshes
                            yield return null;
                            yield return null;
                            string target = loadout == 0 ? file : variant;
                            if (studio.Shoot(model, target, dark.Contains(prefab.name)))
                            {
                                made++;
                                if (variant != null && target != variant)
                                {
                                    File.Copy(target, variant, overwrite: true);
                                }
                            }
                            UnityEngine.Object.Destroy(model);
                        }
                    }
                    if (i % 50 == 49 && Player.m_localPlayer != null)
                    {
                        Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, Lang.F("Знімки для вікі: {0} з {1}", i + 1, work.Count));
                    }
                }
                // hair and beards: the viking's head with each of them, in the colours of this viking
                GameObject viking = ZNetScene.instance.GetPrefab("Player");
                foreach (GameObject item in viking != null ? ObjectDB.instance.m_items : new List<GameObject>())
                {
                    if (Type(item) != ItemDrop.ItemData.ItemType.Customization || Shown(item) == null)
                    {
                        continue;
                    }
                    string file = Path.Combine(Path.Combine(Folder, "hair"), Safe(item.name) + ".png");
                    if (File.Exists(file))
                    {
                        continue;
                    }
                    GameObject model = studio.Place(viking, 1, null, x => studio.Wear(x, item));
                    if (model == null)
                    {
                        continue;
                    }
                    yield return null;
                    yield return null;
                    if (studio.Shoot(model, file, false, studio.HeadFrame()))
                    {
                        made++;
                    }
                    UnityEngine.Object.Destroy(model);
                }
            }
            finally
            {
                studio.Dispose();
                Busy = false;
            }
            Log.Info($"Wiki renders made: {made}");
            if (Player.m_localPlayer != null)
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, Lang.F("Знімки для вікі готові: нових {0}. Тека: {1}", made, Folder));
            }
        }

        // A list of prefab names from a file next to the wiki data, or the given one.
        private static List<string> Wanted(string file, string[] fallback)
        {
            List<string> names = new List<string>();
            string path = Path.Combine(Path.Combine(Loader.Dir ?? ".", "wiki"), file);
            if (File.Exists(path))
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    if (line.Trim().Length > 0 && !line.StartsWith("#"))
                    {
                        names.Add(line.Trim());
                    }
                }
                return names;
            }
            names.AddRange(fallback);
            return names;
        }

        // What a humanoid can come armed with, in the order the game gives it (Humanoid.GiveDefaultItems): its default
        // items, a random shield (the first of them here), a random weapon, a random armour (the first), a random set.
        // One loadout per weapon and set that shows something else in the hands; none for a creature with nothing to hold.
        private static List<List<GameObject>> Loadouts(GameObject prefab)
        {
            List<List<GameObject>> result = new List<List<GameObject>>();
            Humanoid humanoid = prefab.GetComponent<Humanoid>();
            if (humanoid == null)
            {
                return result;
            }
            GameObject shield = humanoid.m_randomShield != null ? Array.Find(humanoid.m_randomShield, x => x != null) : null;
            GameObject armour = humanoid.m_randomArmor != null ? Array.Find(humanoid.m_randomArmor, x => x != null) : null;
            List<GameObject> weapons = humanoid.m_randomWeapon != null ? new List<GameObject>(Array.FindAll(humanoid.m_randomWeapon, x => x != null)) : new List<GameObject>();
            if (weapons.Count == 0)
            {
                weapons.Add(null);
            }
            List<GameObject[]> sets = new List<GameObject[]>();
            foreach (Humanoid.ItemSet set in humanoid.m_randomSets ?? new Humanoid.ItemSet[0])
            {
                sets.Add(Array.FindAll(set.m_items ?? new GameObject[0], x => x != null));
            }
            if (sets.Count == 0)
            {
                sets.Add(new GameObject[0]);
            }
            HashSet<string> names = new HashSet<string>();
            foreach (GameObject weapon in weapons)
            {
                foreach (GameObject[] set in sets)
                {
                    List<GameObject> loadout = new List<GameObject>();
                    if (humanoid.m_defaultItems != null)
                    {
                        loadout.AddRange(Array.FindAll(humanoid.m_defaultItems, x => x != null));
                    }
                    foreach (GameObject item in new[] { shield, weapon, armour })
                    {
                        if (item != null)
                        {
                            loadout.Add(item);
                        }
                    }
                    loadout.AddRange(set);
                    if (names.Add(WeaponOf(loadout)))
                    {
                        result.Add(loadout);
                    }
                }
            }
            // a creature whose items show nothing (its attacks) is drawn as it is
            if (!result.Exists(loadout => loadout.Exists(x => Shown(x) != null)))
            {
                result.Clear();
            }
            return result;
        }

        private enum Hold
        {
            None,
            Right,
            Left,
            Head,
            Skin,
        }

        // Where an item goes on the model, as VisEquipment puts it: weapons in the right hand, shields and bows in the
        // left, helmets on the head, what is worn over the body on the body.
        private static Hold Joint(GameObject item)
        {
            ItemDrop drop = item != null ? item.GetComponent<ItemDrop>() : null;
            if (drop?.m_itemData?.m_shared == null)
            {
                return Hold.None;
            }
            switch (drop.m_itemData.m_shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.Torch:
                case ItemDrop.ItemData.ItemType.Tool:
                    return Hold.Right;
                case ItemDrop.ItemData.ItemType.Shield:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                    return Hold.Left;
                case ItemDrop.ItemData.ItemType.Helmet:
                    return Hold.Head;
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Shoulder:
                    return Hold.Skin;
                default:
                    return Hold.None;
            }
        }

        // What of an item the model shows, as VisEquipment.AttachItem finds it: its "attach" or "attach_skin" child.
        // A creature's attacks are items too, with nothing to show.
        private static Transform Shown(GameObject item)
        {
            if (item == null)
            {
                return null;
            }
            for (int i = 0; i < item.transform.childCount; i++)
            {
                Transform child = item.transform.GetChild(i);
                if (child.name == "attach" || child.name == "attach_skin")
                {
                    return child;
                }
            }
            return null;
        }

        private static ItemDrop.ItemData.ItemType Type(GameObject item)
        {
            return item?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_itemType ?? ItemDrop.ItemData.ItemType.None;
        }

        // What a loadout shows, as Humanoid.EquipItem leaves the hands when the items are given in turn: the last
        // weapon in the right hand, a shield beside a one-handed weapon or a torch, a bow or a two-handed weapon alone;
        // and all that is worn. Items that show nothing (a creature's attacks) are passed over.
        private static List<GameObject> Held(List<GameObject> loadout)
        {
            GameObject right = null;
            GameObject left = null;
            List<GameObject> held = new List<GameObject>();
            foreach (GameObject item in loadout)
            {
                if (Shown(item) == null)
                {
                    continue;
                }
                ItemDrop.ItemData.ItemType type = Type(item);
                switch (Joint(item))
                {
                    case Hold.Right:
                        if (type == ItemDrop.ItemData.ItemType.TwoHandedWeapon || type == ItemDrop.ItemData.ItemType.Tool
                            || (left != null && Type(left) != ItemDrop.ItemData.ItemType.Shield && Type(left) != ItemDrop.ItemData.ItemType.Torch))
                        {
                            left = null;
                        }
                        right = item;
                        break;
                    case Hold.Left:
                        if (type != ItemDrop.ItemData.ItemType.Shield
                            || (right != null && Type(right) != ItemDrop.ItemData.ItemType.OneHandedWeapon && Type(right) != ItemDrop.ItemData.ItemType.Torch))
                        {
                            right = null;
                        }
                        left = item;
                        break;
                    case Hold.None:
                        break;
                    default:
                        held.Add(item);
                        break;
                }
            }
            if (left != null)
            {
                held.Insert(0, left);
            }
            if (right != null)
            {
                held.Insert(0, right);
            }
            return held;
        }

        // the name a loadout goes by: what it shows in the hands (the right hand first), or "none"
        private static string WeaponOf(List<GameObject> loadout)
        {
            GameObject any = Held(loadout).Find(x => Joint(x) == Hold.Right || Joint(x) == Hold.Left);
            return any != null ? any.name : "none";
        }

        // The camera, the lights and the picture of one model.
        private sealed class Studio : IDisposable
        {
            private readonly GameObject m_root;
            private readonly Camera m_camera;
            private readonly RenderTexture m_target;
            private readonly Light m_sun;
            private readonly int m_sunMask;

            internal Studio()
            {
                m_root = new GameObject("ValheimHelper_WikiStudio");
                m_root.transform.position = s_far;
                GameObject eye = new GameObject("camera");
                eye.transform.SetParent(m_root.transform, false);
                m_camera = eye.AddComponent<Camera>();
                m_camera.enabled = false;
                m_camera.cullingMask = 1 << StudioLayer;
                m_camera.clearFlags = CameraClearFlags.SolidColor;
                m_camera.fieldOfView = 20f;
                m_camera.nearClipPlane = 0.05f;
                m_camera.farClipPlane = 3000f;
                m_camera.allowHDR = false;
                m_target = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
                m_camera.targetTexture = m_target;
                AddLight(new Vector3(35f, -40f, 0f), 1.1f, new Color(1f, 0.97f, 0.92f));
                AddLight(new Vector3(20f, 150f, 0f), 0.45f, new Color(0.85f, 0.9f, 1f));
                // the sun and the moon of the world would light the models by the hour: not ours
                m_sun = EnvMan.instance != null ? EnvMan.instance.m_dirLight : null;
                if (m_sun != null)
                {
                    m_sunMask = m_sun.cullingMask;
                    m_sun.cullingMask &= ~(1 << StudioLayer);
                }
            }

            private void AddLight(Vector3 angles, float intensity, Color color)
            {
                GameObject go = new GameObject("light");
                go.transform.SetParent(m_root.transform, false);
                go.transform.rotation = Quaternion.Euler(angles);
                Light light = go.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = intensity;
                light.color = color;
                light.cullingMask = 1 << StudioLayer;
                light.shadows = LightShadows.None;
            }

            // A copy of the prefab that is only its looks: made inactive (no script wakes up), its stars set, its
            // scripts, joints and bodies removed, then shown on the studio's layer.
            internal GameObject Place(GameObject prefab, int level, List<GameObject> items = null, Action<GameObject> dress = null)
            {
                bool wasActive = prefab.activeSelf;
                GameObject model;
                prefab.SetActive(false);
                try
                {
                    model = UnityEngine.Object.Instantiate(prefab, s_far, Quaternion.identity);
                }
                catch (Exception e)
                {
                    Log.Warn($"Wiki render {prefab.name}: {e.Message}");
                    return null;
                }
                finally
                {
                    prefab.SetActive(wasActive);
                }
                try
                {
                    if (level > 1)
                    {
                        LevelEffects stars = model.GetComponentInChildren<LevelEffects>(true);
                        if (stars != null)
                        {
                            s_levelCharacter(stars) = model.GetComponent<Character>();
                            stars.SetupLevelVisualization(level);
                        }
                    }
                    if (items != null)
                    {
                        Equip(model, items);
                    }
                    dress?.Invoke(model);
                    Strip(model);
                    foreach (Transform t in model.GetComponentsInChildren<Transform>(true))
                    {
                        t.gameObject.layer = StudioLayer;
                    }
                    foreach (LODGroup lod in model.GetComponentsInChildren<LODGroup>(true))
                    {
                        lod.ForceLOD(0);
                    }
                    foreach (AudioSource audio in model.GetComponentsInChildren<AudioSource>(true))
                    {
                        audio.enabled = false;
                    }
                    foreach (SkinnedMeshRenderer skinned in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        skinned.updateWhenOffscreen = true;
                    }
                    foreach (Animator animator in model.GetComponentsInChildren<Animator>(true))
                    {
                        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    }
                    model.SetActive(true);
                    foreach (ParticleSystem particles in model.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        particles.Simulate(1.5f, true, true);
                    }
                    return model;
                }
                catch (Exception e)
                {
                    Log.Warn($"Wiki render {prefab.name}: {e.Message}");
                    UnityEngine.Object.Destroy(model);
                    return null;
                }
            }

            // The items in the hands and on the body, as VisEquipment.AttachItem puts them: the item's "attach" child
            // on the joint, or its "attach_skin" bound to the body's bones, keeping the size it has in the world (the
            // bones of a model are scaled), moved by its "equipoffset". Made in an inactive holder and moved into the
            // inactive model, so their scripts do not wake up either and go with the model's.
            private static void Equip(GameObject model, List<GameObject> items)
            {
                VisEquipment look = model.GetComponentInChildren<VisEquipment>(true);
                if (look == null)
                {
                    return;
                }
                GameObject holder = new GameObject("ValheimHelper_WikiHolder");
                holder.SetActive(false);
                try
                {
                    foreach (GameObject item in Held(items))
                    {
                        Transform shown = Shown(item);
                        bool skin = shown.name == "attach_skin";
                        Hold hold = Joint(item);
                        Transform joint = skin ? (look.m_bodyModel != null ? look.m_bodyModel.transform.parent : null)
                            : hold == Hold.Right ? look.m_rightHand : hold == Hold.Left ? look.m_leftHand : hold == Hold.Head ? look.m_helmet : null;
                        if (joint == null)
                        {
                            continue;
                        }
                        GameObject held = UnityEngine.Object.Instantiate(shown.gameObject, holder.transform);
                        held.SetActive(true);
                        held.transform.SetParent(joint);
                        held.transform.localPosition = Vector3.zero;
                        held.transform.localRotation = Quaternion.identity;
                        if (skin)
                        {
                            foreach (SkinnedMeshRenderer mesh in held.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                            {
                                mesh.rootBone = look.m_bodyModel.rootBone;
                                mesh.bones = look.m_bodyModel.bones;
                            }
                        }
                        Transform offset = item.transform.Find("equipoffset");
                        if (offset != null)
                        {
                            held.transform.localPosition += offset.position;
                            held.transform.localRotation *= offset.rotation;
                        }
                        foreach (Collider collider in held.GetComponentsInChildren<Collider>(true))
                        {
                            UnityEngine.Object.DestroyImmediate(collider);
                        }
                    }
                }
                finally
                {
                    UnityEngine.Object.Destroy(holder);
                }
            }

            private Transform m_head;
            private GameObject m_worn;

            // The viking with one hair or beard, as VisEquipment puts it: the man's body (beards are his), the skin of the
            // game, the hair colour of this viking, the item's "attach" on the helmet joint (or "attach_skin" bound to the
            // body's bones) with the hair's material and colour.
            internal void Wear(GameObject model, GameObject item)
            {
                m_head = null;
                m_worn = null;
                VisEquipment look = model.GetComponentInChildren<VisEquipment>(true);
                Transform shown = Shown(item);
                if (look == null || look.m_helmet == null || look.m_bodyModel == null || shown == null)
                {
                    return;
                }
                // the body's textures are given by VisEquipment.UpdateBaseModel, as its mesh
                if (look.m_models != null && look.m_models.Length > 0)
                {
                    look.m_bodyModel.sharedMesh = look.m_models[0].m_mesh;
                    Material skin = look.m_bodyModel.materials[0];
                    foreach (string texture in new[] { "_MainTex", "_SkinBumpMap", "_ChestTex", "_ChestBumpMap", "_LegsTex", "_LegsBumpMap" })
                    {
                        if (skin.HasProperty(texture) && look.m_models[0].m_baseMaterial.HasProperty(texture))
                        {
                            skin.SetTexture(texture, look.m_models[0].m_baseMaterial.GetTexture(texture));
                        }
                    }
                }
                // the skin as the game has it before one picks a colour; the hair in this viking's colour
                ZDO zdo = Player.m_localPlayer != null ? Player.m_localPlayer.GetComponent<ZNetView>()?.GetZDO() : null;
                Vector3 skinTone = Vector3.one;
                Vector3 hairTone = zdo != null ? zdo.GetVec3(ZDOVars.s_hairColor, Vector3.one) : Vector3.one;
                Color hair = new Color(hairTone.x, hairTone.y, hairTone.z);
                Material[] body = look.m_bodyModel.materials;
                body[0].SetColor("_SkinColor", new Color(skinTone.x, skinTone.y, skinTone.z));
                if (body.Length > 1)
                {
                    body[1].SetColor("_SkinColor", hair);
                }
                GameObject holder = new GameObject("ValheimHelper_WikiHolder");
                holder.SetActive(false);
                try
                {
                    // a hair bound to the body's bones ("attach_skin") goes beside the body, a rigid one on the head
                    bool bound = shown.name == "attach_skin";
                    GameObject worn = UnityEngine.Object.Instantiate(shown.gameObject, holder.transform);
                    worn.SetActive(true);
                    worn.transform.SetParent(bound ? look.m_bodyModel.transform.parent : look.m_helmet);
                    worn.transform.localPosition = Vector3.zero;
                    worn.transform.localRotation = Quaternion.identity;
                    if (bound)
                    {
                        foreach (SkinnedMeshRenderer mesh in worn.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        {
                            mesh.rootBone = look.m_bodyModel.rootBone;
                            mesh.bones = look.m_bodyModel.bones;
                            mesh.updateWhenOffscreen = true;
                        }
                    }
                    Transform offset = item.transform.Find("equipoffset");
                    if (offset != null)
                    {
                        worn.transform.localPosition += offset.position;
                        worn.transform.localRotation *= offset.rotation;
                    }
                    MaterialPropertyBlock colour = new MaterialPropertyBlock();
                    foreach (Renderer renderer in worn.GetComponentsInChildren<Renderer>(true))
                    {
                        if (look.m_overrideHairMaterial != null)
                        {
                            renderer.sharedMaterial = look.m_overrideHairMaterial;
                        }
                        renderer.GetPropertyBlock(colour);
                        colour.SetColor("_SkinColor", hair);
                        renderer.SetPropertyBlock(colour);
                    }
                    foreach (Collider collider in worn.GetComponentsInChildren<Collider>(true))
                    {
                        UnityEngine.Object.DestroyImmediate(collider);
                    }
                    m_head = look.m_helmet;
                    m_worn = worn;
                }
                finally
                {
                    UnityEngine.Object.Destroy(holder);
                }
            }

            // the head and what it wears, for the picture of a hair or a beard
            internal Bounds? HeadFrame()
            {
                if (m_head == null)
                {
                    return null;
                }
                Bounds frame = new Bounds(m_head.position, Vector3.one * 0.4f);
                if (m_worn != null)
                {
                    foreach (Renderer renderer in m_worn.GetComponentsInChildren<Renderer>())
                    {
                        // a hair bound to the body's bones reports the body's size: the head and a little below it
                        frame.Encapsulate(renderer is SkinnedMeshRenderer
                            ? new Bounds(m_head.position + Vector3.down * 0.1f, Vector3.one * 0.55f)
                            : renderer.bounds);
                    }
                }
                return frame;
            }

            private static void Strip(GameObject model)
            {
                foreach (Joint joint in model.GetComponentsInChildren<Joint>(true))
                {
                    UnityEngine.Object.DestroyImmediate(joint);
                }
                // a script another one requires goes on a later pass
                for (int pass = 0; pass < 3; pass++)
                {
                    MonoBehaviour[] scripts = model.GetComponentsInChildren<MonoBehaviour>(true);
                    if (scripts.Length == 0)
                    {
                        break;
                    }
                    for (int i = scripts.Length - 1; i >= 0; i--)
                    {
                        if (scripts[i] != null)
                        {
                            UnityEngine.Object.DestroyImmediate(scripts[i]);
                        }
                    }
                }
                foreach (Rigidbody body in model.GetComponentsInChildren<Rigidbody>(true))
                {
                    UnityEngine.Object.DestroyImmediate(body);
                }
            }

            // The model from the front-left, a little from above; drawn on black and on white, the difference is its
            // transparency (soft edges and glass included), then cut to what is there. A glowing model comes out in
            // wrong colours so: it is drawn on a dark background instead, as the game shows it ("dark"). "frame" is the
            // part to show (a head), else the whole model.
            internal bool Shoot(GameObject model, string file, bool dark = false, Bounds? frame = null)
            {
                Bounds bounds = new Bounds();
                bool any = false;
                foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>())
                {
                    if (!renderer.enabled || renderer is ParticleSystemRenderer)
                    {
                        continue;
                    }
                    // a rope drawn in the world's coordinates (a cart's harness) is still where the prefab was made:
                    // far from the model, it would make the picture a dot
                    if (renderer is LineRenderer || renderer is TrailRenderer
                        || Vector3.Distance(renderer.bounds.center, model.transform.position) > 100f)
                    {
                        renderer.enabled = false;
                        continue;
                    }
                    if (!any)
                    {
                        bounds = renderer.bounds;
                        any = true;
                    }
                    else
                    {
                        bounds.Encapsulate(renderer.bounds);
                    }
                }
                if (!any || bounds.size.magnitude < 0.01f)
                {
                    Log.Info($"Wiki render {Path.GetFileName(file)}: nothing shown ({model.GetComponentsInChildren<Renderer>(true).Length} renderers in all)");
                    return false;
                }
                if (frame.HasValue)
                {
                    bounds = frame.Value;
                }
                float radius = bounds.extents.magnitude;
                float distance = radius / Mathf.Sin(m_camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.05f;
                Quaternion view = Quaternion.Euler(12f, 180f - 35f, 0f);
                m_camera.transform.position = bounds.center - view * Vector3.forward * distance;
                m_camera.transform.rotation = view;
                m_camera.nearClipPlane = Mathf.Max(0.01f, distance - radius * 1.5f);
                m_camera.farClipPlane = distance + radius * 1.5f;

                bool fog = RenderSettings.fog;
                UnityEngine.Rendering.AmbientMode ambientMode = RenderSettings.ambientMode;
                Color ambient = RenderSettings.ambientLight;
                Texture2D black = null;
                Texture2D white = null;
                Texture2D backdrop = null;
                try
                {
                    RenderSettings.fog = false;
                    RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                    RenderSettings.ambientLight = new Color(0.42f, 0.42f, 0.45f);
                    black = Draw(Color.black);
                    white = Draw(Color.white);
                    if (dark)
                    {
                        backdrop = Draw(new Color(0.1f, 0.11f, 0.14f));
                    }
                }
                finally
                {
                    RenderSettings.fog = fog;
                    RenderSettings.ambientMode = ambientMode;
                    RenderSettings.ambientLight = ambient;
                }
                try
                {
                    Texture2D picture = Matte(black, white, backdrop);
                    if (picture == null)
                    {
                        Log.Info($"Wiki render {Path.GetFileName(file)}: nothing drawn");
                        return false;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllBytes(file, picture.EncodeToPNG());
                    UnityEngine.Object.Destroy(picture);
                    return true;
                }
                catch (Exception e)
                {
                    Log.Warn($"Wiki render {Path.GetFileName(file)}: {e.Message}");
                    return false;
                }
                finally
                {
                    UnityEngine.Object.Destroy(black);
                    UnityEngine.Object.Destroy(white);
                    if (backdrop != null)
                    {
                        UnityEngine.Object.Destroy(backdrop);
                    }
                }
            }

            private Texture2D Draw(Color background)
            {
                m_camera.backgroundColor = background;
                m_camera.Render();
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = m_target;
                Texture2D copy = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                copy.Apply();
                RenderTexture.active = previous;
                return copy;
            }

            // alpha = 1 − (on white − on black); colour = on black / alpha; then cut to the drawn part with a margin.
            // With a backdrop: the picture on it as it is, cut the same way.
            private static Texture2D Matte(Texture2D black, Texture2D white, Texture2D backdrop)
            {
                Color[] b = black.GetPixels();
                Color[] w = white.GetPixels();
                Color[] result = new Color[b.Length];
                int minX = Size, minY = Size, maxX = -1, maxY = -1;
                for (int i = 0; i < b.Length; i++)
                {
                    float alpha = 1f - ((w[i].r - b[i].r) + (w[i].g - b[i].g) + (w[i].b - b[i].b)) / 3f;
                    alpha = Mathf.Clamp01(alpha);
                    if (alpha < 0.004f)
                    {
                        result[i] = Color.clear;
                        continue;
                    }
                    result[i] = new Color(Mathf.Clamp01(b[i].r / alpha), Mathf.Clamp01(b[i].g / alpha), Mathf.Clamp01(b[i].b / alpha), alpha);
                    int x = i % Size;
                    int y = i / Size;
                    minX = Mathf.Min(minX, x);
                    maxX = Mathf.Max(maxX, x);
                    minY = Mathf.Min(minY, y);
                    maxY = Mathf.Max(maxY, y);
                }
                if (maxX < 0)
                {
                    return null;
                }
                int margin = Mathf.RoundToInt(Mathf.Max(maxX - minX, maxY - minY) * 0.04f) + 2;
                minX = Mathf.Max(0, minX - margin);
                minY = Mathf.Max(0, minY - margin);
                maxX = Mathf.Min(Size - 1, maxX + margin);
                maxY = Mathf.Min(Size - 1, maxY + margin);
                int width = maxX - minX + 1;
                int height = maxY - minY + 1;
                if (backdrop != null)
                {
                    result = backdrop.GetPixels();
                    for (int i = 0; i < result.Length; i++)
                    {
                        result[i].a = 1f;
                    }
                }
                Color[] cut = new Color[width * height];
                for (int y = 0; y < height; y++)
                {
                    Array.Copy(result, (minY + y) * Size + minX, cut, y * width, width);
                }
                Texture2D picture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                picture.SetPixels(cut);
                picture.Apply();
                return picture;
            }

            public void Dispose()
            {
                if (m_sun != null)
                {
                    m_sun.cullingMask = m_sunMask;
                }
                m_camera.targetTexture = null;
                m_target.Release();
                UnityEngine.Object.Destroy(m_target);
                UnityEngine.Object.Destroy(m_root);
            }
        }

        // ---------------------------------------------------------------- places

        internal static string StartPlaces()
        {
            if (Busy)
            {
                return Lang.T("Знімки для вікі вже робляться.");
            }
            Player player = Player.m_localPlayer;
            if (player == null || ZoneSystem.instance == null || GameCamera.instance == null)
            {
                return Lang.T("Знімки для вікі робляться у світі.");
            }
            Runner().StartCoroutine(Places(player));
            return Lang.T("Знімаю місця для вікі: вікінга по черзі перенесе до кожного, без шкоди й непомітно, і поверне назад. Біля початку світу на луці він насипле, вирівняє, зоре й засіє травою клаптики землі — робити це лише в тестовому світі.");
        }

        private static IEnumerator Places(Player player)
        {
            Busy = true;
            List<string> wanted = Wanted("places_wanted.txt", s_defaultPlaces);
            Vector3 home = player.transform.position;
            Quaternion homeRotation = player.transform.rotation;
            bool god = player.InGodMode();
            bool moved = false;
            bool ghost = player.InGhostMode();
            string weather = WorldTools.WeatherOverride;
            float hours = WorldTools.IsLocalTimeFixed ? WorldTools.LocalFixedHours : -1f;
            int made = 0;
            try
            {
                player.SetGodMode(true);
                player.SetGhostMode(true);
                WorldTools.RestoreLocal("Clear", 12f);
                foreach (string prefab in wanted)
                {
                    if (player == null || player.IsDead())
                    {
                        break;
                    }
                    string file = Path.Combine(Path.Combine(Folder, "places"), Safe(prefab) + ".png");
                    if (File.Exists(file) || !Nearest(prefab, player.transform.position, out Vector3 place, out float radius))
                    {
                        continue;
                    }
                    // the viking goes next to the place; the camera will look at it from above, from the side with
                    // the fewest trees in the way
                    float distance = Mathf.Clamp(radius * 1.5f + 15f, 25f, 110f);
                    Vector3 back = Quaternion.Euler(0f, 45f, 0f) * Vector3.back * distance;
                    while (player.IsTeleporting())
                    {
                        yield return null;
                    }
                    yield return new WaitForSeconds(2.2f);          // the game waits 2 s between teleports
                    player.TeleportTo(place + back + Vector3.up * 2f, player.transform.rotation, distantTeleport: true);
                    moved = true;
                    yield return null;
                    while (player.IsTeleporting())
                    {
                        yield return null;
                    }
                    // the place and what grows and stands there
                    yield return new WaitForSeconds(6f);
                    float ground = ZoneSystem.instance.GetGroundHeight(place);
                    Vector3 target = new Vector3(place.x, Mathf.Max(ground, place.y) + Mathf.Min(6f, radius * 0.15f), place.z);
                    List<Renderer> trees = TreeRenderers(target, distance * 1.6f);
                    Vector3 eye = BestView(target, distance, trees, out List<Renderer> inTheWay);
                    if (!FreeCamera.Active)
                    {
                        FreeCamera.Toggle();
                    }
                    FreeCamera.Place(eye, Quaternion.LookRotation(target - eye).eulerAngles);
                    bool wasHidden = PhotoMode.VikingHidden;
                    PhotoMode.SetVikingHidden(true);
                    // the trees between the camera and the place stay out of this one picture
                    foreach (Renderer renderer in inTheWay)
                    {
                        renderer.enabled = false;
                    }
                    yield return new WaitForSeconds(1.5f);
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    yield return new WaitForEndOfFrame();
                    ScreenCapture.CaptureScreenshot(file, 1);
                    yield return null;
                    yield return null;
                    foreach (Renderer renderer in inTheWay)
                    {
                        if (renderer != null)
                        {
                            renderer.enabled = true;
                        }
                    }
                    PhotoMode.SetVikingHidden(wasHidden);
                    made++;
                }
                // what the hoe and the cultivator do to the ground, done on a meadow near the start and shot there
                List<string> work = new List<string>();
                foreach (string op in s_groundOps)
                {
                    if (!File.Exists(Path.Combine(Path.Combine(Folder, "ground"), op + ".png")) && GroundOp(op) != null)
                    {
                        work.Add(op);
                    }
                }
                Vector3 meadow = Vector3.zero;
                if (work.Count > 0 && !Meadow(player.transform.position, out meadow))
                {
                    Log.Info("Wiki ground: no meadow near the start");
                }
                else if (work.Count > 0 && player != null && !player.IsDead())
                {
                    while (player.IsTeleporting())
                    {
                        yield return null;
                    }
                    yield return new WaitForSeconds(2.2f);
                    player.TeleportTo(meadow + Vector3.up * 2f, player.transform.rotation, distantTeleport: true);
                    moved = true;
                    yield return null;
                    while (player.IsTeleporting())
                    {
                        yield return null;
                    }
                    yield return new WaitForSeconds(6f);
                    List<Renderer> trees = TreeRenderers(meadow, 90f);
                    List<Vector3> spots = GroundSpots(meadow, trees, work.Count);
                    Log.Info($"Wiki ground: {spots.Count} places for {work.Count} pictures at {meadow}");
                    for (int k = 0; k < work.Count && k < spots.Count; k++)
                    {
                        string op = work[k];
                        Vector3 spot = spots[k];
                        foreach (KeyValuePair<Vector3, string> step in GroundSteps(op, spot))
                        {
                            GameObject prefab = GroundOp(step.Value);
                            if (prefab != null)
                            {
                                UnityEngine.Object.Instantiate(prefab, step.Key, Quaternion.identity);
                            }
                            yield return new WaitForSeconds(0.25f);
                        }
                        // the ground and the grass around it are made anew
                        yield return new WaitForSeconds(1.5f);
                        string file = Path.Combine(Path.Combine(Folder, "ground"), op + ".png");
                        Vector3 target = new Vector3(spot.x, ZoneSystem.instance.GetGroundHeight(spot) + 0.3f, spot.z);
                        Vector3 eye = BestView(target, 7f, trees, out List<Renderer> inTheWay);
                        if (!FreeCamera.Active)
                        {
                            FreeCamera.Toggle();
                        }
                        FreeCamera.Place(eye, Quaternion.LookRotation(target - eye).eulerAngles);
                        bool wasHidden = PhotoMode.VikingHidden;
                        PhotoMode.SetVikingHidden(true);
                        foreach (Renderer renderer in inTheWay)
                        {
                            renderer.enabled = false;
                        }
                        yield return new WaitForSeconds(1f);
                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                        yield return new WaitForEndOfFrame();
                        ScreenCapture.CaptureScreenshot(file, 1);
                        yield return null;
                        yield return null;
                        foreach (Renderer renderer in inTheWay)
                        {
                            if (renderer != null)
                            {
                                renderer.enabled = true;
                            }
                        }
                        PhotoMode.SetVikingHidden(wasHidden);
                        made++;
                    }
                }
            }
            finally
            {
                if (FreeCamera.Active)
                {
                    FreeCamera.Stop();
                }
                if (player != null)
                {
                    WorldTools.RestoreLocal(weather, hours);
                    player.SetGodMode(god);
                    player.SetGhostMode(ghost);
                }
                Busy = false;
            }
            if (player != null && !player.IsDead())
            {
                if (moved)
                {
                    while (player.IsTeleporting())
                    {
                        yield return null;
                    }
                    yield return new WaitForSeconds(2.2f);
                    player.TeleportTo(home, homeRotation, distantTeleport: true);
                }
                player.Message(MessageHud.MessageType.Center, Lang.F("Знімки місць для вікі готові: {0}. Тека: {1}", made, Path.Combine(Folder, "places")));
            }
            Log.Info($"Wiki place pictures made: {made}");
        }

        // the hoe's and the cultivator's work on the ground that the wiki shows: raising, levelling, tilling, grass
        private static readonly string[] s_groundOps = { "raise_v2", "mud_road_v2", "cultivate_v2", "replant_v2" };

        // what the hoe or the cultivator places to change the ground: the game keeps these apart from the prefabs
        private static GameObject GroundOp(string name)
        {
            return ObjectDB.instance != null && ObjectDB.instance.TryGetTerrainOp(name, out TerrainOp op) && op != null ? op.gameObject : null;
        }

        // A meadow near the start: the first point on rings around the sacrificial stones (or the viking) that is in
        // the Meadows, above the water and not steep, as the world generator makes it.
        private static bool Meadow(Vector3 from, out Vector3 meadow)
        {
            Vector3 start = Nearest("StartTemple", from, out Vector3 temple, out _) ? temple : from;
            float water = ZoneSystem.instance.m_waterLevel;
            for (float reach = 70f; reach <= 400f; reach += 30f)
            {
                for (int side = 0; side < 16; side++)
                {
                    Vector3 p = start + Quaternion.Euler(0f, side * 22.5f, 0f) * Vector3.forward * reach;
                    float h = WorldGenerator.instance.GetHeight(p.x, p.z);
                    bool flat = Mathf.Abs(WorldGenerator.instance.GetHeight(p.x + 8f, p.z) - h) < 2f
                        && Mathf.Abs(WorldGenerator.instance.GetHeight(p.x, p.z + 8f) - h) < 2f;
                    if (WorldGenerator.instance.GetBiome(p) == Heightmap.Biome.Meadows && h > water + 2f && flat)
                    {
                        meadow = new Vector3(p.x, h, p.z);
                        return true;
                    }
                }
            }
            meadow = Vector3.zero;
            return false;
        }

        // Places for the ground's pictures around the meadow, as the terrain is now: above the water, not steep, with no
        // tree within 6 m and 14 m from one another.
        private static List<Vector3> GroundSpots(Vector3 meadow, List<Renderer> trees, int count)
        {
            List<Vector3> spots = new List<Vector3>();
            float water = ZoneSystem.instance.m_waterLevel;
            for (float reach = 0f; reach <= 60f && spots.Count < count; reach += 4f)
            {
                int sides = reach == 0f ? 1 : 12;
                for (int side = 0; side < sides && spots.Count < count; side++)
                {
                    Vector3 p = meadow + Quaternion.Euler(0f, side * 30f, 0f) * Vector3.forward * reach;
                    p.y = ZoneSystem.instance.GetGroundHeight(p);
                    float dx = Mathf.Abs(ZoneSystem.instance.GetGroundHeight(p + Vector3.right * 3f) - p.y);
                    float dz = Mathf.Abs(ZoneSystem.instance.GetGroundHeight(p + Vector3.forward * 3f) - p.y);
                    if (p.y < water + 1f || dx > 1.2f || dz > 1.2f || spots.Exists(x => Vector3.Distance(x, p) < 14f))
                    {
                        continue;
                    }
                    if (trees.Exists(t => t != null && Vector2.Distance(new Vector2(t.bounds.center.x, t.bounds.center.z), new Vector2(p.x, p.z)) < 6f))
                    {
                        continue;
                    }
                    spots.Add(p);
                }
            }
            return spots;
        }

        // What the viking would place for each picture: a mound raised three times, each on top of the last; a patch
        // levelled at its middle's height; a tilled patch; a tilled patch with grass sown back on one side of it.
        private static List<KeyValuePair<Vector3, string>> GroundSteps(string op, Vector3 spot)
        {
            List<KeyValuePair<Vector3, string>> steps = new List<KeyValuePair<Vector3, string>>();
            if (op == "raise_v2")
            {
                // a wide low mound: the ground raised all over a patch, then once more in its middle
                for (int x = -1; x <= 1; x++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        Vector3 p = spot + new Vector3(x * 1.5f, 0f, z * 1.5f);
                        p.y = ZoneSystem.instance.GetGroundHeight(p);
                        steps.Add(new KeyValuePair<Vector3, string>(p, op));
                    }
                }
                steps.Add(new KeyValuePair<Vector3, string>(spot + Vector3.up * 0.5f, op));
                return steps;
            }
            string tool = op == "replant_v2" ? "cultivate_v2" : op;
            for (int x = -1; x <= 1; x++)
            {
                for (int z = -1; z <= 1; z++)
                {
                    Vector3 p = spot + new Vector3(x * 1.8f, 0f, z * 1.8f);
                    if (op != "mud_road_v2")
                    {
                        p.y = ZoneSystem.instance.GetGroundHeight(p);
                    }
                    steps.Add(new KeyValuePair<Vector3, string>(p, tool));
                }
            }
            if (op == "replant_v2")
            {
                for (int z = -1; z <= 1; z++)
                {
                    Vector3 p = spot + new Vector3(1.8f, 0f, z * 1.8f);
                    p.y = ZoneSystem.instance.GetGroundHeight(p);
                    steps.Add(new KeyValuePair<Vector3, string>(p, op));
                }
            }
            return steps;
        }

        // the drawn parts of the trees around the place: the big ones (TreeBase) and the young ones (a Destructible of
        // the tree kind)
        private static List<Renderer> TreeRenderers(Vector3 center, float reach)
        {
            List<Renderer> result = new List<Renderer>();
            List<GameObject> trees = new List<GameObject>();
            foreach (TreeBase tree in UnityEngine.Object.FindObjectsByType<TreeBase>(FindObjectsSortMode.None))
            {
                trees.Add(tree.gameObject);
            }
            foreach (Destructible young in UnityEngine.Object.FindObjectsByType<Destructible>(FindObjectsSortMode.None))
            {
                if (young.m_destructibleType == DestructibleType.Tree)
                {
                    trees.Add(young.gameObject);
                }
            }
            foreach (GameObject tree in trees)
            {
                if (tree != null && Vector3.Distance(tree.transform.position, center) < reach)
                {
                    foreach (Renderer renderer in tree.GetComponentsInChildren<Renderer>())
                    {
                        if (renderer.enabled)
                        {
                            result.Add(renderer);
                        }
                    }
                }
            }
            return result;
        }

        // Eight sides, looking down at about 37°: the one with the fewest trees in the picture before the place; those
        // trees are given back to be hidden for the picture. The trees at the place's depth and behind it stay: the
        // forest around it.
        private static Vector3 BestView(Vector3 target, float distance, List<Renderer> trees, out List<Renderer> inTheWay)
        {
            Vector3 best = target + Vector3.up * distance * 0.75f + Vector3.back * distance;
            inTheWay = new List<Renderer>();
            int fewest = int.MaxValue;
            Camera main = Utils.GetMainCamera();
            GameObject probe = new GameObject("ValheimHelper_ViewProbe");
            try
            {
                Camera view = probe.AddComponent<Camera>();
                view.enabled = false;
                view.fieldOfView = main != null ? main.fieldOfView : 65f;
                view.aspect = main != null ? main.aspect : 16f / 9f;
                view.nearClipPlane = 0.3f;
                for (int side = 0; side < 8; side++)
                {
                    Vector3 eye = target + Quaternion.Euler(0f, 45f * side, 0f) * Vector3.back * distance + Vector3.up * distance * 0.75f;
                    Vector3 look = target - eye;
                    float length = look.magnitude;
                    look /= length;
                    probe.transform.position = eye;
                    probe.transform.rotation = Quaternion.LookRotation(look);
                    view.farClipPlane = Mathf.Max(1f, length - 3f);          // up to the place, not beyond
                    Plane[] frustum = GeometryUtility.CalculateFrustumPlanes(view);
                    List<Renderer> blocking = new List<Renderer>();
                    foreach (Renderer renderer in trees)
                    {
                        if (renderer != null && GeometryUtility.TestPlanesAABB(frustum, renderer.bounds))
                        {
                            blocking.Add(renderer);
                        }
                    }
                    if (blocking.Count < fewest)
                    {
                        fewest = blocking.Count;
                        best = eye;
                        inTheWay = blocking;
                    }
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(probe);
            }
            return best;
        }

        // the nearest place of that kind in this world, and how far it reaches
        private static bool Nearest(string prefab, Vector3 from, out Vector3 position, out float radius)
        {
            position = Vector3.zero;
            radius = 20f;
            float best = float.MaxValue;
            foreach (ZoneSystem.LocationInstance instance in ZoneSystem.instance.m_locationInstances.Values)
            {
                if (instance.m_location == null || instance.m_location.m_prefabName != prefab)
                {
                    continue;
                }
                float d = Vector3.Distance(from, instance.m_position);
                if (d < best)
                {
                    best = d;
                    position = instance.m_position;
                    radius = instance.m_location.m_exteriorRadius > 0f ? instance.m_location.m_exteriorRadius : 20f;
                }
            }
            return best < float.MaxValue;
        }

        // ---------------------------------------------------------------- common

        private static StudioRunner Runner()
        {
            GameObject root = Bootstrap.Root;
            StudioRunner runner = root.GetComponent<StudioRunner>();
            return runner != null ? runner : root.AddComponent<StudioRunner>();
        }

        private static string Safe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }
    }

    // The coroutines of the wiki studio run on this.
    internal sealed class StudioRunner : MonoBehaviour
    {
    }
}
