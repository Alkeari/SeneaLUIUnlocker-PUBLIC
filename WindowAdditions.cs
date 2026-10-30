using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace SeneaLUIUnlocker;

// SeneaLUIUnlocker.WindowAdditions.dll holds the parts drawn inside SeneaL UI's own windows with types only SeneaL UI 1.2.0 and later have (the HUD editor additions, the built-in preset, element looks, SeneaL UI's dropdown). It is loaded only when the running SeneaL UI build has every one of those types, so on an older build no loaded assembly holds a type that cannot load, and a mod that lists every loaded type through Harmony logs no warning for this plugin.
internal static class WindowAdditions
{
    public const string FileName = "SeneaLUIUnlocker.WindowAdditions.dll";

    private static readonly string[] Needs = ["SeneaLUI.Hud.EditTarget", "SeneaLUI.Hud.EditOption", "SeneaLUI.Hud.ElementSelect", "SeneaLUI.Hud.BackdropSync", "SeneaLUI.Hud.HudEditor", "SeneaLUI.Hud.ProfileMenu"];

    private static bool _tried;
    private static Assembly _assembly;

    public static string Missing { get; private set; }

    public static Assembly Assembly
    {
        get
        {
            if (!_tried)
            {
                _tried = true;
                _assembly = Load();
            }
            return _assembly;
        }
    }

    public static bool Available => Assembly != null;

    private static Assembly Load()
    {
        foreach (var name in Needs)
        {
            if (TypeLookup.Find(name) == null)
            {
                Missing = $"this SeneaL UI build lacks {name}";
                return null;
            }
        }
        var path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", FileName);
        if (!File.Exists(path))
        {
            Missing = $"{FileName} is not beside {Path.GetFileName(typeof(Plugin).Assembly.Location)}";
            return null;
        }
        // The additions reference this plugin's assembly by name; answering with the one already loaded keeps a resolver that loads by path from making a second copy with its own static state.
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => new AssemblyName(e.Name).Name == typeof(Plugin).Assembly.GetName().Name ? typeof(Plugin).Assembly : null;
        try
        {
            return Assembly.LoadFrom(path);
        }
        catch (Exception e)
        {
            Missing = e.GetBaseException().Message;
            return null;
        }
    }

    // Calls the class's static Create when it has one, otherwise its constructor.
    public static object Create(string className, params object[] args)
    {
        var assembly = Assembly ?? throw new InvalidOperationException(Missing);
        var type = assembly.GetType(typeof(Plugin).Namespace + "." + className, true);
        var factory = type.GetMethod("Create", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return factory != null
            ? factory.Invoke(null, args)
            : Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null);
    }
}

internal interface IEditorBridge
{
    void Tick();

    void LateTick();
}

internal interface IElementLooks
{
    float TextScale(string id);

    void Tick(ICollection<string> selfScaled);

    void SetGrow(string id, int grow);

    void SetLooks(IReadOnlyDictionary<string, string> looks);

    void Tip(RectTransform rect, string title, string body);
}

internal interface IBuiltInPreset
{
    void Tick();
}
