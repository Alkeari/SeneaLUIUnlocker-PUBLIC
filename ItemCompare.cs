using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// What a hovered piece of gear changes against the piece worn in its place and against others carried for that place: one line per stat with both values and whether the hovered one is better, read from the game's own item values at each item's quality and world level.
internal static class ItemCompare
{
    private const string Section = "Item Comparison";

    private static ConfigEntry<bool> _enabled;
    private static ConfigEntry<int> _carried;

    public static bool Enabled => _enabled != null && _enabled.Value && ServerRules.Allows("ItemComparison");

    public static int Carried => _carried?.Value ?? 0;

    public static void Bind(ConfigFile cfg)
    {
        _enabled = cfg.Bind(Section, "Compare On Hover", true,
            "When you hover a piece of gear, show beside its tooltip how it compares with what you wear in its place: each stat, both values, better in green and worse in red.");
        _carried = cfg.Bind(Section, "Compare With Carried Items", 2, new ConfigDescription(
            "How many other carried items of the same kind are compared too, after the one worn.", new AcceptableValueRange<int>(0, 5)));
    }

    public sealed record Line(string Name, string Hovered, string Other, int Better);

    private enum Kind
    {
        None,
        Armor,
        Shield,
        Weapon
    }

    private static Kind KindOf(ItemDrop.ItemData item)
    {
        switch (item.m_shared.m_itemType)
        {
            case ItemDrop.ItemData.ItemType.Helmet:
            case ItemDrop.ItemData.ItemType.Chest:
            case ItemDrop.ItemData.ItemType.Legs:
            case ItemDrop.ItemData.ItemType.Shoulder:
            case ItemDrop.ItemData.ItemType.Utility:
            case ItemDrop.ItemData.ItemType.Trinket:
                return Kind.Armor;
            case ItemDrop.ItemData.ItemType.Shield:
                return Kind.Shield;
            case ItemDrop.ItemData.ItemType.OneHandedWeapon:
            case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
            case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
            case ItemDrop.ItemData.ItemType.Bow:
                return IsTool(item) ? Kind.None : Kind.Weapon;
            default:
                return Kind.None;
        }
    }

    // Building and farming tools are not weapons to compare.
    private static bool IsTool(ItemDrop.ItemData item)
    {
        var name = item.m_dropPrefab == null ? "" : item.m_dropPrefab.name;
        return name is "Hammer" or "Hoe" or "Cultivator" or "Scythe" or "FishingRod" || item.m_shared.m_buildPieces != null;
    }

    public static bool Comparable(ItemDrop.ItemData item) => item != null && KindOf(item) != Kind.None;

    // The worn piece first, then up to Compare With Carried Items others carried of the same kind.
    public static List<ItemDrop.ItemData> Others(Player player, ItemDrop.ItemData item)
    {
        var list = new List<ItemDrop.ItemData>();
        var worn = Worn(player, item);
        if (worn != null && worn != item)
        {
            list.Add(worn);
        }
        var kind = KindOf(item);
        foreach (var other in player.GetInventory().GetAllItems())
        {
            if (list.Count >= Carried + (worn != null && worn != item ? 1 : 0))
            {
                break;
            }
            if (other != item && other != worn && !other.m_equipped && KindOf(other) == kind && SameSlot(other, item))
            {
                list.Add(other);
            }
        }
        return list;
    }

    private static bool SameSlot(ItemDrop.ItemData a, ItemDrop.ItemData b) =>
        KindOf(a) == Kind.Weapon || a.m_shared.m_itemType == b.m_shared.m_itemType;

    private static ItemDrop.ItemData Worn(Player player, ItemDrop.ItemData item) => item.m_shared.m_itemType switch
    {
        ItemDrop.ItemData.ItemType.Helmet => player.m_helmetItem,
        ItemDrop.ItemData.ItemType.Chest => player.m_chestItem,
        ItemDrop.ItemData.ItemType.Legs => player.m_legItem,
        ItemDrop.ItemData.ItemType.Shoulder => player.m_shoulderItem,
        ItemDrop.ItemData.ItemType.Utility => player.m_utilityItem,
        ItemDrop.ItemData.ItemType.Trinket => player.m_trinketItem,
        ItemDrop.ItemData.ItemType.Shield => player.m_leftItem != null && player.m_leftItem.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield ? player.m_leftItem : null,
        _ => player.GetCurrentWeapon() is { } weapon && (player.m_unarmedWeapon == null || weapon != player.m_unarmedWeapon.m_itemData) && Comparable(weapon) ? weapon : null
    };

    public static List<Line> Lines(Player player, ItemDrop.ItemData hovered, ItemDrop.ItemData other)
    {
        var lines = new List<Line>();
        void Number(string name, float a, float b, bool higherBetter, string format = "0.#")
        {
            if (a == 0f && b == 0f)
            {
                return;
            }
            var better = Mathf.Approximately(a, b) ? 0 : (a > b) == higherBetter ? 1 : -1;
            lines.Add(new Line(name, a.ToString(format), b.ToString(format), better));
        }
        void Percent(string name, float a, float b, bool higherBetter) =>
            Number(name, a * 100f, b * 100f, higherBetter, "+0;-0;0");
        var ha = hovered.m_shared;
        var oa = other.m_shared;
        switch (KindOf(hovered))
        {
            case Kind.Armor:
                Number("Armor", hovered.GetArmor(hovered.m_quality, hovered.m_worldLevel), other.GetArmor(other.m_quality, other.m_worldLevel), true);
                break;
            case Kind.Shield:
                Number("Block armor", hovered.GetBaseBlockPower(hovered.m_quality), other.GetBaseBlockPower(other.m_quality), true);
                Number("Block force", hovered.GetDeflectionForce(hovered.m_quality), other.GetDeflectionForce(other.m_quality), true);
                Number("Parry bonus", ha.m_timedBlockBonus, oa.m_timedBlockBonus, true, "0.#x");
                break;
            case Kind.Weapon:
                var hd = hovered.GetDamage(hovered.m_quality, hovered.m_worldLevel);
                var od = other.GetDamage(other.m_quality, other.m_worldLevel);
                Number("Damage", hd.GetTotalDamage(), od.GetTotalDamage(), true);
                foreach (var (name, a, b) in DamageParts(hd, od))
                {
                    Number("  " + name, a, b, true);
                }
                Number("Block armor", hovered.GetBaseBlockPower(hovered.m_quality), other.GetBaseBlockPower(other.m_quality), true);
                Number("Parry bonus", ha.m_timedBlockBonus, oa.m_timedBlockBonus, true, "0.#x");
                Number("Knockback", ha.m_attackForce, oa.m_attackForce, true);
                Number("Backstab", ha.m_backstabBonus, oa.m_backstabBonus, true, "0.#x");
                Number("Attack stamina", ha.m_attack?.m_attackStamina ?? 0f, oa.m_attack?.m_attackStamina ?? 0f, false);
                break;
        }
        Number("Durability", hovered.GetMaxDurability(hovered.m_quality), other.GetMaxDurability(other.m_quality), true, "0");
        Number("Weight", hovered.GetWeight(), other.GetWeight(), false);
        Percent("Movement speed", ha.m_movementModifier, oa.m_movementModifier, true);
        Percent("Eitr regeneration", ha.m_eitrRegenModifier, oa.m_eitrRegenModifier, true);
        Percent("Heat resistance", ha.m_heatResistanceModifier, oa.m_heatResistanceModifier, true);
        Percent("Stamina while running", ha.m_runStaminaModifier, oa.m_runStaminaModifier, false);
        Percent("Stamina while jumping", ha.m_jumpStaminaModifier, oa.m_jumpStaminaModifier, false);
        Percent("Stamina while sneaking", ha.m_sneakStaminaModifier, oa.m_sneakStaminaModifier, false);
        Percent("Stamina while swimming", ha.m_swimStaminaModifier, oa.m_swimStaminaModifier, false);
        Percent("Stamina for attacks", ha.m_attackStaminaModifier, oa.m_attackStaminaModifier, false);
        Percent("Stamina for blocks", ha.m_blockStaminaModifier, oa.m_blockStaminaModifier, false);
        Percent("Stamina for dodges", ha.m_dodgeStaminaModifier, oa.m_dodgeStaminaModifier, false);
        foreach (var (type, a, b) in Resistances(ha, oa))
        {
            var rank = Rank(a) - Rank(b);
            lines.Add(new Line(type + " damage", Pretty(a), Pretty(b), rank == 0 ? 0 : rank > 0 ? 1 : -1));
        }
        Sets(player, hovered, other, lines);
        return lines;
    }

    private static IEnumerable<(string Name, float A, float B)> DamageParts(HitData.DamageTypes a, HitData.DamageTypes b)
    {
        yield return ("Blunt", a.m_blunt, b.m_blunt);
        yield return ("Slash", a.m_slash, b.m_slash);
        yield return ("Pierce", a.m_pierce, b.m_pierce);
        yield return ("Chop", a.m_chop, b.m_chop);
        yield return ("Pickaxe", a.m_pickaxe, b.m_pickaxe);
        yield return ("Fire", a.m_fire, b.m_fire);
        yield return ("Frost", a.m_frost, b.m_frost);
        yield return ("Lightning", a.m_lightning, b.m_lightning);
        yield return ("Poison", a.m_poison, b.m_poison);
        yield return ("Spirit", a.m_spirit, b.m_spirit);
    }

    private static IEnumerable<(string Type, HitData.DamageModifier A, HitData.DamageModifier B)> Resistances(ItemDrop.ItemData.SharedData a, ItemDrop.ItemData.SharedData b)
    {
        var types = (a.m_damageModifiers ?? []).Select(pair => pair.m_type).Concat((b.m_damageModifiers ?? []).Select(pair => pair.m_type)).Distinct();
        foreach (var type in types)
        {
            yield return (SettingsPage.Pretty(type.ToString()), Mod(a, type), Mod(b, type));
        }
    }

    private static HitData.DamageModifier Mod(ItemDrop.ItemData.SharedData shared, HitData.DamageType type) =>
        shared.m_damageModifiers?.FirstOrDefault(pair => pair.m_type == type) is { } pair && pair.m_type == type ? pair.m_modifier : HitData.DamageModifier.Normal;

    // How much a modifier protects, from most damage taken to none.
    private static int Rank(HitData.DamageModifier modifier) => modifier switch
    {
        HitData.DamageModifier.VeryWeak => 0,
        HitData.DamageModifier.Weak => 1,
        HitData.DamageModifier.SlightlyWeak => 2,
        HitData.DamageModifier.Normal => 3,
        HitData.DamageModifier.Ignore => 3,
        HitData.DamageModifier.SlightlyResistant => 4,
        HitData.DamageModifier.Resistant => 5,
        HitData.DamageModifier.VeryResistant => 6,
        HitData.DamageModifier.Immune => 7,
        _ => 3
    };

    private static string Pretty(HitData.DamageModifier modifier) => SettingsPage.Pretty(modifier.ToString());

    // Set pieces worn now against after the swap, when either item belongs to a set.
    private static void Sets(Player player, ItemDrop.ItemData hovered, ItemDrop.ItemData other, List<Line> lines)
    {
        foreach (var set in new[] { hovered.m_shared.m_setName, other.m_shared.m_setName }.Where(name => !string.IsNullOrEmpty(name)).Distinct())
        {
            var worn = player.GetInventory().GetEquippedItems().Count(item => item.m_shared.m_setName == set);
            var after = worn - (other.m_equipped && other.m_shared.m_setName == set ? 1 : 0) + (hovered.m_shared.m_setName == set && !hovered.m_equipped ? 1 : 0);
            var size = hovered.m_shared.m_setName == set ? hovered.m_shared.m_setSize : other.m_shared.m_setSize;
            if (!other.m_equipped)
            {
                continue;
            }
            lines.Add(new Line("Set pieces", $"{after}/{size}", $"{worn}/{size}", after == worn ? 0 : after > worn ? 1 : -1));
        }
    }
}
