using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// How the game decides where a piece may stand. Vanilla keeps every rule. Accurate drops the game's spacing radius between station extensions (2 m between the pivots of two Chopping Blocks, Tanning Racks, Anvils and the like, though most are under a meter across), so they may stand as close as their own shapes allow: every extension keeps the game's clipping test, which refuses a piece whose colliders sink into another more than 0.2 m. Unrestricted (Cheat) accepts any spot where the piece shows, as DadsBetterVal does, except that it keeps refusing a spot inside another player (the game would trap them) and, unless the setting says otherwise, inside someone's ward.
internal static class BuildRestrictions
{
    private const string Section = "Building";
    private const string CheatSection = "Cheats: Building";
    internal const string Vanilla = "Vanilla";
    internal const string Accurate = "Accurate";
    internal const string Unrestricted = "Unrestricted (Cheat)";

    private static ConfigEntry<string> _mode;
    private static ConfigEntry<bool> _wards;
    private static ConfigEntry<bool> _noBuild;
    private static ConfigEntry<bool> _station;

    public static void Bind(ConfigFile cfg)
    {
        _mode = cfg.Bind(Section, "Build Restrictions", Accurate, new ConfigDescription(
            "Vanilla = every placement rule of the game. Accurate = station extensions (Chopping Block, Tanning Rack, Anvils and the like) may stand as close together as their own shapes allow, instead of 2 m apart; every other rule stays. Unrestricted (Cheat) = any spot where the piece shows is valid, except inside another player, with the choices in Cheats, Building.",
            new AcceptableValueList<string>(Vanilla, Accurate, Unrestricted)));
        _wards = cfg.Bind(CheatSection, "Unrestricted Ignores Wards (Cheat)", false,
            "Cheat, off by default: with Build Restrictions on Unrestricted (Cheat), pieces may also be placed inside other players' wards.");
        _noBuild = cfg.Bind(CheatSection, "Unrestricted Ignores No-Build Zones (Cheat)", false,
            "Cheat, off by default: with Build Restrictions on Unrestricted (Cheat), pieces may be placed in the places the game keeps free of building (around traders, boss altars and the like).");
        _station = cfg.Bind(CheatSection, "Unrestricted Ignores Station Range (Cheat)", false,
            "Cheat, off by default: with Build Restrictions on Unrestricted (Cheat), pieces that need a crafting station nearby (a workbench for most) may be placed without one.");
    }

    // The mode in effect: a server running this plugin can deny Unrestricted, and then Accurate applies where it is allowed.
    internal static string Mode()
    {
        var mode = _mode?.Value ?? Vanilla;
        if (mode == Unrestricted && !ServerRules.Allows("UnrestrictedBuilding"))
        {
            mode = Accurate;
        }
        if (mode == Accurate && !ServerRules.Allows("AccuratePlacement"))
        {
            mode = Vanilla;
        }
        return mode;
    }

    private static Player _placing;

    // The extension spacing test is called only while the local player places a piece. A postfix, so the answer stands whatever replaced the test first (ValheimCommunityPatch answers it from a prefix of its own).
    [HarmonyPatch(typeof(StationExtension), nameof(StationExtension.OtherExtensionInRange))]
    internal static class SpacingPatch
    {
        private static void Postfix(ref bool __result)
        {
            if (_placing != null && Mode() != Vanilla)
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.UpdatePlacementGhost))]
    internal static class GhostPatch
    {
        private static void Prefix(Player __instance)
        {
            _placing = __instance == Player.m_localPlayer ? __instance : null;
        }

        private static void Postfix(Player __instance)
        {
            _placing = null;
            var ghost = __instance.m_placementGhost;
            if (__instance != Player.m_localPlayer || ghost == null || !ghost.activeSelf || Mode() != Unrestricted)
            {
                return;
            }
            var status = __instance.m_placementStatus;
            var kept = status == Player.PlacementStatus.Valid
                || status == Player.PlacementStatus.NoRayHits
                || status == Player.PlacementStatus.BlockedbyPlayer
                || status == Player.PlacementStatus.PrivateZone && !_wards.Value
                || status == Player.PlacementStatus.NoBuildZone && !_noBuild.Value;
            if (kept)
            {
                return;
            }
            // A spot the game refused for more than one reason is still checked for the ones kept.
            if (!_noBuild.Value && Location.IsInsideNoBuildLocation(ghost.transform.position) || !_wards.Value && !PrivateArea.CheckAccess(ghost.transform.position, 0f, false, false))
            {
                return;
            }
            __instance.m_placementStatus = Player.PlacementStatus.Valid;
            __instance.SetPlacementGhostValid(true);
        }
    }

    // The station a piece needs is set aside while the game checks whether the player can build it, and put back right after. First of all prefixes, since SeneaL UI's building from chests answers this check from a prefix of its own.
    [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), typeof(Piece), typeof(Player.RequirementMode))]
    internal static class StationPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(Player __instance, Piece piece, Player.RequirementMode mode, out CraftingStation __state)
        {
            __state = null;
            if (mode != Player.RequirementMode.CanBuild || piece == null || piece.m_craftingStation == null || __instance != Player.m_localPlayer
                || !_station.Value || Mode() != Unrestricted
                || CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name, __instance.transform.position) != null)
            {
                return;
            }
            __state = piece.m_craftingStation;
            piece.m_craftingStation = null;
        }

        private static void Finalizer(Piece piece, CraftingStation __state)
        {
            if (__state != null)
            {
                piece.m_craftingStation = __state;
            }
        }
    }
}
