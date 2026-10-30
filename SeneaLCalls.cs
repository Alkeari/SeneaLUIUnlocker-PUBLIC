using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// Calls into SeneaL UI's own public and private static methods by name. SeneaL UI's code is called, never patched; a member a SeneaL UI build lacks resolves to null and the feature using it stays off.
internal static class SeneaLCalls
{
    private static readonly Type Slots = TypeLookup.Find("SeneaLUI.Inv.Slots");
    private static readonly Type Stack = TypeLookup.Find("SeneaLUI.Mods.Stack");
    private static readonly Type Stock = TypeLookup.Find("SeneaLUI.Core.Stock");
    private static readonly Type Policy = TypeLookup.Find("SeneaLUI.Net.Policy");
    private static readonly Type SeneaLPlugin = TypeLookup.Find("SeneaLUI.Plugin");

    private static readonly MethodInfo SortPlayerMethod = Method(Slots, nameof(SeneaLUI.Inv.Slots.SortPlayer), typeof(Inventory), typeof(int), typeof(bool));
    private static readonly MethodInfo SortContainerMethod = Method(Slots, nameof(SeneaLUI.Inv.Slots.SortContainer), typeof(Inventory), typeof(int));
    private static readonly MethodInfo LocksOfMethod = Method(Slots, nameof(SeneaLUI.Inv.Slots.LocksOf), typeof(Inventory));
    private static readonly MethodInfo MainRowsMethod = Method(Slots, nameof(SeneaLUI.Inv.Slots.MainRows), typeof(Inventory));
    private static readonly MethodInfo ChangedMethod = Method(Slots, nameof(SeneaLUI.Inv.Slots.Changed), typeof(Inventory));
    private static readonly MethodInfo NearbyMethod = Method(Stack, nameof(SeneaLUI.Mods.Stack.Nearby), typeof(UnityEngine.Vector3));
    private static readonly MethodInfo CraftChestsMethod = Method(Stock, nameof(SeneaLUI.Core.Stock.Nearby), typeof(UnityEngine.Vector3));
    private static readonly MethodInfo NviewMethod = Method(Stock, nameof(SeneaLUI.Core.Stock.Nview), typeof(Container));
    private static readonly PropertyInfo CanQuickStackProperty = Policy == null ? null : AccessTools.Property(Policy, nameof(SeneaLUI.Net.Policy.CanQuickStack));
    private static readonly PropertyInfo SortHotbarProperty = SeneaLPlugin == null ? null : AccessTools.Property(SeneaLPlugin, nameof(SeneaLUI.Plugin.SortHotbar));

    public static bool CanSort => SortPlayerMethod != null && SortContainerMethod != null && LocksOfMethod != null && MainRowsMethod != null && ChangedMethod != null;

    public static bool CanRestock => NearbyMethod != null && NviewMethod != null && CanQuickStackProperty != null;

    public static void SortPlayer(Inventory inv, int mode) => SortPlayerMethod.Invoke(null, [inv, mode, SortHotbar]);

    public static void SortContainer(Inventory inv, int mode) => SortContainerMethod.Invoke(null, [inv, mode]);

    public static HashSet<int> LocksOf(Inventory inv) => (HashSet<int>)LocksOfMethod.Invoke(null, [inv]);

    public static int MainRows(Inventory inv) => (int)MainRowsMethod.Invoke(null, [inv]);

    public static void Changed(Inventory inv) => ChangedMethod.Invoke(null, [inv]);

    public static bool SortHotbar => SortHotbarProperty?.GetValue(null) is BepInEx.Configuration.ConfigEntry<bool> { Value: true };

    // SeneaL UI's own restock chest list: chests in its quick stack range that the player may open and that it has not been told to skip.
    public static List<Container> NearbyChests(UnityEngine.Vector3 pos) => (List<Container>)NearbyMethod.Invoke(null, [pos]);

    // The chests SeneaL UI crafts and builds from, within its chest range ([Chests] ChestRange), skipping chests marked not to craft from; the quick stack search where a build lacks it.
    public static List<Container> CraftChests(UnityEngine.Vector3 pos) => CraftChestsMethod == null ? NearbyChests(pos) : (List<Container>)CraftChestsMethod.Invoke(null, [pos]);

    public static ZNetView Nview(Container chest) => (ZNetView)NviewMethod.Invoke(null, [chest]);

    // SeneaL UI's quick stack reach in meters, as its own chest search uses it (Policy.StackRange).
    public static float StackRange => Policy != null && AccessTools.Property(Policy, "StackRange")?.GetValue(null, null) is float range ? range : 0f;

    // Whatever SeneaL UI allows on this server, including the rules a SeneaL UI server sends.
    public static bool QuickStackAllowed => CanQuickStackProperty != null && (bool)CanQuickStackProperty.GetValue(null, null);

    private static MethodInfo Method(Type type, string name, params Type[] args) => type == null ? null : AccessTools.Method(type, name, args);
}
