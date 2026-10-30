using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Name Chest Key, pressed while looking at a chest, asks for a name ("Ores", "Food"), kept on the chest itself in the world's save, the way a sign keeps its text: its hover text and the title of its window show the name, for every player with this mod. Players without it see the chest as before. An empty name removes it.
internal static class ChestNames
{
    private const string Key = "alkeari_chest_name";

    private static ConfigEntry<bool> _on;
    private static ConfigEntry<KeyboardShortcut> _key;

    public static void Bind(ConfigFile cfg)
    {
        _on = cfg.Bind("Inventory", "Chest Names", true,
            "Name Chest Key names the chest you look at; the name shows on hover and in its window's title, for players with this mod.");
        _key = cfg.Bind("Inventory", "Name Chest Key", new KeyboardShortcut(KeyCode.L),
            "Asks for a name for the chest you look at, while Chest Names is on. An empty name removes it.");
    }

    internal static string NameOf(Container chest) =>
        chest == null || chest.m_nview == null || !chest.m_nview.IsValid() ? "" : VehicleNames.Handles(chest) ? VehicleNames.NameOf(chest) : chest.m_nview.GetZDO().GetString(Key);

    private static bool On => _on != null && _on.Value && ServerRules.Allows("ChestNames");

    public static void Tick()
    {
        var player = Player.m_localPlayer;
        if (!On || player == null || TextInput.IsVisible() || InventoryFeatures.Typing() || !KeyInput.Down(_key.Value))
        {
            return;
        }
        var hover = player.GetHoverObject();
        var chest = hover == null ? null : hover.GetComponentInParent<Container>();
        if (chest == null || chest.GetComponent<TombStone>() != null || chest.m_nview == null || !chest.m_nview.IsValid() || VehicleNames.Handles(chest))
        {
            return;
        }
        if (!chest.CheckAccess(player.GetPlayerID()) || chest.m_checkGuardStone && !PrivateArea.CheckAccess(chest.transform.position))
        {
            return;
        }
        TextInput.instance.RequestText(new Receiver(chest), "Chest name", 32);
    }

    private sealed class Receiver(Container chest) : TextReceiver
    {
        public string GetText() => NameOf(chest);

        public void SetText(string text)
        {
            if (chest == null || chest.m_nview == null || !chest.m_nview.IsValid())
            {
                return;
            }
            chest.m_nview.ClaimOwnership();
            chest.m_nview.GetZDO().Set(Key, (text ?? "").Trim());
            if (DebugLog.On)
            {
                DebugLog.Write("chests", $"named {chest.name} at {chest.transform.position} \"{text}\"");
            }
        }
    }

    [HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
    internal static class HoverPatch
    {
        private static void Postfix(Container __instance, ref string __result)
        {
            var name = On && !VehicleNames.Handles(__instance) ? NameOf(__instance) : "";
            if (name.Length > 0)
            {
                __result = $"<b>{name}</b>\n{__result}";
            }
        }
    }

    // The window takes its title from the inventory's name, which the game never saves, so the chest's own name is set there just before it opens.
    [HarmonyPatch(typeof(Container), nameof(Container.Interact))]
    internal static class OpenPatch
    {
        private static void Prefix(Container __instance)
        {
            var name = On && !VehicleNames.Handles(__instance) ? NameOf(__instance) : "";
            if (name.Length > 0 && __instance.GetInventory() != null)
            {
                __instance.GetInventory().m_name = name;
            }
        }
    }
}
