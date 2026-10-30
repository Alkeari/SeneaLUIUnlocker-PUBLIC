using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Splatform;
using UnityEngine;

namespace SeneaLUIUnlocker;

// SeneaL UI drops what no longer fits when it shrinks the inventory or its extra slots (Slots.Resize, Slots.SpillTail, Pouch, CustomStore): every such drop goes through ItemDrop.DropItem.
// The drops of one frame are pinged once, for this player only, and listed in one message.
internal static class DroppedItemPing
{
    private static readonly HashSet<string> SpillTypes = ["SeneaLUI.Inv.Slots", "SeneaLUI.Inv.Pouch", "SeneaLUI.Inv.CustomStore"];

    private static ConfigEntry<bool> _enabled;
    private static readonly List<string> Dropped = [];
    private static Vector3 _at;

    public static void Bind(ConfigEntry<bool> enabled) => _enabled = enabled;

    public static void Tick()
    {
        if (Dropped.Count == 0)
        {
            return;
        }
        var player = Player.m_localPlayer;
        if (player != null && Chat.instance != null)
        {
            Chat.instance.OnNewChatMessage(null, ZNet.GetUID(), _at, Talker.Type.Ping, UserInfo.GetLocalUser(), "");
            player.Message(MessageHud.MessageType.TopLeft, Lang.F("SafetyFeatures.Dropped", "SeneaL UI dropped what no longer fits: {0}", string.Join(", ", Dropped)));
        }
        Dropped.Clear();
    }

    [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.DropItem), typeof(ItemDrop.ItemData), typeof(int), typeof(Vector3), typeof(Quaternion))]
    internal static class DropPatch
    {
        private static void Postfix(ItemDrop.ItemData item, int amount, Vector3 position, ItemDrop __result)
        {
            if (_enabled == null || !_enabled.Value || __result == null || !FromSeneaLSpill())
            {
                return;
            }
            _at = position;
            Dropped.Add(amount > 1 ? $"{Localization.instance.Localize(item.m_shared.m_name)} x{amount}" : Localization.instance.Localize(item.m_shared.m_name));
        }

        private static bool FromSeneaLSpill() =>
            new StackTrace(2, false).GetFrames()?.Any(frame => frame.GetMethod()?.DeclaringType is { } type && SpillTypes.Contains(type.FullName)) == true;
    }
}

// Copies the character file aside the first time the unlock applies on each server, before anything SeneaL UI keeps in its extra slots is saved there.
internal sealed class CharacterBackup
{
    private readonly ConfigEntry<bool> _enabled;
    private readonly ConfigEntry<string> _backedUp;
    private readonly ManualLogSource _log;

    public CharacterBackup(ConfigFile cfg, ConfigEntry<bool> enabled, ManualLogSource log)
    {
        _enabled = enabled;
        _log = log;
        _backedUp = cfg.Bind("Safety", "Backed Up Servers", "", new ConfigDescription(
            "Character and server pairs already backed up, kept by the mod.", null, new ConfigurationManagerAttributes { Browsable = false }));
    }

    public static string Folder => Path.Combine(Paths.ConfigPath, "SeneaL UI Unlocker Backups");

    public void Unlocked(string serverKey)
    {
        var profile = Game.instance == null ? null : Game.instance.GetPlayerProfile();
        if (!_enabled.Value || profile == null || string.IsNullOrEmpty(profile.m_filename))
        {
            return;
        }
        var pair = $"{profile.m_filename}@{serverKey}";
        var done = _backedUp.Value.Split(';').Select(part => part.Trim()).ToList();
        if (done.Contains(pair))
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Folder);
            var source = SaveSystem.GetCharacterPath(profile.m_fileSource, profile.m_filename);
            var server = new string(serverKey.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            var target = Path.Combine(Folder, $"{profile.m_filename} before {server} {DateTime.Now:yyyy-MM-dd HHmmss}.fch");
            if (!FileHelpers.Copy(source, profile.m_fileSource, target, CloudStorageFileGrouping.SameFileEnding, FileHelpers.FileSource.Local))
            {
                _log.LogWarning($"Could not back up the character {profile.m_filename} before its first unlocked join on {serverKey}.");
                return;
            }
            _backedUp.Value = string.Join("; ", done.Where(part => part.Length > 0).Append(pair));
            _log.LogInfo($"Backed up the character {profile.m_filename} to {target}.");
        }
        catch (Exception e)
        {
            _log.LogWarning($"Could not back up the character {profile.m_filename} ({e.GetBaseException().Message}).");
        }
    }
}
