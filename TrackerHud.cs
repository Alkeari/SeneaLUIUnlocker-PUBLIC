using System.Linq;
using System.Collections.Generic;
using SeneaLUI.Components;
using SeneaLUI.Core;
using SeneaLUI.Hud;
using SeneaLUI.Inv;
using SeneaLUI.Patches;
using SeneaLUI.Theme;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

internal interface ITrackerHud
{
    void Tick();
}

// The Tracker on screen and its Track buttons. One sits in SeneaL UI's crafting window beside its Favorite star, shown exactly when the star is, and marks the recipe on display as tracked (Tracker); one sits on each row of SeneaL UI's Skills window and tracks that skill to its next ten levels; a click on a material in the crafting window tracks having the amount the recipe needs. The Tracker is an element of SeneaL UI's HUD editor drawn like its panels: each tracked recipe with how many to make and every material still needed, have against need, green once enough is at hand.
// While the cursor is free (the inventory or a menu is open) each recipe has buttons to make one more or one fewer, and to stop tracking it.
internal sealed class TrackerHud : ITrackerHud
{
    public const string ElementId = "Tracker";
    private const float Width = 280f;
    private const float Pad = 12f;
    private const float HeadH = 26f;
    private const float LineH = 20f;
    private const int SortingOrder = 31;
    private static readonly Color Enough = Palette.Rgba(140, 204, 110, 1f);

    private CraftView _view;
    private Tb _track;
    private GameObject _canvas;
    private RectTransform _root;
    private bool _fallback;
    private RectTransform _viewport;
    private RectTransform _content;
    private TextMeshProUGUI _pageLabel;
    private int _page;
    private int _pages = 1;
    private float _nextStep;
    private readonly List<(RectTransform Rect, int Page)> _paged = new();
    private readonly List<Rect> _obstacles = new();
    private static readonly Vector3[] Corners = new Vector3[4];
    private static System.Reflection.MethodInfo _bounds;
    private static bool _boundsLooked;
    private Rect _lastSelf;
    private bool? _growsUp;
    private float _nextFill;
    private bool _cursor;
    private IChoice _trophy;
    private System.Collections.Generic.List<(string Prefab, string Label)> _creatures = new();
    private IChoice _piece;
    private System.Collections.Generic.List<(string Prefab, string Label)> _pieces = new();
    private IChoice _kill;
    private System.Collections.Generic.List<(string Prefab, string Label)> _killable = new();
    private IChoice _item;
    private System.Collections.Generic.List<(string Prefab, string Label, int Stack)> _items = new();
    private readonly HashSet<RectTransform> _chipClicks = new();
    private readonly Dictionary<RectTransform, Tb> _skillButtons = new();

    private TrackerHud()
    {
        Canvas.willRenderCanvases += PlaceButton;
    }

    public static ITrackerHud Create() => new TrackerHud();

    private static bool CursorFree => InventoryGui.IsVisible() || Menu.IsVisible() || Minimap.IsOpen();

    public void Tick()
    {
        Button();
        SkillButtons();
        ChipClicks();
        if (_canvas == null && Hud.instance != null)
        {
            Build();
        }
        if (_canvas == null)
        {
            return;
        }
        var editing = SeneaLHud.Editing(ElementMode.Hud);
        var show = Tracker.Enabled && Player.m_localPlayer != null && !Hud.IsUserHidden() && (Tracker.Goals.Count > 0 || editing || CursorFree);
        // SeneaL UI's HUD editor keeps a HUD element off the inventory screen; without the editor the inventory windows would cover the Tracker, so it steps aside the same way.
        show &= !(_fallback && InventoryGui.IsVisible());
        // SeneaL UI's settings screen and mod settings window take the middle of the screen; the Tracker and an open picker would show beside or through them.
        show &= !SeneaLHud.SettingsScreenOpen && !SeneaLHud.SettingsOpen;
        if (_canvas.activeSelf != show)
        {
            _canvas.SetActive(show);
            _nextFill = 0f;
        }
        if (!show)
        {
            return;
        }
        var cursor = CursorFree;
        Raise(_canvas.GetComponent<Canvas>());
        _trophy?.Tick(_canvas.GetComponent<Canvas>());
        _piece?.Tick(_canvas.GetComponent<Canvas>());
        _kill?.Tick(_canvas.GetComponent<Canvas>());
        _item?.Tick(_canvas.GetComponent<Canvas>());
        if (Time.time >= _nextFill || cursor != _cursor)
        {
            _nextFill = Time.time + 1f;
            _cursor = cursor;
            Fill(cursor);
        }
        Scroll(cursor);
    }

    // Goals that do not fit even at the maximum width are shown a page at a time, each page whole goals filling every column: the mouse wheel turns the pages while the cursor is free, and in play they turn by themselves, back to the first after the last.
    private void Scroll(bool cursor)
    {
        var page = _page;
        if (_pages <= 1)
        {
            page = 0;
        }
        else if (cursor)
        {
            var wheel = ZInput.GetMouseScrollWheel();
            if (wheel != 0f && RectTransformUtility.RectangleContainsScreenPoint(_viewport, ZInput.pointerPosition, null))
            {
                page = Mathf.Clamp(page - (int)Mathf.Sign(wheel), 0, _pages - 1);
            }
            _nextStep = Time.time + Tracker.ScrollSeconds;
        }
        else if (Tracker.AutoScroll && Time.time >= _nextStep)
        {
            _nextStep = Time.time + Tracker.ScrollSeconds;
            page = (page + 1) % _pages;
        }
        if (page != _page)
        {
            _page = page;
            ShowPage();
        }
    }

    private void ShowPage()
    {
        foreach (var (rect, page) in _paged)
        {
            if (rect != null)
            {
                rect.gameObject.SetActive(page == _page);
            }
        }
        _pageLabel.gameObject.SetActive(_pages > 1);
        _pageLabel.text = Lang.F("TrackerHud.PageOf", "{0} of {1}", _page + 1, _pages);
    }

    // SeneaL UI's Esc menu covers the screen with a backdrop that closes the menu when clicked, so while the menu is open the Tracker draws on the canvas just above the menu's, where its controls take the click.
    private static void Raise(Canvas canvas)
    {
        var menu = Menu.IsVisible() && Menu.instance != null ? Menu.instance.GetComponentInParent<Canvas>() : null;
        var order = menu == null ? SortingOrder : Mathf.Max(SortingOrder, menu.rootCanvas.sortingOrder + 1);
        if (canvas.sortingOrder != order)
        {
            canvas.sortingOrder = order;
        }
    }

    // The Track button, made once for each crafting window SeneaL UI builds.
    private void Button()
    {
        var view = InventoryGuiPatches.Craft;
        if (view == null || view == _view)
        {
            return;
        }
        _view = view;
        _track = new Tb(view._detailContent, "unlocker.track", "marker", () =>
        {
            Tracker.Toggle(_view._dRecipe, _view._dQuality);
            _nextFill = 0f;
        }, () => Tips.Text(new Tip(), Tracker.Tracked(_view._dRecipe, _view._dQuality) ? Lang.T("TrackerHud.StopTracking", "Stop tracking") : Lang.T("TrackerHud.Track", "Track"), Lang.T("TrackerHud.TrackRecipeTip", "Show this recipe, at the level shown, and the materials it still needs in the Tracker. Favorites stay as they are.")));
        _track.Flat();
        _track.SetLabel(null);
        _track.Root.gameObject.SetActive(false);
    }

    // A Track button on each row of SeneaL UI's Skills window, left of the level, lit while the skill is tracked.
    private void SkillButtons()
    {
        var skills = InventoryGuiPatches.Skills;
        if (skills == null || !Tracker.Enabled)
        {
            return;
        }
        foreach (var row in skills._rows)
        {
            if (row.Root == null || !row.Root.gameObject.activeInHierarchy)
            {
                continue;
            }
            if (!_skillButtons.TryGetValue(row.Root, out var button) || button.Root == null)
            {
                var owner = row;
                button = new Tb(row.Root, "unlocker.track", "marker", () =>
                {
                    if (owner.Skill != null)
                    {
                        Tracker.ToggleSkill(owner.Skill.m_info.m_skill);
                        _nextFill = 0f;
                    }
                }, () => Tips.Text(new Tip(), owner.Skill != null && Tracker.SkillTracked(owner.Skill.m_info.m_skill) ? Lang.T("TrackerHud.StopTracking", "Stop tracking") : Lang.T("TrackerHud.Track", "Track"),
                    Lang.T("TrackerHud.TrackSkillTip", "Show this skill in the Tracker with the next whole ten levels as its goal; the Tracker's - and + change the goal.")));
                button.Flat();
                button.SetLabel(null);
                Ui.Place(button.Root, 1f, 0.5f, -84f, 0f, button.Width, Tb.Height);
                _skillButtons[row.Root] = button;
            }
            button.SetIcon("marker", row.Skill != null && Tracker.SkillTracked(row.Skill.m_info.m_skill));
        }
    }

    // A click on a material of the recipe on display in SeneaL UI's crafting window tracks having the amount the recipe needs of it. SeneaL UI's own handler on the chip keeps its tooltip.
    private void ChipClicks()
    {
        if (_view == null || !Tracker.Enabled)
        {
            return;
        }
        foreach (var chip in _view._chips)
        {
            if (chip?.Root == null || !_chipClicks.Add(chip.Root))
            {
                continue;
            }
            var owner = chip;
            var input = chip.Root.gameObject.AddComponent<UIInputHandler>();
            input.m_onLeftClick = _ =>
            {
                if (owner._station || string.IsNullOrEmpty(owner._lastName) || owner._lastNeed <= 0)
                {
                    return;
                }
                Click.Play();
                Tracker.TrackItemNamed(owner._lastName, owner._lastNeed);
                _nextFill = 0f;
            };
        }
    }

    // SeneaL UI's Favorite star, which 1.1.7 does not have: there the button stands at the right of the recipe's name.
    private static readonly System.Reflection.FieldInfo FavoriteField = HarmonyLib.AccessTools.Field(typeof(CraftView), "_pin");

    private static Tb FavoriteStar(CraftView view) => FavoriteField?.GetValue(view) as Tb;

    // Right before drawing, after SeneaL UI has laid out the recipe's details: beside the Favorite star, and the recipe's name kept clear of it.
    private void PlaceButton()
    {
        if (_view == null || _track == null || _track.Root == null)
        {
            return;
        }
        var pin = FavoriteStar(_view);
        var star = pin != null && pin.Root.gameObject.activeSelf;
        var item = _view._dItem;
        var upgrade = !_view._tab && item != null && !_view._rec && !_view._imp && !_view._sock && _view._dQuality <= item.m_shared.m_maxQuality;
        var on = Tracker.Enabled && _view._dRecipe != null && (star || upgrade);
        if (_track.Root.gameObject.activeSelf != on)
        {
            _track.Root.gameObject.SetActive(on);
        }
        if (!on)
        {
            return;
        }
        _track.SetIcon("marker", Tracker.Tracked(_view._dRecipe, _view._dQuality));
        // The upgrade tab has no Favorite star, so the button takes the star's place at the right.
        var x = star ? pin.Root.anchoredPosition.x - _track.Width - 6f : _view._detailW - _track.Width;
        _track.Root.anchoredPosition = new Vector2(x, -12f);
        var name = _view._dName.rectTransform;
        var room = x - 8f - name.anchoredPosition.x;
        if (name.sizeDelta.x > room)
        {
            name.sizeDelta = new Vector2(room, name.sizeDelta.y);
        }
    }

    private void Build()
    {
        _canvas = new GameObject("SeneaLUIUnlocker_Tracker", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        Object.DontDestroyOnLoad(_canvas);
        var canvas = _canvas.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = SortingOrder;
        var scaler = _canvas.GetComponent<CanvasScaler>();
        scaler.referencePixelsPerUnit = SkinScale.Ppu;
        ResolutionScale.Track(scaler);
        _root = Ui.Node(_canvas.transform, "panel");
        _root.anchorMin = _root.anchorMax = new Vector2(0.80f, 0.70f);
        _root.pivot = new Vector2(0f, 1f);
        _root.anchoredPosition = Vector2.zero;
        _root.localScale = Vector3.one * HudRoot.Scale;
        _root.sizeDelta = new Vector2(Width, 60f);
        Ui.Panel(_root, Skin.Sliced(48, Styles.Glass78), Styles.Glass78);
        var title = Ui.Label(_root, "title", TextStyles.CardTitle, 0f, Pad, 10f + TextStyles.CardTitle.Baseline(1.3f), Width - Pad * 2f, TextAlignmentOptions.TopLeft);
        title.text = Lang.T("TrackerHud.Tracker", "Tracker");
        title.raycastTarget = false;
        _pageLabel = Ui.Label(_root, "page", TextStyles.CardLabel, 0f, Width - Pad - 80f, 10f + TextStyles.CardLabel.Baseline(1.3f), 80f, TextAlignmentOptions.TopRight);
        _pageLabel.raycastTarget = false;
        _pageLabel.gameObject.SetActive(false);
        _viewport = Ui.TL(_root, "view", 0f, 36f, Width, 10f);
        _viewport.gameObject.AddComponent<RectMask2D>();
        _content = Ui.TL(_viewport, "content", 0f, 0f, Width, 10f);
        _fallback = SeneaLHud.Register(ElementId, "Tracker", _root) == null;
        _canvas.SetActive(false);
    }

    private void Fill(bool cursor)
    {
        for (var i = _content.childCount - 1; i >= 0; i--)
        {
            Object.Destroy(_content.GetChild(i).gameObject);
        }
        var blocks = new List<(RectTransform Rect, float Height)>();
        var goals = Tracker.Report();
        var most = Mathf.Max(1, Tracker.MaxColumns);
        var pickers = cursor ? Mathf.Ceil(4f / most) * (Choice.H + 8f) : 0f;
        var room = Mathf.Max(HeadH + LineH * 4f, Room() - 36f - 8f - pickers);
        if (goals.Count == 0)
        {
            var block = Ui.TL(_content, "b", 0f, 0f, Width, LineH);
            var empty = Ui.Label(block, "empty", TextStyles.PanelDesc, 0f, Pad, TextStyles.PanelDesc.Baseline(1.3f), Width - Pad * 2f, TextAlignmentOptions.TopLeft);
            empty.textWrappingMode = TextWrappingModes.Normal;
            empty.overflowMode = TextOverflowModes.Overflow;
            empty.text = Lang.T("TrackerHud.TrackARecipeOrClick", "Track a recipe or click a material in the crafting window, track a skill from the Skills window, or pick a trophy, building piece, kill count or item below");
            var high = empty.GetPreferredValues(empty.text, Width - Pad * 2f, 0f).y;
            empty.rectTransform.sizeDelta = new Vector2(Width - Pad * 2f, high);
            blocks.Add((block, Mathf.Max(LineH, high + 4f)));
        }
        foreach (var goal in goals)
        {
            var block = Ui.TL(_content, "b", 0f, 0f, Width, 0f);
            var y = 0f;
            var head = Ui.TL(block, "g", 0f, y, Width, HeadH);
            if (goal.Icon != null)
            {
                Ui.Img(Ui.TL(head, "i", Pad, 3f, 20f, 20f), goal.Icon, Color.white).preserveAspect = true;
            }
            var name = Ui.Label(head, "n", TextStyles.CardTitle, 0f, Pad + 26f, (HeadH - TextStyles.CardTitle.Size * 1.3f) * 0.5f + TextStyles.CardTitle.Baseline(1.3f), Width - Pad * 2f - 26f - (cursor ? 70f : 0f), TextAlignmentOptions.TopLeft);
            name.overflowMode = TextOverflowModes.Ellipsis;
            name.text = goal.Amount > 1 ? Lang.F("TrackerHud.NameCount", "{0} ×{1}", goal.Name, goal.Amount) : goal.Name;
            if (cursor)
            {
                var item = goal.Item;
                var crafts = Tracker.Goals.FirstOrDefault(saved => saved.Item == item).Amount;
                Mini(head, "minus", "chev", Width - Pad - 66f, () => Tracker.SetAmount(item, crafts - 1), "Fewer", "Track one fewer of this goal.");
                Mini(head, "plus", "chevr", Width - Pad - 44f, () => Tracker.SetAmount(item, crafts + 1), "More", "Track one more of this goal.");
                Mini(head, "x", "x", Width - Pad - 20f, () => Tracker.SetAmount(item, 0), "Remove", "Stop tracking this goal.");
            }
            y += HeadH;
            Lines(blocks, ref block, ref y, goal.Name, goal.Needs, room);
            // With every line hidden as gathered, the goal says so rather than standing as a bare heading.
            if (goal.Needs.Count > 0 && !goal.Needs.Any(Shown))
            {
                var met = Ui.Label(block, "met", TextStyles.CardLabel, 0f, Pad + 26f, y + (LineH - TextStyles.CardLabel.Size * 1.3f) * 0.5f + TextStyles.CardLabel.Baseline(1.3f), Width - Pad * 2f - 26f, TextAlignmentOptions.TopLeft);
                met.text = Tracker.Gathers(goal.Item) ? Lang.T("TrackerHud.EverythingGathered", "Everything gathered") : Lang.T("TrackerHud.Done", "Done");
                met.color = Palette.Ash;
                y += LineH;
            }
            y += 6f;
            blocks.Add((block, y));
        }
        // With two or more goals needing materials, what they need together.
        var material = goals.Where(goal => goal.Needs.Any(need => need.Icon != null)).ToList();
        if (Tracker.Totals && material.Count >= 2)
        {
            var block = Ui.TL(_content, "b", 0f, 0f, Width, 0f);
            var y = 0f;
            var head = Ui.Label(block, "tot", TextStyles.CardTitle, 0f, Pad, (HeadH - TextStyles.CardTitle.Size * 1.3f) * 0.5f + TextStyles.CardTitle.Baseline(1.3f), Width - Pad * 2f, TextAlignmentOptions.TopLeft);
            head.text = Lang.T("TrackerHud.AllTogether", "All Together");
            y += HeadH;
            var together = material.SelectMany(goal => goal.Needs).Where(need => need.Icon != null).GroupBy(need => need.Name)
                .Select(group => new Tracker.Need(group.First().Name, group.First().Icon, group.First().Have, group.Sum(need => need.Wanted))).ToList();
            Lines(blocks, ref block, ref y, "All Together", together, room);
            y += 6f;
            blocks.Add((block, y));
        }
        var columns = Arrange(blocks, room, cursor ? most : 1, out var height);
        var width = columns * Width;
        _content.sizeDelta = new Vector2(width, height);
        _viewport.sizeDelta = new Vector2(width, height);
        _pageLabel.rectTransform.anchoredPosition = new Vector2(width - Pad - 80f, _pageLabel.rectTransform.anchoredPosition.y);
        ShowPage();
        var pickY = height;
        Pickers(cursor, columns, ref pickY);
        _root.sizeDelta = new Vector2(width, 36f + pickY + 8f);
    }

    // The goals fill one column as far as the room above or below the Tracker allows, then a column at a time is added up to the maximum; goals that still do not fit go on further pages of the same columns. A goal is never split between columns or pages. With the cursor free the Tracker starts at its full width, so its pickers sit side by side and leave the goals more height.
    private int Arrange(List<(RectTransform Rect, float Height)> blocks, float room, int least, out float height)
    {
        var most = Mathf.Max(1, Tracker.MaxColumns);
        var columns = Mathf.Min(least, most);
        while (columns < most && Pack(blocks, room, columns, false, out _) > 1)
        {
            columns++;
        }
        _pages = Pack(blocks, room, columns, true, out height);
        _page = Mathf.Clamp(_page, 0, _pages - 1);
        return columns;
    }

    // Lays the blocks into columns filled up to the limit, starting a new page when every column is full, and returns how many pages that takes; height is the tallest column on any page.
    private int Pack(List<(RectTransform Rect, float Height)> blocks, float limit, int columns, bool place, out float height)
    {
        var column = 0;
        var page = 0;
        var y = 0f;
        height = 0f;
        if (place)
        {
            _paged.Clear();
        }
        foreach (var (rect, high) in blocks)
        {
            if (y > 0f && y + high > limit + 0.5f)
            {
                y = 0f;
                if (++column == columns)
                {
                    column = 0;
                    page++;
                }
            }
            if (place)
            {
                rect.anchoredPosition = new Vector2(column * Width, -y);
                rect.sizeDelta = new Vector2(Width, high);
                _paged.Add((rect, page));
            }
            y += high;
            height = Mathf.Max(height, y);
        }
        return page + 1;
    }

    // The height, in the Tracker's own units, from the edge it grows from to the nearest element in the way, or to the screen edge less the margin every edge element keeps. Elements are SeneaL UI's (read through HudRoot.Bounds where the running build has it) and this plugin's.
    private float Room()
    {
        if (_root == null || !_root.gameObject.activeInHierarchy || _root.sizeDelta.y <= 0f)
        {
            return Screen.height;
        }
        if (_fallback)
        {
            FallbackPlace();
        }
        _root.GetWorldCorners(Corners);
        var self = Rect.MinMaxRect(Corners[0].x, Corners[0].y, Corners[2].x, Corners[2].y);
        var unit = self.height / _root.sizeDelta.y;
        if (unit <= 0f)
        {
            return Screen.height;
        }
        // Everything above or below any of its columns is in the way, so the room is measured across its whole width.
        var left = self.xMin;
        var right = self.xMax;
        Obstacles();
        var margin = Screen.height * 0.025f;
        var gap = 8f * unit;
        // SeneaL UI's Grow setting for the element names the edge it grows from (4 keeps the bottom, 5 the top; a grow corner sets one of the two). Under any other choice the edge that stayed put the last time the Tracker's height changed decides, and until its height has changed, its pivot.
        var grow = GrowSetting();
        if (grow == 4 || grow == 5)
        {
            _growsUp = grow == 4;
        }
        else if (Mathf.Abs(self.height - _lastSelf.height) > 1f)
        {
            if (Mathf.Abs(self.yMin - _lastSelf.yMin) <= 1f)
            {
                _growsUp = true;
            }
            else if (Mathf.Abs(self.yMax - _lastSelf.yMax) <= 1f)
            {
                _growsUp = false;
            }
        }
        var was = _lastSelf.height;
        _lastSelf = self;
        if (_growsUp ?? _root.pivot.y < 0.5f)
        {
            var top = Screen.height - margin;
            foreach (var other in _obstacles)
            {
                if (other.xMax > left + 1f && other.xMin < right - 1f && other.yMin > self.yMin + 2f && !Same(other, self))
                {
                    top = Mathf.Min(top, other.yMin - gap);
                }
            }
            LogRoom($"room up from {self.yMin:0} to {top:0} over x {left:0}..{right:0}: {_obstacles.Count} elements, bounds {(_bounds != null)}, height {self.height:0}", $" was {was:0}");
            return (top - self.yMin) / unit;
        }
        var bottom = margin;
        foreach (var other in _obstacles)
        {
            if (other.xMax > left + 1f && other.xMin < right - 1f && other.yMax < self.yMax - 2f && !Same(other, self))
            {
                bottom = Mathf.Max(bottom, other.yMax + gap);
            }
        }
        LogRoom($"room down from {self.yMax:0} to {bottom:0} over x {left:0}..{right:0}: {_obstacles.Count} elements, bounds {(_bounds != null)}", "");
        return (self.yMax - bottom) / unit;
    }

    private string _roomLogged = "";

    // The room is measured every frame, so only a change is written; the height it had before is left out of the comparison, since it only ever differs on the frame the height changes.
    private void LogRoom(string room, string before)
    {
        if (DebugLog.On && room != _roomLogged)
        {
            _roomLogged = room;
            DebugLog.Write("Tracker", room + before);
        }
    }

    private static System.Reflection.MethodInfo _byKey;
    private static bool _byKeyLooked;

    private static int GrowSetting()
    {
        if (!_byKeyLooked)
        {
            _byKeyLooked = true;
            _byKey = HarmonyLib.AccessTools.Method("SeneaLUI.Core.Prefs:ByKey");
        }
        if (_byKey == null)
        {
            return -1;
        }
        try
        {
            var elem = _byKey.Invoke(null, ["x." + Plugin.Guid + "." + ElementId]);
            var entry = elem == null ? null : HarmonyLib.AccessTools.Field(elem.GetType(), "Grow")?.GetValue(elem) as BepInEx.Configuration.ConfigEntryBase;
            return entry?.BoxedValue is int value ? value : -1;
        }
        catch (System.Exception)
        {
            _byKey = null;
            return -1;
        }
    }

    private static bool Same(Rect a, Rect b) => Mathf.Abs(a.xMin - b.xMin) < 3f && Mathf.Abs(a.yMax - b.yMax) < 3f;

    // Without SeneaL UI's HUD editor (before 1.2.0-preview.10) nothing places the Tracker, so it stands at the right edge under the minimap, time and wind card and task list those builds draw in the top right corner, and under this plugin's cards that stand there too.
    private void FallbackPlace()
    {
        var top = FallbackColumn.Place();
        _root.GetWorldCorners(Corners);
        _root.position += new Vector3(Screen.width - FallbackColumn.Margin - Corners[2].x, top - Corners[2].y, 0f);
    }

    private void Obstacles()
    {
        _obstacles.Clear();
        if (!_boundsLooked)
        {
            _boundsLooked = true;
            _bounds = HarmonyLib.AccessTools.Method("SeneaLUI.Hud.HudRoot:Bounds");
        }
        if (_bounds != null)
        {
            try
            {
                _bounds.Invoke(null, [null, _obstacles]);
            }
            catch (System.Exception)
            {
                _bounds = null;
            }
        }
        foreach (var (id, rect) in SeneaLHud.Registrations)
        {
            if (id == ElementId || rect == null || !rect.gameObject.activeInHierarchy)
            {
                continue;
            }
            rect.GetWorldCorners(Corners);
            _obstacles.Add(Rect.MinMaxRect(Corners[0].x, Corners[0].y, Corners[2].x, Corners[2].y));
        }
    }

    // A goal's lines that run past the room continue in a block of their own under the goal's name, so a column or page never holds more than fits.
    private void Lines(List<(RectTransform Rect, float Height)> blocks, ref RectTransform block, ref float y, string title, IEnumerable<Tracker.Need> needs, float room)
    {
        foreach (var need in needs)
        {
            if (!Shown(need))
            {
                continue;
            }
            if (y + LineH > room)
            {
                blocks.Add((block, y));
                block = Ui.TL(_content, "b", 0f, 0f, Width, 0f);
                var head = Ui.Label(block, "cont", TextStyles.CardTitle, 0f, Pad, (HeadH - TextStyles.CardTitle.Size * 1.3f) * 0.5f + TextStyles.CardTitle.Baseline(1.3f), Width - Pad * 2f, TextAlignmentOptions.TopLeft);
                head.overflowMode = TextOverflowModes.Ellipsis;
                head.text = Lang.F("TrackerHud.Continued", "{0} (continued)", title);
                y = HeadH;
            }
            Need(block, need, ref y);
        }
    }

    private static bool Shown(Tracker.Need need) => !(Tracker.HideGathered && need.Have >= need.Wanted);

    private void Need(RectTransform parent, Tracker.Need need, ref float y)
    {
        if (!Shown(need))
        {
            return;
        }
        var line = Ui.TL(parent, "m", 0f, y, Width, LineH);
        if (need.Icon != null)
        {
            Ui.Img(Ui.TL(line, "i", Pad + 26f, 2f, 16f, 16f), need.Icon, Color.white).preserveAspect = true;
        }
        var label = Ui.Label(line, "n", TextStyles.CardLabel, 0f, Pad + 46f, (LineH - TextStyles.CardLabel.Size * 1.3f) * 0.5f + TextStyles.CardLabel.Baseline(1.3f), Width - Pad * 2f - 46f - 60f, TextAlignmentOptions.TopLeft);
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.text = need.Name;
        var count = Ui.Label(line, "c", TextStyles.CardLabel, 0f, Width - Pad - 60f, (LineH - TextStyles.CardLabel.Size * 1.3f) * 0.5f + TextStyles.CardLabel.Baseline(1.3f), 60f, TextAlignmentOptions.TopRight);
        count.text = $"{Mathf.Min(need.Have, 9999)}/{need.Wanted}";
        count.color = need.Have >= need.Wanted ? Enough : Palette.Parchment;
        y += LineH;
    }

    // While the cursor is free, dropdowns at the bottom track a creature's trophy or a building piece.
    private static void Place(RectTransform picker, int index, int columns, float top) =>
        picker.anchoredPosition = new Vector2(Pad + index % columns * Width, -(36f + top + index / columns * (Choice.H + 8f) + 4f));

    private void Pickers(bool cursor, int columns, ref float y)
    {
        if (cursor)
        {
            var top = y;
            if (_trophy == null)
            {
                _trophy = Choice.Create(_root, Pad, 0f, Width - Pad * 2f, _root);
                Tooltip.Bind(_trophy.Root, () => Tips.Text(new Tip(), Lang.T("TrackerHud.TrackATrophy", "Track a Trophy"), Lang.T("TrackerHud.PickACreatureToTrack", "Pick a creature to track its trophy and your kills of it.")));
                _trophy.OnPick = pick =>
                {
                    if (pick >= 0 && pick < _creatures.Count)
                    {
                        Tracker.TrackTrophy(_creatures[pick].Prefab);
                        _trophy.SetCurrent("Track a Trophy");
                        _nextFill = 0f;
                    }
                };
            }
            if (!_trophy.Open)
            {
                _creatures = Tracker.TrophyCreatures();
                _trophy.BindNames(_creatures.Select(c => c.Label).ToArray(), -1);
                _trophy.SetCurrent("Track a Trophy");
            }
            _trophy.Root.gameObject.SetActive(true);
            Place(_trophy.Root, 0, columns, top);
            if (_piece == null)
            {
                _piece = Choice.Create(_root, Pad, 0f, Width - Pad * 2f, _root);
                Tooltip.Bind(_piece.Root, () => Tips.Text(new Tip(), Lang.T("TrackerHud.TrackABuildingPiece", "Track a Building Piece"), Lang.T("TrackerHud.PickABuildingPieceTo", "Pick a building piece to track the materials it needs.")));
                _piece.OnPick = pick =>
                {
                    if (pick >= 0 && pick < _pieces.Count)
                    {
                        Tracker.TrackPiece(_pieces[pick].Prefab);
                        _piece.SetCurrent("Track a Building Piece");
                        _nextFill = 0f;
                    }
                };
            }
            if (!_piece.Open)
            {
                _pieces = Tracker.KnownPieces();
                _piece.BindNames(_pieces.Select(p => p.Label).ToArray(), -1);
                _piece.SetCurrent("Track a Building Piece");
            }
            _piece.Root.gameObject.SetActive(true);
            Place(_piece.Root, 1, columns, top);
            if (_kill == null)
            {
                _kill = Choice.Create(_root, Pad, 0f, Width - Pad * 2f, _root);
                Tooltip.Bind(_kill.Root, () => Tips.Text(new Tip(), Lang.T("TrackerHud.TrackAKillCount", "Track a Kill Count"), Lang.T("TrackerHud.PickACreatureToCount", "Pick a creature to count how many of it you kill.")));
                _kill.OnPick = pick =>
                {
                    if (pick >= 0 && pick < _killable.Count)
                    {
                        Tracker.TrackKills(_killable[pick].Prefab);
                        _kill.SetCurrent("Track a Kill Count");
                        _nextFill = 0f;
                    }
                };
            }
            if (!_kill.Open)
            {
                _killable = Tracker.Creatures();
                _kill.BindNames(_killable.Select(c => c.Label).ToArray(), -1);
                _kill.SetCurrent("Track a Kill Count");
            }
            _kill.Root.gameObject.SetActive(true);
            Place(_kill.Root, 2, columns, top);
            if (_item == null)
            {
                _item = Choice.Create(_root, Pad, 0f, Width - Pad * 2f, _root);
                Tooltip.Bind(_item.Root, () => Tips.Text(new Tip(), Lang.T("TrackerHud.TrackAnItem", "Track an Item"), Lang.T("TrackerHud.PickAnItemToTrack", "Pick an item to track how many you carry against an amount.")));
                _item.OnPick = pick =>
                {
                    if (pick >= 0 && pick < _items.Count)
                    {
                        Tracker.TrackItem(_items[pick].Prefab, _items[pick].Stack);
                        _item.SetCurrent("Track an Item");
                        _nextFill = 0f;
                    }
                };
            }
            if (!_item.Open)
            {
                _items = Tracker.KnownItems();
                _item.BindNames(_items.Select(i => i.Label).ToArray(), -1);
                _item.SetCurrent("Track an Item");
            }
            _item.Root.gameObject.SetActive(true);
            Place(_item.Root, 3, columns, top);
            y = top + Mathf.Ceil(4f / columns) * (Choice.H + 8f);
        }
        else
        {
            foreach (var select in new[] { _trophy, _piece, _kill, _item })
            {
                if (select != null)
                {
                    select.Close();
                    select.Root.gameObject.SetActive(false);
                }
            }
        }
    }

    private void Mini(RectTransform parent, string name, string icon, float x, System.Action click, string tipTitle, string tipBody)
    {
        var button = Ui.TL(parent, name, x, 2f, 22f, 22f);
        var image = Ui.Icon(button, "i", Icons.Get(icon), Palette.Brass, 0.5f, 0.5f, 0f, 0f, 12f);
        image.preserveAspect = true;
        Ui.Img(button, null, Palette.Clear).raycastTarget = true;
        var input = button.gameObject.AddComponent<UIInputHandler>();
        input.m_onPointerEnter = _ => image.color = Palette.Gold;
        input.m_onPointerExit = _ => image.color = Palette.Brass;
        input.m_onLeftClick = _ =>
        {
            Click.Play();
            click();
            _nextFill = 0f;
        };
        Tooltip.Bind(input, button, () => Tips.Text(new Tip(), tipTitle, tipBody));
    }
}
