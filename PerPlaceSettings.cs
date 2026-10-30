using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// Applies a saved SeneaL UI interface preset chosen for the server or single player world being entered, through SeneaL UI's own preset loader.
internal sealed class InterfacePerPlace
{
    private static readonly Type Profiles = TypeLookup.Find("SeneaLUI.Core.Profiles");
    private static readonly Type Prefs = TypeLookup.Find("SeneaLUI.Core.Prefs");
    private static readonly MethodInfo Load = Profiles == null ? null : AccessTools.Method(Profiles, nameof(SeneaLUI.Core.Profiles.Load));
    private static readonly MethodInfo Apply = Profiles == null ? null : AccessTools.Method(Profiles, nameof(SeneaLUI.Core.Profiles.Apply));
    private static readonly FieldInfo Active = Prefs == null ? null : AccessTools.Field(Prefs, nameof(SeneaLUI.Core.Prefs.ActiveProfile));
    private static readonly MethodInfo ApplyPreset = Profiles == null ? null : AccessTools.Method(Profiles, nameof(SeneaLUI.Core.Profiles.ApplyPreset));
    private static readonly Type ProfileMenu = TypeLookup.Find("SeneaLUI.Hud.ProfileMenu");
    private static readonly MethodInfo PresetName = ProfileMenu == null ? null : AccessTools.Method(ProfileMenu, "PresetName", [typeof(string)]);
    private const string BuiltinPrefix = "builtin:";

    private readonly ConfigEntry<string> _servers;
    private readonly ConfigEntry<string> _worlds;
    private readonly ConfigEntry<bool> _remember;
    private readonly ManualLogSource _log;
    private Player _appliedFor;

    public InterfacePerPlace(ConfigFile cfg, ManualLogSource log)
    {
        _log = log;
        const string Section = "Interface";
        _servers = cfg.Bind(Section, "Server Interface Presets", "",
            "SeneaL UI interface presets to apply when joining a server: SeneaL UI's packaged presets or your own saved ones. The server is its address (host:port) or join code, as in Listed Servers.");
        _worlds = cfg.Bind(Section, "World Interface Presets", "",
            "SeneaL UI interface presets to apply when playing a world yourself (single player or hosting): SeneaL UI's packaged presets or your own saved ones.");
        _remember = cfg.Bind(Section, "Remember Preset For This Place", false,
            "Turn on in a world to save SeneaL UI's current interface preset for this server or world; it switches itself back off.");
        _remember.SettingChanged += (_, _) => Remember();
    }

    public static bool Available => Load != null && Apply != null && Active != null;

    public void Tick()
    {
        var player = Player.m_localPlayer;
        if (player == null || player == _appliedFor || !Available || !ServerRules.Allows("PlacePresets"))
        {
            return;
        }
        _appliedFor = player;
        var (map, key) = Place();
        if (key.Length == 0 || !Parse(map.Value).TryGetValue(key, out var preset))
        {
            return;
        }
        // One of SeneaL UI's packaged presets, chosen from the settings page.
        if (preset.StartsWith(BuiltinPrefix, StringComparison.OrdinalIgnoreCase) && ApplyPreset != null)
        {
            var id = preset.Substring(BuiltinPrefix.Length);
            ApplyPreset.Invoke(null, [id]);
            // As SeneaL UI's Interfaces menu does after applying one of its presets, so the menu shows which preset is in use.
            ((ConfigEntry<string>)Active.GetValue(null)).Value = PresetName?.Invoke(null, [id]) as string ?? id;
            _log.LogInfo($"Applied SeneaL UI's {preset.Substring(BuiltinPrefix.Length)} preset for {key}.");
            return;
        }
        object[] args = [preset, null];
        if (!(bool)Load.Invoke(null, args))
        {
            _log.LogWarning($"The interface preset {preset} chosen for {key} was not found.");
            return;
        }
        Apply.Invoke(null, [args[1], true]);
        ((ConfigEntry<string>)Active.GetValue(null)).Value = preset;
        _log.LogInfo($"Applied the interface preset {preset} for {key}.");
    }

    private void Remember()
    {
        if (!_remember.Value)
        {
            return;
        }
        _remember.Value = false;
        var (map, key) = Place();
        var preset = Active == null ? null : ((ConfigEntry<string>)Active.GetValue(null)).Value;
        if (key.Length == 0 || string.IsNullOrEmpty(preset))
        {
            return;
        }
        var all = Parse(map.Value);
        all[key] = preset;
        map.Value = string.Join("; ", all.Select(pair => $"{pair.Key} = {pair.Value}"));
    }

    private (ConfigEntry<string> Map, string Key) Place()
    {
        var net = ZNet.instance;
        if (net == null)
        {
            return (_servers, "");
        }
        return net.IsServer() ? (_worlds, net.GetWorldName() ?? "") : (_servers, ServerDirectory.CurrentKey);
    }

    private static Dictionary<string, string> Parse(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(';'))
        {
            var eq = part.LastIndexOf('=');
            if (eq > 0)
            {
                map[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
            }
        }
        return map;
    }
}

// SeneaL UI keeps one list of pinned recipes for every character; this keeps one per character in the character's own save and hands it to SeneaL UI when that character plays.
internal sealed class RecipePinsPerCharacter
{
    private const string DataKey = "alkeari.senealuiunlocker.pins";

    private static readonly Type SeneaLPlugin = TypeLookup.Find("SeneaLUI.Plugin");
    // Looked up once: SeneaL UI 1.1.7 has no CraftPins, and a lookup per frame logged a Harmony warning per frame.
    private static readonly PropertyInfo CraftPins = SeneaLPlugin == null ? null : AccessTools.Property(SeneaLPlugin, nameof(SeneaLUI.Plugin.CraftPins));

    private readonly ConfigEntry<bool> _enabled;
    private ConfigEntry<string> _pins;
    private Player _for;

    public static bool Available => CraftPins != null;

    public RecipePinsPerCharacter(ConfigFile cfg)
    {
        _enabled = cfg.Bind("Interface", "Recipe Pins Per Character", true,
            "Keep a separate list of pinned recipes for each character instead of SeneaL UI's one list for all.");
    }

    public void Tick()
    {
        if (!_enabled.Value)
        {
            return;
        }
        if (_pins == null)
        {
            _pins = CraftPins?.GetValue(null, null) as ConfigEntry<string>;
            if (_pins == null)
            {
                return;
            }
            _pins.SettingChanged += (_, _) => Store();
        }
        var player = Player.m_localPlayer;
        if (player == null || player == _for)
        {
            return;
        }
        _for = player;
        if (player.m_customData.TryGetValue(DataKey, out var saved))
        {
            _pins.Value = saved;
        }
        else
        {
            Store();
        }
    }

    private void Store()
    {
        var player = Player.m_localPlayer;
        if (_enabled.Value && player != null && player == _for)
        {
            player.m_customData[DataKey] = _pins.Value;
        }
    }
}
