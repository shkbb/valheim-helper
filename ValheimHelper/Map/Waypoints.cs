using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace ValheimHelper.Map
{
    // Waypoints: marks seen in the 3D world (an icon with the name and distance over the spot, an arrow at the screen
    // edge for the one you follow) and on the large map. Placed with Alt + click on the map, from a selected place,
    // or with a key where you look. Like the drawings they are either yours (a file on your computer for this world)
    // or shared with friends who have the mod (the host keeps them and sends them to everyone).
    internal static class Waypoints
    {
        internal sealed class Mark
        {
            public long Id;
            public long Owner;
            public string OwnerName = "";
            public Vector3 Pos;
            public int Color;
            public string Name = "";
            public bool Shared;
        }

        private const string RpcAdd = "ValheimHelper_WaypointAdd";
        private const string RpcAdded = "ValheimHelper_WaypointAdded";
        private const string RpcRemove = "ValheimHelper_WaypointRemove";
        private const string RpcRemoved = "ValheimHelper_WaypointRemoved";
        private const string RpcAll = "ValheimHelper_WaypointAll";
        private const int MaxMarks = 500;

        internal static readonly List<Mark> Mine = new List<Mark>();
        internal static readonly List<Mark> Shared = new List<Mark>();
        internal static int Version;
        // The mark you follow: its arrow stays at the screen edge while it is out of view.
        internal static long GuideId;
        // Alt is held over the open map: clicks place and remove marks instead of doing what the game does.
        internal static bool CapturesMouse;

        private static long s_worldUid;
        private static bool s_dirty;
        private static float s_saveAt;
        private static bool s_sharedReceived;
        private static readonly System.Random s_random = new System.Random();

        internal static bool SharedAvailable => MapData.IsHost || s_sharedReceived;

        private static string MinePath => Path.Combine(Path.Combine(Loader.Dir ?? ".", "maps"), s_worldUid.ToString(CultureInfo.InvariantCulture) + ".waypoints.txt");
        private static string SharedPath => Path.Combine(Path.Combine(Loader.Dir ?? ".", "maps"), s_worldUid.ToString(CultureInfo.InvariantCulture) + ".shared_waypoints.txt");

        private static long MyId => Game.instance != null ? Game.instance.GetPlayerProfile().GetPlayerID() : 0L;
        private static string MyName => Game.instance != null ? Game.instance.GetPlayerProfile().GetName() : "";

        internal static IEnumerable<Mark> All()
        {
            foreach (Mark mark in Shared)
            {
                yield return mark;
            }
            foreach (Mark mark in Mine)
            {
                yield return mark;
            }
        }

        internal static Mark Find(long id)
        {
            return Mine.Find(mark => mark.Id == id) ?? Shared.Find(mark => mark.Id == id);
        }

        // ---------------------------------------------------------------- session and files

        internal static void OnSession(long worldUid)
        {
            Flush();
            s_worldUid = worldUid;
            Mine.Clear();
            Shared.Clear();
            s_sharedReceived = false;
            GuideId = 0L;
            Load(MinePath, Mine, false);
            if (MapData.IsHost)
            {
                Load(SharedPath, Shared, true);
            }
            Version++;
        }

        internal static void Reset()
        {
            Flush();
            Mine.Clear();
            Shared.Clear();
            s_sharedReceived = false;
            GuideId = 0L;
            CapturesMouse = false;
            Version++;
        }

        internal static void Tick()
        {
            // marks put while the host's list had not come yet join the others once it has
            if (MapSettings.WaypointsShared && Mine.Count > 0 && SharedAvailable)
            {
                SetMineShared(true);
            }
            if (s_dirty && Time.unscaledTime >= s_saveAt)
            {
                Flush();
            }
        }

        internal static void Flush()
        {
            if (!s_dirty || s_worldUid == 0)
            {
                return;
            }
            s_dirty = false;
            Save(MinePath, Mine);
            if (MapData.IsHost)
            {
                Save(SharedPath, Shared);
            }
        }

        private static void Changed()
        {
            s_dirty = true;
            s_saveAt = Time.unscaledTime + 1f;
            Version++;
        }

        private static string Format(Mark mark)
        {
            return string.Join("|", new[]
            {
                mark.Id.ToString(CultureInfo.InvariantCulture),
                mark.Owner.ToString(CultureInfo.InvariantCulture),
                Clean(mark.OwnerName),
                mark.Pos.x.ToString("0.##", CultureInfo.InvariantCulture),
                mark.Pos.y.ToString("0.##", CultureInfo.InvariantCulture),
                mark.Pos.z.ToString("0.##", CultureInfo.InvariantCulture),
                mark.Color.ToString(CultureInfo.InvariantCulture),
                Clean(mark.Name),
            });
        }

        private static string Clean(string text)
        {
            return (text ?? "").Replace("|", "/").Replace("\n", " ").Replace("\r", "");
        }

        private static Mark Parse(string line, bool shared)
        {
            string[] parts = (line ?? "").Split('|');
            if (parts.Length < 8
                || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long owner)
                || !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                || !float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
                || !int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int color))
            {
                return null;
            }
            return new Mark
            {
                Id = id,
                Owner = owner,
                OwnerName = parts[2],
                Pos = new Vector3(x, y, z),
                Color = Mathf.Clamp(color, 0, SketchRules.Palette.Length - 1),
                Name = parts[7].Length > 60 ? parts[7].Substring(0, 60) : parts[7],
                Shared = shared,
            };
        }

        private static void Load(string path, List<Mark> into, bool shared)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    Mark mark = Parse(line, shared);
                    if (mark != null && into.Count < MaxMarks)
                    {
                        into.Add(mark);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private static void Save(string path, List<Mark> marks)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                List<string> lines = new List<string>();
                foreach (Mark mark in marks)
                {
                    lines.Add(Format(mark));
                }
                File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        // ---------------------------------------------------------------- adding and removing

        // A new mark at a world point. Height: the ground there (the world generator's, for places far away).
        internal static string Add(Vector3 pos, string name)
        {
            Mark mark = Create(pos, name);
            if (mark == null)
            {
                return Lang.T("Зайди у світ.");
            }
            return mark.Shared ? Lang.F("Мітку «{0}» поставлено для всіх з модом.", mark.Name) : Lang.F("Мітку «{0}» поставлено.", mark.Name);
        }

        internal static Mark Create(Vector3 pos, string name)
        {
            if (Player.m_localPlayer == null)
            {
                return null;
            }
            pos.y = GroundHeight(pos);
            bool shared = MapSettings.WaypointsShared && SharedAvailable;
            int number = Mine.Count + Shared.Count + 1;
            Mark mark = new Mark
            {
                Id = NewId(),
                Owner = MyId,
                OwnerName = MyName,
                Pos = pos,
                Color = Mathf.Clamp(MapSettings.WaypointColor, 0, SketchRules.Palette.Length - 1),
                Name = string.IsNullOrEmpty(name) ? Lang.F("Мітка {0}", number) : name,
                Shared = shared,
            };
            Put(mark);
            return mark;
        }

        internal static float GroundHeight(Vector3 pos)
        {
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(pos, out float height))
            {
                return height;
            }
            return WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(pos.x, pos.z) : pos.y;
        }

        // Adds or replaces (same id) a mark in its layer; shared marks go through the host.
        internal static void Put(Mark mark)
        {
            if (!mark.Shared)
            {
                Upsert(Mine, mark);
                Changed();
                return;
            }
            if (MapData.IsHost)
            {
                Upsert(Shared, mark);
                Changed();
                Broadcast(RpcAdded, Package(mark));
            }
            else if (ZRoutedRpc.instance != null)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(RpcAdd, Package(mark));
            }
        }

        private static void Upsert(List<Mark> list, Mark mark)
        {
            int index = list.FindIndex(existing => existing.Id == mark.Id);
            if (index >= 0)
            {
                list[index] = mark;
            }
            else if (list.Count < MaxMarks)
            {
                list.Add(mark);
            }
        }

        // Whether friends with the mod see my marks: all of them move to the host's shared list, or back to mine
        // (marks of others stay as they are). Returns how many moved.
        internal static int SetMineShared(bool shared)
        {
            int moved = 0;
            long guide = GuideId;
            if (shared)
            {
                if (!SharedAvailable)
                {
                    return 0;
                }
                foreach (Mark mark in Mine.ToArray())
                {
                    Mine.Remove(mark);
                    mark.Shared = true;
                    Put(mark);
                    moved++;
                }
            }
            else
            {
                long me = MyId;
                foreach (Mark mark in Shared.ToArray())
                {
                    if (mark.Owner != me)
                    {
                        continue;
                    }
                    Remove(mark);
                    Upsert(Mine, new Mark
                    {
                        Id = mark.Id,
                        Owner = mark.Owner,
                        OwnerName = mark.OwnerName,
                        Pos = mark.Pos,
                        Color = mark.Color,
                        Name = mark.Name,
                        Shared = false,
                    });
                    moved++;
                }
            }
            GuideId = guide;
            Changed();
            return moved;
        }

        internal static string Remove(Mark mark)
        {
            if (mark == null)
            {
                return "";
            }
            if (GuideId == mark.Id)
            {
                GuideId = 0L;
            }
            if (!mark.Shared)
            {
                Mine.RemoveAll(existing => existing.Id == mark.Id);
                Changed();
                return Lang.F("Мітку «{0}» прибрано.", mark.Name);
            }
            ZPackage package = new ZPackage();
            package.Write(mark.Id);
            if (MapData.IsHost)
            {
                Shared.RemoveAll(existing => existing.Id == mark.Id);
                Changed();
                Broadcast(RpcRemoved, package);
            }
            else if (ZRoutedRpc.instance != null)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(RpcRemove, package);
            }
            return Lang.F("Мітку «{0}» прибрано для всіх.", mark.Name);
        }

        internal static string Rename(Mark mark, string name)
        {
            if (mark == null || string.IsNullOrEmpty(name) || name == mark.Name)
            {
                return "";
            }
            Mark renamed = new Mark
            {
                Id = mark.Id,
                Owner = mark.Owner,
                OwnerName = mark.OwnerName,
                Pos = mark.Pos,
                Color = mark.Color,
                Name = name.Length > 60 ? name.Substring(0, 60) : name,
                Shared = mark.Shared,
            };
            Put(renamed);
            return "";
        }

        // A new colour for a mark; new marks take the colour picked last.
        internal static void Recolor(Mark mark, int color)
        {
            if (mark == null)
            {
                return;
            }
            color = Mathf.Clamp(color, 0, SketchRules.Palette.Length - 1);
            MapSettings.WaypointColor = color;
            MapSettings.Changed();
            Put(new Mark
            {
                Id = mark.Id,
                Owner = mark.Owner,
                OwnerName = mark.OwnerName,
                Pos = mark.Pos,
                Color = color,
                Name = mark.Name,
                Shared = mark.Shared,
            });
        }

        // The nearest mark to a map point within a radius (meters), for Alt + right click.
        internal static Mark Nearest(Vector3 pos, float radius)
        {
            Mark best = null;
            float bestDistance = radius * radius;
            foreach (Mark mark in All())
            {
                float dx = mark.Pos.x - pos.x;
                float dz = mark.Pos.z - pos.z;
                float distance = dx * dx + dz * dz;
                if (distance <= bestDistance)
                {
                    best = mark;
                    bestDistance = distance;
                }
            }
            return best;
        }

        // Where the camera looks (terrain or anything solid), for the "mark where I look" key.
        internal static string AddWhereLooking()
        {
            Camera camera = Utils.GetMainCamera();
            if (camera == null || Player.m_localPlayer == null)
            {
                return Lang.T("Зайди у світ.");
            }
            Ray ray = new Ray(camera.transform.position, camera.transform.forward);
            Vector3 point = Physics.Raycast(ray, out RaycastHit hit, 2000f, ~0, QueryTriggerInteraction.Ignore)
                ? hit.point
                : camera.transform.position + camera.transform.forward * 100f;
            return Add(point, null);
        }

        private static long NewId()
        {
            byte[] bytes = new byte[8];
            s_random.NextBytes(bytes);
            return BitConverter.ToInt64(bytes, 0) & long.MaxValue;
        }

        // ---------------------------------------------------------------- network

        internal static void Register(ZRoutedRpc rpc)
        {
            rpc.Register<ZPackage>(RpcAdd, OnAdd);
            rpc.Register<ZPackage>(RpcAdded, OnAdded);
            rpc.Register<ZPackage>(RpcRemove, OnRemove);
            rpc.Register<ZPackage>(RpcRemoved, OnRemoved);
            rpc.Register<ZPackage>(RpcAll, OnAll);
        }

        private static ZPackage Package(Mark mark)
        {
            ZPackage package = new ZPackage();
            package.Write(Format(mark));
            return package;
        }

        private static void Broadcast(string method, ZPackage package)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, method, package);
        }

        private static void OnAdd(long sender, ZPackage package)
        {
            if (!MapData.IsHost)
            {
                return;
            }
            Mark mark = Parse(package.ReadString(), true);
            if (mark != null)
            {
                Upsert(Shared, mark);
                Changed();
                Broadcast(RpcAdded, Package(mark));
            }
        }

        private static void OnAdded(long sender, ZPackage package)
        {
            if (MapData.IsHost)
            {
                return;
            }
            Mark mark = Parse(package.ReadString(), true);
            if (mark != null)
            {
                s_sharedReceived = true;
                Upsert(Shared, mark);
                Version++;
            }
        }

        private static void OnRemove(long sender, ZPackage package)
        {
            if (!MapData.IsHost)
            {
                return;
            }
            long id = package.ReadLong();
            Shared.RemoveAll(mark => mark.Id == id);
            Changed();
            ZPackage removed = new ZPackage();
            removed.Write(id);
            Broadcast(RpcRemoved, removed);
        }

        private static void OnRemoved(long sender, ZPackage package)
        {
            if (MapData.IsHost)
            {
                return;
            }
            long id = package.ReadLong();
            Shared.RemoveAll(mark => mark.Id == id);
            if (GuideId == id)
            {
                GuideId = 0L;
            }
            Version++;
        }

        // Host: every shared mark to a player who asked for the map data.
        internal static void SendAll(long target)
        {
            if (!MapData.IsHost || ZRoutedRpc.instance == null)
            {
                return;
            }
            ZPackage package = new ZPackage();
            package.Write(Shared.Count);
            foreach (Mark mark in Shared)
            {
                package.Write(Format(mark));
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcAll, package);
        }

        private static void OnAll(long sender, ZPackage package)
        {
            if (MapData.IsHost)
            {
                return;
            }
            int count = Mathf.Clamp(package.ReadInt(), 0, MaxMarks);
            Shared.Clear();
            for (int i = 0; i < count; i++)
            {
                Mark mark = Parse(package.ReadString(), true);
                if (mark != null)
                {
                    Shared.Add(mark);
                }
            }
            s_sharedReceived = true;
            Version++;
        }

        internal static Color ColorOf(Mark mark)
        {
            int rgb = SketchRules.Palette[Mathf.Clamp(mark.Color, 0, SketchRules.Palette.Length - 1)];
            return new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
        }
    }

    // The marks in the 3D world: over each mark in view an icon, its name and the distance; the mark you follow
    // gets an arrow at the screen edge while it is out of view. Only drawn; nothing is placed in the world.
    internal sealed class WaypointOverlay : MonoBehaviour
    {
        private GUIStyle m_label;
        private float m_labelScale;
        private bool m_loggedError;

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }
            try
            {
                Player player = Player.m_localPlayer;
                Camera camera = Utils.GetMainCamera();
                if (player == null || camera == null || !MapSettings.WaypointsIn3D || !GameScreen.WorldMarkers
                    || (Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Large) || FreeCameraActive())
                {
                    return;
                }
                float scale = Mathf.Max(1f, Screen.height / 1080f);
                EnsureStyle(scale);
                GUI.depth = 20;
                GUI.matrix = Matrix4x4.identity;
                Vector3 eye = camera.transform.position;
                foreach (Waypoints.Mark mark in Waypoints.All())
                {
                    Draw(camera, eye, player, mark, scale);
                }
            }
            catch (Exception e)
            {
                if (!m_loggedError)
                {
                    m_loggedError = true;
                    Log.Error(e);
                }
            }
        }

        private static bool FreeCameraActive()
        {
            return FreeCamera.Active;
        }

        private void EnsureStyle(float scale)
        {
            if (m_label != null && Mathf.Approximately(scale, m_labelScale))
            {
                return;
            }
            m_labelScale = scale;
            GUISkin skin = HelperSkin.Get();
            m_label = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(15f * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperCenter,
                wordWrap = false,
                richText = true,
            };
            if (skin.font != null)
            {
                m_label.font = skin.font;
            }
        }

        private void Draw(Camera camera, Vector3 eye, Player player, Waypoints.Mark mark, float scale)
        {
            // The icon hangs a little above the ground so it is not hidden in grass.
            Vector3 world = mark.Pos + Vector3.up * 2.5f;
            float distance = Vector3.Distance(player.transform.position, mark.Pos);
            Vector3 viewport = camera.WorldToViewportPoint(world);
            bool guided = mark.Id == Waypoints.GuideId;
            Color color = Waypoints.ColorOf(mark);
            bool inView = viewport.z > 0f && viewport.x >= 0f && viewport.x <= 1f && viewport.y >= 0f && viewport.y <= 1f;
            if (inView)
            {
                Vector2 at = new Vector2(viewport.x * Screen.width, (1f - viewport.y) * Screen.height);
                // Near marks are big, far ones shrink to a readable minimum.
                float size = Mathf.Lerp(26f, 14f, Mathf.InverseLerp(20f, 1500f, distance)) * scale * (guided ? 1.25f : 1f);
                GUI.color = new Color(0f, 0f, 0f, 0.8f);
                GUI.DrawTexture(new Rect(at.x - size * 0.5f - 2f, at.y - size * 0.5f - 2f, size + 4f, size + 4f), HelperSkin.Circle);
                GUI.color = color;
                GUI.DrawTexture(new Rect(at.x - size * 0.5f, at.y - size * 0.5f, size, size), HelperSkin.Circle);
                if (guided)
                {
                    GUI.color = Color.white;
                    GUI.DrawTexture(new Rect(at.x - size, at.y - size, size * 2f, size * 2f), HelperSkin.Ring);
                }
                string text = $"{mark.Name}\n<color=#e0d8c0>{DistanceText(distance)}</color>";
                GUIContent content = new GUIContent(text);
                Vector2 textSize = m_label.CalcSize(content);
                Rect label = new Rect(at.x - textSize.x * 0.5f, at.y + size * 0.5f + 2f, textSize.x, textSize.y);
                GUI.color = new Color(0f, 0f, 0f, 0.9f);
                GUI.Label(new Rect(label.x + 1f, label.y + 1f, label.width, label.height), StripColor(text), m_label);
                GUI.color = Color.Lerp(color, Color.white, 0.4f);
                GUI.Label(label, content, m_label);
                GUI.color = Color.white;
                return;
            }
            if (!guided)
            {
                return;
            }
            // Out of view: an arrow on the screen edge in the mark's direction.
            Vector3 local = camera.transform.InverseTransformPoint(world);
            Vector2 direction = new Vector2(local.x, local.y);
            if (local.z < 0f)
            {
                direction = -direction;
                if (direction.sqrMagnitude < 0.0001f)
                {
                    direction = Vector2.down;
                }
            }
            direction.Normalize();
            Vector2 center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            float margin = 48f * scale;
            float halfW = center.x - margin;
            float halfH = center.y - margin;
            float t = Mathf.Min(halfW / Mathf.Max(0.0001f, Mathf.Abs(direction.x)), halfH / Mathf.Max(0.0001f, Mathf.Abs(direction.y)));
            Vector2 edge = center + new Vector2(direction.x, -direction.y) * t;
            float arrow = 30f * scale;
            float angle = Mathf.Atan2(-direction.y, direction.x) * Mathf.Rad2Deg;
            Matrix4x4 matrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, edge);
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.DrawTexture(new Rect(edge.x - arrow * 0.5f - 2f, edge.y - arrow * 0.5f - 2f, arrow + 4f, arrow + 4f), HelperSkin.Arrow);
            GUI.color = color;
            GUI.DrawTexture(new Rect(edge.x - arrow * 0.5f, edge.y - arrow * 0.5f, arrow, arrow), HelperSkin.Arrow);
            GUI.matrix = matrix;
            GUI.color = Color.white;
            string name = $"{mark.Name} · {DistanceText(distance)}";
            Vector2 nameSize = m_label.CalcSize(new GUIContent(name));
            Vector2 toward = center - edge;
            Vector2 labelAt = edge + toward.normalized * (arrow + 6f);
            Rect rect = new Rect(labelAt.x - nameSize.x * 0.5f, labelAt.y - nameSize.y * 0.5f, nameSize.x, nameSize.y);
            GUI.color = new Color(0f, 0f, 0f, 0.9f);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), name, m_label);
            GUI.color = Color.Lerp(color, Color.white, 0.4f);
            GUI.Label(rect, name, m_label);
            GUI.color = Color.white;
        }

        private static string StripColor(string text)
        {
            return text.Replace("<color=#e0d8c0>", "").Replace("</color>", "");
        }

        internal static string DistanceText(float meters)
        {
            return meters >= 1000f ? Lang.F("{0:0.0} км", meters / 1000f) : Lang.F("{0:0} м", meters);
        }
    }
}
