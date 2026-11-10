using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace ValheimHelper.Perf
{
    // The mod's own render layer for large bases. Only how things are drawn and heard changes, never what happens in
    // the world: pieces, fires, smoke, support and damage keep running exactly as in the game, so nothing touches
    // achievements.
    //  - Instancing: GPU instancing for the materials of building pieces.
    //  - Detail: details only a few pixels tall are not drawn, small ones stop casting shadows.
    //  - Fire: particles of fires out of view or tiny on screen are paused; far or unseen lights stop flickering.
    //  - Sound: looping sounds (fires, stations) farther than they can be heard are paused.
    //  - Lights: when more point lights are on than the budget, the least visible ones are switched off through the
    //    game's own light LOD (it fades them), and only the most visible ones keep shadows.
    // Building pieces are looked at a slice per frame; pieces with effects a third per frame.
    internal sealed class RenderLayer : MonoBehaviour
    {
        internal const int FeatureInstancing = 1;
        internal const int FeatureDetail = 2;
        internal const int FeatureFire = 4;
        internal const int FeatureSound = 8;
        internal const int FeatureLights = 16;
        internal const int AllFeatures = 31;

        private const int SyncChecksPerFrame = 2000;
        private const int NewPiecesPerFrame = 150;
        private const float SyncEverySec = 2f;
        private const float BudgetEverySec = 0.5f;
        // Flames rise above the piece; the piece bounds are grown by this much for the view test of its fire.
        private const float FireBoundsMargin = 3f;
        // Lights just outside the view still count as seen, so turning the camera a little does not switch lights.
        private const float ViewMarginDegrees = 25f;

        private static readonly AccessTools.FieldRef<LightLod, float> s_lightBaseRange = AccessTools.FieldRefAccess<LightLod, float>("m_baseRange");

        private sealed class PieceEntry
        {
            public WearNTear Piece;
            public Bounds Bounds;
            public float Radius;
            public Renderer[] Renderers;
            public ShadowCastingMode[] Shadows;
            public bool Hidden;
            public bool NoShadow;
            public FxEntry Fx;
        }

        private sealed class FxEntry
        {
            public PieceEntry Owner;
            public ParticleSystem[] Particles;
            public bool[] Paused;
            public bool Asleep;
            public LightFlicker[] Flickers;
            public Light[] FlickerLights;
            public LightLod[] FlickerLods;
            public float[] FlickerIntensity;
            public bool[] FlickerOff;
            public AudioSource[] Sounds;
            public bool[] Silenced;
            public LightLod[] Lights;
            public float[] LightDistance;
            public float[] ShadowDistance;
            public bool[] LightOff;
            public bool[] HasShadow;
        }

        // Benchmarks: ForceOff switches the whole layer off; BenchMask (when >= 0) selects exactly these features.
        internal static bool ForceOff;
        internal static int BenchMask = -1;

        internal static int PieceCount => s_entries.Count;
        internal static int HiddenCount;
        internal static int NoShadowCount;
        internal static int SleepingFires;
        internal static int PausedSystems;
        internal static int FrozenFlickers;
        internal static int SilencedSounds;
        internal static int LightsConsidered;
        internal static int LightsOff;
        internal static int ShadowLightsKept;
        internal static int InstancedMaterials => s_instanced.Count;
        internal static double LastCostMs;

        private static readonly List<PieceEntry> s_entries = new List<PieceEntry>();
        private static readonly List<FxEntry> s_fx = new List<FxEntry>();
        private static readonly HashSet<WearNTear> s_known = new HashSet<WearNTear>();
        private static readonly Dictionary<WearNTear, PieceEntry> s_byPiece = new Dictionary<WearNTear, PieceEntry>();

        // Whether the layer keeps this piece's detail hidden now (too far to matter); the builder view asks when it
        // shows a piece again.
        internal static bool IsDetailHidden(WearNTear piece)
        {
            return piece != null && s_byPiece.TryGetValue(piece, out PieceEntry entry) && entry.Hidden;
        }
        private static readonly HashSet<Material> s_instanced = new HashSet<Material>();

        private readonly Plane[] m_planes = new Plane[6];
        private readonly List<Renderer> m_rendererBuffer = new List<Renderer>();
        private readonly List<Material> m_materialBuffer = new List<Material>();
        private readonly List<float> m_scores = new List<float>();
        private readonly List<bool> m_had = new List<bool>();
        private readonly List<FxEntry> m_lightOwners = new List<FxEntry>();
        private readonly List<int> m_lightIndices = new List<int>();

        private int m_features;
        private int m_cursor;
        private int m_fxCursor;
        private int m_syncCursor;
        private int m_syncLeft;
        private int m_lastInstanceCount = -1;
        private float m_syncAt;
        private float m_budgetAt;
        private int m_presetApplied = -1;
        private bool m_loggedError;

        private static bool Has(int features, int feature)
        {
            return (features & feature) != 0;
        }

        private static int WantedFeatures()
        {
            if (BenchMask >= 0)
            {
                return BenchMask;
            }
            if (!PerfSettings.Enabled || ForceOff)
            {
                return 0;
            }
            return (PerfSettings.Instancing ? FeatureInstancing : 0) | (PerfSettings.DetailLod ? FeatureDetail : 0) | (PerfSettings.FireLod ? FeatureFire : 0)
                | (PerfSettings.SoundLod ? FeatureSound : 0) | (PerfSettings.LightBudget ? FeatureLights : 0);
        }

        private void LateUpdate()
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                PerfSettings.SaveIfDirty();
                Bench.Tick();
                Camera camera = Utils.GetMainCamera();
                if (Player.m_localPlayer == null || camera == null)
                {
                    SetFeatures(0);
                    if (Player.m_localPlayer == null)
                    {
                        Forget();
                    }
                    return;
                }
                SetFeatures(WantedFeatures());
                if (m_features == 0)
                {
                    return;
                }
                if (m_presetApplied != PerfSettings.Preset)
                {
                    // Thresholds changed: everything is looked at again right away.
                    m_presetApplied = PerfSettings.Preset;
                    m_cursor = m_fxCursor = 0;
                    m_budgetAt = 0f;
                }
                Sync();
                GeometryUtility.CalculateFrustumPlanes(camera, m_planes);
                Vector3 cameraPos = camera.transform.position;
                float tanHalfFov = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                float screenHeight = Mathf.Max(1, camera.pixelHeight);
                EvaluateDetail(cameraPos, tanHalfFov, screenHeight);
                EvaluateFx(cameraPos, tanHalfFov, screenHeight);
                if (Time.unscaledTime >= m_budgetAt)
                {
                    m_budgetAt = Time.unscaledTime + BudgetEverySec;
                    UpdateLightBudget(camera);
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
            finally
            {
                LastCostMs = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            }
        }

        private void OnApplicationQuit()
        {
            PerfSettings.Flush();
        }

        // Features that were just switched off are put back right away; switched on ones start from the beginning.
        private void SetFeatures(int wanted)
        {
            if (wanted == m_features)
            {
                return;
            }
            int removed = m_features & ~wanted;
            m_features = wanted;
            ApplyInstancing(Has(wanted, FeatureInstancing));
            if (Has(removed, FeatureDetail))
            {
                foreach (PieceEntry entry in s_entries)
                {
                    if (entry.Piece != null && (entry.Hidden || entry.NoShadow))
                    {
                        SetDetail(entry, false, false);
                    }
                }
            }
            foreach (FxEntry fx in s_fx)
            {
                if (fx.Owner.Piece == null)
                {
                    continue;
                }
                if (Has(removed, FeatureFire))
                {
                    fx.Asleep = false;
                    SetParticles(fx, false);
                    for (int i = 0; i < fx.Flickers.Length; i++)
                    {
                        if (fx.FlickerOff[i])
                        {
                            Wake(fx, i);
                        }
                    }
                }
                if (Has(removed, FeatureSound))
                {
                    for (int i = 0; i < fx.Sounds.Length; i++)
                    {
                        SetSound(fx, i, false);
                    }
                }
                if (Has(removed, FeatureLights))
                {
                    RestoreLights(fx);
                }
            }
            m_cursor = m_fxCursor = 0;
            m_budgetAt = 0f;
            RecountDetail();
            RecountFx();
            if (!Has(wanted, FeatureLights))
            {
                LightsConsidered = LightsOff = ShadowLightsKept = 0;
            }
        }

        // ---------------------------------------------------------------- pieces

        private void Sync()
        {
            List<WearNTear> all = WearNTear.GetAllInstances();
            if (all == null)
            {
                return;
            }
            if (m_syncLeft <= 0 && (all.Count != m_lastInstanceCount || Time.unscaledTime >= m_syncAt))
            {
                m_lastInstanceCount = all.Count;
                m_syncAt = Time.unscaledTime + SyncEverySec;
                m_syncLeft = all.Count;
            }
            int created = 0;
            for (int i = 0; i < SyncChecksPerFrame && m_syncLeft > 0; i++, m_syncLeft--)
            {
                if (m_syncCursor >= all.Count)
                {
                    m_syncCursor = 0;
                }
                if (all.Count == 0)
                {
                    m_syncLeft = 0;
                    break;
                }
                WearNTear piece = all[m_syncCursor++];
                if (piece == null || !s_known.Add(piece))
                {
                    continue;
                }
                PieceEntry entry = Create(piece);
                if (entry != null)
                {
                    s_entries.Add(entry);
                    s_byPiece[piece] = entry;
                    if (entry.Fx != null)
                    {
                        s_fx.Add(entry.Fx);
                    }
                }
                if (++created >= NewPiecesPerFrame)
                {
                    break;
                }
            }
        }

        private PieceEntry Create(WearNTear piece)
        {
            // Ships and carts move; only standing buildings are handled.
            if (piece.GetComponentInParent<Rigidbody>() != null)
            {
                return null;
            }
            PieceEntry entry = new PieceEntry { Piece = piece };
            piece.GetComponentsInChildren(true, m_rendererBuffer);
            List<Renderer> renderers = new List<Renderer>();
            bool haveBounds = false;
            Bounds bounds = new Bounds(piece.transform.position, Vector3.one);
            foreach (Renderer renderer in m_rendererBuffer)
            {
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
                {
                    continue;
                }
                renderers.Add(renderer);
                if (renderer.enabled && renderer.gameObject.activeInHierarchy)
                {
                    if (haveBounds)
                    {
                        bounds.Encapsulate(renderer.bounds);
                    }
                    else
                    {
                        bounds = renderer.bounds;
                        haveBounds = true;
                    }
                }
            }
            entry.Bounds = bounds;
            entry.Radius = Mathf.Max(0.05f, bounds.extents.magnitude);
            entry.Renderers = renderers.ToArray();
            entry.Shadows = new ShadowCastingMode[entry.Renderers.Length];
            for (int i = 0; i < entry.Renderers.Length; i++)
            {
                entry.Shadows[i] = entry.Renderers[i].shadowCastingMode;
                if (Has(m_features, FeatureInstancing))
                {
                    EnableInstancing(entry.Renderers[i]);
                }
            }
            entry.Fx = CreateFx(entry);
            return entry;
        }

        private FxEntry CreateFx(PieceEntry entry)
        {
            Transform root = entry.Piece.transform;
            ParticleSystem[] systems = root.GetComponentsInChildren<ParticleSystem>(true);
            List<ParticleSystem> roots = new List<ParticleSystem>();
            foreach (ParticleSystem system in systems)
            {
                // Children follow their root system (Pause/Play with children).
                bool nested = false;
                for (Transform parent = system.transform.parent; parent != null && parent != root.parent; parent = parent.parent)
                {
                    if (parent.GetComponent<ParticleSystem>() != null)
                    {
                        nested = true;
                        break;
                    }
                }
                if (!nested)
                {
                    roots.Add(system);
                }
            }

            List<LightFlicker> flickers = new List<LightFlicker>();
            List<Light> flickerLights = new List<Light>();
            foreach (LightFlicker flicker in root.GetComponentsInChildren<LightFlicker>(true))
            {
                Light light = flicker.GetComponent<Light>();
                // Temporary lights destroy themselves from their own update; those are never paused.
                if (light != null && flicker.m_ttl <= 0f && flicker.m_fadeInDuration <= 0f)
                {
                    flickers.Add(flicker);
                    flickerLights.Add(light);
                }
            }

            List<AudioSource> sounds = new List<AudioSource>();
            foreach (ZSFX sfx in root.GetComponentsInChildren<ZSFX>(true))
            {
                AudioSource source = sfx.GetComponent<AudioSource>();
                // Only endless loops (fire crackle, stations); one-shot sounds end by themselves.
                if (source != null && source.loop && sfx.m_playOnAwake && source.spatialBlend > 0.5f)
                {
                    sounds.Add(source);
                }
            }

            List<LightLod> lights = new List<LightLod>();
            foreach (LightLod lod in root.GetComponentsInChildren<LightLod>(true))
            {
                if (lod.m_lightLod || lod.m_shadowLod)
                {
                    lights.Add(lod);
                }
            }

            if (roots.Count == 0 && flickers.Count == 0 && sounds.Count == 0 && lights.Count == 0)
            {
                return null;
            }
            FxEntry fx = new FxEntry
            {
                Owner = entry,
                Particles = roots.ToArray(),
                Paused = new bool[roots.Count],
                Flickers = flickers.ToArray(),
                FlickerLights = flickerLights.ToArray(),
                FlickerLods = new LightLod[flickers.Count],
                FlickerIntensity = new float[flickers.Count],
                FlickerOff = new bool[flickers.Count],
                Sounds = sounds.ToArray(),
                Silenced = new bool[sounds.Count],
                Lights = lights.ToArray(),
                LightDistance = new float[lights.Count],
                ShadowDistance = new float[lights.Count],
                LightOff = new bool[lights.Count],
                HasShadow = new bool[lights.Count],
            };
            for (int i = 0; i < fx.FlickerLights.Length; i++)
            {
                fx.FlickerLods[i] = fx.FlickerLights[i].GetComponent<LightLod>();
            }
            for (int i = 0; i < fx.Lights.Length; i++)
            {
                fx.LightDistance[i] = fx.Lights[i].m_lightDistance;
                fx.ShadowDistance[i] = fx.Lights[i].m_shadowDistance;
            }
            return fx;
        }

        // ---------------------------------------------------------------- details

        private void EvaluateDetail(Vector3 cameraPos, float tanHalfFov, float screenHeight)
        {
            bool detail = Has(m_features, FeatureDetail);
            if (!detail || s_entries.Count == 0)
            {
                // Dead entries are still dropped.
                PruneOne();
                return;
            }
            LodPreset preset = PerfSettings.Current;
            // The whole base is looked at in about 30 frames.
            int count = Mathf.Min(s_entries.Count, Mathf.Max(120, s_entries.Count / 30));
            for (int n = 0; n < count && s_entries.Count > 0; n++)
            {
                if (m_cursor >= s_entries.Count)
                {
                    m_cursor = 0;
                    RecountDetail();
                    s_known.RemoveWhere(piece => piece == null);
                }
                PieceEntry entry = s_entries[m_cursor];
                if (entry.Piece == null)
                {
                    RemoveAt(m_cursor);
                    continue;
                }
                m_cursor++;
                float pixels = LodRules.ProjectedPixels(entry.Radius, Vector3.Distance(cameraPos, entry.Bounds.center), screenHeight, tanHalfFov);
                bool hide = LodRules.Below(entry.Hidden, pixels, preset.RenderPx);
                bool noShadow = LodRules.Below(entry.NoShadow, pixels, preset.ShadowPx);
                if (hide != entry.Hidden || noShadow != entry.NoShadow)
                {
                    SetDetail(entry, hide, noShadow);
                }
            }
        }

        private void PruneOne()
        {
            if (s_entries.Count == 0)
            {
                return;
            }
            if (m_cursor >= s_entries.Count)
            {
                m_cursor = 0;
                s_known.RemoveWhere(piece => piece == null);
            }
            if (s_entries[m_cursor].Piece == null)
            {
                RemoveAt(m_cursor);
            }
            else
            {
                m_cursor++;
            }
        }

        private static void RemoveAt(int index)
        {
            PieceEntry entry = s_entries[index];
            if (entry.Piece != null)
            {
                s_byPiece.Remove(entry.Piece);
            }
            s_entries[index] = s_entries[s_entries.Count - 1];
            s_entries.RemoveAt(s_entries.Count - 1);
            if (entry.Fx != null)
            {
                s_fx.Remove(entry.Fx);
            }
        }

        private static void SetDetail(PieceEntry entry, bool hide, bool noShadow)
        {
            for (int i = 0; i < entry.Renderers.Length; i++)
            {
                Renderer renderer = entry.Renderers[i];
                if (renderer == null)
                {
                    continue;
                }
                // Shadow-only helpers are not drawn anyway; only their shadow is decided.
                bool shadowOnly = entry.Shadows[i] == ShadowCastingMode.ShadowsOnly;
                if (!shadowOnly && hide != entry.Hidden)
                {
                    renderer.forceRenderingOff = hide || BuilderView.Hides(entry.Piece);
                }
                ShadowCastingMode wanted = noShadow || (shadowOnly && hide) ? ShadowCastingMode.Off : entry.Shadows[i];
                if (entry.Shadows[i] != ShadowCastingMode.Off && renderer.shadowCastingMode != wanted)
                {
                    renderer.shadowCastingMode = wanted;
                }
            }
            entry.Hidden = hide;
            entry.NoShadow = noShadow;
        }

        // ---------------------------------------------------------------- fire, flicker, sound

        private void EvaluateFx(Vector3 cameraPos, float tanHalfFov, float screenHeight)
        {
            bool fire = Has(m_features, FeatureFire);
            bool sound = Has(m_features, FeatureSound);
            if ((!fire && !sound) || s_fx.Count == 0)
            {
                return;
            }
            LodPreset preset = PerfSettings.Current;
            AudioListener listener = AudioMan.instance != null ? AudioMan.instance.GetActiveAudioListener() : null;
            Vector3 listenerPos = listener != null ? listener.transform.position : cameraPos;
            // A third of the fires per frame: a fire that comes into view wakes within a few frames.
            int count = Mathf.Min(s_fx.Count, Mathf.Max(40, (s_fx.Count + 2) / 3));
            for (int n = 0; n < count; n++)
            {
                if (m_fxCursor >= s_fx.Count)
                {
                    m_fxCursor = 0;
                    RecountFx();
                }
                FxEntry fx = s_fx[m_fxCursor++];
                if (fx.Owner.Piece == null)
                {
                    continue;
                }
                if (fire)
                {
                    if (fx.Particles.Length > 0)
                    {
                        Bounds bounds = fx.Owner.Bounds;
                        bounds.Expand(FireBoundsMargin);
                        float pixels = LodRules.ProjectedPixels(fx.Owner.Radius, Vector3.Distance(cameraPos, bounds.center), screenHeight, tanHalfFov);
                        bool asleep = !GeometryUtility.TestPlanesAABB(m_planes, bounds) || LodRules.Below(fx.Asleep, pixels, preset.FirePx);
                        fx.Asleep = asleep;
                        SetParticles(fx, asleep);
                    }
                    SetFlicker(fx, cameraPos, preset);
                }
                if (sound)
                {
                    for (int i = 0; i < fx.Sounds.Length; i++)
                    {
                        AudioSource source = fx.Sounds[i];
                        if (source == null)
                        {
                            continue;
                        }
                        float limit = LodRules.AudibleDistance(source.maxDistance, source.rolloffMode == AudioRolloffMode.Logarithmic);
                        float distance = Vector3.Distance(listenerPos, source.transform.position);
                        SetSound(fx, i, fx.Silenced[i] ? distance > limit - LodRules.AudibleHysteresis : distance > limit);
                    }
                }
            }
        }

        private static void SetParticles(FxEntry fx, bool asleep)
        {
            for (int i = 0; i < fx.Particles.Length; i++)
            {
                ParticleSystem system = fx.Particles[i];
                if (system == null)
                {
                    continue;
                }
                if (asleep)
                {
                    // isPlaying stays true while paused, so isPaused is checked as well.
                    if (system.isPlaying && !system.isPaused)
                    {
                        system.Pause(true);
                        fx.Paused[i] = true;
                    }
                }
                else if (fx.Paused[i])
                {
                    fx.Paused[i] = false;
                    // A fire that went out meanwhile was stopped by the game; only a still paused one is resumed.
                    if (system.isPaused && system.gameObject.activeInHierarchy)
                    {
                        system.Play(true);
                    }
                }
            }
        }

        private void SetFlicker(FxEntry fx, Vector3 cameraPos, LodPreset preset)
        {
            for (int i = 0; i < fx.Flickers.Length; i++)
            {
                LightFlicker flicker = fx.Flickers[i];
                Light light = fx.FlickerLights[i];
                if (flicker == null || light == null || !light.enabled)
                {
                    // A light switched off by the game's light LOD costs nothing to flicker; nothing to decide.
                    continue;
                }
                LightLod lod = fx.FlickerLods[i];
                float range = lod != null ? Mathf.Max(light.range, s_lightBaseRange(lod)) : light.range;
                Vector3 position = light.transform.position;
                // Frozen when far, or when its light does not reach anything in view.
                bool freeze = Vector3.Distance(cameraPos, position) > preset.FlickerDistance
                    || !GeometryUtility.TestPlanesAABB(m_planes, new Bounds(position, Vector3.one * (2f * range + 1f)));
                if (freeze && !fx.FlickerOff[i] && flicker.enabled)
                {
                    fx.FlickerIntensity[i] = light.intensity;
                    flicker.enabled = false;
                    fx.FlickerOff[i] = true;
                }
                else if (!freeze && fx.FlickerOff[i])
                {
                    Wake(fx, i);
                }
            }
        }

        private static void Wake(FxEntry fx, int i)
        {
            fx.FlickerOff[i] = false;
            LightFlicker flicker = fx.Flickers[i];
            Light light = fx.FlickerLights[i];
            if (flicker == null || light == null || flicker.enabled)
            {
                return;
            }
            flicker.enabled = true;
            // Enabling resets the intensity to 0 until the next update; keep the frozen one meanwhile.
            light.intensity = fx.FlickerIntensity[i];
        }

        // A looping sound is paused, not stopped: the game's sound manager keeps it registered as it was, and it
        // continues where it was when the player comes closer.
        private static void SetSound(FxEntry fx, int i, bool silence)
        {
            AudioSource source = fx.Sounds[i];
            if (source == null)
            {
                return;
            }
            if (silence)
            {
                // Also a sound the game started again meanwhile (a fire lit again) is paused.
                if (source.isPlaying)
                {
                    source.Pause();
                    fx.Silenced[i] = true;
                }
            }
            else if (fx.Silenced[i])
            {
                fx.Silenced[i] = false;
                source.UnPause();
            }
        }

        // ---------------------------------------------------------------- lights

        private void UpdateLightBudget(Camera camera)
        {
            if (!Has(m_features, FeatureLights))
            {
                return;
            }
            LodPreset preset = PerfSettings.Current;
            Vector3 cameraPos = camera.transform.position;
            Vector3 forward = camera.transform.forward;
            float halfAngle = Mathf.Atan(Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * Mathf.Max(1f, camera.aspect)) * Mathf.Rad2Deg + ViewMarginDegrees;
            float cosView = Mathf.Cos(Mathf.Min(89f, halfAngle) * Mathf.Deg2Rad);
            // LightLod measures its distances from the player, not from the camera.
            Vector3 reference = Player.m_localPlayer != null && !GameCamera.InFreeFly() ? Player.m_localPlayer.transform.position : cameraPos;

            m_scores.Clear();
            m_had.Clear();
            m_lightOwners.Clear();
            m_lightIndices.Clear();
            foreach (FxEntry fx in s_fx)
            {
                if (fx.Owner.Piece == null)
                {
                    continue;
                }
                for (int i = 0; i < fx.Lights.Length; i++)
                {
                    LightLod lod = fx.Lights[i];
                    if (lod == null)
                    {
                        continue;
                    }
                    Vector3 position = lod.transform.position;
                    float fromPlayer = Vector3.Distance(reference, position);
                    float score = 0f;
                    // Only lights the game itself would keep on take part.
                    if (lod.isActiveAndEnabled && lod.m_lightLod && fromPlayer < fx.LightDistance[i])
                    {
                        float range = Mathf.Max(1f, s_lightBaseRange(lod));
                        Vector3 toLight = position - cameraPos;
                        float distance = toLight.magnitude;
                        bool seen = distance < range + 2f || Vector3.Dot(forward, toLight / Mathf.Max(0.001f, distance)) > cosView;
                        score = LodRules.LightScore(range, distance, seen, fromPlayer < preset.NearLights);
                    }
                    m_scores.Add(score);
                    m_had.Add(!fx.LightOff[i]);
                    m_lightOwners.Add(fx);
                    m_lightIndices.Add(i);
                }
            }

            bool[] on = LodRules.ShadowBudget(m_scores, m_had, preset.Lights);
            for (int n = 0; n < m_scores.Count; n++)
            {
                FxEntry fx = m_lightOwners[n];
                int i = m_lightIndices[n];
                LightLod lod = fx.Lights[i];
                // Shadows only among lights that stay on and are close enough for the game to give them a shadow.
                if (!on[n] || !lod.m_shadowLod || Vector3.Distance(reference, lod.transform.position) >= fx.ShadowDistance[i])
                {
                    m_scores[n] = 0f;
                }
                m_had[n] = fx.HasShadow[i];
            }
            bool[] shadow = LodRules.ShadowBudget(m_scores, m_had, preset.ShadowLights);

            int considered = 0;
            int off = 0;
            int shadows = 0;
            for (int n = 0; n < m_lightOwners.Count; n++)
            {
                FxEntry fx = m_lightOwners[n];
                int i = m_lightIndices[n];
                LightLod lod = fx.Lights[i];
                bool candidate = lod.isActiveAndEnabled && lod.m_lightLod && Vector3.Distance(reference, lod.transform.position) < fx.LightDistance[i];
                // LightLod turns a light (and its shadow) on only within its distance: 0 keeps it off, and the
                // game fades it out by itself.
                bool lightOn = !candidate || on[n];
                SetDistance(ref lod.m_lightDistance, lightOn ? fx.LightDistance[i] : 0f);
                SetDistance(ref lod.m_shadowDistance, lightOn && shadow[n] ? fx.ShadowDistance[i] : 0f);
                fx.LightOff[i] = !lightOn;
                fx.HasShadow[i] = lightOn && shadow[n];
                considered += candidate ? 1 : 0;
                off += lightOn ? 0 : 1;
                shadows += fx.HasShadow[i] ? 1 : 0;
            }
            LightsConsidered = considered;
            LightsOff = off;
            ShadowLightsKept = shadows;
        }

        private static void SetDistance(ref float field, float value)
        {
            if (!field.Equals(value))
            {
                field = value;
            }
        }

        private static void RestoreLights(FxEntry fx)
        {
            for (int i = 0; i < fx.Lights.Length; i++)
            {
                LightLod lod = fx.Lights[i];
                if (lod != null)
                {
                    lod.m_lightDistance = fx.LightDistance[i];
                    lod.m_shadowDistance = fx.ShadowDistance[i];
                }
                fx.LightOff[i] = false;
                fx.HasShadow[i] = false;
            }
        }

        // ---------------------------------------------------------------- instancing

        private void EnableInstancing(Renderer renderer)
        {
            renderer.GetSharedMaterials(m_materialBuffer);
            foreach (Material material in m_materialBuffer)
            {
                if (material != null && !material.enableInstancing && !s_instanced.Contains(material))
                {
                    material.enableInstancing = true;
                    s_instanced.Add(material);
                }
            }
        }

        private bool m_instancingApplied;

        private void ApplyInstancing(bool enabled)
        {
            if (enabled == m_instancingApplied)
            {
                return;
            }
            m_instancingApplied = enabled;
            if (enabled)
            {
                foreach (PieceEntry entry in s_entries)
                {
                    foreach (Renderer renderer in entry.Renderers)
                    {
                        if (renderer != null)
                        {
                            EnableInstancing(renderer);
                        }
                    }
                }
                return;
            }
            foreach (Material material in s_instanced)
            {
                if (material != null)
                {
                    material.enableInstancing = false;
                }
            }
            s_instanced.Clear();
        }

        // ---------------------------------------------------------------- bookkeeping

        // Leaving the world: the objects are gone, the lists are dropped.
        private void Forget()
        {
            if (s_entries.Count == 0 && s_known.Count == 0)
            {
                return;
            }
            s_entries.Clear();
            s_byPiece.Clear();
            s_fx.Clear();
            s_known.Clear();
            s_instanced.RemoveWhere(material => material == null);
            m_cursor = m_fxCursor = m_syncCursor = m_syncLeft = 0;
            m_lastInstanceCount = -1;
            RecountDetail();
            RecountFx();
        }

        private static void RecountDetail()
        {
            int hidden = 0;
            int noShadow = 0;
            foreach (PieceEntry entry in s_entries)
            {
                hidden += entry.Hidden ? 1 : 0;
                noShadow += entry.NoShadow ? 1 : 0;
            }
            HiddenCount = hidden;
            NoShadowCount = noShadow;
        }

        private static void RecountFx()
        {
            int fires = 0;
            int systems = 0;
            int flickers = 0;
            int sounds = 0;
            foreach (FxEntry fx in s_fx)
            {
                fires += fx.Asleep && fx.Particles.Length > 0 ? 1 : 0;
                systems += Count(fx.Paused);
                flickers += Count(fx.FlickerOff);
                sounds += Count(fx.Silenced);
            }
            SleepingFires = fires;
            PausedSystems = systems;
            FrozenFlickers = flickers;
            SilencedSounds = sounds;
        }

        private static int Count(bool[] flags)
        {
            int count = 0;
            foreach (bool flag in flags)
            {
                count += flag ? 1 : 0;
            }
            return count;
        }
    }
}
