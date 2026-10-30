using System;
using System.Collections;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Valheim Terrain Tools writes its brush costs into the game's build panel from a Hud.SetupPieceInfo postfix, which SeneaL UI skips while its own build panel is on. Just before canvases render, after SeneaL UI has filled its requirement chips with the single-use amounts, the chips are filled again with the brush's amounts and laid out again the way SeneaL UI lays them out, since a larger number makes a chip wider.
internal sealed class TerrainToolsCosts
{
    public const string TerrainToolsGuid = "icecub.ValheimTerrainTools";
    private const string Section = "Compatibility";

    private const float PadX = 19f;
    private const float PadY = 17f;
    private const float MinWidth = 440f;
    private const float MaxWidth = 640f;
    private const float ChipHeight = 36f;
    private const float ChipGap = 6f;
    private const float RowStep = 42f;

    private static readonly Type HudRoot = TypeLookup.Find("SeneaLUI.Hud.HudRoot");
    private static readonly Type View = TypeLookup.Find("SeneaLUI.Hud.BuildInfoView");
    private static readonly Type Chip = TypeLookup.Find("SeneaLUI.Components.ReqChip");
    private static readonly Type Stock = TypeLookup.Find("SeneaLUI.Core.Stock");
    private static readonly Type Pov = TypeLookup.Find("SeneaLUI.Mods.Pov");

    private readonly ConfigEntry<bool> _enabled;
    private readonly FieldInfo _build;
    private readonly FieldInfo _shown;
    private readonly FieldInfo _piece;
    private readonly FieldInfo _reqs;
    private readonly FieldInfo _root;
    private readonly FieldInfo _snap;
    private readonly FieldInfo _textW;
    private readonly FieldInfo _titleH;
    private readonly FieldInfo _descH;
    private readonly FieldInfo _have;
    private readonly MethodInfo _fill;
    private readonly PropertyInfo _width;
    private readonly PropertyInfo _isStation;
    private readonly FieldInfo _chipRoot;
    private readonly FieldInfo _stockBuild;
    private readonly MethodInfo _stockCount;
    private readonly MethodInfo _fromChests;
    private readonly PropertyInfo _povOn;
    private readonly MethodInfo _povCost;
    private readonly FieldInfo _instance;
    private readonly FieldInfo _brush;
    private readonly MethodInfo _owns;
    private readonly FieldInfo _costs;
    private readonly FieldInfo _costPiece;
    private readonly FieldInfo _free;
    private readonly FieldInfo _factor;
    private readonly MethodInfo _materialCost;

    private TerrainToolsCosts(ConfigFile cfg, Type plugin, Type brush, Type costs, Type math)
    {
        _enabled = cfg.Bind(Section, "Terrain Tools Brush Costs In Build Panel", true,
            "While a Valheim Terrain Tools brush is the selected piece, SeneaL UI's build panel shows the materials the whole brush costs instead of the single-use amounts.");
        _build = AccessTools.Field(HudRoot, nameof(SeneaLUI.Hud.HudRoot._build));
        _shown = AccessTools.Field(View, nameof(SeneaLUI.Hud.BuildInfoView._shown));
        _piece = AccessTools.Field(View, nameof(SeneaLUI.Hud.BuildInfoView._piece));
        _reqs = AccessTools.Field(View, nameof(SeneaLUI.Hud.BuildInfoView._reqs));
        _root = AccessTools.Field(View, nameof(SeneaLUI.Hud.BuildInfoView._root));
        _snap = AccessTools.Field(View, nameof(SeneaLUI.Hud.BuildInfoView._snap));
        _textW = AccessTools.Field(View, nameof(SeneaLUI.Hud.BuildInfoView._textW));
        _titleH = AccessTools.Field(View, nameof(SeneaLUI.Hud.BuildInfoView._titleH));
        _descH = AccessTools.Field(View, nameof(SeneaLUI.Hud.BuildInfoView._descH));
        _have = AccessTools.Field(View, nameof(SeneaLUI.Hud.BuildInfoView._have));
        _fill = AccessTools.Method(Chip, nameof(SeneaLUI.Components.ReqChip.Fill),
            [typeof(Sprite), typeof(string), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(string), typeof(int)]);
        _width = AccessTools.Property(Chip, nameof(SeneaLUI.Components.ReqChip.Width));
        _isStation = AccessTools.Property(Chip, nameof(SeneaLUI.Components.ReqChip.IsStation));
        _chipRoot = AccessTools.Field(Chip, nameof(SeneaLUI.Components.ReqChip.Root));
        _stockBuild = Stock == null ? null : AccessTools.Field(Stock, nameof(SeneaLUI.Core.Stock.Build));
        _stockCount = Stock == null ? null : AccessTools.Method(Stock, nameof(SeneaLUI.Core.Stock.Count), [typeof(Player), typeof(string), typeof(int)]);
        _fromChests = Stock == null ? null : AccessTools.Method(Stock, nameof(SeneaLUI.Core.Stock.FromChests), [typeof(Player), typeof(string)]);
        _povOn = Pov == null ? null : AccessTools.Property(Pov, nameof(SeneaLUI.Mods.Pov.On));
        _povCost = Pov == null ? null : AccessTools.Method(Pov, nameof(SeneaLUI.Mods.Pov.BuildCost), [typeof(Player), typeof(int)]);
        _instance = AccessTools.Field(plugin, "Instance");
        _brush = AccessTools.Field(plugin, "Brush");
        _owns = AccessTools.Method(brush, "Owns", [typeof(Player), typeof(Piece)]);
        _costs = AccessTools.Field(brush, "_costs");
        _costPiece = AccessTools.Field(costs, "Piece");
        _free = AccessTools.Field(costs, "Free");
        _factor = AccessTools.Field(costs, "Factor");
        _materialCost = AccessTools.Method(math, "MaterialCost", [typeof(int), typeof(double)]);
    }

    private bool Complete() =>
        _build != null && _shown != null && _piece != null && _reqs != null && _root != null && _snap != null && _textW != null
        && _descH != null && _have != null && _fill != null && _width != null && _isStation != null && _chipRoot != null && _stockBuild != null
        && _stockCount != null && _fromChests != null && _instance != null && _brush != null && _owns != null && _costs != null
        && _costPiece != null && _free != null && _factor != null && _materialCost != null;

    public static TerrainToolsCosts Resolve(ConfigFile cfg, BepInEx.Logging.ManualLogSource log)
    {
        if (!Chainloader.PluginInfos.ContainsKey(TerrainToolsGuid))
        {
            return null;
        }
        var plugin = TypeLookup.Find("ValheimTerrainTools.Plugin");
        var brush = TypeLookup.Find("ValheimTerrainTools.BrushController");
        var costs = TypeLookup.Find("ValheimTerrainTools.BrushCosts");
        var math = TypeLookup.Find("ValheimTerrainTools.BrushMath");
        var fix = plugin == null || brush == null || costs == null || math == null || HudRoot == null || View == null || Chip == null
            ? null
            : new TerrainToolsCosts(cfg, plugin, brush, costs, math);
        if (fix == null || !fix.Complete())
        {
            log.LogWarning("Valheim Terrain Tools or SeneaL UI's build panel has an unknown shape; brush costs in SeneaL UI's build panel are off.");
            return null;
        }
        return fix;
    }

    public void BeforeRender()
    {
        if (!_enabled.Value)
        {
            return;
        }
        var player = Player.m_localPlayer;
        var view = _build.GetValue(null);
        if (player == null || view == null || !player.InPlaceMode() || !(bool)_shown.GetValue(view))
        {
            return;
        }
        var piece = _piece.GetValue(view) as Piece;
        var plugin = _instance.GetValue(null) as UnityEngine.Object;
        if (piece == null || plugin == null || !(bool)_owns.Invoke(null, [player, piece]))
        {
            return;
        }
        var costs = _costs.GetValue(_brush.GetValue(plugin));
        if (costs == null || _costPiece.GetValue(costs) as Piece != piece)
        {
            return;
        }
        Fill(player, piece, view, (double)_factor.GetValue(costs), (bool)_free.GetValue(costs));
        Place(view);
    }

    // The same chips in the same order as SeneaL UI's BuildInfoView.Tick, with Valheim Terrain Tools' amount and its own rule for red: the player's inventory holds fewer than the brush uses.
    private void Fill(Player player, Piece piece, object view, double factor, bool free)
    {
        if (_reqs.GetValue(view) is not IList chips || piece.m_resources == null)
        {
            return;
        }
        var pov = _povOn != null && _povCost != null && _povOn.GetValue(null, null) is true;
        var flash = Mathf.Sin(Time.time * 10f) > 0f;
        var haveFormat = _have.GetValue(view) as string;
        var index = 0;
        _stockBuild.SetValue(null, true);
        try
        {
            foreach (var req in piece.m_resources)
            {
                if (req == null || req.m_resItem == null || req.m_resItem.m_itemData == null)
                {
                    continue;
                }
                var single = req.GetAmount(0);
                var listed = pov ? (int)_povCost.Invoke(null, [player, single]) : single;
                if (listed <= 0)
                {
                    continue;
                }
                if (index >= chips.Count)
                {
                    return;
                }
                var chip = chips[index++];
                var shared = req.m_resItem.m_itemData.m_shared;
                var need = (long)_materialCost.Invoke(null, [single, factor]);
                var have = (int)_stockCount.Invoke(null, [player, shared.m_name, -1]);
                var ok = free || player.GetInventory().CountItems(shared.m_name, -1, true) >= need;
                var fromChests = (int)_fromChests.Invoke(null, [player, shared.m_name]);
                _fill.Invoke(chip, [req.m_resItem.m_itemData.GetIcon(), shared.m_name, (int)Math.Min(need, int.MaxValue), have, ok, flash, haveFormat, fromChests]);
            }
        }
        finally
        {
            _stockBuild.SetValue(null, false);
        }
    }

    // BuildInfoView.Place and the size at the end of BuildInfoView.Tick, rerun with the widths the brush amounts give.
    private void Place(object view)
    {
        if (_reqs.GetValue(view) is not IList chips || _root.GetValue(view) is not RectTransform root)
        {
            return;
        }
        var snap = _snap.GetValue(view) as Behaviour;
        var textWidth = 96f + (snap != null && snap.enabled ? 28f : 0f);
        var width = Mathf.Clamp(Mathf.Ceil(textWidth + (float)_textW.GetValue(view)), MinWidth, MaxWidth);
        var text = Mathf.Max(44f, TitleHeight(view) + (float)_descH.GetValue(view) + 2f);
        var x = PadX;
        var y = PadY + text + 10f;
        var rowMax = 0f;
        var shown = 0;
        foreach (var chip in chips)
        {
            if (chip == null || _chipRoot.GetValue(chip) is not RectTransform chipRoot || !chipRoot.gameObject.activeSelf)
            {
                continue;
            }
            shown++;
            var chipWidth = (float)_width.GetValue(chip, null);
            if (x > PadX && ((bool)_isStation.GetValue(chip, null) || x + chipWidth + PadX > MaxWidth))
            {
                x = PadX;
                y += RowStep;
            }
            chipRoot.anchoredPosition = new Vector2(x, -y);
            x += chipWidth + ChipGap;
            rowMax = Mathf.Max(rowMax, x - ChipGap);
        }
        var size = shown == 0
            ? new Vector2(width, PadY + text + PadY)
            : new Vector2(Mathf.Min(MaxWidth, Mathf.Max(width, Mathf.Ceil(rowMax + PadX))), y + ChipHeight + PadY);
        if (root.sizeDelta != size)
        {
            root.sizeDelta = size;
        }
    }

    // SeneaL UI 1.1.x keeps the title on one line and has no measured title height: there it is the panel title style's size times 1.3, as SeneaL UI lays it out.
    private float TitleHeight(object view)
    {
        if (_titleH != null)
        {
            return (float)_titleH.GetValue(view);
        }
        var style = AccessTools.Field(TypeLookup.Find("SeneaLUI.Theme.TextStyles"), "PanelTitle")?.GetValue(null);
        return style == null ? 0f : (float)AccessTools.Field(style.GetType(), "Size").GetValue(style) * 1.3f;
    }
}
