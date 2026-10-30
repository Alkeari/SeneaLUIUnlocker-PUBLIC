using System;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// DadsSkills puts every skill back up to its last five-level checkpoint (up to 75) after the game's death loss, so the death preview's vanilla loss overstated what dying would cost. Its own GetProtectedFloor gives each skill's floor, so the preview follows its rule exactly.
internal static class DadsSkillsCompat
{
    internal const string DadsSkillsGuid = "com.dadisbored.dadsskills";

    private static ConfigEntry<bool> _enabled;
    private static Func<float, float> _floor;

    public static void Resolve(ConfigFile cfg, ManualLogSource log)
    {
        if (!Chainloader.PluginInfos.ContainsKey(DadsSkillsGuid))
        {
            return;
        }
        var floor = AccessTools.Method(TypeLookup.Find("DadsSkills.SkillDeathCheckpointPatch"), "GetProtectedFloor", [typeof(float)]);
        if (floor == null || floor.ReturnType != typeof(float))
        {
            log.LogWarning("DadsSkills has an unknown shape; Death Preview Counts DadsSkills is off.");
            return;
        }
        _floor = (Func<float, float>)Delegate.CreateDelegate(typeof(Func<float, float>), floor);
        _enabled = cfg.Bind("Compatibility", "Death Preview Counts DadsSkills", true,
            "With DadsSkills, the death preview on the Esc menu shows each skill stopping at the checkpoint DadsSkills keeps, instead of the game's full loss.");
    }

    public static float After(float level, float vanillaAfter) =>
        _floor == null || !_enabled.Value ? vanillaAfter : Math.Max(vanillaAfter, _floor(level));
}
