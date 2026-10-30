using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

internal enum ShelterCardShow
{
    Always,
    AtBase,
    Off
}

// Shelter And Comfort: a card, an element of SeneaL UI's HUD editor, that explains the game's own Shelter status and comfort level where the player stands: how many of the game's 17 cover rays hit something, whether a roof is overhead, which directions are open, the comfort pieces counted per group, and the known hammer pieces that would add the most comfort here.
internal static class ShelterComfort
{
    private const string Section = "Status Cards";
    private const string RuleKey = "ShelterComfort";
    private const float ComfortRadius = 10f;
    private const float RayStart = 0.5f;
    private const float RayLength = 30f;
    private const float RoofLength = 100f;
    private const float ShelterShare = 0.8f;
    private const float NearFireSeconds = 0.25f;
    private const string Sample = "<b>Not sheltered</b>: covered 12 of 17 directions (needs 14), no roof\nAdd a roof overhead\nClose the walls to the north and east\n<b>Comfort 1</b>: beyond 1 needs shelter\nFire: Campfire +1\nBuild a Raven throne: +3";

    private static readonly string[] Compass = ["north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest"];

    private static ConfigEntry<ShelterCardShow> _show;
    private static ConfigEntry<int> _tips;
    private static ConfigEntry<bool> _cover;
    private static ConfigEntry<bool> _pieces;

    private static readonly ThemedCard Card = new ThemedCard(new Vector2(1f, 0.5f), new Vector2(1f, 1f), new Vector2(-24f, -70f), 27, 14f)
        .Element("ShelterComfort", "Sheltered and Comfort", Sample);

    private static readonly StringBuilder Text = new StringBuilder(512);
    private static readonly RaycastHit[] Hits = new RaycastHit[128];
    private static readonly List<Piece> Nearby = [];
    private static readonly HashSet<string> CountedNames = new(StringComparer.Ordinal);
    private static readonly HashSet<string> TipNames = new(StringComparer.Ordinal);
    private static readonly List<Tip> Tips = [];
    private static readonly List<Piece> Candidates = [];
    private static readonly List<GameObject> CandidatePrefabs = [];
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal);
    private static readonly bool[] Open = new bool[8];
    private static readonly IComparer<Piece> ComfortOrder = Comparer<Piece>.Create(SE_Rested.PieceComfortSort);

    private static string[] _groupNames;
    private static int[] _groupComfort;
    private static Piece[] _groupPiece;
    private static Piece[] _bestPiece;
    private static int[] _rayCompass;
    private static ObjectDB _candidatesFrom;

    private static float _next;
    private static string _last;
    private static bool _sample;
    private static bool _loggedShelter;
    private static int _loggedComfort = -1;

    public static void Bind(ConfigFile cfg)
    {
        _show = cfg.Bind(Section, "Shelter And Comfort Card", ShelterCardShow.AtBase,
            "A card that explains the game's Shelter status and comfort level where you stand: the cover the game counts, the open directions to close, the comfort pieces counted and the known pieces that would add the most comfort. At Base shows it inside a crafting station's building range or a ward, or within 10 m of a comfort piece.");
        _tips = cfg.Bind(Section, "Comfort Tips", 3,
            new ConfigDescription("Most suggestions of known pieces that would raise the comfort where you stand, the largest gain first.", new AcceptableValueRange<int>(0, 6)));
        _cover = cfg.Bind(Section, "Shelter Shows Cover", true,
            "The Shelter And Comfort card says how many of the game's 17 directions are covered, how many shelter needs, and whether a roof is overhead.");
        _pieces = cfg.Bind(Section, "Shelter Shows Comfort Pieces", true,
            "The Shelter And Comfort card lists the comfort pieces the game counts where you stand, each with what it adds.");
    }

    public static void Tick(float now)
    {
        Card.Prepare();
        if (SeneaLHud.EditorOpen)
        {
            if (!_sample)
            {
                _sample = true;
                _last = null;
                Card.Show(null);
            }
            return;
        }
        if (_sample)
        {
            _sample = false;
            _next = 0f;
            Card.Show(null);
        }
        var player = Player.m_localPlayer;
        var on = _show.Value != ShelterCardShow.Off && ServerRules.Allows(RuleKey) && player != null && !player.IsDead() && Hud.instance != null && !Hud.IsUserHidden();
        if (!on)
        {
            Shown(null);
            _next = 0f;
            return;
        }
        if (now < _next)
        {
            return;
        }
        _next = now + 1f;
        Refresh(player);
    }

    private static void Shown(string text)
    {
        if (!ReferenceEquals(text, _last))
        {
            _last = text;
            Card.Show(text);
        }
    }

    private static void Refresh(Player player)
    {
        Setup();
        var at = player.transform.position;
        Nearby.Clear();
        Piece.GetAllComfortPiecesInRadius(at, ComfortRadius, Nearby);
        if (_show.Value == ShelterCardShow.AtBase && Nearby.Count == 0 && !InBuildRange(at) && !InWard(at))
        {
            Shown(null);
            return;
        }
        Text.Length = 0;
        var sheltered = player.InShelter();
        AppendShelter(player, sheltered);
        var comfort = SE_Rested.CalculateComfortLevel(player);
        AppendComfort(player, sheltered, comfort);
        AppendTips(player);
        LogChange(sheltered, comfort);
        if (_last == null || !Same(Text, _last))
        {
            Shown(Text.ToString());
        }
    }

    private static void Setup()
    {
        if (_rayCompass != null)
        {
            return;
        }
        Cover.Setup();
        var rays = Cover.m_coverRays;
        _rayCompass = new int[rays.Length];
        for (var i = 0; i < rays.Length; i++)
        {
            var ray = rays[i];
            _rayCompass[i] = ray.x == 0f && ray.z == 0f ? -1 : ((Mathf.RoundToInt(Mathf.Atan2(ray.x, ray.z) * Mathf.Rad2Deg / 45f) % 8) + 8) % 8;
        }
        var groups = Enum.GetNames(typeof(Piece.ComfortGroup));
        _groupNames = new string[groups.Length];
        for (var i = 0; i < groups.Length; i++)
        {
            _groupNames[i] = SettingsPage.Pretty(groups[i]);
        }
        _groupComfort = new int[groups.Length];
        _groupPiece = new Piece[groups.Length];
        _bestPiece = new Piece[groups.Length];
    }

    private static bool InBuildRange(Vector3 at)
    {
        foreach (var station in CraftingStation.m_allStations)
        {
            if (station != null && station.GetStationBuildRange() > 0f && Utils.DistanceXZ(station.transform.position, at) < station.GetStationBuildRange())
            {
                return true;
            }
        }
        return false;
    }

    private static bool InWard(Vector3 at)
    {
        foreach (var ward in PrivateArea.m_allAreas)
        {
            if (ward != null && ward.IsInside(at, 0f))
            {
                return true;
            }
        }
        return false;
    }

    // The same rays, mask and start offset as the game's Cover.GetCoverForPoint, cast one by one so the open ones can be named; the count shown is the player's own m_coverPercentage, which the game's Shelter status reads.
    private static void AppendShelter(Player player, bool sheltered)
    {
        var rays = Cover.m_coverRays;
        var center = player.GetCenterPoint();
        var upOpen = false;
        for (var i = 0; i < Open.Length; i++)
        {
            Open[i] = false;
        }
        for (var i = 0; i < rays.Length; i++)
        {
            var ray = rays[i];
            if (Physics.Raycast(center + ray * RayStart, ray, out _, RayLength - RayStart, Cover.m_coverRayMask))
            {
                continue;
            }
            if (_rayCompass[i] < 0)
            {
                upOpen = true;
            }
            else
            {
                Open[_rayCompass[i]] = true;
            }
        }
        var covered = Mathf.RoundToInt(player.m_coverPercentage * rays.Length);
        var needed = Mathf.CeilToInt(ShelterShare * rays.Length);
        var roof = player.m_underRoof ? 1 : Physics.SphereCastNonAlloc(center, 0.1f, Vector3.up, Hits, RoofLength, Cover.m_coverRayMask) > 0 ? 2 : 0;
        Text.Append(sheltered ? "<b>Sheltered</b>" : "<b>Not sheltered</b>");
        if (_cover.Value)
        {
            Text.Append(": covered ").Append(covered).Append(" of ").Append(rays.Length).Append(" directions");
            if (!sheltered)
            {
                Text.Append(" (needs ").Append(needed).Append(')');
            }
            Text.Append(roof == 1 ? ", roof overhead" : roof == 2 ? ", roof leaks" : ", no roof");
        }
        if (sheltered)
        {
            return;
        }
        if (roof == 2)
        {
            Text.Append("\nPut a solid roof overhead");
        }
        else if (roof == 0 || upOpen)
        {
            Text.Append("\nAdd a roof overhead");
        }
        AppendWalls();
    }

    // A corner is named only when neither wall beside it is open, since closing those walls usually closes the corner too.
    private static void AppendWalls()
    {
        var all = true;
        for (var i = 0; i < Open.Length; i++)
        {
            all &= Open[i];
        }
        if (all)
        {
            Text.Append("\nBuild walls all around");
            return;
        }
        var count = 0;
        for (var i = 0; i < Open.Length; i++)
        {
            if (Named(i))
            {
                count++;
            }
        }
        if (count == 0)
        {
            return;
        }
        Text.Append("\nClose the walls to the ");
        var written = 0;
        for (var i = 0; i < Open.Length; i++)
        {
            if (!Named(i))
            {
                continue;
            }
            if (written > 0)
            {
                Text.Append(written == count - 1 ? " and " : ", ");
            }
            Text.Append(Compass[i]);
            written++;
        }
    }

    private static bool Named(int direction) => Open[direction] && (direction % 2 == 0 || !Open[(direction + 7) % 8] && !Open[(direction + 1) % 8]);

    // Walks the nearby pieces in the order and with the skip rule of SE_Rested.CalculateComfortLevel, so the lines name exactly the pieces the game adds.
    private static void AppendComfort(Player player, bool sheltered, int comfort)
    {
        for (var i = 0; i < _groupComfort.Length; i++)
        {
            _groupComfort[i] = 0;
            _groupPiece[i] = null;
        }
        CountedNames.Clear();
        Nearby.Sort(ComfortOrder);
        Piece previous = null;
        var others = 0;
        foreach (var piece in Nearby)
        {
            var skip = previous != null && (piece.m_comfortGroup != Piece.ComfortGroup.None && piece.m_comfortGroup == previous.m_comfortGroup || piece.m_name == previous.m_name);
            previous = piece;
            if (skip)
            {
                continue;
            }
            var group = (int)piece.m_comfortGroup;
            if (group == 0)
            {
                if (CountedNames.Add(piece.m_name) && piece.GetComfort() > 0)
                {
                    others++;
                }
                continue;
            }
            if (group < _groupPiece.Length)
            {
                _groupPiece[group] = piece;
                _groupComfort[group] = piece.GetComfort();
            }
        }
        Text.Append("\n<b>Comfort ").Append(comfort).Append("</b>");
        if (!sheltered)
        {
            Text.Append(": beyond 1 needs shelter");
        }
        if (player.m_nearFireTimer >= NearFireSeconds)
        {
            Text.Append("\nNo fire within reach to rest by");
        }
        if (!_pieces.Value)
        {
            return;
        }
        for (var i = 1; i < _groupPiece.Length; i++)
        {
            if (_groupPiece[i] != null && _groupComfort[i] > 0)
            {
                Text.Append('\n').Append(_groupNames[i]).Append(": ").Append(Name(_groupPiece[i])).Append(" +").Append(_groupComfort[i]);
            }
        }
        if (others == 0)
        {
            return;
        }
        Text.Append("\nOther: ");
        var written = 0;
        previous = null;
        foreach (var piece in Nearby)
        {
            var skip = previous != null && piece.m_name == previous.m_name;
            previous = piece;
            if (skip || piece.m_comfortGroup != Piece.ComfortGroup.None || piece.GetComfort() <= 0)
            {
                continue;
            }
            if (written > 0)
            {
                Text.Append(", ");
            }
            Text.Append(Name(piece)).Append(" +").Append(piece.GetComfort());
            written++;
        }
    }

    // Per comfort group the best known hammer piece that beats what the group adds now, and every known ungrouped piece not yet near, ranked by the comfort it would add.
    private static void AppendTips(Player player)
    {
        var most = _tips.Value;
        if (most <= 0 || !LoadCandidates())
        {
            return;
        }
        for (var i = 0; i < _bestPiece.Length; i++)
        {
            _bestPiece[i] = null;
        }
        Tips.Clear();
        TipNames.Clear();
        for (var i = 0; i < Candidates.Count; i++)
        {
            var piece = Candidates[i];
            if (piece == null || !Known(player, piece, CandidatePrefabs[i]))
            {
                continue;
            }
            var group = (int)piece.m_comfortGroup;
            if (group == 0)
            {
                if (!CountedNames.Contains(piece.m_name) && TipNames.Add(piece.m_name))
                {
                    Add(new Tip { Piece = piece, Gain = piece.m_comfort });
                }
                continue;
            }
            if (group < _bestPiece.Length && piece.m_comfort > _groupComfort[group] && (_bestPiece[group] == null || piece.m_comfort > _bestPiece[group].m_comfort))
            {
                _bestPiece[group] = piece;
            }
        }
        for (var i = 1; i < _bestPiece.Length; i++)
        {
            if (_bestPiece[i] != null)
            {
                Add(new Tip { Piece = _bestPiece[i], Gain = _bestPiece[i].m_comfort - _groupComfort[i] });
            }
        }
        for (var i = 0; i < Tips.Count && i < most; i++)
        {
            var name = Name(Tips[i].Piece);
            Text.Append("\nBuild ").Append(StartsWithVowel(name) ? "an " : "a ").Append(name).Append(": +").Append(Tips[i].Gain);
        }
    }

    // Kept sorted by gain as tips are added, so no comparer or delegate is made per refresh.
    private static void Add(Tip tip)
    {
        var at = Tips.Count;
        while (at > 0 && Tips[at - 1].Gain < tip.Gain)
        {
            at--;
        }
        Tips.Insert(at, tip);
    }

    // The same tests the game's PieceTable.UpdateAvailable puts a hammer piece through before the build menu lists it, plus Player.HaveRequirements in its IsKnown mode.
    private static bool Known(Player player, Piece piece, GameObject prefab)
    {
        if (!player.IsRecipeKnown(piece.m_name))
        {
            return false;
        }
        if (!piece.m_enabled && (player.CurrentSeason == null || !player.CurrentSeason.Pieces.Contains(prefab)))
        {
            return false;
        }
        return player.HaveRequirements(piece, Player.RequirementMode.IsKnown);
    }

    private static bool LoadCandidates()
    {
        var db = ObjectDB.instance;
        if (db == null)
        {
            return false;
        }
        if (_candidatesFrom == db)
        {
            return Candidates.Count > 0;
        }
        _candidatesFrom = db;
        Candidates.Clear();
        CandidatePrefabs.Clear();
        var hammer = db.GetItemPrefab("Hammer");
        var drop = hammer == null ? null : hammer.GetComponent<ItemDrop>();
        var table = drop == null ? null : drop.m_itemData.m_shared.m_buildPieces;
        if (table == null)
        {
            return false;
        }
        foreach (var prefab in table.m_pieces)
        {
            var piece = prefab == null ? null : prefab.GetComponent<Piece>();
            if (piece != null && piece.m_comfort > 0)
            {
                Candidates.Add(piece);
                CandidatePrefabs.Add(prefab);
            }
        }
        DebugLog.Write("shelter", $"{Candidates.Count} comfort pieces in the hammer's build pieces");
        return Candidates.Count > 0;
    }

    private static string Name(Piece piece)
    {
        if (!Names.TryGetValue(piece.m_name, out var name))
        {
            name = Localization.instance == null ? piece.m_name : Localization.instance.Localize(piece.m_name);
            Names[piece.m_name] = name;
        }
        return name;
    }

    private static bool StartsWithVowel(string name) => name.Length > 0 && "AEIOUaeiou".IndexOf(name[0]) >= 0;

    private static bool Same(StringBuilder built, string text)
    {
        if (built.Length != text.Length)
        {
            return false;
        }
        for (var i = 0; i < text.Length; i++)
        {
            if (built[i] != text[i])
            {
                return false;
            }
        }
        return true;
    }

    private static void LogChange(bool sheltered, int comfort)
    {
        if (!DebugLog.On || sheltered == _loggedShelter && comfort == _loggedComfort)
        {
            return;
        }
        _loggedShelter = sheltered;
        _loggedComfort = comfort;
        DebugLog.Write("shelter", $"{(sheltered ? "sheltered" : "not sheltered")}, comfort {comfort}, {Nearby.Count} comfort pieces within {ComfortRadius} m");
    }

    private struct Tip
    {
        public Piece Piece;
        public int Gain;
    }
}
