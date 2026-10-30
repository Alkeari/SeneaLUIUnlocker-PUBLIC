using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// The chat command /recipe <item> tells how an item is made (crafting with its station and materials, smelting, fermenting, cooking) and what you can make with it, from the game's own recipes and stations. Only what your character has already discovered is named, so it never spoils an item you have yet to find.
internal sealed class RecipeLookup
{
    private const int MaxUses = 12;

    private readonly ConfigEntry<bool> _on;
    private readonly ConfigEntry<bool> _spoilers;
    private bool _added;

    public RecipeLookup(ConfigFile cfg)
    {
        _on = cfg.Bind("Recipes", "Recipe Command", true,
            "Typing /recipe and an item name in the chat shows how it is made and what it is used for.");
        _spoilers = cfg.Bind("Recipes", "Recipe Command Shows Undiscovered", false,
            "Let /recipe also name items and recipes your character has not discovered yet. Off keeps the game's discoveries a surprise.");
    }

    public void Tick()
    {
        if (!_added && Chat.instance != null)
        {
            _added = true;
            _ = new Terminal.ConsoleCommand("recipe", "[item] How an item is made and what uses it.", args => Lookup(args.ArgsAll));
        }
    }

    private static string Name(ItemDrop item) => item == null ? "?" : Localization.instance.Localize(item.m_itemData.m_shared.m_name);

    private void Lookup(string term)
    {
        var player = Player.m_localPlayer;
        term = (term ?? "").Trim();
        if (!_on.Value || !ServerRules.Allows("RecipeLookup") || player == null || ObjectDB.instance == null)
        {
            return;
        }
        if (term.Length == 0)
        {
            Line("Use /recipe and part of an item's name, for example /recipe bronze");
            return;
        }
        bool Known(ItemDrop item) => _spoilers.Value || item != null && (player.IsMaterialKnown(item.m_itemData.m_shared.m_name) || player.IsRecipeKnown(item.m_itemData.m_shared.m_name));
        var matches = ObjectDB.instance.m_items
            .Select(go => go == null ? null : go.GetComponent<ItemDrop>())
            .Where(item => item != null && Known(item) && Name(item).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
            .GroupBy(Name).Select(group => group.First())
            .OrderBy(item => Name(item).Length).Take(3).ToList();
        if (matches.Count == 0)
        {
            Line($"No item you know matches \"{term}\".");
            return;
        }
        var conversions = Conversions();
        foreach (var item in matches)
        {
            var shared = item.m_itemData.m_shared.m_name;
            Line($"<b>{Name(item)}</b>");
            var made = 0;
            foreach (var recipe in ObjectDB.instance.m_recipes.Where(r => r != null && r.m_enabled && r.m_item != null && r.m_item.m_itemData.m_shared.m_name == shared))
            {
                var station = recipe.m_craftingStation == null ? "by hand" : $"at {Localization.instance.Localize(recipe.m_craftingStation.m_name)} level {recipe.m_minStationLevel}";
                var materials = string.Join(", ", recipe.m_resources.Where(r => r.m_resItem != null && r.m_amount > 0).Select(r => $"{Name(r.m_resItem)} {r.m_amount}"));
                Line($"  Craft {recipe.m_amount} {station}: {materials}");
                made++;
            }
            foreach (var (station, from, to, amount) in conversions.Where(c => c.To != null && c.To.m_itemData.m_shared.m_name == shared))
            {
                Line($"  {station}: {Name(from)} gives {amount} {Name(to)}");
                made++;
            }
            if (made == 0)
            {
                Line("  Not crafted: found, dropped or gathered.");
            }
            var uses = ObjectDB.instance.m_recipes
                .Where(r => r != null && r.m_enabled && r.m_item != null && Known(r.m_item) && r.m_resources.Any(res => res.m_resItem != null && res.m_resItem.m_itemData.m_shared.m_name == shared))
                .Select(r => Name(r.m_item))
                .Concat(conversions.Where(c => c.From != null && c.From.m_itemData.m_shared.m_name == shared && Known(c.To)).Select(c => $"{Name(c.To)} ({c.Station})"))
                .Distinct().ToList();
            if (uses.Count > 0)
            {
                Line($"  Used for: {string.Join(", ", uses.Take(MaxUses))}{(uses.Count > MaxUses ? $" and {uses.Count - MaxUses} more" : "")}");
            }
        }
        if (DebugLog.On)
        {
            DebugLog.Write("recipes", $"/recipe {term}: {string.Join(", ", matches.Select(Name))}");
        }
    }

    // What the game's processing stations turn into what: smelters, kilns and the like, fermenters, and cooking stations and ovens.
    private static List<(string Station, ItemDrop From, ItemDrop To, int Amount)> Conversions()
    {
        var list = new List<(string, ItemDrop, ItemDrop, int)>();
        if (ZNetScene.instance == null)
        {
            return list;
        }
        foreach (var prefab in ZNetScene.instance.m_prefabs)
        {
            if (prefab == null || prefab.GetComponent<Piece>() is not { } piece)
            {
                continue;
            }
            var station = Localization.instance.Localize(piece.m_name);
            if (prefab.GetComponent<Smelter>() is { } smelter)
            {
                list.AddRange(smelter.m_conversion.Select(c => (station, c.m_from, c.m_to, 1)));
            }
            if (prefab.GetComponent<Fermenter>() is { } fermenter)
            {
                list.AddRange(fermenter.m_conversion.Select(c => (station, c.m_from, c.m_to, c.m_producedItems)));
            }
            if (prefab.GetComponent<CookingStation>() is { } cooking)
            {
                list.AddRange(cooking.m_conversion.Select(c => (station, c.m_from, c.m_to, 1)));
            }
        }
        return list.GroupBy(c => (c.Item1, Name(c.Item2), Name(c.Item3))).Select(group => group.First()).ToList();
    }

    private static void Line(string text)
    {
        if (Chat.instance != null)
        {
            Chat.instance.AddString(text);
        }
    }
}
