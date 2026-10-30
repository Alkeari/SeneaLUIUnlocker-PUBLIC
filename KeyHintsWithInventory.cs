using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// SeneaL UI's HUD key hints bar lists the inventory's keys while the inventory is open, beside the inventory's own hint bar that shows the same keys. A canvas group of this plugin's own on the bar's root hides it while the inventory is open; the HUD editor keeps it shown so it can still be placed.
internal static class KeyHintsWithInventory
{
    private static readonly System.Type HudRootType = TypeLookup.Find("SeneaLUI.Hud.HudRoot");
    private static readonly System.Type ViewType = TypeLookup.Find("SeneaLUI.Hud.KeyHintsView");
    private static readonly System.Type EditorType = TypeLookup.Find("SeneaLUI.Hud.HudEditor");
    private static readonly FieldInfo Hints = HudRootType == null ? null : AccessTools.Field(HudRootType, "_hints");
    private static readonly FieldInfo Root = ViewType == null ? null : AccessTools.Field(ViewType, "_root");
    private static readonly PropertyInfo EditorOpen = EditorType == null ? null : AccessTools.Property(EditorType, "Open");

    private static ConfigEntry<bool> _hide;
    private static CanvasGroup _group;

    public static bool Available => Hints != null && Root != null && EditorOpen != null;

    public static void Bind(ConfigFile cfg)
    {
        _hide = cfg.Bind("Interface", "Hide HUD Key Hints With Inventory Open", true,
            "Hide SeneaL UI's key hints bar on the HUD while the inventory is open, since the inventory's own hint bar shows the same keys.");
    }

    public static void Tick()
    {
        if (_hide == null)
        {
            return;
        }
        if (_group == null)
        {
            if (Hints.GetValue(null) is not { } view || Root.GetValue(view) is not RectTransform root || root == null)
            {
                return;
            }
            _group = root.GetComponent<CanvasGroup>();
            if (_group == null)
            {
                _group = root.gameObject.AddComponent<CanvasGroup>();
            }
        }
        var hidden = _hide.Value && InventoryGui.IsVisible() && !(bool)EditorOpen.GetValue(null);
        var alpha = hidden ? 0f : 1f;
        if (_group.alpha != alpha)
        {
            _group.alpha = alpha;
        }
    }
}
