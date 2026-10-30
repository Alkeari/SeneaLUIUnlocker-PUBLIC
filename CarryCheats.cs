using BepInEx.Configuration;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// Two separate carrying cheats, both off by default. Infinite Carry Weight lifts the limit itself, so auto-pickup never stops for weight either. No Encumbrance keeps the limit, and the game's refusal to auto-pick up past it, but never slows you or drains stamina for going over it.
internal static class CarryCheats
{
    private const string Section = "Cheats: Carrying";
    private const float Unlimited = 100000f;

    private static ConfigEntry<bool> _infinite;
    private static ConfigEntry<bool> _noEncumbrance;

    public static void Bind(ConfigFile cfg)
    {
        _infinite = cfg.Bind(Section, "Infinite Carry Weight (Cheat)", false,
            "Cheat, off by default: your carry weight limit is 100000, so you are never encumbered and auto-pickup never stops for weight.");
        _noEncumbrance = cfg.Bind(Section, "No Encumbrance (Cheat)", false,
            "Cheat, off by default: going over your carry weight never encumbers you (no slowdown, no stamina drain, running allowed), while the limit itself and auto-pickup stopping at it stay as the game has them.");
    }

    private static bool Local(Player player) => player != null && player == Player.m_localPlayer;

    [HarmonyPatch(typeof(Player), nameof(Player.GetMaxCarryWeight))]
    internal static class WeightPatch
    {
        private static void Postfix(Player __instance, ref float __result)
        {
            if (_infinite != null && Local(__instance) && ServerRules.On(_infinite, "InfiniteCarry") && __result < Unlimited)
            {
                __result = Unlimited;
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.IsEncumbered))]
    internal static class EncumberedPatch
    {
        private static void Postfix(Player __instance, ref bool __result)
        {
            if (__result && _noEncumbrance != null && Local(__instance) && ServerRules.On(_noEncumbrance, "NoEncumbrance"))
            {
                __result = false;
            }
        }
    }
}
