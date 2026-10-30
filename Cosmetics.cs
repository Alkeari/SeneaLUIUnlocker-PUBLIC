using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// What the character shows for each piece of armor, chosen apart from the armor actually worn, which keeps giving its stats. The game draws worn armor from Humanoid.SetupVisEquipment, which names each piece's prefab to VisEquipment; for the local player each chosen piece is named there instead.
// VisEquipment writes those names into the player's own network object, which the game already sends to everyone, so other players see the chosen look too, with or without this mod, on a vanilla server.
// The choices are kept per character in its save (m_customData). A piece can be chosen once the character knows it: crafted or picked up at least once (the game's known items, m_knownMaterial).
internal static class Cosmetics
{
    private const string DataKey = "alkeari.senealuiunlocker.cosmetics";
    public const string Worn = "";
    public const string Nothing = "-";

    public static readonly (string Slot, ItemDrop.ItemData.ItemType Type)[] Slots =
    [
        ("Helmet", ItemDrop.ItemData.ItemType.Helmet),
        ("Chest", ItemDrop.ItemData.ItemType.Chest),
        ("Legs", ItemDrop.ItemData.ItemType.Legs),
        ("Cape", ItemDrop.ItemData.ItemType.Shoulder),
        ("Belt", ItemDrop.ItemData.ItemType.Utility),
        ("Trinket", ItemDrop.ItemData.ItemType.Trinket)
    ];

    private static ConfigEntry<bool> _enabled;
    private static Player _for;
    private static Dictionary<string, string> _choices = new(StringComparer.OrdinalIgnoreCase);

    public static bool Enabled => _enabled != null && _enabled.Value && ServerRules.Allows("Cosmetics");

    public static void Bind(ConfigFile cfg)
    {
        _enabled = cfg.Bind("Cosmetics", "Cosmetic Equipment", true,
            "Show a chosen piece, from every armor piece your character has crafted or picked up, in place of the armor worn in each slot, or nothing at all; the worn armor keeps its stats. Other players see the chosen look.");
        _enabled.SettingChanged += (_, _) => Refresh();
    }

    // The choice for a slot: Worn (show the armor worn), Nothing (show no piece), or a prefab name with an optional :variant.
    public static string Choice(string slot)
    {
        Load();
        return _choices.TryGetValue(slot, out var value) ? value : Worn;
    }

    public static void Choose(string slot, string value)
    {
        var player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }
        Load();
        if (string.IsNullOrEmpty(value))
        {
            _choices.Remove(slot);
        }
        else
        {
            _choices[slot] = value;
        }
        player.m_customData[DataKey] = string.Join(";", _choices.Select(pair => $"{pair.Key}={pair.Value}"));
        Refresh();
    }

    // Every piece the character knows for a slot, as (stored value, shown name), each style of a piece with several as its own entry.
    public static List<(string Value, string Label)> Known(ItemDrop.ItemData.ItemType type)
    {
        var player = Player.m_localPlayer;
        var list = new List<(string, string)>();
        if (player == null || ObjectDB.instance == null)
        {
            return list;
        }
        foreach (var prefab in ObjectDB.instance.m_items)
        {
            var drop = prefab == null ? null : prefab.GetComponent<ItemDrop>();
            var shared = drop == null ? null : drop.m_itemData.m_shared;
            if (shared == null || shared.m_itemType != type || !player.m_knownMaterial.Contains(shared.m_name))
            {
                continue;
            }
            var name = Localization.instance.Localize(shared.m_name);
            var styles = shared.m_icons == null ? 1 : Math.Max(1, shared.m_icons.Length);
            for (var v = 0; v < styles; v++)
            {
                list.Add((v == 0 ? prefab.name : $"{prefab.name}:{v}", styles > 1 ? $"{name} (style {v + 1})" : name));
            }
        }
        // Several prefabs can carry one item (an upgraded copy, another mod's variant); the list keeps one entry per name shown, preferring the prefab a recipe crafts.
        var crafted = new HashSet<string>(ObjectDB.instance.m_recipes.Where(recipe => recipe != null && recipe.m_item != null).Select(recipe => recipe.m_item.name));
        return list.GroupBy(item => item.Item2, StringComparer.OrdinalIgnoreCase).Select(group => group.OrderByDescending(item => crafted.Contains(item.Item1.Split(':')[0])).First()).OrderBy(item => item.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Load()
    {
        var player = Player.m_localPlayer;
        if (player == null || player == _for)
        {
            return;
        }
        _for = player;
        _choices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (player.m_customData.TryGetValue(DataKey, out var saved))
        {
            foreach (var part in saved.Split(';'))
            {
                var eq = part.IndexOf('=');
                if (eq > 0)
                {
                    _choices[part.Substring(0, eq)] = part.Substring(eq + 1);
                }
            }
        }
    }

    public static void Refresh()
    {
        var player = Player.m_localPlayer;
        if (player != null && player.m_visEquipment != null)
        {
            player.SetupVisEquipment(player.m_visEquipment, false);
        }
    }

    private static (int Hash, string Name, int Variant) Piece(string value)
    {
        if (value == Nothing)
        {
            return (0, "", 0);
        }
        var at = value.LastIndexOf(':');
        var name = at > 0 ? value.Substring(0, at) : value;
        var variant = at > 0 && int.TryParse(value.Substring(at + 1), out var v) ? v : 0;
        return (name.GetStableHashCode(), name, variant);
    }

    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.SetupVisEquipment))]
    internal static class LookPatch
    {
        private static void Postfix(Humanoid __instance, VisEquipment visEq)
        {
            if (!Enabled || visEq == null || __instance != Player.m_localPlayer)
            {
                return;
            }
            Load();
            foreach (var pair in _choices)
            {
                var slot = pair.Key;
                var value = pair.Value;
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }
                var (hash, name, variant) = Piece(value);
                switch (slot)
                {
                    case "Helmet":
                        visEq.SetHelmetItem(hash);
                        break;
                    case "Chest":
                        visEq.SetChestItem(hash);
                        break;
                    case "Legs":
                        visEq.SetLegItem(hash);
                        break;
                    case "Cape":
                        visEq.SetShoulderItem(hash, variant, 1);
                        break;
                    case "Belt":
                        visEq.SetUtilityItem(hash);
                        break;
                    case "Trinket":
                        visEq.SetTrinketItem(name);
                        break;
                }
            }
        }
    }
}
