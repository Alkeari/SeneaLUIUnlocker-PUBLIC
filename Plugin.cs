using BepInEx;
using HarmonyLib;

namespace SeneaLUIUnlocker;

[BepInPlugin(Guid, Name, PluginVersion.Value)]
[BepInProcess("valheim.exe")]
[BepInProcess("valheim_server.exe")]
[BepInDependency(SeneaLUiGuid)]
[BepInDependency(RadarAlerts.RadarGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(InputTweaksCompat.InputTweaksGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(TerrainToolsCosts.TerrainToolsGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(SmartCraftChests.SmartCraftGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(MultiCraftCompat.MultiCraftGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(SmartCraftChests.BridgeGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(MultiCraftCompat.BridgeGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(ModOverrides.QuickStackStoreGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(ModOverrides.ExtraSlotsGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(ModOverrides.MyLittleUiGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(ModOverrides.ZenUiGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(ModOverrides.AutomaticFuelGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(ModOverrides.ChatterGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(KeyBindManagerGuard.KeyBindManagerGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(DadsFppCompat.DadsFppGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(DadsSkillsCompat.DadsSkillsGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(BadgersShadingCompat.BadgersShadingGuid, BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "alkeari.valheim.senealuiunlocker";
    public const string Name = "SeneaL UI Unlocker";
    internal const string SeneaLUiGuid = "seneaL.valheim.ui";

    internal static PolicyUnlock Unlock { get; private set; }

    private Settings _settings;
    private ServerDirectory _servers;
    private MainMenuModSettings _modSettings;
    private PresetFix _presets;
    private LayoutSettingsHider _layoutHider;
    private CraftingPlacement _crafting;
    private CharacterBackup _backup;
    private InventoryFeatures _inventory;
    private Reminders _reminders;
    private InterfacePerPlace _interfacePerPlace;
    private RecipePinsPerCharacter _pins;
    private DeathPinCleanup _deathPins;
    private StationAutoRepair _repair;
    private SplitWheel _splitWheel;
    private Loadouts _loadouts;
    private CombatHud _combatHud;
    private DepletedOrePins _orePins;
    private UpdateNotice _updateNotice;
    private RadarAlerts _radar;
    private bool _inputTweaks;
    private SlotLock _slotLock;
    private TerrainToolsCosts _terrainCosts;
    private TerrainToolsRoom _terrainRoom;
    private Party _party;
    private PartyHud _partyHud;
    private PartyMap _partyMap;
    private PartyShare _partyShare;
    private PreviewWatermark _watermark;
    private IEditorBridge _hudEditor;
    private IBuiltInPreset _preset;
    private ISettingsPage _settingsPage;
    private IPinChips _pinChips;
    private ICosmeticsPanel _cosmetics;
    private ICompareCards _compare;
    private ITrackerHud _tracker;
    private FuelHelper _fuel;
    private WorldPreviews _previews;
    private AutoDoors _doors;
    private ProductionAlerts _alerts;
    private FindInChests _find;
    private PinLookedAt _pinLookedAt;
    private RecipeLookup _recipes;
    private BedAndHotbar _bedAndHotbar;
    private StatusCards _statusCards;
    private IStashButton _stash;
    private TameTracker _tames;
    private IFoodPanel _food;
    private MapCheats _mapCheats;
    private IMapSearch _mapSearch;
    private IMapRanges _mapRanges;
    private IMapPinExtras _pinExtras;
    private ChatPlus _chat;
    private ChatExtras _chatExtras;
    private AutoPins _autoPins;
    private LivePins _livePins;
    private LowDurabilityTint _durabilityTint;
    private BeyondMaximums _beyond;
    private SeneaLKeys _seneaLKeys;
    private IGrowCorners _growCorners;
    private SmartCraftChests _smartCraft;
    private Harmony _harmony;
    private readonly System.Collections.Generic.HashSet<string> _failed = [];

    private void Awake()
    {
        Lang.Log = Logger;
        DebugLog.Start(Config, Logger);
        ServerRules.Bind(Config, Logger);
        // A dedicated server only hands its rules to joining players; every feature of this plugin is the player's.
        if (Paths.ProcessName == "valheim_server")
        {
            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(ServerRules.ConnectPatch));
            Logger.LogInfo($"{Name} {PluginVersion.Value} loaded on a dedicated server: it sends its [Server Rules] to players with this mod.");
            return;
        }
        _settings = new Settings(Config);
        _servers = new ServerDirectory(Config, _settings, Logger);
        _modSettings = MainMenuModSettings.Resolve(Logger);
        _presets = new PresetFix(_settings.FixLargePresets, Logger);
        _layoutHider = new LayoutSettingsHider(_settings.HideLayoutSettings);
        _crafting = new CraftingPlacement(Config, _settings.MergeCraftingElements);
        _backup = new CharacterBackup(Config, _settings.BackUpCharacter, Logger);
        DroppedItemPing.Bind(_settings.PingDroppedItems);
        _inventory = new InventoryFeatures(Config, Logger);
        _reminders = new Reminders(Config);
        _durabilityTint = new LowDurabilityTint(Config);
        // A SeneaL UI build without interface presets or recipe pins (1.1.7) gets neither setting, since either would do nothing there.
        _interfacePerPlace = InterfacePerPlace.Available ? new InterfacePerPlace(Config, Logger) : null;
        _pins = RecipePinsPerCharacter.Available ? new RecipePinsPerCharacter(Config) : null;
        _deathPins = new DeathPinCleanup(Config, Logger);
        _repair = new StationAutoRepair(Config, Logger);
        _splitWheel = new SplitWheel(Config);
        _loadouts = new Loadouts(Config);
        _combatHud = new CombatHud(Config);
        _orePins = new DepletedOrePins(Config);
        _updateNotice = new UpdateNotice(Config, Logger);
        _radar = RadarAlerts.Resolve(Config, Logger);
        _inputTweaks = InputTweaksCompat.Resolve(Config, Logger);
        _terrainCosts = TerrainToolsCosts.Resolve(Config, Logger);
        _terrainRoom = TerrainToolsRoom.Resolve(Config);
        ChatFilters.Bind(Config);
        _party = new Party(Config, Logger);
        _partyHud = new PartyHud(Config, _party);
        _partyMap = new PartyMap(Config, _party);
        _partyShare = new PartyShare(Config, _party);
        _watermark = new PreviewWatermark(Config);
        _autoPins = new AutoPins(Config, Logger);
        _livePins = new LivePins(Config);
        TrashRecycle.Bind(Config);
        PinKinds.Bind(Config);
        RichText.Bind(Config);
        Cosmetics.Bind(Config);
        ItemCompare.Bind(Config);
        Tracker.Bind(Config);
        AutoPickupIgnore.Bind(Config);
        DeathReport.Bind(Config);
        AreaPreview.Bind(Config);
        PortalRules.Bind(Config);
        PortalTravel.Bind(Config);
        ToolSwap.Bind(Config);
        AutoShield.Bind(Config);
        CarryCheats.Bind(Config);
        BuildRestrictions.Bind(Config);
        UndoRemove.Bind(Config);
        TameInfo.Bind(Config);
        Accessibility.Bind(Config);
        MoreCheats.Bind(Config);
        ChestNames.Bind(Config);
        VehicleNames.Bind(Config);
        CycleChests.Bind(Config);
        SignPins.Bind(Config);
        DroppedItemMarkers.Bind(Config);
        RenameLinkedPortals.Bind(Config);
        BuildCamera.Bind(Config);
        BuildSections.Bind(Config);
        RepairNearby.Bind(Config);
        Progress.Bind(Config);
        ChatBubbles.Bind(Config);
        ResolutionScale.Bind(Config);
        LayoutArea.Bind(Config);
        ElementLook.Bind(Config);
        FoodPlanner.Bind(Config);
        Cartography.Bind(Config);
        _fuel = new FuelHelper(Config);
        _previews = new WorldPreviews(Config);
        _doors = new AutoDoors(Config);
        _alerts = new ProductionAlerts(Config);
        _find = new FindInChests(Config);
        _pinLookedAt = new PinLookedAt(Config);
        _recipes = new RecipeLookup(Config);
        _bedAndHotbar = new BedAndHotbar(Config);
        _statusCards = new StatusCards(Config);
        SkillProgress.Bind(Config);
        ShelterComfort.Bind(Config);
        _tames = new TameTracker(Config);
        _mapCheats = new MapCheats(Config);
        _chat = new ChatPlus(Config, _party);
        _chatExtras = new ChatExtras(Config);
        if (MultiSelectKey.Available)
        {
            MultiSelectKey.Bind(Config, Logger);
        }
        if (KeyHintsWithInventory.Available)
        {
            KeyHintsWithInventory.Bind(Config);
        }
        if (SlotLock.Available)
        {
            _slotLock = new SlotLock(Config);
        }
        else
        {
            Logger.LogWarning("This SeneaL UI build's slot locks or multi-select differ from the ones this mod knows; Lock Selected Slots Together and Select Empty and Locked Slots are off.");
        }
        try
        {
            _hudEditor = CreateHudEditor();
        }
        catch (System.Exception e)
        {
            Logger.LogWarning($"This SeneaL UI build's HUD editor differs from the one this mod knows ({e.GetBaseException().Message}); SeneaL UI's own editor stays in use.");
        }
        try
        {
            _preset = CreatePreset();
        }
        catch (System.Exception e)
        {
            Logger.LogWarning($"The SeneaL UI Unlocker interface preset is not offered with this SeneaL UI build ({e.GetBaseException().Message}).");
        }
        try
        {
            _settingsPage = CreateSettingsPage();
        }
        catch (System.Exception e)
        {
            Logger.LogWarning($"This SeneaL UI build's mod settings window differs from the one this mod knows ({e.GetBaseException().Message}); its settings show in SeneaL UI's own list.");
        }
        try
        {
            _cosmetics = CreateCosmetics();
        }
        catch (System.Exception e)
        {
            Logger.LogWarning($"This SeneaL UI build's windows differ from the ones this mod knows ({e.GetBaseException().Message}); the Cosmetics window is off.");
        }
        try
        {
            _compare = CreateCompare();
        }
        catch (System.Exception e)
        {
            Logger.LogWarning($"This SeneaL UI build's tooltip differs from the one this mod knows ({e.GetBaseException().Message}); item comparison is off.");
        }
        try
        {
            _tracker = CreateTracker();
        }
        catch (System.Exception e)
        {
            Logger.LogWarning($"This SeneaL UI build's crafting window differs from the one this mod knows ({e.GetBaseException().Message}); the Tracker is off.");
        }
        try
        {
            _stash = CreateStash();
        }
        catch (System.Exception e)
        {
            Logger.LogWarning($"This SeneaL UI build's inventory window differs from the one this mod knows ({e.GetBaseException().Message}); the stash button is off.");
        }
        try
        {
            _food = CreateFood();
        }
        catch (System.Exception e)
        {
            Logger.LogWarning($"This SeneaL UI build's windows differ from the ones this mod knows ({e.GetBaseException().Message}); the Food window is off.");
        }
        try
        {
            _mapSearch = CreateMapSearch(Config);
        }
        catch (System.Exception e)
        {
            Logger.LogWarning($"This SeneaL UI build's world map differs from the one this mod knows ({e.GetBaseException().Message}); pin search is off.");
        }
        try
        {
            _pinExtras = CreatePinExtras();
        }
        catch (System.Exception e)
        {
            Logger.LogWarning($"This SeneaL UI build's world map differs from the one this mod knows ({e.GetBaseException().Message}); the pin extras are off.");
        }
        try
        {
            _mapRanges = CreateMapRanges();
        }
        catch (System.Exception e)
        {
            Logger.LogWarning($"This SeneaL UI build's world map differs from the one this mod knows ({e.GetBaseException().Message}); range rings on the map are off.");
        }
        try
        {
            _pinChips = CreatePinChips();
        }
        catch (System.Exception e)
        {
            Logger.LogWarning($"This SeneaL UI build's world map differs from the one this mod knows ({e.GetBaseException().Message}); auto-pin kinds get no chips on it.");
        }
        Unlock = PolicyUnlock.Resolve(Logger, _settings, _servers);
        _beyond = BeyondMaximums.Resolve(Config, Logger, Unlock);
        _seneaLKeys = SeneaLKeys.Resolve(Config, Logger, Unlock);
        _smartCraft = SmartCraftChests.Resolve(Config, Logger, Unlock);
        if (Unlock != null)
        {
            try
            {
                _growCorners = CreateGrowCorners();
            }
            catch (System.Exception e)
            {
                Logger.LogWarning($"This SeneaL UI build's element placement differs from the one this mod knows ({e.GetBaseException().Message}); the corner grow choices are off.");
            }
        }
        _harmony = new Harmony(Guid);
        _harmony.PatchAll(typeof(ServerRules.ConnectPatch));
        _harmony.PatchAll(typeof(ServerDirectory.JoinPatch));
        _harmony.PatchAll(typeof(DroppedItemPing.DropPatch));
        _harmony.PatchAll(typeof(TrashRecycle.RemovePatch));
        _harmony.PatchAll(typeof(PinKinds.SavePatch));
        _harmony.PatchAll(typeof(PinKinds.SharePatch));
        _harmony.PatchAll(typeof(PinKinds.UpdatePatch));
        _harmony.PatchAll(typeof(LivePins.ResetPatch));
        _harmony.PatchAll(typeof(RichText.TextPatch));
        _harmony.PatchAll(typeof(Cosmetics.LookPatch));
        _harmony.PatchAll(typeof(DeathReport.DeathPatch));
        _harmony.PatchAll(typeof(ToolSwap.AttackPatch));
        _harmony.PatchAll(typeof(AutoShield.EquipPatch));
        _harmony.PatchAll(typeof(AutoShield.TogglePatch));
        _harmony.PatchAll(typeof(CarryCheats.WeightPatch));
        _harmony.PatchAll(typeof(CarryCheats.EncumberedPatch));
        _harmony.PatchAll(typeof(BuildRestrictions.SpacingPatch));
        _harmony.PatchAll(typeof(BuildRestrictions.GhostPatch));
        _harmony.PatchAll(typeof(BuildRestrictions.StationPatch));
        _harmony.PatchAll(typeof(UndoRemove.RemovePatch));
        _harmony.PatchAll(typeof(UndoRemove.WearPatch));
        _harmony.PatchAll(typeof(UndoRemove.DropPatch));
        _harmony.PatchAll(typeof(TameInfo.HoverPatch));
        _harmony.PatchAll(typeof(TameInfo.YoungHoverPatch));
        _harmony.PatchAll(typeof(Accessibility.ShakePatch));
        _harmony.PatchAll(typeof(Accessibility.SmokePatch));
        _harmony.PatchAll(typeof(Accessibility.FlashPatch));
        _harmony.PatchAll(typeof(SplitWheel.ArrowPatch));
        _harmony.PatchAll(typeof(MoreCheats.SkillPatch));
        _harmony.PatchAll(typeof(MoreCheats.StaminaPatch));
        _harmony.PatchAll(typeof(MoreCheats.FallPatch));
        _harmony.PatchAll(typeof(ChestNames.HoverPatch));
        _harmony.PatchAll(typeof(ChestNames.OpenPatch));
        _harmony.PatchAll(typeof(VehicleNames.RudderHoverPatch));
        _harmony.PatchAll(typeof(VehicleNames.CartHoverPatch));
        _harmony.PatchAll(typeof(VehicleNames.StorageHoverPatch));
        _harmony.PatchAll(typeof(VehicleNames.OpenPatch));
        _harmony.PatchAll(typeof(SignPins.SetTextPatch));
        _harmony.PatchAll(typeof(ChatBubbles.AddPatch));
        _harmony.PatchAll(typeof(ChatBubbles.PlacePatch));
        _harmony.PatchAll(typeof(SceneObjects.PortalPatch));
        _harmony.PatchAll(typeof(SceneObjects.ShipPatch));
        _harmony.PatchAll(typeof(SceneObjects.ContainerPatch));
        _harmony.PatchAll(typeof(SceneObjects.FirePatch));
        _harmony.PatchAll(typeof(SceneObjects.GravePatch));
        _harmony.PatchAll(typeof(SceneObjects.PickablePatch));
        _harmony.PatchAll(typeof(SceneObjects.SmelterPatch));
        _harmony.PatchAll(typeof(SceneObjects.FermenterPatch));
        _harmony.PatchAll(typeof(SceneObjects.CookingPatch));
        _harmony.PatchAll(typeof(DroppedItemMarkers.DropPatch));
        _harmony.PatchAll(typeof(DroppedItemMarkers.PlayerDropPatch));
        _harmony.PatchAll(typeof(RenameLinkedPortals.SetTextPatch));
        _harmony.PatchAll(typeof(PortalTravel.TeleportPatch));
        _harmony.PatchAll(typeof(BuildCamera.CameraPatch));
        _harmony.PatchAll(typeof(BuildCamera.ControlsPatch));
        _harmony.PatchAll(typeof(BuildSections.TabPatch));
        _harmony.PatchAll(typeof(Cartography.ReadPatch));
        _harmony.PatchAll(typeof(Tracker.KillPatch));
        _harmony.PatchAll(typeof(SkillProgress.RaisePatch));
        try
        {
            PatchHoverSettle();
        }
        catch (System.Exception e)
        {
            Logger.LogWarning($"The hover card's first-frame placement is left to SeneaL UI on this build ({e.GetBaseException().Message}).");
        }
        _harmony.PatchAll(typeof(AutoPickupIgnore.AutoPickupPatch));
        _harmony.PatchAll(typeof(AutoPickupIgnore.CanPickupPatch));
        _harmony.PatchAll(typeof(RichText.SetTextPatch));
        _harmony.PatchAll(typeof(RichText.SetTextOnePatch));
        if (_slotLock != null)
        {
            _harmony.PatchAll(typeof(SlotLock.LeftDownPatch));
        }
        _harmony.PatchAll(typeof(Loadouts.HotbarPatch));
        _harmony.PatchAll(typeof(ChatFilters.PlayerLinePatch));
        _harmony.PatchAll(typeof(ChatFilters.TitledLinePatch));
        _harmony.PatchAll(typeof(ChatFilters.LinePatch));
        _harmony.PatchAll(typeof(ChatExtras.LinePatch));
        _harmony.PatchAll(typeof(ChatExtras.InputPatch));
        _harmony.PatchAll(typeof(ChatExtras.ShoutPinsPatch));
        _harmony.PatchAll(typeof(ModOverrides.SavePatch));
        _harmony.PatchAll(typeof(ModOverrides.HostPatch));
        _harmony.PatchAll(typeof(ModOverrides.LeavePatch));
        StartCoroutine(ModOverrides.ApplyAfterStart(Config, Logger));
        StartCoroutine(_updateNotice.Check());
        if (MultiCraftCompat.Resolve(Config, Logger))
        {
            _harmony.PatchAll(typeof(MultiCraftCompat.SavePatch));
            _harmony.PatchAll(typeof(MultiCraftCompat.RestorePatch));
        }
        if (_radar != null)
        {
            UnityEngine.Canvas.willRenderCanvases += _radar.BeforeRender;
        }
        if (_inputTweaks)
        {
            _harmony.PatchAll(typeof(InputTweaksCompat.GuiUpdatePatch));
            _harmony.PatchAll(typeof(InputTweaksCompat.GridUpdatePatch));
            InputTweaksWorldGuard.Resolve(Config, _harmony, Logger);
        }
        if (_terrainCosts != null)
        {
            UnityEngine.Canvas.willRenderCanvases += _terrainCosts.BeforeRender;
        }
        if (Unlock != null)
        {
            Unlock.Granted += _backup.Unlocked;
            _harmony.PatchAll(typeof(ConnectionPatch));
            _harmony.PatchAll(typeof(PolicyUnlock.AnswerPatch));
        }
        KeyBindManagerGuard.Resolve(Config, _harmony, Logger);
        DadsFppCompat.Resolve(Config, _harmony, Logger);
        DadsSkillsCompat.Resolve(Config, Logger);
        BadgersShadingCompat.Resolve(Config, _harmony, Logger);
        RetiredSettings.Drop(Config);
        try
        {
            DebugLog.Settings(Config);
            DebugLog.TraceAll();
        }
        catch (System.Exception e)
        {
            DebugLog.Write("exception", $"debug tracing: {e}");
        }
        Logger.LogInfo($"{Name} {PluginVersion.Value} loaded");
    }

    private void Update()
    {
        if (_settings == null)
        {
            return;
        }
        Run("debug log", () => DebugLog.Tick(Unlock));
        if (_settings.MainMenuModSettings.Value)
        {
            Run("main menu mod settings", () => _modSettings?.Tick());
        }
        Run("main menu mod settings button", () => _modSettings?.TickButton(_settings.MainMenuModSettings.Value && _settings.MainMenuModSettingsButton.Value));
        Run("server list", _servers.Tick);
        Run("HUD editor settings", _layoutHider.Tick);
        Run("crafting placement", _crafting.Tick);
        Run("unlock", () => Unlock?.Tick());
        Run("beyond maximums", () => _beyond?.Tick());
        Run("SeneaL UI keys", () => _seneaLKeys?.Tick());
        Run("grow corners", () => _growCorners?.Tick());
        Run("SmartCraftStorage chests", () => _smartCraft?.Tick());
        Run("dropped item ping", DroppedItemPing.Tick);
        Run("inventory", _inventory.Tick);
        Run("reminders", () => _reminders.Tick(UnityEngine.Time.time));
        Run("durability tint", _durabilityTint.Tick);
        Run("interface presets per place", () => _interfacePerPlace?.Tick());
        Run("recipe pins", () => _pins?.Tick());
        Run("death pins", () => _deathPins.Tick(UnityEngine.Time.time));
        Run("auto repair", _repair.Tick);
        Run("multi-select key", MultiSelectKey.Tick);
        Run("key hints with inventory", KeyHintsWithInventory.Tick);
        Run("terrain tools room", () => _terrainRoom?.Tick());
        Run("slot lock", () => _slotLock?.Tick());
        Run("split wheel", () =>
        {
            if (!_inputTweaks || !InputTweaksCompat.HandlesSplitWheel())
            {
                _splitWheel.Tick();
            }
        });
        Run("loadouts", _loadouts.Tick);
        Run("auto shield", AutoShield.Tick);
        Run("combat HUD", _combatHud.Tick);
        Run("depleted ore pins", () => _orePins.Tick(UnityEngine.Time.time));
        Run("update notice", () => _updateNotice.Tick(UnityEngine.Time.time));
        Run("radar alerts", () => _radar?.Tick(UnityEngine.Time.time));
        Run("party", () => _party.Tick(UnityEngine.Time.time));
        Run("HUD elements", SeneaLHud.Tick);
        Run("element looks", ElementLook.Tick);
        Run("party HUD", _partyHud.Tick);
        Run("party map", _partyMap.Tick);
        Run("party sharing", _partyShare.Tick);
        Run("preview watermark", () => _watermark.Tick(UnityEngine.Time.realtimeSinceStartup));
        Run("HUD editor additions", () => _hudEditor?.Tick());
        Run("interface preset", () => _preset?.Tick());
        Run("settings page", () => _settingsPage?.Tick());
        Run("auto pins", () => _autoPins.Tick(UnityEngine.Time.time));
        Run("pin kinds", PinKinds.Tick);
        Run("pin chips", () => _pinChips?.Tick());
        Run("cosmetics window", () => _cosmetics?.Tick());
        Run("item comparison", () => _compare?.Tick());
        Run("tracker", () => _tracker?.Tick());
        Run("fuel helper", () => _fuel.Tick(UnityEngine.Time.time));
        Run("area preview", AreaPreview.Tick);
        Run("world previews", _previews.Tick);
        Run("auto doors", () => _doors.Tick(UnityEngine.Time.time));
        Run("production alerts", () => _alerts.Tick(UnityEngine.Time.time));
        Run("find in chests", () => _find.Tick(UnityEngine.Time.time));
        Run("pin looked-at", _pinLookedAt.Tick);
        Run("sign pins", SignPins.Tick);
        Run("dropped item markers", DroppedItemMarkers.Tick);
        Run("rename linked portals", RenameLinkedPortals.Tick);
        Run("undo remove", UndoRemove.Tick);
        Run("recipe lookup", _recipes.Tick);
        Run("bed pins and hotbar swap", () => _bedAndHotbar.Tick(UnityEngine.Time.time));
        Run("status cards", () => _statusCards.Tick(UnityEngine.Time.time));
        Run("skill progress", () => SkillProgress.Tick(UnityEngine.Time.time));
        Run("shelter and comfort", () => ShelterComfort.Tick(UnityEngine.Time.time));
        Run("survival cheats", MoreCheats.Tick);
        Run("chest names", ChestNames.Tick);
        Run("chat bubbles", ChatBubbles.Tick);
        Run("resolution scale", ResolutionScale.Tick);
        Run("layout area", LayoutArea.Tick);
        Run("vehicle names", VehicleNames.Tick);
        Run("cycle chests", CycleChests.Tick);
        Run("build camera", BuildCamera.Tick);
        Run("tame command", TameInfo.Tick);
        Run("repair nearby", RepairNearby.Tick);
        Run("chat notices", () => ChatFilters.Tick(UnityEngine.Time.time));
        Run("death report", DeathReport.Tick);
        Run("portal rules", () => PortalRules.Tick(UnityEngine.Time.time));
        Run("portal travel", PortalTravel.Tick);
        Run("stash button", () => _stash?.Tick());
        Run("tames on map", () => _tames.Tick(UnityEngine.Time.time));
        Run("progress", Progress.Tick);
        Run("cards", ThemedCard.TickAll);
        Run("food window", () => _food?.Tick());
        Run("map cheats", _mapCheats.Tick);
        Run("map search", () => _mapSearch?.Tick());
        Run("map ranges", () => _mapRanges?.Tick());
        Run("pin extras", () => _pinExtras?.Tick());
        Run("chat", _chat.Tick);
        Run("chat extras", _chatExtras.Tick);
        Run("live pins", () => _livePins.Tick(UnityEngine.Time.time));
    }

    private void LateUpdate()
    {
        if (_settings == null)
        {
            return;
        }
        Run("interface presets", _presets.LateTick);
        Run("HUD editor locks", () => _hudEditor?.LateTick());
    }

    // Each feature runs on its own, so an error in one never stops the others for the rest of the frame; it is logged once rather than every frame.
    private void Run(string feature, System.Action tick)
    {
        var start = DebugLog.On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        try
        {
            tick();
        }
        catch (System.Exception e)
        {
            if (_failed.Add(feature))
            {
                Logger.LogWarning($"{feature} failed: {e}");
            }
        }
        if (start != 0L)
        {
            DebugLog.Timed(feature, System.Diagnostics.Stopwatch.GetTimestamp() - start);
        }
    }

    private IEditorBridge CreateHudEditor()
    {
        return (IEditorBridge)WindowAdditions.Create("EditorAdditions", Config, Logger);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void PatchHoverSettle()
    {
        HoverSettle.Patch(_harmony);
    }

    private IBuiltInPreset CreatePreset()
    {
        return (IBuiltInPreset)WindowAdditions.Create("BuiltInPreset", Config, Logger);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private ISettingsPage CreateSettingsPage()
    {
        return SettingsPage.Create(Config, Logger);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static IPinChips CreatePinChips()
    {
        return PinChips.Create();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private ICosmeticsPanel CreateCosmetics()
    {
        return CosmeticsPanel.Create(Config);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static ICompareCards CreateCompare()
    {
        return CompareCards.Create();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static ITrackerHud CreateTracker()
    {
        return TrackerHud.Create();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private IStashButton CreateStash()
    {
        return StashButton.Create(Config);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private IFoodPanel CreateFood()
    {
        return FoodPanel.Create(Config);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private IGrowCorners CreateGrowCorners()
    {
        return GrowCorners.Create(Config, Logger, Unlock);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static IMapSearch CreateMapSearch(BepInEx.Configuration.ConfigFile cfg)
    {
        return MapSearch.Create(cfg);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private IMapPinExtras CreatePinExtras()
    {
        return MapPinExtras.Create(Config);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private IMapRanges CreateMapRanges()
    {
        return MapRanges.Create(Config);
    }

    private void OnDestroy()
    {
        ModOverrides.Restore();
        if (_radar != null)
        {
            UnityEngine.Canvas.willRenderCanvases -= _radar.BeforeRender;
        }
        if (_terrainCosts != null)
        {
            UnityEngine.Canvas.willRenderCanvases -= _terrainCosts.BeforeRender;
        }
        _beyond?.Shutdown();
        _seneaLKeys?.Shutdown();
        _growCorners?.Shutdown();
        _smartCraft?.Shutdown();
        _harmony?.UnpatchSelf();
    }
}
