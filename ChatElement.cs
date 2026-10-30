using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// The chat window as an element of SeneaL UI's HUD editor. The game's chat box (Chat.m_chatWindow) is a screen-sized container, so as an element its frame would cover the whole screen; the editor gets instead a stand-in rectangle the size of the chat's text area (Chat.m_output, where its lines and SeneaL UI's chat panel are drawn), and the whole chat box follows the stand-in's place, size, rotation and fade right before each frame is drawn. SeneaL UI's chat panel is not the reference: it refits itself to the lines and the screen on its own, which would move the chat.
internal sealed class ChatElement
{
    private readonly Chat _chat;
    private readonly RectTransform _box;
    private readonly RectTransform _area;
    private readonly RectTransform _proxy;
    private readonly RectTransform _input;
    private readonly CanvasGroup _boxGroup;
    private readonly Vector3 _boxScale;
    private bool _placed;
    private Vector2 _nativeMin;
    private Vector2 _nativeMax;
    private Vector2 _nativeScreen;
    private ConfigEntryBase _placedX;
    private const float SeneaLMargin = 28f;
    private static readonly System.Reflection.PropertyInfo HudScale = TypeLookup.Find("SeneaLUI.Hud.HudRoot") is { } root ? HarmonyLib.AccessTools.Property(root, "Scale") : null;
    private static readonly Vector3[] Corners = new Vector3[4];

    // Where the chat box is moved from its placed spot this frame, in screen pixels, to clear another mod's panel (TerrainToolsRoom).
    public static Vector3 Nudge { get; set; }

    private static ChatElement _current;

    // How far the chat box sits from its placed spot right now, in screen pixels: the nudge as actually applied, which lags Nudge until the next frame is drawn.
    public static Vector3 Displacement =>
        _current == null || !_current._placed || _current._box == null || _current._proxy == null ? Vector3.zero : _current.UnionMin() - _current._proxy.position;

    private ChatElement(Chat chat, RectTransform area)
    {
        _chat = chat;
        _box = chat.m_chatWindow;
        _area = area;
        _input = chat.m_input == null ? null : chat.m_input.transform as RectTransform;
        _boxScale = _box.localScale;
        _proxy = new GameObject("SeneaLUIUnlocker_ChatFrame", typeof(RectTransform)).GetComponent<RectTransform>();
        _proxy.SetParent(_box.parent, false);
        _proxy.pivot = Vector2.zero;
        _proxy.anchorMin = _proxy.anchorMax = new Vector2(0.5f, 0.5f);
        _proxy.sizeDelta = area.rect.size;
        _proxy.gameObject.SetActive(false);
        _boxGroup = _box.GetComponent<CanvasGroup>();
        if (_boxGroup == null)
        {
            _boxGroup = _box.gameObject.AddComponent<CanvasGroup>();
        }
        Canvas.willRenderCanvases += Follow;
        _current = this;
    }

    public RectTransform Frame => _proxy;

    public static ChatElement Create(Chat chat)
    {
        return chat.m_output == null ? null : new ChatElement(chat, chat.m_output.rectTransform);
    }

    private static Vector3 Corner(RectTransform rect) => rect.TransformPoint(rect.rect.min);

    // Until it is placed in the HUD editor the chat keeps the game's own place, which sits a fixed number of pixels from the screen corner nearest it; scaled, it keeps that corner and its distance from it grows with the scale, as an element placed in the editor would, rather than growing from its own lower left corner off the screen.
    private Vector3 Native(float scale)
    {
        var size = (_nativeMax - _nativeMin) * scale;
        var screen = new Vector2(Screen.width, Screen.height);
        var center = (_nativeMin + _nativeMax) / 2f;
        var x = center.x < _nativeScreen.x / 2f ? _nativeMin.x * scale : screen.x - (_nativeScreen.x - _nativeMax.x) * scale - size.x;
        var y = center.y < _nativeScreen.y / 2f ? _nativeMin.y * scale : screen.y - (_nativeScreen.y - _nativeMax.y) * scale - size.y;
        return new Vector3(x, y, _proxy.position.z);
    }

    private bool PlacedInEditor()
    {
        _placedX ??= SeneaLConfig.File is { } cfg ? SeneaLConfig.Entry(cfg, "Layout", "x.alkeari.valheim.senealuiunlocker.ChatX") : null;
        return _placedX == null || (float)_placedX.BoxedValue >= 0f;
    }

    // SeneaL UI shifts the chat's lines on its own to keep its chat panel 28 units (at its HUD scale) inside the screen, which would draw the chat away from its frame: outside the HUD editor the frame is kept inside that margin, so SeneaL UI never needs to.
    private void KeepInMargin()
    {
        if (SeneaLHud.EditorOpen)
        {
            return;
        }
        var margin = SeneaLMargin * (HudScale?.GetValue(null, null) is float hudScale ? hudScale : 1f);
        _proxy.GetWorldCorners(Corners);
        var min = Vector2.Min(Corners[0], Corners[2]);
        var max = Vector2.Max(Corners[0], Corners[2]);
        var dx = min.x < margin ? margin - min.x : max.x > Screen.width - margin ? Screen.width - margin - max.x : 0f;
        var dy = min.y < margin ? margin - min.y : max.y > Screen.height - margin ? Screen.height - margin - max.y : 0f;
        if (dx != 0f || dy != 0f)
        {
            _proxy.position += new Vector3(dx, dy, 0f);
        }
    }

    // What the chat draws is its lines and, below them, its typing line, which is wider than the lines: the frame covers both, measured in the chat box's own units so it does not depend on how the box is scaled right now.
    private Rect UnionLocal()
    {
        var rect = Local(_area);
        if (_input != null)
        {
            var input = Local(_input);
            rect = Rect.MinMaxRect(Mathf.Min(rect.xMin, input.xMin), Mathf.Min(rect.yMin, input.yMin), Mathf.Max(rect.xMax, input.xMax), Mathf.Max(rect.yMax, input.yMax));
        }
        return rect;
    }

    private Rect Local(RectTransform rect)
    {
        var min = _box.InverseTransformPoint(rect.TransformPoint(rect.rect.min));
        var max = _box.InverseTransformPoint(rect.TransformPoint(rect.rect.max));
        return Rect.MinMaxRect(Mathf.Min(min.x, max.x), Mathf.Min(min.y, max.y), Mathf.Max(min.x, max.x), Mathf.Max(min.y, max.y));
    }

    private Vector3 UnionMin() => _box.TransformPoint(UnionLocal().min);

    private void Follow()
    {
        if (_chat == null || _box == null || _proxy == null || _area == null)
        {
            Canvas.willRenderCanvases -= Follow;
            if (_proxy != null)
            {
                Object.Destroy(_proxy.gameObject);
            }
            return;
        }
        var shown = _box.gameObject.activeInHierarchy;
        // The stand-in takes the chat's own place the first time the chat is shown, once the game has laid it out; until then the chat stays where the game puts it.
        if (!_placed)
        {
            if (!shown)
            {
                return;
            }
            _placed = true;
            _proxy.position = UnionMin();
            _nativeMin = UnionMin();
            _nativeMax = _box.TransformPoint(UnionLocal().max);
            _nativeScreen = new Vector2(Screen.width, Screen.height);
        }
        var factor = ResolutionScale.Factor;
        // The game draws the chat box at its own scale (_boxScale), so the frame takes the text area's size as drawn.
        var size = UnionLocal().size * factor * _boxScale.x;
        if (_proxy.sizeDelta != size)
        {
            _proxy.sizeDelta = size;
        }
        if (!PlacedInEditor())
        {
            _proxy.position = Native(factor * _proxy.localScale.x);
        }
        // The game hides the chat box between messages, but the stand-in stays: an element attached to the chat loses its attachment while the chat's frame is empty and jumps to its own saved spot.
        if (!_proxy.gameObject.activeSelf)
        {
            _proxy.gameObject.SetActive(true);
        }
        KeepInMargin();
        var scale = _proxy.localScale.x;
        var wanted = _boxScale * (scale * factor);
        if (_box.localScale != wanted)
        {
            _box.localScale = wanted;
        }
        if (_box.localRotation != _proxy.localRotation)
        {
            _box.localRotation = _proxy.localRotation;
        }
        var offset = _proxy.position + Nudge - UnionMin();
        if (offset.sqrMagnitude > 0.01f)
        {
            _box.position += offset;
        }
        var fade = _proxy.GetComponent<CanvasGroup>();
        var alpha = fade == null ? 1f : fade.alpha;
        if (!Mathf.Approximately(_boxGroup.alpha, alpha))
        {
            _boxGroup.alpha = alpha;
        }
    }
}
