using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// In the build menu the game's Q and E keys (TabLeft, TabRight) step through the sorting tabs (by usage, by material, recent, favorites), which SeneaL UI shows as the four icons above the pieces. With Q And E Change Section on they step through the sections beside the pieces (SeneaL UI's category chips) instead, the same step the gamepad triggers make, and the tabs stay on the mouse and the gamepad bumpers. The game's tab handler reads the keys right after, so they are cleared once used. SeneaL UI's menu is read by name; a build without it leaves the keys as the game has them.
internal static class BuildSections
{
    private static readonly System.Type MenuType = TypeLookup.Find("SeneaLUI.Hud.BuildMenuView");
    private static readonly FieldInfo InstanceField = MenuType == null ? null : AccessTools.Field(MenuType, "_instance");
    private static readonly FieldInfo ShownField = MenuType == null ? null : AccessTools.Field(MenuType, "_shown");
    private static readonly FieldInfo TypingField = MenuType == null ? null : AccessTools.Field(MenuType, "Typing");
    private static readonly MethodInfo StepMethod = MenuType == null ? null : AccessTools.Method(MenuType, "PadChip", [typeof(int)]);

    private static ConfigEntry<bool> _on;

    public static bool Available => InstanceField != null && ShownField != null && StepMethod != null;

    public static void Bind(ConfigFile cfg)
    {
        _on = cfg.Bind("Building", "Q And E Change Section", false,
            "In the build menu, Q and E step through the sections beside the pieces (the category chips) instead of the four sorting tabs. Off, they change the tab as the game does.");
    }

    [HarmonyPatch(typeof(TabHandler), "Update")]
    internal static class TabPatch
    {
        private static void Prefix(TabHandler __instance)
        {
            if (_on == null || !_on.Value || !Available)
            {
                return;
            }
            var left = ZInput.GetButtonDown("TabLeft");
            var right = ZInput.GetButtonDown("TabRight");
            if (!left && !right)
            {
                return;
            }
            if (__instance.GetComponentInParent<BuildUi>() == null || InventoryFeatures.Typing() || TypingField?.GetValue(null) is true)
            {
                return;
            }
            var menu = InstanceField.GetValue(null);
            if (menu == null || ShownField.GetValue(menu) is not true)
            {
                return;
            }
            StepMethod.Invoke(menu, [left ? -1 : 1]);
            ZInput.ResetButtonStatus(left ? "TabLeft" : "TabRight");
        }
    }
}
