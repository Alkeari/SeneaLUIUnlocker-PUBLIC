using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// SeneaL UI 1.1.8 and later craft a chosen amount with their own stepper, which writes InventoryGui.m_multiCraftAmount. MultiCraft overwrites that field at the start of every UpdateRecipe, the call in which the game finishes a craft, so with MultiCraft installed a batch from SeneaL UI's stepper crafts one item. The amount is kept across MultiCraft's prefix while SeneaL UI's crafting window is in use.
// MultiCraft's Harmony instance has a random id, so the order around its prefix is set by priority alone. Only the game's own method is patched.
internal static class MultiCraftCompat
{
    public const string MultiCraftGuid = "maximods.valheim.multicraft";
    public const string BridgeGuid = "alkeari.valheim.multicraftforsenealui";

    private static ConfigEntry<bool> _on;
    private static FieldInfo _craftView;
    private static FieldInfo _applied;
    private static int _seneaLAmount;

    public static bool Resolve(ConfigFile cfg, ManualLogSource log)
    {
        if (!Chainloader.PluginInfos.ContainsKey(MultiCraftGuid))
        {
            return false;
        }
        _on = cfg.Bind("Compatibility", "Keep SeneaL UI Craft Amount", true,
            "With MultiCraft installed, SeneaL UI's crafting window crafts the amount its own stepper shows (SeneaL UI 1.1.8 and later), rather than the single item MultiCraft resets it to.");
        if (Chainloader.PluginInfos.ContainsKey(BridgeGuid))
        {
            log.LogInfo("MultiCraft for SeneaL UI is installed and keeps SeneaL UI's craft amount itself; Keep SeneaL UI Craft Amount stands down.");
            return false;
        }
        var view = TypeLookup.Find("SeneaLUI.Inv.CraftView");
        _craftView = AccessTools.Field(TypeLookup.Find("SeneaLUI.Patches.InventoryGuiPatches"), "Craft");
        _applied = view == null ? null : AccessTools.Field(view, "_applied");
        if (view == null || AccessTools.Field(view, "_stepOn") == null || _craftView == null || _applied == null)
        {
            return false;
        }
        return true;
    }

    private static bool SeneaLCraftingActive()
    {
        var view = _craftView.GetValue(null);
        return view != null && _applied.GetValue(view) is true;
    }

    [HarmonyPatch(typeof(InventoryGui), "UpdateRecipe")]
    internal static class SavePatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(InventoryGui __instance) => _seneaLAmount = __instance.m_multiCraftAmount;
    }

    [HarmonyPatch(typeof(InventoryGui), "UpdateRecipe")]
    internal static class RestorePatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(InventoryGui __instance)
        {
            if (_on.Value && ServerRules.Allows("MultiCraftAmount") && SeneaLCraftingActive())
            {
                __instance.m_multiCraftAmount = _seneaLAmount;
            }
        }
    }
}
