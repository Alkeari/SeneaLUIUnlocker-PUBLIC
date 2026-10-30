using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace SeneaLUIUnlocker;

// SeneaL UI's elements sit on the game's HUD canvas, which GuiScaler scales by min(width / 1920, height / 1080) times the game's GUI scale, so they keep the same share of any screen. This mod's elements draw on canvases of their own; scaled the same way, measured from 2560 x 1440 where they were laid out, they keep their size beside SeneaL UI's at every resolution.
internal static class ResolutionScale
{
    private static readonly AccessTools.FieldRef<float> GuiScale = AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(typeof(GuiScaler), "m_largeGuiScale"));
    private static readonly List<CanvasScaler> Scalers = [];
    private static ConfigEntry<bool> _enabled;

    public static void Bind(ConfigFile cfg)
    {
        _enabled = cfg.Bind("HUD Editor", "Scale With Resolution", true,
            "This mod's HUD elements and windows keep the same share of the screen at every resolution, as SeneaL UI's own do, sized as they are at 2560 x 1440; off, they keep the same size in pixels.");
    }

    public static float Factor
    {
        get
        {
            if (_enabled == null || !_enabled.Value)
            {
                return 1f;
            }
            var gui = GuiScale == null ? 1f : GuiScale();
            return Mathf.Min(Screen.width / 2560f, Screen.height / 1440f) * (gui > 0f ? gui : 1f);
        }
    }

    public static void Track(CanvasScaler scaler)
    {
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = Factor;
        Scalers.Add(scaler);
    }

    public static void Tick()
    {
        var factor = Factor;
        for (var i = Scalers.Count - 1; i >= 0; i--)
        {
            var scaler = Scalers[i];
            if (scaler == null)
            {
                Scalers.RemoveAt(i);
            }
            else if (scaler.scaleFactor != factor)
            {
                scaler.scaleFactor = factor;
            }
        }
    }
}
