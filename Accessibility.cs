using BepInEx.Configuration;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// Comfort switches for players who find parts of the game's presentation hard to bear. Camera Shake off stops the camera shaking from hits, explosions and heavy footsteps. Show Smoke off hides the smoke of fires without removing it: the game's smoke still rises, gathers under roofs and smokes out whoever stands in it, so a fire indoors still needs a vent. Damage Flash off keeps the screen from flashing red when you are hurt.
internal static class Accessibility
{
    private static ConfigEntry<bool> _shake;
    private static ConfigEntry<bool> _smoke;
    private static ConfigEntry<bool> _flash;

    public static void Bind(ConfigFile cfg)
    {
        _shake = cfg.Bind("Accessibility", "Camera Shake", true,
            "The camera shakes from hits, explosions and heavy steps, as the game has it; off keeps it still.");
        _smoke = cfg.Bind("Accessibility", "Show Smoke", true,
            "Fires' smoke is drawn, as the game has it. Off hides it, while the smoke itself still gathers under roofs and still smokes you out.");
        _flash = cfg.Bind("Accessibility", "Damage Flash", true,
            "The screen flashes red when you are hurt, as the game has it; off keeps it clear.");
    }

    [HarmonyPatch(typeof(Hud), nameof(Hud.DamageFlash))]
    internal static class FlashPatch
    {
        private static bool Prefix() => _flash == null || _flash.Value;
    }

    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.AddShake))]
    internal static class ShakePatch
    {
        private static bool Prefix() => _shake == null || _shake.Value;
    }

    [HarmonyPatch(typeof(SmokeRenderer), "LateUpdate")]
    internal static class SmokePatch
    {
        private static void Postfix(SmokeRenderer __instance)
        {
            var show = _smoke == null || _smoke.Value;
            foreach (var system in __instance.m_chunkedParticleSystems.Values)
            {
                var renderer = system == null ? null : system.GetComponent<UnityEngine.ParticleSystemRenderer>();
                if (renderer != null && renderer.enabled != show)
                {
                    renderer.enabled = show;
                }
            }
        }
    }
}
