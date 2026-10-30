using System.Collections.Generic;
using Splatform;
using UnityEngine;

namespace SeneaLUIUnlocker;

// The platform profile pictures SeneaL UI's Players page shows, fetched the way the game's own player list does (SessionPlayerListEntry.SetValues). The platform only gives pictures of players on the same platform; anyone else gets no picture and the HUD draws their initial.
internal static class PartyAvatars
{
    private const uint Resolution = 128;

    private static readonly Dictionary<string, Texture2D> Pictures = [];
    private static readonly HashSet<string> Asked = [];

    public static Texture2D Of(ZNet.CrossNetworkUserInfo user)
    {
        var key = user.m_id.ToString();
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }
        if (Pictures.TryGetValue(key, out var picture))
        {
            return picture;
        }
        if (Asked.Add(key))
        {
            Ask(user.m_id, key);
        }
        return null;
    }

    private static void Ask(PlatformUserID id, string key)
    {
        var relations = PlatformManager.DistributionPlatform?.RelationsProvider;
        if (relations == null || id.m_platform != PlatformManager.DistributionPlatform.Platform)
        {
            return;
        }
        relations.GetUserProfileAsync(id, profile =>
        {
            profile.ProfilePictureUpdated += () => Store(key, profile);
            profile.RequestProfilePictureAsync(Resolution);
            Store(key, profile);
        }, (_, _) => { });
    }

    private static void Store(string key, IUserProfile profile)
    {
        if (profile.ProfilePicture != null)
        {
            Pictures[key] = profile.ProfilePicture;
        }
    }
}
