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

internal interface ICosmeticsPanel
{
    void Tick();
}

// The Cosmetics window beside the inventory, drawn like SeneaL UI's own panels: one dropdown per armor slot with the armor worn, nothing, and every piece the character knows for that slot (Cosmetics). It is an element of SeneaL UI's HUD editor, placed and sized there, and shows while the inventory is open.
internal sealed class CosmeticsPanel : ICosmeticsPanel
{
    public const string ElementId = "Cosmetics";
    private const float Width = 330f;
    private const float Pad = 14f;
    private const float RowH = 42f;
    private const float TitleH = 44f;
    private const float SelectW = 200f;
    private const int SortingOrder = 27000;

    private readonly ConfigEntry<bool> _collapsed;
    private readonly List<(string Slot, ItemDrop.ItemData.ItemType Type, IChoice Select, List<(string Value, string Label)> Choices)> _rows = [];
    private GameObject _canvas;
    private RectTransform _root;
    private RectTransform _body;
    private Image _chev;
    private bool _wasOpen;

    private CosmeticsPanel(ConfigFile cfg)
    {
        _collapsed = cfg.Bind("Cosmetics", "Cosmetics Window Collapsed", false, new ConfigDescription(
            "Whether the Cosmetics window was folded to its title, kept by the mod.", null, new ConfigurationManagerAttributes { Browsable = false }));
        Canvas.willRenderCanvases += () => WindowCover.Keep(_root);
    }

    public static ICosmeticsPanel Create(ConfigFile cfg) => new CosmeticsPanel(cfg);

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
        var editing = SeneaLHud.EditorOpen;
        // A Tab element: shown with the player's own inventory (not at a crafting station, whose window takes that side, nor under SeneaL UI's settings screen), and in the HUD editor only in its Tab mode, where the editor opens the inventory itself.
        var open = Cosmetics.Enabled && Player.m_localPlayer != null && SeneaLHud.InventoryOpen && Player.m_localPlayer.GetCurrentCraftingStation() == null && (!editing || SeneaLHud.Editing(ElementMode.Tab));
        if (_canvas.activeSelf != open)
        {
            _canvas.SetActive(open);
        }
        if (!open)
        {
            if (_wasOpen)
            {
                foreach (var row in _rows)
                {
                    row.Select.Close();
                }
            }
            _wasOpen = false;
            return;
        }
        if (!_wasOpen)
        {
            Fill();
        }
        _wasOpen = true;
        Fold();
        foreach (var row in _rows)
        {
            row.Select.Tick(_canvas.GetComponent<Canvas>());
        }
    }

    private void Build()
    {
        _canvas = new GameObject("SeneaLUIUnlocker_Cosmetics", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        Object.DontDestroyOnLoad(_canvas);
        var canvas = _canvas.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = SortingOrder;
        var scaler = _canvas.GetComponent<CanvasScaler>();
        scaler.referencePixelsPerUnit = SkinScale.Ppu;
        ResolutionScale.Track(scaler);
        _root = Ui.Node(_canvas.transform, "panel");
        // Default spot in the open column between SeneaL UI's inventory (and the item cards under it) and its crafting window; the HUD editor moves it from there.
        _root.anchorMin = _root.anchorMax = new Vector2(0.45f, 0.86f);
        _root.pivot = new Vector2(0f, 1f);
        _root.anchoredPosition = Vector2.zero;
        _root.localScale = Vector3.one * HudRoot.Scale;
        Ui.Panel(_root, Skin.Sliced(48, Styles.Glass78), Styles.Glass78).raycastTarget = true;
        var title = Ui.Label(_root, "title", TextStyles.PanelTitle, 0f, Pad, 12f + TextStyles.PanelTitle.Baseline(1.3f), Width - Pad * 2f - 30f, TextAlignmentOptions.TopLeft);
        title.text = Lang.T("CosmeticsPanel.Cosmetics", "Cosmetics");
        title.raycastTarget = false;
        var fold = Ui.TL(_root, "fold", Width - Pad - 26f, 10f, 26f, 26f);
        Ui.Img(fold, null, Palette.Clear).raycastTarget = true;
        _chev = Ui.Icon(fold, "c", Icons.Get("down"), Palette.Brass, 0.5f, 0.5f, 0f, 0f, 14f);
        _chev.preserveAspect = true;
        var input = fold.gameObject.AddComponent<UIInputHandler>();
        input.m_onLeftClick = _ =>
        {
            Click.Play();
            _collapsed.Value = !_collapsed.Value;
        };
        input.m_onPointerEnter = _ => _chev.color = Palette.Gold;
        input.m_onPointerExit = _ => _chev.color = Palette.Brass;
        Tooltip.Bind(input, fold, () => Tips.Text(new Tip(), _collapsed.Value ? Lang.T("CosmeticsPanel.Show", "Show") : Lang.T("CosmeticsPanel.Fold", "Fold"), Lang.T("CosmeticsPanel.ShowOrFold", "Show or fold the cosmetic slots.")));
        _body = Ui.TL(_root, "body", 0f, TitleH, Width, Cosmetics.Slots.Length * RowH);
        for (var i = 0; i < Cosmetics.Slots.Length; i++)
        {
            var (slot, type) = Cosmetics.Slots[i];
            var label = Ui.Label(_body, "l" + i, TextStyles.CardLabel, 0f, Pad, i * RowH + (RowH - TextStyles.CardLabel.Size * 1.3f) * 0.5f + TextStyles.CardLabel.Baseline(1.3f), Width - SelectW - Pad * 3f, TextAlignmentOptions.TopLeft);
            label.text = slot;
            label.raycastTarget = false;
            var select = Choice.Create(_body, Width - Pad - SelectW, i * RowH + (RowH - Choice.H) * 0.5f, SelectW, _root);
            var tipSlot = slot;
            Tooltip.Bind(select.Root, () => Tips.Text(new Tip(), tipSlot, Lang.T("CosmeticsPanel.SlotTip", "The piece your character shows in this slot; Armor Worn shows what you wear.")));
            var row = (slot, type, select, new List<(string, string)>());
            var index = i;
            select.OnPick = pick =>
            {
                var choices = _rows[index].Choices;
                if (pick >= 0 && pick < choices.Count)
                {
                    Cosmetics.Choose(_rows[index].Slot, choices[pick].Value);
                    select.SetCurrent(choices[pick].Label);
                }
            };
            _rows.Add(row);
        }
        if (SeneaLHud.Register(ElementId, "Cosmetics", _root, ElementMode.Tab) == null)
        {
            // SeneaL UI before 1.2.0-preview.10 has no HUD editor and draws a wider inventory window that fills that column, so the panel waits under the inventory, right of the vitals and party buttons and above the key hints, with the Cosmetics and Food Planner side by side.
            _root.anchorMin = _root.anchorMax = new Vector2(0.15f, 0.085f);
            _root.pivot = Vector2.zero;
            _root.anchoredPosition = new Vector2(0f, 0f);
        }
        Fold();
        _canvas.SetActive(false);
    }

    // Each slot's list is read again whenever the window opens, since the character learns pieces as it plays.
    private void Fill()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var (slot, type, select, choices) = _rows[i];
            choices.Clear();
            choices.Add((Cosmetics.Worn, "Armor Worn"));
            choices.Add((Cosmetics.Nothing, "Nothing"));
            choices.AddRange(Cosmetics.Known(type));
            var current = Cosmetics.Choice(slot);
            var at = choices.FindIndex(choice => choice.Value == current);
            select.BindNames(choices.Select(choice => choice.Label).ToArray(), at);
            select.SetCurrent(at >= 0 ? choices[at].Label : "Armor Worn");
        }
    }

    private void Fold()
    {
        var folded = _collapsed.Value;
        _root.sizeDelta = new Vector2(Width, TitleH + (folded ? 0f : Cosmetics.Slots.Length * RowH + Pad));
        if (_body.gameObject.activeSelf == !folded)
        {
            return;
        }
        _body.gameObject.SetActive(!folded);
        if (folded)
        {
            foreach (var row in _rows)
            {
                row.Select.Close();
            }
        }
        _chev.rectTransform.localRotation = Quaternion.Euler(0f, 0f, folded ? 90f : 0f);
    }
}
