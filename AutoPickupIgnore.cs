using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// Items the game's auto-pickup leaves on the ground, chosen one by one by the name the game shows (stored as the name token, so every prefab under that name is covered), while auto-pickup still takes everything else and any item can still be picked up by hand. The game asks ItemDrop.CanPickup for each item in range from Player.AutoPickup; inside that call alone an ignored item answers no.
internal static class AutoPickupIgnore
{
    private static ConfigEntry<string> _ignored;
    private static HashSet<string> _set = new(System.StringComparer.OrdinalIgnoreCase);
    private static bool _inAutoPickup;

    public static void Bind(ConfigFile cfg)
    {
        _ignored = cfg.Bind("Auto Pickup", "Never Auto Pickup", "",
            "Items auto-pickup leaves on the ground; you can still pick them up by hand.");
        _ignored.SettingChanged += (_, _) => _set = SettingsPage.Split(_ignored.Value);
        _set = SettingsPage.Split(_ignored.Value);
    }

    private static HashSet<string> _labels;
    private static HashSet<string> _labelsFor;
    private static readonly Dictionary<string, string> ShownCache = new();

    private static string Shown(string token)
    {
        if (!ShownCache.TryGetValue(token, out var name))
        {
            name = Localization.instance == null ? token : Localization.instance.Localize(token);
            ShownCache[token] = name;
        }
        return name;
    }

    // The shown names of the chosen tokens, so another token under the same name (another mod's copy of an item) is left too.
    private static HashSet<string> Labels()
    {
        if (_labelsFor != _set)
        {
            _labelsFor = _set;
            _labels = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var value in _set)
            {
                if (value.StartsWith("$", System.StringComparison.Ordinal))
                {
                    _labels.Add(Shown(value));
                }
            }
        }
        return _labels;
    }

    [HarmonyPatch(typeof(Player), nameof(Player.AutoPickup))]
    internal static class AutoPickupPatch
    {
        private static void Prefix() => _inAutoPickup = true;

        private static void Finalizer() => _inAutoPickup = false;
    }

    [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.CanPickup))]
    internal static class CanPickupPatch
    {
        private static void Postfix(ItemDrop __instance, ref bool __result)
        {
            if (!_inAutoPickup || !__result || _set.Count == 0 || !ServerRules.Allows("AutoPickupIgnore") || __instance.m_itemData == null)
            {
                return;
            }
            var data = __instance.m_itemData;
            if (_set.Contains(data.m_shared.m_name) || Labels().Contains(Shown(data.m_shared.m_name)) || (data.m_dropPrefab != null && _set.Contains(data.m_dropPrefab.name)))
            {
                __result = false;
            }
        }
    }
}
