using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// Settings this plugin no longer binds stay in its settings file forever, since BepInEx keeps every line it does not know: a player reading the file finds two sections for the same cheats and cannot tell which one counts.
internal static class RetiredSettings
{
    private static readonly HashSet<string> Sections = ["Map Cheats", "Beyond Maximums"];

    // Single settings removed or moved to another section; each was checked against the history of this plugin's sources.
    private static readonly HashSet<(string Section, string Key)> Keys =
    [
        ("Compatibility", "Input Tweaks Key Layout"),
        ("Interface", "Separate Crafting Placement"),
        ("Server Rules", "Allow Separate Crafting Placement"),
        ("Compatibility", "Quick Move Key"),
        ("Compatibility", "Split Popup Key"),
        ("HUD Editor", "Keep Elements On Screen"),
        ("HUD Editor", "Snap"),
        ("HUD Editor", "Grid"),
        ("HUD Editor", "Grid Size"),
        ("HUD Editor", "Show Element List"),
        ("HUD Editor", "Use Unlocker HUD Editor"),
        ("Inventory", "Multi-Select Key"),
        ("Inventory", "Slot Lock By Key"),
        ("Inventory", "Slot Lock Key"),
        ("Inventory", "Slot Lock Key Moved"),
        ("Party", "Party Mode"),
        ("Party", "Auto Join Nearby"),
        ("Party", "Join Radius"),
        ("Portals", "Ores And Metals Through Portals (Cheat)"),
        ("Portals", "Ores And Metals Through Portals"),
        ("Previews", "Build Areas"),
        ("Previews", "Ward Areas"),
        ("Previews", "Safe Areas"),
        ("Previews", "Warmth Areas"),
        ("Previews", "Comfort Levels"),
        ("Previews", "Extension Ranges"),
        ("Previews", "Plant Spacing"),
        ("Previews", "Area Fill Opacity"),
        ("Previews", "Comfort Spacing"),
        ("Previews", "Area Rings"),
        ("Previews", "Comfort Preview"),
        ("Safety", "Warn About Extra Slot Items")
    ];

    public static Dictionary<ConfigDefinition, string> Orphans(ConfigFile cfg) =>
        Traverse.Create(cfg).Property("OrphanedEntries").GetValue<Dictionary<ConfigDefinition, string>>() ?? [];

    public static void Drop(ConfigFile cfg)
    {
        var orphans = Orphans(cfg);
        var retired = orphans.Keys.Where(definition => Sections.Contains(definition.Section) || Keys.Contains((definition.Section, definition.Key))).ToList();
        if (retired.Count == 0)
        {
            return;
        }
        foreach (var definition in retired)
        {
            orphans.Remove(definition);
        }
        cfg.Save();
    }
}
