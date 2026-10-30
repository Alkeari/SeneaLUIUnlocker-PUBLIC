using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;

namespace SeneaLUIUnlocker;

// SeneaL UI keeps one placement for its Crafting window, but draws the window wider at a crafting station (760 units against 700, CraftView.Relayout), so a spot that fits beside the inventory overlaps it at a station, and fixing it there moves it beside the inventory too.
// This keeps one placement for each case and swaps SeneaL UI's settings to the matching one whenever the inventory opens in the other case. Edits made in the HUD editor go to the case that is open.
internal sealed class CraftingPlacement
{
    private static readonly (string Section, string Key)[] Placement =
    [
        ("Layout", "CraftingX"), ("Layout", "CraftingY"), ("Sizes", "Crafting"),
        ("Layout", "CraftingAttachedTo"), ("Layout", "CraftingAttachSide"), ("Layout", "CraftingAttachAlign"),
        ("Layout", "CraftingAttachGap"), ("Layout", "CraftingAttachX"), ("Layout", "CraftingAttachY")
    ];

    private readonly ConfigEntry<bool> _merged;
    private readonly ConfigEntry<string> _inventory;
    private readonly ConfigEntry<string> _station;
    private readonly ConfigEntry<string> _holding;
    private ConfigFile _seneaL;
    private bool? _atStation;
    private bool _editing;

    public static CraftingPlacement Current { get; private set; }

    public CraftingPlacement(ConfigFile cfg, ConfigEntry<bool> merged)
    {
        _merged = merged;
        var hidden = new ConfigurationManagerAttributes { Browsable = false };
        _inventory = cfg.Bind("Interface", "Crafting Placement Inventory", "", new ConfigDescription("Saved Crafting window placement beside the inventory, kept by the mod.", null, hidden));
        Current = this;
        _station = cfg.Bind("Interface", "Crafting Placement Station", "", new ConfigDescription("Saved Crafting window placement at a crafting station, kept by the mod.", null, hidden));
        _holding = cfg.Bind("Interface", "Crafting Placement Held", "", new ConfigDescription("Which saved Crafting window placement SeneaL UI's settings hold now (Inventory or Station), kept by the mod.", null, hidden));
    }

    public void Tick()
    {
        if (!Enabled)
        {
            _atStation = null;
            return;
        }
        var player = Player.m_localPlayer;
        // A case chosen in the HUD editor holds until the editor closes; the inventory the editor's Tab mode opens is not the player's own.
        if (_editing)
        {
            if (SeneaLHud.EditorOpen)
            {
                return;
            }
            _editing = false;
        }
        if (player == null || !InventoryGui.IsVisible())
        {
            return;
        }
        _seneaL ??= SeneaLConfig.File;
        if (_seneaL == null)
        {
            return;
        }
        var atStation = player.GetCurrentCraftingStation() != null;
        if (_atStation == atStation)
        {
            return;
        }
        // SeneaL UI's settings keep the placement of the case open when the game last closed, which need not be the case seen first now; with no record of it, the first case seen takes it.
        if (Held is { } previous && previous != atStation)
        {
            (previous ? _station : _inventory).Value = Capture();
            var wanted = (atStation ? _station : _inventory).Value;
            if (wanted.Length > 0)
            {
                Restore(wanted);
            }
        }
        Hold(atStation);
    }

    private bool? Held => _atStation ?? (_holding.Value == "Station" ? true : _holding.Value == "Inventory" ? false : Matching());

    private bool? Matching()
    {
        if (_seneaL == null)
        {
            return null;
        }
        return Holds(_station.Value) ? true : Holds(_inventory.Value) ? false : null;
    }

    private bool Holds(string saved)
    {
        var values = Parse(saved);
        return values.Count > 0 && Entries().All(pair => !values.TryGetValue($"{pair.Section}/{pair.Key}", out var value) || pair.Entry.GetSerializedValue() == value);
    }

    private static Dictionary<string, string> Parse(string saved) =>
        saved.Split('|')
            .Select(line => line.Split(['='], 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1]);

    private void Hold(bool station)
    {
        _atStation = station;
        _holding.Value = station ? "Station" : "Inventory";
    }

    // A preset applied places the Crafting window anew: the placements kept for each case give way to it, and each case takes it until moved.
    public void Forget()
    {
        _inventory.Value = "";
        _station.Value = "";
        _holding.Value = "";
        _atStation = null;
    }

    public bool Enabled => !_merged.Value && ServerRules.Allows("CraftingPlacement");

    // The placement the HUD editor is editing: true for the one at a crafting station.
    public bool EditingStation => Held ?? (Player.m_localPlayer != null && Player.m_localPlayer.GetCurrentCraftingStation() != null);

    // Lets the HUD editor place either case without being in it: the open case is saved and the chosen one put into SeneaL UI's settings, and the inventory opening later swaps to the case it is really in, as always.
    public void Edit(bool station)
    {
        _seneaL ??= SeneaLConfig.File;
        if (!Enabled || _seneaL == null)
        {
            return;
        }
        var current = EditingStation;
        if (current != station)
        {
            (current ? _station : _inventory).Value = Capture();
            var wanted = (station ? _station : _inventory).Value;
            if (wanted.Length > 0)
            {
                Restore(wanted);
            }
        }
        Hold(station);
        _editing = true;
    }

    private string Capture() =>
        string.Join("|", Entries().Select(pair => $"{pair.Section}/{pair.Key}={pair.Entry.GetSerializedValue()}"));

    private void Restore(string saved)
    {
        var values = Parse(saved);
        var writes = Entries()
            .Where(pair => values.ContainsKey($"{pair.Section}/{pair.Key}"))
            .Select(pair => (pair.Entry, values[$"{pair.Section}/{pair.Key}"]))
            .ToList();
        SeneaLConfig.Write(_seneaL, writes);
    }

    private IEnumerable<(string Section, string Key, ConfigEntryBase Entry)> Entries() =>
        Placement.Select(p => (p.Section, p.Key, Entry: SeneaLConfig.Entry(_seneaL, p.Section, p.Key))).Where(p => p.Entry != null);
}
