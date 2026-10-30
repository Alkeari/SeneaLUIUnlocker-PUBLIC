using UnityEngine;

namespace SeneaLUIUnlocker;

// SeneaL UI 1.2.0-preview.2 fades every HUD element its open windows cover while the inventory is open (ExtRig with TabCover), which would hide this plugin's own inventory windows (Cosmetics, Food) exactly when they are meant to show. Right before drawing, such a window's alpha is put back. A window the player hid in the HUD editor is left hidden: SeneaL UI also stops its clicks then, which a cover fade does not.
internal static class WindowCover
{
    public static void Keep(RectTransform root)
    {
        if (root == null || !root.gameObject.activeInHierarchy)
        {
            return;
        }
        var group = root.GetComponent<CanvasGroup>();
        if (group != null && group.blocksRaycasts && group.alpha < 1f)
        {
            group.alpha = 1f;
        }
    }
}
