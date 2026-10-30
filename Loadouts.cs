using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Named gear sets kept in the character's own save. Equipping goes through the game's own equip toggle, so armor still takes its equip time and nothing is taken from outside the player's inventory.
internal sealed class Loadouts
{
    private const string Section = "Loadouts";
    private const string DataKey = "alkeari.senealuiunlocker.loadout";
    private const int Count = 4;

    private static Loadouts _instance;

    private readonly ConfigEntry<bool> _enabled;
    private readonly ConfigEntry<KeyboardShortcut>[] _equipKeys = new ConfigEntry<KeyboardShortcut>[Count];
    private readonly ConfigEntry<KeyboardShortcut>[] _saveKeys = new ConfigEntry<KeyboardShortcut>[Count];

    public Loadouts(ConfigFile cfg)
    {
        _instance = this;
        _enabled = cfg.Bind(Section, "Equipment Loadouts", true,
            "Save what you wear and hold as one of four loadouts per character and equip it again with one key, from your own inventory only.");
        for (var i = 0; i < Count; i++)
        {
            var number = KeyCode.Alpha1 + i;
            _equipKeys[i] = cfg.Bind(Section, $"Equip Loadout {i + 1} Key", new KeyboardShortcut(number, KeyCode.LeftControl),
                $"Equips loadout {i + 1}: puts on its gear from your inventory and takes off what it does not hold. Empty = no key.");
            _saveKeys[i] = cfg.Bind(Section, $"Save Loadout {i + 1} Key", new KeyboardShortcut(number, KeyCode.LeftControl, KeyCode.LeftShift),
                $"Saves what you wear and hold now as loadout {i + 1}. Empty = no key.");
        }
    }

    public void Tick()
    {
        var player = Player.m_localPlayer;
        if (!_enabled.Value || !ServerRules.Allows("Loadouts") || player == null || player.IsDead() || Typing())
        {
            return;
        }
        for (var i = 0; i < Count; i++)
        {
            if (KeyInput.Pressed(_saveKeys[i].Value))
            {
                Save(player, i + 1);
                return;
            }
            if (KeyInput.Pressed(_equipKeys[i].Value))
            {
                Equip(player, i + 1);
                return;
            }
        }
    }

    private bool KeyHeld(KeyCode main) =>
        _enabled.Value && ServerRules.Allows("Loadouts") && _equipKeys.Concat(_saveKeys).Any(key => key.Value.MainKey == main && KeyInput.Held(key.Value));

    private static void Save(Player player, int n)
    {
        var worn = player.GetInventory().GetEquippedItems();
        if (worn.Count == 0)
        {
            player.m_customData.Remove(DataKey + n);
            player.Message(MessageHud.MessageType.TopLeft, Lang.F("Loadouts.LoadoutCleared", "Loadout {0} cleared", n));
            return;
        }
        player.m_customData[DataKey + n] = string.Join(";", worn.Select(Identity));
        player.Message(MessageHud.MessageType.TopLeft, Lang.F("Loadouts.LoadoutSaved", "Loadout {0} saved", n));
    }

    private static void Equip(Player player, int n)
    {
        if (!player.m_customData.TryGetValue(DataKey + n, out var stored) || stored.Length == 0)
        {
            player.Message(MessageHud.MessageType.TopLeft, Lang.F("Loadouts.LoadoutIsEmpty", "Loadout {0} is empty", n));
            return;
        }
        var inv = player.GetInventory();
        var wanted = stored.Split(';').Where(part => part.Length > 0).ToList();
        var chosen = new List<ItemDrop.ItemData>();
        var missing = 0;
        foreach (var id in wanted)
        {
            var item = inv.GetAllItems()
                .Where(candidate => !chosen.Contains(candidate) && Identity(candidate) == id && candidate.IsEquipable())
                .OrderByDescending(candidate => candidate.m_equipped)
                .ThenByDescending(candidate => candidate.m_durability)
                .FirstOrDefault();
            if (item == null)
            {
                missing++;
                continue;
            }
            chosen.Add(item);
        }
        var types = chosen.Select(item => item.m_shared.m_itemType).ToHashSet();
        foreach (var item in inv.GetEquippedItems().Where(item => !chosen.Contains(item) && !types.Contains(item.m_shared.m_itemType)).ToList())
        {
            Toggle(player, item);
        }
        foreach (var item in chosen.Where(item => !player.IsItemEquiped(item)))
        {
            Toggle(player, item);
        }
        player.Message(MessageHud.MessageType.TopLeft, missing == 0 ? Lang.F("Loadouts.LoadoutEquipped", "Loadout {0} equipped", n) : Lang.F("Loadouts.LoadoutEquippedMissing", "Loadout {0} equipped, {1} not in your inventory", n, missing));
    }

    private static void Toggle(Player player, ItemDrop.ItemData item)
    {
        if (!player.IsEquipActionQueued(item))
        {
            player.ToggleEquipped(item);
        }
    }

    private static string Identity(ItemDrop.ItemData item) =>
        $"{item.m_shared.m_name}|{item.m_quality}|{item.m_variant}|{item.m_worldLevel}";

    public static bool Typing()
    {
        if (Chat.instance != null && Chat.instance.HasFocus() || global::Console.IsVisible() || TextInput.IsVisible())
        {
            return true;
        }
        var selected = UnityEngine.EventSystems.EventSystem.current == null ? null : UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject;
        return selected != null && (selected.GetComponent("TMP_InputField") != null || selected.GetComponent("InputField") != null);
    }

    // The game's hotbar keys fire whatever modifier is held, so a loadout key on a number would also use that hotbar slot.
    [HarmonyPatch(typeof(Player), nameof(Player.UseHotbarItem))]
    internal static class HotbarPatch
    {
        private static bool Prefix(Player __instance, int index) =>
            __instance != Player.m_localPlayer || _instance == null || !_instance.KeyHeld(KeyCode.Alpha0 + index);
    }
}
