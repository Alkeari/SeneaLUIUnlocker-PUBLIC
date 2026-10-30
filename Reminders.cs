using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// Warnings for things running low on the player's own character: worn gear, food and timed effects. Each warns once as it crosses its threshold and again only after it recovers.
internal sealed class Reminders
{
    private const string Section = "Reminders";
    private const float CheckSeconds = 1f;

    private static readonly System.Type Slots = TypeLookup.Find("SeneaLUI.Inv.Slots");
    private static readonly PropertyInfo QuickCount = Slots == null ? null : AccessTools.Property(Slots, nameof(SeneaLUI.Inv.Slots.QuickCount));
    private static readonly MethodInfo QuickItem = Slots == null ? null : AccessTools.Method(Slots, nameof(SeneaLUI.Inv.Slots.QuickItem), [typeof(Inventory), typeof(int)]);

    private readonly ConfigEntry<bool> _durability;
    private readonly ConfigEntry<float> _durabilityPercent;
    private readonly ConfigEntry<bool> _food;
    private readonly ConfigEntry<float> _foodSeconds;
    private readonly ConfigEntry<bool> _autoEat;
    private readonly ConfigEntry<bool> _rested;
    private readonly ConfigEntry<bool> _buffs;
    private readonly ConfigEntry<float> _effectSeconds;
    private readonly ConfigEntry<string> _ignoredEffects;

    private readonly HashSet<ItemDrop.ItemData> _worn = [];
    private readonly HashSet<string> _warned = [];
    private float _next;

    public Reminders(ConfigFile cfg)
    {
        _durability = cfg.Bind(Section, "Low Durability Warning", true, "Warn when a piece of worn or held gear falls below Low Durability Percent.");
        _durabilityPercent = cfg.Bind(Section, "Low Durability Percent", 20f, new ConfigDescription("Durability share, percent, below which gear is warned about.", new AcceptableValueRange<float>(1f, 90f)));
        _food = cfg.Bind(Section, "Food Reminder", true, "Warn when an eaten food has less than Food Reminder Seconds left.");
        _foodSeconds = cfg.Bind(Section, "Food Reminder Seconds", 60f, new ConfigDescription("Seconds left on a food at which it is warned about.", new AcceptableValueRange<float>(5f, 600f)));
        _autoEat = cfg.Bind(Section, "Auto Eat From Food Slots", false,
            "When a food runs low or a food slot is free, eat the first food in SeneaL UI's food quick slots that the game lets you eat. Off by default because it uses up food on its own.");
        _rested = cfg.Bind(Section, "Rested Reminder", true, "Warn when Rested has less than Effect Reminder Seconds left.");
        _buffs = cfg.Bind(Section, "Buff Reminder", true, "Warn when any other timed effect, such as a mead, has less than Effect Reminder Seconds left, except those in Ignored Effects.");
        _effectSeconds = cfg.Bind(Section, "Effect Reminder Seconds", 30f, new ConfigDescription("Seconds left on a timed effect at which it is warned about.", new AcceptableValueRange<float>(5f, 600f)));
        _ignoredEffects = cfg.Bind(Section, "Ignored Effects", "Wet; Cold; Freezing; Smoked; Burning; Poisoned; Frost; Shocked; Tared; Encumbered",
            "Timed effects Buff Reminder never warns about, by the name the game shows, separated by semicolons.");
    }

    public void Tick(float now)
    {
        var player = Player.m_localPlayer;
        if (player == null || now < _next)
        {
            return;
        }
        _next = now + CheckSeconds;
        if (_durability.Value && ServerRules.Allows("Reminders"))
        {
            Durability(player);
        }
        Foods(player);
        if ((_rested.Value || _buffs.Value) && ServerRules.Allows("Reminders"))
        {
            Effects(player);
        }
    }

    private void Durability(Player player)
    {
        var limit = _durabilityPercent.Value / 100f;
        foreach (var item in player.GetInventory().GetEquippedItems())
        {
            var max = item.GetMaxDurability();
            if (!item.m_shared.m_useDurability || max <= 0f)
            {
                continue;
            }
            var low = item.m_durability / max < limit;
            if (low && _worn.Add(item))
            {
                player.Message(MessageHud.MessageType.TopLeft, Lang.F("Reminders.IsAtDurability", "{0} is at {1}% durability", Name(item.m_shared.m_name), UnityEngine.Mathf.RoundToInt(item.m_durability / max * 100f)));
            }
            else if (!low)
            {
                _worn.Remove(item);
            }
        }
    }

    private void Foods(Player player)
    {
        var foods = player.GetFoods();
        var lowAny = false;
        foreach (var food in foods)
        {
            var key = "food:" + food.m_name;
            var low = food.m_time < _foodSeconds.Value;
            lowAny |= low;
            Once(player, key, low && _food.Value && ServerRules.Allows("Reminders"), Lang.F("Reminders.FoodRunsOut", "{0} runs out in {1} s", Name(food.m_item.m_shared.m_name), UnityEngine.Mathf.CeilToInt(food.m_time)));
        }
        if (_autoEat.Value && ServerRules.Allows("AutoEat") && (lowAny || foods.Count < 3))
        {
            EatFromQuickSlots(player);
        }
    }

    private static void EatFromQuickSlots(Player player)
    {
        if (QuickCount == null || QuickItem == null)
        {
            return;
        }
        var inv = player.GetInventory();
        var count = (int)QuickCount.GetValue(null, null);
        for (var i = 0; i < count; i++)
        {
            if (QuickItem.Invoke(null, [inv, i]) is ItemDrop.ItemData item && player.CanEat(item, false))
            {
                player.UseItem(inv, item, false);
                return;
            }
        }
    }

    private void Effects(Player player)
    {
        var ignored = _ignoredEffects.Value.Split(';').Select(part => part.Trim().ToLowerInvariant()).Where(part => part.Length > 0).ToHashSet();
        var active = new HashSet<string>();
        foreach (var effect in player.GetSEMan().GetStatusEffects().Where(effect => effect.m_ttl > 0f))
        {
            var name = Name(effect.m_name);
            var rested = effect.m_name == "$se_rested_name";
            var wanted = rested ? _rested.Value : _buffs.Value && !ignored.Contains(name.ToLowerInvariant());
            var key = "effect:" + effect.m_name;
            active.Add(key);
            Once(player, key, wanted && effect.GetRemaningTime() < _effectSeconds.Value, Lang.F("Reminders.EffectEnds", "{0} ends in {1} s", name, UnityEngine.Mathf.CeilToInt(effect.GetRemaningTime())));
        }
        _warned.RemoveWhere(key => key.StartsWith("effect:") && !active.Contains(key));
    }

    private void Once(Player player, string key, bool low, string text)
    {
        if (low && _warned.Add(key))
        {
            player.Message(MessageHud.MessageType.TopLeft, text);
        }
        else if (!low)
        {
            _warned.Remove(key);
        }
    }

    private static string Name(string token) => Localization.instance.Localize(token);
}
