using SeneaLUI.Components;
using SeneaLUI.Inv;
using SeneaLUI.Patches;
using SeneaLUI.Theme;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

internal interface IStashButton
{
    void Tick();
}

// A SeneaL UI button under its inventory window, shown while you carry something that cannot go through a portal: it stashes those items in the nearest chest with room (PortalRules.Stash).
internal sealed class StashButton : IStashButton
{
    private const float Width = 320f;
    private readonly BepInEx.Configuration.ConfigEntry<bool> _enabled;
    private GameObject _canvas;
    private GoldButton _button;

    private StashButton(BepInEx.Configuration.ConfigFile cfg)
    {
        _enabled = cfg.Bind("Portals", "Stash Button", true,
            "While the inventory is open and you carry items that cannot go through a portal, show a button under it that stashes them in the nearest chest with room.");
        Canvas.willRenderCanvases += Place;
    }

    public static IStashButton Create(BepInEx.Configuration.ConfigFile cfg) => new StashButton(cfg);

    public void Tick()
    {
        if (_canvas == null && Hud.instance != null)
        {
            _canvas = new GameObject("SeneaLUIUnlocker_Stash", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Object.DontDestroyOnLoad(_canvas);
            var canvas = _canvas.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 27000;
            _canvas.GetComponent<CanvasScaler>().referencePixelsPerUnit = SkinScale.Ppu;
            ResolutionScale.Track(_canvas.GetComponent<CanvasScaler>());
            _button = new GoldButton(_canvas.transform, "stash", Width, () => PortalRules.Stash(), 34, dark: true, TextStyles.BtnDarkText);
            _button.SetLabel(Lang.T("StashButton.StashItemsThatCannot", "Stash Items That Cannot Teleport"));
            Tooltip.Bind(_button.Root, () => Tips.Text(new Tip(), Lang.T("StashButton.Stash", "Stash"), Lang.T("StashButton.PutEveryItemThatCannot", "Put every item that cannot go through a portal into the nearest chests with room.")));
            _button.Root.pivot = new Vector2(0f, 1f);
            _button.Root.anchorMin = _button.Root.anchorMax = Vector2.zero;
            _canvas.SetActive(false);
        }
        if (_canvas == null)
        {
            return;
        }
        var show = _enabled.Value && ServerRules.Allows("StashButton") && SeneaLHud.InventoryOpen && PortalRules.CarriesBlocked(Player.m_localPlayer) && InventoryGuiPatches.View != null;
        if (_canvas.activeSelf != show)
        {
            _canvas.SetActive(show);
        }
    }

    // Under the lower left corner of SeneaL UI's inventory window, at its scale.
    private void Place()
    {
        if (_canvas == null || !_canvas.activeSelf || InventoryGuiPatches.View?.Panel == null)
        {
            return;
        }
        var panel = InventoryGuiPatches.View.Panel;
        var corners = new Vector3[4];
        panel.GetWorldCorners(corners);
        _button.Root.localScale = Vector3.one * panel.lossyScale.x;
        _button.Root.position = corners[0] + new Vector3(0f, -8f * panel.lossyScale.x, 0f);
    }
}
