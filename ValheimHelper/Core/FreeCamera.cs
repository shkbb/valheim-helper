using HarmonyLib;
using UnityEngine;

namespace ValheimHelper
{
    // A camera of its own for screenshots: it leaves the character where it stands, hides the HUD (the game's own
    // Ctrl+F3 switch) and the minimap, and flies with the movement keys and the mouse. It is not the game's debug
    // "freefly" (that needs the console and marks the character as cheating): the game's camera update is skipped
    // for these frames and the camera is moved here. F11 still saves the game's screenshot.
    internal static class FreeCamera
    {
        private static readonly AccessTools.FieldRef<Hud, bool> s_userHidden = AccessTools.FieldRefAccess<Hud, bool>("m_userHidden");

        internal static bool Active { get; private set; }
        internal static float Speed = 10f;

        private static float s_yaw;
        private static float s_pitch;
        private static bool s_hudWasHidden;

        internal static string Toggle()
        {
            if (Active)
            {
                Stop();
                return Lang.T("Вільну камеру вимкнено.");
            }
            Player player = Player.m_localPlayer;
            GameCamera camera = GameCamera.instance;
            if (player == null || camera == null)
            {
                return Lang.T("Зайди у світ.");
            }
            Vector3 angles = camera.transform.rotation.eulerAngles;
            s_yaw = angles.y;
            s_pitch = angles.x > 180f ? angles.x - 360f : angles.x;
            if (Hud.instance != null)
            {
                s_hudWasHidden = s_userHidden(Hud.instance);
                s_userHidden(Hud.instance) = true;
            }
            if (Minimap.instance != null)
            {
                Minimap.instance.m_smallRoot.SetActive(false);
            }
            State.MenuOpen = false;
            Active = true;
            return Lang.F("Вільна камера: миша — огляд, рух — клавіші ходьби, стрибок/присід — угору/вниз, біг — швидше, коліщатко — швидкість, F11 — знімок. {0} — фоторежим. Esc — назад.", PhotoMode.KeyName());
        }

        internal static void Stop()
        {
            if (!Active)
            {
                return;
            }
            Active = false;
            PhotoMode.Stop();
            if (Hud.instance != null)
            {
                s_userHidden(Hud.instance) = s_hudWasHidden;
            }
            if (Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Small)
            {
                Minimap.instance.m_smallRoot.SetActive(true);
            }
        }

        // Puts the flying camera at a point looking the given way (the wiki studio's pictures of places).
        internal static void Place(Vector3 position, Vector3 eulerAngles)
        {
            if (!Active || GameCamera.instance == null)
            {
                return;
            }
            s_yaw = eulerAngles.y;
            s_pitch = Mathf.Clamp(eulerAngles.x > 180f ? eulerAngles.x - 360f : eulerAngles.x, -89f, 89f);
            GameCamera.instance.transform.position = position;
        }

        // Replaces GameCamera.UpdateCamera while active.
        internal static void Move(GameCamera camera, float dt)
        {
            Player player = Player.m_localPlayer;
            if (player == null || player.IsDead())
            {
                Stop();
                return;
            }
            bool paused = Menu.IsVisible() || Console.IsVisible() || (Chat.instance != null && Chat.instance.HasFocus()) || State.MenuOpen;
            bool photo = PhotoMode.Active;
            // in the photo mode the cursor is free for the panel: the camera looks around while the right button is
            // held, and stays where it is while the keys move the viking
            bool movesCamera = !photo || PhotoMode.Controls == PhotoMode.Target.Camera;
            if (!paused)
            {
                if (ZInput.GetKeyDown(KeyCode.P) && !Binds.IsBound("photomode"))
                {
                    player.Message(MessageHud.MessageType.TopLeft, PhotoMode.Toggle());
                    photo = PhotoMode.Active;
                    movesCamera = !photo || PhotoMode.Controls == PhotoMode.Target.Camera;
                }
                if (movesCamera && (!photo || PhotoMode.Looking))
                {
                    Vector2 mouse = ZInput.GetMouseDelta() * PlayerController.m_mouseSens;
                    s_yaw += mouse.x;
                    s_pitch = Mathf.Clamp(s_pitch + (PlayerController.m_invertMouse ? mouse.y : -mouse.y), -89f, 89f);
                }
                if (movesCamera && !PhotoPanel.PointerOver)
                {
                    float wheel = ZInput.GetMouseScrollWheel();
                    if (wheel > 0f)
                    {
                        Speed = Mathf.Min(200f, Speed * 1.2f);
                    }
                    else if (wheel < 0f)
                    {
                        Speed = Mathf.Max(1f, Speed / 1.2f);
                    }
                }
            }
            Transform transform = camera.transform;
            transform.rotation = Quaternion.Euler(s_pitch, s_yaw, photo ? PhotoMode.Roll : 0f);
            if (photo)
            {
                Camera view = camera.GetComponent<Camera>();
                if (view != null)
                {
                    view.fieldOfView = PhotoMode.Fov;
                }
            }
            if (paused || !movesCamera)
            {
                return;
            }
            Vector3 move = Vector3.zero;
            if (ZInput.GetButton("Forward"))
            {
                move += transform.forward;
            }
            if (ZInput.GetButton("Backward"))
            {
                move -= transform.forward;
            }
            if (ZInput.GetButton("Right"))
            {
                move += transform.right;
            }
            if (ZInput.GetButton("Left"))
            {
                move -= transform.right;
            }
            if (ZInput.GetButton("Jump"))
            {
                move += Vector3.up;
            }
            if (ZInput.GetButton("Crouch"))
            {
                move -= Vector3.up;
            }
            float speed = Speed * (ZInput.GetButton("Run") ? 3f : 1f);
            transform.position += move.normalized * speed * dt;
        }
    }
}
