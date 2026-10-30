using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SeneaLUI.Components;
using SeneaLUI.Core;
using SeneaLUI.Hud;
using SeneaLUI.Inv;
using SeneaLUI.Theme;
using TMPro;
using UnityEngine;

namespace SeneaLUIUnlocker;

// SeneaL UI 1.2.1 and newer list every element in their own Objects popup, so there this plugin's Elements panel stays closed and its row tools (lock, pick, the Crafting window's station placement) join each of SeneaL UI's rows, with Copy Look, Paste Look and the line-up buttons under the list.
// ObjectsMenu is missing from older supported builds, so it is reached only by reflection and never named as a type.
internal sealed partial class EditorAdditions
{
    // A plain lookup, since AccessTools.Field logs a warning on every build without the field.
    private static readonly FieldInfo ObjectsField = typeof(HudEditor).GetField("_objects", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly System.Type ObjectsType = ObjectsField?.FieldType;
    private static readonly PropertyInfo ObjectsOpen = ObjectsType == null ? null : AccessTools.Property(ObjectsType, "Open");
    private static readonly FieldInfo ObjectsPop = ObjectsType == null ? null : AccessTools.Field(ObjectsType, "_pop");
    private static readonly FieldInfo ObjectsRows = ObjectsType == null ? null : AccessTools.Field(ObjectsType, "_rows");
    private static readonly FieldInfo ObjectsList = ObjectsType == null ? null : AccessTools.Field(ObjectsType, "_list");

    private const float ToolGap = 6f;
    private const float GridWidth = 108f;

    private static readonly string[] ArrangeLabels = ["Align Left", "Align Center", "Align Right", "Align Top", "Align Middle", "Align Bottom", "Space Across", "Space Down", "Clear Picks"];
    private static readonly string[] ArrangeTips =
    [
        "Move the picked elements so their left edges line up with the first one picked.",
        "Move the picked elements so their centers line up across with the first one picked.",
        "Move the picked elements so their right edges line up with the first one picked.",
        "Move the picked elements so their top edges line up with the first one picked.",
        "Move the picked elements so their centers line up down with the first one picked.",
        "Move the picked elements so their bottom edges line up with the first one picked.",
        "Space the picked elements evenly across, between the outermost two.",
        "Space the picked elements evenly down, between the outermost two.",
        "Unpick every picked element."
    ];

    private readonly Dictionary<GameObject, (int Index, Tb Lock, Tb Pick, Tb Station)> _objectRows = [];
    private RectTransform _objectsTools;
    private RectTransform _arrangeGrid;
    private RectTransform _toolsPop;
    private float _popSet = -1f;
    private float _listHeight;
    private bool _arrangeShown;
    private int _firstRow;
    private int _rowCount = -1;

    private static readonly bool SeneaLListsObjects = ObjectsField != null
        && ObjectsOpen?.PropertyType == typeof(bool)
        && ObjectsPop?.FieldType == typeof(RectTransform)
        && ObjectsRows?.FieldType == typeof(List<GameObject>)
        && ObjectsList?.FieldType == typeof(List<int>);

    private void ExtendObjects(HudEditor editor)
    {
        var objects = ObjectsField.GetValue(editor);
        if (objects == null || !(bool)ObjectsOpen.GetValue(objects))
        {
            return;
        }
        var pop = (RectTransform)ObjectsPop.GetValue(objects);
        var rows = (List<GameObject>)ObjectsRows.GetValue(objects);
        var list = (List<int>)ObjectsList.GetValue(objects);
        if (pop == null || rows == null || list == null || rows.Count != list.Count)
        {
            return;
        }
        for (var k = 0; k < rows.Count; k++)
        {
            var row = rows[k];
            if (row == null || list[k] < 0 || list[k] >= editor._targets.Length)
            {
                continue;
            }
            if (!_objectRows.TryGetValue(row, out var tools))
            {
                tools = AddRowTools(editor, (RectTransform)row.transform, list[k]);
                _objectRows[row] = tools;
            }
            var t = editor._targets[tools.Index];
            if (t.E != null)
            {
                tools.Lock?.SetLit(Locked(t));
                tools.Pick?.SetLit(Picked(t));
            }
            tools.Station?.SetLit(CraftingPlacement.Current != null && CraftingPlacement.Current.EditingStation);
        }
        foreach (var gone in _objectRows.Keys.Where(r => r == null).ToList())
        {
            _objectRows.Remove(gone);
        }
        ExtendFooter(pop, rows.Count == 0 ? 0 : rows[0].GetInstanceID(), rows.Count);
    }

    // Each row's own buttons sit left of SeneaL UI's eye, which takes the row's right 30 units, and its name gives up the room they take.
    private (int Index, Tb Lock, Tb Pick, Tb Station) AddRowTools(HudEditor editor, RectTransform row, int index)
    {
        var target = editor._targets[index];
        if (target.E == null || !target.Available || !target.Present)
        {
            return (index, null, null, null);
        }
        var height = row.sizeDelta.y;
        var locker = new Tb(row, "unlocker.lock", "lock", () => ToggleLock(Editor._targets[index]), () => Tips.Text(new Tip(), "Lock", "Lock this element in place: it can still be selected and set, but not moved."));
        locker.SetLabel(null);
        Ui.Place(locker.Root, 1f, 1f, -34f, -(height - 26f) * 0.5f, 26f, 26f);
        var pick = new Tb(row, "unlocker.pick", "check", () => TogglePick(Editor._targets[index]), () => Tips.Text(new Tip(), "Pick", "Pick this element to line it up or space it evenly with the other picked elements."));
        pick.SetLabel(null);
        Ui.Place(pick.Root, 1f, 1f, -64f, -(height - 26f) * 0.5f, 26f, 26f);
        var used = 60f;
        Tb station = null;
        if (target.E.Key == "Crafting" && CraftingPlacement.Current != null && CraftingPlacement.Current.Enabled)
        {
            station = new Tb(row, "unlocker.station", "layers", () =>
            {
                var placement = CraftingPlacement.Current;
                placement?.Edit(!placement.EditingStation);
                Editor.Select(index);
            }, () => Tips.Text(new Tip(), "Station Placement", "Switch between placing the Crafting window beside your inventory and at a crafting station, where SeneaL UI draws it wider; lit while the station placement is the one being set."));
            station.SetLabel(null);
            Ui.Place(station.Root, 1f, 1f, -94f, -(height - 26f) * 0.5f, 26f, 26f);
            used = 90f;
        }
        var name = row.Find("t") as RectTransform;
        if (name != null)
        {
            name.sizeDelta = new Vector2(Mathf.Max(60f, name.sizeDelta.x - used), name.sizeDelta.y);
        }
        var note = row.Find("n") as RectTransform;
        if (note != null)
        {
            note.sizeDelta = new Vector2(Mathf.Max(60f, note.sizeDelta.x - used), note.sizeDelta.y);
        }
        return (index, locker, pick, station);
    }

    // SeneaL UI sizes the popup to its list each time it refills it, destroying and recreating every row, so new rows or a height other than the one last set here mean a fresh list height, and the footer goes on top of it.
    private void ExtendFooter(RectTransform pop, int firstRow, int rowCount)
    {
        if (_toolsPop != pop || _objectsTools == null)
        {
            BuildFooter(pop);
        }
        var arrange = _picked.Count >= 2;
        var refilled = firstRow != _firstRow || rowCount != _rowCount;
        _firstRow = firstRow;
        _rowCount = rowCount;
        if (refilled || !Mathf.Approximately(pop.sizeDelta.y, _popSet))
        {
            _listHeight = pop.sizeDelta.y;
            _arrangeShown = !arrange;
        }
        else if (arrange == _arrangeShown)
        {
            return;
        }
        _arrangeShown = arrange;
        _arrangeGrid.gameObject.SetActive(arrange);
        var footer = Tb.Height + ToolGap + (arrange ? (Tb.Height + ToolGap) * 3f : 0f);
        _objectsTools.sizeDelta = new Vector2(340f, footer);
        _popSet = _listHeight + footer;
        pop.sizeDelta = new Vector2(pop.sizeDelta.x, _popSet);
    }

    private void BuildFooter(RectTransform pop)
    {
        _toolsPop = pop;
        _popSet = -1f;
        _arrangeShown = false;
        _objectsTools = Ui.Node(pop, "unlocker.tools");
        _objectsTools.anchorMin = _objectsTools.anchorMax = new Vector2(0f, 0f);
        _objectsTools.pivot = new Vector2(0f, 0f);
        _objectsTools.anchoredPosition = new Vector2(10f, 10f);
        _objectsTools.sizeDelta = new Vector2(340f, Tb.Height);
        var copy = new Tb(_objectsTools, "copy", "layers", () => CopyLook(Editor), () => Tips.Text(new Tip(), "Copy Look", "Copy the selected element's look: size, text size, opacity, transparency, background, colors, style, value position, key cap, caption look and animation."));
        copy.SetLabel("Copy Look");
        var paste = new Tb(_objectsTools, "paste", "grab", () => PasteLook(Editor), () => Tips.Text(new Tip(), "Paste Look", "Give the selected element the look last copied; its place, attachment and content stay as they are."));
        paste.SetLabel("Paste Look");
        Ui.Place(copy.Root, 0f, 0f, 0f, 0f, copy.Width, Tb.Height);
        Ui.Place(paste.Root, 0f, 0f, copy.Width + ToolGap, 0f, paste.Width, Tb.Height);
        _arrangeGrid = Ui.Node(_objectsTools, "arrange");
        _arrangeGrid.anchorMin = _arrangeGrid.anchorMax = new Vector2(0f, 0f);
        _arrangeGrid.pivot = new Vector2(0f, 0f);
        _arrangeGrid.anchoredPosition = new Vector2(0f, Tb.Height + ToolGap);
        _arrangeGrid.sizeDelta = new Vector2(340f, (Tb.Height + ToolGap) * 3f - ToolGap);
        for (var i = 0; i < ArrangeLabels.Length; i++)
        {
            var how = i;
            var tip = ArrangeTips[i];
            var label = ArrangeLabels[i];
            var button = new Tb(_arrangeGrid, "arrange" + i, "check", () =>
            {
                Arrange(Editor, how);
            }, () => Tips.Text(new Tip(), label, tip));
            button.SetLabel(label);
            var column = i % 3;
            var line = 2 - i / 3;
            Ui.Place(button.Root, 0f, 0f, column * (GridWidth + ToolGap), line * (Tb.Height + ToolGap), GridWidth, Tb.Height);
        }
        _arrangeGrid.gameObject.SetActive(false);
    }
}
