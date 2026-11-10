using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace ValheimHelper
{
    // The photo mode on top of the free camera: the cursor is free for the panel, the camera looks around while the
    // right mouse button is held, and the keys move either the camera or the viking. The viking can do the game's
    // emotes, stand still in a pose, turn, take items of the inventory in hand or hide, the picture gets filters,
    // focus, vignette and grain, lights can be put anywhere, and a shot is saved without the panel.
    // Everything is local to this computer and made of what the game allows anyway (emotes, equipping from the
    // inventory); nothing is spawned and nothing marks the character as cheating.
    internal static class PhotoMode
    {
        internal enum Target
        {
            Camera,
            Viking,
        }

        internal sealed class PhotoLight
        {
            public GameObject Object;
            public Light Light;
            public int Color;
        }

        private static readonly AccessTools.FieldRef<Character, Animator> s_animator = AccessTools.FieldRefAccess<Character, Animator>("m_animator");
        private static readonly AccessTools.FieldRef<Character, Rigidbody> s_body = AccessTools.FieldRefAccess<Character, Rigidbody>("m_body");
        private static readonly System.Reflection.MethodInfo s_stopEmote = AccessTools.Method(typeof(Player), "StopEmote");
        private static readonly System.Reflection.MethodInfo s_showHands = AccessTools.Method(typeof(Humanoid), "ShowHandItems", new[] { typeof(bool), typeof(bool) });

        internal static readonly Color[] LightColors =
        {
            new Color(1f, 0.82f, 0.6f),
            new Color(1f, 1f, 1f),
            new Color(0.6f, 0.75f, 1f),
            new Color(1f, 0.5f, 0.2f),
            new Color(0.5f, 1f, 0.6f),
            new Color(0.85f, 0.5f, 1f),
        };

        internal static bool Active { get; private set; }
        internal static Target Controls = Target.Camera;
        internal static bool PanelHidden;
        internal static bool Frozen { get; private set; }
        internal static bool VikingHidden { get; private set; }
        internal static bool Grid;
        internal static float Fov = 65f;
        internal static float Roll;
        internal static int ShotScale = 1;
        internal static bool AutoFocus = true;
        internal static readonly ImageTuning.PhotoLook Look = new ImageTuning.PhotoLook();
        internal static readonly List<PhotoLight> Lights = new List<PhotoLight>();
        internal static string LastShot = "";

        // The shot is taken at the end of a frame drawn without the panel.
        internal static bool Capturing => s_shotStage > 0;
        private static int s_shotStage;
        private static string s_shotPath;

        private static bool s_bodyWasKinematic;
        private static readonly List<Renderer> s_hiddenRenderers = new List<Renderer>();

        internal static bool ControlsViking => Active && Controls == Target.Viking;

        // The right mouse button held: the mouse looks around (the cursor is free otherwise, for the panel).
        internal static bool Looking => Active && ZInput.GetKey(KeyCode.Mouse1);

        internal static string ShotFolder => Path.Combine(Loader.Dir ?? ".", "screenshots");

        // The key that opens the photo mode: the one chosen in the settings, or P while the free camera flies.
        internal static string KeyName()
        {
            return Binds.TryGetKey("photomode", out KeyChord chord) ? chord.ToString() : "P";
        }

        internal static string Toggle()
        {
            if (Active)
            {
                Stop();
                return Lang.T("Фоторежим вимкнено, вільна камера лишилась.");
            }
            if (!FreeCamera.Active)
            {
                string started = FreeCamera.Toggle();
                if (!FreeCamera.Active)
                {
                    return started;
                }
            }
            Active = true;
            PanelHidden = false;
            Controls = Target.Camera;
            Fov = GameCamera.instance != null ? GameCamera.instance.m_fov : 65f;
            Roll = 0f;
            ImageTuning.Photo = Look;
            ImageTuning.PhotoChanged();
            return Lang.T("Фоторежим: права кнопка миші — огляд, клавіші руху — камера або вікінг (перемикач на панелі). Esc — назад до вільної камери.");
        }

        internal static void Stop()
        {
            if (!Active)
            {
                return;
            }
            Active = false;
            SetFrozen(false);
            SetVikingHidden(false);
            RemoveLights();
            ImageTuning.Photo = null;
            ImageTuning.PhotoChanged();
            Controls = Target.Camera;
            s_shotStage = 0;
        }

        // Every frame (LateUpdate of the panel): the pose stays frozen, a shot is taken when its frame comes.
        internal static void Tick()
        {
            if (!Active)
            {
                return;
            }
            Player player = Player.m_localPlayer;
            if (player == null || !FreeCamera.Active)
            {
                Stop();
                return;
            }
            if (Frozen)
            {
                Animator animator = s_animator(player);
                if (animator != null)
                {
                    animator.speed = 0f;
                }
            }
            if (AutoFocus && Look.Focus && GameCamera.instance != null)
            {
                float distance = Vector3.Distance(GameCamera.instance.transform.position, player.transform.position + Vector3.up * 1.2f);
                if (Mathf.Abs(distance - Look.FocusDistance) > 0.05f)
                {
                    Look.FocusDistance = distance;
                    ImageTuning.PhotoChanged();
                }
            }
            if (s_shotStage == 1)
            {
                // this frame is drawn without the panel; the picture is saved at its end
                ScreenCapture.CaptureScreenshot(s_shotPath, Mathf.Clamp(ShotScale, 1, 4));
                s_shotStage = 2;
            }
            else if (s_shotStage == 2)
            {
                s_shotStage = 0;
                LastShot = s_shotPath;
                player.Message(MessageHud.MessageType.TopLeft, Lang.F("Знімок збережено: {0}", Path.GetFileName(s_shotPath)));
            }
        }

        // ---------------------------------------------------------------- the viking

        internal static void Emote(Emotes emote)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return;
            }
            if (Frozen)
            {
                SetFrozen(false);
            }
            global::Emote.DoEmote(emote);
        }

        internal static void StopEmote()
        {
            Player player = Player.m_localPlayer;
            if (player != null)
            {
                s_stopEmote?.Invoke(player, null);
            }
        }

        // The viking stands still in whatever the animation shows now, in the air too.
        internal static void SetFrozen(bool frozen)
        {
            Player player = Player.m_localPlayer;
            if (player == null || frozen == Frozen)
            {
                Frozen = frozen && player != null;
                return;
            }
            Frozen = frozen;
            Animator animator = s_animator(player);
            Rigidbody body = s_body(player);
            if (frozen)
            {
                if (body != null)
                {
                    s_bodyWasKinematic = body.isKinematic;
                    body.velocity = Vector3.zero;
                    body.isKinematic = true;
                }
                if (animator != null)
                {
                    animator.speed = 0f;
                }
            }
            else
            {
                if (animator != null)
                {
                    animator.speed = 1f;
                }
                if (body != null)
                {
                    body.isKinematic = s_bodyWasKinematic;
                }
            }
        }

        internal static float VikingYaw()
        {
            Player player = Player.m_localPlayer;
            return player != null ? player.transform.rotation.eulerAngles.y : 0f;
        }

        internal static void TurnViking(float yaw)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return;
            }
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            player.SetLookDir(rotation * Vector3.forward);
            player.transform.rotation = rotation;
            Rigidbody body = s_body(player);
            if (body != null)
            {
                body.rotation = rotation;
            }
        }

        internal static void FaceCamera()
        {
            Player player = Player.m_localPlayer;
            if (player == null || GameCamera.instance == null)
            {
                return;
            }
            Vector3 toCamera = GameCamera.instance.transform.position - player.transform.position;
            toCamera.y = 0f;
            if (toCamera.sqrMagnitude > 0.01f)
            {
                TurnViking(Quaternion.LookRotation(toCamera).eulerAngles.y);
            }
        }

        // Landscapes without the viking: only its look is switched off here, the character is where it was.
        internal static void SetVikingHidden(bool hidden)
        {
            Player player = Player.m_localPlayer;
            if (hidden == VikingHidden)
            {
                return;
            }
            VikingHidden = hidden;
            if (hidden && player != null)
            {
                s_hiddenRenderers.Clear();
                foreach (Renderer renderer in player.GetComponentsInChildren<Renderer>())
                {
                    if (renderer.enabled)
                    {
                        renderer.enabled = false;
                        s_hiddenRenderers.Add(renderer);
                    }
                }
            }
            else
            {
                foreach (Renderer renderer in s_hiddenRenderers)
                {
                    if (renderer != null)
                    {
                        renderer.enabled = true;
                    }
                }
                s_hiddenRenderers.Clear();
            }
        }

        // Items the viking can hold or wear, from the inventory.
        internal static List<ItemDrop.ItemData> Equipable()
        {
            List<ItemDrop.ItemData> items = new List<ItemDrop.ItemData>();
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return items;
            }
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems())
            {
                if (item != null && item.IsEquipable())
                {
                    items.Add(item);
                }
            }
            items.Sort((a, b) => string.Compare(Localization.instance.Localize(a.m_shared.m_name), Localization.instance.Localize(b.m_shared.m_name), StringComparison.CurrentCultureIgnoreCase));
            return items;
        }

        internal static void ToggleEquip(ItemDrop.ItemData item)
        {
            Player player = Player.m_localPlayer;
            if (player == null || item == null)
            {
                return;
            }
            if (player.IsItemEquiped(item))
            {
                player.UnequipItem(item);
            }
            else
            {
                player.EquipItem(item);
            }
        }

        internal static void HideHands()
        {
            Player.m_localPlayer?.HideHandItems();
        }

        internal static void ShowHands()
        {
            Player player = Player.m_localPlayer;
            if (player != null)
            {
                s_showHands?.Invoke(player, new object[] { false, true });
            }
        }

        // ---------------------------------------------------------------- lights

        internal static void AddLight()
        {
            if (GameCamera.instance == null)
            {
                return;
            }
            Transform camera = GameCamera.instance.transform;
            GameObject go = new GameObject("VH photo light");
            go.transform.position = camera.position + camera.forward * 1.5f;
            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 8f;
            light.intensity = 1.5f;
            light.color = LightColors[0];
            light.shadows = LightShadows.Soft;
            Lights.Add(new PhotoLight { Object = go, Light = light, Color = 0 });
        }

        internal static void RemoveLight(PhotoLight light)
        {
            if (light?.Object != null)
            {
                UnityEngine.Object.Destroy(light.Object);
            }
            Lights.Remove(light);
        }

        internal static void RemoveLights()
        {
            foreach (PhotoLight light in Lights)
            {
                if (light.Object != null)
                {
                    UnityEngine.Object.Destroy(light.Object);
                }
            }
            Lights.Clear();
        }

        // ---------------------------------------------------------------- filters and the shot

        internal static void SetFilter(ImageTuning.PhotoLook preset)
        {
            bool focus = Look.Focus;
            float distance = Look.FocusDistance;
            float aperture = Look.Aperture;
            Look.Exposure = preset.Exposure;
            Look.Contrast = preset.Contrast;
            Look.Saturation = preset.Saturation;
            Look.Temperature = preset.Temperature;
            Look.Tint = preset.Tint;
            Look.Vignette = preset.Vignette;
            Look.Grain = preset.Grain;
            Look.Aberration = preset.Aberration;
            // the focus is the camera's, not the filter's
            Look.Focus = focus;
            Look.FocusDistance = distance;
            Look.Aperture = aperture;
            ImageTuning.PhotoChanged();
        }

        internal static string Shot()
        {
            if (!Active)
            {
                return Lang.T("Знімок робиться у фоторежимі.");
            }
            try
            {
                Directory.CreateDirectory(ShotFolder);
            }
            catch (Exception e)
            {
                return e.Message;
            }
            s_shotPath = Path.Combine(ShotFolder, "Valheim_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".png");
            s_shotStage = 1;
            return null;
        }
    }
}
