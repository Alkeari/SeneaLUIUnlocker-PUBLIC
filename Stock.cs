using System;
using System.Collections.Generic;
using UnityEngine;

namespace SeneaLUIUnlocker;

// How many of each item (by its shared name) the player has at hand: what is carried and, when chosen, what the chests SeneaL UI crafts from hold within its range. Counted at most once a second.
internal static class Stock
{
    private static float _countedAt = -10f;
    private static bool _countedChests;
    private static Dictionary<string, int> _stock = new(StringComparer.Ordinal);

    public static int Have(string sharedName, bool chests)
    {
        Count(chests);
        return _stock.TryGetValue(sharedName, out var n) ? n : 0;
    }

    private static void Count(bool chests)
    {
        var player = Player.m_localPlayer;
        if (player == null || (Time.time - _countedAt < 1f && chests == _countedChests))
        {
            return;
        }
        _countedAt = Time.time;
        _countedChests = chests;
        var stock = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(Inventory inventory)
        {
            foreach (var item in inventory.GetAllItems())
            {
                stock.TryGetValue(item.m_shared.m_name, out var n);
                stock[item.m_shared.m_name] = n + item.m_stack;
            }
        }
        Add(player.GetInventory());
        if (chests && SeneaLCalls.CanRestock)
        {
            try
            {
                foreach (var chest in SeneaLCalls.CraftChests(player.transform.position))
                {
                    if (chest != null && chest.GetInventory() != null)
                    {
                        Add(chest.GetInventory());
                    }
                }
            }
            catch (Exception)
            {
            }
        }
        _stock = stock;
    }
}
