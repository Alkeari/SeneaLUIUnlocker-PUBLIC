using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Does for Input Tweaks 1.1.6 what SeneaL UI's own Input Tweaks support does for the version it was written against: SeneaL UI looks for CollectInventoryButtons(InventoryGrid, List<InventoryButton>), which 1.1.6 declares as CollectInventoryButtons(InventoryGrid, InventoryGrid), so its support never switches on. Input Tweaks' cached cell lists are cleaned after Input Tweaks fills them and dropped when SeneaL UI rebuilds a grid's cells, through the game's own methods only.
internal static class InputTweaksCompat
{
    public const string InputTweaksGuid = "com.riintouge.inputtweaks";
    private const string Section = "Compatibility";

    private static readonly Dictionary<int, int> Counts = new(4);

    private static ConfigEntry<bool> _cells;
    private static ConfigEntry<bool> _wheel;
    private static FieldInfo _players;
    private static FieldInfo _containers;
    private static FieldInfo _handler;
    private static FieldInfo _mouseContext;
    private static PropertyInfo _enabledValue;
    private static object _enabledEntry;
    private static FieldInfo _seneaLOwnSupport;
    private static FieldInfo _splitPanel;

    public static bool Resolve(ConfigFile cfg, BepInEx.Logging.ManualLogSource log)
    {
        if (!Chainloader.PluginInfos.ContainsKey(InputTweaksGuid))
        {
            return false;
        }
        var patch = TypeLookup.Find("InputTweaks.InputTweaks+InventoryGuiPatch");
        var button = TypeLookup.Find("InputTweaks.InventoryButton");
        var main = TypeLookup.Find("InputTweaks.InputTweaks");
        _players = patch == null ? null : AccessTools.Field(patch, "PlayerButtons");
        _containers = patch == null ? null : AccessTools.Field(patch, "ContainerButtons");
        _mouseContext = patch == null ? null : AccessTools.Field(patch, "MouseContext");
        _handler = button == null ? null : AccessTools.Field(button, "inputHandler");
        _enabledEntry = main == null ? null : AccessTools.Field(main, "IsEnabled")?.GetValue(null);
        _enabledValue = _enabledEntry == null ? null : AccessTools.Property(_enabledEntry.GetType(), "Value");
        if (_players == null || _containers == null || _handler == null || _mouseContext == null || _enabledValue == null)
        {
            log.LogWarning("Input Tweaks has an unknown shape; the Input Tweaks compatibility fixes are off.");
            return false;
        }
        var seneaL = TypeLookup.Find("SeneaLUI.Mods.InputTweaks");
        _seneaLOwnSupport = seneaL == null ? null : AccessTools.Field(seneaL, nameof(SeneaLUI.Mods.InputTweaks.On));
        _splitPanel = AccessTools.Field(typeof(SplitDialog), "m_panel");
        _cells = cfg.Bind(Section, "Input Tweaks Cell List Fix", true,
            "Keep Input Tweaks' list of inventory cells in step with SeneaL UI's inventory grid, as SeneaL UI's own Input Tweaks support does for the Input Tweaks version it recognizes.");
        _wheel = cfg.Bind(Section, "Leave Split Wheel To Input Tweaks", true,
            "While the cursor is where Input Tweaks turns the mouse wheel into split amount steps, Split with Mouse Wheel does nothing, so one wheel step changes the amount once.");
        return true;
    }

    // Input Tweaks steps the split amount by one per wheel step, from its UpdateItemDrag prefix, under exactly these conditions.
    public static bool HandlesSplitWheel()
    {
        if (_wheel == null || !_wheel.Value || !(bool)_enabledValue.GetValue(_enabledEntry, null) || _mouseContext.GetValue(null) != null)
        {
            return false;
        }
        if (ListCount(_players) == 0 && ListCount(_containers) == 0)
        {
            return false;
        }
        if (ZInput.GetMouseButton(0) || ZInput.GetMouseButton(1))
        {
            return false;
        }
        var gui = InventoryGui.instance;
        if (gui == null || gui.m_splitDialog == null || !gui.m_splitDialog.gameObject.activeInHierarchy)
        {
            return false;
        }
        var panel = _splitPanel == null ? null : _splitPanel.GetValue(gui.m_splitDialog) as RectTransform;
        return panel != null && RectTransformUtility.RectangleContainsScreenPoint(panel, ZInput.pointerPosition);
    }

    private static bool Active() =>
        _cells != null && _cells.Value && !(_seneaLOwnSupport != null && (bool)_seneaLOwnSupport.GetValue(null));

    private static int ListCount(FieldInfo field) => field.GetValue(null) is IList list ? list.Count : 0;

    private static void Clean(FieldInfo field)
    {
        if (field.GetValue(null) is not IList list)
        {
            return;
        }
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var entry = list[i];
            var handler = entry == null ? null : _handler.GetValue(entry) as UnityEngine.Object;
            if (handler == null)
            {
                list.RemoveAt(i);
            }
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "Update")]
    internal static class GuiUpdatePatch
    {
        [HarmonyAfter(InputTweaksGuid)]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            if (!Active())
            {
                return;
            }
            Clean(_players);
            Clean(_containers);
        }
    }

    [HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.UpdateGui))]
    internal static class GridUpdatePatch
    {
        private static void Postfix(InventoryGrid __instance)
        {
            if (!Active())
            {
                return;
            }
            var count = __instance.m_elements == null ? 0 : __instance.m_elements.Count;
            var id = __instance.GetInstanceID();
            if (Counts.TryGetValue(id, out var last) && last == count)
            {
                return;
            }
            Counts[id] = count;
            var gui = InventoryGui.instance;
            if (gui == null)
            {
                return;
            }
            var field = __instance == gui.m_playerGrid ? _players : __instance == gui.m_containerGrid ? _containers : null;
            if (field != null && field.GetValue(null) is IList list)
            {
                list.Clear();
            }
        }
    }
}
