using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// SeneaL UI's multi-select starts on a Ctrl + left click, with Ctrl hard-coded, which takes Ctrl from the game's own click modifiers. Here it starts from Multi-Select Key instead, the middle mouse button by default: a press over a slot selects or unselects it, and holding it while moving selects every slot the rectangle covers.
// Selection.TestCtrl is held false so a Ctrl click stays the game's (or Input Tweaks') own, Selection.PressAt starts the selection at the slot under the cursor, and Selection.TestHeld stands in for the held left mouse button SeneaL UI's selection follows until the key is released.
internal static class MultiSelectKey
{
    private static readonly System.Type SelectionType = TypeLookup.Find("SeneaLUI.Inv.Selection");
    private static readonly System.Type GridSkinType = TypeLookup.Find("SeneaLUI.Inv.GridSkin");
    private static readonly System.Type CellType = GridSkinType == null ? null : AccessTools.Inner(GridSkinType, "Cellv");
    private static readonly FieldInfo TestCtrl = Field(SelectionType, "TestCtrl");
    private static readonly FieldInfo TestHeld = Field(SelectionType, "TestHeld");
    private static readonly FieldInfo Painting = Field(SelectionType, "_painting");
    private static readonly FieldInfo HoverCell = Field(GridSkinType, "HoverCell");
    private static readonly FieldInfo HoverInv = Field(GridSkinType, "HoverInv");
    private static readonly FieldInfo CellX = Field(CellType, "X");
    private static readonly FieldInfo CellY = Field(CellType, "Y");
    private static readonly MethodInfo PressAt = SelectionType == null ? null : AccessTools.Method(SelectionType, "PressAt", [typeof(Inventory), typeof(ItemDrop.ItemData), typeof(Vector2i)]);
    private static readonly PropertyInfo ShiftSelect = AccessTools.Property(TypeLookup.Find("SeneaLUI.Plugin"), "ShiftSelect");

    private static ConfigEntry<KeyboardShortcut> _key;
    private static ConfigEntry<bool> _turnOn;
    private static ManualLogSource _log;
    private static bool _driving;

    public static bool Available =>
        TestCtrl != null && TestHeld != null && Painting != null && HoverCell != null && HoverInv != null && CellX != null && CellY != null && PressAt != null;

    public static void Bind(ConfigFile cfg, ManualLogSource log)
    {
        _log = log;
        _key = cfg.Bind("Hotkeys", "Multi-Select Key", new KeyboardShortcut(KeyCode.Mouse2),
            "Pressed over an inventory or chest slot to select or unselect it with SeneaL UI's multi-select; held while moving the cursor to select every slot it passes over. Ctrl + left click no longer selects, so Ctrl stays the game's click modifier. Uses SeneaL UI's own \"Select several items\" setting ([Inventory] ShiftSelect, off in a new SeneaL UI install), which the setting \"Multi-Select Key turns on Select several items\" switches on at the first press.");
        _turnOn = cfg.Bind("Hotkeys", "Multi-Select Key Turns On Select Several Items", true,
            "The first press of the Multi-Select Key over a slot turns on SeneaL UI's own \"Select several items\" setting when it is off, so the key works without finding that setting first.");
    }

    public static void Tick()
    {
        if (_key == null)
        {
            return;
        }
        TestCtrl.SetValue(null, false);
        if (_driving)
        {
            if ((bool)Painting.GetValue(null))
            {
                TestHeld.SetValue(null, KeyInput.Held(_key.Value));
                return;
            }
            TestHeld.SetValue(null, null);
            _driving = false;
        }
        if (!InventoryGui.IsVisible() || !ServerRules.Allows("MultiSelect") || !KeyInput.Down(_key.Value) || HoverCell.GetValue(null) is not { } cell || HoverInv.GetValue(null) is not Inventory inv)
        {
            return;
        }
        TurnOnSelectSeveral();
        var pos = new Vector2i((int)CellX.GetValue(cell), (int)CellY.GetValue(cell));
        PressAt.Invoke(null, [inv, inv.GetItemAt(pos.x, pos.y), pos]);
        TestHeld.SetValue(null, true);
        _driving = true;
    }

    private static void TurnOnSelectSeveral()
    {
        if (!_turnOn.Value || ShiftSelect?.GetValue(null) is not ConfigEntry<bool> { Value: false } entry)
        {
            return;
        }
        entry.Value = true;
        _log.LogInfo("Turned on SeneaL UI's \"Select several items\" setting for the Multi-Select Key.");
    }

    private static FieldInfo Field(System.Type type, string name) => type == null ? null : AccessTools.Field(type, name);
}
