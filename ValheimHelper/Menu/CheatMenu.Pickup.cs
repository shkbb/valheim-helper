using System.Collections.Generic;
using UnityEngine;
using ValheimHelper.Storage;

namespace ValheimHelper
{
    // "Підібрати": items lying around, picked up from the menu.
    internal sealed partial class CheatMenu
    {
        private List<RemotePickup.Group> m_pickupGroups = new List<RemotePickup.Group>();
        private float m_pickupListedAt = -10f;

        private void DrawPickupTab()
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                GUILayout.Label(Lang.T("Зайди у світ."));
                return;
            }
            if (Event.current.type == EventType.Layout && Time.unscaledTime - m_pickupListedAt > 0.5f)
            {
                m_pickupListedAt = Time.unscaledTime;
                m_pickupGroups = RemotePickup.Around(player);
            }

            GUILayout.BeginVertical(HelperSkin.Section);
            GUILayout.Label(Lang.T("Підібрати з землі"), HelperSkin.Header);
            GUILayout.Label(Lang.T("<color=#c0c0c0>Усе, що лежить довкола в радіусі: дроп з мобів, вирубаного лісу, розбитого каміння. Підбирає сама гра, " +
                "як натисканням E біля кожної купки: влізе лише те, на що є місце й вага. Зброя сама в руки не береться.</color>"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.F("Підбирати в радіусі: {0:0} м", RemotePickup.Radius), GUILayout.Width(200f));
            float radius = Mathf.Round(GUILayout.HorizontalSlider(RemotePickup.Radius, RemotePickup.MinRadius, RemotePickup.MaxRadius, GUILayout.Width(300f)));
            if (!radius.Equals(RemotePickup.Radius))
            {
                RemotePickup.Radius = radius;
                m_pickupListedAt = -10f;
            }
            GUILayout.EndHorizontal();
            int total = 0;
            foreach (RemotePickup.Group group in m_pickupGroups)
            {
                total += group.Amount;
            }
            GUI.enabled = !RemotePickup.Busy && m_pickupGroups.Count > 0;
            if (Bindable(GUILayout.Button((RemotePickup.Busy ? Lang.T("Підбираю…") : Lang.F("Підібрати все ({0} шт.)", total)) + Binds.Hint("pickupall"), GUILayout.Height(34f)), "pickupall"))
            {
                ShowMessage(RemotePickup.Start(player, null));
                m_pickupListedAt = -10f;
            }
            GUI.enabled = true;
            GUILayout.EndVertical();

            GUILayout.BeginVertical(HelperSkin.Section);
            if (m_pickupGroups.Count == 0)
            {
                GUILayout.Label(Lang.T("<color=#c0c0c0>Поруч нічого не лежить.</color>"));
            }
            foreach (RemotePickup.Group group in m_pickupGroups)
            {
                GUILayout.BeginHorizontal();
                Rect icon = GUILayoutUtility.GetRect(28f, 28f, GUILayout.Width(28f), GUILayout.Height(28f));
                if (group.Icon != null && Event.current.type == EventType.Repaint)
                {
                    GUI.DrawTextureWithTexCoords(icon, group.Icon, group.IconUv);
                }
                string piles = group.Piles > 1 ? Lang.F(", купок: {0}", group.Piles) : "";
                GUILayout.Label(Lang.F("{0} ×{1}  <color=#909090>{2:0} м{3}</color>", group.Name, group.Amount, group.Nearest, piles), GUILayout.Height(28f));
                GUI.enabled = !RemotePickup.Busy;
                if (GUILayout.Button(Lang.T("Підібрати"), GUILayout.Width(110f), GUILayout.Height(28f)))
                {
                    ShowMessage(RemotePickup.Start(player, group.SharedName));
                    m_pickupListedAt = -10f;
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
        }
    }
}
