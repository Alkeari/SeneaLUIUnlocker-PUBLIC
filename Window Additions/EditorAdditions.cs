using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using SeneaLUI.Components;
using SeneaLUI.Core;
using SeneaLUI.Hud;
using SeneaLUI.Inv;
using SeneaLUI.Theme;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

// Additions inside SeneaL UI's own HUD editor, which stays exactly as SeneaL UI draws and runs it: an Elements button in its bar opens a panel in its own glass style listing every element with search, a show switch and a lock, and the selected element's position and size as numbers. A locked element keeps SeneaL UI's frame and settings but its frame no longer drags or resizes, and a nudge is put back.
internal sealed partial class EditorAdditions : IEditorBridge
{
    private const float PanelWidth = 300f;
    private const float RowHeight = 28f;
    private const float Pad = 12f;
    private const float BarSlot = 478f;
    private const float BarStep = 44f;

    private readonly ConfigEntry<bool> _enabled;
    private readonly ConfigEntry<string> _locked;
    private readonly ConfigEntry<bool> _panelOpen;
    private readonly ConfigEntry<bool> _showOthers;
    private readonly ConfigEntry<bool> _fitFrames;
    private bool _fitBroken;
    private UIInputHandler _othersHooked;
    private readonly ManualLogSource _log;
    private readonly ConfigFile _cfg;

    private const float PanelHeight = 600f;

    private bool _broken;
    private GameObject _canvas;
    private RectTransform _bar;
    private EditTarget[] _targets;
    private float _fitted;
    private Tb _button;
    private RectTransform _panel;
    private SearchBox _search;
    private ScrollBox _scroll;
    private readonly List<Row> _rows = [];
    private Prefs.Elem _demoOn;
    private readonly List<string> _picked = [];
    private ElementSelect _align;
    private bool _alignShown;
    private bool _targetsBroken;
    private OwnerSwitch _modes;
    private RectTransform _modesBar;
    private int _modeShown = -1;
    private bool _other;
    private ElementMode _listedMode = (ElementMode)(-1);
    private bool _panelMoved;
    private RectTransform _inspHooked;
    private Vector2 _inspAt;
    private bool _inspMoved;
    private int _seenSel = -1;
    private string _filter = "";
    private bool _dirty = true;
    private readonly Dictionary<RectTransform, (Action<Vector2> Down, Action<Vector2> Move, Action Up)> _original = [];
    private readonly Dictionary<string, string[]> _lockedPlace = [];

    private EditorAdditions(ConfigFile cfg, ConfigEntry<bool> enabled, ConfigEntry<string> locked, ConfigEntry<bool> panelOpen, ConfigEntry<bool> showOthers, ConfigEntry<bool> fitFrames, ManualLogSource log)
    {
        _cfg = cfg;
        _showOthers = showOthers;
        _fitFrames = fitFrames;
        // Canvases are built after every LateUpdate, so the frames are fitted after SeneaL UI has placed them for the frame, whichever runs first.
        Canvas.willRenderCanvases += FitFrames;
        _enabled = enabled;
        _locked = locked;
        _panelOpen = panelOpen;
        _log = log;
        Canvas.willRenderCanvases += PlaceInspector;
        Canvas.willRenderCanvases += HideForOther;
        Canvas.willRenderCanvases += KeepListsInMode;
    }

    // Everything that names SeneaL UI's editor types lives behind this call, so a SeneaL UI build whose editor differs leaves the editor as SeneaL UI made it.
    public static EditorAdditions Create(ConfigFile cfg, ManualLogSource log)
    {
        const string Section = "HUD Editor";
        var enabled = cfg.Bind(Section, "Editor Additions", true,
            "Add an Elements button to SeneaL UI's HUD editor bar: a list of every element with search, show and lock; clicking one opens its settings panel and shows it with sample content where SeneaL UI has one.");
        var locked = cfg.Bind(Section, "Locked Elements", "", new ConfigDescription(
            "Elements locked in the HUD editor, kept by the mod.", null, new ConfigurationManagerAttributes { Browsable = false }));
        var panelOpen = cfg.Bind(Section, "Elements Panel Open", false, new ConfigDescription(
            "Whether the Elements panel was open, kept by the mod.", null, new ConfigurationManagerAttributes { Browsable = false }));
        var showOthers = cfg.Bind(Section, "Show Other Elements", false, new ConfigDescription(
            "The state of the HUD editor's Others button, kept by the mod so it stays as you set it.", null, new ConfigurationManagerAttributes { Browsable = false }));
        var fitFrames = cfg.Bind(Section, "Frames Match Elements", true,
            "Draw each frame in SeneaL UI's HUD editor exactly around its element instead of 5 units outside it, so elements placed edge to edge show frames that touch rather than overlap.");
        _ = HudEditor.Open;
        ElementContent.Bind(cfg);
        return new EditorAdditions(cfg, enabled, locked, panelOpen, showOthers, fitFrames, log);
    }

    private static HudEditor Editor => HudEditor._i;

    private bool Ready(out HudEditor editor)
    {
        editor = Editor;
        return _enabled.Value && ServerRules.Allows("EditorAdditions") && editor != null && editor._built && editor._targets != null && editor._bar != null;
    }

    public void Tick()
    {
        if (_broken)
        {
            return;
        }
        try
        {
            Step();
        }
        catch (Exception e)
        {
            _broken = true;
            if (_canvas != null)
            {
                _canvas.SetActive(false);
            }
            _log.LogWarning($"The HUD editor additions stopped after an error and stay off until the game restarts; SeneaL UI's editor is unaffected: {e}");
        }
    }

    private void Step()
    {
        if (!Ready(out var editor))
        {
            if (_canvas != null)
            {
                _canvas.SetActive(false);
            }
            if (_button != null && _button.Root != null && !(_enabled.Value && ServerRules.Allows("EditorAdditions")))
            {
                _button.Root.gameObject.SetActive(false);
            }
            return;
        }
        var seneaLList = SeneaLListsObjects;
        // SeneaL UI rebuilds its editor, and with it its bar, whenever another mod's element joins it, which takes this button with it.
        if (!seneaLList && (_button == null || _button.Root == null || _bar != editor._bar))
        {
            BuildButton(editor);
        }
        if (!seneaLList && !_button.Root.gameObject.activeSelf)
        {
            _button.Root.gameObject.SetActive(true);
        }
        // SeneaL UI shows Others only while one element is soloed, so the button takes its place while it is hidden rather than leaving a gap.
        var others = editor._othersBtn == null ? null : editor._othersBtn.Root;
        if (!seneaLList && others != null)
        {
            var x = others.gameObject.activeSelf ? others.anchoredPosition.x + BarStep : others.anchoredPosition.x;
            if (_button.Root.anchoredPosition.x != x)
            {
                _button.Root.anchoredPosition = new Vector2(x, _button.Root.anchoredPosition.y);
            }
        }
        HookOthers(editor);
        KeepOthers(editor);
        HookInspector(editor);
        if (_targets != editor._targets)
        {
            // Each of this plugin's elements gets the full settings panel of SeneaL UI's own elements (ElementTarget) in place of the plain one SeneaL UI gives an added element.
            if (!_targetsBroken)
            {
                try
                {
                    for (var i = 0; i < editor._targets.Length; i++)
                    {
                        if (ElementTarget.Ours(editor._targets[i]))
                        {
                            editor._targets[i] = new ElementTarget(((ExtTarget)editor._targets[i])._rig);
                        }
                    }
                }
                catch (Exception e)
                {
                    _targetsBroken = true;
                    _log.LogWarning($"This plugin's elements could not get their full settings panels; they keep SeneaL UI's plain one: {e.GetBaseException().Message}");
                }
            }
            _targets = editor._targets;
            _original.Clear();
            _dirty = true;
        }
        if (_canvas == null)
        {
            BuildPanel(editor);
        }
        Modes(editor);
        var show = !seneaLList && editor._open && _panelOpen.Value;
        if (_canvas.activeSelf != show)
        {
            _canvas.SetActive(show);
        }
        if (!editor._open)
        {
            if (_demoOn != null)
            {
                HudEditor.SetDemo(_demoOn, false);
                _demoOn = null;
            }
            _seenSel = -1;
            return;
        }
        if (seneaLList)
        {
            ExtendObjects(editor);
        }
        else
        {
            _button.SetLit(_panelOpen.Value);
        }
        ShowSample(editor);
        if (!show)
        {
            return;
        }
        Fit(editor);
        _search.Tick(dismiss: false);
        if (_dirty)
        {
            Rebuild(editor);
        }
        foreach (var row in _rows)
        {
            row.Sync(editor, Locked(editor._targets[row.Index]), Picked(editor._targets[row.Index]));
        }
        if (_align != null && _alignShown)
        {
            _align.Tick(_canvas.GetComponent<Canvas>());
        }
        if (_align == null || !_align.Open)
        {
            _scroll.Tick(editor);
        }
    }

    private bool Picked(EditTarget t) => t.E != null && _picked.Contains(t.E.Key);

    private void TogglePick(EditTarget t)
    {
        if (t.E == null)
        {
            return;
        }
        if (!_picked.Remove(t.E.Key))
        {
            _picked.Add(t.E.Key);
        }
        _fitted = -1f;
    }

    private static readonly string[] AlignNames =
    [
        "Align Left Edges", "Align Centers Across", "Align Right Edges", "Align Top Edges", "Align Centers Down", "Align Bottom Edges",
        "Space Evenly Across", "Space Evenly Down", "Clear Picked Elements"
    ];

    // Lines the picked elements up on the first one picked, or spaces them evenly between the outermost two, by moving each one's place as dragging it would.
    private void Arrange(HudEditor editor, int how)
    {
        if (how == 8)
        {
            _picked.Clear();
            _fitted = -1f;
            return;
        }
        var targets = editor._targets.Where(t => Picked(t) && !t.E.Glued).OrderBy(t => _picked.IndexOf(t.E.Key)).ToList();
        if (targets.Count < 2)
        {
            return;
        }
        var frames = targets.Select(t => t.Frame()).ToList();
        var lead = frames[0];
        if (how >= 6)
        {
            var across = how == 6;
            var order = Enumerable.Range(0, targets.Count).OrderBy(i => across ? frames[i].center.x : frames[i].center.y).ToList();
            var first = frames[order[0]];
            var last = frames[order[order.Count - 1]];
            var room = across ? last.xMax - first.xMin : last.yMax - first.yMin;
            var used = order.Sum(i => across ? frames[i].width : frames[i].height);
            var gap = (room - used) / (order.Count - 1);
            var at = across ? first.xMin : first.yMin;
            foreach (var i in order)
            {
                var start = across ? frames[i].xMin : frames[i].yMin;
                Shift(targets[i], across ? new Vector2(at - start, 0f) : new Vector2(0f, at - start));
                at += (across ? frames[i].width : frames[i].height) + gap;
            }
            return;
        }
        for (var i = 1; i < targets.Count; i++)
        {
            var f = frames[i];
            var delta = how switch
            {
                0 => new Vector2(lead.xMin - f.xMin, 0f),
                1 => new Vector2(lead.center.x - f.center.x, 0f),
                2 => new Vector2(lead.xMax - f.xMax, 0f),
                3 => new Vector2(0f, lead.yMax - f.yMax),
                4 => new Vector2(0f, lead.center.y - f.center.y),
                _ => new Vector2(0f, lead.yMin - f.yMin)
            };
            Shift(targets[i], delta);
        }
    }

    // Moves an element by screen pixels through its stored place, which SeneaL UI keeps as a fraction of the screen (Prefs.Elem.PosX/PosY); one still in its default place gets the point SeneaL UI would store for where it is now.
    private static void Shift(EditTarget t, Vector2 delta)
    {
        var e = t.E;
        if (e.PosX == null || e.PosY == null || delta.sqrMagnitude < 0.25f)
        {
            return;
        }
        Vector2 stored;
        if (e.PosX.Value >= 0f && e.PosY.Value >= 0f)
        {
            stored = new Vector2(e.PosX.Value * Screen.width, e.PosY.Value * Screen.height);
        }
        else if (e.Grow != null && e.Grow.Value >= 1)
        {
            stored = e.GrowPoint(t.Frame());
        }
        else if (e.LiveRef != null)
        {
            stored = e.LiveRef();
        }
        else
        {
            return;
        }
        var moved = stored + delta;
        e.PosX.Value = Mathf.Clamp01(moved.x / Screen.width);
        e.PosY.Value = Mathf.Clamp01(moved.y / Screen.height);
    }

    // After SeneaL UI's editor has handled its frames and keys for the frame.
    public void LateTick()
    {
        if (_broken || !Ready(out var editor) || !editor._open || editor._frames == null)
        {
            return;
        }
        KeepOthers(editor);
        for (var i = 0; i < editor._targets.Length && i < editor._frames.Length; i++)
        {
            var locked = Locked(editor._targets[i]);
            Hold(editor, i, locked);
            if (locked)
            {
                KeepPlace(editor._targets[i].E);
            }
            else
            {
                _lockedPlace.Remove(editor._targets[i].E.Key);
            }
        }
    }

    // SeneaL UI places each frame 5 units outside its element's rectangle, so frames of elements that touch overlap by 10; each frame is set back to the rectangle itself, so it outlines exactly what the element draws.
    private void FitFrames()
    {
        var editor = Editor;
        if (_broken || _fitBroken || !_fitFrames.Value || editor == null || !editor._open || editor._frames == null || editor._last == null)
        {
            return;
        }
        try
        {
            for (var i = 0; i < editor._frames.Length; i++)
            {
                Fit(editor, i);
            }
        }
        catch (Exception e)
        {
            _fitBroken = true;
            _log.LogWarning($"Frames Match Elements stopped after an error and stays off until the game restarts: {e.GetBaseException().Message}");
        }
    }

    private static void Fit(HudEditor editor, int i)
    {
        var frame = editor._frames[i];
        if (frame == null || !frame.gameObject.activeSelf || i >= editor._last.Length)
        {
            return;
        }
        var drawn = editor._last[i];
        if (drawn.width <= 0f || drawn.height <= 0f || frame.anchoredPosition == drawn.min && frame.sizeDelta == drawn.size)
        {
            return;
        }
        Ui.Place(frame, 0f, 0f, drawn.xMin, drawn.yMin, drawn.width, drawn.height);
    }

    // SeneaL UI turns "Show other elements" (its Others button) back off whenever an element is focused, left or the editor reopens; only a click on the button changes the state kept here.
    private void HookOthers(HudEditor editor)
    {
        var handler = editor._othersBtn == null || editor._othersBtn.Root == null ? null : editor._othersBtn.Root.GetComponent<UIInputHandler>();
        if (handler == null || handler == _othersHooked)
        {
            return;
        }
        _othersHooked = handler;
        handler.m_onLeftClick += _ =>
        {
            if (Editor != null)
            {
                _showOthers.Value = Editor._showAll;
            }
        };
    }

    private void KeepOthers(HudEditor editor)
    {
        if (editor._open && editor._showAll != _showOthers.Value)
        {
            editor._showAll = _showOthers.Value;
        }
    }

    private ElementMode CurrentMode(HudEditor editor) => !editor._open ? ElementMode.Hud : _other ? ElementMode.Other : editor._tabMode ? ElementMode.Tab : ElementMode.Hud;

    // SeneaL UI's editor switches between its HUD and Tab modes; this switch takes its place in the bar with a third, Other, for this plugin's elements that appear on other screens, such as the Esc menu. In Other mode the editor stays in HUD mode underneath, and only the Other elements' frames are drawn (HideForOther).
    private void Modes(HudEditor editor)
    {
        if (!editor._open)
        {
            _other = false;
        }
        if (_modes == null || _modes.Root == null || _modesBar != editor._bar)
        {
            _modesBar = editor._bar;
            _modes = new OwnerSwitch(editor._bar, "unlocker.mode", [Loc.HudModeHud, Loc.HudModeTab, "Other"], PickMode, 140f);
            Tooltip.Bind(_modes.Root, () => Tips.Text(new Tip(), "Editor Mode", "HUD places what shows in play, Tab what shows with the inventory open, and Other what shows elsewhere, such as the Esc menu."));
            Ui.Place(_modes.Root, 0f, 0.5f, 204f, 0f, _modes.Width, 28f);
            _modeShown = -1;
        }
        if (editor._modeSw != null && editor._modeSw.Root != null && editor._modeSw.Root.gameObject.activeSelf)
        {
            editor._modeSw.Root.gameObject.SetActive(false);
        }
        var mode = CurrentMode(editor);
        SeneaLHud.EditorMode = mode;
        if ((int)mode != _modeShown)
        {
            _modeShown = (int)mode;
            _modes.SetIdx(_modeShown);
            _dirty = true;
        }
        if (editor._open)
        {
            _modes.Tick();
            if (_other)
            {
                SelectInMode(editor, ElementMode.Other);
            }
        }
    }

    private void PickMode(int index)
    {
        var editor = Editor;
        if (editor == null)
        {
            return;
        }
        _other = index == 2;
        editor.SetMode(index == 1);
        if (_other)
        {
            SelectInMode(editor, ElementMode.Other);
        }
        _dirty = true;
    }

    // Keeps the selection on an element of the given mode, picking the first one when the selected element is not.
    private static void SelectInMode(HudEditor editor, ElementMode mode)
    {
        if (editor._sel >= 0 && editor._sel < editor._targets.Length && editor._targets[editor._sel] is ElementTarget current && current.Mode == mode)
        {
            return;
        }
        for (var i = 0; i < editor._targets.Length; i++)
        {
            if (editor._targets[i] is ElementTarget target && target.Mode == mode && target.Present)
            {
                editor.Select(i);
                return;
            }
        }
    }

    // In Other mode the frames of every element of the HUD and Tab modes are hidden right before the frame is drawn, after SeneaL UI's editor has shown them for its HUD mode.
    private void HideForOther()
    {
        var editor = Editor;
        if (!_other || editor == null || !editor._open || editor._frames == null || editor._targets == null)
        {
            return;
        }
        for (var i = 0; i < editor._targets.Length && i < editor._frames.Length; i++)
        {
            if (editor._targets[i] is ElementTarget target && target.Mode == ElementMode.Other)
            {
                continue;
            }
            if (editor._frames[i] != null && editor._frames[i].gameObject.activeSelf)
            {
                editor._frames[i].gameObject.SetActive(false);
                editor._can[i] = false;
            }
            if (editor._handles != null && i < editor._handles.Length && editor._handles[i] != null)
            {
                foreach (var handle in editor._handles[i])
                {
                    if (handle != null && handle.gameObject.activeSelf)
                    {
                        handle.gameObject.SetActive(false);
                    }
                }
            }
        }
    }

    private static readonly System.Reflection.FieldInfo InspectorField = HarmonyLib.AccessTools.Field(typeof(HudEditor), "_insp");
    private static readonly System.Reflection.FieldInfo InspectorMap = InspectorField == null ? null : HarmonyLib.AccessTools.Field(InspectorField.FieldType, "_map");
    private static readonly System.Reflection.FieldInfo InspectorOptions = InspectorField == null ? null : HarmonyLib.AccessTools.Field(InspectorField.FieldType, "_opts");
    private static readonly System.Reflection.FieldInfo AttachCandidates = HarmonyLib.AccessTools.Field(typeof(EditTarget), "Candidates");

    // SeneaL UI's details panel lists every element of the HUD or Tab mode in its element dropdown and its Attach To list, so in Other mode it offered HUD elements such as the Minimap that cannot be picked there: right before drawing, both keep only the elements of the mode in use.
    private void KeepListsInMode()
    {
        var editor = Editor;
        if (_broken || editor == null || !editor._open || editor._targets == null || InspectorField?.GetValue(editor) is not { } inspector)
        {
            return;
        }
        try
        {
            if (InspectorMap?.GetValue(inspector) is List<int> map)
            {
                map.RemoveAll(i => i < 0 || i >= editor._targets.Length || !InMode(editor, editor._targets[i]));
            }
            if (AttachCandidates != null && InspectorOptions?.GetValue(inspector) is List<EditOption> options)
            {
                foreach (var option in options)
                {
                    if (option.Names?.Method.Name == "AttachNames")
                    {
                        AttachInMode(editor, option);
                    }
                }
            }
        }
        catch (Exception e)
        {
            _broken = true;
            _log.LogWarning($"The HUD editor additions stopped after an error and stay off until the game restarts; SeneaL UI's editor is unaffected: {e}");
        }
    }

    // SeneaL UI builds the Attach To names as None, an optional fixed entry (Helm or Crosshair), then each candidate it gathered; the wrapped option shows only the candidates in the mode in use and hands SeneaL UI the index it would have had.
    private void AttachInMode(HudEditor editor, EditOption option)
    {
        var names = option.Names;
        var get = option.GetI;
        var set = option.SetI;
        var kept = new List<int>();
        void Gather()
        {
            var all = names();
            var candidates = AttachCandidates.GetValue(null) as System.Collections.IList;
            var fixedCount = all.Length - (candidates?.Count ?? 0);
            kept.Clear();
            for (var i = 0; i < all.Length; i++)
            {
                if (i < fixedCount || candidates?[i - fixedCount] is not EditTarget target || InMode(editor, target))
                {
                    kept.Add(i);
                }
            }
        }
        option.Names = () =>
        {
            var all = names();
            Gather();
            return kept.Select(i => all[i]).ToArray();
        };
        option.GetI = () =>
        {
            Gather();
            var shown = kept.IndexOf(get());
            return shown < 0 ? 0 : shown;
        };
        option.SetI = shown =>
        {
            Gather();
            set(shown >= 0 && shown < kept.Count ? kept[shown] : 0);
        };
    }

    // Whether a target belongs in the Elements list for the mode in use: one of this plugin's elements by its own mode, one of SeneaL UI's by its HUD or Tab mode, never in Other.
    private bool InMode(HudEditor editor, EditTarget target)
    {
        var mode = CurrentMode(editor);
        if (target is ElementTarget ours)
        {
            return ours.Mode == mode;
        }
        return mode != ElementMode.Other && target.IsTab == (mode == ElementMode.Tab);
    }

    private void BuildButton(HudEditor editor)
    {
        _bar = editor._bar;
        _button = new Tb(editor._bar, "unlocker.elements", "search", () =>
        {
            _panelOpen.Value = !_panelOpen.Value;
            _dirty = true;
        }, () => Tips.Text(new Tip(), "Elements", "Open or close the list of every element, with search, show, lock, lining up and spacing."));
        _button.SetLabel(null);
        // SeneaL UI's last bar button (Others) sits at 434 up to 1.2.0-preview.8 and at 478 from preview.9, so the slot follows it.
        var others = editor._othersBtn == null ? null : editor._othersBtn.Root;
        var slot = others == null ? BarSlot : others.anchoredPosition.x + BarStep;
        Ui.Place(_button.Root, 0f, 0.5f, slot, 0f, _button.Width, 36f);
        // The bar's right-hand buttons (Interfaces, Done) are anchored to its right edge, and from 1.2.0-preview.10 Interfaces starts at 542 of its 800 units, inside this slot: the bar grows just enough to keep them clear.
        var right = slot + _button.Width + 6f;
        var width = _bar.sizeDelta.x;
        var nearest = width;
        foreach (RectTransform child in _bar)
        {
            if (child != _button.Root && child.anchorMin.x >= 1f && child.gameObject.activeSelf)
            {
                nearest = Mathf.Min(nearest, width + child.anchoredPosition.x - child.sizeDelta.x * child.pivot.x);
            }
        }
        if (nearest < right)
        {
            _bar.sizeDelta = new Vector2(width + right - nearest, _bar.sizeDelta.y);
        }
    }

    // The panel lives on a canvas of its own just above SeneaL UI's editor, so SeneaL UI's frames, docking and rebuilds never cover, move or destroy it; it takes the editor's scale and pixel density so it draws like the editor's own panels.
    private void BuildPanel(HudEditor editor)
    {
        _canvas = new GameObject("SeneaLUIUnlocker_EditorPanel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        UnityEngine.Object.DontDestroyOnLoad(_canvas);
        var canvas = _canvas.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var editorCanvas = editor.GetComponent<Canvas>();
        canvas.sortingOrder = (editorCanvas == null ? 30500 : editorCanvas.sortingOrder) + 1;
        var scaler = _canvas.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1f;
        var editorScaler = editor.GetComponent<CanvasScaler>();
        scaler.referencePixelsPerUnit = editorScaler == null ? SkinScale.Ppu : editorScaler.referencePixelsPerUnit;

        _panel = Ui.Node(_canvas.transform, "panel");
        _panel.anchorMin = _panel.anchorMax = new Vector2(0f, 1f);
        _panel.pivot = new Vector2(0f, 1f);
        _panel.anchoredPosition = new Vector2(24f, -120f);
        _panel.sizeDelta = new Vector2(PanelWidth, PanelHeight);
        // The whole panel background is the handle: the rows, boxes and buttons on it take their own clicks first.
        Ui.Panel(_panel, Skin.Sliced(48, Styles.Glass78), Styles.Glass78).raycastTarget = true;
        var drag = _panel.gameObject.AddComponent<DragSurface>();
        var from = Vector2.zero;
        var start = Vector2.zero;
        drag.OnDown = p =>
        {
            from = p;
            start = _panel.anchoredPosition;
        };
        drag.OnMove = p =>
        {
            _panelMoved = true;
            _panel.anchoredPosition = start + (p - from);
        };
        var title = Ui.Label(_panel, "title", TextStyles.PanelTitle, 0f, Pad, 26f, PanelWidth - Pad * 2f, TextAlignmentOptions.Left);
        title.text = "Elements";
        title.raycastTarget = false;
        var copy = new Tb(_panel, "copy", "layers", () => CopyLook(Editor), () => Tips.Text(new Tip(), "Copy Look", "Copy the selected element's look: size, text size, opacity, transparency, background, colors, style, value position, key cap, caption look and animation."));
        copy.SetLabel("Copy");
        var paste = new Tb(_panel, "paste", "grab", () => PasteLook(Editor), () => Tips.Text(new Tip(), "Paste Look", "Give the selected element the look last copied; its place, attachment and content stay as they are."));
        paste.SetLabel("Paste");
        // SeneaL UI lays a labeled button's text out for its full height (Tb.Height), so a shorter button leaves the label below its middle.
        Ui.Place(paste.Root, 1f, 1f, -Pad, -4f, paste.Width, Tb.Height);
        Ui.Place(copy.Root, 1f, 1f, -Pad - paste.Width - 6f, -4f, copy.Width, Tb.Height);
        _search = new SearchBox(_panel, "search", PanelWidth - Pad * 2f);
        Ui.Place(_search.Root, 0f, 1f, Pad, -42f, PanelWidth - Pad * 2f, 32f);
        _search.SetPlaceholder("Search");
        _search.Changed += text =>
        {
            _filter = (text ?? "").Trim().ToLowerInvariant();
            _dirty = true;
        };
        _scroll = new ScrollBox(_panel, "rows", PanelWidth - Pad * 2f, PanelHeight - 80f - Pad);
        _fitted = -1f;
        _dirty = true;
    }

    // The panel takes the editor's current scale, shrinks to the screen's height, and stays fully on screen however it was dragged.
    private void Fit(HudEditor editor)
    {
        var scale = Mathf.Max(0.3f, editor._host.lossyScale.x);
        if (!Mathf.Approximately(_panel.localScale.x, scale))
        {
            _panel.localScale = Vector3.one * scale;
        }
        var height = Mathf.Clamp(Screen.height / scale - 40f, 260f, PanelHeight);
        if (!Mathf.Approximately(height, _fitted))
        {
            _fitted = height;
            _panel.sizeDelta = new Vector2(PanelWidth, height);
            var top = 80f;
            // With two or more elements picked, a dropdown above the list lines them up or spaces them.
            _alignShown = _picked.Count >= 2;
            if (_alignShown && _align == null)
            {
                _align = new ElementSelect(_panel, Pad, 0f, PanelWidth - Pad * 2f, _panel);
                _align.BindNames(AlignNames, -1);
                _align.SetCurrent("Line Up Picked Elements");
                _align.OnPick = i =>
                {
                    Arrange(Editor, i);
                    _align.SetCurrent("Line Up Picked Elements");
                };
            }
            if (_align != null)
            {
                _align._root.gameObject.SetActive(_alignShown);
                if (!_alignShown)
                {
                    _align.Close();
                }
                _align._root.anchoredPosition = new Vector2(Pad, -top);
            }
            if (_alignShown)
            {
                top += ElementSelect.H + 8f;
            }
            _scroll.Place(Pad, top, PanelWidth - Pad * 2f, height - top - Pad);
        }
        var w = PanelWidth * scale;
        var h = height * scale;
        var at = _panel.anchoredPosition;
        var clamped = new Vector2(Mathf.Clamp(at.x, 0f, Mathf.Max(0f, Screen.width - w)), Mathf.Clamp(at.y, -Mathf.Max(0f, Screen.height - h), 0f));
        // Until it is dragged, the panel keeps clear of SeneaL UI's element settings panel and of the selected element, taking whichever side of the screen covers neither.
        if (!_panelMoved)
        {
            var avoid = new List<Rect>(2);
            var insp = editor._insp == null ? null : editor._insp.Root;
            if (insp != null && insp.gameObject.activeInHierarchy)
            {
                avoid.Add(ScreenRect(insp));
            }
            if (editor._sel >= 0 && editor._sel < editor._targets.Length && editor._targets[editor._sel].Editable)
            {
                avoid.Add(editor._targets[editor._sel].Frame());
            }
            bool Clear(float x) => !avoid.Exists(r => r.width > 0f && r.Overlaps(new Rect(x, Screen.height + clamped.y - h, w, h)));
            var left = 24f;
            var right = Mathf.Max(0f, Screen.width - w - 24f);
            if (!Clear(clamped.x))
            {
                clamped.x = Clear(left) ? left : Clear(right) ? right : clamped.x;
            }
        }
        if (clamped != at)
        {
            _panel.anchoredPosition = clamped;
        }
    }

    private static Rect ScreenRect(RectTransform rect)
    {
        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        return Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
    }

    private void Rebuild(HudEditor editor)
    {
        _dirty = false;
        foreach (var row in _rows)
        {
            UnityEngine.Object.Destroy(row.Root.gameObject);
        }
        _rows.Clear();
        _listedMode = CurrentMode(editor);
        var y = 0f;
        // Only the elements of the mode in use that the installed mods and settings offer: an element SeneaL UI does not offer right now (Available) is not listed, since it could not be shown or placed.
        for (var i = 0; i < editor._targets.Length; i++)
        {
            var target = editor._targets[i];
            if (!target.Present || !target.Available || !InMode(editor, target))
            {
                continue;
            }
            // The Crafting window keeps two placements (CraftingPlacement), each listed as its own row.
            var cases = target.E != null && target.E.Key == "Crafting" && CraftingPlacement.Current != null && CraftingPlacement.Current.Enabled ? new bool?[] { false, true } : new bool?[] { null };
            foreach (var station in cases)
            {
                var name = Row.Title(target, station);
                if (_filter.Length > 0 && !name.ToLowerInvariant().Contains(_filter))
                {
                    continue;
                }
                var row = new Row(this, _scroll.Content, i, station, PanelWidth - Pad * 2f - 12f);
                row.Root.anchoredPosition = new Vector2(0f, -y);
                y += RowHeight;
                _rows.Add(row);
            }
        }
        _scroll.SetContentHeight(y);
    }

    private bool Locked(EditTarget t) => LockedKeys().Contains(t.E.Key);

    private HashSet<string> LockedKeys() => _locked.Value.Split(';').Select(k => k.Trim()).Where(k => k.Length > 0).ToHashSet();

    private void ToggleLock(EditTarget t)
    {
        var keys = LockedKeys();
        if (!keys.Remove(t.E.Key))
        {
            keys.Add(t.E.Key);
        }
        _locked.Value = string.Join("; ", keys.OrderBy(k => k));
    }

    // A locked frame still selects on a click, so its settings stay reachable, but SeneaL UI's drag and resize handlers are set aside until it is unlocked.
    private void Hold(HudEditor editor, int i, bool locked)
    {
        var surfaces = new List<RectTransform> { editor._frames[i] };
        if (editor._handles != null && i < editor._handles.Length && editor._handles[i] != null)
        {
            surfaces.AddRange(editor._handles[i]);
        }
        foreach (var rect in surfaces.Where(r => r != null))
        {
            var drag = rect.GetComponent<DragSurface>();
            if (drag == null)
            {
                continue;
            }
            if (locked && !_original.ContainsKey(rect))
            {
                _original[rect] = (drag.OnDown, drag.OnMove, drag.OnUp);
                var index = i;
                drag.OnDown = _ => editor.Select(index);
                drag.OnMove = null;
                drag.OnUp = null;
            }
            else if (!locked && _original.TryGetValue(rect, out var was))
            {
                drag.OnDown = was.Down;
                drag.OnMove = was.Move;
                drag.OnUp = was.Up;
                _original.Remove(rect);
            }
        }
    }

    // SeneaL UI binds a position only for an element it can move and a size only for one it sizes (Prefs.Elem); the chest panel, for one, has no size.
    private static ConfigEntryBase[] Place(Prefs.Elem e) => new ConfigEntryBase[] { e.PosX, e.PosY, e.Size, e.GlueX, e.GlueY }.Where(entry => entry != null).ToArray();

    // SeneaL UI's element settings panel docks itself beside the selected element; dragging its background moves it by an offset kept for the session, put on top of SeneaL UI's own placement right before each frame is drawn.
    private void HookInspector(HudEditor editor)
    {
        var root = editor._insp == null ? null : editor._insp.Root;
        if (root == null || root == _inspHooked)
        {
            return;
        }
        _inspHooked = root;
        if (root.GetComponent<Graphic>() == null)
        {
            Ui.Img(root, null, Palette.Clear).raycastTarget = true;
        }
        var drag = root.gameObject.AddComponent<DragSurface>();
        var from = Vector2.zero;
        var start = Vector2.zero;
        drag.OnDown = p =>
        {
            from = p;
            start = root.anchoredPosition;
        };
        drag.OnMove = p =>
        {
            _inspMoved = true;
            _inspAt = start + (p - from) / Mathf.Max(0.01f, root.lossyScale.x);
        };
    }

    // What makes up an element's look, as SeneaL UI's per-element settings: everything but its place, attachment, visibility and caption text.
    private static readonly string[] LookFields =
    [
        "Size", "TextK", "AlphaK", "BackK", "Variant", "Readout", "CapSide", "CapColor", "CapStyle", "CapSize", "Ticks", "VerticalE", "ReverseE",
        "Fixed", "Angle", "Thick", "LenLo", "LenHi", "LenAt", "TintS", "AccentS", "HideEmptyK", "Anim", "Style", "Transp", "ReadAt", "KeyShow", "Lack"
    ];

    private Dictionary<string, object> _look;
    private string _lookFrom;

    // A short message in the editor's own hint line, for 3.5 seconds as SeneaL UI shows its own.
    private static void Toast(HudEditor editor, string text)
    {
        if (editor == null)
        {
            return;
        }
        editor.SetHint(text);
        editor._toastUntil = Time.unscaledTime + 3.5f;
    }

    private static Prefs.Elem Selected(HudEditor editor) =>
        editor == null || editor._targets == null || editor._sel < 0 || editor._sel >= editor._targets.Length ? null : editor._targets[editor._sel].E;

    private void CopyLook(HudEditor editor)
    {
        var elem = Selected(editor);
        if (elem == null)
        {
            Toast(editor, "Select an element to copy its look");
            return;
        }
        _look = new Dictionary<string, object>();
        _lookFrom = editor._targets[editor._sel].Name;
        foreach (var name in LookFields)
        {
            if (HarmonyLib.AccessTools.Field(typeof(Prefs.Elem), name)?.GetValue(elem) is ConfigEntryBase entry)
            {
                _look[name] = entry.BoxedValue;
            }
        }
        Toast(editor, $"Copied the look of {_lookFrom}");
    }

    private void PasteLook(HudEditor editor)
    {
        var elem = Selected(editor);
        if (elem == null || _look == null)
        {
            Toast(editor, _look == null ? "Copy an element's look first" : "Select an element to paste the look onto");
            return;
        }
        foreach (var pair in _look)
        {
            if (HarmonyLib.AccessTools.Field(typeof(Prefs.Elem), pair.Key)?.GetValue(elem) is ConfigEntryBase entry)
            {
                entry.BoxedValue = pair.Value;
            }
        }
        Toast(editor, $"Gave {editor._targets[editor._sel].Name} the look of {_lookFrom}");
    }

    // SeneaL UI opens the details panel on the side away from the selected element, which for an element on the right puts it under the Elements panel, the very list it was opened from: it moves just past the Elements panel instead.
    private void ClearElementsPanel(RectTransform root)
    {
        if (_panel == null || !_panel.gameObject.activeInHierarchy)
        {
            return;
        }
        var insp = new Vector3[4];
        root.GetWorldCorners(insp);
        var list = new Vector3[4];
        _panel.GetWorldCorners(list);
        var overlaps = insp[0].x < list[2].x && insp[2].x > list[0].x && insp[0].y < list[2].y && insp[2].y > list[0].y;
        if (overlaps)
        {
            root.anchoredPosition += new Vector2((list[2].x + 12f - insp[0].x) / Mathf.Max(0.01f, root.lossyScale.x), 0f);
        }
    }

    private void PlaceInspector()
    {
        var editor = Editor;
        var root = _inspHooked;
        if (root == null || editor == null || editor._insp == null || editor._insp.Root != root || !root.gameObject.activeInHierarchy)
        {
            return;
        }
        // Once dragged, the panel stays where it was put for the session instead of following SeneaL UI's choice of side for each selected element, and is kept on screen.
        if (!_inspMoved)
        {
            ClearElementsPanel(root);
            return;
        }
        root.anchoredPosition = _inspAt;
        var parent = root.parent as RectTransform;
        if (parent == null)
        {
            return;
        }
        var corners = new Vector3[4];
        root.GetWorldCorners(corners);
        var bounds = new Vector3[4];
        parent.GetWorldCorners(bounds);
        var scale = Mathf.Max(0.01f, root.lossyScale.x);
        var shift = Vector2.zero;
        if (corners[2].x > bounds[2].x)
        {
            shift.x = (bounds[2].x - corners[2].x) / scale;
        }
        if (corners[0].x + shift.x * scale < bounds[0].x)
        {
            shift.x = (bounds[0].x - corners[0].x) / scale;
        }
        if (corners[2].y > bounds[2].y)
        {
            shift.y = (bounds[2].y - corners[2].y) / scale;
        }
        if (corners[0].y + shift.y * scale < bounds[0].y)
        {
            shift.y = (bounds[0].y - corners[0].y) / scale;
        }
        if (shift != Vector2.zero)
        {
            _inspAt += shift;
            root.anchoredPosition = _inspAt;
        }
    }

    // The selected element shows SeneaL UI's own sample content (its Show a sample switch, HudEditor.SetDemo) while it is selected, so an element with nothing to show right now is seen as it will look; a sample the player switched on stays on.
    private void ShowSample(HudEditor editor)
    {
        var sel = editor._sel;
        if (sel == _seenSel)
        {
            return;
        }
        _seenSel = sel;
        if (_demoOn != null)
        {
            HudEditor.SetDemo(_demoOn, false);
            _demoOn = null;
        }
        if (sel >= 0 && sel < editor._targets.Length && !HudEditor.Demo(editor._targets[sel].E))
        {
            _demoOn = editor._targets[sel].E;
            HudEditor.SetDemo(_demoOn, true);
        }
    }

    // Arrow keys still reach a selected locked element through SeneaL UI's editor; its stored place is put back.
    private void KeepPlace(Prefs.Elem e)
    {
        var now = Place(e).Select(entry => entry.GetSerializedValue()).ToArray();
        if (!_lockedPlace.TryGetValue(e.Key, out var kept))
        {
            _lockedPlace[e.Key] = now;
            return;
        }
        if (now.SequenceEqual(kept))
        {
            return;
        }
        var entries = Place(e);
        for (var k = 0; k < entries.Length; k++)
        {
            entries[k].SetSerializedValue(kept[k]);
        }
    }

    private sealed class Row
    {
        public readonly RectTransform Root;
        public readonly int Index;
        private readonly bool? _station;
        private readonly TextMeshProUGUI _name;
        private readonly Tb _show;
        private readonly Tb _lock;
        private readonly Tb _pick;

        public Row(EditorAdditions owner, RectTransform parent, int index, bool? station, float width)
        {
            Index = index;
            _station = station;
            Root = Ui.TL(parent, "row" + index, 0f, 0f, width, RowHeight);
            Ui.Img(Root, null, Palette.Clear).raycastTarget = true;
            Root.gameObject.AddComponent<DragSurface>().OnDown = _ =>
            {
                if (station is { } atStation)
                {
                    CraftingPlacement.Current?.Edit(atStation);
                }
                Open(index);
            };
            Tooltip.Bind(Root, () => station switch
            {
                true => Tips.Text(new Tip(), "Crafting: Station", "Select where the Crafting window sits while you use a crafting station, where SeneaL UI draws it wider; it starts where the inventory one is until you move it."),
                false => Tips.Text(new Tip(), "Crafting: Inventory", "Select where the Crafting window sits beside your inventory away from a crafting station."),
                _ => Tips.Text(new Tip(), "Select", "Select this element and open its settings.")
            });
            _name = Ui.Label(Root, "name", TextStyles.CardLabel, 0f, 4f, 19f, width - 116f, TextAlignmentOptions.Left);
            _name.raycastTarget = false;
            // A faint rule under each row lines the name up with its buttons at the right.
            Ui.Img(Ui.TL(Root, "rule", 0f, RowHeight - 1f, width, 1f), null, Palette.Rgba(214, 178, 110, 0.16f)).raycastTarget = false;
            _show = new Tb(Root, "show", "eye", () =>
            {
                var t = Editor._targets[index];
                t.Shown = !t.Shown;
            }, () => Tips.Text(new Tip(), "Show", "Show or hide this element in play."));
            _show.SetLabel(null);
            Ui.Place(_show.Root, 1f, 0.5f, -40f, 0f, 30f, 26f);
            _pick = new Tb(Root, "pick", "check", () => owner.TogglePick(Editor._targets[index]), () => Tips.Text(new Tip(), "Pick", "Pick this element to line it up or space it evenly with the other picked elements."));
            _pick.SetLabel(null);
            Ui.Place(_pick.Root, 1f, 0.5f, -76f, 0f, 30f, 26f);
            _lock = new Tb(Root, "lock", "lock", () => owner.ToggleLock(Editor._targets[index]), () => Tips.Text(new Tip(), "Lock", "Lock this element in place: it can still be selected and set, but not moved."));
            _lock.SetLabel(null);
            Ui.Place(_lock.Root, 1f, 0.5f, -4f, 0f, 30f, 26f);
        }

        // The Crafting window's two placements are named for where the window is drawn: beside the inventory, or at a crafting station, where SeneaL UI draws it wider.
        public static string Title(EditTarget target, bool? station) =>
            station is not { } atStation ? target.Name ?? "" : atStation ? "Crafting: Station" : "Crafting: Inventory";

        // As a right-click on the element's frame does: select it and open its settings panel.
        private static void Open(int index)
        {
            var editor = Editor;
            editor.Select(index);
            editor.Focus();
        }

        public void Sync(HudEditor editor, bool locked, bool picked)
        {
            _pick.SetLit(picked);
            var t = editor._targets[Index];
            _name.text = Title(t, _station);
            var selected = Index == editor._sel && (_station is not { } atStation || CraftingPlacement.Current != null && CraftingPlacement.Current.EditingStation == atStation);
            _name.color = selected ? Palette.Gold : t.Editable ? Palette.Parchment : Palette.Ash;
            _show.SetIcon(t.Shown ? "eye" : "eyeoff", false);
            _lock.SetLit(locked);
        }
    }
}
