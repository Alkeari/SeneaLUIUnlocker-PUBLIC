using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace SeneaLUIUnlocker;

// The servers this client already knows: the game's own Favorite and Recent lists, and QuickConnect's list when that mod is installed.
internal sealed class ServerDirectory
{
    private const string Pick = "(pick a server)";
    private const string Other = "Other";
    private const string QuickConnectFile = "quick_connect_servers.cfg";

    private static ServerDirectory _instance;

    private readonly ConfigFile _cfg;
    private readonly Settings _settings;
    private readonly ManualLogSource _log;
    private ConfigEntry<string> _addKnown;

    public ServerDirectory(ConfigFile cfg, Settings settings, ManualLogSource log)
    {
        _cfg = cfg;
        _settings = settings;
        _log = log;
        _instance = this;
        _settings.AddCurrentServer.SettingChanged += (_, _) => AddCurrent();
    }

    public static string CurrentKey { get; private set; } = "";
    public static string CurrentLabel { get; private set; } = "";

    // A listed server follows its own Unlock switch, which starts from what Unlock On says for listed servers; any other server follows Unlock On.
    public bool Allows(string key)
    {
        var listed = Listed().FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));
        if (listed.Key != null)
        {
            return Flag(listed.Options, UnlockOption, _settings.Filter.Value != ServerFilter.All_Except_Listed_Servers);
        }
        return _settings.Filter.Value != ServerFilter.Only_Listed_Servers;
    }

    public const string UnlockOption = "unlock";
    public const string PopupOption = "popup";
    public const string BeyondOption = "beyond";

    // A per-server switch of the server being joined, or the general setting when it is not listed or has no such switch.
    public static bool Option(string name, bool fallback)
    {
        var self = _instance;
        if (self == null || CurrentKey.Length == 0)
        {
            return fallback;
        }
        var listed = self.Listed().FirstOrDefault(entry => string.Equals(entry.Key, CurrentKey, StringComparison.OrdinalIgnoreCase));
        return listed.Key == null ? fallback : Flag(listed.Options, name, fallback);
    }

    private static bool Flag(Dictionary<string, string> options, string name, bool fallback) =>
        options.TryGetValue(name, out var text) ? text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase) : fallback;

    public static List<ListedServer> Entries() => _instance == null ? [] : _instance.Listed().Select(entry => new ListedServer(entry.Label, entry.Key, entry.Options)).ToList();

    public static void Save(IEnumerable<ListedServer> servers)
    {
        if (_instance == null)
        {
            return;
        }
        _instance._settings.ListedServers.Value = string.Join("; ", servers.Where(server => server.Key.Length > 0).Select(server =>
        {
            var head = server.Label.Length > 0 && server.Label != server.Key ? $"{Clean(server.Label)}={server.Key}" : server.Key;
            return head + string.Concat(server.Options.Select(option => $"|{option.Key}={option.Value}"));
        }));
    }

    public static List<(string Label, string Key)> KnownServers() => _instance == null ? [] : _instance.Known();

    public static void AddCurrentServer()
    {
        if (_instance != null && CurrentKey.Length > 0 && ZNet.instance != null && !ZNet.instance.IsServer())
        {
            _instance.Add(CurrentLabel, CurrentKey);
        }
    }

    public static void AddServer(string label, string key) => _instance?.Add(label, key);

    internal sealed class ListedServer(string label, string key, Dictionary<string, string> options)
    {
        public string Label = label;
        public string Key = key;
        public readonly Dictionary<string, string> Options = options;
    }

    // The game reads its server lists only once its platform layer is up, which the main menu guarantees.
    public void Tick()
    {
        if (_addKnown != null || FejdStartup.instance == null)
        {
            return;
        }
        var known = Known();
        var choices = new[] { Pick }.Concat(known.Select(Choice)).Distinct().Append(Other).ToArray();
        _addKnown = _cfg.Bind("Servers", "Add Known Server", Pick, new ConfigDescription(
            "Pick a server from your Favorite and Recent lists (and QuickConnect's, when installed) to add it to Listed Servers, or pick Other and type its address (host:port) or join code. The list is read when the game starts.",
            new AcceptableValueList<string>(choices)));
        _addKnown.Value = Pick;
        OtherChoices.Add(_addKnown, Other, typed => Add(typed, typed));
        _addKnown.SettingChanged += (_, _) => AddKnown(known);
    }

    private void AddKnown(List<(string Label, string Key)> known)
    {
        var picked = known.FirstOrDefault(server => Choice(server) == _addKnown.Value);
        _addKnown.Value = Pick;
        if (picked.Key != null)
        {
            Add(picked.Label, picked.Key);
        }
    }

    private void AddCurrent()
    {
        if (!_settings.AddCurrentServer.Value)
        {
            return;
        }
        _settings.AddCurrentServer.Value = false;
        if (CurrentKey.Length > 0 && ZNet.instance != null && !ZNet.instance.IsServer())
        {
            Add(CurrentLabel, CurrentKey);
        }
    }

    private void Add(string label, string key)
    {
        if (Listed().Any(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        var entry = label.Length > 0 && label != key ? $"{Clean(label)}={key}" : key;
        var current = _settings.ListedServers.Value.Trim().TrimEnd(';');
        _settings.ListedServers.Value = current.Length == 0 ? entry : $"{current}; {entry}";
    }

    // Each entry is [label=]address, then |name=value switches for that server.
    private IEnumerable<(string Label, string Key, Dictionary<string, string> Options)> Listed() =>
        _settings.ListedServers.Value.Split(';')
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .Select(part =>
            {
                var pieces = part.Split('|');
                var head = pieces[0].Trim();
                var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var piece in pieces.Skip(1))
                {
                    var at = piece.IndexOf('=');
                    if (at > 0)
                    {
                        options[piece.Substring(0, at).Trim()] = piece.Substring(at + 1).Trim();
                    }
                }
                var eq = head.LastIndexOf('=');
                return eq < 0 ? (head, head, options) : (head.Substring(0, eq).Trim(), head.Substring(eq + 1).Trim(), options);
            });

    private static string Choice((string Label, string Key) server) =>
        server.Label == server.Key ? server.Key : $"{server.Label} ({server.Key})";

    private static string Clean(string label) => label.Replace(";", ",").Replace("=", "-").Replace("|", "/");

    private List<(string Label, string Key)> Known()
    {
        var servers = new List<(string Label, string Key)>();
        foreach (var file in new[] { "favorite", "recent" })
        {
            try
            {
                using var list = new LocalServerList(file, ServerListGui.GetServerListLocations(file));
                servers.AddRange(list.m_list.Select(join => (NameOf(join), join.ToString())));
            }
            catch (Exception e)
            {
                _log.LogWarning($"Could not read the game's {file} server list ({e.GetBaseException().Message}).");
            }
        }
        servers.AddRange(QuickConnect());
        return servers.Where(server => server.Key.Length > 0).GroupBy(server => server.Key, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
    }

    private static string NameOf(ServerJoinData join) =>
        MultiBackendMatchmaking.TryGetServerName(join, out var name, out _) && !string.IsNullOrEmpty(name.m_name) ? name.m_name : join.ToString();

    // Only the name and address of each line are read: the password field never leaves the file.
    private static IEnumerable<(string Label, string Key)> QuickConnect()
    {
        var path = Path.Combine(Paths.ConfigPath, QuickConnectFile);
        if (!File.Exists(path))
        {
            return [];
        }
        return File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("#"))
            .Select(line => line.Split(':'))
            .Where(parts => parts.Length >= 3)
            .Select(parts => (parts[0].Trim(), $"{parts[1].Trim()}:{parts[2].Trim()}"));
    }

    [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.JoinServer))]
    internal static class JoinPatch
    {
        private static void Prefix(FejdStartup __instance)
        {
            var join = __instance.m_joinServer;
            CurrentKey = join.IsValid ? join.ToString() : "";
            CurrentLabel = join.IsValid ? NameOf(join) : "";
        }
    }
}
