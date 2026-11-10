using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ValheimHelper.Perf
{
    // "Що навколо": counts of what the game keeps loaded right now, with what each of them costs.
    // Scanned once on request (a scene search takes a few milliseconds on a big base).
    internal static class SceneStats
    {
        internal static readonly List<string> Lines = new List<string>();
        internal static double ScanMs;

        private static readonly System.Reflection.FieldInfo s_itemDrops = AccessTools.Field(typeof(ItemDrop), "s_instances");

        internal static void Scan()
        {
            Lines.Clear();
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                ScanInto(Lines);
            }
            catch (Exception e)
            {
                Log.Error(e);
                Lines.Add(Lang.T("<color=#ff6060>Не вдалося порахувати, деталі у ValheimHelper.log</color>"));
            }
            ScanMs = watch.Elapsed.TotalMilliseconds;
        }

        private static void ScanInto(List<string> lines)
        {
            List<WearNTear> pieces = WearNTear.GetAllInstances();
            lines.Add(Lang.F("Частин будівель завантажено: <b>{0}</b> (у шарі оптимізації: {1}). Кожна частина — окремі виклики малювання й тіней.", pieces?.Count ?? 0, RenderLayer.PieceCount));

            int renderers = 0;
            int visible = 0;
            int visibleCasters = 0;
            foreach (Renderer renderer in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (!renderer.enabled)
                {
                    continue;
                }
                renderers++;
                if (renderer.isVisible)
                {
                    visible++;
                    visibleCasters += renderer.shadowCastingMode != ShadowCastingMode.Off ? 1 : 0;
                }
            }
            lines.Add(Lang.F("Рендерерів: <b>{0}</b>, у кадрі або в тінях: <b>{1}</b>, з них кидають тінь: {2}.", renderers, visible, visibleCasters));

            int lightsOn = 0;
            int pointShadows = 0;
            int pointLights = 0;
            foreach (Light light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (!light.enabled || light.intensity <= 0f)
                {
                    continue;
                }
                lightsOn++;
                if (light.type != LightType.Directional)
                {
                    pointLights++;
                    pointShadows += light.shadows != LightShadows.None ? 1 : 0;
                }
            }
            lines.Add(Lang.F("Світла увімкнено: <b>{0}</b> (точкових {1}), з тінями: <b>{2}</b>. Кожне точкове з тінню малює сцену навколо себе ще 6 разів.", lightsOn, pointLights, pointShadows));
            lines.Add(Lang.F("Гра: ліміт точкових світел {0}, з тінями {1}; тіні сонця до {2:0} м, каскадів {3}; LOD bias {4:0.##}.", Limit(LightLod.m_lightLimit), Limit(LightLod.m_shadowLimit), QualitySettings.shadowDistance, QualitySettings.shadowCascades, QualitySettings.lodBias));

            int systems = 0;
            int playing = 0;
            int paused = 0;
            long particles = 0;
            Dictionary<string, int> playingNames = new Dictionary<string, int>();
            foreach (ParticleSystem system in Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None))
            {
                systems++;
                if (system.isPaused)
                {
                    paused++;
                }
                else if (system.isPlaying)
                {
                    playing++;
                    particles += system.particleCount;
                    // Grouped by the object the effect belongs to, so it is clear which effects are running.
                    string owner = system.transform.root.name.Replace("(Clone)", "");
                    playingNames[owner] = (playingNames.TryGetValue(owner, out int count) ? count : 0) + 1;
                }
            }
            lines.Add(Lang.F("Систем частинок: {0}, грають: <b>{1}</b>, на паузі: {2}, частинок: {3}. Приспано шаром: {4} вогнів.", systems, playing, paused, particles, RenderLayer.SleepingFires));
            lines.Add(Lang.T("<color=#c0c0c0>Найбільше частинок грає в: ") + Top(playingNames, 6) + "</color>");

            int fires = 0;
            int burning = 0;
            foreach (Fireplace fireplace in Object.FindObjectsByType<Fireplace>(FindObjectsSortMode.None))
            {
                fires++;
                burning += fireplace.IsBurning() ? 1 : 0;
            }
            lines.Add(Lang.F("Вогнищ і факелів: {0}, горить: <b>{1}</b>. Диму в повітрі: {2} (ліміт гри 100, дим — фізичні об'єкти).", fires, burning, Smoke.GetTotalSmoke()));

            int audio = 0;
            foreach (AudioSource source in Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None))
            {
                audio += source.isPlaying ? 1 : 0;
            }
            int cloth = Object.FindObjectsByType<Cloth>(FindObjectsSortMode.None).Length;
            Type magica = AccessTools.TypeByName("MagicaCloth2.MagicaCloth");
            int magicaCount = magica != null ? Object.FindObjectsByType(magica, FindObjectsSortMode.None).Length : 0;
            lines.Add(Lang.F("Звуків грає: {0} (на паузі від шару: {1}). Тканини (банери, накидки, вітрила): {2}.", audio, RenderLayer.SilencedSounds, cloth + magicaCount));

            int characters = 0;
            int tamed = 0;
            foreach (Character character in Character.GetAllCharacters())
            {
                characters++;
                tamed += character != null && character.IsTamed() ? 1 : 0;
            }
            int drops = (s_itemDrops?.GetValue(null) as List<ItemDrop>)?.Count ?? 0;
            int awake = 0;
            foreach (Rigidbody body in Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
            {
                awake += !body.isKinematic && !body.IsSleeping() ? 1 : 0;
            }
            lines.Add(Lang.F("Персонажів: <b>{0}</b> (приручених {1}). Предметів на землі: <b>{2}</b>. Фізичних тіл у русі: {3}.", characters, tamed, drops, awake));

            if (ZNet.instance != null && ZDOMan.instance != null)
            {
                lines.Add(Lang.F("Мережа: {0}, гравців підключено: {1}, об'єктів у світі (ZDO): {2}.", (ZNet.instance.IsServer() ? Lang.T("ти хост") : Lang.T("ти клієнт")), ZNet.instance.GetPeers().Count, ZDOMan.instance.NrOfObjects()));
            }
        }

        private static string Top(Dictionary<string, int> counts, int max)
        {
            List<KeyValuePair<string, int>> sorted = new List<KeyValuePair<string, int>>(counts);
            sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> pair in sorted)
            {
                if (parts.Count == max)
                {
                    break;
                }
                parts.Add($"{pair.Key} ×{pair.Value}");
            }
            return parts.Count > 0 ? string.Join(", ", parts.ToArray()) : Lang.T("нічого");
        }

        private static string Limit(int limit)
        {
            return limit < 0 ? Lang.T("без обмеження") : limit.ToString();
        }
    }
}
