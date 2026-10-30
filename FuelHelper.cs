using System;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Tops up nearby fires, torches and sconces you could fuel by hand, the way the game's own interaction does: one fuel item taken, then the fire's RPC_AddFuel, which the game sends to every player. Off by default, since it spends your fuel on its own.
internal sealed class FuelHelper
{
    private const string Section = "Fuel Helper";
    private const float CheckSeconds = 2f;

    private readonly ConfigEntry<bool> _enabled;
    private readonly ConfigEntry<float> _radius;
    private readonly ConfigEntry<int> _below;
    private readonly ConfigEntry<bool> _chests;
    private float _next;

    public FuelHelper(ConfigFile cfg)
    {
        _enabled = cfg.Bind(Section, "Fuel Helper", false,
            "Top up nearby fires, torches and sconces from your inventory, and from nearby chests when chosen. Off by default, since it spends your fuel on its own.");
        _radius = cfg.Bind(Section, "Fuel Radius", 10f, new ConfigDescription("Distance, meters, within which fires are topped up.", new AcceptableValueRange<float>(2f, 30f)));
        _below = cfg.Bind(Section, "Refill Below Percent", 50, new ConfigDescription("A fire is topped up to full once its fuel drops below this share of its maximum.", new AcceptableValueRange<int>(5, 100)));
        _chests = cfg.Bind(Section, "Take Fuel From Chests", true,
            "Take fuel from the chests SeneaL UI crafts from, within its range, when you carry none.");
    }

    public float Radius => _radius.Value;

    public bool Enabled => _enabled.Value && ServerRules.Allows("FuelHelper");

    public void Tick(float now)
    {
        var player = Player.m_localPlayer;
        if (!Enabled || player == null || player.IsDead() || now < _next)
        {
            return;
        }
        _next = now + CheckSeconds;
        foreach (var fire in SceneObjects.Live(SceneObjects.Fires))
        {
            if (fire == null || !fire.m_canRefill || fire.m_infiniteFuel || fire.m_fuelItem == null || fire.m_nview == null || !fire.m_nview.IsValid()
                || Vector3.Distance(fire.transform.position, player.transform.position) > _radius.Value)
            {
                continue;
            }
            var fuel = fire.m_nview.GetZDO().GetFloat(ZDOVars.s_fuel);
            if (fuel >= fire.m_maxFuel * _below.Value / 100f)
            {
                continue;
            }
            var wanted = Mathf.FloorToInt(fire.m_maxFuel - fuel);
            var name = fire.m_fuelItem.m_itemData.m_shared.m_name;
            for (var i = 0; i < wanted && Take(player, name); i++)
            {
                fire.m_nview.InvokeRPC("RPC_AddFuel");
            }
        }
    }

    private bool Take(Player player, string name)
    {
        var inventory = player.GetInventory();
        if (inventory.HaveItem(name))
        {
            inventory.RemoveItem(name, 1);
            return true;
        }
        if (!_chests.Value || !SeneaLCalls.CanRestock)
        {
            return false;
        }
        try
        {
            // A chest is changed only by the player that owns it, as Restock To Targets does: one nobody has open is taken over first.
            foreach (var chest in SeneaLCalls.CraftChests(player.transform.position))
            {
                var view = chest == null ? null : SeneaLCalls.Nview(chest);
                if (view == null || !view.IsValid() || chest.GetInventory() == null || !chest.GetInventory().HaveItem(name))
                {
                    continue;
                }
                if (!view.IsOwner())
                {
                    if (chest.IsInUse())
                    {
                        continue;
                    }
                    view.ClaimOwnership();
                }
                chest.GetInventory().RemoveItem(name, 1);
                return true;
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
