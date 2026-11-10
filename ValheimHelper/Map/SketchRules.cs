using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimHelper.Map
{
    internal struct P2
    {
        public float X;
        public float Y;

        public P2(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    // One line drawn on the map. Points are world X/Z in metres.
    internal sealed class SketchStroke
    {
        public long Id;
        public long Owner;
        public string OwnerName = "";
        public int Color;
        public float Width;
        public readonly List<P2> Points = new List<P2>();
        // World bounds, recomputed when the number of points changes (see SketchRules.Intersects).
        internal float MinX;
        internal float MinY;
        internal float MaxX;
        internal float MaxY;
        internal int BoundsCount = -1;
    }

    // Pure rules of map drawing without Unity types, so tools/SortTests can check them.
    internal static class SketchRules
    {
        internal const int MaxPoints = 1500;
        internal const float MinWidth = 2f;
        internal const float MaxWidth = 12f;

        // Colours as 0xRRGGBB.
        internal static readonly int[] Palette = { 0xFF4040, 0xFFB030, 0xFFE44A, 0x5BE35B, 0x4AC8FF, 0x6A7BFF, 0xE070FF, 0xF2F2F2, 0x202020 };

        // Ramer–Douglas–Peucker: drops points that lie within tolerance of the line through their neighbours.
        internal static List<P2> Simplify(IList<P2> points, float tolerance)
        {
            List<P2> result = new List<P2>();
            if (points.Count <= 2)
            {
                result.AddRange(points);
                return result;
            }
            bool[] keep = new bool[points.Count];
            keep[0] = keep[points.Count - 1] = true;
            Stack<KeyValuePair<int, int>> ranges = new Stack<KeyValuePair<int, int>>();
            ranges.Push(new KeyValuePair<int, int>(0, points.Count - 1));
            while (ranges.Count > 0)
            {
                KeyValuePair<int, int> range = ranges.Pop();
                float worst = -1f;
                int index = -1;
                for (int i = range.Key + 1; i < range.Value; i++)
                {
                    float distance = DistanceToSegment(points[i], points[range.Key], points[range.Value]);
                    if (distance > worst)
                    {
                        worst = distance;
                        index = i;
                    }
                }
                if (index >= 0 && worst > tolerance)
                {
                    keep[index] = true;
                    ranges.Push(new KeyValuePair<int, int>(range.Key, index));
                    ranges.Push(new KeyValuePair<int, int>(index, range.Value));
                }
            }
            for (int i = 0; i < points.Count; i++)
            {
                if (keep[i])
                {
                    result.Add(points[i]);
                }
            }
            return result;
        }

        internal static float DistanceToSegment(P2 p, P2 a, P2 b)
        {
            float dx = b.X - a.X;
            float dy = b.Y - a.Y;
            float lengthSqr = dx * dx + dy * dy;
            float t = lengthSqr > 0f ? ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSqr : 0f;
            t = Math.Max(0f, Math.Min(1f, t));
            float x = a.X + t * dx - p.X;
            float y = a.Y + t * dy - p.Y;
            return (float)Math.Sqrt(x * x + y * y);
        }

        // Whether the stroke can be seen in this world rectangle (grown by margin), so strokes far from the view are skipped.
        internal static bool Intersects(SketchStroke stroke, float minX, float minY, float maxX, float maxY, float margin)
        {
            if (stroke.BoundsCount != stroke.Points.Count)
            {
                stroke.BoundsCount = stroke.Points.Count;
                stroke.MinX = stroke.MinY = float.MaxValue;
                stroke.MaxX = stroke.MaxY = float.MinValue;
                foreach (P2 point in stroke.Points)
                {
                    stroke.MinX = Math.Min(stroke.MinX, point.X);
                    stroke.MinY = Math.Min(stroke.MinY, point.Y);
                    stroke.MaxX = Math.Max(stroke.MaxX, point.X);
                    stroke.MaxY = Math.Max(stroke.MaxY, point.Y);
                }
            }
            return stroke.Points.Count > 0 && stroke.MaxX >= minX - margin && stroke.MinX <= maxX + margin
                && stroke.MaxY >= minY - margin && stroke.MinY <= maxY + margin;
        }

        // Whether the eraser at p with this radius touches the stroke.
        internal static bool Touches(SketchStroke stroke, P2 p, float radius)
        {
            if (!Intersects(stroke, p.X, p.Y, p.X, p.Y, radius))
            {
                return false;
            }
            List<P2> points = stroke.Points;
            if (points.Count == 1)
            {
                return DistanceToSegment(p, points[0], points[0]) <= radius;
            }
            for (int i = 1; i < points.Count; i++)
            {
                if (DistanceToSegment(p, points[i - 1], points[i]) <= radius)
                {
                    return true;
                }
            }
            return false;
        }

        // Liang–Barsky: the part of segment a–b inside the rectangle, or false when nothing is inside.
        internal static bool Clip(ref P2 a, ref P2 b, float minX, float minY, float maxX, float maxY)
        {
            float t0 = 0f;
            float t1 = 1f;
            float dx = b.X - a.X;
            float dy = b.Y - a.Y;
            if (!ClipEdge(-dx, a.X - minX, ref t0, ref t1) || !ClipEdge(dx, maxX - a.X, ref t0, ref t1)
                || !ClipEdge(-dy, a.Y - minY, ref t0, ref t1) || !ClipEdge(dy, maxY - a.Y, ref t0, ref t1))
            {
                return false;
            }
            P2 start = new P2(a.X + t0 * dx, a.Y + t0 * dy);
            P2 end = new P2(a.X + t1 * dx, a.Y + t1 * dy);
            a = start;
            b = end;
            return true;
        }

        private static bool ClipEdge(float p, float q, ref float t0, ref float t1)
        {
            if (p == 0f)
            {
                return q >= 0f;
            }
            float r = q / p;
            if (p < 0f)
            {
                if (r > t1)
                {
                    return false;
                }
                t0 = Math.Max(t0, r);
            }
            else
            {
                if (r < t0)
                {
                    return false;
                }
                t1 = Math.Min(t1, r);
            }
            return true;
        }

        // "id|owner|name|colour|width|x,z;x,z;…" — the name has "|" and ";" removed.
        internal static string Format(SketchStroke stroke)
        {
            StringBuilder text = new StringBuilder();
            text.Append(stroke.Id.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(stroke.Owner.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append((stroke.OwnerName ?? "").Replace("|", "").Replace(";", "").Replace("\n", "")).Append('|')
                .Append(stroke.Color.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(stroke.Width.ToString("0.#", CultureInfo.InvariantCulture)).Append('|');
            for (int i = 0; i < stroke.Points.Count; i++)
            {
                if (i > 0)
                {
                    text.Append(';');
                }
                text.Append(stroke.Points[i].X.ToString("0.#", CultureInfo.InvariantCulture)).Append(',')
                    .Append(stroke.Points[i].Y.ToString("0.#", CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        internal static SketchStroke Parse(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return null;
            }
            string[] parts = line.Split('|');
            if (parts.Length != 6
                || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long owner)
                || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int color)
                || !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float width))
            {
                return null;
            }
            SketchStroke stroke = new SketchStroke
            {
                Id = id,
                Owner = owner,
                OwnerName = parts[2],
                Color = Math.Max(0, Math.Min(Palette.Length - 1, color)),
                Width = Math.Max(MinWidth, Math.Min(MaxWidth, width)),
            };
            foreach (string pair in parts[5].Split(';'))
            {
                int comma = pair.IndexOf(',');
                if (comma > 0
                    && float.TryParse(pair.Substring(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(pair.Substring(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                    && !float.IsNaN(x) && !float.IsNaN(y) && !float.IsInfinity(x) && !float.IsInfinity(y))
                {
                    stroke.Points.Add(new P2(x, y));
                    if (stroke.Points.Count >= MaxPoints)
                    {
                        break;
                    }
                }
            }
            return stroke.Points.Count > 0 ? stroke : null;
        }
    }
}
