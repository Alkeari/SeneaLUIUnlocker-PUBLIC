using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// More cheats, off by default. Unlimited Stamina skips every stamina cost of the local player. No Fall Damage lets the landing see no fall at all, so the game deals no damage for it. No Skill Loss On Death skips the game's lowering of every skill when you die. No Durability Loss gives back whatever durability your gear loses, from any cause, so tools, weapons, armor and torches never wear out; repairs still raise it.
internal static class MoreCheats
{
    private const string Section = "Cheats: Survival";

    private static ConfigEntry<bool> _skills;
    private static ConfigEntry<bool> _durability;
    private static ConfigEntry<bool> _stamina;
    private static ConfigEntry<bool> _fall;
    private static readonly Dictionary<ItemDrop.ItemData, float> Kept = [];

    public static void Bind(ConfigFile cfg)
    {
        _skills = cfg.Bind(Section, "No Skill Loss On Death (Cheat)", false,
            "Cheat, off by default: dying no longer lowers your skills.");
        _durability = cfg.Bind(Section, "No Durability Loss (Cheat)", false,
            "Cheat, off by default: your tools, weapons, armor and torches never lose durability.");
        _stamina = cfg.Bind(Section, "Unlimited Stamina (Cheat)", false,
            "Cheat, off by default: running, jumping, swimming, fighting and building never use stamina.");
        _fall = cfg.Bind(Section, "No Fall Damage (Cheat)", false,
            "Cheat, off by default: falling from any height does you no harm.");
    }

    public static void Tick()
    {
        var player = Player.m_localPlayer;
        if (player == null || !ServerRules.On(_durability, "NoDurabilityLoss"))
        {
            Kept.Clear();
            return;
        }
        foreach (var item in player.GetInventory().GetAllItems())
        {
            if (!item.m_shared.m_useDurability)
            {
                continue;
            }
            if (Kept.TryGetValue(item, out var kept) && item.m_durability < kept)
            {
                item.m_durability = kept;
            }
            else
            {
                Kept[item] = item.m_durability;
            }
        }
    }

    [HarmonyPatch(typeof(Skills), nameof(Skills.OnDeath))]
    internal static class SkillPatch
    {
        private static bool Prefix(Skills __instance) =>
            Player.m_localPlayer == null || __instance != Player.m_localPlayer.GetSkills() || !ServerRules.On(_skills, "NoSkillLoss");
    }

    [HarmonyPatch(typeof(Player), nameof(Player.UseStamina))]
    internal static class StaminaPatch
    {
        private static bool Prefix(Player __instance) => __instance != Player.m_localPlayer || !ServerRules.On(_stamina, "UnlimitedStamina");
    }

    [HarmonyPatch(typeof(Character), nameof(Character.UpdateGroundContact))]
    internal static class FallPatch
    {
        private static void Prefix(Character __instance)
        {
            if (__instance == Player.m_localPlayer && __instance.m_groundContact && ServerRules.On(_fall, "NoFallDamage"))
            {
                __instance.m_maxAirAltitude = __instance.transform.position.y;
            }
        }
    }
}
