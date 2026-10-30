using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

// A small text card in SeneaL UI's card look and fonts, on a canvas of its own: an element of SeneaL UI's HUD editor, placed, sized and styled there.
internal sealed class ThemedCard
{
    private const float MaxWidth = 520f;
    private const float PadX = 14f;
    private const float PadY = 9f;

    private readonly Vector2 _anchor;
    private readonly Vector2 _pivot;
    private readonly Vector2 _offset;
    private readonly int _order;
    private readonly float _fontSize;
    private RectTransform _back;
    private TextMeshProUGUI _text;
    private string _id;
    private string _title;
    private string _sample;
    private ElementMode _mode;
    private string _requested;
    private string _shown;
    private float _scale = -1f;

    private static readonly System.Collections.Generic.List<ThemedCard> All = [];

    // The editor opening, closing or changing mode, and a text size set in it, show on every card at once, without waiting for its feature's next update.
    public static void TickAll()
    {
        foreach (var card in All)
        {
            card.Prepare();
            card.Apply();
        }
    }

    public ThemedCard(Vector2 anchor, Vector2 pivot, Vector2 offset, int order, float fontSize = 15f)
    {
        _anchor = anchor;
        _pivot = pivot;
        _offset = offset;
        _order = order;
        _fontSize = fontSize;
    }

    // Makes the card an element of SeneaL UI's HUD editor in the editor mode where it appears; while the editor is open in that mode an empty card shows the sample so it can be found and moved.
    public ThemedCard Element(string id, string title, string sample, ElementMode mode = ElementMode.Hud)
    {
        _id = id;
        _title = title;
        _sample = sample;
        _mode = mode;
        All.Add(this);
        return this;
    }

    // Builds the card, hidden, as soon as the HUD exists, because SeneaL UI's HUD editor adds an element's frame only when it rebuilds, which it does while closed.
    public void Prepare()
    {
        if (_back == null && Hud.instance != null)
        {
            Build();
            _back.gameObject.SetActive(false);
        }
    }

    public RectTransform Rect => _back;

    public void Show(string text)
    {
        _requested = text;
        Prepare();
        Apply();
    }

    private void Apply()
    {
        var text = _requested;
        if (_id != null && SeneaLHud.EditorOpen)
        {
            // In the editor a card shows only in its own mode, and there always, with its sample when it has nothing to say right now.
            text = !SeneaLHud.Editing(_mode) ? null : string.IsNullOrEmpty(text) ? _sample : text;
        }
        // SeneaL API hides an element of its HUD editor along with the HUD; without it (SeneaL UI 1.1.x) the card follows the HUD itself.
        if (_mode == ElementMode.Hud && !SeneaLHud.EditorOpen && Hud.IsUserHidden())
        {
            text = null;
        }
        // The inventory hides the minimap these cards stack under, and covers the corner they would move up into.
        if (FallbackColumn.Holds(_id) && SeneaLHud.Fallback && InventoryGui.IsVisible())
        {
            text = null;
        }
        var show = !string.IsNullOrEmpty(text);
        if (_back == null)
        {
            return;
        }
        if (_back.gameObject.activeSelf != show)
        {
            _back.gameObject.SetActive(show);
        }
        if (!show)
        {
            return;
        }
        var scale = _id == null ? 1f : ElementLook.TextScale(_id);
        if (text == _shown && scale == _scale)
        {
            return;
        }
        _shown = text;
        _scale = scale;
        _text.fontSize = _fontSize * scale;
        _text.text = text;
        // Measured on every change rather than once: the first measure can come before the font has laid the text out.
        _text.ForceMeshUpdate();
        var width = Mathf.Min(_text.GetPreferredValues(text, MaxWidth, 0f).x, MaxWidth);
        var height = _text.GetPreferredValues(text, width, 0f).y;
        _text.rectTransform.sizeDelta = new Vector2(width, height);
        _back.sizeDelta = new Vector2(width + PadX * 2f, height + PadY * 2f);
    }

    private void Build()
    {
        var canvasObject = new GameObject("SeneaLUIUnlocker_Card", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        Object.DontDestroyOnLoad(canvasObject);
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = _order;
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.referencePixelsPerUnit = SkinScale.Ppu;
        ResolutionScale.Track(scaler);
        _back = new GameObject("card", typeof(RectTransform)).GetComponent<RectTransform>();
        _back.SetParent(canvasObject.transform, false);
        _back.anchorMin = _back.anchorMax = _anchor;
        _back.pivot = _pivot;
        _back.anchoredPosition = _offset;
        SeneaLTheme.Back(_back, "Glass78", 48);
        var text = new GameObject("t", typeof(RectTransform));
        text.transform.SetParent(_back, false);
        // Inactive until the font is set, so TextMeshPro never wakes looking for the default font the game does not ship.
        text.SetActive(false);
        _text = text.AddComponent<TextMeshProUGUI>();
        var font = SeneaLTheme.Font("Medium");
        if (font != null)
        {
            _text.font = font;
        }
        text.SetActive(true);
        _text.fontSize = _fontSize;
        _text.richText = true;
        _text.alignment = TextAlignmentOptions.TopLeft;
        _text.color = SeneaLTheme.Color("Parchment", new Color(0.93f, 0.88f, 0.76f));
        _text.raycastTarget = false;
        _text.textWrappingMode = TextWrappingModes.Normal;
        _text.overflowMode = TextOverflowModes.Overflow;
        var textRect = _text.rectTransform;
        textRect.anchorMin = textRect.anchorMax = textRect.pivot = new Vector2(0f, 1f);
        textRect.anchoredPosition = new Vector2(PadX, -PadY);
        if (_id != null)
        {
            SeneaLHud.Register(_id, _title, _back, _mode);
            ElementLook.Own(_id);
        }
    }
}
