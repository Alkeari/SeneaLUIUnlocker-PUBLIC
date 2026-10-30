using System;
using System.Collections.Generic;
using HarmonyLib;
using SeneaLUI.Components;
using SeneaLUI.Core;
using SeneaLUI.Theme;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

// A dropdown in SeneaL UI's look: SeneaL UI's own ElementSelect where the build has it (1.2.0 and later), otherwise one built here from the parts every SeneaL UI build shares, so the windows that use a dropdown work on SeneaL UI 1.1.x too.
internal interface IChoice
{
    Action<int> OnPick { get; set; }

    RectTransform Root { get; }

    bool Open { get; }

    void BindNames(string[] names, int current);

    void SetCurrent(string name);

    bool Close();

    void Tick(Component owner);

    void Dispose();
}

internal static class Choice
{
    public const float H = 34f;

    public static IChoice Create(RectTransform parent, float x, float y, float w, RectTransform popHost = null) =>
        WindowAdditions.Available ? (IChoice)WindowAdditions.Create("SeneaLChoice", parent, x, y, w, popHost) : new OwnChoice(parent, x, y, w, popHost);
}

internal sealed class OwnChoice : IChoice
{
    private const float RowH = 30f;
    private const float ListTop = 46f;
    private const float MaxListH = 210f;

    private static readonly Vector3[] Corners = new Vector3[4];

    private readonly RectTransform _root;
    private readonly RectTransform _pop;
    private readonly RectTransform _host;
    private readonly Image _bg;
    private readonly Image _chevron;
    private readonly TextMeshProUGUI _label;
    private readonly SearchBox _search;
    private readonly ScrollBox _scroll;
    private readonly TextMeshProUGUI _empty;
    private readonly List<GameObject> _rows = [];
    private readonly float _w;
    private readonly Sprite _normal = Skin.Sliced(34, Styles.Search);
    private readonly Sprite _hot = Skin.Sliced(34, Styles.SearchFocus);
    private readonly TextStyle _nameStyle = TextStyle.SemiBold(15f, Palette.Parchment);
    private readonly TextStyle _itemStyle = TextStyle.Medium(14f, Palette.Linen);

    private string[] _names = [];
    private int _current = -1;
    private string _query = "";
    private int _first = -1;
    private bool _hover;
    private bool _open;
    private bool _dirty;

    public Action<int> OnPick { get; set; }

    public RectTransform Root => _root;

    public bool Open => _open;

    public OwnChoice(RectTransform parent, float x, float y, float w, RectTransform popHost)
    {
        _w = w;
        _host = popHost;
        _root = Ui.TL(parent, "select", x, y, w, Choice.H);
        _bg = Ui.Skinned(_root, _normal, Styles.Search.Pad, sliced: true);
        Ui.Img(_root, null, Palette.Clear).raycastTarget = true;
        _label = Ui.Label(_root, "t", _nameStyle, 0f, 14f, (Choice.H - _nameStyle.Size * 1.3f) * 0.5f + _nameStyle.Baseline(1.3f), w - 50f, TextAlignmentOptions.TopLeft);
        _chevron = Ui.Icon(_root, "c", Icons.Get("down"), Palette.Ash, 1f, 0.5f, -21f, 0f, 14f);
        _chevron.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        _chevron.preserveAspect = true;
        var input = _root.gameObject.AddComponent<UIInputHandler>();
        input.m_onPointerEnter = _ => Hover(true);
        input.m_onPointerExit = _ => Hover(false);
        input.m_onLeftClick = _ =>
        {
            if (_open)
            {
                Close();
            }
            else
            {
                Show();
            }
        };
        _pop = Ui.TL(popHost != null ? popHost : parent, "pop", x, y + Choice.H + 4f, w, 100f);
        var solid = new BoxStyle(10f, Palette.Rgba(29, 24, 21, 1f), Palette.Rgba(19, 15, 12, 1f), Palette.BrassLine32);
        Ui.Panel(_pop, Skin.Sliced(48, solid), solid, solid: true);
        _search = new SearchBox(_pop, "q", w - 16f);
        _search.Root.anchoredPosition = new Vector2(8f, -8f);
        _search.Changed += value =>
        {
            _query = (value ?? "").Trim();
            _dirty = true;
        };
        _search.Submitted += _ =>
        {
            if (_open && _first >= 0)
            {
                Pick(_first);
            }
        };
        _scroll = new ScrollBox(_pop, "list", w - 16f, RowH, RowH * 2f);
        _scroll.Place(8f, ListTop, w - 16f, RowH, 0f);
        _empty = Ui.Label(_pop, "empty", _itemStyle, 0.5f, 0f, ListTop + (RowH - _itemStyle.Size * 1.3f) * 0.5f + _itemStyle.Baseline(1.3f), w - 20f, TextAlignmentOptions.Top);
        _empty.color = Palette.Rgba(169, 156, 134, 0.8f);
        _pop.gameObject.SetActive(false);
    }

    private void Hover(bool on)
    {
        _hover = on;
        _bg.sprite = _open || _hover ? _hot : _normal;
    }

    public void BindNames(string[] names, int current)
    {
        _names = names ?? [];
        _current = current;
        _dirty = true;
    }

    public void SetCurrent(string name)
    {
        if (_label.text != name)
        {
            _label.text = name;
        }
    }

    private void Show()
    {
        _open = true;
        _query = "";
        _search.SetPlaceholder("Search");
        _search.Clear();
        _pop.gameObject.SetActive(true);
        _pop.SetAsLastSibling();
        _chevron.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 180f);
        Hover(_hover);
        Dock();
        Refill();
        _search.Focus();
    }

    public bool Close()
    {
        if (!_open)
        {
            return false;
        }
        _open = false;
        _search.Blur();
        _pop.gameObject.SetActive(false);
        _chevron.rectTransform.localRotation = Quaternion.identity;
        Hover(_hover);
        return true;
    }

    // With a host the list lives on the host's canvas, above whatever it would otherwise be drawn under, and follows the box wherever the box moves.
    private void Dock()
    {
        if (_host == null)
        {
            return;
        }
        _root.GetWorldCorners(Corners);
        Vector2 local = _host.InverseTransformPoint(Corners[0]);
        var rect = _host.rect;
        _pop.anchoredPosition = local - new Vector2(rect.xMin, rect.yMax) + new Vector2(0f, -4f);
    }

    public void Tick(Component owner)
    {
        if (!_open)
        {
            return;
        }
        Dock();
        if (_dirty)
        {
            Refill();
        }
        _search.Tick(false);
        _scroll.Tick(owner);
        if (ZInput.GetMouseButtonDown(0))
        {
            Vector2 at = ZInput.pointerPosition;
            if (!RectTransformUtility.RectangleContainsScreenPoint(_pop, at, null) && !RectTransformUtility.RectangleContainsScreenPoint(_root, at, null))
            {
                Close();
            }
        }
    }

    private void Refill()
    {
        _dirty = false;
        foreach (var row in _rows)
        {
            UnityEngine.Object.Destroy(row);
        }
        _rows.Clear();
        _first = -1;
        var query = _query.ToLowerInvariant();
        var y = 0f;
        for (var i = 0; i < _names.Length; i++)
        {
            if (query.Length > 0 && _names[i].ToLowerInvariant().IndexOf(query, StringComparison.Ordinal) < 0)
            {
                continue;
            }
            if (_first < 0)
            {
                _first = i;
            }
            Row(i, y);
            y += RowH;
        }
        var listH = Mathf.Clamp(y, RowH, MaxListH);
        _empty.gameObject.SetActive(_rows.Count == 0);
        _empty.text = _rows.Count == 0 ? Lang.F("Choice.NothingMatches", "Nothing matches \"{0}\"", _query) : "";
        _scroll.Place(8f, ListTop, _w - 16f, listH, 0f);
        _scroll.SetContentHeight(y);
        _scroll.ScrollTop();
        _pop.sizeDelta = new Vector2(_w, ListTop + listH + 8f);
    }

    private void Row(int index, float y)
    {
        var current = index == _current;
        var root = Ui.TL(_scroll.Content, "r", 0f, y, _w - 16f, RowH);
        var hover = Skin.Sliced(30, Styles.RowHover);
        var bg = Ui.Skinned(root, Skin.Sliced(30, Styles.RowOn), Styles.RowOn.Pad, sliced: true);
        bg.enabled = current;
        Ui.Img(root, null, Palette.Clear).raycastTarget = true;
        var text = Ui.Label(root, "t", _itemStyle, 0f, 12f, (RowH - _itemStyle.Size * 1.3f) * 0.5f + _itemStyle.Baseline(1.3f), _w - 44f, TextAlignmentOptions.TopLeft);
        text.text = _names[index];
        text.color = current ? Palette.Gold : Palette.Linen;
        var input = root.gameObject.AddComponent<UIInputHandler>();
        input.m_onPointerEnter = _ =>
        {
            if (!current)
            {
                bg.sprite = hover;
                bg.enabled = true;
            }
        };
        input.m_onPointerExit = _ =>
        {
            if (!current)
            {
                bg.enabled = false;
            }
        };
        input.m_onLeftClick = _ => Pick(index);
        _rows.Add(root.gameObject);
    }

    private void Pick(int index)
    {
        Close();
        OnPick?.Invoke(index);
    }

    public void Dispose()
    {
        if (_host != null && _pop != null)
        {
            UnityEngine.Object.Destroy(_pop.gameObject);
        }
    }
}
