using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Each kind of auto-pin is a pin type of its own, so the map shows and hides each kind on its own and SeneaL UI's map gets a chip for each.
// The types lie past every type the game and SeneaL UI know (SeneaL UI's own extra types end at its ore type, the game's count plus 4), so they exist only while this plugin runs: they are written into a save, and into a shared map, as plain dots, and turned back into their kind when the plugin sees them again (AutoPins). A character played without this plugin therefore never carries a pin type the game cannot draw.
internal static class PinKinds
{
    public static string[] Names => [Lang.T("PinKinds.Traders", "Traders"), Lang.T("PinKinds.Dungeons", "Dungeons"), Lang.T("PinKinds.VillagesAndCamps", "Villages And Camps"), Lang.T("PinKinds.NestsAndPits", "Nests And Pits"), Lang.T("PinKinds.Resources", "Resources"), Lang.T("PinKinds.Tames", "Tames"), Lang.T("PinKinds.Runestones", "Runestones")];
    public static readonly string[] Icons = ["bag", "swords", "house", "alert", "star", "cat", "valknut"];

    private static readonly int First = Enum.GetValues(typeof(Minimap.PinType)).Length + 10;
    private static ConfigEntry<string> _hidden;

    public static Minimap.PinType Type(int index) => (Minimap.PinType)(First + index);

    public static Minimap.PinType Of(string kind) => Type(Array.IndexOf(Names, kind));

    public static bool Has(string kind) => Array.IndexOf(Names, kind) >= 0;

    public static int IndexOf(Minimap.PinType type) => (int)type - First is var i && i >= 0 && i < Names.Length ? i : -1;

    public static void Bind(ConfigFile cfg)
    {
        _hidden = cfg.Bind("Map", "Hidden Pin Kinds", "", new ConfigDescription(
            "Kinds of auto-pins hidden from the map with their chip, kept by the mod.", null, new ConfigurationManagerAttributes { Browsable = false }));
        _private = cfg.Bind("Map", "Kinds Kept Off Cartography Tables", "",
            "Kinds of auto-pins that stay on your own map only and are not written to a cartography table.");
    }

    private static ConfigEntry<string> _private;

    // A single pin kept off cartography tables by its own switch (MapPinExtras).
    public static Func<Minimap.PinData, bool> KeptOff = _ => false;

    public static bool Shown(int index) => !SettingsPage.Split(_hidden?.Value).Contains(Names[index]);

    public static void SetShown(int index, bool on)
    {
        var hidden = SettingsPage.Split(_hidden.Value);
        if (on)
        {
            hidden.Remove(Names[index]);
        }
        else
        {
            hidden.Add(Names[index]);
        }
        _hidden.Value = string.Join("; ", hidden);
    }

    // The game sizes its visible-types table to its own pin types; it is widened here to cover these, holding each kind's shown state, and their pins get the game's dot as their picture until SeneaL UI's map gives them their own.
    public static void Tick()
    {
        var map = Minimap.instance;
        if (map == null || _hidden == null)
        {
            return;
        }
        var visible = Widen(map);
        for (var i = 0; i < Names.Length; i++)
        {
            visible[First + i] = Shown(i);
        }
        var dot = map.GetSprite(Minimap.PinType.Icon3);
        foreach (var pin in map.m_pins)
        {
            if (IndexOf(pin.m_type) >= 0 && pin.m_icon == null)
            {
                pin.m_icon = dot;
            }
        }
    }

    private static bool[] Widen(Minimap map)
    {
        var visible = map.m_visibleIconTypes;
        var needed = First + Names.Length;
        if (visible != null && visible.Length < needed)
        {
            var wider = new bool[needed];
            for (var i = 0; i < needed; i++)
            {
                wider[i] = i >= visible.Length ? i < First || Shown(i - First) : visible[i];
            }
            map.m_visibleIconTypes = visible = wider;
        }
        return visible;
    }

    // The game reads the table for every pin as it updates the map, and SeneaL UI may hand it a new, shorter table when it restores its own filters, so it is widened again right before each map update.
    [HarmonyPatch(typeof(Minimap), nameof(Minimap.Update))]
    internal static class UpdatePatch
    {
        private static void Prefix(Minimap __instance) => Widen(__instance);
    }

    // Saves and shared maps get these pins as plain dots for the length of the write.
    private static List<(Minimap.PinData Pin, Minimap.PinType Type)> AsDots(Minimap map)
    {
        var changed = new List<(Minimap.PinData, Minimap.PinType)>();
        foreach (var pin in map.m_pins.Where(pin => IndexOf(pin.m_type) >= 0))
        {
            changed.Add((pin, pin.m_type));
            pin.m_type = Minimap.PinType.Icon3;
        }
        return changed;
    }

    private static void Restore(List<(Minimap.PinData Pin, Minimap.PinType Type)> changed)
    {
        foreach (var (pin, type) in changed)
        {
            pin.m_type = type;
        }
    }

    [HarmonyPatch(typeof(Minimap), nameof(Minimap.GetMapData))]
    internal static class SavePatch
    {
        private static void Prefix(Minimap __instance, out List<(Minimap.PinData, Minimap.PinType)> __state) => __state = AsDots(__instance);

        private static void Finalizer(List<(Minimap.PinData, Minimap.PinType)> __state) => Restore(__state);
    }

    [HarmonyPatch(typeof(Minimap), nameof(Minimap.GetSharedMapData))]
    // A cartography table also leaves out the kinds kept off it, which are unsaved for the length of the write.
    internal static class SharePatch
    {
        private static void Prefix(Minimap __instance, out (List<(Minimap.PinData, Minimap.PinType)> Types, List<Minimap.PinData> Kept) __state)
        {
            var kept = SettingsPage.Split(_private?.Value);
            var held = __instance.m_pins.Where(pin => pin.m_save && (IndexOf(pin.m_type) is var i && i >= 0 && kept.Contains(Names[i]) || KeptOff(pin))).ToList();
            foreach (var pin in held)
            {
                pin.m_save = false;
            }
            __state = (AsDots(__instance), held);
        }

        private static void Finalizer((List<(Minimap.PinData, Minimap.PinType)> Types, List<Minimap.PinData> Kept) __state)
        {
            Restore(__state.Types);
            foreach (var pin in __state.Kept)
            {
                pin.m_save = true;
            }
        }
    }
}
