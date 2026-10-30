using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Undo Remove Key puts back the last piece you took down with the hammer, where it stood and as it faced, paying its materials again as building it would: the materials came back to you when you removed it. Only your own removals count, within reach of where you stand now, and only where the game would let you build it (station in range, materials, a ward that admits you).
internal static class UndoRemove
{
    private const string Section = "Building";
    private const int Kept = 20;

    private static ConfigEntry<bool> _on;
    private static ConfigEntry<KeyboardShortcut> _key;
    private static readonly List<(string Prefab, Vector3 Position, Quaternion Rotation)> Removed = [];
    private static bool _removing;

    public static void Bind(ConfigFile cfg)
    {
        _on = cfg.Bind(Section, "Undo Remove", true,
            "Undo Remove Key puts back the last pieces you removed with the hammer, one per press, paying their materials again.");
        _key = cfg.Bind(Section, "Undo Remove Key", new KeyboardShortcut(KeyCode.Backspace),
            "Puts back the last piece you removed with the hammer, while Undo Remove is on and a building tool is in hand.");
    }

    public static void Tick()
    {
        var player = Player.m_localPlayer;
        if (!_on.Value || player == null || !player.InPlaceMode() || Removed.Count == 0 || InventoryFeatures.Typing() || !KeyInput.Down(_key.Value) || !ServerRules.Allows("UndoRemove"))
        {
            return;
        }
        var (prefabName, position, rotation) = Removed[Removed.Count - 1];
        var prefab = ZNetScene.instance == null ? null : ZNetScene.instance.GetPrefab(prefabName);
        var piece = prefab == null ? null : prefab.GetComponent<Piece>();
        if (piece == null)
        {
            Removed.RemoveAt(Removed.Count - 1);
            return;
        }
        var name = Localization.instance.Localize(piece.m_name);
        if (Vector3.Distance(player.transform.position, position) > player.m_maxPlaceDistance + 12f)
        {
            player.Message(MessageHud.MessageType.Center, Lang.F("UndoRemove.WalkBackToTheTo", "Walk back to the {0} to put it back", name));
            return;
        }
        if (!PrivateArea.CheckAccess(position))
        {
            return;
        }
        if (!player.m_noPlacementCost && !player.HaveRequirements(piece, Player.RequirementMode.CanBuild))
        {
            player.Message(MessageHud.MessageType.Center, Lang.F("UndoRemove.YouLackWhatTheNeeds", "You lack what the {0} needs", name));
            return;
        }
        Removed.RemoveAt(Removed.Count - 1);
        player.PlacePiece(piece, position, rotation);
        if (!player.m_noPlacementCost && !ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey()))
        {
            player.ConsumeResources(piece.m_resources, 0);
        }
        player.Message(MessageHud.MessageType.TopLeft, Lang.F("UndoRemove.PutBackThe", "Put back the {0}", name));
        if (DebugLog.On)
        {
            DebugLog.Write("undo", $"put back {prefabName} at {position}");
        }
    }

    private static void Remember(Piece piece)
    {
        if (!_removing || piece == null || _on == null || !_on.Value)
        {
            return;
        }
        _removing = false;
        Removed.Add((Utils.GetPrefabName(piece.gameObject), piece.transform.position, piece.transform.rotation));
        if (Removed.Count > Kept)
        {
            Removed.RemoveAt(0);
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.RemovePiece))]
    internal static class RemovePatch
    {
        private static void Prefix(Player __instance) => _removing = __instance == Player.m_localPlayer;

        private static void Finalizer() => _removing = false;
    }

    // The game's two ways a hammer removes a piece: a WearNTear piece removes itself, anything else drops its resources first.
    [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Remove))]
    internal static class WearPatch
    {
        private static void Prefix(WearNTear __instance) => Remember(__instance.GetComponent<Piece>());
    }

    [HarmonyPatch(typeof(Piece), nameof(Piece.DropResources))]
    internal static class DropPatch
    {
        private static void Prefix(Piece __instance) => Remember(__instance);
    }
}
