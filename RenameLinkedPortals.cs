using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Splatform;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Rename Linked Portals: renaming a portal that is paired gives the portal at the other end the same new tag, so the pair stays linked instead of both falling unpaired. It acts only when the old tag was not empty, the far portal still holds it and no other portal this game knows of shares it, and the far portal is not in a ward you cannot access.
internal static class RenameLinkedPortals
{
    // A far portal outside the area this client holds arrives from the server on request; how long to wait for it.
    private const float Wait = 10f;

    private static ConfigEntry<bool> _on;
    private static readonly List<(ZDOID Near, ZDOID Far, string OldTag, string NewTag, string Author, float Until)> Queue = [];

    public static void Bind(ConfigFile cfg)
    {
        _on = cfg.Bind("Portals", "Rename Linked Portals", true,
            "Renaming a portal that is linked to exactly one other gives that portal the same new name, so the two stay linked. A portal in a ward you cannot access is left unchanged.");
    }

    private static bool On => _on != null && _on.Value && ServerRules.Allows("RenameLinkedPortals");

    public static void Tick()
    {
        if (!On || ZDOMan.instance == null)
        {
            Queue.Clear();
            return;
        }
        if (Queue.Count > 0)
        {
            Process();
        }
    }

    private static void Process()
    {
        for (var i = Queue.Count - 1; i >= 0; i--)
        {
            var job = Queue[i];
            var far = ZDOMan.instance.GetZDO(job.Far);
            if (far == null)
            {
                if (Time.time > job.Until)
                {
                    Queue.RemoveAt(i);
                    Trace($"linked portal {job.Far} never arrived from the server; \"{job.OldTag}\" left unchanged there");
                }
                continue;
            }
            Queue.RemoveAt(i);
            Apply(job.Near, far, job.OldTag, job.NewTag, job.Author);
        }
    }

    private static void Apply(ZDOID near, ZDO far, string oldTag, string newTag, string author)
    {
        if (far.GetString(ZDOVars.s_tag) != oldTag)
        {
            Trace($"linked portal {far.m_uid} no longer holds \"{oldTag}\"; left as it is");
            return;
        }
        foreach (var portal in ZDOMan.instance.GetPortalList())
        {
            if (portal.m_uid != near && portal.m_uid != far.m_uid && portal.GetString(ZDOVars.s_tag) == oldTag)
            {
                Trace($"another portal {portal.m_uid} also holds \"{oldTag}\"; linked portal {far.m_uid} left unchanged");
                return;
            }
        }
        if (!PrivateArea.CheckAccess(far.GetPosition(), 0f, false))
        {
            if (Player.m_localPlayer != null)
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, Lang.F("RenameLinkedPortals.TheLinkedPortalIsIn", "The linked portal \"{0}\" is in a ward you cannot access, so it was left unchanged", oldTag));
            }
            Trace($"linked portal {far.m_uid} at {far.GetPosition()} is warded; left unchanged");
            return;
        }
        var owner = far.GetOwner();
        var me = ZDOMan.GetSessionID();
        // A portal another player has loaded, or one loaded here, takes the rename through the game's own RPC_SetTag, as a rename typed at it would; an unloaded one nobody holds is claimed and written the way TeleportWorld.SetConnectedPortal and Game.SetConnection write a far portal.
        if (owner != 0L && owner != me && IsConnectedPlayer(owner) || owner == me && ZNetScene.instance != null && ZNetScene.instance.FindInstance(far) != null)
        {
            ZRoutedRpc.instance.InvokeRoutedRPC(owner, far.m_uid, "RPC_SetTag", newTag, author);
            Trace($"linked portal {far.m_uid} renamed \"{oldTag}\" to \"{newTag}\" through its owner {owner}");
            return;
        }
        far.SetOwner(me);
        far.UpdateConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None);
        far.Set(ZDOVars.s_tag, newTag);
        far.Set(ZDOVars.s_tagauthor, author);
        Trace($"linked portal {far.m_uid} at {far.GetPosition()} renamed \"{oldTag}\" to \"{newTag}\" on its ZDO (owner was {owner})");
    }

    private static bool IsConnectedPlayer(long uid)
    {
        if (ZNet.instance == null)
        {
            return false;
        }
        foreach (var info in ZNet.instance.GetPlayerList())
        {
            if (info.m_characterID.UserID == uid)
            {
                return true;
            }
        }
        return false;
    }

    private static void Trace(string message)
    {
        if (DebugLog.On)
        {
            DebugLog.Write("portals", message);
        }
    }

    // The rename reaches the portal's owner by RPC and may already be applied when SetText returns, so the old tag and the pairing are read before it.
    [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.SetText))]
    internal static class SetTextPatch
    {
        private static void Prefix(TeleportWorld __instance, out (string Tag, ZDOID Far) __state)
        {
            __state = ("", ZDOID.None);
            if (!On || __instance == null || __instance.m_nview == null || !__instance.m_nview.IsValid())
            {
                return;
            }
            var zdo = __instance.m_nview.GetZDO();
            __state = (zdo.GetString(ZDOVars.s_tag), zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal));
        }

        private static void Postfix(TeleportWorld __instance, string text, (string Tag, ZDOID Far) __state)
        {
            // An emptied name is how a portal is unlinked, and an empty tag pairs with any other unnamed portal, so it is not carried over.
            if (string.IsNullOrEmpty(__state.Tag) || __state.Far.IsNone() || string.IsNullOrEmpty(text) || text == __state.Tag || ZDOMan.instance == null)
            {
                return;
            }
            var near = __instance.m_nview.GetZDO().m_uid;
            var author = PlatformManager.DistributionPlatform.LocalUser.PlatformUserID.ToString();
            Queue.Add((near, __state.Far, __state.Tag, text, author, Time.time + Wait));
            if (ZDOMan.instance.GetZDO(__state.Far) == null)
            {
                ZDOMan.instance.RequestZDO(__state.Far);
                Trace($"renamed portal {near} \"{__state.Tag}\" to \"{text}\"; asked the server for linked portal {__state.Far}");
                return;
            }
            Process();
        }
    }
}
