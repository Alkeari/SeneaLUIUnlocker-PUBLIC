using System;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// The one feature that patches SeneaL UI's own methods. A SeneaL UI server lists every foreign patch on SeneaL UI's code in the build proof it asks for (Integrity.Foreign, computed inside Policy.Handle when the server's SeneaLUI_Policy answer arrives), so these patches exist only while the unlock stands on a server that has not answered, under their own Harmony id, and a guard on the game's own ZRpc.HandlePackage removes them before that answer reaches SeneaL UI.
internal sealed class BeyondMaximums
{
    private const string Section = "Cheats: Beyond Maximums";
    private const string PatchId = Plugin.Guid + ".beyond";
    private const string GuardId = Plugin.Guid + ".beyond.guard";
    private const string PolicyType = "SeneaLUI.Net.Policy";
    private const string PovType = "SeneaLUI.Mods.Pov";
    private const string BlipsType = "SeneaLUI.Radar.Blips";
    private const string PolicyRpcName = "SeneaLUI_Policy";

    private static readonly int PolicyRpc = PolicyRpcName.GetStableHashCode();
    private static BeyondMaximums _current;

    private readonly ManualLogSource _log;
    private readonly PolicyUnlock _unlock;
    private readonly ConfigEntry<bool> _on;
    private readonly Harmony _patches = new(PatchId);
    private readonly Harmony _guard = new(GuardId);
    private readonly Lift[] _lifts;
    private readonly (MethodInfo Getter, string Postfix)[] _targets;

    private bool _eligible;
    private bool _applied;
    private bool _guardApplied;
    private bool _broken;

    private BeyondMaximums(ManualLogSource log, PolicyUnlock unlock, ConfigEntry<bool> on, Lift[] lifts, (MethodInfo, string)[] targets)
    {
        _log = log;
        _unlock = unlock;
        _on = on;
        _lifts = lifts;
        _targets = targets;
    }

    public static BeyondMaximums Resolve(ConfigFile cfg, ManualLogSource log, PolicyUnlock unlock)
    {
        var on = cfg.Bind(Section, "Go Beyond SeneaL UI Maximums (Cheat)", false,
            "Cheat, off by default: in your own world and on servers without SeneaL UI, raise the ranges below past SeneaL UI's own maximums. It patches SeneaL UI's code there; the patches are removed before a SeneaL UI server could see them.");
        var chest = Cap(cfg, "Max Chest Range Beyond", 200f, 100f, 500f,
            "Chest range, meters, for crafting, building, quick stack and restock wherever SeneaL UI's own range is at its 100 m maximum. Chests farther away than the game has loaded are not found.");
        var area = Cap(cfg, "Max Area Pickup Beyond", 20f, 10f, 50f,
            "Area pickup radius, meters, used when SeneaL UI's own radius is at its 10 m maximum.");
        var ore = Cap(cfg, "Max Ore Radius Beyond", 120f, 60f, 300f,
            "Ore detection radius, meters, used when SeneaL UI's own radius is at its 60 m maximum.");
        var radar = Cap(cfg, "Max Radar Range Beyond", 300f, 200f, 500f,
            "SeneaL Radar range, meters, used when the radar's own range is at its 200 m maximum.");

        var policy = TypeLookup.Find(PolicyType);
        if (unlock == null || policy == null)
        {
            return null;
        }
        var pov = TypeLookup.Find(PovType);
        var chestReach = Reach(pov, nameof(SeneaLUI.Mods.Pov.ChestReach));
        var pickupReach = Reach(pov, nameof(SeneaLUI.Mods.Pov.PickupReach));
        var maxRange = Constant(policy, nameof(SeneaLUI.Net.Policy.MaxRange));
        var maxArea = Constant(policy, nameof(SeneaLUI.Net.Policy.MaxArea));
        var maxOre = Constant(policy, nameof(SeneaLUI.Net.Policy.MaxOre));
        var maxRadar = Constant(policy, nameof(SeneaLUI.Net.Policy.MaxRadar));

        Lift[] lifts =
        [
            new(maxRange, chest, chestReach),
            new(maxRange, chest, chestReach),
            new(maxRange, chest, null),
            new(maxArea, area, pickupReach),
            new(maxOre, ore, null),
            new(maxRadar, radar, null)
        ];
        var blips = Quiet.Type(BlipsType);
        (MethodInfo, string)[] targets =
        [
            (Getter(policy, nameof(SeneaLUI.Net.Policy.ChestRange), maxRange), nameof(ChestRange)),
            (Getter(policy, nameof(SeneaLUI.Net.Policy.BuildRange), maxRange), nameof(BuildRange)),
            (Getter(policy, nameof(SeneaLUI.Net.Policy.StackRange), maxRange), nameof(StackRange)),
            (Getter(policy, nameof(SeneaLUI.Net.Policy.AreaRange), maxArea), nameof(AreaRange)),
            (Getter(policy, nameof(SeneaLUI.Net.Policy.OreRadius), maxOre), nameof(OreRadius)),
            (blips == null ? null : Getter(blips, "Range", maxRadar), nameof(RadarRange))
        ];
        if (Array.TrueForAll(targets, t => t.Item1 == null))
        {
            log.LogWarning("This SeneaL UI build has none of the ranges Go Beyond SeneaL UI Maximums (Cheat) raises, so it does nothing.");
            return null;
        }
        var beyond = new BeyondMaximums(log, unlock, on, lifts, targets);
        _current = beyond;
        unlock.Granted += _ => beyond.Granted();
        return beyond;
    }

    private static ConfigEntry<float> Cap(ConfigFile cfg, string key, float value, float min, float max, string desc) =>
        cfg.Bind(Section, key, value, new ConfigDescription(desc, new AcceptableValueRange<float>(min, max)));

    private static Func<float> Reach(Type pov, string name)
    {
        var getter = pov == null ? null : AccessTools.PropertyGetter(pov, name);
        return getter == null ? null : AccessTools.MethodDelegate<Func<float>>(getter);
    }

    private static float Constant(Type type, string name) =>
        AccessTools.Field(type, name)?.GetValue(null) is float value ? value : float.NaN;

    private static MethodInfo Getter(Type type, string name, float max) =>
        float.IsNaN(max) ? null : AccessTools.PropertyGetter(type, name);

    private bool Live => _applied && (_unlock.IsUnlocked || _unlock.OwnWorld);

    private void Granted()
    {
        _eligible = true;
        Tick();
    }

    public void Tick()
    {
        if (_guardApplied && !_applied)
        {
            _guard.UnpatchSelf();
            _guardApplied = false;
        }
        if (_unlock.OwnWorld)
        {
            _eligible = true;
        }
        else if (!_unlock.IsUnlocked)
        {
            _eligible = false;
        }
        var wanted = _on.Value && ServerRules.Allows("BeyondMaximums") && _eligible && !_broken && ServerDirectory.Option(ServerDirectory.BeyondOption, true);
        if (wanted && !_applied)
        {
            Apply();
        }
        else if (!wanted && _applied)
        {
            Remove("the unlock or the setting ended");
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
            foreach (var (getter, postfix) in _targets)
            {
                if (getter != null)
                {
                    _patches.Patch(getter, postfix: new HarmonyMethod(AccessTools.Method(typeof(BeyondMaximums), postfix)));
                }
            }
            _applied = true;
            _log.LogInfo("SeneaL UI's range maximums are raised on this server.");
        }
        catch (Exception e)
        {
            _broken = true;
            _patches.UnpatchSelf();
            _applied = false;
            _log.LogWarning($"Could not raise SeneaL UI's maximums ({e.GetBaseException().Message}); Go Beyond SeneaL UI Maximums (Cheat) stays off until the game restarts.");
        }
    }

    private static HarmonyMethod Guard(string name) =>
        new(AccessTools.Method(typeof(BeyondMaximums), name)) { priority = Priority.First };

    // Leaves the guard patched until the next Tick: it sits on the game's own methods, which a SeneaL UI server never sees, and unpatching a method from inside its own prefix is avoided.
    private void Remove(string why)
    {
        _patches.UnpatchSelf();
        _applied = false;
        _log.LogInfo($"SeneaL UI's range maximums are back ({why}).");
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

    private static void ChestRange(ref float __result) => __result = _current.Raise(0, __result);

    private static void BuildRange(ref float __result) => __result = _current.Raise(1, __result);

    private static void StackRange(ref float __result) => __result = _current.Raise(2, __result);

    private static void AreaRange(ref float __result) => __result = _current.Raise(3, __result);

    private static void OreRadius(ref float __result) => __result = _current.Raise(4, __result);

    private static void RadarRange(ref float __result) => __result = _current.Raise(5, __result);

    // SeneaL UI's getter already took the smaller of the player's own setting and every limit, so a result at SeneaL UI's maximum means nothing narrower applies; a narrower server rule, feature switch or progression reach is left as it is.
    private float Raise(int index, float value)
    {
        var lift = _lifts[index];
        if (!Live || value < lift.Max - 0.01f)
        {
            return value;
        }
        var cap = lift.Cap.Value;
        return lift.Reach == null ? cap : Mathf.Min(cap, lift.Reach());
    }

    private sealed record Lift(float Max, ConfigEntry<float> Cap, Func<float> Reach);
}
