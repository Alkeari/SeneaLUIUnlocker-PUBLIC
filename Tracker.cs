using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Goals the player tracks beside SeneaL UI's Favorites, which keep their own job: recipes marked with the Track button in SeneaL UI's crafting window, each with how many to make, and the materials they still need against what is carried and, optionally, what nearby chests hold.
// Kept per character in its save (m_customData) as recipe item prefab = amount, the prefab followed by @level for an upgrade to that level; trophy:, piece:, skill: (target level), kills: (kills wanted) and item: (amount to have) goals share the same list.
internal static class Tracker
{
    private const string Section = "Tracker";
    private const string DataKey = "alkeari.senealuiunlocker.tracker";

    private static ConfigEntry<bool> _enabled;
    private static ConfigEntry<bool> _chests;
    private static ConfigEntry<bool> _hideGathered;
    private static ConfigEntry<bool> _totals;
    private static ConfigEntry<int> _maxColumns;
    private static ConfigEntry<bool> _autoScroll;
    private static ConfigEntry<float> _scrollSeconds;
    private static Player _for;
    private static List<(string Item, int Amount)> _goals = [];

    public static bool Enabled => _enabled != null && _enabled.Value && ServerRules.Allows("Tracker");
    public static bool HideGathered => _hideGathered != null && _hideGathered.Value;
    public static bool Totals => _totals != null && _totals.Value;
    public static int MaxColumns => _maxColumns == null ? 2 : _maxColumns.Value;
    public static bool AutoScroll => _autoScroll == null || _autoScroll.Value;
    public static float ScrollSeconds => _scrollSeconds == null ? 4f : _scrollSeconds.Value;

    public static void Bind(ConfigFile cfg)
    {
        _enabled = cfg.Bind(Section, "Tracker", true,
            "Show a Tracker beside the HUD with the recipes you mark with the Track button in the crafting window and the materials they still need. SeneaL UI's Favorites stay as they are.");
        _chests = cfg.Bind(Section, "Count Nearby Chests", true,
            "Count what the chests SeneaL UI crafts from hold, within its range, as well as what you carry.");
        _hideGathered = cfg.Bind(Section, "Hide Gathered Materials", true,
            "Leave out of the Tracker the materials you already have enough of.");
        _totals = cfg.Bind(Section, "Material Totals", true,
            "With two or more tracked goals needing materials, also list what they need together.");
        _maxColumns = cfg.Bind(Section, "Maximum Columns", 2,
            new ConfigDescription("How many columns wide the Tracker may grow when its goals do not fit the room left above or below it: it adds a column at a time up to this many, and past it the goals go on further pages, so every goal stays reachable. The default of 2 keeps it at the screen edge, clear of the middle of the screen.", new AcceptableValueRange<int>(1, 4)));
        _autoScroll = cfg.Bind(Section, "Turn Pages by Itself", true,
            "When the goals do not fit even at the maximum columns, the Tracker shows them a page at a time and turns the pages by itself while you play; with the cursor free the mouse wheel over it turns them.");
        _scrollSeconds = cfg.Bind(Section, "Seconds per Page", 4f,
            new ConfigDescription("How long the Tracker shows each page of goals before it turns to the next by itself.", new AcceptableValueRange<float>(1f, 30f)));
    }

    public static IReadOnlyList<(string Item, int Amount)> Goals
    {
        get
        {
            Load();
            return _goals;
        }
    }

    private static string Key(Recipe recipe, int quality) => quality > 1 ? $"{recipe.m_item.name}@{quality}" : recipe.m_item.name;

    public static bool Tracked(Recipe recipe, int quality = 1) => recipe != null && recipe.m_item != null && Goals.Any(goal => goal.Item == Key(recipe, quality));

    // Tracks the recipe at a quality level: 1 crafts the item, a higher level upgrades it to that level.
    public static void Toggle(Recipe recipe, int quality = 1)
    {
        if (recipe == null || recipe.m_item == null)
        {
            return;
        }
        Load();
        var name = Key(recipe, quality);
        var at = _goals.FindIndex(goal => goal.Item == name);
        if (at >= 0)
        {
            _goals.RemoveAt(at);
        }
        else
        {
            _goals.Add((name, 1));
        }
        Save();
    }

    public static void SetAmount(string item, int amount)
    {
        Load();
        var at = _goals.FindIndex(goal => goal.Item == item);
        if (at < 0)
        {
            return;
        }
        if (amount <= 0)
        {
            _goals.RemoveAt(at);
        }
        else
        {
            _goals[at] = (item, Math.Min(amount, item.StartsWith(SkillPrefix, StringComparison.Ordinal) ? MaxSkill : item.StartsWith(ItemPrefix, StringComparison.Ordinal) ? 9999 : 999));
        }
        Save();
    }

    private const string ItemPrefix = "item:";

    // Tracks having an amount of an item, by its prefab name; tracking it again raises the amount when the new one is larger.
    public static void TrackItem(string prefab, int amount)
    {
        Load();
        var item = ItemPrefix + prefab;
        var at = _goals.FindIndex(goal => goal.Item == item);
        amount = Math.Max(1, Math.Min(amount, 9999));
        if (at < 0)
        {
            _goals.Add((item, amount));
        }
        else if (_goals[at].Amount < amount)
        {
            _goals[at] = (item, amount);
        }
        Save();
    }

    // Tracks an item by its shared name, as SeneaL UI's crafting window names a material.
    public static void TrackItemNamed(string sharedName, int amount)
    {
        var drop = ObjectDB.instance == null ? null : ObjectDB.instance.m_items.Select(prefab => prefab == null ? null : prefab.GetComponent<ItemDrop>())
            .FirstOrDefault(found => found != null && found.m_itemData.m_shared.m_name == sharedName);
        if (drop != null)
        {
            TrackItem(drop.name, amount);
        }
    }

    // Items the character has seen, as (prefab, shown name, a full stack).
    public static List<(string Prefab, string Label, int Stack)> KnownItems()
    {
        var list = new List<(string, string, int)>();
        var player = Player.m_localPlayer;
        if (player == null || ObjectDB.instance == null)
        {
            return list;
        }
        foreach (var prefab in ObjectDB.instance.m_items)
        {
            var drop = prefab == null ? null : prefab.GetComponent<ItemDrop>();
            var shared = drop == null ? null : drop.m_itemData.m_shared;
            if (shared == null || !player.m_knownMaterial.Contains(shared.m_name) || shared.m_icons.Length == 0)
            {
                continue;
            }
            list.Add((prefab.name, Localization.instance.Localize(shared.m_name), Math.Max(1, shared.m_maxStackSize)));
        }
        return list.GroupBy(item => item.Item2).Select(group => group.First()).OrderBy(item => item.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Goal ItemGoal(string item, int amount)
    {
        var prefab = ObjectDB.instance.GetItemPrefab(item.Substring(ItemPrefix.Length));
        var drop = prefab == null ? null : prefab.GetComponent<ItemDrop>();
        if (drop == null)
        {
            return null;
        }
        var shared = drop.m_itemData.m_shared;
        var name = Localization.instance.Localize(shared.m_name);
        return new Goal(item, $"Have {name}", drop.m_itemData.GetIcon(), 1, [new Need(name, shared.m_icons.Length > 0 ? shared.m_icons[0] : null, Stock.Have(shared.m_name, _chests.Value), amount)]);
    }

    private const string SkillPrefix = "skill:";
    private const string KillPrefix = "kills:";
    private const string KillStartKey = "alkeari.senealuiunlocker.killstart";
    private const int MaxSkill = 100;
    private const int DefaultKills = 10;

    public static bool SkillTracked(Skills.SkillType skill) => Goals.Any(goal => goal.Item == SkillPrefix + skill);

    // Tracks a skill up to the next whole ten above its level, or stops tracking it.
    public static void ToggleSkill(Skills.SkillType skill)
    {
        var player = Player.m_localPlayer;
        if (player == null || skill == Skills.SkillType.None)
        {
            return;
        }
        Load();
        var item = SkillPrefix + skill;
        var at = _goals.FindIndex(goal => goal.Item == item);
        if (at >= 0)
        {
            _goals.RemoveAt(at);
        }
        else
        {
            var level = Mathf.FloorToInt(SkillLevel(player, skill));
            _goals.Add((item, Math.Min(MaxSkill, (level / 10 + 1) * 10)));
        }
        Save();
    }

    // The level the Skills window shows, read without creating a skill the character has never used.
    private static float SkillLevel(Player player, Skills.SkillType skill) =>
        player.m_skills != null && player.m_skills.m_skillData.TryGetValue(skill, out var data) ? data.m_level : 0f;

    private static Goal SkillGoal(Player player, string item, int target)
    {
        if (!Enum.TryParse<Skills.SkillType>(item.Substring(SkillPrefix.Length), out var skill) || player.m_skills == null)
        {
            return null;
        }
        var def = player.m_skills.GetSkillDef(skill);
        var name = Localization.instance.Localize("$skill_" + skill.ToString().ToLower());
        var level = Mathf.FloorToInt(SkillLevel(player, skill));
        return new Goal(item, $"{name} to {target}", def?.m_icon, 1, [new Need("Level", null, level, target)]);
    }

    // Tracks how many of a creature to kill from now on.
    public static void TrackKills(string creature)
    {
        var player = Player.m_localPlayer;
        Load();
        var item = KillPrefix + creature;
        if (player == null || _goals.Any(goal => goal.Item == item))
        {
            return;
        }
        var starts = Read(player, KillStartKey);
        starts[creature] = Kills(player).TryGetValue(creature, out var n) ? n : 0;
        player.m_customData[KillStartKey] = Write(starts);
        _goals.Add((item, DefaultKills));
        Save();
    }

    // Every creature but players, as (prefab, shown name).
    public static List<(string Prefab, string Label)> Creatures()
    {
        var list = new List<(string, string)>();
        if (ZNetScene.instance == null)
        {
            return list;
        }
        foreach (var prefab in ZNetScene.instance.m_prefabs)
        {
            var character = prefab == null ? null : prefab.GetComponent<Character>();
            if (character == null || character is Player || string.IsNullOrEmpty(character.m_name))
            {
                continue;
            }
            var label = Localization.instance.Localize(character.m_name);
            if (label.Length > 0 && !label.StartsWith("[", StringComparison.Ordinal) && !label.StartsWith("$", StringComparison.Ordinal))
            {
                list.Add((prefab.name, label));
            }
        }
        return list.GroupBy(item => item.Item2).Select(group => group.First()).OrderBy(item => item.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Goal KillGoal(Player player, string item, int wanted)
    {
        var creature = item.Substring(KillPrefix.Length);
        var prefab = ZNetScene.instance == null ? null : ZNetScene.instance.GetPrefab(creature);
        var character = prefab == null ? null : prefab.GetComponent<Character>();
        if (character == null)
        {
            return null;
        }
        var drops = prefab.GetComponent<CharacterDrop>();
        var trophy = drops == null ? null : TrophyDrop(drops);
        var kills = (Kills(player).TryGetValue(creature, out var n) ? n : 0) - (Read(player, KillStartKey).TryGetValue(creature, out var start) ? start : 0);
        return new Goal(item, $"Kill {Localization.instance.Localize(character.m_name)}", trophy == null ? null : trophy.m_prefab.GetComponent<ItemDrop>().m_itemData.GetIcon(), 1,
            [new Need("Kills", null, Math.Max(0, kills), wanted)]);
    }

    private static Dictionary<string, int> Read(Player player, string key)
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        if (player.m_customData.TryGetValue(key, out var saved))
        {
            foreach (var part in saved.Split(';'))
            {
                var eq = part.LastIndexOf('=');
                if (eq > 0 && int.TryParse(part.Substring(eq + 1), out var n))
                {
                    values[part.Substring(0, eq)] = n;
                }
            }
        }
        return values;
    }

    private static string Write(Dictionary<string, int> values) => string.Join(";", values.Select(pair => $"{pair.Key}={pair.Value}"));

    private static void Load()
    {
        var player = Player.m_localPlayer;
        if (player == null || player == _for)
        {
            return;
        }
        _for = player;
        _goals = [];
        if (player.m_customData.TryGetValue(DataKey, out var saved))
        {
            foreach (var part in saved.Split(';'))
            {
                var eq = part.LastIndexOf('=');
                if (eq > 0 && int.TryParse(part.Substring(eq + 1), out var amount))
                {
                    _goals.Add((part.Substring(0, eq), amount));
                }
            }
        }
    }

    private static void Save()
    {
        if (Player.m_localPlayer != null)
        {
            Player.m_localPlayer.m_customData[DataKey] = string.Join(";", _goals.Select(goal => $"{goal.Item}={goal.Amount}"));
        }
    }

    private const string TrophyPrefix = "trophy:";
    private const string PiecePrefix = "piece:";

    // Recipe and building goals are met by gathering materials; item, skill, kill and trophy goals are met outright.
    public static bool Gathers(string item) => item.StartsWith(PiecePrefix, StringComparison.Ordinal) || !(item.StartsWith(ItemPrefix, StringComparison.Ordinal) || item.StartsWith(SkillPrefix, StringComparison.Ordinal) || item.StartsWith(KillPrefix, StringComparison.Ordinal) || item.StartsWith(TrophyPrefix, StringComparison.Ordinal));

    // Tracks a building piece, by its prefab name.
    public static void TrackPiece(string prefab)
    {
        Load();
        var item = PiecePrefix + prefab;
        if (_goals.All(goal => goal.Item != item))
        {
            _goals.Add((item, 1));
            Save();
        }
    }

    // Building pieces the character knows, from every building tool's piece table, as (prefab, shown name).
    public static List<(string Prefab, string Label)> KnownPieces()
    {
        var list = new List<(string, string)>();
        var player = Player.m_localPlayer;
        if (player == null || ObjectDB.instance == null)
        {
            return list;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prefab in ObjectDB.instance.m_items)
        {
            var table = prefab == null ? null : prefab.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_buildPieces;
            if (table == null)
            {
                continue;
            }
            foreach (var go in table.m_pieces)
            {
                var piece = go == null ? null : go.GetComponent<Piece>();
                if (piece == null || !seen.Add(go.name) || piece.m_resources.Length == 0 || !player.m_knownRecipes.Contains(piece.m_name))
                {
                    continue;
                }
                list.Add((go.name, Localization.instance.Localize(piece.m_name)));
            }
        }
        return list.GroupBy(item => item.Item2).Select(group => group.First()).OrderBy(item => item.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Goal PieceGoal(string item, int amount)
    {
        var prefab = ZNetScene.instance == null ? null : ZNetScene.instance.GetPrefab(item.Substring(PiecePrefix.Length));
        var piece = prefab == null ? null : prefab.GetComponent<Piece>();
        if (piece == null)
        {
            return null;
        }
        var needs = new List<Need>();
        foreach (var requirement in piece.m_resources.Where(r => r != null && r.m_resItem != null && r.m_amount > 0))
        {
            var shared = requirement.m_resItem.m_itemData.m_shared;
            needs.Add(new Need(Localization.instance.Localize(shared.m_name), shared.m_icons.Length > 0 ? shared.m_icons[0] : null, Stock.Have(shared.m_name, _chests.Value), requirement.m_amount * amount));
        }
        return new Goal(item, Localization.instance.Localize(piece.m_name), piece.m_icon, amount, needs);
    }
    private const string KillsKey = "alkeari.senealuiunlocker.kills";

    // Tracks the trophy of a creature, by the creature's prefab name.
    public static void TrackTrophy(string creature)
    {
        Load();
        var item = TrophyPrefix + creature;
        if (_goals.All(goal => goal.Item != item))
        {
            _goals.Add((item, 1));
            Save();
        }
    }

    // Creatures whose drops include a trophy, as (prefab, shown name).
    public static List<(string Prefab, string Label)> TrophyCreatures()
    {
        var list = new List<(string, string)>();
        if (ZNetScene.instance == null)
        {
            return list;
        }
        foreach (var prefab in ZNetScene.instance.m_prefabs)
        {
            var character = prefab == null ? null : prefab.GetComponent<Character>();
            var drops = prefab == null ? null : prefab.GetComponent<CharacterDrop>();
            if (character == null || drops == null || character is Player || TrophyDrop(drops) == null)
            {
                continue;
            }
            list.Add((prefab.name, Localization.instance.Localize(character.m_name)));
        }
        return list.GroupBy(item => item.Item2).Select(group => group.First()).OrderBy(item => item.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static CharacterDrop.Drop TrophyDrop(CharacterDrop drops) =>
        drops.m_drops.FirstOrDefault(drop => drop.m_prefab != null && drop.m_prefab.GetComponent<ItemDrop>() is { } item && item.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trophy);

    // A trophy goal: kills counted since tracking began on this character, the trophy's drop chance, and whether the character has the trophy.
    private static Goal Trophy(Player player, string item)
    {
        var creature = item.Substring(TrophyPrefix.Length);
        var prefab = ZNetScene.instance == null ? null : ZNetScene.instance.GetPrefab(creature);
        var drops = prefab == null ? null : prefab.GetComponent<CharacterDrop>();
        var drop = drops == null ? null : TrophyDrop(drops);
        if (drop == null)
        {
            return null;
        }
        var trophy = drop.m_prefab.GetComponent<ItemDrop>().m_itemData;
        var has = player.m_trophies.Contains(trophy.m_shared.m_name) || player.GetInventory().HaveItem(trophy.m_shared.m_name);
        var kills = Kills(player).TryGetValue(creature, out var n) ? n : 0;
        var needs = new List<Need>
        {
            new($"{kills} {(kills == 1 ? "kill" : "kills")}, {Mathf.RoundToInt(drop.m_chance * 100f)}% drop", null, has ? 1 : 0, 1)
        };
        return new Goal(item, Localization.instance.Localize(trophy.m_shared.m_name), trophy.GetIcon(), 1, needs);
    }

    private static Dictionary<string, int> Kills(Player player) => Read(player, KillsKey);

    // Kills of creatures tracked for their trophy or a kill count by this player, counted where the game hands out the creature's drops (CharacterDrop.OnDeath), which runs on the client that owns the creature.
    [HarmonyLib.HarmonyPatch(typeof(CharacterDrop), nameof(CharacterDrop.OnDeath))]
    internal static class KillPatch
    {
        private static void Prefix(CharacterDrop __instance)
        {
            var player = Player.m_localPlayer;
            var character = __instance.m_character;
            if (player == null || character == null || character.m_lastHit == null || character.m_lastHit.GetAttacker() != player)
            {
                return;
            }
            var creature = Utils.GetPrefabName(__instance.gameObject);
            if (Goals.All(goal => goal.Item != TrophyPrefix + creature && goal.Item != KillPrefix + creature))
            {
                return;
            }
            var kills = Kills(player);
            kills[creature] = (kills.TryGetValue(creature, out var n) ? n : 0) + 1;
            player.m_customData[KillsKey] = Write(kills);
        }
    }

    public sealed record Need(string Name, Sprite Icon, int Have, int Wanted);

    public sealed record Goal(string Item, string Name, Sprite Icon, int Amount, List<Need> Needs);

    // A goal's item prefab and quality level, from its saved key.
    public static (string Prefab, int Quality) Split(string item)
    {
        var at = item.LastIndexOf('@');
        return at > 0 && int.TryParse(item.Substring(at + 1), out var quality) ? (item.Substring(0, at), Math.Max(1, quality)) : (item, 1);
    }

    // Every tracked recipe with each material it needs for the amount tracked at its quality level, and how much of it is at hand.
    public static List<Goal> Report()
    {
        var player = Player.m_localPlayer;
        var list = new List<Goal>();
        if (player == null || ObjectDB.instance == null)
        {
            return list;
        }
        foreach (var (item, amount) in Goals)
        {
            if (item.StartsWith(PiecePrefix, StringComparison.Ordinal))
            {
                if (PieceGoal(item, amount) is { } built)
                {
                    list.Add(built);
                }
                continue;
            }
            if (item.StartsWith(ItemPrefix, StringComparison.Ordinal))
            {
                if (ItemGoal(item, amount) is { } owned)
                {
                    list.Add(owned);
                }
                continue;
            }
            if (item.StartsWith(SkillPrefix, StringComparison.Ordinal))
            {
                if (SkillGoal(player, item, amount) is { } skill)
                {
                    list.Add(skill);
                }
                continue;
            }
            if (item.StartsWith(KillPrefix, StringComparison.Ordinal))
            {
                if (KillGoal(player, item, amount) is { } kill)
                {
                    list.Add(kill);
                }
                continue;
            }
            if (item.StartsWith(TrophyPrefix, StringComparison.Ordinal))
            {
                if (Trophy(player, item) is { } trophy)
                {
                    list.Add(trophy);
                }
                continue;
            }
            var (prefabName, quality) = Split(item);
            var prefab = ObjectDB.instance.GetItemPrefab(prefabName);
            var drop = prefab == null ? null : prefab.GetComponent<ItemDrop>();
            var recipe = drop == null ? null : ObjectDB.instance.GetRecipe(drop.m_itemData);
            if (recipe == null)
            {
                continue;
            }
            var needs = new List<Need>();
            foreach (var requirement in recipe.m_resources.Where(r => r != null && r.m_resItem != null))
            {
                var shared = requirement.m_resItem.m_itemData.m_shared;
                var wanted = requirement.GetAmount(quality) * amount;
                if (wanted <= 0)
                {
                    continue;
                }
                var have = Stock.Have(shared.m_name, _chests.Value);
                needs.Add(new Need(Localization.instance.Localize(shared.m_name), shared.m_icons.Length > 0 ? shared.m_icons[0] : null, have, wanted));
            }
            var crafts = quality > 1 ? 1 : Math.Max(1, recipe.m_amount);
            var shown = Localization.instance.Localize(drop.m_itemData.m_shared.m_name) + (quality > 1 ? $", level {quality}" : "");
            list.Add(new Goal(item, shown, drop.m_itemData.GetIcon(), amount * crafts, needs));
        }
        return list;
    }
}
