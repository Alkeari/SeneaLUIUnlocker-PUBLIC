using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// With SmartCraftStorage installed, SeneaL UI's crafting window, build panel and its own chest crafting count and take what SmartCraftStorage finds in nearby chests, by SmartCraftStorage's radius and container rules, instead of SeneaL UI's own chest scan; adding both would count chests twice.
// It patches SeneaL UI's Stock, so like BeyondMaximums it exists only while the unlock stands on a server that has not answered, under its own Harmony id, and a guard on the game's ZRpc.HandlePackage removes it before a SeneaL UI server's rules reach SeneaL UI. SmartCraftStorage and SeneaL UI's Stock are reached by name, so either one missing turns this off alone.
internal sealed class SmartCraftChests
{
    public const string SmartCraftGuid = "com.zellds.smartcraftstorage";
    public const string BridgeGuid = "alkeari.valheim.smartcraftstorageforsenealui";
    private const string PatchId = Plugin.Guid + ".smartcraft";
    private const string GuardId = Plugin.Guid + ".smartcraft.guard";
    private static readonly int PolicyRpc = "SeneaLUI_Policy".GetStableHashCode();

    private static SmartCraftChests _current;

    private readonly ManualLogSource _log;
    private readonly PolicyUnlock _unlock;
    private readonly ConfigEntry<bool> _on;
    private readonly Harmony _patches = new(PatchId);
    private readonly Harmony _guard = new(GuardId);
    private readonly MethodInfo[] _targets;
    private readonly Func<Inventory, string, int, int> _own;
    private readonly MethodInfo _context;
    private readonly MethodInfo _find;
    private readonly ConfigEntry<float> _radius;
    private readonly Func<Inventory, string, int, bool, int> _countUnlocked;
    private readonly MethodInfo _cacheGet;
    private readonly MethodInfo _cacheStore;

    private bool _eligible;
    private bool _applied;
    private bool _guardApplied;
    private bool _broken;

    private SmartCraftChests(ManualLogSource log, PolicyUnlock unlock, ConfigEntry<bool> on, MethodInfo[] targets, Func<Inventory, string, int, int> own,
        MethodInfo context, MethodInfo find, ConfigEntry<float> radius, Func<Inventory, string, int, bool, int> countUnlocked, MethodInfo cacheGet, MethodInfo cacheStore)
    {
        _log = log;
        _unlock = unlock;
        _on = on;
        _targets = targets;
        _own = own;
        _context = context;
        _find = find;
        _radius = radius;
        _countUnlocked = countUnlocked;
        _cacheGet = cacheGet;
        _cacheStore = cacheStore;
    }

    public static SmartCraftChests Resolve(ConfigFile cfg, ManualLogSource log, PolicyUnlock unlock)
    {
        if (!Chainloader.PluginInfos.ContainsKey(SmartCraftGuid))
        {
            return null;
        }
        var on = cfg.Bind("Compatibility", "Count SmartCraftStorage Chests", true,
            "With SmartCraftStorage installed, SeneaL UI's crafting window and build panel show, and its chest crafting takes, what SmartCraftStorage finds in nearby chests by SmartCraftStorage's own radius and rules, instead of SeneaL UI's own chest scan. Only in your own world and on servers without SeneaL UI.");
        if (Chainloader.PluginInfos.ContainsKey(BridgeGuid))
        {
            log.LogInfo("SmartCraftStorage for SeneaL UI is installed and counts SmartCraftStorage's chests itself; Count SmartCraftStorage Chests stands down.");
            return null;
        }
        var stock = TypeLookup.Find("SeneaLUI.Core.Stock");
        var context = AccessTools.Method(TypeLookup.Find("SmartCraftStorage.CraftingChestAccess.InventoryChestPatches"), "IsCraftingOrBuildingContext");
        var find = AccessTools.Method(TypeLookup.Find("SmartCraftStorage.Shared.NearbyContainers"), "Find", [typeof(Vector3), typeof(float), typeof(Player)]);
        var radius = AccessTools.Field(TypeLookup.Find("SmartCraftStorage.Config.ModConfig"), "CraftingChestRadius")?.GetValue(null) as ConfigEntry<float>;
        var count = AccessTools.Method(TypeLookup.Find("SmartCraftStorage.Shared.UnlockedInventory"), "CountItems", [typeof(Inventory), typeof(string), typeof(int), typeof(bool)]);
        var cache = TypeLookup.Find("SmartCraftStorage.Shared.ChestCountCache");
        var own = stock == null ? null : AccessTools.Method(stock, "Own", [typeof(Inventory), typeof(string), typeof(int)]);
        MethodInfo[] targets = stock == null ? [] :
        [
            AccessTools.Method(stock, "Count", [typeof(Player), typeof(string), typeof(int)]),
            AccessTools.Method(stock, "FromChests", [typeof(Player), typeof(string)]),
            AccessTools.Method(stock, "Have", [typeof(Player), typeof(string)]),
            AccessTools.Method(stock, "Remove", [typeof(Player), typeof(string), typeof(int), typeof(int)])
        ];
        if (unlock == null || own == null || context == null || find == null || radius == null || count == null || cache == null || Array.Exists(targets, t => t == null))
        {
            log.LogWarning("SmartCraftStorage or SeneaL UI has an unknown shape; Count SmartCraftStorage Chests is off.");
            return null;
        }
        var chests = new SmartCraftChests(log, unlock, on, targets, AccessTools.MethodDelegate<Func<Inventory, string, int, int>>(own), context, find, radius,
            AccessTools.MethodDelegate<Func<Inventory, string, int, bool, int>>(count), AccessTools.Method(cache, "TryGet"), AccessTools.Method(cache, "Store"));
        _current = chests;
        unlock.Granted += _ =>
        {
            chests._eligible = true;
            chests.Tick();
        };
        return chests;
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
        var wanted = _on.Value && ServerRules.Allows("SmartCraftChests") && _eligible && !_broken;
        if (wanted && !_applied)
        {
            Apply();
        }
        else if (!wanted && _applied)
        {
            Remove();
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
                var guard = new HarmonyMethod(AccessTools.Method(typeof(SmartCraftChests), nameof(BeforePackage))) { priority = Priority.First };
                _guard.Patch(AccessTools.Method(typeof(ZRpc), nameof(ZRpc.HandlePackage)), prefix: guard);
                var change = new HarmonyMethod(AccessTools.Method(typeof(SmartCraftChests), nameof(BeforeConnectionChange))) { priority = Priority.First };
                _guard.Patch(AccessTools.Method(typeof(ZNet), nameof(ZNet.StopAll)), prefix: change);
                _guard.Patch(AccessTools.Method(typeof(ZNet), nameof(ZNet.OnNewConnection)), prefix: change);
                _guardApplied = true;
            }
            string[] prefixes = [nameof(Count), nameof(FromChests), nameof(Have), nameof(Take)];
            for (var i = 0; i < _targets.Length; i++)
            {
                _patches.Patch(_targets[i], prefix: new HarmonyMethod(AccessTools.Method(typeof(SmartCraftChests), prefixes[i])));
            }
            _applied = true;
            _log.LogInfo("SeneaL UI counts SmartCraftStorage's chests on this server.");
        }
        catch (Exception e)
        {
            _broken = true;
            _patches.UnpatchSelf();
            _applied = false;
            _log.LogWarning($"Could not count SmartCraftStorage's chests in SeneaL UI ({e.GetBaseException().Message}); Count SmartCraftStorage Chests stays off until the game restarts.");
        }
    }

    // Leaves the guard patched until the next Tick, since unpatching a method from inside its own prefix is avoided.
    private void Remove()
    {
        _patches.UnpatchSelf();
        _applied = false;
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
            self.Remove();
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
            self.Remove();
        }
    }

    private static bool IsLocal(Player p) => p != null && p == Player.m_localPlayer;

    private static bool Count(Player p, string name, int quality, ref int __result)
    {
        if (!IsLocal(p))
        {
            return true;
        }
        __result = _current._own(p.GetInventory(), name, quality) + _current.ChestTotal(p, name, quality);
        return false;
    }

    private static bool FromChests(Player p, string name, ref int __result)
    {
        if (!IsLocal(p))
        {
            return true;
        }
        __result = _current.ChestTotal(p, name, -1);
        return false;
    }

    // Inventory.HaveItem and Inventory.RemoveItem reach SmartCraftStorage's chests through its own patches.
    private static bool Have(Player p, string name, ref bool __result)
    {
        if (!IsLocal(p))
        {
            return true;
        }
        __result = p.GetInventory().HaveItem(name, true);
        return false;
    }

    private static bool Take(Player p, string name, int amount, int quality)
    {
        if (!IsLocal(p))
        {
            return true;
        }
        p.GetInventory().RemoveItem(name, amount, quality, true);
        return false;
    }

    private int ChestTotal(Player p, string name, int quality)
    {
        object[] context = [p.GetInventory(), null];
        if (!(bool)_context.Invoke(null, context) || context[1] is not Player player)
        {
            return 0;
        }
        object[] cached = [name, quality, true, 0];
        if ((bool)_cacheGet.Invoke(null, cached))
        {
            return (int)cached[3];
        }
        var total = 0;
        foreach (var container in (IEnumerable<Container>)_find.Invoke(null, [player.transform.position, _radius.Value, player]))
        {
            total += _countUnlocked(container.GetInventory(), name, quality, true);
        }
        _cacheStore.Invoke(null, [name, quality, true, total]);
        return total;
    }
}
