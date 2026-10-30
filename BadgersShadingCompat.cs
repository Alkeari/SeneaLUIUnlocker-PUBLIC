using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// Badgers Shading Overhaul opens its menu on its Mod Toggle Key (Page Down by default) from its own Update whenever a character exists, so with a chest open, Next Chest Key also opened its shading menu over the chest. Its Update is skipped only on a frame where a chest key of this plugin is pressed with a chest open.
internal static class BadgersShadingCompat
{
    internal const string BadgersShadingGuid = "Badgers.ShadingOverhaul";

    private static ConfigEntry<bool> _enabled;

    public static void Resolve(ConfigFile cfg, Harmony harmony, ManualLogSource log)
    {
        if (!Chainloader.PluginInfos.TryGetValue(BadgersShadingGuid, out var info) || info.Instance == null)
        {
            return;
        }
        var update = AccessTools.Method(info.Instance.GetType(), "Update");
        if (update == null)
        {
            log.LogWarning("Badgers Shading Overhaul has an unknown shape; Chest Keys Skip Badgers Shading Menu is off.");
            return;
        }
        _enabled = cfg.Bind("Compatibility", "Chest Keys Skip Badgers Shading Menu", true,
            "With Badgers Shading Overhaul, Next Chest Key and Previous Chest Key with a chest open do not also open its shading menu when it uses the same key.");
        harmony.Patch(update, prefix: new HarmonyMethod(AccessTools.Method(typeof(BadgersShadingCompat), nameof(BeforeUpdate))));
    }

    private static bool BeforeUpdate() => !_enabled.Value || !CycleChests.ChestKeyDown;
}
