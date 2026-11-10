using UnityEngine;

namespace ValheimHelper
{
    // "Кому" of the items page: the same choice of player as on the "Друзі" page (one list, one selection), to give
    // an item to yourself or send it straight into a friend's inventory.
    internal sealed partial class CheatMenu
    {
        private float m_giftResultSeen = -1f;

        private void DrawGiftTarget()
        {
            // The player list and texts change only on Layout, so every event of a frame draws the same controls.
            if (Event.current.type == EventType.Layout)
            {
                Gifts.RequestCheck(force: false);
                RefreshTargets();
                if (Gifts.LastResultAt > m_giftResultSeen)
                {
                    m_giftResultSeen = Gifts.LastResultAt;
                    ShowMessage(Gifts.LastResult);
                }
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Кому:"), GUILayout.Width(60f));
            int target = GUILayout.Toolbar(m_playerTarget, m_playerNames);
            GUILayout.EndHorizontal();
            if (target != m_playerTarget)
            {
                m_playerTarget = target;
                Gifts.RequestCheck(force: true);
            }
            GUILayout.Label(GiftStatusText());
            Gifts.AcceptGifts = GUILayout.Toggle(Gifts.AcceptGifts, Lang.T(" Приймати речі, які надсилають друзі") + Binds.Hint("acceptgifts"));
            Bindable("acceptgifts");
        }

        private string GiftStatusText()
        {
            PlayerTools.Target selected = SelectedTarget();
            if (selected == null || selected.IsMe)
            {
                return m_playerTargets.Count <= 1 ? Lang.T("<color=#c0c0c0>Інших гравців у світі зараз немає.</color>") : "";
            }
            switch (Gifts.GetState(selected.PeerId))
            {
                case Gifts.ModState.HasMod:
                    return Lang.F("<color=#80ff80>У {0} є Valheim Helper: річ одразу з'явиться в його інвентарі.</color>\n" +
                        "<color=#c0c0c0>Передана річ звичайна, як скрафчена чи підібрана, тож досягнень друга не зачіпає.</color>", selected.Name);
                case Gifts.ModState.Checking:
                    return Lang.F("<color=#c0c0c0>Перевіряю, чи є в {0} Valheim Helper…</color>", selected.Name);
                case Gifts.ModState.OtherVersion:
                    return Lang.F("<color=#ffb040>У {0} інша версія Valheim Helper, тому видати йому річ не вийде. Оновіться до однієї версії.</color>", selected.Name);
                default:
                    return Lang.F("<color=#ffb040>У {0} немає Valheim Helper, тому видати йому річ не вийде: інвентар гравця змінює лише його власна гра. " +
                        "Хай встановить ту саму версію мода.</color>", selected.Name);
            }
        }

        // Returns true when the item went (or was refused) to a friend instead of the local inventory.
        private bool TrySendToFriend(ItemEntry entry, int amount, int quality)
        {
            PlayerTools.Target selected = SelectedTarget();
            if (selected == null || selected.IsMe)
            {
                return false;
            }
            if (Gifts.GetState(selected.PeerId) != Gifts.ModState.HasMod)
            {
                ShowMessage(Lang.F("У {0} немає Valheim Helper цієї версії, річ не передано.", selected.Name));
                return true;
            }
            Gifts.Send(selected.PeerId, selected.Name, entry.Prefab, amount, quality);
            ShowMessage(Lang.F("Надсилаю {0}: {1} ×{2} (якість {3})…", selected.Name, entry.DisplayName, amount, quality));
            return true;
        }
    }
}
