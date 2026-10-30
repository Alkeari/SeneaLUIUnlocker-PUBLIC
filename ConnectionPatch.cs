using HarmonyLib;

namespace SeneaLUIUnlocker;

// Patches the game's method rather than SeneaL UI's, because a SeneaL UI server lists every foreign patch on SeneaL UI's own code and may refuse the player for it.
[HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
[HarmonyAfter(Plugin.SeneaLUiGuid)]
internal static class ConnectionPatch
{
    private static void Postfix(ZNet __instance)
    {
        if (!__instance.IsServer())
        {
            Plugin.Unlock.Asked();
        }
    }
}
