using UnityEngine;
using UnityEngine.PostProcessing;

namespace ValheimHelper
{
    // Brightness, gamma and the strength of the glow (bloom) on top of the game's own post-processing profile: the
    // game sets its look once and only switches bloom on or off, so the mod keeps the game's values as a baseline and
    // adds its offsets to them. Only how the picture is drawn on this computer changes. The photo mode's look
    // (filters, vignette, grain, focus) goes through here too, so one place owns the profile.
    internal static class ImageTuning
    {
        // The photo mode's look; default values change nothing.
        internal sealed class PhotoLook
        {
            public float Exposure;
            public float Contrast = 1f;
            public float Saturation = 1f;
            public float Temperature;
            public float Tint;
            public float Vignette;
            public float Grain;
            public float Aberration;
            public bool Focus;
            public float FocusDistance = 5f;
            public float Aperture = 2.8f;

            public PhotoLook Copy()
            {
                return (PhotoLook)MemberwiseClone();
            }
        }

        // Set while the photo mode is on; PhotoChanged after every change.
        internal static PhotoLook Photo;
        private static int s_photoVersion;
        private static int s_photoApplied = -1;
        private static VignetteModel.Settings s_baseVignette;
        private static bool s_baseVignetteEnabled;
        private static GrainModel.Settings s_baseGrain;
        private static bool s_baseGrainEnabled;
        private static ChromaticAberrationModel.Settings s_baseAberration;
        private static bool s_baseAberrationEnabled;
        private static DepthOfFieldModel.Settings s_baseFocus;
        private static bool s_baseFocusEnabled;

        internal static void PhotoChanged()
        {
            s_photoVersion++;
        }
        internal const float MinBrightness = -1.5f;
        internal const float MaxBrightness = 1.5f;

        private static PostProcessingProfile s_profile;
        private static ColorGradingModel.Settings s_baseGrading;
        private static bool s_baseGradingEnabled;
        private static float s_baseBloom;
        private static bool s_applied;
        private static float s_brightness;
        private static float s_gamma;
        private static float s_glow = 1f;

        internal static bool IsDefault => State.ImageBrightness == 0f && State.ImageGamma == 0f && State.ImageGlow == 1f;

        internal static void Reset()
        {
            State.ImageBrightness = 0f;
            State.ImageGamma = 0f;
            State.ImageGlow = 1f;
        }

        // Every frame; changes the profile only when a slider moved or the camera (and its profile) is new.
        internal static void Tick()
        {
            CameraEffects effects = CameraEffects.instance;
            PostProcessingBehaviour behaviour = effects != null ? effects.GetComponent<PostProcessingBehaviour>() : null;
            PostProcessingProfile profile = behaviour != null ? behaviour.profile : null;
            if (profile == null)
            {
                return;
            }
            if (!ReferenceEquals(profile, s_profile))
            {
                s_profile = profile;
                s_baseGrading = profile.colorGrading.settings;
                s_baseGradingEnabled = profile.colorGrading.enabled;
                s_baseBloom = profile.bloom.settings.bloom.intensity;
                s_baseVignette = profile.vignette.settings;
                s_baseVignetteEnabled = profile.vignette.enabled;
                s_baseGrain = profile.grain.settings;
                s_baseGrainEnabled = profile.grain.enabled;
                s_baseAberration = profile.chromaticAberration.settings;
                s_baseAberrationEnabled = profile.chromaticAberration.enabled;
                s_baseFocus = profile.depthOfField.settings;
                s_baseFocusEnabled = profile.depthOfField.enabled;
                s_applied = false;
            }
            int photo = Photo != null ? s_photoVersion : -1;
            if (s_applied && s_brightness == State.ImageBrightness && s_gamma == State.ImageGamma && s_glow == State.ImageGlow && photo == s_photoApplied)
            {
                return;
            }
            s_photoApplied = photo;
            s_brightness = State.ImageBrightness;
            s_gamma = State.ImageGamma;
            s_glow = State.ImageGlow;
            s_applied = true;
            Apply(profile);
        }

        private static void Apply(PostProcessingProfile profile)
        {
            PhotoLook look = Photo;
            bool photoGrading = look != null && (look.Exposure != 0f || look.Contrast != 1f || look.Saturation != 1f || look.Temperature != 0f || look.Tint != 0f);
            if (s_brightness != 0f || s_gamma != 0f || photoGrading)
            {
                ColorGradingModel.Settings grading = s_baseGrading;
                grading.basic.postExposure += s_brightness;
                if (photoGrading)
                {
                    grading.basic.postExposure += look.Exposure;
                    grading.basic.contrast = Mathf.Clamp(grading.basic.contrast * look.Contrast, 0f, 2f);
                    grading.basic.saturation = Mathf.Clamp(grading.basic.saturation * look.Saturation, 0f, 2f);
                    grading.basic.temperature = Mathf.Clamp(grading.basic.temperature + look.Temperature, -100f, 100f);
                    grading.basic.tint = Mathf.Clamp(grading.basic.tint + look.Tint, -100f, 100f);
                }
                // Both wheel modes: a positive value lifts the midtones (the wheels' own math turns it into an exponent).
                if (grading.colorWheels.mode == ColorGradingModel.ColorWheelMode.Linear)
                {
                    ColorGradingModel.LinearWheelsSettings linear = grading.colorWheels.linear;
                    linear.gamma.a += s_gamma * 0.2f;
                    grading.colorWheels.linear = linear;
                }
                else
                {
                    ColorGradingModel.LogWheelsSettings log = grading.colorWheels.log;
                    log.power.a += s_gamma * 0.6f;
                    grading.colorWheels.log = log;
                }
                if (!s_baseGradingEnabled)
                {
                    // The game draws without colour grading: switched on only for these offsets, without a tone curve of its own.
                    grading.tonemapping.tonemapper = ColorGradingModel.Tonemapper.None;
                }
                profile.colorGrading.settings = grading;
                profile.colorGrading.enabled = true;
            }
            else
            {
                profile.colorGrading.settings = s_baseGrading;
                profile.colorGrading.enabled = s_baseGradingEnabled;
            }
            BloomModel.Settings bloom = profile.bloom.settings;
            bloom.bloom.intensity = s_baseBloom * Mathf.Clamp01(s_glow);
            profile.bloom.settings = bloom;
            ApplyPhotoEffects(profile, look);
        }

        // Vignette, grain, colour fringes and focus of the photo mode, or the game's own when it is off.
        private static void ApplyPhotoEffects(PostProcessingProfile profile, PhotoLook look)
        {
            if (look != null && look.Vignette > 0f)
            {
                VignetteModel.Settings vignette = s_baseVignette;
                vignette.mode = VignetteModel.Mode.Classic;
                vignette.color = Color.black;
                vignette.center = new Vector2(0.5f, 0.5f);
                vignette.intensity = look.Vignette;
                vignette.smoothness = 0.4f;
                vignette.roundness = 1f;
                vignette.opacity = 1f;
                profile.vignette.settings = vignette;
                profile.vignette.enabled = true;
            }
            else
            {
                profile.vignette.settings = s_baseVignette;
                profile.vignette.enabled = s_baseVignetteEnabled;
            }
            if (look != null && look.Grain > 0f)
            {
                GrainModel.Settings grain = s_baseGrain;
                grain.colored = false;
                grain.intensity = look.Grain;
                grain.size = 1.2f;
                grain.luminanceContribution = 0.8f;
                profile.grain.settings = grain;
                profile.grain.enabled = true;
            }
            else
            {
                profile.grain.settings = s_baseGrain;
                profile.grain.enabled = s_baseGrainEnabled;
            }
            if (look != null && look.Aberration > 0f)
            {
                ChromaticAberrationModel.Settings aberration = s_baseAberration;
                aberration.intensity = look.Aberration;
                profile.chromaticAberration.settings = aberration;
                profile.chromaticAberration.enabled = true;
            }
            else
            {
                profile.chromaticAberration.settings = s_baseAberration;
                profile.chromaticAberration.enabled = s_baseAberrationEnabled;
            }
            if (look != null && look.Focus)
            {
                DepthOfFieldModel.Settings focus = s_baseFocus;
                focus.focusDistance = Mathf.Max(0.1f, look.FocusDistance);
                focus.aperture = Mathf.Clamp(look.Aperture, 0.5f, 32f);
                focus.useCameraFov = true;
                focus.kernelSize = DepthOfFieldModel.KernelSize.Large;
                profile.depthOfField.settings = focus;
                profile.depthOfField.enabled = true;
            }
            else
            {
                profile.depthOfField.settings = s_baseFocus;
                profile.depthOfField.enabled = s_baseFocusEnabled;
            }
        }
    }
}
