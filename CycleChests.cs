using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// While a chest is open in the inventory window, Next Chest Key and Previous Chest Key open the next or previous chest within the game's own reach (or Cycle Chests Range with the Beyond Reach cheat), nearest first, so chests can be browsed without walking to each. Only chests the player could open by hand are offered, and each is opened through the game's own Container.Interact, so the owner hands it over as it does for a hand-opened chest.
internal static class CycleChests
{
    private const float AnswerWait = 3f;
    private static readonly Collider[] Near = new Collider[2048];
    private static readonly int Mask = LayerMask.GetMask("piece", "piece_nonsolid", "vehicle");

    private static ConfigEntry<bool> _on;
    private static ConfigEntry<KeyboardShortcut> _next;
    private static ConfigEntry<KeyboardShortcut> _previous;
    private static ConfigEntry<bool> _beyond;
    private static ConfigEntry<float> _range;
    private static Container _opened;
    private static float _requestedAt;
    private static bool _raised;
    private static float _gameReach;

    public static void Bind(ConfigFile cfg)
    {
        _on = cfg.Bind("Inventory", "Cycle Chests", true,
            "While a chest is open, Next Chest Key and Previous Chest Key open the next or previous chest you could open by hand from where you stand, nearest first.");
        _next = cfg.Bind("Inventory", "Next Chest Key", new KeyboardShortcut(KeyCode.PageDown),
            "With a chest open, opens the next chest farther away, while Cycle Chests is on.");
        _previous = cfg.Bind("Inventory", "Previous Chest Key", new KeyboardShortcut(KeyCode.PageUp),
            "With a chest open, opens the previous chest nearer to you, while Cycle Chests is on.");
        _beyond = cfg.Bind("Cheats: Inventory", "Cycle Chests Beyond Reach (Cheat)", false,
            "Cycle Chests also opens chests out to Cycle Chests Range and keeps them open there. A cheat: the game closes a chest once you are 4 m from it.");
        _range = cfg.Bind("Cheats: Inventory", "Cycle Chests Range", 10f, new ConfigDescription(
            "How far from you, in meters, Cycle Chests Beyond Reach looks for chests. Cart and ship storage counts only within the game's own reach.", new AcceptableValueRange<float>(3f, 30f)));
    }

    private static bool On => _on != null && _on.Value && ServerRules.Allows("CycleChests");

    internal static bool ChestKeyDown =>
        On && InventoryGui.instance != null && InventoryGui.IsVisible() && InventoryGui.instance.m_currentContainer != null
        && (KeyInput.Down(_next.Value) || KeyInput.Down(_previous.Value));

    private static bool Beyond =>_beyond.Value && ServerRules.Allows("CycleChestsBeyondReach");

    public static void Tick()
    {
        var player = Player.m_localPlayer;
        var gui = InventoryGui.instance;
        if (gui == null)
        {
            return;
        }
        var current = InventoryGui.IsVisible() ? gui.m_currentContainer : null;
        if (!On || player == null || !InventoryGui.IsVisible()
            || current != null && current != _opened
            || current == null && Time.time > _requestedAt + AnswerWait)
        {
            _opened = null;
        }
        Reach(gui, _opened != null && Beyond);
        if (!On || player == null || current == null || InventoryFeatures.Typing())
        {
            return;
        }
        var step = KeyInput.Down(_next.Value) ? 1 : KeyInput.Down(_previous.Value) ? -1 : 0;
        if (step != 0)
        {
            Cycle(player, gui, current, step);
        }
    }

    // The game closes an open chest farther than InventoryGui.m_autoCloseDistance (4 m) from the player, so the distance is raised to the range only while a chest this feature opened is open or its answer is awaited, and given back afterwards.
    private static void Reach(InventoryGui gui, bool raise)
    {
        if (raise)
        {
            if (!_raised)
            {
                _gameReach = gui.m_autoCloseDistance;
                _raised = true;
            }
            gui.m_autoCloseDistance = Mathf.Max(_gameReach, _range.Value + 1f);
        }
        else if (_raised)
        {
            gui.m_autoCloseDistance = _gameReach;
            _raised = false;
        }
    }

    private static void Cycle(Player player, InventoryGui gui, Container current, int step)
    {
        var chests = Candidates(player, gui, current);
        var at = chests.IndexOf(current);
        if (chests.Count == 0 || chests.Count == 1 && at == 0)
        {
            player.Message(MessageHud.MessageType.TopLeft, Lang.F("CycleChests.NoOtherChestWithinM", "No other chest within {0:0} m", Range(gui)));
            return;
        }
        var index = at < 0 ? (step > 0 ? 0 : chests.Count - 1) : (at + step + chests.Count) % chests.Count;
        var chest = chests[index];
        gui.CloseContainer();
        _opened = chest;
        _requestedAt = Time.time;
        Reach(gui, Beyond);
        chest.Interact(player, false, false);
        var name = ChestNames.NameOf(chest);
        if (name.Length == 0)
        {
            name = Localization.instance.Localize(chest.m_name);
        }
        player.Message(MessageHud.MessageType.TopLeft, Lang.F("CycleChests.Position", "{0}, {1} of {2}", name, index + 1, chests.Count));
        if (DebugLog.On)
        {
            DebugLog.Write("chests", $"cycled {(step > 0 ? "next" : "previous")} to {chest.name} at {chest.transform.position}, {index + 1} of {chests.Count}");
        }
    }

    private static float GameReach(InventoryGui gui) => _raised ? _gameReach : gui.m_autoCloseDistance;

    private static float Range(InventoryGui gui) => Beyond ? Mathf.Max(_range.Value, GameReach(gui)) : GameReach(gui);

    private static List<Container> Candidates(Player player, InventoryGui gui, Container current)
    {
        var position = player.transform.position;
        var gameReach = GameReach(gui);
        var range = Range(gui);
        var count = Physics.OverlapSphereNonAlloc(position, range, Near, Mask);
        var seen = new HashSet<Container>();
        var found = new List<(Container Chest, float Distance)>();
        for (var i = 0; i < count; i++)
        {
            var chest = Near[i] == null ? null : Near[i].GetComponentInParent<Container>();
            if (chest == null || !seen.Add(chest))
            {
                continue;
            }
            var distance = Vector3.Distance(position, chest.transform.position);
            if (distance <= range && (chest == current || Openable(player, chest, distance, gameReach)))
            {
                found.Add((chest, distance));
            }
        }
        return found.OrderBy(entry => entry.Distance).Select(entry => entry.Chest).ToList();
    }

    // The checks Container.Interact makes (ward, privacy) and the refusals RPC_RequestOpen sends back to anyone but the owner (in use, a cart being pulled), read from the chest's ZDO since only its owner keeps m_inUse.
    private static bool Openable(Player player, Container chest, float distance, float gameReach)
    {
        if (chest.m_nview == null || !chest.m_nview.IsValid() || chest.GetInventory() == null || chest.GetComponent<TombStone>() != null)
        {
            return false;
        }
        if (!chest.CheckAccess(player.GetPlayerID()) || chest.m_checkGuardStone && !PrivateArea.CheckAccess(chest.transform.position, 0f, false))
        {
            return false;
        }
        var vehicle = chest.m_wagon != null || chest.GetComponentInParent<Ship>() != null;
        if (vehicle && distance > gameReach)
        {
            return false;
        }
        if (chest.IsOwner())
        {
            return true;
        }
        return chest.m_nview.GetZDO().GetInt(ZDOVars.s_inUse) != 1 && (chest.m_wagon == null || !chest.m_wagon.IsAttached());
    }
}
