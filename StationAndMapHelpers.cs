using System.Linq;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace SeneaLUIUnlocker;

internal static class InstalledMods
{
    public static bool Has(string name) =>
        Chainloader.PluginInfos.Values.Any(info => info.Metadata.Name.Replace(" ", "").IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0);

    // Read on first use, since BepInEx lists a plugin only once it loads and most load after this one.
    public static bool StandsDown(ref bool? known, string name, string setting, ManualLogSource log)
    {
        if (known == null)
        {
            known = Has(name);
            if (known.Value)
            {
                log.LogInfo($"{name} is installed and does this itself; {setting} stands down.");
            }
        }
        return known.Value;
    }
}

// Removes a death pin once its grave is gone: checked only while the player is close enough for the grave to be loaded, so a grave far away is never mistaken for a recovered one.
internal sealed class DeathPinCleanup
{
    private const float LoadedRange = 40f;
    private const float GraveMatch = 6f;
    private const float CheckSeconds = 2f;

    private readonly ConfigEntry<bool> _enabled;
    private readonly ManualLogSource _log;
    private bool? _otherMod;
    private float _next;

    public DeathPinCleanup(ConfigFile cfg, ManualLogSource log)
    {
        _log = log;
        _enabled = cfg.Bind("Map", "Remove Death Pin On Recovery", true,
            "Remove a death pin from the map once its grave has been emptied. Does nothing while the AutoRemoveDeathPin mod is installed, which already does this.");
    }

    public void Tick(float now)
    {
        var player = Player.m_localPlayer;
        var map = Minimap.instance;
        if (InstalledMods.StandsDown(ref _otherMod, "AutoRemoveDeathPin", "Remove Death Pin On Recovery", _log) || !_enabled.Value || !ServerRules.Allows("DeathPins") || player == null || map == null || now < _next)
        {
            return;
        }
        _next = now + CheckSeconds;
        var at = player.transform.position;
        var graves = SceneObjects.Live(SceneObjects.Graves);
        foreach (var pin in map.m_pins.Where(pin => pin.m_type == Minimap.PinType.Death && Vector3.Distance(pin.m_pos, at) < LoadedRange).ToList())
        {
            if (!graves.Any(grave => grave != null && Vector3.Distance(grave.transform.position, pin.m_pos) < GraveMatch))
            {
                map.RemovePin(pin);
            }
        }
    }
}

// Repairs everything the station can repair when the player opens it, using the game's own repair one item at a time.
internal sealed class StationAutoRepair
{
    private const int MostItems = 100;

    private readonly ConfigEntry<bool> _enabled;
    private readonly ManualLogSource _log;
    private bool? _otherMod;
    private CraftingStation _last;

    public StationAutoRepair(ConfigFile cfg, ManualLogSource log)
    {
        _log = log;
        _enabled = cfg.Bind("Crafting", "Auto Repair At Stations", true,
            "Repair every item the crafting station can repair as soon as you open it. Does nothing while the ComfyAutoRepair mod is installed, which already does this.");
    }

    public void Tick()
    {
        var player = Player.m_localPlayer;
        var gui = InventoryGui.instance;
        var station = player == null || gui == null || !InventoryGui.IsVisible() ? null : player.GetCurrentCraftingStation();
        if (station == _last)
        {
            return;
        }
        _last = station;
        if (InstalledMods.StandsDown(ref _otherMod, "ComfyAutoRepair", "Auto Repair At Stations", _log) || !_enabled.Value || !ServerRules.Allows("AutoRepair") || station == null)
        {
            return;
        }
        for (var i = 0; i < MostItems && gui.HaveRepairableItems(); i++)
        {
            gui.RepairOneItem();
        }
    }
}
