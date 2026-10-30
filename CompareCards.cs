using System.Collections.Generic;
using SeneaLUI.Components;
using SeneaLUI.Core;
using SeneaLUI.Inv;
using SeneaLUI.Theme;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

internal interface ICompareCards
{
    void Tick();
}

// Comparison cards beside SeneaL UI's item tooltip (ItemCompare), drawn in the tooltip's own box and text styles inside its layer, so they fade, scale and sit above everything exactly as it does. SeneaL UI's tooltip itself is left as it is.
internal sealed class CompareCards : ICompareCards
{
    private const float CardW = 300f;
    private const float Gap = 8f;
    private const float Pad = 14f;
    private const float LineH = 20f;
    private static readonly Color Better = Palette.Rgba(140, 204, 110, 1f);
    private static readonly Color Worse = Palette.Rgba(222, 96, 84, 1f);

    private readonly List<RectTransform> _cards = [];
    private ItemDrop.ItemData _for;
    private int _stamp;

    public static ICompareCards Create() => new CompareCards();

    public void Tick()
    {
        var player = Player.m_localPlayer;
        var panel = Tooltip._panel;
        var item = ItemCompare.Enabled && player != null && Tooltip._shown && panel != null && panel.gameObject.activeInHierarchy && GridSkin.HoverCell != null && GridSkin.HoverInv != null
            ? GridSkin.HoverInv.GetItemAt(GridSkin.HoverCell.X, GridSkin.HoverCell.Y)
            : null;
        if (item == null || !ItemCompare.Comparable(item))
        {
            Clear();
            return;
        }
        var others = ItemCompare.Others(player, item);
        var stamp = Stamp(item, others);
        if (item != _for || stamp != _stamp)
        {
            _for = item;
            _stamp = stamp;
            Build(player, item, others);
        }
        Place(panel);
    }

    private static int Stamp(ItemDrop.ItemData item, List<ItemDrop.ItemData> others)
    {
        var stamp = item.m_quality * 31 + (item.m_equipped ? 1 : 0);
        foreach (var other in others)
        {
            stamp = stamp * 31 + other.GetHashCode() + other.m_quality;
        }
        return stamp;
    }

    private void Clear()
    {
        foreach (var card in _cards)
        {
            if (card != null)
            {
                Object.Destroy(card.gameObject);
            }
        }
        _cards.Clear();
        _for = null;
    }

    private void Build(Player player, ItemDrop.ItemData item, List<ItemDrop.ItemData> others)
    {
        Clear();
        _for = item;
        foreach (var other in others)
        {
            var lines = ItemCompare.Lines(player, item, other);
            if (lines.Count == 0)
            {
                continue;
            }
            var card = Ui.Node(Tooltip._root, "unlocker.compare");
            card.pivot = new Vector2(0f, 1f);
            Ui.Panel(card, Skin.Sliced(64, Tooltip.Box), Tooltip.Box, solid: true);
            var title = Ui.Label(card, "t", TextStyles.TipTitle, 0f, Pad, 12f + TextStyles.TipTitle.Baseline(1.3f), CardW - Pad * 2f, TextAlignmentOptions.TopLeft);
            title.text = Localization.instance.Localize(other.m_shared.m_name);
            var sub = Ui.Label(card, "s", TextStyles.TipSub, 0f, Pad, 12f + TextStyles.TipTitle.Size * 1.3f + 2f + TextStyles.TipSub.Baseline(1.3f), CardW - Pad * 2f, TextAlignmentOptions.TopLeft);
            sub.text = other.m_equipped ? Lang.T("CompareCards.WornNow", "Worn now") : Lang.T("CompareCards.Carried", "Carried");
            var y = 12f + TextStyles.TipTitle.Size * 1.3f + 2f + TextStyles.TipSub.Size * 1.3f + 10f;
            Column(card, "", Lang.T("CompareCards.This", "This") + "|" + (other.m_equipped ? Lang.T("CompareCards.Worn", "Worn") : Lang.T("CompareCards.That", "That")), y, Palette.Ash, Palette.Ash);
            y += LineH;
            foreach (var line in lines)
            {
                var color = line.Better > 0 ? Better : line.Better < 0 ? Worse : Palette.Linen;
                Column(card, line.Name, line.Hovered + "|" + line.Other, y, Palette.Parchment, color);
                y += LineH;
            }
            card.sizeDelta = new Vector2(CardW, y + 10f);
            _cards.Add(card);
        }
    }

    // A stat line: the name at the left, the hovered item's value and the compared item's value in two columns at the right.
    private static void Column(RectTransform card, string name, string values, float y, Color nameColor, Color valueColor)
    {
        var row = TextStyles.TipRow;
        var label = Ui.Label(card, "n", row, 0f, Pad, y + row.Baseline(1.3f), CardW - Pad * 2f - 130f, TextAlignmentOptions.TopLeft);
        label.text = name;
        label.color = nameColor;
        var parts = values.Split('|');
        var mine = Ui.Label(card, "a", TextStyles.TipValue, 0f, CardW - Pad - 130f, y + row.Baseline(1.3f), 62f, TextAlignmentOptions.TopRight);
        mine.text = parts[0];
        mine.color = valueColor;
        var theirs = Ui.Label(card, "b", TextStyles.TipValue, 0f, CardW - Pad - 62f, y + row.Baseline(1.3f), 62f, TextAlignmentOptions.TopRight);
        theirs.text = parts.Length > 1 ? parts[1] : "";
        theirs.color = Palette.Ash;
    }

    // Stacked beside the tooltip: to its right where the screen has room, otherwise to its left.
    private void Place(RectTransform panel)
    {
        if (_cards.Count == 0)
        {
            return;
        }
        var corners = new Vector3[4];
        panel.GetWorldCorners(corners);
        var scale = panel.lossyScale.x;
        var total = CardW * scale + Gap * scale;
        var right = corners[2].x + total <= Screen.width;
        var x = right ? corners[2].x + Gap * scale : corners[1].x - total;
        var y = corners[1].y;
        foreach (var card in _cards)
        {
            card.position = new Vector3(x, y, 0f);
            y -= (card.sizeDelta.y + Gap) * scale;
        }
    }
}
