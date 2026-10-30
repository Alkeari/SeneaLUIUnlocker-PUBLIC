using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace SeneaLUIUnlocker;

internal enum ChestSort
{
    Off,
    Name,
    Weight,
    Category,
    Value
}

// Sorting reuses SeneaL UI's own sort (its locks, hotbar choice, stack merging and slot limit) and then reorders the cells it filled by value, most valuable first.
internal static class ValueSort
{
    public const int SeneaLByName = 0;

    public static void Player(Inventory inv)
    {
        SeneaLCalls.SortPlayer(inv, SeneaLByName);
        var start = SeneaLCalls.SortHotbar ? 0 : 1;
        Reorder(inv, start, SeneaLCalls.MainRows(inv));
    }

    public static void Container(Inventory inv)
    {
        SeneaLCalls.SortContainer(inv, SeneaLByName);
        Reorder(inv, 0, inv.GetHeight());
    }

    private static void Reorder(Inventory inv, int start, int rows)
    {
        var locks = SeneaLCalls.LocksOf(inv);
        var items = inv.GetAllItems()
            .Where(item => item.m_gridPos.y >= start && item.m_gridPos.y < rows && !locks.Contains(item.m_gridPos.y * 128 + item.m_gridPos.x))
            .OrderBy(item => item.m_gridPos.y).ThenBy(item => item.m_gridPos.x)
            .ToList();
        var cells = items.Select(item => item.m_gridPos).ToList();
        var byValue = items.OrderByDescending(item => item.m_shared.m_value * item.m_stack).ToList();
        for (var i = 0; i < byValue.Count; i++)
        {
            byValue[i].m_gridPos = cells[i];
        }
        SeneaLCalls.Changed(inv);
    }
}

internal sealed class InventoryFeatures
{
    private readonly ConfigEntry<KeyboardShortcut> _sortKey;
    private readonly ConfigEntry<KeyboardShortcut> _chestSortKey;
    private readonly ConfigEntry<ChestSort> _autoSort;
    private readonly ConfigEntry<string> _restockTargets;
    private readonly ConfigEntry<KeyboardShortcut> _restockKey;
    private readonly ManualLogSource _log;
    private Container _openChest;

    public InventoryFeatures(ConfigFile cfg, ManualLogSource log)
    {
        _log = log;
        const string Section = "Inventory";
        _sortKey = cfg.Bind(Section, "Sort By Value Key", new KeyboardShortcut(UnityEngine.KeyCode.V, UnityEngine.KeyCode.LeftShift),
            "With the inventory open, sorts it by value (most valuable stacks first), keeping SeneaL UI's locked slots and its hotbar choice. Empty = no key.");
        _chestSortKey = cfg.Bind(Section, "Chest Sort By Value Key", new KeyboardShortcut(UnityEngine.KeyCode.V, UnityEngine.KeyCode.LeftShift, UnityEngine.KeyCode.LeftControl),
            "With a chest open, sorts the chest by value, keeping SeneaL UI's locked chest slots. Empty = no key.");
        _autoSort = cfg.Bind(Section, "Auto Sort Chests", ChestSort.Off,
            "Sort a chest each time you open it: by name, weight or category (SeneaL UI's own sorts) or by value. Off by default because it reorders chests other players share.");
        _restockTargets = cfg.Bind(Section, "Restock Targets", "",
            "Amounts to keep in your inventory, separated by semicolons, as item name and amount (for example Wood arrow: 100; Cooked meat: 10). Names are the ones the game shows, in any case.");
        _restockKey = cfg.Bind(Section, "Restock To Targets Key", new KeyboardShortcut(UnityEngine.KeyCode.R, UnityEngine.KeyCode.LeftShift),
            "Tops your inventory up to Restock Targets from nearby chests, within SeneaL UI's quick stack range and only where SeneaL UI allows quick stack. Empty = no key.");
    }

    public void Tick()
    {
        var player = Player.m_localPlayer;
        var gui = InventoryGui.instance;
        if (player == null || gui == null || !SeneaLCalls.CanSort)
        {
            return;
        }
        var chest = InventoryGui.IsVisible() ? gui.m_currentContainer : null;
        if (chest != _openChest)
        {
            _openChest = chest;
            if (chest != null)
            {
                AutoSort(chest.GetInventory());
            }
        }
        if (Typing())
        {
            return;
        }
        if (InventoryGui.IsVisible())
        {
            if (chest != null && KeyInput.Pressed(_chestSortKey.Value) && ServerRules.Allows("Sorting"))
            {
                Run(() => ValueSort.Container(chest.GetInventory()));
            }
            else if (KeyInput.Pressed(_sortKey.Value) && ServerRules.Allows("Sorting"))
            {
                Run(() => ValueSort.Player(player.GetInventory()));
            }
        }
        if (KeyInput.Pressed(_restockKey.Value) && ServerRules.Allows("Restock"))
        {
            Run(() => Restock(player));
        }
    }

    private void AutoSort(Inventory inv)
    {
        if (!ServerRules.Allows("AutoSortChests"))
        {
            return;
        }
        switch (_autoSort.Value)
        {
            case ChestSort.Off:
                return;
            case ChestSort.Value:
                Run(() => ValueSort.Container(inv));
                return;
            default:
                Run(() => SeneaLCalls.SortContainer(inv, (int)_autoSort.Value - 1));
                return;
        }
    }

    private void Restock(Player player)
    {
        if (!SeneaLCalls.CanRestock)
        {
            return;
        }
        if (!SeneaLCalls.QuickStackAllowed)
        {
            player.Message(MessageHud.MessageType.TopLeft, Lang.T("InventoryFeatures.RestockIsNotAllowedOn", "Restock is not allowed on this server"));
            return;
        }
        var targets = Targets();
        if (targets.Count == 0)
        {
            player.Message(MessageHud.MessageType.TopLeft, Lang.T("InventoryFeatures.NoRestockTargetsSet", "No Restock Targets set"));
            return;
        }
        var inv = player.GetInventory();
        var moved = 0;
        var chests = SeneaLCalls.NearbyChests(player.transform.position);
        if (chests.Count == 0)
        {
            player.Message(MessageHud.MessageType.TopLeft, Lang.F("InventoryFeatures.NoChestWithinSenealUi", "No chest within SeneaL UI's quick stack range ({0:0.#} m) to restock from", SeneaLCalls.StackRange));
            return;
        }
        foreach (var chest in chests)
        {
            var view = SeneaLCalls.Nview(chest);
            if (view == null || !view.IsValid())
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
            moved += TakeFrom(inv, chest.GetInventory(), targets);
        }
        player.Message(MessageHud.MessageType.TopLeft, moved > 0 ? Lang.F("InventoryFeatures.Restocked", "Restocked {0} items to your targets", moved) : Lang.T("InventoryFeatures.NothingToRestock", "Nothing to restock"));
    }

    private static int TakeFrom(Inventory inv, Inventory chest, Dictionary<string, int> targets)
    {
        var moved = 0;
        foreach (var item in chest.GetAllItems().ToList())
        {
            var name = Localization.instance.Localize(item.m_shared.m_name).ToLowerInvariant();
            if (!targets.TryGetValue(name, out var target))
            {
                continue;
            }
            var need = target - inv.CountItems(item.m_shared.m_name);
            var take = Math.Min(need, item.m_stack);
            if (take <= 0)
            {
                continue;
            }
            var copy = item.Clone();
            copy.m_stack = take;
            if (!inv.CanAddItem(copy) || !inv.AddItem(copy))
            {
                continue;
            }
            chest.RemoveItem(item, take);
            moved += take;
        }
        return moved;
    }

    private Dictionary<string, int> Targets()
    {
        var targets = new Dictionary<string, int>();
        foreach (var part in _restockTargets.Value.Split(';'))
        {
            var colon = part.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(part.Substring(colon + 1).Trim(), out var amount) || amount <= 0)
            {
                continue;
            }
            targets[part.Substring(0, colon).Trim().ToLowerInvariant()] = amount;
        }
        return targets;
    }

    // Keys typed into a text box (chat, console, SeneaL UI's search boxes) are text, not commands.
    internal static bool Typing()
    {
        if (Chat.instance != null && Chat.instance.HasFocus() || Console.IsVisible() || TextInput.IsVisible())
        {
            return true;
        }
        var selected = UnityEngine.EventSystems.EventSystem.current == null ? null : UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject;
        return selected != null && (selected.GetComponent("TMP_InputField") != null || selected.GetComponent("InputField") != null);
    }

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            _log.LogWarning($"Could not finish an inventory action ({e.GetBaseException().Message}).");
        }
    }
}
