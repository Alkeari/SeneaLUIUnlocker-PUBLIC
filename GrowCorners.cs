using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using SeneaLUI.Core;
using SeneaLUI.Hud;
using UnityEngine;

namespace SeneaLUIUnlocker;

internal interface IGrowCorners
{
    void Tick();
    void Shutdown();
}

// How an element grows when its size or content changes, past SeneaL UI's six choices: an element can also grow up and left, up and right, down and left or down and right, keeping the opposite corner where it was put. The crosshair never moves as hover text comes and goes: its reticle stays where it was put.
// SeneaL UI's element settings panel offers the choice as one dropdown of SeneaL UI's own kind. The corner is kept here per element, with SeneaL UI's own Grow setting at up or down beneath it, which is what applies wherever these patches are not.
// The element's stored place stays the middle of its bottom or top edge at the width it had when its corner was chosen (kept here as a share of the screen height, `Key=corner@width`), and the kept corner sits half that width from it: SeneaL UI alone, on a SeneaL UI server where these patches cannot run, then puts the element exactly where it was placed and only its growth differs. An entry without a width keeps the corner itself as the stored place, as entries written before the width did.
// That patches SeneaL UI's code (Prefs.Elem.GrowPoint and EditTarget.Options), which a SeneaL UI server lists in the build proof it asks for, so as with SeneaLKeys the patches exist only while the unlock stands, under their own Harmony id, and a guard on the game's own ZRpc.HandlePackage removes them before a SeneaL UI server's answer reaches SeneaL UI.
internal sealed class GrowCorners : IGrowCorners
{
    private const string PatchId = Plugin.Guid + ".grow";
    private const string GuardId = Plugin.Guid + ".grow.guard";
    private const int UpLeft = 6;
    private const int UpRight = 7;
    private const int DownLeft = 8;
    private const int DownRight = 9;
    private static readonly int PolicyRpc = "SeneaLUI_Policy".GetStableHashCode();
    private static GrowCorners _current;

    private readonly ManualLogSource _log;
    private readonly PolicyUnlock _unlock;
    private readonly ConfigEntry<string> _store;
    private readonly Harmony _patches = new(PatchId);
    private readonly Harmony _guard = new(GuardId);
    private Dictionary<string, int> _corners = new(StringComparer.Ordinal);
    private Dictionary<string, float> _widths = new(StringComparer.Ordinal);
    private bool _eligible;
    private bool _applied;
    private bool _guardApplied;
    private bool _broken;

    private GrowCorners(ConfigFile cfg, ManualLogSource log, PolicyUnlock unlock)
    {
        _log = log;
        _unlock = unlock;
        _store = cfg.Bind("HUD Editor", "Grow Corners", "", new ConfigDescription(
            "Elements set in the HUD editor to grow toward a corner, kept by the mod.", null, new ConfigurationManagerAttributes { Browsable = false }));
        Load();
        _store.SettingChanged += (_, _) => Load();
        unlock.Granted += _ =>
        {
            _eligible = true;
            Tick();
        };
    }

    public static IGrowCorners Create(ConfigFile cfg, ManualLogSource log, PolicyUnlock unlock)
    {
        _ = typeof(Prefs.Elem).GetMethod(nameof(Prefs.Elem.GrowPoint)) ?? throw new MissingMethodException("Prefs.Elem.GrowPoint");
        return _current = new GrowCorners(cfg, log, unlock);
    }

    private void Load()
    {
        _corners = new Dictionary<string, int>(StringComparer.Ordinal);
        _widths = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var part in (_store.Value ?? "").Split(';'))
        {
            var eq = part.LastIndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var value = part.Substring(eq + 1).Split('@');
            if (int.TryParse(value[0], out var corner) && corner >= UpLeft && corner <= DownRight)
            {
                var key = part.Substring(0, eq);
                _corners[key] = corner;
                if (value.Length > 1 && float.TryParse(value[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var width) && width > 0f)
                {
                    _widths[key] = width;
                }
            }
        }
    }

    private void Save() => _store.Value = string.Join(";", _corners.Select(pair =>
        _widths.TryGetValue(pair.Key, out var width) ? $"{pair.Key}={pair.Value}@{width.ToString("0.#######", CultureInfo.InvariantCulture)}" : $"{pair.Key}={pair.Value}"));

    public void Tick()
    {
        if (_guardApplied && !_applied)
        {
            _guard.UnpatchSelf();
            _guardApplied = false;
        }
        if (_unlock != null && _unlock.OwnWorld)
        {
            _eligible = true;
        }
        else if (_unlock == null || !_unlock.IsUnlocked)
        {
            _eligible = false;
        }
        var wanted = _eligible && !_broken && ServerRules.Allows("EditorAdditions");
        if (wanted && !_applied)
        {
            Apply();
        }
        else if (!wanted && _applied)
        {
            Remove("the unlock ended");
        }
    }

    public void Shutdown()
    {
        _patches.UnpatchSelf();
        _guard.UnpatchSelf();
        _applied = false;
        _guardApplied = false;
    }

    private void Apply()
    {
        try
        {
            if (!_guardApplied)
            {
                _guard.Patch(AccessTools.Method(typeof(ZRpc), nameof(ZRpc.HandlePackage)), prefix: Guard(nameof(BeforePackage)));
                _guard.Patch(AccessTools.Method(typeof(ZNet), nameof(ZNet.StopAll)), prefix: Guard(nameof(BeforeConnectionChange)));
                _guard.Patch(AccessTools.Method(typeof(ZNet), nameof(ZNet.OnNewConnection)), prefix: Guard(nameof(BeforeConnectionChange)));
                _guardApplied = true;
            }
            _patches.Patch(AccessTools.Method(typeof(Prefs.Elem), nameof(Prefs.Elem.GrowPoint)), postfix: new HarmonyMethod(AccessTools.Method(typeof(GrowCorners), nameof(AfterGrowPoint))));
            _patches.Patch(AccessTools.Method(typeof(EditTarget), nameof(EditTarget.Options)), postfix: new HarmonyMethod(AccessTools.Method(typeof(GrowCorners), nameof(AfterOptions))));
            _applied = true;
            _log.LogInfo("Elements can grow toward a corner and the crosshair holds still on this server.");
        }
        catch (Exception e)
        {
            _broken = true;
            _patches.UnpatchSelf();
            _applied = false;
            _log.LogWarning($"Could not add the corner grow choices ({e.GetBaseException().Message}); SeneaL UI's own grow choices stay until the game restarts.");
        }
    }

    private static HarmonyMethod Guard(string name) =>
        new(AccessTools.Method(typeof(GrowCorners), name)) { priority = Priority.First };

    private void Remove(string why)
    {
        _patches.UnpatchSelf();
        _applied = false;
        _log.LogInfo($"SeneaL UI's own grow choices are back ({why}).");
    }

    private static void BeforePackage(ZPackage package)
    {
        var self = _current;
        if (self == null || !self._applied)
        {
            return;
        }
        var pos = package.GetPos();
        if (package.Size() - pos < sizeof(int))
        {
            return;
        }
        var hash = package.ReadInt();
        package.SetPos(pos);
        if (hash == PolicyRpc)
        {
            self._eligible = false;
            self.Remove("the server runs SeneaL UI");
        }
    }

    private static void BeforeConnectionChange()
    {
        var self = _current;
        if (self == null)
        {
            return;
        }
        self._eligible = false;
        if (self._applied)
        {
            self.Remove("the connection changed");
        }
    }

    private int Corner(Prefs.Elem elem)
    {
        if (elem == null || elem.Grow == null || !_corners.TryGetValue(elem.Key, out var corner))
        {
            return -1;
        }
        // A grow choice made since, through SeneaL UI's own setting, replaces the corner.
        var vertical = corner <= UpRight ? 4 : 5;
        return elem.Grow.Value == vertical ? corner : -1;
    }

    // The point of the element's visible frame that stays where it was put.
    private static void AfterGrowPoint(Prefs.Elem __instance, Rect f, ref Vector2 __result)
    {
        var self = _current;
        if (self == null)
        {
            return;
        }
        if (__instance == Prefs.Crosshair && __instance.LiveRef != null)
        {
            __result = __instance.LiveRef();
            return;
        }
        var half = self._widths.TryGetValue(__instance.Key, out var width) ? width * Screen.height / 2f : 0f;
        switch (self.Corner(__instance))
        {
            case UpLeft:
                __result = new Vector2(f.xMax - half, f.yMin);
                break;
            case UpRight:
                __result = new Vector2(f.xMin + half, f.yMin);
                break;
            case DownLeft:
                __result = new Vector2(f.xMax - half, f.yMax);
                break;
            case DownRight:
                __result = new Vector2(f.xMin + half, f.yMax);
                break;
        }
    }

    // A corner chosen in the editor takes the element's width now, and an element placed freely keeps its frame where it is.
    private void Choose(Prefs.Elem elem, Rect frame, int corner)
    {
        _corners[elem.Key] = corner;
        elem.Grow.Value = corner <= UpRight ? 4 : 5;
        if (frame.width <= 0f || Screen.height <= 0)
        {
            _widths.Remove(elem.Key);
            return;
        }
        _widths[elem.Key] = frame.width / Screen.height;
        if (!elem.Glued && elem.PosX != null && elem.PosY != null && elem.PosX.Value >= 0f && elem.PosY.Value >= 0f)
        {
            elem.PosX.Value = Mathf.Clamp01(frame.center.x / Screen.width);
            elem.PosY.Value = Mathf.Clamp01((corner <= UpRight ? frame.yMin : frame.yMax) / Screen.height);
        }
    }

    // SeneaL UI's six-way grow switch becomes a dropdown of SeneaL UI's own kind with the four corners added.
    private static void AfterOptions(EditTarget __instance, List<EditOption> __result)
    {
        var self = _current;
        var elem = __instance.E;
        // The lambdas below capture only the frame getter, never the target itself: a captured EditTarget becomes a field of a compiler-made class in this assembly, a type SeneaL UI 1.1.7 lacks, which fails every other mod's Harmony type scan of it.
        Func<Rect> frame = __instance.Frame;
        if (self == null || elem == null || elem.Grow == null || __result == null)
        {
            return;
        }
        var anchor = Loc.HudGrowAnchor;
        var at = __result.FindIndex(option => option.Kind == EditKind.Seg && option.Names != null && option.Names() is { Length: 6 } names && names[0] == anchor);
        if (at < 0)
        {
            return;
        }
        var old = __result[at];
        __result[at] = new EditOption
        {
            Tab = old.Tab,
            Kind = EditKind.Select,
            Label = old.Label,
            Enabled = old.Enabled,
            Names = () =>
            [
                Loc.HudGrowAnchor, Loc.HudGrowCenter, Loc.HudGrowRight, Loc.HudGrowLeft, Loc.HudGrowUp, Loc.HudGrowDown,
                "Grows up and left", "Grows up and right", "Grows down and left", "Grows down and right"
            ],
            GetI = () =>
            {
                var corner = self.Corner(elem);
                return corner >= 0 ? corner : elem.Grow.Value;
            },
            SetI = i =>
            {
                if (i >= UpLeft && i <= DownRight)
                {
                    self.Choose(elem, frame(), i);
                }
                else
                {
                    self._corners.Remove(elem.Key);
                    self._widths.Remove(elem.Key);
                    elem.Grow.Value = i;
                }
                self.Save();
            }
        };
    }
}
