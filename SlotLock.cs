using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

// SeneaL UI locks or unlocks one slot on a left click with its LockSlot key held (Alt by default). When that click lands while slots are selected with SeneaL UI's multi-select, every selected slot takes the clicked slot's new state.
// SeneaL UI's multi-select holds items only and leaves locked items out, so the slots its selection rectangle covers, empty and locked ones included, are tracked here and framed like SeneaL UI's own selection.
internal sealed class SlotLock
{
    private const string Section = "Inventory";
    private const string FrameName = "unlocker.slotsel";

    private static readonly Type SelectionType = TypeLookup.Find("SeneaLUI.Inv.Selection");
    private static readonly Type SlotsType = TypeLookup.Find("SeneaLUI.Inv.Slots");
    private static readonly Type GridSkinType = TypeLookup.Find("SeneaLUI.Inv.GridSkin");
    private static readonly Type CellType = GridSkinType == null ? null : AccessTools.Inner(GridSkinType, "Cellv");
    private static readonly Type EntryType = SelectionType == null ? null : AccessTools.Inner(SelectionType, "Entry");
    private static readonly Type SkinType = TypeLookup.Find("SeneaLUI.Theme.Skin");
    private static readonly Type MucType = TypeLookup.Find("SeneaLUI.Mods.Muc");

    private static readonly FieldInfo Painting = Field(SelectionType, "_painting");
    private static readonly FieldInfo Moved = Field(SelectionType, "_moved");
    private static readonly FieldInfo AnchorInv = Field(SelectionType, "_anchorInv");
    private static readonly FieldInfo Ax = Field(SelectionType, "_ax");
    private static readonly FieldInfo Ay = Field(SelectionType, "_ay");
    private static readonly FieldInfo Lx = Field(SelectionType, "_lx");
    private static readonly FieldInfo Ly = Field(SelectionType, "_ly");
    private static readonly FieldInfo Items = Field(SelectionType, "Items");
    private static readonly FieldInfo FrameSprite = Field(SelectionType, "_frame");
    private static readonly FieldInfo FrameStyle = Field(SelectionType, "FrameStyle");
    private static readonly FieldInfo EntryInv = Field(EntryType, "Inv");
    private static readonly FieldInfo EntryItem = Field(EntryType, "Item");
    private static readonly FieldInfo Cells = Field(GridSkinType, "Cells");
    private static readonly FieldInfo HoverCell = Field(GridSkinType, "HoverCell");
    private static readonly FieldInfo HoverInv = Field(GridSkinType, "HoverInv");
    private static readonly FieldInfo CellEl = Field(CellType, "El");
    private static readonly FieldInfo CellSlot = Field(CellType, "Slot");
    private static readonly FieldInfo CellX = Field(CellType, "X");
    private static readonly FieldInfo CellY = Field(CellType, "Y");
    private static readonly FieldInfo Current = Field(SlotsType, "Current");
    private static readonly MethodInfo SkinFor = SelectionType == null ? null : AccessTools.Method(SelectionType, "SkinFor", [typeof(Inventory)]);
    private static readonly MethodInfo Contains = SelectionType == null ? null : AccessTools.Method(SelectionType, "Contains", [typeof(ItemDrop.ItemData)]);
    private static readonly MethodInfo IsPlayer = SlotsMethod("IsPlayer", typeof(Inventory));
    private static readonly MethodInfo IsContainer = SlotsMethod("IsContainer", typeof(Inventory));
    private static readonly MethodInfo Ready = SlotsMethod("Ready", typeof(Inventory));
    private static readonly MethodInfo IsReserved = SlotsMethod("IsReserved", typeof(Inventory), typeof(int));
    private static readonly MethodInfo LockedIn = SlotsMethod("LockedIn", typeof(Inventory), typeof(int), typeof(int));
    private static readonly MethodInfo ToggleLock = SlotsMethod("ToggleLock", typeof(Inventory), typeof(int), typeof(int));
    private static readonly MethodInfo Remote = MucType == null ? null : AccessTools.Method(MucType, "Remote", [typeof(Container)]);
    private static readonly MethodInfo Box = SkinType == null || FrameStyle == null ? null : AccessTools.Method(SkinType, "Box", [typeof(int), typeof(int), FrameStyle.FieldType, typeof(int)]);

    private static SlotLock _current;
    private static ConfigEntry<KeyboardShortcut> _lockKey;

    private readonly ConfigEntry<bool> _enabled;
    private readonly ConfigEntry<bool> _selectSlots;
    private readonly HashSet<(Inventory Inv, int X, int Y)> _cells = new();
    private readonly Dictionary<(Inventory Inv, int X, int Y), GameObject> _frames = new();
    private HashSet<(Inventory Inv, int X, int Y)> _base = new();
    private bool _wasPainting;
    private bool _moved;
    private int _selectedItems;
    private Sprite _frame;

    public static bool Available =>
        new object[] { Painting, Moved, AnchorInv, Ax, Ay, Lx, Ly, Items, EntryInv, EntryItem, Cells, HoverCell, HoverInv, CellEl, CellSlot, CellX, CellY, Current, SkinFor, Contains, IsPlayer, IsContainer, Ready, IsReserved, LockedIn, ToggleLock, Remote }
            .All(member => member != null);

    public SlotLock(ConfigFile cfg)
    {
        _enabled = cfg.Bind(Section, "Lock Selected Slots Together", true,
            "Locking or unlocking a slot with SeneaL UI's lock (its LockSlot key held during a left click) while slots are selected with the multi-select does the same to every selected slot.");
        _selectSlots = cfg.Bind(Section, "Select Empty And Locked Slots", true,
            "SeneaL UI's multi-select also selects the empty and locked slots it covers, so they lock and unlock together with the rest.");
        if (Chainloader.PluginInfos.TryGetValue("seneaL.valheim.ui", out var seneal) && seneal.Instance != null)
        {
            _lockKey = SeneaLConfig.Entry(seneal.Instance.Config, "Keys", "LockSlot") as ConfigEntry<KeyboardShortcut>;
        }
        _current = this;
    }

    public void Tick()
    {
        var gui = InventoryGui.instance;
        if (gui == null || !InventoryGui.IsVisible())
        {
            ClearCells();
            _wasPainting = false;
            return;
        }
        var items = (IList)Items.GetValue(null);
        if (_selectSlots.Value && ServerRules.Allows("MultiSelect"))
        {
            TrackSelection(items);
        }
        else if (_cells.Count > 0)
        {
            ClearCells();
        }
        DrawFrames();
    }

    // Mirrors SeneaL UI's Selection: a drag adds the slots under its rectangle to the slots selected before it, a press without a drag toggles the pressed slot, and a plain click or SeneaL UI clearing its selection clears it.
    private void TrackSelection(IList items)
    {
        var painting = (bool)Painting.GetValue(null);
        if (painting && !_wasPainting)
        {
            _base = new HashSet<(Inventory, int, int)>(_cells);
            _moved = false;
        }
        var inv = AnchorInv.GetValue(null) as Inventory;
        if (painting && inv != null)
        {
            var lx = (int)Lx.GetValue(null);
            if ((bool)Moved.GetValue(null) && lx != int.MinValue)
            {
                _moved = true;
                _cells.Clear();
                _cells.UnionWith(_base);
                _cells.UnionWith(Rect(inv, (int)Ax.GetValue(null), (int)Ay.GetValue(null), lx, (int)Ly.GetValue(null)));
            }
        }
        else if (!painting && _wasPainting && !_moved && inv != null)
        {
            var cell = (inv, (int)Ax.GetValue(null), (int)Ay.GetValue(null));
            if (!_cells.Remove(cell) && Lockable(cell))
            {
                _cells.Add(cell);
            }
        }
        else if (!painting && (_selectedItems > 0 && items.Count == 0 || ZInput.GetMouseButtonDown(0) && !LockHeld))
        {
            ClearCells();
        }
        _wasPainting = painting;
        _selectedItems = items.Count;
    }

    // The same rectangle SeneaL UI selects by: every shown cell whose center lies between the anchor cell and the cell under the cursor on screen.
    private static IEnumerable<(Inventory, int, int)> Rect(Inventory inv, int ax, int ay, int lx, int ly)
    {
        var cells = ShownCells(inv).ToList();
        var from = cells.FirstOrDefault(c => c.X == ax && c.Y == ay);
        var to = cells.FirstOrDefault(c => c.X == lx && c.Y == ly);
        if (from.Rect == null || to.Rect == null)
        {
            yield break;
        }
        var a = Center(from.Rect);
        var b = Center(to.Rect);
        var min = Vector2.Min(a, b) - Vector2.one * 3f;
        var max = Vector2.Max(a, b) + Vector2.one * 3f;
        foreach (var cell in cells)
        {
            var c = Center(cell.Rect);
            if (c.x >= min.x && c.x <= max.x && c.y >= min.y && c.y <= max.y && Lockable((inv, cell.X, cell.Y)))
            {
                yield return (inv, cell.X, cell.Y);
            }
        }
    }

    private static Vector2 Center(RectTransform rect) => rect.TransformPoint(rect.rect.center);

    private static IEnumerable<(int X, int Y, RectTransform Rect)> ShownCells(Inventory inv)
    {
        if (SkinFor.Invoke(null, [inv]) is not { } skin || Cells.GetValue(skin) is not IList cells)
        {
            yield break;
        }
        foreach (var cell in cells)
        {
            var el = CellEl.GetValue(cell) as InventoryElement;
            if (el != null && CellSlot.GetValue(cell) != null && el.gameObject.activeInHierarchy)
            {
                yield return ((int)CellX.GetValue(cell), (int)CellY.GetValue(cell), (RectTransform)el.transform);
            }
        }
    }

    // The slots SeneaL UI's own lock accepts: the player's grid outside its reserved rows once SeneaL UI has laid it out, and a chest no other player has open.
    private static bool Lockable((Inventory Inv, int X, int Y) cell)
    {
        if ((bool)IsPlayer.Invoke(null, [cell.Inv]))
        {
            return (bool)Ready.Invoke(null, [cell.Inv]) && !(bool)IsReserved.Invoke(null, [cell.Inv, cell.Y]);
        }
        return (bool)IsContainer.Invoke(null, [cell.Inv]) && !(bool)Remote.Invoke(null, [Current.GetValue(null)]);
    }

    private static bool Locked((Inventory Inv, int X, int Y) cell) => (bool)LockedIn.Invoke(null, [cell.Inv, cell.X, cell.Y]);

    private static bool LockHeld => _lockKey != null && KeyInput.Held(_lockKey.Value);

    private void LockTogether((Inventory Inv, int X, int Y) clicked)
    {
        var targets = new HashSet<(Inventory Inv, int X, int Y)>(_cells);
        foreach (var entry in (IList)Items.GetValue(null))
        {
            if (EntryInv.GetValue(entry) is Inventory inv && EntryItem.GetValue(entry) is ItemDrop.ItemData item)
            {
                targets.Add((inv, item.m_gridPos.x, item.m_gridPos.y));
            }
        }
        targets.Remove(clicked);
        if (targets.Count == 0 || !Lockable(clicked))
        {
            return;
        }
        var locked = Locked(clicked);
        foreach (var cell in targets.Where(cell => Lockable(cell) && Locked(cell) != locked))
        {
            ToggleLock.Invoke(null, [cell.Inv, cell.X, cell.Y]);
        }
    }

    // Runs after SeneaL UI's slot lock prefix; a click it took with LockSlot held has already toggled the clicked slot.
    [HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.OnLeftDown))]
    [HarmonyPriority(Priority.Last)]
    internal static class LeftDownPatch
    {
        private static void Prefix(InventoryGrid __instance, UIInputHandler clickHandler, bool __runOriginal)
        {
            var self = _current;
            if (self == null || !self._enabled.Value || !ServerRules.Allows("MultiSelect") || __runOriginal || !LockHeld)
            {
                return;
            }
            var pos = __instance.GetButtonPos(clickHandler.gameObject);
            self.LockTogether((__instance.GetInventory(), pos.x, pos.y));
        }
    }

    private void DrawFrames()
    {
        foreach (var key in _frames.Keys.Where(key => !_cells.Contains(key)).ToList())
        {
            Drop(key);
        }
        foreach (var inv in _cells.Select(cell => cell.Inv).Distinct().ToList())
        {
            foreach (var shown in ShownCells(inv))
            {
                var key = (inv, shown.X, shown.Y);
                if (!_cells.Contains(key))
                {
                    continue;
                }
                var item = inv.GetItemAt(shown.X, shown.Y);
                var framedBySeneaL = item != null && (bool)Contains.Invoke(null, [item]);
                _frames.TryGetValue(key, out var frame);
                if (framedBySeneaL)
                {
                    Drop(key);
                    continue;
                }
                if (frame == null || frame.transform.parent != shown.Rect)
                {
                    Drop(key);
                    _frames[key] = MakeFrame(shown.Rect);
                }
            }
        }
    }

    private GameObject MakeFrame(RectTransform cell)
    {
        var go = new GameObject(FrameName, typeof(RectTransform), typeof(Image));
        var rect = (RectTransform)go.transform;
        rect.SetParent(cell, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        var image = go.GetComponent<Image>();
        image.sprite = FrameSpriteNow();
        image.type = Image.Type.Sliced;
        image.raycastTarget = false;
        return go;
    }

    // SeneaL UI's own selection ring, made by SeneaL UI the first time it selects an item, or drawn here from the same style before that.
    private Sprite FrameSpriteNow()
    {
        if (FrameSprite?.GetValue(null) is Sprite seneal && seneal != null)
        {
            return seneal;
        }
        if (_frame == null && Box != null)
        {
            _frame = Box.Invoke(null, [64, 64, FrameStyle.GetValue(null), -1]) as Sprite;
        }
        return _frame;
    }

    private void Drop((Inventory, int, int) key)
    {
        if (_frames.TryGetValue(key, out var frame))
        {
            if (frame != null)
            {
                UnityEngine.Object.Destroy(frame);
            }
            _frames.Remove(key);
        }
    }

    private void ClearCells()
    {
        _cells.Clear();
        _base.Clear();
        foreach (var key in _frames.Keys.ToList())
        {
            Drop(key);
        }
    }

    private static FieldInfo Field(Type type, string name) => type == null ? null : AccessTools.Field(type, name);

    private static MethodInfo SlotsMethod(string name, params Type[] args) => SlotsType == null ? null : AccessTools.Method(SlotsType, name, args);
}
