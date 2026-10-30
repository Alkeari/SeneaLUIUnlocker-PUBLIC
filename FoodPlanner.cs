using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;

namespace SeneaLUIUnlocker;

internal enum FoodFocus
{
    Total,
    Health,
    Stamina,
    Eitr
}

// Which foods to eat: every food the character knows (has the recipe for, or has picked up), ranked by the chosen focus, minus the ones marked ignored. "Best you have" are the top ones at hand right now (carried, or in the chests SeneaL UI crafts from); "best you know" are the top ones overall, whose missing materials can go to the Tracker.
internal static class FoodPlanner
{
    private const string Section = "Food";

    private static ConfigEntry<bool> _enabled;
    private static ConfigEntry<FoodFocus> _focus;
    private static ConfigEntry<string> _ignored;
    private static ConfigEntry<bool> _chests;

    public static bool Enabled => _enabled != null && _enabled.Value && ServerRules.Allows("FoodPlanner");

    public static FoodFocus Focus
    {
        get => _focus.Value;
        set => _focus.Value = value;
    }

    public static void Bind(ConfigFile cfg)
    {
        _enabled = cfg.Bind(Section, "Food Planner", true,
            "Show a Food window beside the inventory with the best foods you have and the best you know, ranked by Food Focus.");
        _focus = cfg.Bind(Section, "Food Focus", FoodFocus.Total, "What the Food window ranks foods by: their health, stamina and eitr together, or one of them.");
        _ignored = cfg.Bind(Section, "Ignored Foods", "", "Foods the Food window leaves out, by item name.");
        _chests = cfg.Bind(Section, "Count Food In Nearby Chests", true, "Count food in the chests SeneaL UI crafts from as food you have.");
    }

    public sealed record Food(string Prefab, string Name, UnityEngine.Sprite Icon, float Health, float Stamina, float Eitr, int Have, bool HasRecipe);

    public static float Score(Food food) => _focus.Value switch
    {
        FoodFocus.Health => food.Health,
        FoodFocus.Stamina => food.Stamina,
        FoodFocus.Eitr => food.Eitr,
        _ => food.Health + food.Stamina + food.Eitr
    };

    // Every food the game has that the character knows, for the settings page's Ignored Foods list as well as the window.
    public static List<Food> Known()
    {
        var player = Player.m_localPlayer;
        var list = new List<Food>();
        if (player == null || ObjectDB.instance == null)
        {
            return list;
        }
        var ignored = SettingsPage.Split(_ignored.Value);
        foreach (var prefab in ObjectDB.instance.m_items)
        {
            var drop = prefab == null ? null : prefab.GetComponent<ItemDrop>();
            var shared = drop == null ? null : drop.m_itemData.m_shared;
            if (shared == null || shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable || shared.m_food + shared.m_foodStamina + shared.m_foodEitr <= 0f || ignored.Contains(prefab.name))
            {
                continue;
            }
            var recipe = player.m_knownRecipes.Contains(shared.m_name);
            if (!recipe && !player.m_knownMaterial.Contains(shared.m_name))
            {
                continue;
            }
            list.Add(new Food(prefab.name, Localization.instance.Localize(shared.m_name), drop.m_itemData.GetIcon(), shared.m_food, shared.m_foodStamina, shared.m_foodEitr,
                Stock.Have(shared.m_name, _chests.Value), recipe && ObjectDB.instance.GetRecipe(drop.m_itemData) != null));
        }
        return list.OrderByDescending(Score).ThenBy(food => food.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IEnumerable<(string Value, string Label)> All()
    {
        var player = Player.m_localPlayer;
        if (player == null || ObjectDB.instance == null)
        {
            return Array.Empty<(string, string)>();
        }
        return ObjectDB.instance.m_items
            .Select(prefab => (Prefab: prefab, Drop: prefab == null ? null : prefab.GetComponent<ItemDrop>()))
            .Where(pair => pair.Drop != null && pair.Drop.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable
                && pair.Drop.m_itemData.m_shared.m_food + pair.Drop.m_itemData.m_shared.m_foodStamina + pair.Drop.m_itemData.m_shared.m_foodEitr > 0f
                && (player.m_knownRecipes.Contains(pair.Drop.m_itemData.m_shared.m_name) || player.m_knownMaterial.Contains(pair.Drop.m_itemData.m_shared.m_name)))
            .Select(pair => (pair.Prefab.name, Localization.instance.Localize(pair.Drop.m_itemData.m_shared.m_name)));
    }

    // The foods currently eaten, by item name, which the window does not suggest again.
    public static HashSet<string> Eaten()
    {
        var player = Player.m_localPlayer;
        return player == null ? [] : player.GetFoods().Where(food => food.m_item != null && food.m_item.m_dropPrefab != null).Select(food => food.m_item.m_dropPrefab.name).ToHashSet();
    }

    public static void Track(string prefab)
    {
        var item = ObjectDB.instance == null ? null : ObjectDB.instance.GetItemPrefab(prefab);
        var drop = item == null ? null : item.GetComponent<ItemDrop>();
        var recipe = drop == null ? null : ObjectDB.instance.GetRecipe(drop.m_itemData);
        if (recipe != null && !Tracker.Tracked(recipe))
        {
            Tracker.Toggle(recipe);
        }
    }
}
