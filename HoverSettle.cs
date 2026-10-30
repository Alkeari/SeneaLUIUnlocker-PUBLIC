using HarmonyLib;
using SeneaLUI.Hud;

namespace SeneaLUIUnlocker;

// SeneaL UI measures its hover text card's frame by scanning what the card draws at most every half second (ElemRig.AutoFrame, _autoAt), and places the card from that measure. A new hover text can therefore be placed for up to half a second from the frame of the text before it, which draws the card off its place (down and to the right of the crosshair) until the next scan moves it where the HUD editor put it. When the game's hover text changes, this clears that half-second wait right before SeneaL UI's own HUD update, which runs as a postfix of the game's Hud.Update, so the card is measured and placed for its new text in the same frame. Only the game's method is patched; the wait is SeneaL UI's own field.
internal static class HoverSettle
{
    private static string _last;

    public static void Patch(Harmony harmony)
    {
        _ = HudRoot.RigHover;
        harmony.Patch(AccessTools.Method(typeof(Hud), nameof(Hud.Update)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HoverSettle), nameof(BeforeSeneaL))) { priority = Priority.First, before = ["seneaL.valheim.ui"] });
    }

    private static void BeforeSeneaL(Hud __instance)
    {
        var text = __instance.m_hoverName == null ? null : __instance.m_hoverName.text;
        if (text == _last)
        {
            return;
        }
        _last = text;
        var rig = HudRoot.RigHover;
        if (rig != null)
        {
            rig._autoAt = 0f;
        }
    }
}
