using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

internal enum PortalOres
{
    Never,
    After_Boss,
    Always
}

// What may go through portals, and stashing what may not. The game reads each item's shared m_teleportable, both where it checks a portal and where SeneaL UI badges an item, so a rule here sets that value on the item types it covers and gives the game's own value back when the rule is off.
// Ores and metals going through portals is a cheat against the game's rules, so it is offered and off by default; After Boss lets each metal through once its biome's boss is defeated, as some servers do.
internal static class PortalRules
{
    private const string Section = "Portals";

    // Each metal with its ore and scrap, and the global key the game sets when the boss that opens its biome dies.
    private static readonly (string Key, string[] Items)[] Metals =
    [
        ("defeated_gdking", ["CopperOre", "Copper", "CopperScrap", "TinOre", "Tin", "Bronze"]),
        ("defeated_bonemass", ["IronScrap", "IronOre", "Iron"]),
        ("defeated_dragon", ["SilverOre", "Silver", "DragonEgg"]),
        ("defeated_goblinking", ["BlackMetalScrap", "BlackMetal"]),
        ("defeated_queen", ["FlametalOre", "Flametal", "FlametalOreNew", "FlametalNew"])
    ];

    private static ConfigEntry<PortalOres> _ores;
    private static readonly Dictionary<string, bool> Original = new(StringComparer.Ordinal);
    private static float _next;

    public static void Bind(ConfigFile cfg)
    {
        _ores = cfg.Bind("Cheats: Portals", "Ores And Metals Through Portals (Cheat)", PortalOres.Never,
            "Cheat, Never by default. Never = as the game has it. After Boss = each metal, its ore and scrap may go through once the boss that opens its biome is defeated (Elder for copper, tin and bronze, Bonemass for iron, Moder for silver, Yagluth for black metal, the Queen for flametal). Always = all of them.");
        _ores.SettingChanged += (_, _) => _next = 0f;
    }

    public static void Tick(float now)
    {
        if (ObjectDB.instance == null || now < _next)
        {
            return;
        }
        _next = now + 5f;
        foreach (var (key, items) in Metals)
        {
            var allowed = ServerRules.Allows("OresThroughPortals") && _ores.Value switch
            {
                PortalOres.Always => true,
                PortalOres.After_Boss => ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(key),
                _ => false
            };
            foreach (var name in items)
            {
                var prefab = ObjectDB.instance.GetItemPrefab(name);
                var drop = prefab == null ? null : prefab.GetComponent<ItemDrop>();
                if (drop == null)
                {
                    continue;
                }
                var shared = drop.m_itemData.m_shared;
                if (!Original.ContainsKey(name))
                {
                    Original[name] = shared.m_teleportable;
                }
                shared.m_teleportable = allowed || Original[name];
            }
        }
    }

    // Moves every item that cannot go through a portal, and is not worn, into the nearest chest SeneaL UI crafts from that has room, as Restock To Targets moves items: a chest nobody has open is taken over first.
    public static int Stash()
    {
        var player = Player.m_localPlayer;
        if (player == null || !SeneaLCalls.CanRestock)
        {
            return 0;
        }
        var inventory = player.GetInventory();
        var items = inventory.GetAllItems().Where(item => !item.m_shared.m_teleportable && !item.m_equipped).ToList();
        if (items.Count == 0)
        {
            return 0;
        }
        var moved = 0;
        foreach (var chest in SeneaLCalls.CraftChests(player.transform.position).Where(chest => chest != null).OrderBy(chest => Vector3.Distance(chest.transform.position, player.transform.position)))
        {
            var view = SeneaLCalls.Nview(chest);
            if (view == null || !view.IsValid() || chest.GetInventory() == null)
            {
                continue;
            }
            if (!view.IsOwner())
            {
                if (chest.IsInUse())
                {
                    continue;
                }
                view.ClaimOwnership();
            }
            foreach (var item in items.ToList())
            {
                if (chest.GetInventory().CanAddItem(item))
                {
                    chest.GetInventory().MoveItemToThis(inventory, item);
                    items.Remove(item);
                    moved++;
                }
            }
            if (items.Count == 0)
            {
                break;
            }
        }
        player.Message(MessageHud.MessageType.TopLeft, moved > 0
            ? Lang.F("PortalRules.Stashed", "Stashed {0} items that cannot go through portals", moved)
            : Lang.T("PortalRules.NoRoom", "No nearby chest has room for the items that cannot go through portals"));
        return moved;
    }

    public static bool CarriesBlocked(Player player) => player != null && player.GetInventory().GetAllItems().Any(item => !item.m_shared.m_teleportable && !item.m_equipped);
}
