using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace SeneaLUIUnlocker;

// Two optional cards, each an element of SeneaL UI's HUD editor. Weather Forecast lists the weather where you stand now and the next changes with the time left until each: the game picks each period's weather from the world clock and the biome alone, so the same pick made ahead of time is the weather that comes (an event, such as a raid, can still override it). Base Status names what applies where you stand: monster-free base area, building range, wards, shelter and the comfort you would rest with.
internal sealed class StatusCards
{
    private readonly ConfigEntry<bool> _forecast;
    private readonly ConfigEntry<bool> _status;
    private readonly ConfigEntry<int> _changes;
    // Without SeneaL UI's HUD editor (SeneaL UI 1.1.x) FallbackColumn stacks these under the minimap, wind and clock SeneaL UI draws at the top right.
    private readonly ThemedCard _forecastCard = new ThemedCard(new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-24f, -560f), 27, 14f)
        .Element("Forecast", "Weather Forecast", "Weather now: Clear\nRain in 4m 10s\nClear in 9m 30s");
    private readonly ThemedCard _statusCard = new ThemedCard(new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-24f, -660f), 27, 14f)
        .Element("BaseStatus", "Base Status", "Safe from monster spawns\nIn building range (Workbench)\nComfort level 7");
    private float _next;

    public StatusCards(ConfigFile cfg)
    {
        _forecast = cfg.Bind("Cheats: Information", "Weather Forecast (Cheat)", false,
            "A card with the weather where you stand and the next changes, each with the time until it comes. A cheat: the game never shows coming weather.");
        _changes = cfg.Bind("Cheats: Information", "Forecast Changes", 3, new ConfigDescription(
            "How many coming weather changes the Weather Forecast lists.", new AcceptableValueRange<int>(1, 6)));
        _status = cfg.Bind("Status Cards", "Base Status", false,
            "A card naming what applies where you stand: monster-free base area, building range, wards, shelter and comfort. Off by default: it is one more card on the screen.");
    }

    public void Tick(float now)
    {
        var player = Player.m_localPlayer;
        var alive = player != null && !player.IsDead() && Hud.instance != null;
        if (now >= _next)
        {
            _next = now + 1f;
            _forecastCard.Show(alive && _forecast.Value && ServerRules.Allows("WeatherForecast") ? Forecast(_changes.Value) : null);
            _statusCard.Show(alive && _status.Value && ServerRules.Allows("BaseStatus") ? Status(player) : null);
        }
    }

    private static string Forecast(int changes)
    {
        var env = EnvMan.instance;
        if (env == null || ZNet.instance == null || env.m_environmentDuration <= 0)
        {
            return null;
        }
        if (!string.IsNullOrEmpty(env.GetEnvironmentOverride()))
        {
            return $"Weather now: {Pretty(env.GetCurrentEnvironment()?.m_name)} (event)";
        }
        var seconds = ZNet.instance.GetTimeSeconds();
        var period = (long)seconds / env.m_environmentDuration;
        var biome = env.GetBiome();
        var available = env.GetAvailableEnvironments(biome)?.ToList();
        if (available == null || available.Count == 0)
        {
            return null;
        }
        var position = Utils.GetMainCamera() == null ? Vector3.zero : Utils.GetMainCamera().transform.position;
        var ashlands = WorldGenerator.IsAshlands(position.x, position.z);
        var deepNorth = WorldGenerator.IsDeepnorth(position.x, position.y);
        var state = Random.state;
        var lines = new List<string>();
        try
        {
            string last = null;
            for (var k = 0L; k < 40 && lines.Count <= changes; k++)
            {
                Random.InitState((int)(period + k));
                var pick = env.SelectWeightedEnvironment(available);
                foreach (var entry in available)
                {
                    if (entry.m_ashlandsOverride && ashlands || entry.m_deepnorthOverride && deepNorth)
                    {
                        pick = entry.m_env;
                    }
                }
                var name = Pretty(pick?.m_name);
                if (name == last)
                {
                    continue;
                }
                last = name;
                lines.Add(k == 0 ? $"Weather now: {name}" : $"{name} in {TameInfo.Time((period + k) * env.m_environmentDuration - seconds)}");
            }
        }
        finally
        {
            Random.state = state;
        }
        return string.Join("\n", lines);
    }

    private static string Pretty(string name) => string.IsNullOrEmpty(name) ? "?" : SettingsPage.Pretty(name.Replace('_', ' '));

    private static string Status(Player player)
    {
        var at = player.transform.position;
        var lines = new List<string>
        {
            EffectArea.IsPointInsideArea(at, EffectArea.Type.PlayerBase) != null ? "Safe from monster spawns" : "Monsters may spawn here"
        };
        var station = CraftingStation.m_allStations.FirstOrDefault(s => s != null && s.GetStationBuildRange() > 0f && Utils.DistanceXZ(s.transform.position, at) < s.GetStationBuildRange());
        lines.Add(station != null ? $"In building range ({Localization.instance.Localize(station.m_name)})" : "No crafting station in range");
        var ward = PrivateArea.m_allAreas.FirstOrDefault(w => w != null && w.IsEnabled() && w.IsInside(at, 0f));
        if (ward != null)
        {
            lines.Add(ward.HaveLocalAccess() ? "Inside a ward that admits you" : "Inside someone else's ward");
        }
        if (player.InShelter())
        {
            lines.Add("Sheltered");
        }
        lines.Add($"Comfort level {SE_Rested.CalculateComfortLevel(player)}");
        return string.Join("\n", lines);
    }
}
