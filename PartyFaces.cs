using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace SeneaLUIUnlocker;

// A picture of each party member's face, taken in game without their helmet whenever they are near enough to be drawn, for the Party HUD in place of their platform picture. A small camera renders only the characters' layer at the member's head for one frame; the picture is kept on disk by the member's platform id so it is there in later sessions before they come near again.
internal static class PartyFaces
{
    private const int Resolution = 128;
    private static BepInEx.Configuration.ConfigEntry<float> _reach;
    private static BepInEx.Configuration.ConfigEntry<float> _retakeMinutes;

    private static readonly Dictionary<string, Texture2D> Pictures = [];
    private static readonly Dictionary<string, float> Taken = [];
    private static readonly HashSet<string> Loaded = [];
    private static Camera _camera;
    private static RenderTexture _target;

    // Unity's PNG encoding lives in a module built against a newer .NET Standard than this plugin compiles with, so it is reached by name.
    private static readonly System.Type Conversion = System.Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
    private static readonly System.Reflection.MethodInfo Encode = Conversion == null ? null : HarmonyLib.AccessTools.Method(Conversion, "EncodeToPNG", [typeof(Texture2D)]);
    private static readonly System.Reflection.MethodInfo Decode = Conversion == null ? null : HarmonyLib.AccessTools.Method(Conversion, "LoadImage", [typeof(Texture2D), typeof(byte[])]);

    private static string Folder => Path.Combine(Paths.ConfigPath, "SeneaL UI Unlocker Faces");

    public static void Bind(BepInEx.Configuration.ConfigFile cfg)
    {
        _reach = cfg.Bind("Party HUD", "Face Picture Distance", 30f, new BepInEx.Configuration.ConfigDescription(
            "How near, in meters, a member must be for their face picture to be taken.", new BepInEx.Configuration.AcceptableValueRange<float>(5f, 60f)));
        _retakeMinutes = cfg.Bind("Party HUD", "Face Picture Retake Minutes", 10f, new BepInEx.Configuration.ConfigDescription(
            "Minutes before a member's face picture is taken again, so a new look or haircut shows.", new BepInEx.Configuration.AcceptableValueRange<float>(1f, 120f)));
    }

    public static Texture2D Of(string key, Player player)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }
        if (Loaded.Add(key))
        {
            Load(key);
        }
        var due = !Taken.TryGetValue(key, out var at) || Time.time - at > _retakeMinutes.Value * 60f;
        if (due && player != null && Player.m_localPlayer != null && Vector3.Distance(player.transform.position, Player.m_localPlayer.transform.position) <= _reach.Value)
        {
            Taken[key] = Time.time;
            Take(key, player);
        }
        return Pictures.TryGetValue(key, out var picture) ? picture : null;
    }

    private static string FileOf(string key)
    {
        foreach (var bad in Path.GetInvalidFileNameChars())
        {
            key = key.Replace(bad, '_');
        }
        return Path.Combine(Folder, key + ".png");
    }

    private static void Load(string key)
    {
        try
        {
            var file = FileOf(key);
            if (Decode == null || !File.Exists(file))
            {
                return;
            }
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (Decode.Invoke(null, [texture, File.ReadAllBytes(file)]) is true)
            {
                Pictures[key] = texture;
            }
        }
        catch (IOException)
        {
        }
    }

    private static void Take(string key, Player player)
    {
        var head = player.m_head;
        if (head == null)
        {
            return;
        }
        if (_camera == null)
        {
            var go = new GameObject("SeneaLUIUnlocker_FaceCamera");
            Object.DontDestroyOnLoad(go);
            _camera = go.AddComponent<Camera>();
            _camera.enabled = false;
            _camera.fieldOfView = 26f;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 4f;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.11f, 0.09f, 0.07f, 1f);
            _target = new RenderTexture(Resolution, Resolution, 24, RenderTextureFormat.ARGB32);
            _camera.targetTexture = _target;
        }
        var face = head.position + Vector3.up * 0.06f;
        var forward = player.transform.forward;
        _camera.transform.position = face + forward * 0.62f;
        _camera.transform.LookAt(face);
        _camera.cullingMask = 1 << player.gameObject.layer;
        // The helmet stays out of the picture: its renderers are off for this one render.
        var hidden = new List<Renderer>();
        var vis = player.GetComponent<VisEquipment>();
        if (vis != null && vis.m_helmetItemInstance != null)
        {
            foreach (var renderer in vis.m_helmetItemInstance.GetComponentsInChildren<Renderer>())
            {
                if (renderer.enabled)
                {
                    renderer.enabled = false;
                    hidden.Add(renderer);
                }
            }
        }
        var previous = RenderTexture.active;
        try
        {
            _camera.Render();
            RenderTexture.active = _target;
            if (!Pictures.TryGetValue(key, out var picture))
            {
                picture = new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, false);
                Pictures[key] = picture;
            }
            picture.ReadPixels(new Rect(0, 0, Resolution, Resolution), 0, 0);
            picture.Apply();
            if (Encode != null && Encode.Invoke(null, [picture]) is byte[] png)
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllBytes(FileOf(key), png);
            }
        }
        catch (IOException)
        {
        }
        finally
        {
            RenderTexture.active = previous;
            foreach (var renderer in hidden)
            {
                renderer.enabled = true;
            }
        }
    }
}
