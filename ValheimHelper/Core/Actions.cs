using System.Reflection;
using HarmonyLib;

namespace ValheimHelper
{
    // Toggles and one-shot actions with side effects, shared by the menu and key binds.
    internal static class Actions
    {
        private static readonly FieldInfo s_allSkillsField = AccessTools.Field(typeof(Skills), "s_allSkills");

        // Every skill of the game (the list the game itself keeps), without None and All.
        internal static System.Collections.Generic.List<Skills.SkillType> SkillTypes()
        {
            System.Collections.Generic.List<Skills.SkillType> result = new System.Collections.Generic.List<Skills.SkillType>();
            System.Array source = (s_allSkillsField?.GetValue(null) as System.Array) ?? System.Enum.GetValues(typeof(Skills.SkillType));
            foreach (object value in source)
            {
                Skills.SkillType type = (Skills.SkillType)value;
                if (type != Skills.SkillType.None && type != Skills.SkillType.All)
                {
                    result.Add(type);
                }
            }
            return result;
        }

        internal static string AllSkillsTo100()
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return null;
            }
            Skills skills = player.GetSkills();
            foreach (Skills.SkillType type in SkillTypes())
            {
                skills.CheatRaiseSkill(type.ToString(), 100f, showMessage: false);
            }
            return Lang.T("Усі навички піднято до 100.");
        }

        private static readonly MethodInfo s_updateAvailablePieces = AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList");

        internal static void SetFly(bool on)
        {
            if (State.Fly == on)
            {
                return;
            }
            State.Fly = on;
            // Landing after disabling fly in the air would otherwise count as a huge fall.
            State.LandingProtection = !on;
        }

        internal static void SetFreeBuild(bool on)
        {
            if (State.FreeBuild == on)
            {
                return;
            }
            State.FreeBuild = on;
            RefreshBuildPieces();
        }

        // The build menu list is cached by the game, rebuild it right away.
        internal static void RefreshBuildPieces()
        {
            if (Player.m_localPlayer != null)
            {
                s_updateAvailablePieces?.Invoke(Player.m_localPlayer, null);
            }
        }

        internal static void SetNoDurabilityLoss(bool on)
        {
            State.NoDurabilityLoss = on;
            RefreshWorldRates();
        }

        internal static void RefreshWorldRates()
        {
            if (ZoneSystem.instance != null && Game.instance != null)
            {
                ZoneSystem.instance.UpdateWorldRates();
            }
        }

        internal static bool Heal()
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return false;
            }
            player.Heal(player.GetMaxHealth(), true);
            return true;
        }

        // Same result as repairing at a crafting station: durability back to the item's own maximum.
        // Returns -1 when there is no local player.
        internal static int RepairAll()
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return -1;
            }
            int repaired = 0;
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems())
            {
                if (item.m_shared.m_useDurability && item.m_durability < item.GetMaxDurability())
                {
                    item.m_durability = item.GetMaxDurability();
                    repaired++;
                }
            }
            return repaired;
        }
    }
}
