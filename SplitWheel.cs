using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

// Turns the mouse wheel in SeneaL UI's split dialog into amount steps, through the dialog's own setter so its slider, text and the game's split dialog stay in step.
internal sealed class SplitWheel
{
    private static readonly Type Patches = TypeLookup.Find("SeneaLUI.Patches.InventoryGuiPatches");
    private static readonly Type View = TypeLookup.Find("SeneaLUI.Inv.SplitView");
    private static readonly FieldInfo Split = Patches == null ? null : AccessTools.Field(Patches, nameof(SeneaLUI.Patches.InventoryGuiPatches.Split));
    private static readonly FieldInfo Want = View == null ? null : AccessTools.Field(View, nameof(SeneaLUI.Inv.SplitView._want));
    private static readonly FieldInfo Max = View == null ? null : AccessTools.Field(View, nameof(SeneaLUI.Inv.SplitView._max));
    private static readonly MethodInfo Set = View == null ? null : AccessTools.Method(View, nameof(SeneaLUI.Inv.SplitView.Set), [typeof(int)]);

    private readonly ConfigEntry<bool> _enabled;
    private readonly ConfigEntry<int> _shiftStep;
    private readonly ConfigEntry<KeyboardShortcut> _bigStepKey;
    private readonly ConfigEntry<KeyboardShortcut> _tenthKey;
    private static ConfigEntry<bool> _arrowsOnce;

    public SplitWheel(ConfigFile cfg)
    {
        _enabled = cfg.Bind("Inventory", "Split With Mouse Wheel", true,
            "In SeneaL UI's split dialog, the mouse wheel changes the amount by 1, by Split Wheel Shift Step while Split Wheel Step Key is held, and by a tenth of the stack while Split Wheel Tenth Key is held.");
        _shiftStep = cfg.Bind("Inventory", "Split Wheel Shift Step", 10, new ConfigDescription("Amount one wheel step changes while Split Wheel Step Key is held.", new AcceptableValueRange<int>(2, 100)));
        _bigStepKey = cfg.Bind("Inventory", "Split Wheel Step Key", new KeyboardShortcut(KeyCode.LeftShift),
            "Held while turning the wheel in the split dialog to change the amount by Split Wheel Shift Step.");
        _tenthKey = cfg.Bind("Inventory", "Split Wheel Tenth Key", new KeyboardShortcut(KeyCode.LeftControl),
            "Held while turning the wheel in the split dialog to change the amount by a tenth of the stack.");
        _arrowsOnce = cfg.Bind("Inventory", "Split Arrow Keys Step By 1", true,
            "In SeneaL UI's split dialog, one press of an arrow key changes the amount by 1. Without it the game's own slider moves too, so one press changes it by 2, and a moved Split More Key or Split Less Key leaves the arrow keys still working.");
    }

    // The game keeps its split slider selected (its UIGroupHandler reselects it every frame), so Unity's own slider steps on every arrow press as well as SeneaL UI's key handler does.
    [HarmonyPatch(typeof(Slider), nameof(Slider.OnMove))]
    internal static class ArrowPatch
    {
        private static bool Prefix(Slider __instance)
        {
            var gui = InventoryGui.instance;
            if (_arrowsOnce == null || !_arrowsOnce.Value || gui == null || gui.m_splitDialog == null || __instance != gui.m_splitDialog.m_splitSlider || Split == null || Want == null)
            {
                return true;
            }
            var view = Split.GetValue(null);
            return view == null || !(bool)Want.GetValue(view);
        }
    }

    public void Tick()
    {
        if (!_enabled.Value || !ServerRules.Allows("SplitWheel") || Split == null || Want == null || Max == null || Set == null)
        {
            return;
        }
        var wheel = ZInput.GetMouseScrollWheel();
        var gui = InventoryGui.instance;
        if (wheel == 0f || gui == null || gui.m_splitDialog == null || !gui.m_splitDialog.IsActive)
        {
            return;
        }
        var view = Split.GetValue(null);
        if (view == null || !(bool)Want.GetValue(view))
        {
            return;
        }
        var max = (int)Max.GetValue(view);
        var step = KeyInput.Held(_tenthKey.Value) ? Mathf.Max(1, max / 10) : KeyInput.Held(_bigStepKey.Value) ? _shiftStep.Value : 1;
        var now = (int)gui.m_splitDialog.SliderValue;
        Set.Invoke(view, [Mathf.Clamp(now + (wheel > 0f ? step : -step), 1, max)]);
    }
}
