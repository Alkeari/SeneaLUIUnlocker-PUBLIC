using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Build Camera Key, while a hammer, hoe or cultivator is in hand, lets the camera leave the character and fly around the build with the movement keys (jump and crouch for up and down), while placing, removing and repairing work from where the camera looks. The character stands still meanwhile, and the camera stays within Build Camera Range of it, so pieces still go up only near you: the game's own station range, materials and wards still decide what may be built. Being hurt, putting the tool away or the key again brings the camera back.
internal static class BuildCamera
{
    private const string Section = "Building";

    private static ConfigEntry<bool> _on;
    private static ConfigEntry<KeyboardShortcut> _key;
    private static ConfigEntry<float> _range;
    private static ConfigEntry<float> _speed;

    private static bool _active;
    private static float _yaw;
    private static float _pitch;
    private static float _health;
    private static float _savedReach = -1f;

    internal static bool Active => _active;

    public static void Bind(ConfigFile cfg)
    {
        _on = cfg.Bind(Section, "Build Camera", true,
            "Build Camera Key lets the camera fly free around your build while a building tool is in hand; placing works from where it looks.");
        _key = cfg.Bind(Section, "Build Camera Key", new KeyboardShortcut(KeyCode.O),
            "Starts and stops the build camera while Build Camera is on and a hammer, hoe or cultivator is in hand.");
        _range = cfg.Bind(Section, "Build Camera Range", 30f, new ConfigDescription(
            "How far from your character, in meters, the build camera may fly, and so how far away you can place pieces with it.", new AcceptableValueRange<float>(8f, 60f)));
        _speed = cfg.Bind(Section, "Build Camera Speed", 8f, new ConfigDescription(
            "How fast the build camera flies, meters per second; hold Run for three times as fast.", new AcceptableValueRange<float>(1f, 30f)));
    }

    public static void Tick()
    {
        var player = Player.m_localPlayer;
        if (_active && (player == null || player.IsDead() || !player.InPlaceMode() || player.GetHealth() < _health - 0.01f || !_on.Value || !ServerRules.Allows("BuildCamera")))
        {
            Stop(player);
            return;
        }
        if (player == null || !_on.Value || InventoryFeatures.Typing() || !KeyInput.Down(_key.Value) || !ServerRules.Allows("BuildCamera"))
        {
            return;
        }
        if (_active)
        {
            Stop(player);
        }
        else if (player.InPlaceMode() && GameCamera.instance != null)
        {
            Start(player);
        }
    }

    private static void Start(Player player)
    {
        var camera = GameCamera.instance.transform;
        var angles = camera.rotation.eulerAngles;
        _yaw = angles.y;
        _pitch = angles.x > 180f ? angles.x - 360f : angles.x;
        _health = player.GetHealth();
        _savedReach = player.m_maxPlaceDistance;
        player.m_maxPlaceDistance = Mathf.Max(_savedReach, _range.Value + 10f);
        _active = true;
        player.Message(MessageHud.MessageType.TopLeft, Lang.T("BuildCamera.BuildCameraOn", "Build camera on"));
        Log("on");
    }

    private static void Stop(Player player)
    {
        _active = false;
        if (player != null && _savedReach >= 0f)
        {
            player.m_maxPlaceDistance = _savedReach;
        }
        _savedReach = -1f;
        if (player != null)
        {
            player.Message(MessageHud.MessageType.TopLeft, Lang.T("BuildCamera.BuildCameraOff", "Build camera off"));
        }
        Log("off");
    }

    private static void Log(string what)
    {
        if (DebugLog.On)
        {
            DebugLog.Write("build camera", what);
        }
    }

    // The camera's own update while the build camera flies: look with the mouse, fly with the movement keys, kept within range of the character.
    [HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
    internal static class CameraPatch
    {
        private static bool Prefix(GameCamera __instance, float dt)
        {
            var player = Player.m_localPlayer;
            if (!_active || player == null)
            {
                return true;
            }
            var looking = !Hud.IsPieceSelectionVisible() && !InventoryGui.IsVisible() && !Menu.IsVisible() && !Minimap.IsOpen() && !Console.IsVisible() && (Chat.instance == null || !Chat.instance.HasFocus());
            var transform = __instance.transform;
            if (looking)
            {
                var mouse = ZInput.GetMouseDelta() * PlayerController.m_mouseSens;
                _yaw += mouse.x;
                _pitch = Mathf.Clamp(_pitch - mouse.y, -89f, 89f);
                var move = Vector3.zero;
                if (ZInput.GetButton("Forward")) move += Vector3.forward;
                if (ZInput.GetButton("Backward")) move -= Vector3.forward;
                if (ZInput.GetButton("Right")) move += Vector3.right;
                if (ZInput.GetButton("Left")) move -= Vector3.right;
                var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
                var velocity = rotation * move;
                if (ZInput.GetButton("Jump")) velocity += Vector3.up;
                if (ZInput.GetButton("Crouch")) velocity -= Vector3.up;
                var speed = _speed.Value * (ZInput.GetButton("Run") ? 3f : 1f);
                var position = transform.position + Vector3.ClampMagnitude(velocity, 1f) * speed * dt;
                var center = player.m_eye.position;
                if (Vector3.Distance(position, center) > _range.Value)
                {
                    position = center + (position - center).normalized * _range.Value;
                }
                transform.position = position;
                transform.rotation = rotation;
            }
            __instance.m_camera.fieldOfView = __instance.m_fov;
            __instance.m_skyCamera.fieldOfView = __instance.m_fov;
            return false;
        }
    }

    // The character stands still while the camera flies; placing and removing still go through.
    [HarmonyPatch(typeof(Player), nameof(Player.SetControls))]
    internal static class ControlsPatch
    {
        private static void Prefix(Player __instance, ref Vector3 movedir, ref bool jump, ref bool crouch, ref bool run, ref bool autoRun, ref bool dodge)
        {
            if (!_active || __instance != Player.m_localPlayer)
            {
                return;
            }
            movedir = Vector3.zero;
            jump = false;
            crouch = false;
            run = false;
            autoRun = false;
            dodge = false;
        }
    }
}
