using BepInEx.Configuration;

namespace SeneaLUIUnlocker;

internal enum ServerFilter
{
    All_Servers,
    Only_Listed_Servers,
    All_Except_Listed_Servers
}

internal enum RadarUnlock
{
    Off,
    Minimap_Only,
    On
}

internal sealed class Settings
{
    private const string General = "General";
    private const string Features = "Unlocked Features";
    private const string Limits = "Unlocked Limits";
    private const string Servers = "Servers";
    private const string Interface = "Interface";
    private const string Safety = "Safety";

    public readonly ConfigFile File;
    public readonly ConfigEntry<bool> Unlock;
    public readonly ConfigEntry<float> WaitSeconds;
    public readonly ConfigEntry<bool> UnlockNotice;
    public readonly ConfigEntry<bool> FillUnstatedRules;
    public readonly ConfigEntry<bool> HidePopup;
    public readonly ConfigEntry<bool> MainMenuModSettings;
    public readonly ConfigEntry<bool> MainMenuModSettingsButton;

    public readonly ConfigEntry<bool> PingDroppedItems;
    public readonly ConfigEntry<bool> BackUpCharacter;

    public readonly ConfigEntry<bool> FixLargePresets;
    public readonly ConfigEntry<bool> HideLayoutSettings;
    public readonly ConfigEntry<bool> MergeCraftingElements;

    public readonly ConfigEntry<bool> InventorySize;
    public readonly ConfigEntry<bool> CraftFromChests;
    public readonly ConfigEntry<bool> BuildFromChests;
    public readonly ConfigEntry<bool> QuickStack;
    public readonly ConfigEntry<bool> ShowAllRecipes;
    public readonly ConfigEntry<bool> AreaPickup;
    public readonly ConfigEntry<bool> QuickSlots;
    public readonly ConfigEntry<bool> ActionSlots;
    public readonly ConfigEntry<bool> KeySlots;
    public readonly ConfigEntry<bool> Wallet;
    public readonly ConfigEntry<bool> CoinWeight;
    public readonly ConfigEntry<bool> RadarMonsters;
    public readonly ConfigEntry<bool> OreDetection;
    public readonly ConfigEntry<bool> SmelterAutoFill;
    public readonly ConfigEntry<bool> LiveMarks;
    public readonly ConfigEntry<bool> Coordinates;
    public readonly ConfigEntry<bool> HealthNumbers;
    public readonly ConfigEntry<bool> ChestPreview;
    public readonly ConfigEntry<bool> HoverTimers;
    public readonly ConfigEntry<bool> Compass;
    public readonly ConfigEntry<bool> Clock;
    public readonly ConfigEntry<bool> Recycle;
    public readonly ConfigEntry<bool> RecycleAnyStation;
    public readonly ConfigEntry<bool> RecycleUnknown;

    public readonly ConfigEntry<int> MaxSlots;
    public readonly ConfigEntry<int> MaxQuickSlots;
    public readonly ConfigEntry<int> MaxActionSlots;
    public readonly ConfigEntry<float> CraftRange;
    public readonly ConfigEntry<float> BuildRange;
    public readonly ConfigEntry<float> StackRange;
    public readonly ConfigEntry<float> AreaRadius;
    public readonly ConfigEntry<float> OreRadius;
    public readonly ConfigEntry<RadarUnlock> Radar;
    public readonly ConfigEntry<float> RadarRange;
    public readonly ConfigEntry<int> MaxRecycleRate;

    public readonly ConfigEntry<ServerFilter> Filter;
    public readonly ConfigEntry<string> ListedServers;
    public readonly ConfigEntry<bool> AddCurrentServer;

    public Settings(ConfigFile cfg)
    {
        File = cfg;
        Unlock = cfg.Bind(General, "Unlock SeneaL UI", true,
            "Turn SeneaL UI's features back on when the server does not run SeneaL UI. Off = SeneaL UI keeps its vanilla limits there.");
        WaitSeconds = cfg.Bind(General, "Wait For Server Seconds", 5f, new ConfigDescription(
            "How long to wait for a SeneaL UI server to answer before deciding the server does not run it (SeneaL UI's own wait is 5). A SeneaL UI server that answers later still applies its own rules.",
            new AcceptableValueRange<float>(1f, 30f)));
        FillUnstatedRules = cfg.Bind(General, "Own Settings For Unstated Server Rules", true,
            "On a server that runs SeneaL UI, every rule the server states is kept, and each rule it does not state (an older SeneaL UI server knows fewer) follows your own choice below instead of staying off.");
        UnlockNotice = cfg.Bind(General, "Unlock Notice", true,
            "Show a short message in the top left corner when the features are unlocked on a server.");
        HidePopup = cfg.Bind(General, "Hide No SeneaL UI Popup", true,
            "Hide SeneaL UI's popup saying the server does not run SeneaL UI. Its text lists SeneaL UI's vanilla limits, which this mod lifts.");
        MainMenuModSettings = cfg.Bind(General, "Mod Settings On Main Menu", true,
            "Let SeneaL UI's mod settings key (F1 by default) open its window on the main menu, not only in a world.");
        MainMenuModSettingsButton = cfg.Bind(General, "Mod Settings Button On Main Menu", true,
            "Show a Mod Settings button under the main menu's Settings button that opens SeneaL UI's mod settings window. Needs Mod Settings On Main Menu.");

        PingDroppedItems = cfg.Bind(Safety, "Ping Dropped Items", true,
            "When SeneaL UI drops items because the inventory or its extra slots got smaller, ping the spot (only you see it) and list what dropped.");
        BackUpCharacter = cfg.Bind(Safety, "Back Up Character On New Servers", true,
            "Copy the character file to the profile's BepInEx config folder, under SeneaL UI Unlocker Backups, the first time the unlock applies on each server.");

        FixLargePresets = cfg.Bind(Interface, "Restore Full Interface Presets", true,
            "Early SeneaL UI 1.2.0 previews read only the first 900 settings of a saved interface preset and reset the rest to their defaults; later builds read 6000. On = after a saved preset is applied, the settings it skipped are set from the same file.");
        HideLayoutSettings = cfg.Bind(Interface, "Hide HUD Editor Settings", true,
            "Leave SeneaL UI's HUD editor settings ([Layout], [Elements], [Sizes], [TimeWind], over a thousand entries) out of its page in the mod settings window. The HUD editor and interface presets still use them.");
        MergeCraftingElements = cfg.Bind(Interface, "Merge Crafting Elements", true,
            "Keep SeneaL UI's Crafting window as one HUD editor element with one placement. Turn off to keep one placement beside the inventory and another at crafting stations, where SeneaL UI draws it wider, each placed in the HUD editor in its own case.");

        InventorySize = Feature(cfg, "Inventory Size", "inventory slots beyond the vanilla 32, up to Max Inventory Slots");
        CraftFromChests = Feature(cfg, "Craft From Chests", "crafting and upgrading with materials from nearby chests");
        BuildFromChests = Feature(cfg, "Build From Chests", "building with materials from nearby chests");
        QuickStack = Feature(cfg, "Quick Stack And Restock", "the quick stack and restock buttons reaching into nearby chests");
        ShowAllRecipes = Feature(cfg, "Show All Recipes", "recipes that are not unlocked yet in the crafting list");
        AreaPickup = Feature(cfg, "Area Pickup", "picking everything of a kind around the target at once");
        QuickSlots = Feature(cfg, "Food Quick Slots", "food quick slots beyond the 3 SeneaL UI allows there, up to Max Food Quick Slots");
        ActionSlots = Feature(cfg, "Action Slots", "action slots, up to Max Action Slots");
        KeySlots = Feature(cfg, "Key Slots", "the two slots for dungeon and door keys");
        Wallet = Feature(cfg, "Wallet", "the coin wallet with no stack limit");
        CoinWeight = Feature(cfg, "Coin Weight", "lighter coins ([Inventory] CoinWeight)");
        RadarMonsters = Feature(cfg, "Radar Monsters", "hostile monsters and bosses on SeneaL Radar");
        OreDetection = Feature(cfg, "Ore Detection", "automatic ore detection, up to Max Ore Radius");
        SmelterAutoFill = Feature(cfg, "Smelter Auto Fill", "smelters, fires and cooking stations topping themselves up from nearby chests");
        LiveMarks = Feature(cfg, "Live Marks", "ships, carts and portals on the map, the minimap and the compass");
        Coordinates = Feature(cfg, "Coordinates", "coordinates on the world map and under the minimap");
        HealthNumbers = Feature(cfg, "Health Numbers", "health numbers of creatures, bosses and targets");
        ChestPreview = Feature(cfg, "Chest Preview", "the preview of what is inside a chest or a grave under the crosshair");
        HoverTimers = Feature(cfg, "Hover Timers", "time left and progress of plants, smelters, beehives and the like in the hover text");
        Compass = Feature(cfg, "Compass", "the compass strip at the top of the screen with its markers");
        Clock = Feature(cfg, "Clock", "the time and wind card");
        Recycle = Feature(cfg, "Recycle", "SeneaL Recycle taking crafted items apart into materials, up to Max Recycle Rate");
        RecycleAnyStation = Feature(cfg, "Recycle At Any Station", "SeneaL Recycle working at any crafting station, not only the one an item is crafted at");
        RecycleUnknown = Feature(cfg, "Recycle Unknown Recipes", "SeneaL Recycle taking apart items whose recipe you do not know yet");

        MaxSlots = cfg.Bind(Limits, "Max Inventory Slots", 64, new ConfigDescription(
            "Largest main inventory allowed when Inventory Size is unlocked. Your own SeneaL UI [Inventory] Slots setting picks the size up to this.",
            new AcceptableValueRange<int>(32, 64)));
        MaxQuickSlots = cfg.Bind(Limits, "Max Food Quick Slots", 6, new ConfigDescription(
            "Most food quick slots allowed when Food Quick Slots is unlocked.", new AcceptableValueRange<int>(0, 6)));
        MaxActionSlots = cfg.Bind(Limits, "Max Action Slots", 6, new ConfigDescription(
            "Most action slots allowed when Action Slots is unlocked.", new AcceptableValueRange<int>(0, 6)));
        CraftRange = Range(cfg, "Max Craft Range", 100f, 1f, 100f, "Largest chest range for crafting, meters.");
        BuildRange = Range(cfg, "Max Build Range", 100f, 1f, 100f, "Largest chest range for building, meters.");
        StackRange = Range(cfg, "Max Quick Stack Range", 100f, 1f, 100f, "Largest chest range for quick stack and restock, meters.");
        AreaRadius = Range(cfg, "Max Area Pickup Radius", 10f, 1f, 10f, "Largest area pickup radius, meters.");
        OreRadius = Range(cfg, "Max Ore Radius", 60f, 10f, 60f, "Largest ore detection radius, meters.");
        Radar = cfg.Bind(Limits, "Radar", RadarUnlock.On,
            "SeneaL Radar, when installed: On = creatures on the minimap and the world map, Minimap Only = only on the minimap, Off = the radar shows nothing.");
        RadarRange = Range(cfg, "Max Radar Range", 200f, 10f, 200f, "Largest SeneaL Radar range, meters.");
        MaxRecycleRate = cfg.Bind(Limits, "Max Recycle Rate (%)", 100, new ConfigDescription(
            "Largest share of an item's materials SeneaL Recycle may give back when Recycle is unlocked. Your own SeneaL Recycle share picks the amount up to this.",
            new AcceptableValueRange<int>(0, 100)));

        Filter = cfg.Bind(Servers, "Unlock On", ServerFilter.All_Servers,
            "Which servers without SeneaL UI get the unlock: every one, only the servers in Listed Servers, or every one except them. Each listed server's own Unlock switch can still change it for that server.");
        ListedServers = cfg.Bind(Servers, "Listed Servers", "",
            "Servers with their own switches: whether SeneaL UI is unlocked there (starting from Unlock On), whether its no-SeneaL-UI popup is hidden there, whether ranges go beyond SeneaL UI's maximums there, and the interface preset used there. Add one by typing its address (host:port) or join code, from Add a Known Server, or with Add the Current Server while connected.");
        AddCurrentServer = cfg.Bind(Servers, "Add Current Server", false,
            "Turn on while connected to a server to add it to Listed Servers; it switches itself back off.");
    }

    public ConfigEntryBase[] RuleEntries =>
    [
        InventorySize, CraftFromChests, BuildFromChests, QuickStack, ShowAllRecipes, AreaPickup, QuickSlots, ActionSlots, KeySlots,
        Wallet, CoinWeight, RadarMonsters, OreDetection, SmelterAutoFill, LiveMarks, Coordinates, HealthNumbers, ChestPreview, HoverTimers,
        Compass, Clock, Recycle, RecycleAnyStation, RecycleUnknown,
        MaxSlots, MaxQuickSlots, MaxActionSlots, CraftRange, BuildRange, StackRange, AreaRadius, OreRadius, Radar, RadarRange, MaxRecycleRate
    ];

    public bool AllUnlocked =>
        InventorySize.Value && CraftFromChests.Value && BuildFromChests.Value && QuickStack.Value && ShowAllRecipes.Value
        && AreaPickup.Value && QuickSlots.Value && ActionSlots.Value && KeySlots.Value && Wallet.Value && CoinWeight.Value
        && RadarMonsters.Value && OreDetection.Value && SmelterAutoFill.Value && LiveMarks.Value && Coordinates.Value
        && HealthNumbers.Value && ChestPreview.Value && HoverTimers.Value
        && Compass.Value && Clock.Value && Recycle.Value && RecycleAnyStation.Value && RecycleUnknown.Value && MaxRecycleRate.Value == 100
        && MaxSlots.Value == 64 && MaxQuickSlots.Value == 6 && MaxActionSlots.Value == 6
        && CraftRange.Value >= 100f && BuildRange.Value >= 100f && StackRange.Value >= 100f
        && AreaRadius.Value >= 10f && OreRadius.Value >= 60f && Radar.Value == RadarUnlock.On && RadarRange.Value >= 200f;

    private static ConfigEntry<bool> Feature(ConfigFile cfg, string key, string what) =>
        cfg.Bind(Features, key, true, $"On servers without SeneaL UI, unlock {what}. Off = it stays as SeneaL UI limits it there.");

    private static ConfigEntry<float> Range(ConfigFile cfg, string key, float value, float min, float max, string desc) =>
        cfg.Bind(Limits, key, value, new ConfigDescription(desc, new AcceptableValueRange<float>(min, max)));
}
