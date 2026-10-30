using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// A server that runs this plugin decides which of its features its players may use. Each feature has an Allow setting in [Server Rules], sent to the players who join while this game hosts the world (a dedicated server or a session opened to friends): a feature that is not a cheat is allowed and a cheat is denied until the host says otherwise. A joining player asks over the connection's own RPC; a server without this plugin never registered it and drops the request without a word, so on such a server every feature follows the player's own settings.
internal static class ServerRules
{
    private const string Section = "Server Rules";
    private const string Rpc = "alkeari.senealuiunlocker.rules";

    // Every feature a server can allow or deny, by the key the code checks and the name the host sees. Cheats are listed last.
    public static readonly (string Key, string Name, bool Cheat)[] Features =
    [
        ("Unlock", "Unlocking SeneaL UI", false),
        ("InventorySize", "Larger Inventory", false),
        ("CraftFromChests", "Crafting From Chests", false),
        ("BuildFromChests", "Building From Chests", false),
        ("QuickStack", "Quick Stack And Restock", false),
        ("ShowAllRecipes", "Show All Recipes", false),
        ("AreaPickup", "Area Pickup", false),
        ("QuickSlots", "Food Quick Slots", false),
        ("ActionSlots", "Action Slots", false),
        ("KeySlots", "Key Slots", false),
        ("Wallet", "Wallet", false),
        ("CoinWeight", "Coin Weight", false),
        ("Radar", "Radar", false),
        ("RadarMonsters", "Radar Monsters", false),
        ("OreDetection", "Ore Detection", false),
        ("SmelterAutoFill", "Smelter Auto Fill", false),
        ("LiveMarks", "Live Marks", false),
        ("Coordinates", "Coordinates", false),
        ("HealthNumbers", "Health Numbers", false),
        ("ChestPreview", "Chest Preview", false),
        ("HoverTimers", "Hover Timers", false),
        ("Compass", "Compass", false),
        ("Clock", "Clock", false),
        ("Recycle", "Recycle", false),
        ("RecycleAnyStation", "Recycle At Any Station", false),
        ("RecycleUnknown", "Recycle Unknown Recipes", false),
        ("Sorting", "Sort By Value", false),
        ("AutoSortChests", "Auto Sort Chests", false),
        ("Restock", "Restock To Targets", false),
        ("MultiSelect", "Multi-Select And Slot Locks", false),
        ("SplitWheel", "Split Wheel", false),
        ("ItemComparison", "Item Comparison", false),
        ("Loadouts", "Equipment Loadouts", false),
        ("AutoShield", "Auto Shield", false),
        ("Cosmetics", "Cosmetic Equipment", false),
        ("TrashRecycle", "Trash Recycle Mode", false),
        ("AutoPickupIgnore", "Never Auto Pickup", false),
        ("StashButton", "Stash Button", false),
        ("RenameLinkedPortals", "Rename Linked Portals", false),
        ("Reminders", "Reminders", false),
        ("AutoEat", "Auto Eat", false),
        ("Tracker", "Tracker", false),
        ("FoodPlanner", "Food Planner", false),
        ("DeathReport", "Death Report And Preview", false),
        ("Progress", "Progress On Esc Menu", false),
        ("MapSearch", "Map Search And Pin Extras", false),
        ("AutoPins", "Auto Pins", false),
        ("LivePins", "Live Pins", false),
        ("DepletedOrePins", "Depleted Ore Pin Removal", false),
        ("DeathPins", "Death Pin Removal", false),
        ("RangeRings", "Range Rings On Map", false),
        ("Tames", "Tames On Map", false),
        ("Cartography", "Cartography Table Pins", false),
        ("Party", "Party, Party HUD And Party Chat", false),
        ("Chat", "Chat Filters, Mentions And Friends", false),
        ("EditorAdditions", "HUD Editor Additions", false),
        ("CombatHud", "Combat Only Elements", false),
        ("CraftingPlacement", "Two Crafting Placements", false),
        ("Previews", "Area Previews", false),
        ("FuelHelper", "Fuel Helper", false),
        ("ToolSwap", "Tool Swap", false),
        ("AutoRepair", "Auto Repair At Stations", false),
        ("RadarExtras", "Radar Alerts And Ignore List", false),
        ("PlacePresets", "Interface Presets Per Place", false),
        ("SeneaLKeys", "SeneaL UI Keys", false),
        ("SmartCraftChests", "SmartCraftStorage Chests", false),
        ("MultiCraftAmount", "SeneaL UI Craft Amount With MultiCraft", false),
        ("AccuratePlacement", "Accurate Build Restrictions", false),
        ("UndoRemove", "Undo Remove", false),
        ("AutoDoors", "Auto Doors", false),
        ("TameInfo", "Tame Details", false),
        ("ProductionAlerts", "Production Alerts", false),
        ("FindInChests", "Find In Chests", false),
        ("PinLookedAt", "Pin Looked-At", false),
        ("SignPins", "Sign Pins", false),
        ("DroppedItemMarkers", "Dropped Item Markers", false),
        ("RecipeLookup", "Recipe Command", false),
        ("OldBedPins", "Keep Old Bed Pins", false),
        ("HotbarSwap", "Hotbar Swap", false),
        ("BaseStatus", "Base Status", false),
        ("SkillProgress", "Skill Progress", false),
        ("ShelterComfort", "Shelter And Comfort", false),
        ("ChestNames", "Chest Names", false),
        ("VehicleNames", "Vehicle Names", false),
        ("CycleChests", "Cycle Chests", false),
        ("BuildCamera", "Build Camera", false),
        ("TameCommand", "Tame Command Key", false),
        ("RepairNearby", "Repair Nearby", false),
        ("UnrestrictedBuilding", "Unrestricted Building (Cheat)", true),
        ("TeleportFromMap", "Teleport From Map (Cheat)", true),
        ("ExplorationRadius", "Exploration Radius (Cheat)", true),
        ("BeyondMaximums", "Beyond SeneaL UI Maximums (Cheat)", true),
        ("OresThroughPortals", "Ores And Metals Through Portals (Cheat)", true),
        ("CartsThroughPortals", "Carts Through Portals (Cheat)", true),
        ("TamesThroughPortals", "Tames Through Portals (Cheat)", true),
        ("InfiniteCarry", "Infinite Carry Weight (Cheat)", true),
        ("NoEncumbrance", "No Encumbrance (Cheat)", true),
        ("NoSkillLoss", "No Skill Loss On Death (Cheat)", true),
        ("NoDurabilityLoss", "No Durability Loss (Cheat)", true),
        ("UnlimitedStamina", "Unlimited Stamina (Cheat)", true),
        ("NoFallDamage", "No Fall Damage (Cheat)", true),
        ("WeatherForecast", "Weather Forecast (Cheat)", true),
        ("CycleChestsBeyondReach", "Cycle Chests Beyond Reach (Cheat)", true)
    ];

    private static readonly Dictionary<string, ConfigEntry<bool>> Hosted = new(StringComparer.Ordinal);
    private static Dictionary<string, bool> _received;
    private static ManualLogSource _log;

    public static event Action Changed;

    public static void Bind(ConfigFile cfg, ManualLogSource log)
    {
        _log = log;
        foreach (var (key, name, cheat) in Features)
        {
            Hosted[key] = cfg.Bind(Section, "Allow " + name, !cheat,
                $"While this game hosts the world, whether players with this mod may use {name}. Off turns it off for every player of this mod here. {(cheat ? "A cheat, so off unless you allow it." : "On unless you deny it.")}");
            Hosted[key].SettingChanged += (_, _) => SendToAll();
        }
    }

    // Whether this feature may run: the hosting server's answer when it runs this plugin, and allowed everywhere else. A game that hosts plays by its own settings; its [Server Rules] are for the players who join it.
    public static bool Allows(string key)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer())
        {
            return true;
        }
        return _received == null || !_received.TryGetValue(key, out var allowed) || allowed;
    }

    public static bool On(ConfigEntry<bool> setting, string key) => setting.Value && Allows(key);

    // Names of the features this server turns off, for the player's notice.
    public static List<string> Denied() =>
        _received == null ? [] : Features.Where(feature => _received.TryGetValue(feature.Key, out var allowed) && !allowed).Select(feature => feature.Name).ToList();

    private static ZPackage Package()
    {
        var package = new ZPackage();
        package.Write(1);
        package.Write(Hosted.Count);
        foreach (var pair in Hosted)
        {
            package.Write(pair.Key);
            package.Write(pair.Value.Value);
        }
        return package;
    }

    private static void SendToAll()
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }
        foreach (var peer in ZNet.instance.GetPeers())
        {
            peer.m_rpc.Invoke(Rpc, Package());
        }
        Changed?.Invoke();
    }

    private static void Read(ZRpc rpc, ZPackage package)
    {
        if (ZNet.instance == null)
        {
            return;
        }
        if (ZNet.instance.IsServer())
        {
            // A player asking; the answer carries this server's rules.
            rpc.Invoke(Rpc, Package());
            return;
        }
        try
        {
            if (package.ReadInt() < 1)
            {
                return;
            }
            var rules = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (var count = package.ReadInt(); count > 0; count--)
            {
                rules[package.ReadString()] = package.ReadBool();
            }
            _received = rules;
        }
        catch (Exception e)
        {
            _log?.LogWarning($"Could not read the server's rules for this mod ({e.GetBaseException().Message}); every feature follows your own settings.");
            return;
        }
        var denied = Denied();
        _log?.LogInfo(denied.Count == 0 ? "The server runs this mod and allows every feature." : $"The server runs this mod and turns off: {string.Join(", ", denied)}.");
        if (denied.Count > 0 && Player.m_localPlayer != null)
        {
            Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, Lang.F("ServerRules.TurnsOff", "This server turns off: {0}", string.Join(", ", denied)));
        }
        Changed?.Invoke();
    }

    // The game's own connection method: both sides register the rules RPC on the new connection, and a joining player asks for them.
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
    internal static class ConnectPatch
    {
        private static void Postfix(ZNet __instance, ZNetPeer peer)
        {
            peer.m_rpc.Register<ZPackage>(Rpc, Read);
            if (!__instance.IsServer())
            {
                _received = null;
                peer.m_rpc.Invoke(Rpc, new ZPackage());
            }
        }
    }
}
