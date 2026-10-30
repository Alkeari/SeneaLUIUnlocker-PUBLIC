using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// KeyBindManager takes over every string setting whose description contains "key", which catches SeneaL UI's attachment settings ("Key of the element...") and this mod's element lists. It keeps its own copy of each in its config and writes that copy back over the original at every world load, so a layout changed since its last save snaps back.
// Its scan, a ZoneSystem.Start prefix, skips any definition already in its IgnoreConfigList, so this prefix runs first and lists every string setting of SeneaL UI and this mod that is not a key bind.
internal static class KeyBindManagerGuard
{
    internal const string KeyBindManagerGuid = "Searica.Valheim.KeyBindManager";
    private static readonly string[] Owners = [Plugin.SeneaLUiGuid, Plugin.Guid];

    private static HashSet<ConfigDefinition> _ignored;
    private static ConfigEntry<bool> _enabled;

    public static void Resolve(ConfigFile cfg, Harmony harmony, ManualLogSource log)
    {
        if (!Chainloader.PluginInfos.ContainsKey(KeyBindManagerGuid))
        {
            return;
        }
        _ignored = AccessTools.Field(TypeLookup.Find("KeyBindManager.KeyBindManager"), "IgnoreConfigList")?.GetValue(null) as HashSet<ConfigDefinition>;
        if (_ignored == null)
        {
            log.LogWarning("KeyBindManager has an unknown shape; Keep KeyBindManager Off Layout Settings is off.");
            return;
        }
        _enabled = cfg.Bind("Compatibility", "Keep KeyBindManager Off Layout Settings", true,
            "KeyBindManager manages only real key binds of SeneaL UI and this mod, so it never puts back an old HUD layout or element list when you load a world.");
        harmony.Patch(AccessTools.Method(typeof(ZoneSystem), "Start"),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(KeyBindManagerGuard), nameof(BeforeScan))) { priority = Priority.First });
    }

    private static void BeforeScan()
    {
        if (!_enabled.Value)
        {
            return;
        }
        foreach (var guid in Owners)
        {
            if (!Chainloader.PluginInfos.TryGetValue(guid, out var info) || info.Instance == null)
            {
                continue;
            }
            foreach (var entry in info.Instance.Config.Select(pair => pair.Value).Where(entry => entry.SettingType == typeof(string) && !IsKeyBind(entry)))
            {
                _ignored.Add(entry.Definition);
            }
        }
    }

    private static bool IsKeyBind(ConfigEntryBase entry)
    {
        var text = entry.Description.Description.ToLowerInvariant();
        return text.Contains("key bind") || text.Contains("shortcut");
    }
}
