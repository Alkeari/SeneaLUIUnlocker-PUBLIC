using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Input Tweaks drops a dragged item into the chest, armor stand or item stand under the cursor by raycasting into the world from its OnDropOutside prefix, and previews it while dragging, without asking whether the cursor is over a window. SeneaL UI's own OnDropOutside prefix keeps the item when it is released over one of SeneaL UI's inventory windows, but both prefixes run, so a chest standing behind SeneaL UI's inventory, chest, crafting, tab or hint panel took the item anyway.
// Input Tweaks' world raycast finds nothing while the cursor is over one of those panels, the same panels SeneaL UI's drop check uses (InventoryGuiPatches.Over).
internal static class InputTweaksWorldGuard
{
    private static readonly string[] Panels = ["View", "Chest", "Backpack", "Craft", "Tabs", "Hints"];

    private static FieldInfo[] _views;
    private static MethodInfo _over;
    private static ConfigEntry<bool> _enabled;

    public static void Resolve(ConfigFile cfg, Harmony harmony, ManualLogSource log)
    {
        var raycast = AccessTools.Method(TypeLookup.Find("InputTweaks.InputTweaks+InventoryGuiPatch"), "RaycastComponentForWorldInteraction");
        var patches = TypeLookup.Find("SeneaLUI.Patches.InventoryGuiPatches");
        _over = patches == null ? null : AccessTools.Method(patches, "Over", [typeof(RectTransform), typeof(Vector2)]);
        _views = patches == null ? [] : Panels.Select(name => AccessTools.Field(patches, name)).Where(field => field != null).ToArray();
        if (raycast == null || _over == null || _views.Length == 0)
        {
            log.LogWarning("Input Tweaks or SeneaL UI has an unknown shape; Input Tweaks ignores world behind panels is off.");
            return;
        }
        _enabled = cfg.Bind("Compatibility", "Input Tweaks Ignores World Behind Panels", true,
            "Input Tweaks puts a dragged item into the chest, armor stand or item stand under the cursor only when the cursor is not over one of SeneaL UI's inventory windows, so releasing an item over a window never sends it to a chest behind it.");
        harmony.Patch(raycast, prefix: new HarmonyMethod(AccessTools.Method(typeof(InputTweaksWorldGuard), nameof(RaycastPrefix))));
    }

    // The hit Input Tweaks gets back stays at its default when the raycast is skipped.
    private static bool RaycastPrefix(ref Component __result)
    {
        if (!_enabled.Value || !OverSeneaLPanel())
        {
            return true;
        }
        __result = null;
        return false;
    }

    private static bool OverSeneaLPanel()
    {
        Vector2 pointer = ZInput.pointerPosition;
        foreach (var field in _views)
        {
            var view = field.GetValue(null);
            var panel = view == null ? null : AccessTools.Property(view.GetType(), "Panel")?.GetValue(view) as RectTransform;
            if (panel != null && (bool)_over.Invoke(null, [panel, pointer]))
            {
                return true;
            }
        }
        return false;
    }
}
