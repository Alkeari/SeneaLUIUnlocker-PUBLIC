using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// A progress card on the Esc menu: which bosses this character has defeated, how many trophies the character has collected out of every trophy the game has, and the biomes it has found.
// A boss counts as defeated when the character holds its defeated_ unique key, the world holds the global key, or the character has ever picked up the boss's trophy: a character whose bosses fell on a server that never set the keys (seen on a real character with four boss trophies and no defeated_ key anywhere) still shows them.
internal static class Progress
{
    private static readonly (string Key, string Name)[] Bosses =
    [
        ("defeated_eikthyr", "Eikthyr"),
        ("defeated_gdking", "The Elder"),
        ("defeated_bonemass", "Bonemass"),
        ("defeated_dragon", "Moder"),
        ("defeated_goblinking", "Yagluth"),
        ("defeated_queen", "The Queen"),
        ("defeated_fader", "Fader")
    ];

    private static System.Collections.Generic.Dictionary<string, string> _bossTrophies;

    // The trophy each boss drops, read from the boss prefab whose death sets the key.
    private static string BossTrophy(string key)
    {
        if (_bossTrophies == null)
        {
            _bossTrophies = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var prefab in ZNetScene.instance.m_prefabs)
            {
                var character = prefab == null ? null : prefab.GetComponent<Character>();
                var drops = prefab == null ? null : prefab.GetComponent<CharacterDrop>();
                if (character == null || drops == null || string.IsNullOrEmpty(character.m_defeatSetGlobalKey))
                {
                    continue;
                }
                var trophy = drops.m_drops.Select(drop => drop.m_prefab).FirstOrDefault(item => item != null && item.GetComponent<ItemDrop>() != null
                    && item.GetComponent<ItemDrop>().m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trophy);
                if (trophy != null)
                {
                    _bossTrophies[character.m_defeatSetGlobalKey] = trophy.name;
                }
            }
        }
        return _bossTrophies.TryGetValue(key, out var name) ? name : null;
    }

    private static bool Defeated(Player player, string key)
    {
        var trophy = BossTrophy(key);
        return player.HaveUniqueKey(key) || ZoneSystem.instance.GetGlobalKey(key) || (trophy != null && player.m_trophies.Contains(trophy));
    }

    // How many of each trader's wares are open to buy: those whose required world key is set (Trader.TradeItem.m_requiredGlobalKey).
    private static string Traders()
    {
        if (ZNetScene.instance == null)
        {
            return "";
        }
        var lines = "";
        foreach (var prefabName in new[] { "Haldor", "Hildir", "BogWitch" })
        {
            var prefab = ZNetScene.instance.GetPrefab(prefabName);
            var trader = prefab == null ? null : prefab.GetComponent<Trader>();
            if (trader == null || trader.m_items.Count == 0)
            {
                continue;
            }
            var open = trader.m_items.Count(item => string.IsNullOrEmpty(item.m_requiredGlobalKey) || ZoneSystem.instance.GetGlobalKey(item.m_requiredGlobalKey));
            lines += $"{SettingsPage.Pretty(prefabName)}: {open} of {trader.m_items.Count} wares unlocked\n";
        }
        return lines;
    }

    private static readonly ThemedCard Card = new ThemedCard(new Vector2(0.28f, 0.4f), new Vector2(0f, 1f), Vector2.zero, 32005, 14f)
        .Element("Progress", "Progress", "<b>Progress</b>\nBosses defeated: 3 of 7 (defeated in gold)\nEikthyr, The Elder, Bonemass, Moder, Yagluth, The Queen, Fader\nTrophies collected: 12 of 74\nHaldor: 14 of 22 wares unlocked\nBiomes found: 4\nBlack Forest, Meadows, Mountain, Swamp", ElementMode.Other);

    private static ConfigEntry<bool> _enabled;
    private static ConfigEntry<bool> _bosses;
    private static ConfigEntry<bool> _trophies;
    private static ConfigEntry<bool> _traders;
    private static ConfigEntry<bool> _places;

    public static void Bind(ConfigFile cfg)
    {
        _enabled = cfg.Bind("Progress", "Progress On Esc Menu", true,
            "Show on the Esc menu which bosses are defeated, how many trophies you have collected, what the traders sell and the biomes you have found.");
        _bosses = cfg.Bind("Progress", "Progress Shows Bosses", true, "The Progress card lists every boss, the defeated ones in gold.");
        _trophies = cfg.Bind("Progress", "Progress Shows Trophies", true, "The Progress card says how many of the game's trophies you have collected.");
        _traders = cfg.Bind("Progress", "Progress Shows Traders", true, "The Progress card says how many of each trader's wares are open to buy.");
        _places = cfg.Bind("Progress", "Progress Shows Places", true, "The Progress card lists the biomes you have found.");
    }

    public static void Tick()
    {
        var player = Player.m_localPlayer;
        if (!_enabled.Value || !ServerRules.Allows("Progress") || player == null || !Menu.IsVisible() || SeneaLHud.SettingsOpen || ZoneSystem.instance == null || ObjectDB.instance == null)
        {
            Card.Show(null);
            return;
        }
        var gold = "#" + ColorUtility.ToHtmlStringRGB(SeneaLTheme.Color("Gold", new Color(0.95f, 0.78f, 0.3f)));
        var ash = "#" + ColorUtility.ToHtmlStringRGB(SeneaLTheme.Color("Ash", new Color(0.66f, 0.61f, 0.53f)));
        var bosses = Bosses.Select(boss => Defeated(player, boss.Key)
            ? $"<color={gold}>{boss.Name}</color>"
            : $"<color={ash}>{boss.Name}</color>");
        var trophies = ObjectDB.instance.m_items
            .Where(prefab => prefab != null && prefab.GetComponent<ItemDrop>() != null
                && prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trophy)
            .Select(prefab => prefab.name)
            .Distinct()
            .ToList();
        // The game records a collected trophy by its prefab name (Player.AddKnownItem), not its display token.
        var have = trophies.Count(name => player.m_trophies.Contains(name));
        var biomes = player.m_knownBiome.Select(name => Localization.instance.Localize(name)).OrderBy(name => name).ToList();
        var text = "<b>Progress</b>";
        if (_bosses.Value)
        {
            text += $"\nBosses defeated: {Bosses.Count(boss => Defeated(player, boss.Key))} of {Bosses.Length} (defeated in gold)\n" + string.Join(", ", bosses);
        }
        if (_trophies.Value)
        {
            text += $"\nTrophies collected: {have} of {trophies.Count}";
        }
        if (_traders.Value)
        {
            text += "\n" + Traders().TrimEnd('\n');
        }
        if (_places.Value)
        {
            text += $"\nBiomes found: {biomes.Count}" + (biomes.Count > 0 ? "\n" + string.Join(", ", biomes) : "");
        }
        Card.Show(text);
    }
}
