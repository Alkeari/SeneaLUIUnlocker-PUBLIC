using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// When you swing at a tree, log, rock, ore deposit or other breakable with a tool that cannot harm it, the best carried tool that can is equipped first: an axe for wood, a pickaxe for stone and ore, of at least the tier the target needs. Off by default.
internal static class ToolSwap
{
    private const float Reach = 4f;
    private static ConfigEntry<bool> _enabled;

    public static void Bind(ConfigFile cfg)
    {
        _enabled = cfg.Bind("Tools", "Tool Swap", false,
            "When you attack a tree, log, rock, ore deposit or other breakable with a tool that cannot harm it, equip the best tool you carry that can.");
    }

    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
    internal static class AttackPatch
    {
        private static void Prefix(Humanoid __instance)
        {
            if (_enabled == null || !_enabled.Value || !ServerRules.Allows("ToolSwap") || __instance != Player.m_localPlayer || GameCamera.instance == null)
            {
                return;
            }
            var player = (Player)__instance;
            var (chop, pickaxe, tier) = Needs(player);
            if (!chop && !pickaxe)
            {
                return;
            }
            var current = player.GetRightItem();
            if (current != null && Fits(current, chop, pickaxe, tier))
            {
                return;
            }
            var best = player.GetInventory().GetAllItems()
                .Where(item => item.IsEquipable() && Fits(item, chop, pickaxe, tier))
                .OrderByDescending(item => chop ? item.GetDamage().m_chop : item.GetDamage().m_pickaxe)
                .FirstOrDefault();
            if (best != null)
            {
                player.EquipItem(best);
            }
        }
    }

    private static bool Fits(ItemDrop.ItemData item, bool chop, bool pickaxe, int tier)
    {
        var damage = item.GetDamage();
        return item.m_shared.m_toolTier >= tier && (chop ? damage.m_chop > 0f : damage.m_pickaxe > 0f);
    }

    // What the thing under the crosshair can be harmed by, and the tool tier it needs.
    private static (bool Chop, bool Pickaxe, int Tier) Needs(Player player)
    {
        var camera = GameCamera.instance.transform;
        if (!Physics.Raycast(camera.position, camera.forward, out var hit, Reach + Vector3.Distance(camera.position, player.GetEyePoint()), Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            return (false, false, 0);
        }
        var target = hit.collider.transform;
        if (target.GetComponentInParent<TreeBase>() is { } tree)
        {
            return (Can(tree.m_damageModifiers.m_chop), false, tree.m_minToolTier);
        }
        if (target.GetComponentInParent<TreeLog>() is { } log)
        {
            return (Can(log.m_damages.m_chop), false, log.m_minToolTier);
        }
        if (target.GetComponentInParent<MineRock5>() is { } rock5)
        {
            return (false, Can(rock5.m_damageModifiers.m_pickaxe), rock5.m_minToolTier);
        }
        if (target.GetComponentInParent<MineRock>() is { } rock)
        {
            return (false, Can(rock.m_damageModifiers.m_pickaxe), rock.m_minToolTier);
        }
        if (target.GetComponentInParent<Destructible>() is { } destructible && destructible.GetComponent<Piece>() == null)
        {
            var wood = Can(destructible.m_damages.m_chop);
            var stone = Can(destructible.m_damages.m_pickaxe);
            return (wood && !stone, stone && !wood, destructible.m_minToolTier);
        }
        return (false, false, 0);
    }

    private static bool Can(HitData.DamageModifier modifier) => modifier is not (HitData.DamageModifier.Immune or HitData.DamageModifier.Ignore);
}
