using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SeneaLUIUnlocker;

// SeneaL UI reads a few keys as fixed KeyCodes rather than settings. On a server without SeneaL UI each becomes a setting here: the ZInput.GetKey or GetKeyDown call that reads it inside SeneaL UI's method is redirected to the setting.
// That patches SeneaL UI's code, which a SeneaL UI server lists in the build proof it asks for (Integrity.Foreign), so as with BeyondMaximums the patches exist only while the unlock stands, under their own Harmony id, and a guard on the game's own ZRpc.HandlePackage removes them before a SeneaL UI server's answer reaches SeneaL UI. They are applied only while a key differs from SeneaL UI's own, so with every key at its default nothing of SeneaL UI's is patched.
internal sealed class SeneaLKeys
{
    private const string Section = "SeneaL UI Keys";
    private const string PatchId = Plugin.Guid + ".keys";
    private const string GuardId = Plugin.Guid + ".keys.guard";

    private static readonly int PolicyRpc = "SeneaLUI_Policy".GetStableHashCode();
    private static readonly MethodInfo GetKey = AccessTools.Method(typeof(ZInput), nameof(ZInput.GetKey), [typeof(KeyCode), typeof(bool)]);
    private static readonly MethodInfo GetKeyDown = AccessTools.Method(typeof(ZInput), nameof(ZInput.GetKeyDown), [typeof(KeyCode), typeof(bool)]);
    private static SeneaLKeys _current;

    private readonly ManualLogSource _log;
    private readonly PolicyUnlock _unlock;
    private readonly Harmony _patches = new(PatchId);
    private readonly Harmony _guard = new(GuardId);
    private readonly List<Binding> _bindings = new();
    private readonly List<Site> _sites = new();

    private bool _eligible;
    private bool _applied;
    private bool _guardApplied;
    private bool _broken;

    private SeneaLKeys(ManualLogSource log, PolicyUnlock unlock)
    {
        _log = log;
        _unlock = unlock;
    }

    public static SeneaLKeys Resolve(ConfigFile cfg, ManualLogSource log, PolicyUnlock unlock)
    {
        if (unlock == null || GetKey == null || GetKeyDown == null)
        {
            return null;
        }
        var keys = new SeneaLKeys(log, unlock);
        var stepKeys = TypeLookup.Find("SeneaLUI.Core.StepKeys");
        var craftView = TypeLookup.Find("SeneaLUI.Inv.CraftView");
        var modsView = TypeLookup.Find("SeneaLUI.Hud.ModsView");
        var splitView = TypeLookup.Find("SeneaLUI.Inv.SplitView");
        var enchantView = TypeLookup.Find("SeneaLUI.Mods.EnchantView");
        var chords = TypeLookup.Find("SeneaLUI.Core.Chords");
        var plugin = TypeLookup.Find("SeneaLUI.Plugin");
        var mult = stepKeys == null ? null : AccessTools.PropertyGetter(stepKeys, "Mult");
        var craft = stepKeys == null ? null : AccessTools.PropertyGetter(stepKeys, "Craft");

        keys.Bind(cfg, "Amount Step 10 Key", KeyCode.LeftShift, "Held on SeneaL UI's amount arrows (store, skills, smelter and other amount rows) to step by 10.",
            (mult, Shift));
        keys.Bind(cfg, "Amount Step 5 Key", KeyCode.LeftControl, "Held on SeneaL UI's amount arrows to step by 5.",
            (mult, Ctrl));
        keys.Bind(cfg, "Craft Step 10 Key", KeyCode.LeftControl, "Held on SeneaL UI's craft amount arrows to step by 10.",
            (craft, Ctrl));
        keys.Bind(cfg, "Craft Step 5 Key", KeyCode.LeftShift, "Held on SeneaL UI's craft amount arrows to step by 5.",
            (craft, Shift));
        keys.Bind(cfg, "Craft Queue Key", KeyCode.LeftShift, "Held while clicking Craft in SeneaL UI's crafting window to add the recipe to the craft queue.",
            (Method(craftView, "OnCraft"), Shift));
        keys.Bind(cfg, "Craft Ten Key", KeyCode.LeftControl, "Held in SeneaL UI's crafting window to craft up to 10 at once.",
            (Method(craftView, "TickDetail"), Ctrl));
        keys.Bind(cfg, "Mod Settings Zoom Key", KeyCode.LeftControl, "Held while turning the mouse wheel in SeneaL UI's mod settings window to make the window larger or smaller.",
            (Method(modsView, "Update"), Ctrl));
        keys.Bind(cfg, "Split Less Key", KeyCode.LeftArrow, "In SeneaL UI's split dialog, takes 1 off the amount.",
            (Method(splitView, "Keys"), new[] { KeyCode.LeftArrow }));
        keys.Bind(cfg, "Split More Key", KeyCode.RightArrow, "In SeneaL UI's split dialog, adds 1 to the amount.",
            (Method(splitView, "Keys"), new[] { KeyCode.RightArrow }));
        keys.Bind(cfg, "Split Confirm Key", KeyCode.Return, "Confirms SeneaL UI's split dialog. Keypad Enter also confirms it.",
            (Method(splitView, "Keys"), new[] { KeyCode.Return }));
        keys.Bind(cfg, "Enchant Key", KeyCode.Space, "Enchants the item in SeneaL UI's enchanting window.",
            (Method(enchantView, "Tick"), new[] { KeyCode.Space }));
        keys.Bind(cfg, "Enchant Confirm Key", KeyCode.Return, "Also enchants the item in SeneaL UI's enchanting window. Keypad Enter also does.",
            (Method(enchantView, "Tick"), new[] { KeyCode.Return }));
        keys.Bind(cfg, "Shortcut Block Key", KeyCode.LeftAlt, "While held, SeneaL UI's shortcuts made of a single key do not fire.",
            (Method(chords, "Claim"), Alt), (Method(plugin, "Pressed"), Alt));

        if (keys._sites.Count == 0)
        {
            log.LogWarning("This SeneaL UI build reads none of the fixed keys the SeneaL UI Keys settings replace, so they do nothing.");
            return null;
        }
        _current = keys;
        unlock.Granted += _ => keys.Granted();
        return keys;
    }

    private static readonly KeyCode[] Shift = [KeyCode.LeftShift, KeyCode.RightShift];
    private static readonly KeyCode[] Ctrl = [KeyCode.LeftControl, KeyCode.RightControl];
    private static readonly KeyCode[] Alt = [KeyCode.LeftAlt, KeyCode.RightAlt];

    private static MethodInfo Method(Type type, string name) => type == null ? null : AccessTools.Method(type, name);

    // A key this SeneaL UI build no longer reads (1.2.0-preview.7's Craft always queues, so it reads no Shift there) gets no setting, since one would change nothing.
    private void Bind(ConfigFile cfg, string key, KeyCode value, string description, params (MethodInfo Method, KeyCode[] Codes)[] reads)
    {
        var sites = reads.Where(read => read.Method != null)
            .SelectMany(read => read.Codes.Where(code => Reads(read.Method, code)).Select(code => (read.Method, Code: code)))
            .ToList();
        if (sites.Count == 0)
        {
            return;
        }
        var index = _bindings.Count;
        var entry = cfg.Bind(Section, key, new KeyboardShortcut(value), description + " Used in your own world and on servers without SeneaL UI; a SeneaL UI server keeps its own keys.");
        _bindings.Add(new Binding(entry, new KeyboardShortcut(value)));
        foreach (var (method, code) in sites)
        {
            _sites.Add(new Site(method, code, index));
        }
    }

    private static bool Reads(MethodInfo method, KeyCode code)
    {
        try
        {
            var codes = PatchProcessor.GetOriginalInstructions(method);
            for (var i = 2; i < codes.Count; i++)
            {
                if ((codes[i].Calls(GetKey) || codes[i].Calls(GetKeyDown)) && Constant(codes[i - 2], out var read) && read == (int)code)
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            return false;
        }
        return false;
    }

    private bool Changed => _bindings.Any(binding => !binding.Entry.Value.Equals(binding.Default));

    private void Granted()
    {
        _eligible = true;
        Tick();
    }

    public void Tick()
    {
        if (_guardApplied && !_applied)
        {
            _guard.UnpatchSelf();
            _guardApplied = false;
        }
        if (_unlock.OwnWorld)
        {
            _eligible = true;
        }
        else if (!_unlock.IsUnlocked)
        {
            _eligible = false;
        }
        var wanted = _eligible && !_broken && Changed && ServerRules.Allows("SeneaLKeys");
        if (wanted && !_applied)
        {
            Apply();
        }
        else if (!wanted && _applied)
        {
            Remove("the unlock ended or every key is back at SeneaL UI's own");
        }
    }

    public void Shutdown()
    {
        _patches.UnpatchSelf();
        _guard.UnpatchSelf();
        _applied = false;
        _guardApplied = false;
    }

    private void Apply()
    {
        try
        {
            if (!_guardApplied)
            {
                _guard.Patch(AccessTools.Method(typeof(ZRpc), nameof(ZRpc.HandlePackage)), prefix: Guard(nameof(BeforePackage)));
                _guard.Patch(AccessTools.Method(typeof(ZNet), nameof(ZNet.StopAll)), prefix: Guard(nameof(BeforeConnectionChange)));
                _guard.Patch(AccessTools.Method(typeof(ZNet), nameof(ZNet.OnNewConnection)), prefix: Guard(nameof(BeforeConnectionChange)));
                _guardApplied = true;
            }
            var transpiler = new HarmonyMethod(AccessTools.Method(typeof(SeneaLKeys), nameof(Redirect)));
            foreach (var method in _sites.Select(site => site.Method).Distinct())
            {
                _patches.Patch(method, transpiler: transpiler);
            }
            _applied = true;
            _log.LogInfo("SeneaL UI's fixed keys follow the SeneaL UI Keys settings on this server.");
        }
        catch (Exception e)
        {
            _broken = true;
            _patches.UnpatchSelf();
            _applied = false;
            _log.LogWarning($"Could not move SeneaL UI's fixed keys ({e.GetBaseException().Message}); the SeneaL UI Keys settings stay off until the game restarts.");
        }
    }

    private static HarmonyMethod Guard(string name) =>
        new(AccessTools.Method(typeof(SeneaLKeys), name)) { priority = Priority.First };

    // Leaves the guard patched until the next Tick: it sits on the game's own methods, which a SeneaL UI server never sees, and unpatching a method from inside its own prefix is avoided.
    private void Remove(string why)
    {
        _patches.UnpatchSelf();
        _applied = false;
        _log.LogInfo($"SeneaL UI's fixed keys are back ({why}).");
    }

    private static void BeforePackage(ZPackage package)
    {
        var self = _current;
        if (self == null || !self._applied)
        {
            return;
        }
        var pos = package.GetPos();
        if (package.Size() - pos < sizeof(int))
        {
            return;
        }
        var hash = package.ReadInt();
        package.SetPos(pos);
        if (hash == PolicyRpc)
        {
            self._eligible = false;
            self.Remove("the server runs SeneaL UI");
        }
    }

    private static void BeforeConnectionChange()
    {
        var self = _current;
        if (self == null)
        {
            return;
        }
        self._eligible = false;
        if (self._applied)
        {
            self.Remove("the connection changed");
        }
    }

    // Each read is `ldc KeyCode; ldc bool; call ZInput.GetKey(Down)`; a read of a key this method has a setting for gets the setting's index pushed and calls Held or Down below in place of ZInput.
    private static IEnumerable<CodeInstruction> Redirect(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var codes = instructions.ToList();
        var self = _current;
        for (var i = 2; i < codes.Count; i++)
        {
            var down = codes[i].Calls(GetKeyDown);
            if (!down && !codes[i].Calls(GetKey) || !Constant(codes[i - 2], out var code))
            {
                continue;
            }
            var site = self._sites.FirstOrDefault(s => s.Method == __originalMethod && (int)s.Code == code);
            if (site == null)
            {
                continue;
            }
            var call = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SeneaLKeys), down ? nameof(Down) : nameof(Held)));
            call.labels.AddRange(codes[i].labels);
            call.blocks.AddRange(codes[i].blocks);
            codes[i] = call;
            codes.Insert(i, new CodeInstruction(OpCodes.Ldc_I4, site.Index));
            i++;
        }
        return codes;
    }

    private static bool Constant(CodeInstruction instruction, out int value)
    {
        value = 0;
        if (instruction.opcode == OpCodes.Ldc_I4 || instruction.opcode == OpCodes.Ldc_I4_S)
        {
            value = Convert.ToInt32(instruction.operand);
            return true;
        }
        return false;
    }

    private static bool Held(KeyCode code, bool ignoreFocus, int index)
    {
        var self = _current;
        return self != null && self._applied ? KeyInput.Held(self._bindings[index].Entry.Value) : ZInput.GetKey(code, ignoreFocus);
    }

    private static bool Down(KeyCode code, bool ignoreFocus, int index)
    {
        var self = _current;
        return self != null && self._applied ? KeyInput.Down(self._bindings[index].Entry.Value) : ZInput.GetKeyDown(code, ignoreFocus);
    }

    private sealed record Binding(ConfigEntry<KeyboardShortcut> Entry, KeyboardShortcut Default);

    private sealed record Site(MethodInfo Method, KeyCode Code, int Index);
}
