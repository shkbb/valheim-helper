using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimHelper
{
    // View modes for building: only the frame (beams and poles), only walls, no roofs... The other pieces are just
    // not drawn on this computer (Renderer.forceRenderingOff, the same switch the performance layer uses for far
    // details); they stay in the world, still solid, and pieces still snap to them. Nothing is changed in the world
    // and friends see everything as usual.
    internal static class BuilderView
    {
        internal enum Kind
        {
            Frame,
            Walls,
            Roofs,
            Floors,
            Openings,
            Other,
        }

        internal static readonly string[] KindNames = { Lang.T("Опори й балки"), Lang.T("Стіни"), Lang.T("Дахи"), Lang.T("Підлоги й сходи"), Lang.T("Двері й вікна"), Lang.T("Решта") };

        // Which kinds are drawn; all by default.
        internal static readonly bool[] Shown = { true, true, true, true, true, true };
        internal static int Version;

        private const float RefreshSec = 1f;

        private static readonly HashSet<WearNTear> s_hidden = new HashSet<WearNTear>();
        private static readonly Dictionary<string, Kind> s_kinds = new Dictionary<string, Kind>();
        private static float s_refreshAt;
        private static int s_appliedVersion = -1;

        internal static bool Active
        {
            get
            {
                foreach (bool shown in Shown)
                {
                    if (!shown)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        internal static int HiddenCount => s_hidden.Count;

        // For the performance layer: a piece hidden here stays hidden whatever its distance.
        internal static bool Hides(WearNTear piece)
        {
            return piece != null && s_hidden.Contains(piece);
        }

        internal static void Set(Kind kind, bool shown)
        {
            if (Shown[(int)kind] != shown)
            {
                Shown[(int)kind] = shown;
                Version++;
            }
        }

        // Presets: only the given kinds are shown (none given: everything).
        internal static string Preset(string name, params Kind[] kinds)
        {
            for (int i = 0; i < Shown.Length; i++)
            {
                Shown[i] = kinds.Length == 0 || Array.IndexOf(kinds, (Kind)i) >= 0;
            }
            Version++;
            return kinds.Length == 0 ? Lang.T("Видно всі будівлі.") : Lang.F("Режим перегляду: {0}.", name);
        }

        private static readonly string[] s_presetNames = { Lang.T("усе"), Lang.T("лише каркас"), Lang.T("каркас і підлоги"), Lang.T("без дахів"), Lang.T("лише стіни") };
        private static int s_preset;

        // For a key: all → frame → frame and floors → no roofs → walls only → all.
        internal static string NextPreset()
        {
            s_preset = (s_preset + 1) % s_presetNames.Length;
            switch (s_preset)
            {
                case 1: return Preset(s_presetNames[1], Kind.Frame);
                case 2: return Preset(s_presetNames[2], Kind.Frame, Kind.Floors);
                case 3: return Preset(s_presetNames[3], Kind.Frame, Kind.Walls, Kind.Floors, Kind.Openings, Kind.Other);
                case 4: return Preset(s_presetNames[4], Kind.Walls, Kind.Openings);
                default: return Preset(s_presetNames[0]);
            }
        }

        internal static Kind KindOf(WearNTear wear)
        {
            string name = wear.gameObject.name;
            int cut = name.IndexOf('(');
            if (cut > 0)
            {
                name = name.Substring(0, cut);
            }
            name = name.Trim();
            if (s_kinds.TryGetValue(name, out Kind kind))
            {
                return kind;
            }
            Piece piece = wear.GetComponent<Piece>();
            kind = Classify(name.ToLowerInvariant(), piece);
            s_kinds[name] = kind;
            return kind;
        }

        // Structural pieces by their prefab names; furniture, stations and the rest are "Решта".
        private static Kind Classify(string name, Piece piece)
        {
            bool structural = piece == null || piece.m_category == Piece.PieceCategory.BuildingWorkbench
                || piece.m_category == Piece.PieceCategory.BuildingStonecutter || piece.m_category == Piece.PieceCategory.DeepNorth;
            if (!structural)
            {
                return Kind.Other;
            }
            if (Has(name, "roof"))
            {
                return Kind.Roofs;
            }
            if (Has(name, "door", "gate", "window", "shutter", "hatch"))
            {
                return Kind.Openings;
            }
            if (Has(name, "floor", "stair", "ladder", "step"))
            {
                return Kind.Floors;
            }
            if (Has(name, "beam", "pole", "pillar", "column", "arch", "log", "rafter", "support"))
            {
                return Kind.Frame;
            }
            if (Has(name, "wall", "fence", "palisade", "stake", "divider", "half"))
            {
                return Kind.Walls;
            }
            return Kind.Other;
        }

        private static bool Has(string name, params string[] parts)
        {
            foreach (string part in parts)
            {
                if (name.Contains(part))
                {
                    return true;
                }
            }
            return false;
        }

        // Every frame; the loaded pieces are looked through once a second or when the mode changes.
        internal static void Tick()
        {
            bool active = Active && Player.m_localPlayer != null;
            if (!active)
            {
                if (s_hidden.Count > 0)
                {
                    ShowAll();
                }
                return;
            }
            if (s_appliedVersion == Version && Time.unscaledTime < s_refreshAt)
            {
                return;
            }
            s_appliedVersion = Version;
            s_refreshAt = Time.unscaledTime + RefreshSec;
            s_hidden.RemoveWhere(piece => piece == null);
            foreach (WearNTear wear in WearNTear.GetAllInstances())
            {
                if (wear == null)
                {
                    continue;
                }
                bool hide = !Shown[(int)KindOf(wear)];
                bool hidden = s_hidden.Contains(wear);
                if (hide == hidden)
                {
                    continue;
                }
                if (hide)
                {
                    s_hidden.Add(wear);
                }
                else
                {
                    s_hidden.Remove(wear);
                }
                Apply(wear, hide);
            }
        }

        private static void ShowAll()
        {
            foreach (WearNTear wear in s_hidden)
            {
                if (wear != null)
                {
                    Apply(wear, false);
                }
            }
            s_hidden.Clear();
        }

        // Hidden here, or back to what the performance layer wants for it (it may hide far details itself).
        private static void Apply(WearNTear wear, bool hide)
        {
            bool farHidden = !hide && Perf.RenderLayer.IsDetailHidden(wear);
            foreach (Renderer renderer in wear.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is ParticleSystemRenderer)
                {
                    continue;
                }
                renderer.forceRenderingOff = hide || farHidden;
            }
        }
    }
}
