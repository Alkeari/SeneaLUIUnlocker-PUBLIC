using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// Mods that do more than this plugin, but also part of what it and SeneaL UI do, keep their other features while the parts both change are switched off in them, so this plugin's and SeneaL UI's setting is the one that applies.
// The other mod's settings are changed for the session only: whenever its file is saved, by it or by anyone, the saved values are its own, so a game that is killed rather than closed leaves nothing of the change behind.
// A setting the other mod syncs from the server ("[Synced with Server]" in its description) goes back to its own value while this game hosts a world open to other players, since the host's value is sent to everyone who joins (read 2026-10-19 from ConditionalConfigSync 1.0.11, which packages each synced entry's BoxedValue) and would switch the feature off for them too.
internal static class ModOverrides
{
    public const string QuickStackStoreGuid = "goldenrevolver.quick_stack_store";
    public const string ExtraSlotsGuid = "shudnal.ExtraSlots";
    public const string MyLittleUiGuid = "shudnal.MyLittleUI";
    public const string ZenUiGuid = "ZenDragon.ZenUI";
    public const string AutomaticFuelGuid = "TastyChickenLegs.AutomaticFuel";
    public const string ChatterGuid = "redseiko.valheim.chatter";

    private sealed record Rule(string Section, string Key, string Value, string SeneaLSection = null, string SeneaLKey = null);

    private sealed record Mod(string Guid, string Name, string Setting, string Description, Rule[] Rules);

    private static readonly Mod[] Mods =
    [
        new(QuickStackStoreGuid, "Quick Stack Store Sort Trash Restock", "SeneaL UI Wins Over Quick Stack Store",
            "With Quick Stack Store Sort Trash Restock installed, its quick stack, restock, sort, store and trash keys and buttons and its trash can are switched off, so SeneaL UI's own do the job once; its favoriting and stacking rules stay.",
            [
                new("0 - General", "OverrideKeybindBehavior", "DisableAllNewHotkeys"),
                new("0 - General", "OverrideButtonDisplay", "DisableAllNewButtons"),
                new("5 - Trashing", "DisplayTrashCanUI", "false"),
            ]),
        new(ExtraSlotsGuid, "Extra Slots", "SeneaL UI Wins Over Extra Slots",
            "With Extra Slots installed, its quick, utility, ammo, food and misc slots are switched off, so SeneaL UI's own slots hold those items; its other features stay.",
            [
                new("Extra slots", "Amount of quick slots", "0"),
                new("Extra slots", "Amount of extra utility slots", "0"),
                new("Extra slots", "Enable ammo slots", "false"),
                new("Extra slots", "Enable food slots", "false"),
                new("Extra slots", "Enable misc slots", "false"),
            ]),
        new(MyLittleUiGuid, "MyLittleUI", "SeneaL UI Wins Over MyLittleUI",
            "With MyLittleUI installed, its clock, wind and forecast blocks, status effect lists, item tooltip, quality and durability looks, crafting filter, sorting and multicraft, station hovers and chest contents are switched off, so SeneaL UI's and this mod's own show once; its other tweaks stay.",
            [
                new("Info - Clock", "Show time", "false"),
                new("Info - Clock", "Show day", "false"),
                new("Info - Forecast", "Enabled", "false"),
                new("Info - Winds", "Enabled", "false"),
                new("Status effects - Map - List", "Enable", "false"),
                new("Status effects - Nomap - List", "Enable", "false"),
                new("Item - Tooltip", "Enabled", "false"),
                new("Item - Quality", "Enabled", "false"),
                new("Item - Durability", "0 - Enabled", "false"),
                new("Item - Crafting - Filter", "Enabled", "false"),
                new("Item - Crafting - Filter", "Enable sorting panels", "false"),
                new("Item - Available resources amount", "Multicraft", "false"),
                new("Item - Crafting - New Mark", "Enabled", "false"),
                new("Hover - Stations", "Plants Hover Enabled", "false"),
                new("Hover - Stations", "Pickables Hover Enabled", "false"),
                new("Hover - Stations", "Cooking stations Hover Enabled", "false"),
                new("Hover - Stations", "Bee Hive Hover Enabled", "false"),
                new("Hover - Stations", "Fermenter Hover Enabled", "false"),
                new("Hover - Smelters", "Show estimated time", "false"),
                new("Hover - Chests - Content", "Enable chest content", "false"),
                new("Info - Inventory", "Show current weight", "false"),
                new("Info - Inventory", "Show current slots", "false"),
            ]),
        new(ZenUiGuid, "ZenUI", "SeneaL UI Wins Over ZenUI",
            "With ZenUI installed, its crafting panel and search, assigned gear slots, colored food bars, hidden status effects and chat case are switched off, so SeneaL UI's and this mod's own apply; its other features stay.",
            [
                new("Crafting Panel", "Enable Panel", "false"),
                new("Crafting Panel - Search", "Enable", "false"),
                new("Inventory Equip", "Assigned Gear Slots", "false"),
                new("General", "Colored Food Bars", "false"),
                new("General", "Hide Status Effects", ""),
                new("General", "Chat Force Case", "false"),
            ]),
        new(AutomaticFuelGuid, "AutomaticFuel", "SeneaL UI Wins Over AutomaticFuel",
            "With AutomaticFuel installed and SeneaL UI's Smelters automation on, AutomaticFuel is switched off, so SeneaL UI's autosmelters load stations once, in the order set in their window.",
            [
                new("General", "IsOn", "false", "Automation", "Smelters"),
            ]),
        new(ChatterGuid, "Chatter", "SeneaL UI Wins Over Chatter",
            "With Chatter installed and SeneaL UI's chat style on, Chatter is switched off, so SeneaL UI's chat window and this mod's chat filters and extras apply.",
            [
                new("_Global", "isModEnabled", "false", "HUD", "ChatStyle"),
            ]),
    ];

    private static readonly List<(ConfigFile File, ConfigEntryBase Entry, object Value)> Changed = [];

    // Some of these mods bind their settings in Start rather than Awake, so the overrides wait until every plugin has started.
    public static IEnumerator ApplyAfterStart(ConfigFile cfg, ManualLogSource log)
    {
        yield return null;
        Apply(cfg, log);
    }

    private static void Apply(ConfigFile cfg, ManualLogSource log)
    {
        Chainloader.PluginInfos.TryGetValue(Plugin.SeneaLUiGuid, out var seneaL);
        foreach (var mod in Mods)
        {
            if (!Chainloader.PluginInfos.TryGetValue(mod.Guid, out var info) || info.Instance == null)
            {
                continue;
            }
            if (!cfg.Bind("Compatibility", mod.Setting, true, mod.Description).Value)
            {
                continue;
            }
            var file = info.Instance.Config;
            var save = file.SaveOnConfigSet;
            file.SaveOnConfigSet = false;
            var count = 0;
            var hidden = 0;
            foreach (var rule in mod.Rules)
            {
                if (rule.SeneaLKey != null && !SeneaLOn(seneaL?.Instance?.Config, rule.SeneaLSection, rule.SeneaLKey))
                {
                    continue;
                }
                var definition = new ConfigDefinition(rule.Section, rule.Key);
                if (!file.ContainsKey(definition))
                {
                    log.LogWarning($"{mod.Name}: no setting [{rule.Section}] {rule.Key} in this version; that part is left as it is.");
                    continue;
                }
                var entry = file[definition];
                var value = TomlTypeConverter.ConvertToValue(rule.Value, entry.SettingType);
                if (Equals(entry.BoxedValue, value))
                {
                    continue;
                }
                Changed.Add((file, entry, entry.BoxedValue));
                Overrides[entry] = value;
                if (entry.Description?.Description?.Contains("[Synced with Server]") == true)
                {
                    Synced.Add(entry);
                }
                entry.BoxedValue = value;
                if (Hide(entry))
                {
                    hidden++;
                }
                count++;
            }
            file.SaveOnConfigSet = save;
            log.LogInfo($"{mod.Name} is installed: {count} of its settings that overlap SeneaL UI or this mod are switched off for this session, {hidden} of them hidden from settings screens.");
        }
    }

    // A forced setting is taken off every settings screen that honors ConfigurationManagerAttributes.Browsable (Configuration Manager, SeneaL UI's mod settings), so nobody changes a value that would not apply. The entry gets a description of its own, since several entries can share one description or one attributes object.
    // The other mod's own attributes objects stay: ServerSync and ConditionalConfigSync read theirs back with First() on every lock change and throw when it is gone. Ours goes last, since Configuration Manager lets a later attributes object override an earlier one and SeneaL UI hides on any Browsable false.
    private static bool Hide(ConfigEntryBase entry)
    {
        var description = entry.Description;
        var tags = (description?.Tags ?? []).Where(tag => tag is not ConfigurationManagerAttributes).Append(new ConfigurationManagerAttributes()).ToArray();
        var field = AccessTools.Field(typeof(ConfigEntryBase), "<Description>k__BackingField");
        if (field == null)
        {
            return false;
        }
        Descriptions[entry] = description;
        field.SetValue(entry, new ConfigDescription(description?.Description ?? "", description?.AcceptableValues, tags));
        return true;
    }

    private sealed class ConfigurationManagerAttributes
    {
        public bool? Browsable = false;
    }

    private static readonly Dictionary<ConfigEntryBase, ConfigDescription> Descriptions = [];
    private static readonly HashSet<ConfigEntryBase> Synced = [];
    private static readonly HashSet<ConfigEntryBase> Suspended = [];

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.OpenServer))]
    internal static class HostPatch
    {
        private static void Prefix()
        {
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                SetSynced(false);
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), "OnDestroy")]
    internal static class LeavePatch
    {
        private static void Postfix() => SetSynced(true);
    }

    // Off puts back the other mod's own value for every synced setting this plugin forced, and shows it on settings screens again; on forces them again once the hosted world is left.
    private static void SetSynced(bool forced)
    {
        foreach (var (file, entry, value) in Changed)
        {
            if (!Synced.Contains(entry) || Suspended.Contains(entry) != forced)
            {
                continue;
            }
            var save = file.SaveOnConfigSet;
            file.SaveOnConfigSet = false;
            if (forced)
            {
                Suspended.Remove(entry);
                entry.BoxedValue = Overrides[entry];
                Hide(entry);
            }
            else
            {
                Suspended.Add(entry);
                entry.BoxedValue = value;
                if (Descriptions.TryGetValue(entry, out var description))
                {
                    AccessTools.Field(typeof(ConfigEntryBase), "<Description>k__BackingField")?.SetValue(entry, description);
                }
            }
            file.SaveOnConfigSet = save;
        }
    }

    private static bool SeneaLOn(ConfigFile seneaL, string section, string key) =>
        seneaL != null && seneaL.ContainsKey(new ConfigDefinition(section, key)) && seneaL[new ConfigDefinition(section, key)].BoxedValue is true;

    [HarmonyPatch(typeof(ConfigFile), nameof(ConfigFile.Save))]
    internal static class SavePatch
    {
        private static void Prefix(ConfigFile __instance) => Swap(__instance, true);

        private static void Postfix(ConfigFile __instance) => Swap(__instance, false);
    }

    // Writes the value straight into the entry's field, so the other mod sees no setting change while its file is written.
    private static void Swap(ConfigFile file, bool original)
    {
        foreach (var (owner, entry, value) in Changed)
        {
            if (owner == file && !Suspended.Contains(entry))
            {
                AccessTools.Field(entry.GetType(), "_typedValue")?.SetValue(entry, original ? value : Overrides[entry]);
            }
        }
    }

    private static readonly Dictionary<ConfigEntryBase, object> Overrides = [];

    public static void Restore()
    {
        var files = new HashSet<ConfigFile>();
        for (var i = Changed.Count - 1; i >= 0; i--)
        {
            var (file, entry, value) = Changed[i];
            var save = file.SaveOnConfigSet;
            file.SaveOnConfigSet = false;
            entry.BoxedValue = value;
            file.SaveOnConfigSet = save;
            if (Descriptions.TryGetValue(entry, out var description))
            {
                AccessTools.Field(typeof(ConfigEntryBase), "<Description>k__BackingField")?.SetValue(entry, description);
            }
            files.Add(file);
        }
        Changed.Clear();
        Overrides.Clear();
        Descriptions.Clear();
        Synced.Clear();
        Suspended.Clear();
        foreach (var file in files)
        {
            file.Save();
        }
    }
}
