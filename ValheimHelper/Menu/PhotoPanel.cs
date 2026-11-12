using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimHelper
{
    // The photo mode's panel: the same look as the mod's menu (its skin, sections and a bar of pages), at the right
    // edge of the screen. What the keys move, the shot, the camera, the viking, what it holds, filters and lights.
    // Everything that changes what is drawn waits for the next Layout, so a frame's events always see the same
    // controls.
    internal sealed class PhotoPanel : MonoBehaviour
    {
        private const float Width = 430f;
        private const float BaseScreenHeight = 1080f;

        private static readonly Emotes[] s_emotes =
        {
            Emotes.Wave, Emotes.Cheer, Emotes.ThumbsUp, Emotes.Point, Emotes.Challenge, Emotes.Flex,
            Emotes.Bow, Emotes.Kneel, Emotes.Sit, Emotes.Relax, Emotes.Rest, Emotes.Dance,
            Emotes.Laugh, Emotes.Roar, Emotes.Cry, Emotes.Despair, Emotes.Cower, Emotes.Shrug,
            Emotes.NoNoNo, Emotes.ComeHere, Emotes.Headbang, Emotes.BlowKiss, Emotes.Toast, Emotes.Vibe, Emotes.LoveYou,
        };

        private static string[] s_emoteNames;
        private static string[] s_targets;
        private static string[] s_pages;
        private static KeyValuePair<string, ImageTuning.PhotoLook>[] s_filters;

        internal static bool PointerOver;

        private Rect m_window = new Rect(0f, 20f, Width, 700f);
        private bool m_placed;
        private int m_page;
        private int m_pendingPage = -1;
        private Vector2 m_scroll;
        private readonly Queue<Action> m_pending = new Queue<Action>();
        private List<ItemDrop.ItemData> m_items = new List<ItemDrop.ItemData>();
        private List<PhotoMode.PhotoLight> m_lights = new List<PhotoMode.PhotoLight>();
        private GUISkin m_skin;
        private GUIStyle m_hintStyle;
        private bool m_wasFree;
        private float m_hintUntil;
        private const float HintSeconds = 8f;

        private static void EnsureNames()
        {
            if (s_emoteNames != null)
            {
                return;
            }
            s_targets = new[] { Lang.T("Рухаю камеру"), Lang.T("Рухаю вікінга") };
            s_pages = new[] { Lang.T("Кадр"), Lang.T("Камера"), Lang.T("Вікінг"), Lang.T("Речі"), Lang.T("Фільтр"), Lang.T("Світло") };
            s_emoteNames = new[]
            {
                Lang.T("Помахати"), Lang.T("Радіти"), Lang.T("Клас"), Lang.T("Показати"), Lang.T("Виклик"), Lang.T("М'язи"),
                Lang.T("Уклін"), Lang.T("На коліно"), Lang.T("Сісти"), Lang.T("Відпочинок"), Lang.T("Лягти"), Lang.T("Танець"),
                Lang.T("Сміх"), Lang.T("Рик"), Lang.T("Плакати"), Lang.T("Відчай"), Lang.T("Злякатись"), Lang.T("Знизати плечима"),
                Lang.T("Ні-ні-ні"), Lang.T("Сюди"), Lang.T("Хедбенг"), Lang.T("Поцілунок"), Lang.T("Тост"), Lang.T("Вайб"), Lang.T("Кохаю"),
            };
            s_filters = new[]
            {
                Filter(Lang.T("Без фільтра"), 0f, 1f, 1f, 0f, 0f, 0f, 0f, 0f),
                Filter(Lang.T("Ч/Б"), 0f, 1.15f, 0f, 0f, 0f, 0.2f, 0f, 0f),
                Filter(Lang.T("Нуар"), -0.1f, 1.5f, 0f, 0f, 0f, 0.5f, 0.45f, 0f),
                Filter(Lang.T("Сепія"), 0f, 1.05f, 0.25f, 45f, 10f, 0.3f, 0.2f, 0f),
                Filter(Lang.T("Старе фото"), 0f, 0.9f, 0.4f, 25f, 5f, 0.45f, 0.6f, 0f),
                Filter(Lang.T("Кіно"), 0f, 1.25f, 0.85f, 5f, 0f, 0.4f, 0.3f, 0.1f),
                Filter(Lang.T("Тепло"), 0.1f, 1.05f, 1.1f, 30f, 0f, 0.15f, 0f, 0f),
                Filter(Lang.T("Холод"), 0f, 1.05f, 0.9f, -35f, 0f, 0.15f, 0f, 0f),
                Filter(Lang.T("Яскраво"), 0.1f, 1.1f, 1.4f, 0f, 0f, 0f, 0f, 0f),
                Filter(Lang.T("Мрія"), 0.3f, 0.85f, 0.9f, 10f, 5f, 0.25f, 0.1f, 0.2f),
            };
        }

        private static KeyValuePair<string, ImageTuning.PhotoLook> Filter(string name, float exposure, float contrast, float saturation,
            float temperature, float tint, float vignette, float grain, float aberration)
        {
            return new KeyValuePair<string, ImageTuning.PhotoLook>(name, new ImageTuning.PhotoLook
            {
                Exposure = exposure,
                Contrast = contrast,
                Saturation = saturation,
                Temperature = temperature,
                Tint = tint,
                Vignette = vignette,
                Grain = grain,
                Aberration = aberration,
            });
        }

        private void LateUpdate()
        {
            try
            {
                PhotoMode.Tick();
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private void OnGUI()
        {
            if (!PhotoMode.Active || PhotoMode.Capturing)
            {
                PointerOver = false;
                DrawHint();
                return;
            }
            EnsureNames();
            if (m_skin == null)
            {
                m_skin = HelperSkin.Get();
            }
            GUI.skin = m_skin;
            float scale = Mathf.Max(1f, Screen.height / BaseScreenHeight);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            GUI.depth = -20;
            float screenWidth = Screen.width / scale;
            float screenHeight = Screen.height / scale;
            if (PhotoMode.Grid && Event.current.type == EventType.Repaint)
            {
                DrawGrid(screenWidth, screenHeight);
            }
            if (PhotoMode.PanelHidden)
            {
                PointerOver = false;
                return;
            }
            if (Event.current.type == EventType.Layout)
            {
                while (m_pending.Count > 0)
                {
                    m_pending.Dequeue()();
                }
                if (m_pendingPage >= 0)
                {
                    m_page = m_pendingPage;
                    m_pendingPage = -1;
                    m_scroll = Vector2.zero;
                }
                m_items = PhotoMode.Equipable();
                m_lights = new List<PhotoMode.PhotoLight>(PhotoMode.Lights);
            }
            if (!m_placed)
            {
                m_placed = true;
                m_window = new Rect(screenWidth - Width - 24f, 24f, Width, Mathf.Min(760f, screenHeight - 48f));
            }
            m_window.height = Mathf.Min(760f, screenHeight - 48f);
            m_window = GUILayout.Window(0x5048, m_window, DrawWindow, Lang.T("Фоторежим"),
                GUILayout.Width(m_window.width), GUILayout.Height(m_window.height));
            PointerOver = m_window.Contains(Event.current.mousePosition);
        }

        // While the free camera flies, a line at the bottom says that the photo mode is a key away. It fades out by
        // itself in a few seconds, so it cannot end up on a shot.
        private void DrawHint()
        {
            bool free = FreeCamera.Active;
            if (free && !m_wasFree)
            {
                m_hintUntil = Time.realtimeSinceStartup + HintSeconds;
            }
            m_wasFree = free;
            // nor on the wiki studio's pictures of places
#if EDITION_FULL
            bool studio = WikiStudio.Busy;
#else
            bool studio = false;
#endif
            if (!free || PhotoMode.Capturing || studio || State.MenuOpen || Event.current.type != EventType.Repaint)
            {
                return;
            }
            float left = m_hintUntil - Time.realtimeSinceStartup;
            if (left <= 0f)
            {
                return;
            }
            if (m_skin == null)
            {
                m_skin = HelperSkin.Get();
            }
            if (m_hintStyle == null)
            {
                m_hintStyle = new GUIStyle(m_skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 18,
                    richText = true,
                    wordWrap = false,
                };
            }
            float scale = Mathf.Max(1f, Screen.height / BaseScreenHeight);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            GUI.depth = -20;
            string text = Lang.F("Фоторежим — {0}", PhotoMode.KeyName());
            Vector2 size = m_hintStyle.CalcSize(new GUIContent(text));
            Rect rect = new Rect((Screen.width / scale - size.x - 40f) / 2f, Screen.height / scale - 110f, size.x + 40f, size.y + 16f);
            float alpha = Mathf.Clamp01(left);
            GUI.color = new Color(0.05f, 0.04f, 0.03f, 0.55f * alpha);
            GUI.DrawTexture(rect, HelperSkin.White);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Label(rect, text, m_hintStyle);
            GUI.color = Color.white;
        }

        private static void DrawGrid(float width, float height)
        {
            GUI.color = new Color(1f, 0.9f, 0.7f, 0.25f);
            for (int i = 1; i <= 2; i++)
            {
                GUI.DrawTexture(new Rect(Mathf.Round(width * i / 3f), 0f, 1f, height), HelperSkin.White);
                GUI.DrawTexture(new Rect(0f, Mathf.Round(height * i / 3f), width, 1f), HelperSkin.White);
            }
            GUI.color = Color.white;
        }

        private void Later(Action action)
        {
            m_pending.Enqueue(action);
        }

        private void DrawWindow(int id)
        {
            int target = GUILayout.Toolbar((int)PhotoMode.Controls, s_targets);
            if (target != (int)PhotoMode.Controls)
            {
                Later(() => PhotoMode.Controls = (PhotoMode.Target)target);
            }
            GUILayout.Label(PhotoMode.Controls == PhotoMode.Target.Camera
                ? Lang.T("<color=#c0c0c0>Права кнопка миші — оглядатись. Рух — клавіші ходьби, стрибок і присід — угору й униз, біг — швидше, коліщатко — швидкість.</color>")
                : Lang.T("<color=#c0c0c0>Вікінг ходить клавішами, як у грі; права кнопка миші — повертати його. Камера стоїть на місці.</color>"));
            int page = GUILayout.Toolbar(m_page, s_pages);
            if (page != m_page)
            {
                m_pendingPage = page;
            }
            m_scroll = GUILayout.BeginScrollView(m_scroll);
            switch (m_page)
            {
                case 0: DrawShot(); break;
                case 1: DrawCamera(); break;
                case 2: DrawViking(); break;
                case 3: DrawItems(); break;
                case 4: DrawFilter(); break;
                default: DrawLights(); break;
            }
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Lang.T("Сховати панель")))
            {
                Later(() => PhotoMode.PanelHidden = true);
            }
            if (GUILayout.Button(Lang.T("Вийти з фоторежиму") + Binds.Hint("photomode")))
            {
                Later(PhotoMode.Stop);
            }
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }

        // ---------------------------------------------------------------- pages

        private void DrawShot()
        {
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Знімок"), HelperSkin.Header);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Lang.T("Зробити знімок") + Binds.Hint("photoshot"), GUILayout.Height(34f)))
            {
                string message = PhotoMode.Shot();
                if (message != null)
                {
                    Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, message);
                }
            }
            int shotScale = GUILayout.Toolbar(PhotoMode.ShotScale - 1, new[] { "×1", "×2", "×3" }, GUILayout.Width(140f), GUILayout.Height(34f)) + 1;
            if (shotScale != PhotoMode.ShotScale)
            {
                PhotoMode.ShotScale = shotScale;
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Lang.T("<color=#c0c0c0>×2 і ×3 зберігають знімок у 2 чи 3 рази більший за екран. Панель і сітка на знімок не потрапляють.</color>"));
            if (PhotoMode.LastShot.Length > 0)
            {
                GUILayout.Label(Lang.F("<color=#a0a0a0>Останній: {0}</color>", System.IO.Path.GetFileName(PhotoMode.LastShot)));
            }
            if (GUILayout.Button(Lang.T("Папка зі знімками")))
            {
                System.IO.Directory.CreateDirectory(PhotoMode.ShotFolder);
                Application.OpenURL("file:///" + PhotoMode.ShotFolder.Replace('\\', '/'));
            }
            GUILayout.EndVertical();

            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Композиція"), HelperSkin.Header);
            bool grid = GUILayout.Toggle(PhotoMode.Grid, Lang.T(" Сітка третин"));
            if (grid != PhotoMode.Grid)
            {
                Later(() => PhotoMode.Grid = grid);
            }
            GUILayout.Label(Lang.T("<color=#c0c0c0>Сховану панель повертає Esc. Ще раз Esc — вихід із фоторежиму, далі — з вільної камери.</color>"));
            GUILayout.EndVertical();
        }

        private void DrawCamera()
        {
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Об'єктив"), HelperSkin.Header);
            PhotoMode.Fov = Slider(Lang.T("Кут огляду"), PhotoMode.Fov, 15f, 110f, "{0:0}°");
            PhotoMode.Roll = Slider(Lang.T("Нахил"), PhotoMode.Roll, -45f, 45f, "{0:0}°");
            FreeCamera.Speed = Slider(Lang.T("Швидкість"), FreeCamera.Speed, 1f, 100f, "{0:0} м/с");
            GUILayout.EndVertical();

            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Фокус"), HelperSkin.Header);
            bool focus = GUILayout.Toggle(PhotoMode.Look.Focus, Lang.T(" Розмити все, що ближче чи далі"));
            if (focus != PhotoMode.Look.Focus)
            {
                Later(() =>
                {
                    PhotoMode.Look.Focus = focus;
                    ImageTuning.PhotoChanged();
                });
            }
            if (PhotoMode.Look.Focus)
            {
                bool auto = GUILayout.Toggle(PhotoMode.AutoFocus, Lang.T(" Тримати фокус на вікінгу"));
                if (auto != PhotoMode.AutoFocus)
                {
                    Later(() => PhotoMode.AutoFocus = auto);
                }
                GUI.enabled = !PhotoMode.AutoFocus;
                float distance = Slider(Lang.T("Відстань"), PhotoMode.Look.FocusDistance, 0.5f, 100f, "{0:0.0} м");
                GUI.enabled = true;
                // the smaller the aperture number, the stronger the blur: the slider goes from weak to strong
                float strength = Slider(Lang.T("Сила розмиття"), 16.8f - PhotoMode.Look.Aperture, 0.8f, 16f, "{0:0}");
                if (distance != PhotoMode.Look.FocusDistance || 16.8f - strength != PhotoMode.Look.Aperture)
                {
                    PhotoMode.Look.FocusDistance = distance;
                    PhotoMode.Look.Aperture = 16.8f - strength;
                    ImageTuning.PhotoChanged();
                }
            }
            else
            {
                GUILayout.Label(Lang.T("<color=#c0c0c0>Розмите тло виділяє вікінга, як у портретному знімку.</color>"));
            }
            GUILayout.EndVertical();
        }

        private void DrawViking()
        {
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Поза"), HelperSkin.Header);
            bool frozen = GUILayout.Toggle(PhotoMode.Frozen, Lang.T(" Застигнути посеред руху, стрибка чи емоції") + Binds.Hint("photofreeze"));
            if (frozen != PhotoMode.Frozen)
            {
                Later(() => PhotoMode.SetFrozen(frozen));
            }
            float yaw = Slider(Lang.T("Поворот"), PhotoMode.VikingYaw(), 0f, 360f, "{0:0}°");
            if (Mathf.Abs(Mathf.DeltaAngle(yaw, PhotoMode.VikingYaw())) > 0.5f)
            {
                PhotoMode.TurnViking(yaw);
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Lang.T("Обличчям до камери")))
            {
                Later(PhotoMode.FaceCamera);
            }
            bool hidden = GUILayout.Toggle(PhotoMode.VikingHidden, Lang.T(" Сховати вікінга"));
            if (hidden != PhotoMode.VikingHidden)
            {
                Later(() => PhotoMode.SetVikingHidden(hidden));
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Емоції"), HelperSkin.Header);
            GUILayout.Label(Lang.T("<color=#c0c0c0>Вибери емоцію, а в потрібну мить натисни «Застигнути».</color>"));
            int emote = GUILayout.SelectionGrid(-1, s_emoteNames, 3);
            if (emote >= 0)
            {
                Emotes chosen = s_emotes[emote];
                Later(() => PhotoMode.Emote(chosen));
            }
            if (GUILayout.Button(Lang.T("Зупинити емоцію")))
            {
                Later(PhotoMode.StopEmote);
            }
            GUILayout.EndVertical();
        }

        private void DrawItems()
        {
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("У руках і на собі"), HelperSkin.Header);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Lang.T("Сховати зброю")))
            {
                Later(PhotoMode.HideHands);
            }
            if (GUILayout.Button(Lang.T("Дістати зброю")))
            {
                Later(PhotoMode.ShowHands);
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Lang.T("<color=#c0c0c0>Речі з інвентаря: натисни, щоб узяти чи вдягти, ще раз — зняти. Вдягнене позначено галочкою.</color>"));
            Player player = Player.m_localPlayer;
            foreach (ItemDrop.ItemData item in m_items)
            {
                bool on = player != null && player.IsItemEquiped(item);
                string name = Localization.instance.Localize(item.m_shared.m_name);
                bool value = GUILayout.Toggle(on, " " + name);
                if (value != on)
                {
                    ItemDrop.ItemData chosen = item;
                    Later(() => PhotoMode.ToggleEquip(chosen));
                }
            }
            if (m_items.Count == 0)
            {
                GUILayout.Label(Lang.T("<color=#909090>В інвентарі немає нічого, що можна взяти в руки чи вдягти.</color>"));
            }
            GUILayout.EndVertical();
        }

        private void DrawFilter()
        {
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Готові фільтри"), HelperSkin.Header);
            int filter = GUILayout.SelectionGrid(-1, Array.ConvertAll(s_filters, pair => pair.Key), 3);
            if (filter >= 0)
            {
                ImageTuning.PhotoLook preset = s_filters[filter].Value;
                Later(() => PhotoMode.SetFilter(preset));
            }
            GUILayout.EndVertical();

            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Своє"), HelperSkin.Header);
            ImageTuning.PhotoLook look = PhotoMode.Look;
            float exposure = Slider(Lang.T("Яскравість"), look.Exposure, -2f, 2f, "{0:0.0}");
            float contrast = Slider(Lang.T("Контраст"), look.Contrast, 0.3f, 2f, "{0:0.00}");
            float saturation = Slider(Lang.T("Насиченість"), look.Saturation, 0f, 2f, "{0:0.00}");
            float temperature = Slider(Lang.T("Температура"), look.Temperature, -100f, 100f, "{0:0}");
            float tint = Slider(Lang.T("Відтінок"), look.Tint, -100f, 100f, "{0:0}");
            float vignette = Slider(Lang.T("Віньєтка"), look.Vignette, 0f, 1f, "{0:0.00}");
            float grainValue = Slider(Lang.T("Зерно"), look.Grain, 0f, 1f, "{0:0.00}");
            float aberration = Slider(Lang.T("Кольорова облямівка"), look.Aberration, 0f, 1f, "{0:0.00}");
            if (exposure != look.Exposure || contrast != look.Contrast || saturation != look.Saturation || temperature != look.Temperature
                || tint != look.Tint || vignette != look.Vignette || grainValue != look.Grain || aberration != look.Aberration)
            {
                look.Exposure = exposure;
                look.Contrast = contrast;
                look.Saturation = saturation;
                look.Temperature = temperature;
                look.Tint = tint;
                look.Vignette = vignette;
                look.Grain = grainValue;
                look.Aberration = aberration;
                ImageTuning.PhotoChanged();
            }
            GUILayout.EndVertical();
        }

        private void DrawLights()
        {
            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Світло"), HelperSkin.Header);
            GUILayout.Label(Lang.T("<color=#c0c0c0>Світло бачиш лише ти, у світі його немає. Воно лишається там, де поставлене, тож можна обійти вікінга з іншого боку.</color>"));
            if (GUILayout.Button(Lang.T("Поставити світло перед камерою")))
            {
                Later(PhotoMode.AddLight);
            }
            if (m_lights.Count == 0)
            {
                GUILayout.Label(Lang.T("<color=#909090>Світла ще немає.</color>"));
            }
            GUILayout.EndVertical();
            for (int i = 0; i < m_lights.Count; i++)
            {
                PhotoMode.PhotoLight light = m_lights[i];
                if (light.Light == null)
                {
                    continue;
                }
                GUILayout.BeginVertical(HelperSkin.Section);
                GUILayout.BeginHorizontal();
                GUILayout.Label(Lang.F("Світло {0}", i + 1), GUILayout.Width(90f));
                for (int c = 0; c < PhotoMode.LightColors.Length; c++)
                {
                    Rect swatch = GUILayoutUtility.GetRect(22f, 22f, GUILayout.Width(22f), GUILayout.Height(22f));
                    if (Event.current.type == EventType.Repaint)
                    {
                        GUI.color = PhotoMode.LightColors[c];
                        GUI.DrawTexture(swatch, c == light.Color ? HelperSkin.Circle : HelperSkin.Ring);
                        GUI.color = Color.white;
                    }
                    if (GUI.Button(swatch, GUIContent.none, GUIStyle.none))
                    {
                        light.Color = c;
                        light.Light.color = PhotoMode.LightColors[c];
                    }
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Lang.T("Сюди"), GUILayout.Width(64f)) && GameCamera.instance != null)
                {
                    Transform camera = GameCamera.instance.transform;
                    light.Object.transform.position = camera.position + camera.forward * 1.5f;
                }
                if (GUILayout.Button("✕", GUILayout.Width(30f)))
                {
                    PhotoMode.PhotoLight chosen = light;
                    Later(() => PhotoMode.RemoveLight(chosen));
                }
                GUILayout.EndHorizontal();
                light.Light.intensity = Slider(Lang.T("Яскравість"), light.Light.intensity, 0.1f, 8f, "{0:0.0}");
                light.Light.range = Slider(Lang.T("Дальність"), light.Light.range, 1f, 40f, "{0:0} м");
                GUILayout.EndVertical();
            }
        }

        private float Slider(string label, float value, float min, float max, string format)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(150f));
            float result = GUILayout.HorizontalSlider(value, min, max);
            GUILayout.Label(string.Format(format, result), GUILayout.Width(70f));
            GUILayout.EndHorizontal();
            return result;
        }
    }
}
