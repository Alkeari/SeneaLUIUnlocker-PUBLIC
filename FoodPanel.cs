using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using SeneaLUI.Components;
using SeneaLUI.Core;
using SeneaLUI.Hud;
using SeneaLUI.Inv;
using SeneaLUI.Theme;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

internal interface IFoodPanel
{
    void Tick();
}

// The Food window beside the inventory (FoodPlanner), drawn like SeneaL UI's panels and placed in its HUD editor: a focus switch (total, health, stamina, eitr), the best foods you have, and the best you know with a button that sends a missing one's recipe to the Tracker.
internal sealed class FoodPanel : IFoodPanel
{
    public const string ElementId = "FoodPlanner";
    private const float Width = 330f;
    private const float Pad = 14f;
    private const float RowH = 30f;
    private const int Shown = 3;

    private readonly ConfigEntry<bool> _collapsed;
    private GameObject _canvas;
    private RectTransform _root;
    private RectTransform _body;
    private Seg _focus;
    private Image _chev;
    private float _nextFill;

    private FoodPanel(ConfigFile cfg)
    {
        _collapsed = cfg.Bind("Food", "Food Window Collapsed", false, new ConfigDescription(
            "Whether the Food window was folded to its title, kept by the mod.", null, new ConfigurationManagerAttributes { Browsable = false }));
        Canvas.willRenderCanvases += () => WindowCover.Keep(_root);
    }

    public static IFoodPanel Create(ConfigFile cfg) => new FoodPanel(cfg);

    public void Tick()
    {
        if (_canvas == null && Hud.instance != null)
        {
            Build();
        }
        if (_canvas == null)
        {
            return;
        }
        // A Tab element: shown with the player's own inventory (not at a crafting station, whose window takes that side, nor under SeneaL UI's settings screen), and in the HUD editor only in its Tab mode, where the editor opens the inventory itself.
        var open = FoodPlanner.Enabled && Player.m_localPlayer != null && SeneaLHud.InventoryOpen && Player.m_localPlayer.GetCurrentCraftingStation() == null && (!SeneaLHud.EditorOpen || SeneaLHud.Editing(ElementMode.Tab));
        if (_canvas.activeSelf != open)
        {
            _canvas.SetActive(open);
            _nextFill = 0f;
        }
        if (!open)
        {
            return;
        }
        if (Time.time >= _nextFill)
        {
            _nextFill = Time.time + 1f;
            Fill();
        }
    }

    private static string[] FocusNames => [Lang.T("FoodPanel.FocusTotal", "Total"), Lang.T("FoodPanel.FocusHealth", "Health"), Lang.T("FoodPanel.FocusStamina", "Stamina"), Lang.T("FoodPanel.FocusEitr", "Eitr")];
    private static string[] FocusTips =>
    [
        Lang.T("FoodPanel.RankTotal", "Rank foods by everything they give together."),
        Lang.T("FoodPanel.RankHealth", "Rank foods by the health they give."),
        Lang.T("FoodPanel.RankStamina", "Rank foods by the stamina they give."),
        Lang.T("FoodPanel.RankEitr", "Rank foods by the eitr they give.")
    ];

    private void Build()
    {
        _canvas = new GameObject("SeneaLUIUnlocker_Food", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        Object.DontDestroyOnLoad(_canvas);
        var canvas = _canvas.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 27000;
        var scaler = _canvas.GetComponent<CanvasScaler>();
        scaler.referencePixelsPerUnit = SkinScale.Ppu;
        ResolutionScale.Track(scaler);
        _root = Ui.Node(_canvas.transform, "panel");
        // Default spot in the open column between SeneaL UI's inventory (and the item cards under it) and its crafting window; the HUD editor moves it from there.
        _root.anchorMin = _root.anchorMax = new Vector2(0.45f, 0.45f);
        _root.pivot = new Vector2(0f, 1f);
        _root.anchoredPosition = Vector2.zero;
        _root.localScale = Vector3.one * HudRoot.Scale;
        Ui.Panel(_root, Skin.Sliced(48, Styles.Glass78), Styles.Glass78).raycastTarget = true;
        var title = Ui.Label(_root, "title", TextStyles.PanelTitle, 0f, Pad, 12f + TextStyles.PanelTitle.Baseline(1.3f), Width - Pad * 2f - 30f, TextAlignmentOptions.TopLeft);
        title.text = Lang.T("FoodPanel.Food", "Food");
        title.raycastTarget = false;
        var fold = Ui.TL(_root, "fold", Width - Pad - 26f, 10f, 26f, 26f);
        Ui.Img(fold, null, Palette.Clear).raycastTarget = true;
        _chev = Ui.Icon(fold, "c", Icons.Get("down"), Palette.Brass, 0.5f, 0.5f, 0f, 0f, 14f);
        _chev.preserveAspect = true;
        var foldInput = fold.gameObject.AddComponent<UIInputHandler>();
        foldInput.m_onLeftClick = _ =>
        {
            Click.Play();
            _collapsed.Value = !_collapsed.Value;
            _nextFill = 0f;
        };
        Tooltip.Bind(foldInput, fold, () => Tips.Text(new Tip(), _collapsed.Value ? Lang.T("FoodPanel.Show", "Show") : Lang.T("FoodPanel.Fold", "Fold"), Lang.T("FoodPanel.ShowOrFold", "Show or fold the list of foods.")));
        _body = Ui.TL(_root, "body", 0f, 44f, Width, 10f);
        _focus = new Seg(_body, "focus", ["star", "shield", "bolt", "spark"], i =>
        {
            FoodPlanner.Focus = (FoodFocus)i;
            _focus.SetOn(i);
            _nextFill = 0f;
        }, i => Tips.Text(new Tip(), FocusNames[i], FocusTips[i]));
        _focus.SetLabels(["Total", "Health", "Stamina", "Eitr"], false);
        Ui.Place(_focus.Root, 0f, 1f, Pad, 0f, Width - Pad * 2f, 32f);
        _focus.SetOn((int)FoodPlanner.Focus);
        if (SeneaLHud.Register(ElementId, "Food Planner", _root, ElementMode.Tab) == null)
        {
            // SeneaL UI before 1.2.0-preview.10 has no HUD editor and draws a wider inventory window that fills that column, so the panel waits under the inventory, right of the vitals and party buttons and above the key hints, with the Cosmetics and Food Planner side by side.
            _root.anchorMin = _root.anchorMax = new Vector2(0.15f, 0.085f);
            _root.pivot = Vector2.zero;
            _root.anchoredPosition = new Vector2((Width + 12f) * HudRoot.Scale, 0f);
        }
        _canvas.SetActive(false);
    }

    private void Fill()
    {
        var folded = _collapsed.Value;
        _body.gameObject.SetActive(!folded);
        _chev.rectTransform.localRotation = Quaternion.Euler(0f, 0f, folded ? 90f : 0f);
        if (folded)
        {
            _root.sizeDelta = new Vector2(Width, 44f);
            return;
        }
        for (var i = _body.childCount - 1; i >= 0; i--)
        {
            var child = _body.GetChild(i);
            if (child != _focus.Root)
            {
                Object.Destroy(child.gameObject);
            }
        }
        var eaten = FoodPlanner.Eaten();
        var known = FoodPlanner.Known().Where(food => !eaten.Contains(food.Prefab)).ToList();
        var y = 40f;
        y = Section("Best you have", known.Where(food => food.Have > 0).Take(Shown).ToList(), y, false);
        y = Section("Best you know", known.Take(Shown).ToList(), y, true);
        _root.sizeDelta = new Vector2(Width, 44f + y + 8f);
    }

    private float Section(string title, List<FoodPlanner.Food> foods, float y, bool track)
    {
        var head = Ui.Label(_body, "h", TextStyles.CardLabel, 0f, Pad, y + 6f + TextStyles.CardLabel.Baseline(1.3f), Width - Pad * 2f, TextAlignmentOptions.TopLeft);
        head.text = title;
        head.color = Palette.Brass;
        // The numbers on each row are health, stamina and eitr, in these colors.
        var legend = Ui.Label(_body, "k", TextStyles.PanelDesc, 0f, Width - Pad - 200f - (track ? 26f : 0f), y + 6f + TextStyles.PanelDesc.Baseline(1.3f), 200f, TextAlignmentOptions.TopRight);
        legend.richText = true;
        legend.text = Lang.T("FoodPanel.HealthStaminaEitr", "<color=#e0705c>Health</color>  <color=#e8c060>Stamina</color>  <color=#8090f0>Eitr</color>");
        y += 26f;
        if (foods.Count == 0)
        {
            var none = Ui.Label(_body, "n", TextStyles.CardLabel, 0f, Pad, y + TextStyles.CardLabel.Baseline(1.3f), Width - Pad * 2f, TextAlignmentOptions.TopLeft);
            none.text = Lang.T("FoodPanel.None", "None");
            none.color = Palette.Ash;
            return y + RowH;
        }
        foreach (var food in foods)
        {
            var row = Ui.TL(_body, "f", 0f, y, Width, RowH);
            if (food.Icon != null)
            {
                Ui.Img(Ui.TL(row, "i", Pad, 4f, 22f, 22f), food.Icon, Color.white).preserveAspect = true;
            }
            var name = Ui.Label(row, "n", TextStyles.CardLabel, 0f, Pad + 28f, (RowH - TextStyles.CardLabel.Size * 1.3f) * 0.5f + TextStyles.CardLabel.Baseline(1.3f), 150f, TextAlignmentOptions.TopLeft);
            name.overflowMode = TextOverflowModes.Ellipsis;
            name.text = food.Have > 0 && !track ? Lang.F("FoodPanel.NameCount", "{0} ×{1}", food.Name, food.Have) : food.Name;
            var stats = Ui.Label(row, "s", TextStyles.CardLabel, 0f, Width - Pad - 130f - (track ? 26f : 0f), (RowH - TextStyles.CardLabel.Size * 1.3f) * 0.5f + TextStyles.CardLabel.Baseline(1.3f), 130f, TextAlignmentOptions.TopRight);
            stats.text = $"<color=#e0705c>{food.Health:0}</color> <color=#e8c060>{food.Stamina:0}</color> <color=#8090f0>{food.Eitr:0}</color>";
            stats.richText = true;
            // The stats column is sized for its widest figures; the name takes whatever the actual figures leave free, so long names are not cut short.
            var nameWidth = Width - Pad - (track ? 26f : 0f) - stats.GetPreferredValues(stats.text).x - 8f - (Pad + 28f);
            name.rectTransform.sizeDelta = new Vector2(nameWidth, name.rectTransform.sizeDelta.y);
            if (track && food.Have == 0 && food.HasRecipe)
            {
                var button = Ui.TL(row, "t", Width - Pad - 22f, 4f, 22f, 22f);
                var icon = Ui.Icon(button, "i", Icons.Get("marker"), Palette.Brass, 0.5f, 0.5f, 0f, 0f, 12f);
                icon.preserveAspect = true;
                Ui.Img(button, null, Palette.Clear).raycastTarget = true;
                var input = button.gameObject.AddComponent<UIInputHandler>();
                var prefab = food.Prefab;
                input.m_onPointerEnter = _ => icon.color = Palette.Gold;
                input.m_onPointerExit = _ => icon.color = Palette.Brass;
                input.m_onLeftClick = _ =>
                {
                    Click.Play();
                    FoodPlanner.Track(prefab);
                };
                Tooltip.Bind(input, button, () => Tips.Text(new Tip(), Lang.T("FoodPanel.Track", "Track"), Lang.T("FoodPanel.TrackThisFoodSRecipe", "Track this food's recipe and the materials it still needs.")));
            }
            y += RowH;
        }
        return y + 4f;
    }
}
