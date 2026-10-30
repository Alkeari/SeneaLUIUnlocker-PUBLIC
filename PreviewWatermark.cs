using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// SeneaL UI's preview builds draw their build tag in the bottom right corner (SeneaLUI.Hud.BuildBadge, its own canvas object); a release build has no such object and this does nothing.
internal sealed class PreviewWatermark
{
    private static readonly Type Badge = TypeLookup.Find("SeneaLUI.Hud.BuildBadge");

    private readonly ConfigEntry<bool> _hide;
    private GameObject _badge;
    private float _nextSearch;

    public PreviewWatermark(ConfigFile cfg)
    {
        _hide = cfg.Bind("Interface", "Hide Preview Watermark", true,
            "Hide the build tag SeneaL UI's preview builds show in the bottom right corner (for example SeneaL UI 1.2.0-preview.1). Release builds of SeneaL UI have none.");
    }

    public void Tick(float now)
    {
        if (Badge == null)
        {
            return;
        }
        if (_badge == null)
        {
            if (now < _nextSearch)
            {
                return;
            }
            _nextSearch = now + 5f;
            var component = UnityEngine.Object.FindFirstObjectByType(Badge, FindObjectsInactive.Include) as Component;
            if (component == null)
            {
                return;
            }
            _badge = component.gameObject;
        }
        if (_badge.activeSelf == _hide.Value)
        {
            _badge.SetActive(!_hide.Value);
        }
    }
}
