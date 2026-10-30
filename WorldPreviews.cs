using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Areas drawn on the ground around the player: each crafting station's building range, each ward's protected area, the base areas where monsters do not spawn, the warmth of fires, and a map of the comfort level you would rest with at each spot. By default they show while a hammer is in hand, since that is when the player is laying out a base.
// Areas of one kind that touch or overlap draw as one shape, since the game treats them as one (building is allowed anywhere inside any station's range). Each kind has its own perimeter, fill and fill opacity.
// Shapes are traced on a grid by marching squares over "how far inside the nearest area this point is", and laid on the floor or ground under each grid point, never up the side of a wall: a cell whose corners differ in height by more than a step is laid flat.
// An optional card under the crosshair names the areas of the piece you look at and the comfort where you stand.
internal sealed class WorldPreviews
{
    private const string Section = "Previews";
    private const float ComfortReach = 10f;
    private const float Step = 0.7f;
    private const float Lift = 0.06f;
    private const int MaxCells = 220;

    internal enum Showing
    {
        With_Hammer,
        Always,
        Off
    }

    // One kind of area and how it is drawn.
    private sealed class Kind
    {
        public readonly string Name;
        public readonly Color Color;
        public readonly bool Merge;
        public readonly ConfigEntry<bool> Perimeter;
        public readonly ConfigEntry<bool> Fill;
        public readonly ConfigEntry<int> Opacity;
        public readonly List<(Vector3 Center, float Radius)> Areas = [];
        public readonly Shape Shape;
        public string Drawn = "";
        // Meters the edge is drawn inside the true border, so two kinds whose borders coincide (a workbench's 20 m build and safe areas) both stay visible side by side.
        public float Inset;

        public Kind(ConfigFile cfg, string name, string what, Color color, bool fill, bool merge, Material material)
        {
            Name = name;
            Color = color;
            Merge = merge;
            var section = Section + ": " + name;
            Perimeter = cfg.Bind(section, "Draw Perimeter", true, $"Draw the edge of {what}.");
            Fill = cfg.Bind(section, "Draw Fill", fill, $"Tint the ground inside {what}.");
            Opacity = cfg.Bind(section, "Fill Opacity (%)", 10, new ConfigDescription(
                $"How strongly the ground inside {what} is tinted, from 0 to 100%.", new AcceptableValueRange<int>(0, 100)));
            Shape = new Shape("SeneaLUIUnlocker_" + name.Replace(" ", ""), material);
        }

        // 100% is a tint of 0.1, the strongest that still leaves the ground readable.
        public float Alpha => Opacity.Value / 1000f;

        public bool Wanted => Perimeter.Value || Fill.Value && Opacity.Value > 0;
    }

    private readonly ConfigEntry<Showing> _show;
    private readonly ConfigEntry<float> _reach;
    private readonly ConfigEntry<bool> _card;
    private readonly ConfigEntry<float> _spacing;
    private readonly ConfigEntry<bool> _comfortNumbers;
    private readonly Kind _build;
    private readonly Kind _wards;
    private readonly Kind _safe;
    private readonly Kind _warmth;
    private readonly Kind _extensions;
    private readonly Kind _plants;
    private readonly Kind _comfort;
    private readonly Kind[] _circles;
    private readonly Material _material = new(Shader.Find("Sprites/Default"));
    private readonly List<TextMeshPro> _labels = [];
    private Vector3 _comfortAt = Vector3.positiveInfinity;
    private float _comfortTime;
    private float _areasTime;

    public WorldPreviews(ConfigFile cfg)
    {
        _show = cfg.Bind(Section, "Show Areas", Showing.With_Hammer,
            "When the areas are drawn on the ground: while a hammer is in hand, always, or never.");
        _reach = cfg.Bind(Section, "Area Preview Distance", 60f, new ConfigDescription(
            "How far from you, in meters, the areas of stations, wards and fires are drawn.", new AcceptableValueRange<float>(20f, 150f)));
        _card = cfg.Bind(Section, "Area Text Card", false,
            "Show a card under the crosshair naming the areas of the piece you look at or are placing, and the comfort where you stand.");
        _build = new Kind(cfg, "Build Areas", "each crafting station's building range", new Color(0.85f, 0.72f, 0.43f, 0.9f), true, true, _material);
        _wards = new Kind(cfg, "Ward Areas", "each ward's protected area", new Color(0.45f, 0.65f, 0.95f, 0.9f), false, true, _material);
        _safe = new Kind(cfg, "Safe Areas", "the base areas where monsters do not spawn", new Color(0.45f, 0.85f, 0.45f, 0.9f), false, true, _material);
        _warmth = new Kind(cfg, "Warmth Areas", "the area each fire keeps warm", new Color(0.95f, 0.5f, 0.2f, 0.9f), false, true, _material);
        _extensions = new Kind(cfg, "Extension Ranges", "how far from each matching station a station extension being placed may stand", new Color(0.85f, 0.72f, 0.43f, 0.9f), false, true, _material);
        _plants = new Kind(cfg, "Plant Spacing", "the room each nearby plant and the one being planted needs to grow (each plant apart, so overlaps show)", new Color(0.55f, 0.9f, 0.35f, 0.9f), false, false, _material);
        _safe.Inset = 0.35f;
        _wards.Inset = 0.7f;
        _comfort = new Kind(cfg, "Comfort Levels", "the area near comfort pieces (colored by the comfort level you would rest with there under a roof)", new Color(0.98f, 0.8f, 0.25f, 0.9f), false, true, _material);
        _comfortNumbers = cfg.Bind(Section + ": Comfort Levels", "Comfort Numbers", true,
            "Write the comfort level on the ground every few meters near comfort pieces.");
        _spacing = cfg.Bind(Section + ": Comfort Levels", "Comfort Spacing", 1.5f, new ConfigDescription(
            "Meters between the comfort level spots on the ground; smaller is finer and costs more to draw.", new AcceptableValueRange<float>(1f, 5f)));
        _circles = [_build, _wards, _safe, _warmth, _extensions, _plants];
    }

    private bool Shown(Player player)
    {
        if (player == null || player.IsDead() || _show.Value == Showing.Off || !ServerRules.Allows("Previews"))
        {
            return false;
        }
        if (_show.Value == Showing.Always)
        {
            return true;
        }
        var tool = player.GetRightItem();
        return tool != null && tool.m_shared.m_buildPieces != null && tool.m_dropPrefab != null && tool.m_dropPrefab.name.Contains("Hammer");
    }

    public void Tick()
    {
        var player = Player.m_localPlayer;
        var shown = Shown(player);
        var placing = player != null && !player.IsDead() && player.m_placementGhost != null && _show.Value != Showing.Off && ServerRules.Allows("Previews");
        if ((shown || placing) && Time.time >= _areasTime)
        {
            _areasTime = Time.time + 0.25f;
            foreach (var kind in _circles)
            {
                kind.Areas.Clear();
            }
            if (shown)
            {
                Collect(player.transform.position);
            }
            if (placing)
            {
                Placing(player.m_placementGhost);
            }
            var pieces = Placed(_circles.SelectMany(kind => kind.Areas).ToList());
            if (pieces != _pieceCount)
            {
                _pieceCount = pieces;
                Heights.Clear();
            }
        }
        foreach (var kind in _circles)
        {
            Draw(kind, (shown || placing) && kind.Wanted, player);
        }
        Comfort(shown && _comfort.Wanted ? player : null);
        Card(player);
    }

    private static readonly Collider[] Near = new Collider[64];

    // Areas that matter to the piece being placed: where a station extension may stand around each matching station, and the room plants need around the one being planted.
    private void Placing(GameObject ghost)
    {
        var at = ghost.transform.position;
        if (ghost.GetComponent<StationExtension>() is { } extension && extension.m_craftingStation != null)
        {
            foreach (var station in CraftingStation.m_allStations)
            {
                if (station != null && station.m_name == extension.m_craftingStation.m_name && Utils.DistanceXZ(station.transform.position, at) <= 40f)
                {
                    _extensions.Areas.Add((station.transform.position, extension.m_maxStationDistance));
                }
            }
        }
        if (ghost.GetComponent<Plant>() is { } planting)
        {
            _plants.Areas.Add((at, planting.m_growRadius));
            var count = Physics.OverlapSphereNonAlloc(at, 8f, Near);
            var seen = new HashSet<Plant>();
            for (var i = 0; i < count; i++)
            {
                var plant = Near[i] == null ? null : Near[i].GetComponentInParent<Plant>();
                if (plant != null && plant != planting && seen.Add(plant))
                {
                    _plants.Areas.Add((plant.transform.position, plant.m_growRadius));
                }
            }
        }
    }

    private void Collect(Vector3 at)
    {
        var reach = _reach.Value;
        bool Near(Vector3 point, float radius) => Utils.DistanceXZ(point, at) <= reach + radius;
        foreach (var station in CraftingStation.m_allStations)
        {
            var range = station == null ? 0f : station.GetStationBuildRange();
            if (range > 0f && Near(station.transform.position, range))
            {
                _build.Areas.Add((station.transform.position, range));
            }
        }
        foreach (var ward in PrivateArea.m_allAreas)
        {
            if (ward != null && ward.m_radius > 0f && Near(ward.transform.position, ward.m_radius))
            {
                _wards.Areas.Add((ward.transform.position, ward.m_radius));
            }
        }
        foreach (var area in EffectArea.s_allAreas)
        {
            if (area == null)
            {
                continue;
            }
            var radius = area.m_collider == null ? 0f : area.GetRadius();
            if (radius <= 0f || !Near(area.transform.position, radius))
            {
                continue;
            }
            if ((area.m_type & EffectArea.Type.PlayerBase) != 0)
            {
                _safe.Areas.Add((area.transform.position, radius));
            }
            if ((area.m_type & EffectArea.Type.Heat) != 0)
            {
                _warmth.Areas.Add((area.transform.position, radius));
            }
        }
    }

    // The height of the floor or ground under a point: the lowest upward-facing piece surface below a little above the given height, else the terrain. The lowest is the floor rather than a table, wall top or bench standing on it, and starting a little above the given height leaves out roofs and upper stories; rocks, logs and trees are not floors, so the shape lies on the ground beneath them.
    private static float Surface(float x, float z, float from)
    {
        var ground = ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(new Vector3(x, from, z), out var height) ? height : from;
        var top = Mathf.Max(from, ground) + 1.5f;
        var count = Physics.RaycastNonAlloc(new Vector3(x, top, z), Vector3.down, Hits, top - ground + 0.3f, SurfaceMask);
        var floor = float.MaxValue;
        for (var i = 0; i < count; i++)
        {
            if (Hits[i].normal.y > 0.7f && Hits[i].point.y < floor && Hits[i].point.y > ground - 0.3f)
            {
                floor = Hits[i].point.y;
            }
        }
        return floor < float.MaxValue ? floor : ground;
    }

    private static readonly RaycastHit[] Hits = new RaycastHit[16];

    // Surface heights by grid point, kept until a piece is built or removed nearby, so a shape redrawn as you walk samples only its new points.
    private static readonly Dictionary<(int, int, int), float> Heights = [];
    private static int _pieceCount = -1;

    // Pieces standing inside the drawn areas, measured from the areas and never from the player, so walking among unchanged pieces never throws the sampled heights away: a placement ghost has no network view of its own, so moving it never counts as building.
    private static int Placed(List<(Vector3 Center, float Radius)> areas)
    {
        var count = 0;
        foreach (var piece in Piece.s_allPieces)
        {
            if (piece != null && piece.m_nview != null && piece.m_nview.IsValid())
            {
                var p = piece.transform.position;
                foreach (var area in areas)
                {
                    var within = area.Radius + Step * 2f;
                    var dx = p.x - area.Center.x;
                    var dz = p.z - area.Center.z;
                    if (dx * dx + dz * dz <= within * within)
                    {
                        count++;
                        break;
                    }
                }
            }
        }
        return count;
    }

    private static float CachedSurface(int i, int j, float size, float from)
    {
        var key = (i, j, Mathf.RoundToInt(size * 100f));
        if (!Heights.TryGetValue(key, out var height))
        {
            Heights[key] = height = Surface(i * size, j * size, from);
        }
        return height;
    }

    private static readonly int SurfaceMask = LayerMask.GetMask("piece");

    private void Draw(Kind kind, bool on, Player player)
    {
        if (!on || kind.Areas.Count == 0)
        {
            kind.Shape.Hide();
            kind.Drawn = "";
            return;
        }
        var key = string.Join(";", kind.Areas.Select(area => $"{area.Center.x:0.0},{area.Center.y:0.0},{area.Center.z:0.0},{area.Radius:0.0}"))
            + $"|{kind.Perimeter.Value}{kind.Fill.Value}{kind.Opacity.Value}|{_pieceCount}";
        // Redrawn when the areas, the settings or the pieces around change.
        if (key == kind.Drawn)
        {
            kind.Shape.Show();
            return;
        }
        kind.Drawn = key;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var sampled = Heights.Count;
        var fill = new Color(kind.Color.r, kind.Color.g, kind.Color.b, kind.Fill.Value ? kind.Alpha : 0f);
        var builder = new ShapeBuilder();
        if (kind.Merge)
        {
            var areas = kind.Areas.ToList();
            builder.Trace(Bounds(areas), (x, z) => Inside(areas, x, z) - kind.Inset, areas.Average(area => area.Center.y), fill, kind.Perimeter.Value, kind.Fill.Value);
        }
        else
        {
            foreach (var area in kind.Areas)
            {
                List<(Vector3 Center, float Radius)> one = [area];
                builder.Trace(Bounds(one), (x, z) => Inside(one, x, z) - kind.Inset, area.Center.y, fill, kind.Perimeter.Value, kind.Fill.Value);
            }
        }
        kind.Shape.Set(builder, kind.Color);
        if (DebugLog.On)
        {
            DebugLog.Write("previews", $"{kind.Name}: {kind.Areas.Count} areas, {builder.Lines.Count} lines ({string.Join(", ", builder.Lines.Select(line => line.Count))} points), {builder.Vertices.Count} fill vertices, {Heights.Count - sampled} new surface samples, {watch.ElapsedMilliseconds} ms");
        }
    }

    // How far inside the nearest area a point is, in meters (negative outside every area).
    private static float Inside(List<(Vector3 Center, float Radius)> areas, float x, float z)
    {
        var best = float.MinValue;
        foreach (var (center, radius) in areas)
        {
            var dx = x - center.x;
            var dz = z - center.z;
            var value = radius - Mathf.Sqrt(dx * dx + dz * dz);
            if (value > best)
            {
                best = value;
            }
        }
        return best;
    }

    private static Rect Bounds(List<(Vector3 Center, float Radius)> areas)
    {
        var minX = areas.Min(area => area.Center.x - area.Radius);
        var minZ = areas.Min(area => area.Center.z - area.Radius);
        var maxX = areas.Max(area => area.Center.x + area.Radius);
        var maxZ = areas.Max(area => area.Center.z + area.Radius);
        return Rect.MinMaxRect(minX - 1f, minZ - 1f, maxX + 1f, maxZ + 1f);
    }

    // Triangles for the fill and polylines for the edge of a field traced on a grid, laid on the surface under each grid point.
    private sealed class ShapeBuilder
    {
        public readonly List<Vector3> Vertices = [];
        public readonly List<Color> Colors = [];
        public readonly List<int> Triangles = [];
        public readonly List<List<Vector3>> Lines = [];

        public void Trace(Rect bounds, System.Func<float, float, float> field, float level, Color fill, bool perimeter, bool filled, System.Func<float, float, Color> tint = null, float cell = 0f)
        {
            var size = cell > 0f ? cell : Mathf.Max(0.5f, Mathf.Max(bounds.width, bounds.height) / MaxCells);
            // On a world-aligned grid, so heights sampled for one drawing serve the next.
            var oi = Mathf.FloorToInt(bounds.xMin / size);
            var oj = Mathf.FloorToInt(bounds.yMin / size);
            bounds = Rect.MinMaxRect(oi * size, oj * size, bounds.xMax, bounds.yMax);
            var nx = Mathf.CeilToInt(bounds.width / size) + 1;
            var nz = Mathf.CeilToInt(bounds.height / size) + 1;
            var value = new float[nx, nz];
            for (var i = 0; i < nx; i++)
            {
                for (var j = 0; j < nz; j++)
                {
                    value[i, j] = field(bounds.xMin + i * size, bounds.yMin + j * size);
                }
            }
            var height = new float[nx, nz];
            var sampled = new bool[nx, nz];
            float H(int i, int j)
            {
                if (!sampled[i, j])
                {
                    sampled[i, j] = true;
                    height[i, j] = CachedSurface(oi + i, oj + j, size, level) + Lift;
                }
                return height[i, j];
            }
            // Edge crossing points by grid edge, so the edge segments of neighboring cells join into polylines.
            var segments = new List<(long, long)>();
            var points = new Dictionary<long, Vector3>();
            var ci = new int[4];
            var cj = new int[4];
            var inside = new bool[4];
            var heights = new float[4];
            var sorted = new float[4];
            var polygon = new List<Vector3>(8);
            var crossings = new List<long>(4);
            var edgeHeights = new Dictionary<long, float>();
            for (var i = 0; i < nx - 1; i++)
            {
                for (var j = 0; j < nz - 1; j++)
                {
                    ci[0] = i; cj[0] = j;
                    ci[1] = i + 1; cj[1] = j;
                    ci[2] = i + 1; cj[2] = j + 1;
                    ci[3] = i; cj[3] = j + 1;
                    var count = 0;
                    for (var c = 0; c < 4; c++)
                    {
                        inside[c] = value[ci[c], cj[c]] > 0f;
                        if (inside[c])
                        {
                            count++;
                        }
                    }
                    // Only cells the boundary crosses draw the edge, and inner cells matter only to the fill.
                    if (count == 0 || count == 4 && !filled)
                    {
                        continue;
                    }
                    var low = float.MaxValue;
                    var high = float.MinValue;
                    var n = 0;
                    for (var c = 0; c < 4; c++)
                    {
                        if (inside[c])
                        {
                            heights[c] = H(ci[c], cj[c]);
                            low = Mathf.Min(low, heights[c]);
                            high = Mathf.Max(high, heights[c]);
                            sorted[n++] = heights[c];
                        }
                    }
                    var flat = float.NaN;
                    if (high - low > Step)
                    {
                        System.Array.Sort(sorted, 0, n);
                        flat = sorted[(n - 1) / 2];
                    }
                    // The cell's polygon: its inside corners and the points where its edges cross the boundary, in order around the cell.
                    polygon.Clear();
                    crossings.Clear();
                    for (var c = 0; c < 4; c++)
                    {
                        var d = (c + 1) % 4;
                        if (inside[c])
                        {
                            polygon.Add(new Vector3(bounds.xMin + ci[c] * size, float.IsNaN(flat) ? heights[c] : flat, bounds.yMin + cj[c] * size));
                        }
                        if (inside[c] == inside[d])
                        {
                            continue;
                        }
                        var va = value[ci[c], cj[c]];
                        var t = va / (va - value[ci[d], cj[d]]);
                        var px = bounds.xMin + Mathf.Lerp(ci[c], ci[d], t) * size;
                        var pz = bounds.yMin + Mathf.Lerp(cj[c], cj[d], t) * size;
                        var edge = ci[c] == ci[d] ? ((long)ci[c] << 32 | (uint)Mathf.Min(cj[c], cj[d])) * 2 + 1 : ((long)Mathf.Min(ci[c], ci[d]) << 32 | (uint)cj[c]) * 2;
                        if (!edgeHeights.TryGetValue(edge, out var y))
                        {
                            edgeHeights[edge] = y = Surface(px, pz, level) + Lift;
                        }
                        var point = new Vector3(px, y, pz);
                        polygon.Add(float.IsNaN(flat) ? point : new Vector3(px, flat, pz));
                        crossings.Add(edge);
                        points[edge] = point;
                    }
                    if (filled && fill.a > 0f && polygon.Count >= 3)
                    {
                        var start = Vertices.Count;
                        foreach (var point in polygon)
                        {
                            Vertices.Add(point);
                            Colors.Add(tint == null ? fill : tint(point.x, point.z));
                        }
                        for (var k = 1; k < polygon.Count - 1; k++)
                        {
                            Triangles.Add(start);
                            Triangles.Add(start + k + 1);
                            Triangles.Add(start + k);
                        }
                    }
                    if (perimeter)
                    {
                        for (var k = 0; k + 1 < crossings.Count; k += 2)
                        {
                            segments.Add((crossings[k], crossings[k + 1]));
                        }
                    }
                }
            }
            if (perimeter)
            {
                Chain(segments, points);
            }
        }

        // Joins the edge segments into polylines, broken wherever the edge steps up or down more than a step so it never runs up a wall.
        private void Chain(List<(long A, long B)> segments, Dictionary<long, Vector3> points)
        {
            var neighbors = new Dictionary<long, List<long>>();
            foreach (var (a, b) in segments)
            {
                Add(neighbors, a, b);
                Add(neighbors, b, a);
            }
            var used = new HashSet<long>();
            foreach (var start in neighbors.Keys.OrderBy(edge => neighbors[edge].Count))
            {
                if (used.Contains(start))
                {
                    continue;
                }
                var line = new List<Vector3>();
                long? at = start;
                while (at is { } edge && used.Add(edge))
                {
                    var point = points[edge];
                    if (line.Count > 0 && Mathf.Abs(line[line.Count - 1].y - point.y) > Step)
                    {
                        Finish(line);
                        line = [];
                    }
                    line.Add(point);
                    at = null;
                    foreach (var next in neighbors[edge])
                    {
                        if (!used.Contains(next))
                        {
                            at = next;
                            break;
                        }
                    }
                }
                Finish(line);
            }
        }

        // Chaikin corner cutting: each pass replaces every corner by two points a quarter of the way along its sides, keeping the ends of an open line.
        public void Smooth(int passes)
        {
            for (var l = 0; l < Lines.Count; l++)
            {
                var line = Lines[l];
                for (var pass = 0; pass < passes && line.Count > 2; pass++)
                {
                    var closed = Vector3.Distance(line[0], line[line.Count - 1]) < 0.01f;
                    var next = new List<Vector3>(line.Count * 2);
                    if (!closed)
                    {
                        next.Add(line[0]);
                    }
                    for (var i = 0; i + 1 < line.Count; i++)
                    {
                        next.Add(Vector3.Lerp(line[i], line[i + 1], 0.25f));
                        next.Add(Vector3.Lerp(line[i], line[i + 1], 0.75f));
                    }
                    if (closed)
                    {
                        next.Add(next[0]);
                    }
                    else
                    {
                        next.Add(line[line.Count - 1]);
                    }
                    line = next;
                }
                Lines[l] = line;
            }
        }

        private static void Add(Dictionary<long, List<long>> neighbors, long a, long b)
        {
            if (!neighbors.TryGetValue(a, out var list))
            {
                neighbors[a] = list = [];
            }
            list.Add(b);
        }

        private void Finish(List<Vector3> line)
        {
            if (line.Count >= 2)
            {
                Lines.Add(line.Select(point => point + Vector3.up * 0.1f).ToList());
            }
        }
    }

    // What one kind draws: a fill mesh and a pool of lines.
    private sealed class Shape
    {
        private readonly GameObject _root;
        private readonly MeshFilter _fill;
        private readonly Material _material;
        private readonly List<LineRenderer> _lines = [];

        public Shape(string name, Material material)
        {
            _material = material;
            _root = new GameObject(name);
            Object.DontDestroyOnLoad(_root);
            var fill = new GameObject("fill", typeof(MeshFilter), typeof(MeshRenderer));
            fill.transform.SetParent(_root.transform, false);
            fill.GetComponent<MeshRenderer>().sharedMaterial = material;
            _fill = fill.GetComponent<MeshFilter>();
            _fill.sharedMesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            _root.SetActive(false);
        }

        public void Hide()
        {
            if (_root.activeSelf)
            {
                _root.SetActive(false);
            }
        }

        public void Show()
        {
            if (!_root.activeSelf)
            {
                _root.SetActive(true);
            }
        }

        public void Set(ShapeBuilder shape, Color line)
        {
            Show();
            var mesh = _fill.sharedMesh;
            mesh.Clear();
            mesh.SetVertices(shape.Vertices);
            mesh.SetColors(shape.Colors);
            mesh.SetTriangles(shape.Triangles, 0);
            mesh.RecalculateBounds();
            while (_lines.Count < shape.Lines.Count)
            {
                var go = new GameObject("line");
                go.transform.SetParent(_root.transform, false);
                var renderer = go.AddComponent<LineRenderer>();
                renderer.useWorldSpace = true;
                renderer.widthMultiplier = 0.2f;
                renderer.material = _material;
                _lines.Add(renderer);
            }
            for (var i = 0; i < _lines.Count; i++)
            {
                var on = i < shape.Lines.Count;
                _lines[i].gameObject.SetActive(on);
                if (!on)
                {
                    continue;
                }
                var points = shape.Lines[i];
                var closed = points.Count > 2 && Vector3.Distance(points[0], points[points.Count - 1]) < 1.2f;
                _lines[i].loop = closed;
                _lines[i].positionCount = points.Count;
                _lines[i].SetPositions(points.ToArray());
                _lines[i].startColor = _lines[i].endColor = line;
            }
        }
    }

    // Comfort level at each spot near comfort pieces, as the game counts it under a roof (SE_Rested.CalculateComfortLevel), recomputed as you move or build.
    private void Comfort(Player player)
    {
        if (player == null)
        {
            _comfort.Shape.Hide();
            foreach (var label in _labels)
            {
                if (label.gameObject.activeSelf)
                {
                    label.gameObject.SetActive(false);
                }
            }
            _comfortAt = Vector3.positiveInfinity;
            return;
        }
        _comfort.Shape.Show();
        var at = player.transform.position;
        var settings = $"{_comfort.Perimeter.Value}{_comfort.Fill.Value}{_comfort.Opacity.Value}{_comfortNumbers.Value}{_spacing.Value}";
        if (Vector3.Distance(at, _comfortAt) < 2f && Time.time < _comfortTime && settings == _comfort.Drawn)
        {
            return;
        }
        _comfortAt = at;
        _comfortTime = Time.time + 2f;
        _comfort.Drawn = settings;
        var pieces = new List<Piece>();
        Piece.GetAllComfortPiecesInRadius(at, _reach.Value * 0.5f + ComfortReach, pieces);
        pieces.RemoveAll(piece => piece == null);
        var reach = _reach.Value * 0.5f;
        var step = _spacing.Value;
        var bounds = Rect.MinMaxRect(Mathf.Round((at.x - reach) / step) * step, Mathf.Round((at.z - reach) / step) * step, Mathf.Round((at.x + reach) / step) * step, Mathf.Round((at.z + reach) / step) * step);
        var levels = new Dictionary<(int, int), int>();
        int LevelAt(float x, float z)
        {
            var cell = (Mathf.RoundToInt(x / step), Mathf.RoundToInt(z / step));
            if (levels.TryGetValue(cell, out var level))
            {
                return level;
            }
            var point = new Vector3(cell.Item1 * step, 0f, cell.Item2 * step);
            if (!pieces.Any(piece => Utils.DistanceXZ(piece.transform.position, point) <= ComfortReach))
            {
                return levels[cell] = 1;
            }
            point.y = Surface(point.x, point.z, at.y);
            return levels[cell] = SE_Rested.CalculateComfortLevel(true, point);
        }
        var builder = new ShapeBuilder();
        var alpha = _comfort.Fill.Value ? _comfort.Alpha : 0f;
        if (pieces.Count > 0)
        {
            builder.Trace(bounds, (x, z) => LevelAt(x, z) - 1.5f, at.y, new Color(1f, 1f, 1f, alpha), _comfort.Perimeter.Value, _comfort.Fill.Value,
                (x, z) => Level(LevelAt(x, z), alpha), step);
            // Comfort is a whole number per grid point, so its edge can only fall midway between points and comes out in steps; rounding the corners off draws the circles it really follows.
            builder.Smooth(3);
        }
        _comfort.Shape.Set(builder, _comfort.Color);
        var spots = new List<(Vector3 Point, int Level)>();
        if (_comfortNumbers.Value)
        {
            foreach (var pair in levels.Where(pair => pair.Value > 1 && pair.Key.Item1 % 3 == 0 && pair.Key.Item2 % 3 == 0))
            {
                var x = pair.Key.Item1 * step;
                var z = pair.Key.Item2 * step;
                spots.Add((new Vector3(x, Surface(x, z, at.y) + 0.15f, z), pair.Value));
            }
        }
        Labels(spots);
    }

    // Comfort 1 (nothing) gray, rising through brass to gold at 10 and above.
    private static Color Level(int level, float alpha)
    {
        var t = Mathf.Clamp01((level - 1) / 9f);
        var color = Color.Lerp(new Color(0.66f, 0.61f, 0.53f), new Color(0.98f, 0.8f, 0.25f), t);
        color.a = alpha;
        return color;
    }

    private void Labels(List<(Vector3 Point, int Level)> spots)
    {
        while (_labels.Count < spots.Count)
        {
            // Created inactive so TextMeshPro first wakes with SeneaL UI's font already set, never looking for the default font the game does not ship.
            var go = new GameObject("SeneaLUIUnlocker_ComfortLabel");
            go.SetActive(false);
            Object.DontDestroyOnLoad(go);
            var text = go.AddComponent<TextMeshPro>();
            var font = SeneaLTheme.Font("Medium");
            if (font != null)
            {
                text.font = font;
            }
            text.fontSize = 3f;
            text.alignment = TextAlignmentOptions.Center;
            text.color = SeneaLTheme.Color("Parchment", new Color(0.93f, 0.88f, 0.76f));
            _labels.Add(text);
        }
        for (var i = 0; i < _labels.Count; i++)
        {
            var on = i < spots.Count;
            if (_labels[i].gameObject.activeSelf != on)
            {
                _labels[i].gameObject.SetActive(on);
            }
            if (!on)
            {
                continue;
            }
            _labels[i].text = spots[i].Level.ToString();
            _labels[i].transform.position = spots[i].Point;
            _labels[i].transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }
    }

    private readonly ThemedCard _cardView = new ThemedCard(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 1f), new Vector2(0f, -60f), 29)
        .Element("AreaCard", "Area Card", "Building range 20 m\nNo monster spawns 20 m\nComfort here: 3");

    // The optional card: the areas of the piece looked at or being placed, and the comfort where you stand.
    private void Card(Player player)
    {
        if (!_card.Value || player == null || player.IsDead())
        {
            _cardView.Show(null);
            return;
        }
        var lines = new List<string>();
        var target = player.m_placementGhost != null ? player.m_placementGhost : player.GetHoverObject();
        var piece = target == null ? null : target.GetComponentInParent<Piece>();
        var root = piece != null ? piece.gameObject : target;
        if (root != null)
        {
            if (root.GetComponentInChildren<CraftingStation>(true) is { } station && station.m_rangeBuild > 0f)
            {
                lines.Add($"Building range {station.GetStationBuildRange():0} m");
            }
            if (root.GetComponentInChildren<PrivateArea>(true) is { } ward && ward.m_radius > 0f)
            {
                lines.Add($"Ward area {ward.m_radius:0} m");
            }
            foreach (var area in root.GetComponentsInChildren<EffectArea>(true))
            {
                var radius = area.m_collider == null ? 0f : area.GetRadius();
                if (radius > 0f && (area.m_type & EffectArea.Type.PlayerBase) != 0)
                {
                    lines.Add($"No monster spawns {radius:0} m");
                }
                if (radius > 0f && (area.m_type & EffectArea.Type.Heat) != 0)
                {
                    lines.Add($"Warmth {radius:0} m");
                }
            }
        }
        if (piece != null && piece.m_comfort > 0)
        {
            lines.Add($"Comfort here: {SE_Rested.CalculateComfortLevel(player)}   This piece: +{piece.m_comfort}");
        }
        _cardView.Show(string.Join("\n", lines));
    }
}
