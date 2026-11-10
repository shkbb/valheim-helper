using System;
using System.Collections.Generic;

namespace ValheimHelper.Perf
{
    // How strongly the render layer works: below these sizes on screen (in pixels at the current resolution) a
    // building detail is not drawn, stops casting shadows, and its fire sleeps.
    internal struct LodPreset
    {
        public string Name;
        public float RenderPx;
        public float ShadowPx;
        public float FirePx;
        // Point lights (torches, fires) that keep their shadows at once.
        public int ShadowLights;
        // Beyond this distance a light stops flickering (the flicker is not visible from there).
        public float FlickerDistance;
        // Point lights that stay on at once, the most visible first.
        public int Lights;
        // Lights this close to the player always stay on.
        public float NearLights;
    }

    // Pure rules of the render layer without Unity types, so tools/SortTests can check them.
    internal static class LodRules
    {
        internal static readonly LodPreset[] Presets =
        {
            new LodPreset { Name = Lang.T("Обережний"), RenderPx = 1.5f, ShadowPx = 8f, FirePx = 4f, ShadowLights = 6, FlickerDistance = 45f, Lights = 128, NearLights = 15f },
            new LodPreset { Name = Lang.T("Збалансований"), RenderPx = 2.5f, ShadowPx = 18f, FirePx = 8f, ShadowLights = 4, FlickerDistance = 30f, Lights = 64, NearLights = 12f },
            new LodPreset { Name = Lang.T("Максимальний"), RenderPx = 4f, ShadowPx = 32f, FirePx = 14f, ShadowLights = 2, FlickerDistance = 20f, Lights = 32, NearLights = 8f },
        };

        // A hidden object comes back only when it is this much bigger than the limit, so it does not blink at the border.
        internal const float Hysteresis = 1.3f;
        // A light that has its shadow keeps it unless another light is this much more important.
        internal const float KeepShadowBonus = 1.25f;

        // Height on screen, in pixels, of a sphere of this radius at this distance from the camera.
        internal static float ProjectedPixels(float radius, float distance, float screenHeight, float tanHalfFov)
        {
            if (distance <= radius)
            {
                return float.MaxValue;
            }
            return radius * screenHeight / (distance * Math.Max(0.0001f, tanHalfFov));
        }

        // A looping sound is paused a little beyond its max distance. With logarithmic rolloff the sound does not
        // reach silence at the max distance, so it keeps a wider margin.
        internal const float AudibleHysteresis = 3f;

        internal static float AudibleDistance(float maxDistance, bool logarithmic)
        {
            return Math.Max(1f, maxDistance) * (logarithmic ? 1.5f : 1.05f) + 1f;
        }

        // Which point lights stay on: lights near the player first, then those in view by how big their lit area looks,
        // then the ones out of view (they are dropped first when there are too many).
        internal static float LightScore(float range, float distance, bool seen, bool near)
        {
            float size = range / Math.Max(1f, distance);
            return (near ? 1000f : 0f) + (seen ? size : size * 0.01f);
        }

        internal static bool Below(bool belowNow, float pixels, float limit)
        {
            if (limit <= 0f)
            {
                return false;
            }
            return belowNow ? pixels < limit * Hysteresis : pixels < limit;
        }

        // Which lights keep their shadows: the most important ones (score > 0), at most budget of them.
        // hadShadow gives the current owners a bonus so shadows do not jump between two similar lights.
        internal static bool[] ShadowBudget(IList<float> scores, IList<bool> hadShadow, int budget)
        {
            bool[] chosen = new bool[scores.Count];
            if (budget <= 0)
            {
                return chosen;
            }
            List<int> order = new List<int>();
            for (int i = 0; i < scores.Count; i++)
            {
                if (scores[i] > 0f)
                {
                    order.Add(i);
                }
            }
            Func<int, float> weight = i => scores[i] * (hadShadow[i] ? KeepShadowBonus : 1f);
            order.Sort((a, b) =>
            {
                int byWeight = weight(b).CompareTo(weight(a));
                return byWeight != 0 ? byWeight : a.CompareTo(b);
            });
            for (int i = 0; i < order.Count && i < budget; i++)
            {
                chosen[order[i]] = true;
            }
            return chosen;
        }

        // Frame statistics of a benchmark window: average FPS and "1% low" (FPS of the slowest 1% of frames).
        internal static void FrameStats(IList<float> frameSeconds, out float averageFps, out float lowFps, out float averageMs)
        {
            averageFps = lowFps = averageMs = 0f;
            if (frameSeconds.Count == 0)
            {
                return;
            }
            double total = 0;
            foreach (float seconds in frameSeconds)
            {
                total += seconds;
            }
            averageMs = (float)(total / frameSeconds.Count * 1000.0);
            averageFps = total > 0 ? (float)(frameSeconds.Count / total) : 0f;
            List<float> sorted = new List<float>(frameSeconds);
            sorted.Sort((a, b) => b.CompareTo(a));
            int worst = Math.Max(1, sorted.Count / 100);
            double worstTotal = 0;
            for (int i = 0; i < worst; i++)
            {
                worstTotal += sorted[i];
            }
            lowFps = worstTotal > 0 ? (float)(worst / worstTotal) : 0f;
        }
    }
}
