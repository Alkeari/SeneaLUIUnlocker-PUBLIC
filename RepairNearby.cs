using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Repair Nearby Key, with the hammer in hand, repairs every damaged piece around you at once instead of one click each. Each piece goes through the game's own repair with its own conditions and cost: a station in range for it, a ward that admits you, the hammer's stamina per piece and its wear, so the key stops when you run out of stamina.
internal static class RepairNearby
{
    private static ConfigEntry<bool> _on;
    private static ConfigEntry<KeyboardShortcut> _key;
    private static ConfigEntry<float> _range;

    public static void Bind(ConfigFile cfg)
    {
        _on = cfg.Bind("Building", "Repair Nearby", true,
            "Repair Nearby Key repairs every damaged piece within Repair Nearby Range at once, paying the hammer's stamina for each as repairing them one by one would.");
        _key = cfg.Bind("Building", "Repair Nearby Key", new KeyboardShortcut(KeyCode.U),
            "Repairs every damaged piece near you while Repair Nearby is on and the hammer is in hand.");
        _range = cfg.Bind("Building", "Repair Nearby Range", 15f, new ConfigDescription(
            "How far from you, in meters, Repair Nearby Key repairs.", new AcceptableValueRange<float>(3f, 40f)));
    }

    public static void Tick()
    {
        var player = Player.m_localPlayer;
        if (!_on.Value || player == null || player.IsDead() || !player.InPlaceMode() || InventoryFeatures.Typing() || !KeyInput.Pressed(_key.Value) || !ServerRules.Allows("RepairNearby"))
        {
            return;
        }
        var tool = player.GetRightItem();
        if (tool == null || tool.m_shared.m_buildPieces == null || !tool.m_shared.m_buildPieces.m_pieces.Any(p => p != null && p.GetComponent<Piece>() is { m_repairPiece: true }))
        {
            return;
        }
        var at = player.transform.position;
        var range = _range.Value * _range.Value;
        var repaired = 0;
        var damaged = 0;
        foreach (var wear in WearNTear.GetAllInstances().ToList())
        {
            // Health read from the piece's own saved state, since the cached percentage lags behind fresh damage.
            if (wear == null || wear.m_nview == null || !wear.m_nview.IsValid() || (wear.transform.position - at).sqrMagnitude > range
                || wear.m_nview.GetZDO().GetFloat(ZDOVars.s_health, wear.m_health) >= wear.m_health)
            {
                continue;
            }
            damaged++;
            var piece = wear.GetComponent<Piece>();
            if (piece == null || !player.CheckCanRemovePiece(piece) || !PrivateArea.CheckAccess(piece.transform.position, 0f, false))
            {
                continue;
            }
            if (player.GetStamina() < player.GetBuildStamina())
            {
                player.Message(MessageHud.MessageType.Center, Lang.T("RepairNearby.OutOfStamina", "Out of stamina"));
                break;
            }
            if (!wear.Repair())
            {
                continue;
            }
            repaired++;
            piece.m_placeEffect.Create(piece.transform.position, piece.transform.rotation, null, 1f, -1, player.GetZDOID());
            player.UseStamina(player.GetBuildStamina());
            player.UseEitr(tool.m_shared.m_attack.m_attackEitr);
            if (tool.m_shared.m_useDurability)
            {
                tool.m_durability -= tool.m_shared.m_useDurabilityDrain * Game.m_durabilityRate;
            }
        }
        player.Message(MessageHud.MessageType.TopLeft, damaged == 0 ? Lang.T("RepairNearby.NothingNeedsRepair", "Nothing nearby needs repair") : Lang.F("RepairNearby.Repaired", "Repaired {0} of {1} damaged pieces", repaired, damaged));
        if (repaired > 0)
        {
            player.m_zanim.SetTrigger(tool.m_shared.m_attack.m_attackAnimation);
        }
        if (DebugLog.On)
        {
            DebugLog.Write("repair", $"repaired {repaired} of {damaged} damaged pieces within {_range.Value} m");
        }
    }
}
