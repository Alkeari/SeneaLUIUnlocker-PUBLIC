using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// The loaded portals, ships, carts, containers, fires, graves, pickables and production stations, collected as the game creates them. Searching the whole scene for one type cost about 31 ms each time with a full mod set (2026-10-13), a hitch every few seconds per feature that looked; these lists cost nothing to read.
internal static class SceneObjects
{
    public static readonly List<TeleportWorld> Portals = [];
    public static readonly List<Ship> Ships = [];
    public static readonly List<Container> Containers = [];
    public static readonly List<Fireplace> Fires = [];
    public static readonly List<TombStone> Graves = [];
    public static readonly List<Pickable> Pickables = [];
    public static readonly List<Smelter> Smelters = [];
    public static readonly List<Fermenter> Fermenters = [];
    public static readonly List<CookingStation> CookingStations = [];

    public static IReadOnlyList<Vagon> Carts => Vagon.m_instances;

    // Destroyed objects compare equal to null in Unity; they are dropped as the list is read.
    public static List<T> Live<T>(List<T> list) where T : Object
    {
        list.RemoveAll(item => item == null);
        return list;
    }

    [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Awake))]
    internal static class PortalPatch
    {
        private static void Postfix(TeleportWorld __instance) => Portals.Add(__instance);
    }

    [HarmonyPatch(typeof(Ship), nameof(Ship.Awake))]
    internal static class ShipPatch
    {
        private static void Postfix(Ship __instance) => Ships.Add(__instance);
    }

    [HarmonyPatch(typeof(Container), nameof(Container.Awake))]
    internal static class ContainerPatch
    {
        private static void Postfix(Container __instance) => Containers.Add(__instance);
    }

    [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Start))]
    internal static class FirePatch
    {
        private static void Postfix(Fireplace __instance) => Fires.Add(__instance);
    }

    [HarmonyPatch(typeof(TombStone), nameof(TombStone.Awake))]
    internal static class GravePatch
    {
        private static void Postfix(TombStone __instance) => Graves.Add(__instance);
    }

    [HarmonyPatch(typeof(Smelter), nameof(Smelter.Awake))]
    internal static class SmelterPatch
    {
        private static void Postfix(Smelter __instance) => Smelters.Add(__instance);
    }

    [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.Awake))]
    internal static class FermenterPatch
    {
        private static void Postfix(Fermenter __instance) => Fermenters.Add(__instance);
    }

    [HarmonyPatch(typeof(CookingStation), nameof(CookingStation.Awake))]
    internal static class CookingPatch
    {
        private static void Postfix(CookingStation __instance) => CookingStations.Add(__instance);
    }

    [HarmonyPatch(typeof(Pickable), nameof(Pickable.Awake))]
    internal static class PickablePatch
    {
        private static void Postfix(Pickable __instance) => Pickables.Add(__instance);
    }
}
