using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// Removes the ore markers SeneaL UI strikes through as used up. They go through OreScout.Forget, SeneaL UI's own path for a marker the player deletes: the deposit stays in its list marked off, so it is never pinned again.
internal sealed class DepletedOrePins
{
    private const float CheckSeconds = 2f;

    private static readonly Type OreScout = TypeLookup.Find("SeneaLUI.Map.OreScout");
    private static readonly Type MapPins = TypeLookup.Find("SeneaLUI.Map.MapPins");
    private static readonly Type SeneaLPlugin = TypeLookup.Find("SeneaLUI.Plugin");
    private static readonly MethodInfo Forget = OreScout == null ? null : AccessTools.Method(OreScout, nameof(SeneaLUI.Map.OreScout.Forget), [typeof(Minimap.PinData)]);
    private static readonly FieldInfo OrePinType = MapPins == null ? null : AccessTools.Field(MapPins, nameof(SeneaLUI.Map.MapPins.Ore));
    private static readonly PropertyInfo Threshold = SeneaLPlugin == null ? null : AccessTools.Property(SeneaLPlugin, nameof(SeneaLUI.Plugin.OreThreshold));

    private readonly ConfigEntry<bool> _enabled;
    private float _next;

    public DepletedOrePins(ConfigFile cfg)
    {
        _enabled = cfg.Bind("Map", "Remove Depleted Ore Pins", true,
            "Remove an ore marker from the map once SeneaL UI strikes it through as used up ([Ore] DepletedAt). SeneaL UI then never marks that deposit again.");
    }

    public void Tick(float now)
    {
        var map = Minimap.instance;
        if (!_enabled.Value || !ServerRules.Allows("DepletedOrePins") || Forget == null || OrePinType == null || Threshold == null || map == null || Player.m_localPlayer == null || now < _next)
        {
            return;
        }
        _next = now + CheckSeconds;
        var ore = (Minimap.PinType)OrePinType.GetValue(null);
        if (Threshold.GetValue(null, null) is not ConfigEntry<int> threshold)
        {
            return;
        }
        foreach (var pin in map.m_pins.Where(pin => pin.m_type == ore && pin.m_checked && Share(pin.m_name) <= threshold.Value).ToList())
        {
            Forget.Invoke(null, [pin]);
        }
    }

    // SeneaL UI labels its ore markers "<ore name> <share>%". A marker the player crossed out by hand still shows a share above the threshold and stays.
    private static int Share(string label)
    {
        var space = label.LastIndexOf(' ');
        return space >= 0 && label.EndsWith("%") && int.TryParse(label.Substring(space + 1, label.Length - space - 2), out var share) ? share : int.MaxValue;
    }
}
