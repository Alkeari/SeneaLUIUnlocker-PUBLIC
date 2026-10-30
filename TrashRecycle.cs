using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

internal enum TrashMode
{
    Recycle,
    Trash
}

// SeneaL UI's trash slot and Trash key delete through TrashView.Delete, which removes the item with the game's own Inventory.RemoveItem. In Recycle mode the materials of the item's recipe come back at Recycle Refund Percent when that removal comes from SeneaL UI's trash.
internal static class TrashRecycle
{
    private const string TrashType = "SeneaLUI.Inv.TrashView";

    private static ConfigEntry<TrashMode> _mode;
    private static ConfigEntry<int> _percent;

    public static void Bind(ConfigFile cfg)
    {
        _mode = cfg.Bind("Trash", "Trash Mode", TrashMode.Trash,
            "What SeneaL UI's trash slot and Trash key do. Recycle = the item is removed and the materials of its recipe come back at Recycle Refund Percent; Trash = the item is deleted and nothing comes back, the default, since the game itself gives nothing back for an item.");
        _percent = cfg.Bind("Trash", "Recycle Refund Percent", 100, new ConfigDescription(
            "Share of the recipe's materials, at the item's quality, that Recycle gives back.", new AcceptableValueRange<int>(0, 100)));
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), typeof(ItemDrop.ItemData), typeof(int))]
    internal static class RemovePatch
    {
        private static void Prefix(Inventory __instance, ItemDrop.ItemData item, int amount)
        {
            if (_mode == null || _mode.Value != TrashMode.Recycle || !ServerRules.Allows("TrashRecycle") || _percent.Value <= 0 || item == null || amount <= 0
                || !__instance.ContainsItem(item) || !FromSeneaLTrash())
            {
                return;
            }
            Refund(item, Mathf.Min(amount, item.m_stack));
        }

        private static bool FromSeneaLTrash() =>
            new StackTrace(2, false).GetFrames()?.Any(frame => frame.GetMethod()?.DeclaringType?.FullName == TrashType) == true;
    }

    private static void Refund(ItemDrop.ItemData item, int amount)
    {
        var player = Player.m_localPlayer;
        var recipe = ObjectDB.instance == null ? null : ObjectDB.instance.GetRecipe(item);
        if (player == null || recipe == null || recipe.m_resources == null)
        {
            return;
        }
        // A recipe can make several at once (arrows, for one), so the refund is per item made, for every quality level the item went through.
        var made = Mathf.Max(1, recipe.m_amount);
        var rate = _percent.Value / 100f;
        var back = new List<(GameObject Prefab, int Amount)>();
        foreach (var requirement in recipe.m_resources.Where(r => r != null && r.m_resItem != null))
        {
            var perCraft = 0;
            for (var quality = 1; quality <= Math.Max(1, item.m_quality); quality++)
            {
                perCraft += requirement.GetAmount(quality);
            }
            var count = Mathf.FloorToInt(perCraft * rate * amount / made);
            if (count > 0)
            {
                back.Add((requirement.m_resItem.gameObject, count));
            }
        }
        var inv = player.GetInventory();
        var dropped = false;
        foreach (var (prefab, count) in back)
        {
            var left = count;
            while (left > 0)
            {
                var part = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
                part.m_stack = Mathf.Min(left, part.m_shared.m_maxStackSize);
                part.m_dropPrefab = prefab;
                left -= part.m_stack;
                if (!inv.AddItem(part))
                {
                    ItemDrop.DropItem(part, part.m_stack, player.transform.position + player.transform.forward + Vector3.up, Quaternion.identity);
                    dropped = true;
                }
            }
        }
        if (back.Count > 0)
        {
            var list = string.Join(", ", back.Select(b => $"{Localization.instance.Localize(b.Prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_name)} x{b.Amount}"));
            player.Message(MessageHud.MessageType.TopLeft, dropped ? Lang.F("TrashRecycle.RecycledDropped", "Recycled: {0} (some dropped, inventory full)", list) : Lang.F("TrashRecycle.Recycled", "Recycled: {0}", list));
        }
    }
}
