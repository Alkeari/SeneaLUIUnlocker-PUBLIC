using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

// SeneaL UI's mod settings window (its ModSettings key, F1 by default) only builds once SeneaL UI's in-game HUD exists, so the key does nothing on the main menu.
// Building the window here does what ModsView.Ready does minus that HUD check; the window's own Update then handles the key. Reflection keeps SeneaL UI's code unpatched.
//
// SeneaL UI draws every panel at Skin.Ppu, which HudRoot sets from the HUD canvas before anything is drawn, and caches each drawing by name for every later window. The window is therefore drawn here at the scale its canvas has on the main menu (SeneaL UI's fallback of 50 when no HUD exists), and every drawing it added to the cache is taken out again, so the in-game windows draw their own at the HUD's scale as they do without this mod; drawn into the cache at the default scale instead, they kept the wrong scale and showed wide gaps around their content.
internal sealed class MainMenuModSettings
{
    private const string ModsViewType = "SeneaLUI.Hud.ModsView";
    private const string SkinType = "SeneaLUI.Theme.Skin";
    private const float MenuCanvasPpu = 50f;

    private readonly ManualLogSource _log;
    private readonly FieldInfo _instance;
    private readonly FieldInfo _built;
    private readonly FieldInfo _failed;
    private readonly MethodInfo _build;
    private readonly MethodInfo _patchCm;
    private readonly FieldInfo _cache;
    private readonly FieldInfo _fontsReady;
    private readonly MethodInfo _loadFonts;
    private readonly FieldInfo _dialogTitle;
    private readonly MethodInfo _initStyles;

    private readonly MethodInfo _show;

    private bool _gaveUp;
    private Button _button;

    private MainMenuModSettings(ManualLogSource log, Type modsView, Type skin)
    {
        _log = log;
        _instance = AccessTools.Field(modsView, nameof(SeneaLUI.Hud.ModsView._i));
        _built = AccessTools.Field(modsView, nameof(SeneaLUI.Hud.ModsView._built));
        _failed = AccessTools.Field(modsView, nameof(SeneaLUI.Hud.ModsView._failed));
        _build = AccessTools.Method(modsView, nameof(SeneaLUI.Hud.ModsView.Build), Type.EmptyTypes);
        _patchCm = AccessTools.Method(modsView, nameof(SeneaLUI.Hud.ModsView.PatchCm), Type.EmptyTypes);
        _show = AccessTools.Method(modsView, nameof(SeneaLUI.Hud.ModsView.Show), Type.EmptyTypes);
        _cache = AccessTools.Field(skin, nameof(SeneaLUI.Theme.Skin.Cache));
        var fonts = TypeLookup.Find("SeneaLUI.Theme.Fonts");
        var styles = TypeLookup.Find("SeneaLUI.Theme.TextStyles");
        _fontsReady = fonts == null ? null : AccessTools.Field(fonts, "Ready");
        _loadFonts = fonts == null ? null : AccessTools.Method(fonts, "Load", [typeof(TMPro.TMP_FontAsset)]);
        _dialogTitle = styles == null ? null : AccessTools.Field(styles, "DialogTitle");
        _initStyles = styles == null ? null : AccessTools.Method(styles, "Init", Type.EmptyTypes);
    }

    // SeneaL UI 1.1.x creates its fonts and text styles only when its in-game HUD builds (1.2.0 already does it on the main menu), and the window cannot be built without them: they are made here the way SeneaL UI makes them, from the game's own font, only where they are still missing.
    private void EnsureStyles()
    {
        if (_fontsReady != null && _loadFonts != null && _fontsReady.GetValue(null) is false && FejdStartup.instance.m_versionLabel != null)
        {
            _loadFonts.Invoke(null, [FejdStartup.instance.m_versionLabel.font]);
        }
        if (_dialogTitle != null && _initStyles != null && _dialogTitle.GetValue(null) == null)
        {
            _initStyles.Invoke(null, null);
        }
    }

    private bool Complete => _instance != null && _built != null && _failed != null && _build != null && _patchCm != null && SkinScale.Available && _cache?.GetValue(null) is IDictionary;

    public static MainMenuModSettings Resolve(ManualLogSource log)
    {
        var modsView = TypeLookup.Find(ModsViewType);
        var skin = TypeLookup.Find(SkinType);
        var fix = modsView == null || skin == null ? null : new MainMenuModSettings(log, modsView, skin);
        if (fix == null || !fix.Complete)
        {
            log.LogWarning("This SeneaL UI build does not have the mod settings window this mod builds on the main menu, so its key still works only in a world.");
            return null;
        }
        return fix;
    }

    public void Tick()
    {
        if (_gaveUp || FejdStartup.instance == null)
        {
            return;
        }
        var view = _instance.GetValue(null) as Component;
        if (view == null || (bool)_built.GetValue(view) || (bool)_failed.GetValue(view))
        {
            return;
        }
        var cache = (IDictionary)_cache.GetValue(null);
        var before = new HashSet<object>(cache.Keys.Cast<object>());
        var ppu = SkinScale.Ppu;
        try
        {
            SkinScale.Ppu = MenuCanvasPpu;
            EnsureStyles();
            _build.Invoke(view, null);
            _built.SetValue(view, true);
            _patchCm.Invoke(null, null);
        }
        catch (Exception e)
        {
            // Left unbuilt rather than marked failed, so SeneaL UI still builds it in a world as it always has.
            _gaveUp = true;
            for (var i = view.transform.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(view.transform.GetChild(i).gameObject);
            }
            _log.LogWarning($"Could not build SeneaL UI's mod settings window on the main menu ({e.GetBaseException().Message}); its key works in a world as before.");
        }
        finally
        {
            SkinScale.Ppu = ppu;
            foreach (var key in cache.Keys.Cast<object>().Where(key => !before.Contains(key)).ToList())
            {
                cache.Remove(key);
            }
        }
    }

    // A player who never learned the key still finds the window: a copy of the game's own Settings button, placed under it, opens it.
    public void TickButton(bool wanted)
    {
        var menu = FejdStartup.instance;
        if (menu == null || menu.m_menuList == null)
        {
            return;
        }
        if (_button == null && wanted && !_gaveUp && _show != null)
        {
            _button = AddButton(menu);
        }
        if (_button != null && _button.gameObject.activeSelf != wanted)
        {
            _button.gameObject.SetActive(wanted);
            menu.m_menuButtons = menu.m_menuList.GetComponentsInChildren<Button>();
        }
    }

    private Button AddButton(FejdStartup menu)
    {
        var settings = menu.m_menuList.GetComponentsInChildren<Button>(true).FirstOrDefault(IsSettingsButton);
        if (settings == null)
        {
            _gaveUp = true;
            _log.LogWarning("The main menu has no Settings button to place the Mod Settings button under; SeneaL UI's mod settings key still opens the window there.");
            return null;
        }
        var copy = UnityEngine.Object.Instantiate(settings.gameObject, settings.transform.parent);
        copy.name = "SeneaL UI Mod Settings";
        copy.transform.SetSiblingIndex(settings.transform.GetSiblingIndex() + 1);
        var button = copy.GetComponent<Button>();
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(() => _show.Invoke(null, null));
        foreach (var text in copy.GetComponentsInChildren<TMPro.TMP_Text>(true))
        {
            text.text = Lang.T("MainMenuModSettings.ModSettings", "Mod Settings");
        }
        foreach (var text in copy.GetComponentsInChildren<UnityEngine.UI.Text>(true))
        {
            text.text = Lang.T("MainMenuModSettings.ModSettings", "Mod Settings");
        }
        var tipPrefab = menu.GetComponentsInChildren<UITooltip>(true).Select(t => t.m_tooltipPrefab).FirstOrDefault(p => p != null);
        if (tipPrefab != null)
        {
            var tip = copy.GetComponent<UITooltip>();
            if (tip == null)
            {
                tip = copy.AddComponent<UITooltip>();
            }
            tip.m_tooltipPrefab = tipPrefab;
            tip.m_topic = "";
            tip.m_text = "Open SeneaL UI's mod settings, where every installed mod's settings can be changed.";
        }
        menu.m_menuButtons = menu.m_menuList.GetComponentsInChildren<Button>();
        return button;
    }

    private static bool IsSettingsButton(Button button)
    {
        for (var i = 0; i < button.onClick.GetPersistentEventCount(); i++)
        {
            if (button.onClick.GetPersistentMethodName(i) == nameof(FejdStartup.OnButtonSettings))
            {
                return true;
            }
        }
        return false;
    }
}
