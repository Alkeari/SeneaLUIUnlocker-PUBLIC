using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// DadsFPP moves the camera to the character's eyes in a GameCamera.UpdateCamera postfix that runs even when the build camera's prefix skips the game's own update, so in first person the build camera could not leave the character. Its controller already stands down while the game's free-fly camera is on, so the build camera is passed to it as free fly.
internal static class DadsFppCompat
{
    internal const string DadsFppGuid = "com.dadisbored.dadsfpp";

    private static ConfigEntry<bool> _enabled;

    public static void Resolve(ConfigFile cfg, Harmony harmony, ManualLogSource log)
    {
        if (!Chainloader.PluginInfos.ContainsKey(DadsFppGuid))
        {
            return;
        }
        var update = AccessTools.Method(TypeLookup.Find("DadsFPP.FirstPersonCamera"), "Update", [typeof(GameCamera), typeof(UnityEngine.Camera), typeof(Player), typeof(bool)]);
        if (update == null)
        {
            log.LogWarning("DadsFPP has an unknown shape; Build Camera In First Person is off.");
            return;
        }
        _enabled = cfg.Bind("Compatibility", "Build Camera In First Person", true,
            "With DadsFPP in first person, Build Camera Key still lets the camera fly free around your build, and first person comes back when the build camera ends.");
        harmony.Patch(update, prefix: new HarmonyMethod(AccessTools.Method(typeof(DadsFppCompat), nameof(BeforeUpdate))));
    }

    private static void BeforeUpdate(ref bool freeFly)
    {
        if (_enabled.Value && BuildCamera.Active)
        {
            freeFly = true;
        }
    }
}
