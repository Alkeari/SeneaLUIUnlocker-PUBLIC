using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Two small helpers asked for by SeneaL UI players. The game keeps one bed pin and moves it to each new bed you sleep in, so the bed at home vanishes from the map the first night you camp out: Keep Old Bed Pins leaves a saved pin where the old bed was. Hotbar Swap Key trades the hotbar with another row of the inventory, a second hotbar for tools or a second weapon set.
internal sealed class BedAndHotbar
{
    private readonly ConfigEntry<bool> _bedPins;
    private readonly ConfigEntry<Minimap.PinType> _bedIcon;
    private readonly ConfigEntry<bool> _swap;
    private readonly ConfigEntry<KeyboardShortcut> _swapKey;
    private readonly ConfigEntry<int> _swapRow;
    private Vector3? _bed;
    private float _next;

    public BedAndHotbar(ConfigFile cfg)
    {
        _bedPins = cfg.Bind("Map", "Keep Old Bed Pins", true,
            "When you set your spawn point at a new bed, a saved pin named Old Bed stays where the previous bed was.");
        _bedIcon = cfg.Bind("Map", "Old Bed Pin Icon", Minimap.PinType.Icon1,
            "The map icon of the pin Keep Old Bed Pins leaves at an old bed.");
        _swap = cfg.Bind("Inventory", "Hotbar Swap", true,
            "Hotbar Swap Key trades the items of the hotbar with those of Hotbar Swap Row, keeping what you wear equipped.");
        _swapKey = cfg.Bind("Inventory", "Hotbar Swap Key", new KeyboardShortcut(KeyCode.J),
            "Trades the hotbar with Hotbar Swap Row, while Hotbar Swap is on.");
        _swapRow = cfg.Bind("Inventory", "Hotbar Swap Row", 2, new ConfigDescription(
            "The inventory row, counted from the hotbar as row 1, that Hotbar Swap Key trades with the hotbar.", new AcceptableValueRange<int>(2, 8)));
    }

    public void Tick(float now)
    {
        var player = Player.m_localPlayer;
        if (player == null)
        {
            _bed = null;
            return;
        }
        Swap(player);
        if (now < _next)
        {
            return;
        }
        _next = now + 2f;
        var profile = Game.instance == null ? null : Game.instance.GetPlayerProfile();
        if (profile == null || !profile.HaveCustomSpawnPoint())
        {
            return;
        }
        var bed = profile.GetCustomSpawnPoint();
        if (_bed is { } old && Vector3.Distance(old, bed) > 5f && _bedPins.Value && ServerRules.Allows("OldBedPins") && Minimap.instance != null)
        {
            if (Minimap.instance.m_pins.TrueForAll(pin => pin.m_name != "Old Bed" || Vector3.Distance(pin.m_pos, old) > 5f))
            {
                Minimap.instance.AddPin(old, _bedIcon.Value, "Old Bed", true, false);
                if (DebugLog.On)
                {
                    DebugLog.Write("pins", $"old bed pin at {old}, new bed at {bed}");
                }
            }
        }
        _bed = bed;
    }

    private void Swap(Player player)
    {
        if (!_swap.Value || InventoryFeatures.Typing() || !KeyInput.Down(_swapKey.Value) || !ServerRules.Allows("HotbarSwap"))
        {
            return;
        }
        var inventory = player.GetInventory();
        var row = _swapRow.Value - 1;
        // SeneaL UI keeps hidden rows of mod slots below the rows it shows (Slots.MainRows), and a swap must never reach them.
        if (row >= (SeneaLCalls.CanSort ? SeneaLCalls.MainRows(inventory) : inventory.GetHeight()))
        {
            player.Message(MessageHud.MessageType.Center, Lang.F("BedAndHotbar.YourInventoryHasNoRow", "Your inventory has no row {0}", _swapRow.Value));
            return;
        }
        for (var x = 0; x < inventory.GetWidth(); x++)
        {
            var top = inventory.GetItemAt(x, 0);
            var other = inventory.GetItemAt(x, row);
            if (top != null)
            {
                top.m_gridPos = new Vector2i(x, row);
            }
            if (other != null)
            {
                other.m_gridPos = new Vector2i(x, 0);
            }
        }
        inventory.Changed();
        if (DebugLog.On)
        {
            DebugLog.Write("hotbar", $"swapped the hotbar with row {_swapRow.Value}");
        }
    }
}
