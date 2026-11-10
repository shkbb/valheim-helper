using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace ValheimHelper.Map
{
    // Drawing on the large map, in two layers:
    //  - "мої": only on this computer, saved per world next to the mod;
    //  - "спільні": kept by the host (saved per world), sent to every player with the mod, changed by any of them.
    // Lines are world coordinates, so they stay in place at any zoom and are also drawn on the minimap.
    // Nothing is written into the save or the world: the game's own map and pins are not touched.
    internal static class MapSketch
    {
        private const string RpcAdd = "ValheimHelper_SketchAdd";
        private const string RpcAdded = "ValheimHelper_SketchAdded";
        private const string RpcRemove = "ValheimHelper_SketchRemove";
        private const string RpcRemoved = "ValheimHelper_SketchRemoved";
        private const string RpcAll = "ValheimHelper_SketchAll";
        private const string RpcCursor = "ValheimHelper_SketchCursor";
        // Friends see where you draw in the shared layer: the pointer and the line being drawn, 10 times a second.
        private const float CursorSendSec = 0.1f;
        private const float CursorTimeoutSec = 1.5f;
        private const int MaxShared = 3000;
        private const int MaxMine = 3000;
        private const float PointSpacingPx = 3f;
        private const float EraserPx = 10f;

        // Ctrl is held on the open map (and no text is being typed): the mouse draws instead of moving the map.
        internal static bool CtrlHeld;
        // The pointer is on the map and Ctrl is held or a line is being drawn or erased: the game must not drag the
        // map, place or remove pins.
        internal static bool CapturesMouse;
        internal static bool SharedAvailable => MapData.IsHost || s_sharedReceived;
        internal static int MineCount => s_mine.Count;
        internal static int SharedCount => s_shared.Count;
        internal static int Version;

        private static readonly List<SketchStroke> s_mine = new List<SketchStroke>();
        private static readonly List<SketchStroke> s_shared = new List<SketchStroke>();
        private static SketchStroke s_drawing;
        private static bool s_drawingShared;
        private static Vector2 s_lastPointScreen;
        private static bool s_sharedReceived;
        private static long s_worldUid;
        private static bool s_mineDirty;
        private static bool s_sharedDirty;
        private static float s_saveAt;
        private static Material s_material;
        private static readonly System.Random s_random = new System.Random();

        private static string MinePath => Path.Combine(Path.Combine(Loader.Dir ?? ".", "maps"), s_worldUid.ToString(CultureInfo.InvariantCulture) + ".sketch.txt");
        private static string SharedPath => Path.Combine(Path.Combine(Loader.Dir ?? ".", "maps"), s_worldUid.ToString(CultureInfo.InvariantCulture) + ".shared_sketch.txt");

        private static long MyId => Game.instance != null ? Game.instance.GetPlayerProfile().GetPlayerID() : 0L;
        private static string MyName => Game.instance != null ? Game.instance.GetPlayerProfile().GetName() : "";

        // ---------------------------------------------------------------- session

        internal static void OnSession(long worldUid)
        {
            Flush();
            s_worldUid = worldUid;
            s_mine.Clear();
            s_shared.Clear();
            s_drawing = null;
            s_sharedReceived = false;
            ClearHistory();
            s_cursors.Clear();
            Load(MinePath, s_mine);
            if (MapData.IsHost)
            {
                Load(SharedPath, s_shared);
            }
            Version++;
        }

        internal static void Reset()
        {
            Flush();
            s_mine.Clear();
            s_shared.Clear();
            s_drawing = null;
            s_sharedReceived = false;
            ClearHistory();
            s_cursors.Clear();
            s_cursorShown = false;
            CtrlHeld = false;
            CapturesMouse = false;
            Version++;
        }

        internal static void Register(ZRoutedRpc rpc)
        {
            rpc.Register<ZPackage>(RpcAdd, OnAdd);
            rpc.Register<ZPackage>(RpcAdded, OnAdded);
            rpc.Register<ZPackage>(RpcRemove, OnRemove);
            rpc.Register<ZPackage>(RpcRemoved, OnRemoved);
            rpc.Register<ZPackage>(RpcAll, OnAll);
            rpc.Register<ZPackage>(RpcCursor, OnCursor);
        }

        internal static void Tick()
        {
            if ((s_mineDirty || s_sharedDirty) && Time.unscaledTime >= s_saveAt)
            {
                Flush();
            }
        }

        internal static void Flush()
        {
            if (s_worldUid == 0)
            {
                return;
            }
            if (s_mineDirty)
            {
                s_mineDirty = false;
                Save(MinePath, s_mine);
            }
            if (s_sharedDirty)
            {
                s_sharedDirty = false;
                if (MapData.IsHost)
                {
                    Save(SharedPath, s_shared);
                }
            }
        }

        private static void Changed(bool shared)
        {
            if (shared)
            {
                s_sharedDirty = true;
            }
            else
            {
                s_mineDirty = true;
            }
            s_saveAt = Time.unscaledTime + 1f;
            Version++;
        }

        private static void Load(string path, List<SketchStroke> into)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }
                foreach (string line in File.ReadAllLines(path))
                {
                    SketchStroke stroke = SketchRules.Parse(line);
                    if (stroke != null)
                    {
                        into.Add(stroke);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private static void Save(string path, List<SketchStroke> strokes)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                List<string> lines = new List<string>();
                foreach (SketchStroke stroke in strokes)
                {
                    lines.Add(SketchRules.Format(stroke));
                }
                File.WriteAllLines(path, lines.ToArray());
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        // ---------------------------------------------------------------- editing

        private static List<SketchStroke> Layer(bool shared)
        {
            return shared ? s_shared : s_mine;
        }

        // Whether the line was added (a shared one needs the host's layer).
        private static bool Commit(SketchStroke stroke, bool shared)
        {
            if (shared)
            {
                if (!SharedAvailable)
                {
                    return false;
                }
                AddShared(stroke);
                if (!MapData.IsHost && ZRoutedRpc.instance != null)
                {
                    ZRoutedRpc.instance.InvokeRoutedRPC(RpcAdd, Package(SketchRules.Format(stroke)));
                }
                else
                {
                    Broadcast(RpcAdded, Package(SketchRules.Format(stroke)));
                }
                return true;
            }
            if (s_mine.Count >= MaxMine)
            {
                s_mine.RemoveAt(0);
            }
            if (!s_mine.Exists(existing => existing.Id == stroke.Id))
            {
                s_mine.Add(stroke);
            }
            Changed(false);
            return true;
        }

        private static void AddShared(SketchStroke stroke)
        {
            foreach (RemoteCursor cursor in s_cursors.Values)
            {
                if (cursor.Preview != null && cursor.Preview.Id == stroke.Id)
                {
                    cursor.Preview = null;
                }
            }
            if (s_shared.Exists(existing => existing.Id == stroke.Id))
            {
                return;
            }
            if (s_shared.Count >= MaxShared)
            {
                s_shared.RemoveAt(0);
            }
            s_shared.Add(stroke);
            Changed(true);
        }

        private static void Remove(List<long> ids, bool shared)
        {
            if (ids.Count == 0)
            {
                return;
            }
            Layer(shared).RemoveAll(stroke => ids.Contains(stroke.Id));
            Changed(shared);
            if (!shared)
            {
                return;
            }
            ZPackage package = new ZPackage();
            package.Write(ids.Count);
            foreach (long id in ids)
            {
                package.Write(id);
            }
            if (!MapData.IsHost && ZRoutedRpc.instance != null)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(RpcRemove, package);
            }
            else
            {
                Broadcast(RpcRemoved, package);
            }
        }

        internal static string ClearMine(bool shared)
        {
            long me = MyId;
            SketchAction action = new SketchAction { Shared = shared };
            foreach (SketchStroke stroke in Layer(shared))
            {
                if (!shared || stroke.Owner == me)
                {
                    action.Removed.Add(stroke);
                }
            }
            Remove(action.Removed.ConvertAll(stroke => stroke.Id), shared);
            if (action.Removed.Count == 0)
            {
                return Lang.T("Твоїх ліній тут немає.");
            }
            Record(action);
            return Lang.F("Стерто ліній: {0}. Ctrl+Z на карті — повернути.", action.Removed.Count);
        }

        // Host only: every shared line, whoever drew it.
        internal static string ClearShared()
        {
            if (!MapData.IsHost)
            {
                return Lang.T("Стерти всі спільні лінії може лише хост.");
            }
            SketchAction action = new SketchAction { Shared = true };
            action.Removed.AddRange(s_shared);
            Remove(action.Removed.ConvertAll(stroke => stroke.Id), true);
            if (action.Removed.Count > 0)
            {
                Record(action);
            }
            return Lang.F("Спільні малюнки стерто ({0}). Ctrl+Z на карті — повернути.", action.Removed.Count);
        }

        // ---------------------------------------------------------------- network

        private static ZPackage Package(string line)
        {
            ZPackage package = new ZPackage();
            package.Write(line);
            return package;
        }

        private static void Broadcast(string method, ZPackage package)
        {
            if (ZRoutedRpc.instance != null)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, method, package);
            }
        }

        // Host: a player added a shared line.
        private static void OnAdd(long sender, ZPackage package)
        {
            if (!MapData.IsHost)
            {
                return;
            }
            string line = package.ReadString();
            SketchStroke stroke = SketchRules.Parse(line);
            if (stroke == null)
            {
                return;
            }
            AddShared(stroke);
            Broadcast(RpcAdded, Package(SketchRules.Format(stroke)));
        }

        private static void OnAdded(long sender, ZPackage package)
        {
            if (MapData.IsHost)
            {
                return;
            }
            SketchStroke stroke = SketchRules.Parse(package.ReadString());
            if (stroke != null)
            {
                s_sharedReceived = true;
                AddShared(stroke);
            }
        }

        private static List<long> ReadIds(ZPackage package)
        {
            int count = Mathf.Clamp(package.ReadInt(), 0, MaxShared);
            List<long> ids = new List<long>(count);
            for (int i = 0; i < count; i++)
            {
                ids.Add(package.ReadLong());
            }
            return ids;
        }

        private static void OnRemove(long sender, ZPackage package)
        {
            if (!MapData.IsHost)
            {
                return;
            }
            Remove(ReadIds(package), true);
        }

        private static void OnRemoved(long sender, ZPackage package)
        {
            if (MapData.IsHost)
            {
                return;
            }
            List<long> ids = ReadIds(package);
            s_shared.RemoveAll(stroke => ids.Contains(stroke.Id));
            Version++;
        }

        // Host: the whole shared layer to a player who asked for the map data.
        internal static void SendAll(long target)
        {
            if (!MapData.IsHost || ZRoutedRpc.instance == null)
            {
                return;
            }
            ZPackage inner = new ZPackage();
            inner.Write(s_shared.Count);
            foreach (SketchStroke stroke in s_shared)
            {
                inner.Write(SketchRules.Format(stroke));
            }
            ZPackage package = new ZPackage();
            package.WriteCompressed(inner);
            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcAll, package);
        }

        private static void OnAll(long sender, ZPackage package)
        {
            if (MapData.IsHost)
            {
                return;
            }
            ZPackage inner = package.ReadCompressedPackage();
            int count = Mathf.Clamp(inner.ReadInt(), 0, MaxShared);
            s_shared.Clear();
            for (int i = 0; i < count; i++)
            {
                SketchStroke stroke = SketchRules.Parse(inner.ReadString());
                if (stroke != null)
                {
                    s_shared.Add(stroke);
                }
            }
            s_sharedReceived = true;
            Version++;
        }

        // ---------------------------------------------------------------- input

        // Called every frame while the large map is open. pointerOnMap: over the map image and not over the panel.
        // Ctrl + left button draws, Ctrl + right button erases, Ctrl+Z undoes, Ctrl+Y (or Ctrl+Shift+Z) redoes.
        // A line or an erase started with Ctrl goes on until the button is released, even if Ctrl is let go.
        internal static void UpdateInput(Minimap map, Rect mapRect, bool pointerOnMap, Func<Vector3, Vector3> screenToWorld)
        {
            bool typing = MapOverlay.SearchFocused || Minimap.InTextInput() || (Chat.instance != null && Chat.instance.HasFocus());
            CtrlHeld = !typing && (ZInput.GetKey(KeyCode.LeftControl) || ZInput.GetKey(KeyCode.RightControl));
            CapturesMouse = pointerOnMap && (CtrlHeld || s_drawing != null || s_erasing != null);
            if (typing)
            {
                FinishStroke(map, mapRect);
                FinishErase();
                return;
            }
            if (CtrlHeld && s_drawing == null && s_erasing == null)
            {
                bool shift = ZInput.GetKey(KeyCode.LeftShift) || ZInput.GetKey(KeyCode.RightShift);
                if (ZInput.GetKeyDown(KeyCode.Z))
                {
                    MapOverlay.ShowMessage(shift ? Redo() : Undo());
                }
                else if (ZInput.GetKeyDown(KeyCode.Y))
                {
                    MapOverlay.ShowMessage(Redo());
                }
            }

            Vector3 pointer = ZInput.pointerPosition;
            Vector2 screen = new Vector2(pointer.x, pointer.y);
            if (s_drawing == null && s_erasing == null && CtrlHeld && pointerOnMap && ZInput.GetKeyDown(KeyCode.Mouse0))
            {
                Vector3 world = screenToWorld(pointer);
                s_drawingShared = MapSettings.SketchShared && SharedAvailable;
                s_drawing = new SketchStroke
                {
                    Id = NewId(),
                    Owner = MyId,
                    OwnerName = MyName,
                    Color = Mathf.Clamp(MapSettings.SketchColor, 0, SketchRules.Palette.Length - 1),
                    Width = Mathf.Clamp(MapSettings.SketchWidth, SketchRules.MinWidth, SketchRules.MaxWidth),
                };
                s_drawing.Points.Add(new P2(world.x, world.z));
                s_lastPointScreen = screen;
                Version++;
            }
            else if (s_drawing != null)
            {
                if (!ZInput.GetKey(KeyCode.Mouse0))
                {
                    FinishStroke(map, mapRect);
                }
                else if ((screen - s_lastPointScreen).sqrMagnitude >= PointSpacingPx * PointSpacingPx && s_drawing.Points.Count < SketchRules.MaxPoints)
                {
                    Vector3 world = screenToWorld(pointer);
                    s_drawing.Points.Add(new P2(world.x, world.z));
                    s_lastPointScreen = screen;
                    Version++;
                }
            }

            if (s_erasing == null && s_drawing == null && CtrlHeld && pointerOnMap && ZInput.GetKeyDown(KeyCode.Mouse1))
            {
                s_erasing = new SketchAction { Shared = MapSettings.SketchShared && SharedAvailable };
            }
            if (s_erasing != null)
            {
                if (!ZInput.GetKey(KeyCode.Mouse1))
                {
                    FinishErase();
                }
                else if (pointerOnMap)
                {
                    Vector3 world = screenToWorld(pointer);
                    float metersPerPixel = MetersPerPixel(map, mapRect);
                    P2 at = new P2(world.x, world.z);
                    List<SketchStroke> touched = new List<SketchStroke>();
                    foreach (SketchStroke stroke in Layer(s_erasing.Shared))
                    {
                        if (SketchRules.Touches(stroke, at, (EraserPx + stroke.Width * 0.5f) * metersPerPixel))
                        {
                            touched.Add(stroke);
                        }
                    }
                    if (touched.Count > 0)
                    {
                        s_erasing.Removed.AddRange(touched);
                        Remove(touched.ConvertAll(stroke => stroke.Id), s_erasing.Shared);
                    }
                }
            }
            if (pointerOnMap)
            {
                s_cursorWorld = screenToWorld(pointer);
            }
            SendCursor(s_cursorWorld, pointerOnMap);
        }

        // ---------------------------------------------------------------- friends' cursors

        private sealed class RemoteCursor
        {
            public string Name;
            public P2 At;
            public byte Mode;
            public int Color;
            public float Seen;
            public SketchStroke Preview;
        }

        private const byte CursorHidden = 0;
        private const byte CursorPen = 1;
        private const byte CursorEraser = 2;

        private static readonly Dictionary<long, RemoteCursor> s_cursors = new Dictionary<long, RemoteCursor>();
        private static float s_cursorSentAt;
        // Where the pointer was last over the map: a line dragged past the map's edge keeps its cursor there.
        private static Vector3 s_cursorWorld;
        private static bool s_cursorShown;
        private static long s_previewId;
        private static int s_previewSent;
        private static GUIStyle s_cursorName;

        // Only while drawing or erasing in the shared layer (or holding Ctrl over the map for it): drawing just for
        // yourself stays private.
        private static void SendCursor(Vector3 world, bool pointerOnMap)
        {
            if (ZRoutedRpc.instance == null)
            {
                return;
            }
            bool shared = MapSettings.SketchShared && SharedAvailable;
            byte mode = CursorHidden;
            if (s_erasing != null ? s_erasing.Shared : s_drawing != null ? s_drawingShared : shared && CtrlHeld)
            {
                mode = s_erasing != null || (s_drawing == null && ZInput.GetKey(KeyCode.Mouse1)) ? CursorEraser : CursorPen;
            }
            if (!pointerOnMap && s_drawing == null)
            {
                mode = CursorHidden;
            }
            if (mode == CursorHidden)
            {
                if (s_cursorShown)
                {
                    s_cursorShown = false;
                    ZPackage hidden = new ZPackage();
                    hidden.Write(CursorHidden);
                    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcCursor, hidden);
                }
                return;
            }
            if (Time.unscaledTime - s_cursorSentAt < CursorSendSec)
            {
                return;
            }
            s_cursorSentAt = Time.unscaledTime;
            s_cursorShown = true;
            ZPackage package = new ZPackage();
            package.Write(mode);
            package.Write(MyName ?? "");
            package.Write(world.x);
            package.Write(world.z);
            package.Write(Mathf.Clamp(MapSettings.SketchColor, 0, SketchRules.Palette.Length - 1));
            // The line being drawn: its id, width and the points the friends have not got yet.
            SketchStroke drawing = s_drawing != null && s_drawingShared ? s_drawing : null;
            if (drawing == null)
            {
                package.Write(0L);
            }
            else
            {
                if (drawing.Id != s_previewId)
                {
                    s_previewId = drawing.Id;
                    s_previewSent = 0;
                }
                int from = Mathf.Min(s_previewSent, drawing.Points.Count);
                int count = Mathf.Min(drawing.Points.Count - from, 200);
                package.Write(drawing.Id);
                package.Write(drawing.Color);
                package.Write(drawing.Width);
                package.Write(from);
                package.Write(count);
                for (int i = from; i < from + count; i++)
                {
                    package.Write(drawing.Points[i].X);
                    package.Write(drawing.Points[i].Y);
                }
                s_previewSent = from + count;
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcCursor, package);
        }

        private static void OnCursor(long sender, ZPackage package)
        {
            if (sender == ZDOMan.GetSessionID())
            {
                return;
            }
            byte mode = package.ReadByte();
            if (mode == CursorHidden)
            {
                s_cursors.Remove(sender);
                return;
            }
            if (!s_cursors.TryGetValue(sender, out RemoteCursor cursor))
            {
                cursor = new RemoteCursor();
                s_cursors[sender] = cursor;
            }
            cursor.Mode = mode;
            cursor.Name = package.ReadString();
            cursor.At = new P2(package.ReadSingle(), package.ReadSingle());
            cursor.Color = Mathf.Clamp(package.ReadInt(), 0, SketchRules.Palette.Length - 1);
            cursor.Seen = Time.unscaledTime;
            long id = package.ReadLong();
            if (id == 0L)
            {
                cursor.Preview = null;
                return;
            }
            int color = Mathf.Clamp(package.ReadInt(), 0, SketchRules.Palette.Length - 1);
            float width = Mathf.Clamp(package.ReadSingle(), SketchRules.MinWidth, SketchRules.MaxWidth);
            int from = package.ReadInt();
            int count = Mathf.Clamp(package.ReadInt(), 0, 200);
            if (cursor.Preview == null || cursor.Preview.Id != id)
            {
                // A line already finished (its shared copy arrived first) is not drawn again as a preview.
                if (s_shared.Exists(stroke => stroke.Id == id))
                {
                    return;
                }
                cursor.Preview = new SketchStroke { Id = id, Color = color, Width = width };
            }
            List<P2> points = cursor.Preview.Points;
            for (int i = 0; i < count; i++)
            {
                P2 point = new P2(package.ReadSingle(), package.ReadSingle());
                if (from + i == points.Count && points.Count < SketchRules.MaxPoints)
                {
                    points.Add(point);
                }
            }
            Version++;
        }

        // Friends' lines being drawn and their pointers with names, on the large map. GUI coordinates, Repaint only.
        internal static void DrawRemote(Minimap map, Rect uv, Rect guiRect)
        {
            if (Event.current.type != EventType.Repaint || s_cursors.Count == 0)
            {
                return;
            }
            List<long> gone = null;
            foreach (KeyValuePair<long, RemoteCursor> pair in s_cursors)
            {
                if (Time.unscaledTime - pair.Value.Seen > CursorTimeoutSec)
                {
                    (gone ?? (gone = new List<long>())).Add(pair.Key);
                }
            }
            if (gone != null)
            {
                foreach (long key in gone)
                {
                    s_cursors.Remove(key);
                }
            }
            if (s_cursors.Count == 0)
            {
                return;
            }
            if (MapSettings.SketchShowShared && EnsureMaterial())
            {
                GL.PushMatrix();
                s_material.SetPass(0);
                GL.LoadPixelMatrix();
                GL.Begin(GL.QUADS);
                foreach (RemoteCursor cursor in s_cursors.Values)
                {
                    if (cursor.Preview != null && cursor.Preview.Points.Count > 0)
                    {
                        DrawStroke(map, uv, guiRect, 1f, cursor.Preview);
                    }
                }
                GL.End();
                GL.PopMatrix();
            }
            if (s_cursorName == null)
            {
                s_cursorName = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, wordWrap = false };
            }
            Color previous = GUI.color;
            foreach (RemoteCursor cursor in s_cursors.Values)
            {
                P2 at = ToGui(map, uv, guiRect, cursor.At);
                if (!guiRect.Contains(new Vector2(at.X, at.Y)))
                {
                    continue;
                }
                int rgb = SketchRules.Palette[cursor.Color];
                Color color = new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
                if (cursor.Mode == CursorEraser)
                {
                    float ring = EraserPx * 2f;
                    GUI.color = new Color(1f, 1f, 1f, 0.9f);
                    GUI.DrawTexture(new Rect(at.X - ring * 0.5f, at.Y - ring * 0.5f, ring, ring), HelperSkin.Ring);
                }
                else
                {
                    GUI.color = new Color(0f, 0f, 0f, 0.75f);
                    GUI.DrawTexture(new Rect(at.X - 7f, at.Y - 7f, 14f, 14f), HelperSkin.Circle);
                    GUI.color = color;
                    GUI.DrawTexture(new Rect(at.X - 5f, at.Y - 5f, 10f, 10f), HelperSkin.Circle);
                }
                GUIContent name = new GUIContent(cursor.Name);
                Vector2 size = s_cursorName.CalcSize(name);
                Rect label = new Rect(at.X + 10f, at.Y - size.y - 2f, size.x, size.y);
                GUI.color = new Color(0f, 0f, 0f, 0.85f);
                GUI.Label(new Rect(label.x + 1f, label.y + 1f, label.width, label.height), name, s_cursorName);
                GUI.color = Color.Lerp(color, Color.white, 0.35f);
                GUI.Label(label, name, s_cursorName);
            }
            GUI.color = previous;
        }

        private static void FinishStroke(Minimap map, Rect mapRect)
        {
            SketchStroke stroke = s_drawing;
            s_drawing = null;
            if (stroke == null)
            {
                return;
            }
            Version++;
            // Points closer than a pixel to the line through their neighbours add nothing at this zoom.
            List<P2> simple = SketchRules.Simplify(stroke.Points, 0.8f * MetersPerPixel(map, mapRect));
            stroke.Points.Clear();
            stroke.Points.AddRange(simple);
            if (Commit(stroke, s_drawingShared))
            {
                SketchAction action = new SketchAction { Shared = s_drawingShared };
                action.Added.Add(stroke);
                Record(action);
            }
        }

        private static void FinishErase()
        {
            SketchAction action = s_erasing;
            s_erasing = null;
            if (action != null && action.Removed.Count > 0)
            {
                Record(action);
            }
        }

        // The map was closed: an unfinished line is dropped, an erase so far is kept (and can be undone).
        internal static void OnMapClosed()
        {
            CapturesMouse = false;
            CtrlHeld = false;
            if (s_cursorShown && ZRoutedRpc.instance != null)
            {
                s_cursorShown = false;
                ZPackage hidden = new ZPackage();
                hidden.Write(CursorHidden);
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcCursor, hidden);
            }
            FinishErase();
            if (s_drawing != null)
            {
                s_drawing = null;
                Version++;
            }
        }

        // ---------------------------------------------------------------- undo and redo

        // One step of Ctrl+Z: a drawn line, one erase (all lines wiped while the button was held) or a "Стерти…" button.
        private sealed class SketchAction
        {
            public bool Shared;
            public readonly List<SketchStroke> Added = new List<SketchStroke>();
            public readonly List<SketchStroke> Removed = new List<SketchStroke>();
        }

        private const int MaxHistory = 100;
        private static readonly List<SketchAction> s_undo = new List<SketchAction>();
        private static readonly List<SketchAction> s_redo = new List<SketchAction>();
        private static SketchAction s_erasing;

        internal static bool CanUndo => s_undo.Count > 0;
        internal static bool CanRedo => s_redo.Count > 0;

        private static void Record(SketchAction action)
        {
            s_undo.Add(action);
            if (s_undo.Count > MaxHistory)
            {
                s_undo.RemoveAt(0);
            }
            s_redo.Clear();
        }

        private static void ClearHistory()
        {
            s_undo.Clear();
            s_redo.Clear();
            s_erasing = null;
        }

        internal static string Undo()
        {
            if (s_undo.Count == 0)
            {
                return Lang.T("Немає чого скасовувати.");
            }
            SketchAction action = s_undo[s_undo.Count - 1];
            s_undo.RemoveAt(s_undo.Count - 1);
            if (action.Shared && !SharedAvailable)
            {
                return Lang.T("Спільні малюнки зараз недоступні: хост ще не надіслав їх.");
            }
            Remove(action.Added.ConvertAll(stroke => stroke.Id), action.Shared);
            foreach (SketchStroke stroke in action.Removed)
            {
                Commit(stroke, action.Shared);
            }
            s_redo.Add(action);
            if (action.Removed.Count == 0)
            {
                return Lang.T("Лінію прибрано (Ctrl+Y — повернути).");
            }
            return Lang.F("Стерте повернуто: ліній {0} (Ctrl+Y — стерти знову).", action.Removed.Count);
        }

        internal static string Redo()
        {
            if (s_redo.Count == 0)
            {
                return Lang.T("Немає чого повертати.");
            }
            SketchAction action = s_redo[s_redo.Count - 1];
            s_redo.RemoveAt(s_redo.Count - 1);
            if (action.Shared && !SharedAvailable)
            {
                return Lang.T("Спільні малюнки зараз недоступні: хост ще не надіслав їх.");
            }
            Remove(action.Removed.ConvertAll(stroke => stroke.Id), action.Shared);
            foreach (SketchStroke stroke in action.Added)
            {
                Commit(stroke, action.Shared);
            }
            s_undo.Add(action);
            return action.Removed.Count == 0 ? Lang.T("Лінію повернуто.") : Lang.F("Знову стерто ліній: {0}.", action.Removed.Count);
        }

        // ---------------------------------------------------------------- cursor

        // Under the pointer while Ctrl is held or a line is drawn: a dot of the line's colour and width; while erasing,
        // a ring of the eraser's size. GUI coordinates, identity GUI.matrix, Repaint only.
        internal static void DrawCursor(Vector2 guiPointer)
        {
            if (Event.current.type != EventType.Repaint || !(CtrlHeld || s_drawing != null || s_erasing != null))
            {
                return;
            }
            Color previous = GUI.color;
            if (s_erasing != null || (s_drawing == null && ZInput.GetKey(KeyCode.Mouse1)))
            {
                float size = EraserPx * 2f;
                GUI.color = new Color(1f, 1f, 1f, 0.9f);
                GUI.DrawTexture(new Rect(guiPointer.x - size * 0.5f, guiPointer.y - size * 0.5f, size, size), HelperSkin.Ring);
            }
            else
            {
                int rgb = SketchRules.Palette[Mathf.Clamp(MapSettings.SketchColor, 0, SketchRules.Palette.Length - 1)];
                float size = Mathf.Max(6f, Mathf.Clamp(MapSettings.SketchWidth, SketchRules.MinWidth, SketchRules.MaxWidth) + 2f);
                GUI.color = new Color(0f, 0f, 0f, 0.7f);
                GUI.DrawTexture(new Rect(guiPointer.x - size * 0.5f - 1f, guiPointer.y - size * 0.5f - 1f, size + 2f, size + 2f), HelperSkin.Circle);
                GUI.color = new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
                GUI.DrawTexture(new Rect(guiPointer.x - size * 0.5f, guiPointer.y - size * 0.5f, size, size), HelperSkin.Circle);
            }
            GUI.color = previous;
        }

        private static long NewId()
        {
            byte[] bytes = new byte[8];
            s_random.NextBytes(bytes);
            return BitConverter.ToInt64(bytes, 0) & long.MaxValue;
        }

        private static float MetersPerPixel(Minimap map, Rect mapRect)
        {
            return map.m_mapImageLarge.uvRect.height * map.m_textureSize * map.m_pixelSize / Mathf.Max(1f, mapRect.height);
        }

        // ---------------------------------------------------------------- drawing

        // Draws the visible layers into guiRect (GUI coordinates, identity GUI.matrix); widthScale shrinks lines on the minimap.
        internal static void Draw(Minimap map, Rect uv, Rect guiRect, float widthScale)
        {
            if (Event.current.type != EventType.Repaint || (s_mine.Count == 0 && s_shared.Count == 0 && s_drawing == null))
            {
                return;
            }
            if (!EnsureMaterial())
            {
                return;
            }
            GL.PushMatrix();
            s_material.SetPass(0);
            GL.LoadPixelMatrix();
            GL.Begin(GL.QUADS);
            if (MapSettings.SketchShowShared)
            {
                DrawLayer(map, uv, guiRect, widthScale, s_shared);
            }
            if (MapSettings.SketchShowMine)
            {
                DrawLayer(map, uv, guiRect, widthScale, s_mine);
            }
            if (s_drawing != null)
            {
                DrawStroke(map, uv, guiRect, widthScale, s_drawing);
            }
            GL.End();
            GL.PopMatrix();
        }

        private static void DrawLayer(Minimap map, Rect uv, Rect guiRect, float widthScale, List<SketchStroke> strokes)
        {
            // The part of the world in view; strokes outside it are not converted to the screen at all.
            float half = map.m_textureSize / 2f;
            float minX = (uv.xMin * map.m_textureSize - half) * map.m_pixelSize;
            float maxX = (uv.xMax * map.m_textureSize - half) * map.m_pixelSize;
            float minZ = (uv.yMin * map.m_textureSize - half) * map.m_pixelSize;
            float maxZ = (uv.yMax * map.m_textureSize - half) * map.m_pixelSize;
            float margin = (maxX - minX) * 0.02f;
            foreach (SketchStroke stroke in strokes)
            {
                if (SketchRules.Intersects(stroke, minX, minZ, maxX, maxZ, margin))
                {
                    DrawStroke(map, uv, guiRect, widthScale, stroke);
                }
            }
        }

        private static void DrawStroke(Minimap map, Rect uv, Rect guiRect, float widthScale, SketchStroke stroke)
        {
            int rgb = SketchRules.Palette[Mathf.Clamp(stroke.Color, 0, SketchRules.Palette.Length - 1)];
            Color color = new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 0.95f);
            // A dark edge keeps light lines readable on snow and dark ones on forest.
            Color edge = color.grayscale > 0.35f ? new Color(0f, 0f, 0f, 0.55f) : new Color(1f, 1f, 1f, 0.45f);
            float half = Mathf.Max(1f, stroke.Width * widthScale * 0.5f);
            for (int pass = 0; pass < 2; pass++)
            {
                GL.Color(pass == 0 ? edge : color);
                float width = pass == 0 ? half + 1.2f : half;
                P2 previous = default;
                for (int i = 0; i < stroke.Points.Count; i++)
                {
                    P2 current = ToGui(map, uv, guiRect, stroke.Points[i]);
                    if (stroke.Points.Count == 1)
                    {
                        Dot(current, width, guiRect);
                    }
                    else if (i > 0)
                    {
                        Segment(previous, current, width, guiRect);
                    }
                    previous = current;
                }
            }
        }

        private static P2 ToGui(Minimap map, Rect uv, Rect guiRect, P2 world)
        {
            float half = map.m_textureSize / 2f;
            float mx = (world.X / map.m_pixelSize + half) / map.m_textureSize;
            float my = (world.Y / map.m_pixelSize + half) / map.m_textureSize;
            return new P2(guiRect.xMin + (mx - uv.xMin) / uv.width * guiRect.width, guiRect.yMax - (my - uv.yMin) / uv.height * guiRect.height);
        }

        private static void Segment(P2 a, P2 b, float half, Rect clip)
        {
            if (!SketchRules.Clip(ref a, ref b, clip.xMin, clip.yMin, clip.xMax, clip.yMax))
            {
                return;
            }
            float dx = b.X - a.X;
            float dy = b.Y - a.Y;
            float length = Mathf.Sqrt(dx * dx + dy * dy);
            if (length < 0.01f)
            {
                Dot(a, half, clip);
                return;
            }
            float nx = -dy / length * half;
            float ny = dx / length * half;
            float height = Screen.height;
            GL.Vertex3(a.X + nx, height - (a.Y + ny), 0f);
            GL.Vertex3(b.X + nx, height - (b.Y + ny), 0f);
            GL.Vertex3(b.X - nx, height - (b.Y - ny), 0f);
            GL.Vertex3(a.X - nx, height - (a.Y - ny), 0f);
            // A square at the joint hides the gap between two segments at an angle.
            Dot(b, half, clip);
        }

        private static void Dot(P2 p, float half, Rect clip)
        {
            if (p.X < clip.xMin || p.X > clip.xMax || p.Y < clip.yMin || p.Y > clip.yMax)
            {
                return;
            }
            float height = Screen.height;
            GL.Vertex3(p.X - half, height - (p.Y - half), 0f);
            GL.Vertex3(p.X + half, height - (p.Y - half), 0f);
            GL.Vertex3(p.X + half, height - (p.Y + half), 0f);
            GL.Vertex3(p.X - half, height - (p.Y + half), 0f);
        }

        private static bool EnsureMaterial()
        {
            if (s_material != null)
            {
                return true;
            }
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                return false;
            }
            s_material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            s_material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            s_material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            s_material.SetInt("_Cull", (int)CullMode.Off);
            s_material.SetInt("_ZWrite", 0);
            s_material.SetInt("_ZTest", (int)CompareFunction.Always);
            return true;
        }
    }
}
