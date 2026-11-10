using System.Collections.Generic;
using UnityEngine;

namespace ValheimHelper
{
    // "Тварини": the tamed animals of the world, grouped by kind; a kind opens into its animals with their state and
    // a way to find them.
    internal sealed partial class CheatMenu
    {
        private const int MaxAnimalRows = 60;

        private sealed class AnimalGroup
        {
            public string Kind;
            public bool Summon;
            public Texture2D Icon;
            public Rect IconUv;
            public int Hungry;
            public int Pregnant;
            public readonly List<TamedAnimals.Animal> Animals = new List<TamedAnimals.Animal>();
        }

        private readonly List<AnimalGroup> m_animalGroups = new List<AnimalGroup>();
        private readonly HashSet<string> m_openKinds = new HashSet<string>();
        private string m_pendingKindToggle;
        private int m_animalsVersion = -1;
        private string m_animalsSummary = "";
        private bool m_summonsShown;
        private GUIStyle m_animalDetails;

        private void DrawAnimalsTab()
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                GUILayout.Label(Lang.T("Зайди у світ."));
                return;
            }
            TamedAnimals.Want();
            if (Event.current.type == EventType.Layout)
            {
                if (m_pendingKindToggle != null)
                {
                    if (!m_openKinds.Remove(m_pendingKindToggle))
                    {
                        m_openKinds.Add(m_pendingKindToggle);
                    }
                    m_pendingKindToggle = null;
                }
                if (m_animalsVersion != TamedAnimals.Version)
                {
                    RefreshAnimals(player);
                }
            }
            if (m_animalDetails == null)
            {
                m_animalDetails = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(11, GUI.skin.label.fontSize - 3), wordWrap = true, richText = true };
                m_animalDetails.normal.textColor = new Color(0.75f, 0.72f, 0.66f);
            }

            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Приручені тварини"), HelperSkin.Header);
            if (!string.IsNullOrEmpty(m_animalsSummary))
            {
                GUILayout.Label(m_animalsSummary);
            }
            GUILayout.Label(Lang.T("Натисни на вид, щоб побачити кожну тварину. Хост бачить усіх тварин світу, друг — тих, що поблизу нього."), m_animalDetails);
            GUILayout.Label(Lang.T("Усіх на карті видно з шаром «Приручені тварини» (велика карта → «Шари»)."), m_animalDetails);
            if (m_summonsShown)
            {
                GUILayout.Label(Lang.T("Знизу — <b>виклики</b>: скелети й подібні, кого хтось підняв посохом. Гра рахує їх прирученими, але це не улюбленці: вони не їдять і зникають самі."), m_animalDetails);
            }
            GUILayout.EndVertical();

            if (m_animalGroups.Count == 0)
            {
                GUILayout.Label(TamedAnimals.Version == 0 ? Lang.T("Шукаю…") : Lang.T("Приручених тварин не знайдено."));
                return;
            }
            Vector3 me = player.transform.position;
            foreach (AnimalGroup group in m_animalGroups)
            {
                bool open = m_openKinds.Contains(group.Kind);
                GUILayout.BeginVertical(HelperSkin.Section);
                GUILayout.BeginHorizontal();
                Rect icon = GUILayoutUtility.GetRect(32f, 32f, GUILayout.Width(32f), GUILayout.Height(32f));
                if (group.Icon != null && Event.current.type == EventType.Repaint)
                {
                    GUI.DrawTextureWithTexCoords(icon, group.Icon, group.IconUv);
                }
                List<string> notes = new List<string>();
                if (group.Hungry > 0)
                {
                    notes.Add(Lang.F("<color=#ffb040>голодних {0}</color>", group.Hungry));
                }
                if (group.Pregnant > 0)
                {
                    notes.Add(Lang.F("<color=#ff9ad0>вагітних {0}</color>", group.Pregnant));
                }
                if (group.Summon)
                {
                    notes.Insert(0, Lang.T("<color=#a0a0a0>виклик</color>"));
                }
                string note = notes.Count > 0 ? "   " + string.Join(", ", notes.ToArray()) : "";
                if (GUILayout.Button($"{(open ? "▾" : "▸")}  <b>{group.Kind}</b> — {group.Animals.Count}{note}", GUILayout.MinHeight(32f)))
                {
                    m_pendingKindToggle = group.Kind;
                }
                GUILayout.EndHorizontal();
                if (open)
                {
                    int shown = 0;
                    foreach (TamedAnimals.Animal animal in group.Animals)
                    {
                        if (++shown > MaxAnimalRows)
                        {
                            GUILayout.Label(Lang.F("… і ще {0}, далі від тебе.", group.Animals.Count - MaxAnimalRows), m_animalDetails);
                            break;
                        }
                        DrawAnimalRow(animal, me);
                    }
                }
                GUILayout.EndVertical();
            }
        }

        private void DrawAnimalRow(TamedAnimals.Animal animal, Vector3 me)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(40f);
            GUILayout.BeginVertical();
            string stars = animal.Stars > 0 ? " <color=#ffd060>" + new string('★', animal.Stars) + "</color>" : "";
            string name = string.IsNullOrEmpty(animal.Name) ? animal.Kind : animal.Name;
            GUILayout.Label($"{name}{stars}");
            List<string> details = new List<string> { Lang.F("здоров'я {0:0}/{1:0}", animal.Health, animal.MaxHealth) };
            if (animal.Eats)
            {
                details.Add(animal.Hungry ? Lang.T("<color=#ffb040>голодна</color>") : Lang.T("<color=#80ff80>сита</color>"));
            }
            if (animal.Pregnant)
            {
                details.Add(Lang.T("<color=#ff9ad0>вагітна</color>"));
            }
            if (!string.IsNullOrEmpty(animal.Following))
            {
                details.Add(Lang.F("іде за {0}", animal.Following));
            }
            details.Add(Map.WaypointOverlay.DistanceText(Vector3.Distance(me, animal.Pos)));
            GUILayout.Label(string.Join(" · ", details.ToArray()), m_animalDetails);
            GUILayout.EndVertical();
            if (GUILayout.Button(Lang.T("На карті"), GUILayout.Width(90f), GUILayout.Height(28f)))
            {
                ShowMessage(Map.MapOverlay.ShowOnMap(animal.Pos));
            }
            if (GUILayout.Button(Lang.T("Телепорт"), GUILayout.Width(90f), GUILayout.Height(28f)))
            {
                ShowMessage(TamedAnimals.TeleportTo(animal));
            }
            GUILayout.EndHorizontal();
        }

        // Lists change only on Layout, so every event of a frame draws the same controls.
        private void RefreshAnimals(Player player)
        {
            m_animalsVersion = TamedAnimals.Version;
            Vector3 me = player.transform.position;
            Dictionary<string, AnimalGroup> byKind = new Dictionary<string, AnimalGroup>();
            m_animalGroups.Clear();
            int hungry = 0;
            int pregnant = 0;
            foreach (TamedAnimals.Animal animal in TamedAnimals.Animals)
            {
                if (!byKind.TryGetValue(animal.Kind, out AnimalGroup group))
                {
                    group = new AnimalGroup { Kind = animal.Kind, Summon = animal.Summon, Icon = animal.Icon, IconUv = animal.IconUv };
                    byKind[animal.Kind] = group;
                    m_animalGroups.Add(group);
                }
                group.Animals.Add(animal);
                group.Hungry += animal.Hungry ? 1 : 0;
                group.Pregnant += animal.Pregnant ? 1 : 0;
                hungry += animal.Hungry ? 1 : 0;
                pregnant += animal.Pregnant ? 1 : 0;
            }
            foreach (AnimalGroup group in m_animalGroups)
            {
                group.Animals.Sort((a, b) => (a.Pos - me).sqrMagnitude.CompareTo((b.Pos - me).sqrMagnitude));
            }
            // summons are listed after the pets, never mixed with them
            m_animalGroups.Sort((a, b) => a.Summon != b.Summon ? (a.Summon ? 1 : -1) : b.Animals.Count.CompareTo(a.Animals.Count));
            int summons = 0;
            foreach (AnimalGroup group in m_animalGroups)
            {
                if (group.Summon)
                {
                    summons += group.Animals.Count;
                }
            }
            m_summonsShown = summons > 0;
            int total = TamedAnimals.Animals.Count - summons;
            List<string> parts = new List<string> { Lang.F("<b>Усього: {0}</b>", total) };
            if (hungry > 0)
            {
                parts.Add(Lang.F("<color=#ffb040>голодних {0}</color>", hungry));
            }
            if (pregnant > 0)
            {
                parts.Add(Lang.F("<color=#ff9ad0>вагітних {0}</color>", pregnant));
            }
            if (summons > 0)
            {
                parts.Add(Lang.F("<color=#a0a0a0>викликаних {0}</color>", summons));
            }
            m_animalsSummary = total == 0 && summons == 0 ? "" : string.Join(", ", parts.ToArray());
        }
    }
}
