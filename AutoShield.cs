using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

internal enum ShieldChoice
{
    LastUsed,
    BestBlock,
    BestParry,
    LeastSlowdown
}

// Shield Pairs: each one-handed weapon remembers, in its own saved item data, the shield it was last used with or that it was last used without one, so taking it up again brings that shield, swaps to it, or puts the shield away. A weapon never paired follows the plain Auto Shield rule.
// When the local player takes up a one-handed weapon with the left hand empty, a carried shield goes on with it through the game's own equip toggle, so it takes the shield's equip time like a click would. The weapon's equip only marks it; the shield follows on a later frame once the game would take it (not attacking, dodging, swimming, in a cutscene or seated), and is dropped if the hands change first.
internal static class AutoShield
{
    private static ConfigEntry<bool> _enabled;
    private static ConfigEntry<ShieldChoice> _choice;
    private static ConfigEntry<float> _pause;
    private static ConfigEntry<bool> _pairs;
    private static ConfigEntry<bool> _learn;
    private static ConfigEntry<KeyboardShortcut> _pairKey;
    private static ConfigEntry<bool> _swords;
    private static ConfigEntry<bool> _maces;
    private static ConfigEntry<bool> _axes;
    private static ConfigEntry<bool> _knives;
    private static ConfigEntry<bool> _spears;
    private static ConfigEntry<bool> _others;
    private const string PairKey = "alkeari_shield_pair";
    private const string NoShield = "none";

    private static Player _owner;
    private static ItemDrop.ItemData _last;
    private static string _lastIdentity;
    private static ItemDrop.ItemData _pending;
    private static ItemDrop.ItemData _queued;
    private static ItemDrop.ItemData _ours;
    private static ItemDrop.ItemData _hiddenRight;
    private static float _removedAt = float.NegativeInfinity;

    public static void Bind(ConfigFile cfg)
    {
        _enabled = cfg.Bind("Inventory", "Auto Shield", true,
            "When you equip a one-handed weapon with nothing in your left hand, a shield from your inventory is equipped with it.");
        _choice = cfg.Bind("Inventory", "Auto Shield Choice", ShieldChoice.LastUsed,
            "Which shield Auto Shield equips: Last Used (the one you last had equipped this session, else the best blocker), Best Block (highest block power at its quality), Best Parry (block power times parry bonus) or Least Slowdown (smallest movement penalty).");
        _pause = cfg.Bind("Inventory", "Auto Shield Pause Seconds", 10f, new ConfigDescription(
            "After you take a shield off yourself, Auto Shield leaves your left hand empty for this many seconds.", new AcceptableValueRange<float>(0f, 120f)));
        _pairs = cfg.Bind("Inventory", "Shield Pairs", true,
            "Each one-handed weapon can be paired with a shield, or with no shield: taking that weapon up brings its shield, swaps to it, or puts the shield away. A weapon without a pair follows the Shield With switches and Auto Shield Choice.");
        _learn = cfg.Bind("Inventory", "Learn Shield Pairs", true,
            "Putting a shield on or taking it off by hand while holding a one-handed weapon pairs that weapon with that shield, or with no shield. Off = pairs change only with Shield Pair Key.");
        _pairKey = cfg.Bind("Inventory", "Shield Pair Key", new KeyboardShortcut(KeyCode.Home),
            "Pairs the one-handed weapon in your hand with the shield you wear, or with no shield when you wear none; pressed again on the same pair, it clears it.");
        _swords = cfg.Bind("Inventory", "Shield With Swords", true, "Unpaired swords get a shield from Auto Shield. Off = a sword puts the shield away.");
        _maces = cfg.Bind("Inventory", "Shield With Maces", true, "Unpaired maces and clubs get a shield from Auto Shield. Off = they put the shield away.");
        _axes = cfg.Bind("Inventory", "Shield With Axes", false, "Unpaired one-handed axes get a shield from Auto Shield. Off by default since axes are also the woodcutting tool; off = an axe puts the shield away.");
        _knives = cfg.Bind("Inventory", "Shield With Knives", true, "Unpaired knives get a shield from Auto Shield. Off = a knife puts the shield away.");
        _spears = cfg.Bind("Inventory", "Shield With Spears", true, "Unpaired spears get a shield from Auto Shield. Off = a spear puts the shield away.");
        _others = cfg.Bind("Inventory", "Shield With Other Weapons", true, "Unpaired one-handed weapons of any other kind (magic and modded weapons) get a shield from Auto Shield. Off = they put the shield away.");
    }

    public static void Tick()
    {
        var player = Player.m_localPlayer;
        if (player != _owner)
        {
            _owner = player;
            _last = null;
            _lastIdentity = null;
            _pending = null;
            _queued = null;
            _removedAt = float.NegativeInfinity;
        }
        if (player == null)
        {
            return;
        }
        // Read on the next frame by the equip patch: ShowHandItems clears the hidden item before it equips it again.
        _hiddenRight = player.m_hiddenRightItem;
        if (_queued != null && player.IsEquipActionQueued(_queued) && !HandsWant(player))
        {
            player.RemoveEquipAction(_queued);
            Log($"cancelled the queued {_queued.m_shared.m_name}: the hands changed");
            _queued = null;
        }
        HandlePairKey(player);
        if (_pending == null)
        {
            return;
        }
        var pair = Pair(_pending) ?? (KindWantsShield(_pending) ? null : NoShield);
        if (!ServerRules.On(_enabled, "AutoShield") || player.GetRightItem() != _pending || player.m_hiddenLeftItem != null || ShieldQueued(player)
            || player.GetLeftItem() != null && (pair == null || player.GetLeftItem().m_shared.m_itemType != ItemDrop.ItemData.ItemType.Shield))
        {
            _pending = null;
            return;
        }
        if (pair != null)
        {
            if (player.IsDead() || player.InAttack() || player.InDodge() || player.IsSwimming() || player.InCutscene() || player.IsTeleporting() || player.IsSleeping() || player.IsAttached())
            {
                return;
            }
            _pending = null;
            Paired(player, pair);
            return;
        }
        if (!HandsWant(player))
        {
            _pending = null;
            return;
        }
        if (Time.time - _removedAt < _pause.Value)
        {
            Log("skipped: a shield was taken off by hand moments ago");
            _pending = null;
            return;
        }
        if (player.IsDead() || player.InAttack() || player.InDodge() || player.IsSwimming() || player.InCutscene() || player.IsTeleporting() || player.IsSleeping() || player.IsAttached())
        {
            return;
        }
        _pending = null;
        var shield = Pick(player);
        if (shield == null)
        {
            return;
        }
        _ours = shield;
        player.ToggleEquipped(shield);
        _queued = player.IsEquipActionQueued(shield) ? shield : null;
        Log($"{shield.m_shared.m_name} q{shield.m_quality} {(_queued == null ? "equipped" : "queued")} with {player.GetRightItem().m_shared.m_name} ({_choice.Value})");
    }

    // The weapon's pair, when Shield Pairs is on and it has one: NoShield, or the shield's identity.
    private static string Pair(ItemDrop.ItemData weapon) =>
        _pairs.Value && weapon.m_customData.TryGetValue(PairKey, out var pair) && pair.Length > 0 ? pair : null;

    private static bool KindWantsShield(ItemDrop.ItemData weapon)
    {
        switch (weapon.m_shared.m_skillType)
        {
            case Skills.SkillType.Swords: return _swords.Value;
            case Skills.SkillType.Clubs: return _maces.Value;
            case Skills.SkillType.Axes: return _axes.Value;
            case Skills.SkillType.Knives: return _knives.Value;
            case Skills.SkillType.Spears: return _spears.Value;
            default: return _others.Value;
        }
    }

    private static void HandlePairKey(Player player)
    {
        if (!_pairs.Value || InventoryFeatures.Typing() || !KeyInput.Down(_pairKey.Value) || !ServerRules.On(_enabled, "AutoShield"))
        {
            return;
        }
        var weapon = player.GetRightItem();
        if (weapon == null || weapon.m_shared.m_itemType != ItemDrop.ItemData.ItemType.OneHandedWeapon)
        {
            player.Message(MessageHud.MessageType.Center, Lang.T("AutoShield.HoldAOneHandedWeapon", "Hold a one-handed weapon to pair it with a shield"));
            return;
        }
        var worn = player.GetLeftItem();
        var pair = worn != null && worn.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield ? Identity(worn) : NoShield;
        var name = Localization.instance.Localize(weapon.m_shared.m_name);
        if (weapon.m_customData.TryGetValue(PairKey, out var old) && old == pair)
        {
            weapon.m_customData.Remove(PairKey);
            player.Message(MessageHud.MessageType.TopLeft, Lang.F("AutoShield.UnpairedItFollowsTheShield", "{0} unpaired: it follows the Shield With settings", name));
            Log($"{weapon.m_shared.m_name} unpaired by key");
            return;
        }
        weapon.m_customData[PairKey] = pair;
        player.Message(MessageHud.MessageType.TopLeft, pair == NoShield ? Lang.F("AutoShield.PairedWithNoShield", "{0} paired with no shield", name) : Lang.F("AutoShield.PairedWith", "{0} paired with {1}", name, Localization.instance.Localize(worn.m_shared.m_name)));
        Log($"{weapon.m_shared.m_name} paired with {(pair == NoShield ? "no shield" : pair)} by key");
    }

    private static void Paired(Player player, string pair)
    {
        var worn = player.GetLeftItem();
        var weapon = player.GetRightItem().m_shared.m_name;
        if (pair == NoShield)
        {
            if (worn != null)
            {
                player.UnequipItem(worn);
                Log($"{worn.m_shared.m_name} put away: {weapon} is paired with no shield");
            }
            return;
        }
        if (worn != null && Identity(worn) == pair)
        {
            return;
        }
        var shield = player.GetInventory().GetAllItems().Where(item => Usable(item) && Identity(item) == pair).OrderByDescending(item => item.m_durability).FirstOrDefault();
        if (shield == null)
        {
            shield = worn == null ? Pick(player) : null;
            if (shield == null)
            {
                return;
            }
        }
        _ours = shield;
        player.ToggleEquipped(shield);
        _queued = player.IsEquipActionQueued(shield) ? shield : null;
        Log($"{shield.m_shared.m_name} q{shield.m_quality} {(_queued == null ? "equipped" : "queued")} with {weapon} (pair {pair})");
    }

    private static void Remember(Player player, string pair)
    {
        var weapon = player.GetRightItem();
        if (!_pairs.Value || !_learn.Value || weapon == null || weapon.m_shared.m_itemType != ItemDrop.ItemData.ItemType.OneHandedWeapon)
        {
            return;
        }
        if (!weapon.m_customData.TryGetValue(PairKey, out var old) || old != pair)
        {
            weapon.m_customData[PairKey] = pair;
            Log($"{weapon.m_shared.m_name} paired with {(pair == NoShield ? "no shield" : pair)}");
        }
    }

    private static bool HandsWant(Player player)
    {
        var right = player.GetRightItem();
        return right != null && right.m_shared.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon && player.GetLeftItem() == null && player.m_hiddenLeftItem == null;
    }

    private static bool ShieldQueued(Player player) =>
        player.m_actionQueue.Any(action => action.m_type == Player.MinorActionData.ActionType.Equip && action.m_item != null && action.m_item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield);

    private static ItemDrop.ItemData Pick(Player player)
    {
        var shields = player.GetInventory().GetAllItems().Where(Usable).ToList();
        if (shields.Count == 0)
        {
            return null;
        }
        switch (_choice.Value)
        {
            case ShieldChoice.LastUsed:
                if (_last != null && shields.Contains(_last))
                {
                    return _last;
                }
                var same = shields.Where(item => Identity(item) == _lastIdentity).OrderByDescending(item => item.m_durability).FirstOrDefault();
                return same ?? shields.OrderByDescending(Block).ThenByDescending(item => item.m_durability).First();
            case ShieldChoice.BestParry:
                return shields.OrderByDescending(item => Block(item) * item.m_shared.m_timedBlockBonus).ThenByDescending(Block).First();
            case ShieldChoice.LeastSlowdown:
                return shields.OrderByDescending(item => item.m_shared.m_movementModifier).ThenByDescending(Block).First();
            default:
                return shields.OrderByDescending(Block).ThenByDescending(item => item.m_durability).First();
        }
    }

    private static bool Usable(ItemDrop.ItemData item) =>
        item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield
        && item.IsEquipable()
        && !(item.m_shared.m_useDurability && item.m_durability <= 0f)
        && (item.m_shared.m_dlc.Length == 0 || DLCMan.instance != null && DLCMan.instance.IsDLCInstalled(item.m_shared.m_dlc));

    private static float Block(ItemDrop.ItemData item) => item.GetBaseBlockPower(item.m_quality);

    private static string Identity(ItemDrop.ItemData item) => $"{item.m_shared.m_name}|{item.m_quality}|{item.m_variant}";

    private static void Log(string message)
    {
        if (DebugLog.On)
        {
            DebugLog.Write("auto shield", message);
        }
    }

    // Every equip passes here: a shield becomes the last used one, and a one-handed weapon taken up by the player marks the hand for a shield. Equips without effects are the game putting back what was already worn (loading the character, dragging an equipped item to another slot), which never asks for a shield.
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
    internal static class EquipPatch
    {
        private static void Postfix(Humanoid __instance, ItemDrop.ItemData item, bool triggerEquipEffects, bool __result)
        {
            if (!__result || __instance != Player.m_localPlayer || item == null)
            {
                return;
            }
            var type = item.m_shared.m_itemType;
            if (type == ItemDrop.ItemData.ItemType.Shield)
            {
                _last = item;
                _lastIdentity = Identity(item);
                // A shield Auto Shield put on itself teaches no pair: only the player's own choice does.
                if (item == _ours)
                {
                    _ours = null;
                }
                else if (ServerRules.On(_enabled, "AutoShield"))
                {
                    Remember(Player.m_localPlayer, Identity(item));
                }
                return;
            }
            if (type != ItemDrop.ItemData.ItemType.OneHandedWeapon || !triggerEquipEffects || item == _hiddenRight || !ServerRules.On(_enabled, "AutoShield"))
            {
                return;
            }
            _pending = item;
        }
    }

    // The player's own toggle (hotbar, inventory click, a loadout) is the one way a worn shield comes off by choice; the game's own removals (a two-hander or bow taking the hand, hiding the hands) go through UnequipItem alone and never pause Auto Shield.
    [HarmonyPatch(typeof(Player), nameof(Player.ToggleEquipped))]
    internal static class TogglePatch
    {
        private static void Prefix(Player __instance, ItemDrop.ItemData item)
        {
            if (__instance != Player.m_localPlayer || item == null || item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Shield || !__instance.IsItemEquiped(item) || __instance.InAttack())
            {
                return;
            }
            _removedAt = Time.time;
            _pending = null;
            Log($"{item.m_shared.m_name} taken off by hand");
            if (ServerRules.On(_enabled, "AutoShield"))
            {
                Remember(__instance, NoShield);
            }
        }
    }
}
