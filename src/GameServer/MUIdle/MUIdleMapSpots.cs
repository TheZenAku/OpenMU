// <copyright file="MUIdleMapSpots.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Text.Json;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// HUNT v2: the hunting spots of the player's map for the map window (TAB) - the permanent monster spawns,
/// joined per monster within a cell of <see cref="CellSize"/> tiles, each with the experience per minute
/// the character can expect there (the estimate scaled by what it really earned where both are known,
/// as the navigator weighs maps). A right click on one sends the hunt there (<see cref="OnlineHunt.PinSpot"/>).
/// </summary>
internal static class MUIdleMapSpots
{
    /// <summary>The side of the cell the spawn areas of one monster are joined in.</summary>
    private const int CellSize = 12;

    /// <summary>
    /// Builds the spots of the player's current map as JSON:
    /// <c>{"map":0,"spots":[{"x":..,"y":..,"monster":"..","level":..,"count":..,"rate":..}],"best":3,"pinned":{"x":..,"y":..}}</c>.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The JSON.</returns>
    public static string Build(Player player)
    {
        if (player.CurrentMap?.Definition is not { } map)
        {
            return "{}";
        }

        var clusters = new Dictionary<(short Monster, int CellX, int CellY), Cluster>();
        foreach (var area in map.MonsterSpawns)
        {
            if (area.MonsterDefinition is not { } monster
                || area.Quantity <= 0
                || monster.ObjectKind != NpcObjectKind.Monster
                || !HuntingMapRating.IsPermanentMonsterSpawn(area))
            {
                continue;
            }

            var x = (area.X1 + area.X2) / 2;
            var y = (area.Y1 + area.Y2) / 2;
            var key = (monster.Number, x / CellSize, y / CellSize);
            if (!clusters.TryGetValue(key, out var cluster))
            {
                cluster = new Cluster(monster);
                clusters[key] = cluster;
            }

            cluster.Add(x, y, area.Quantity, HuntingSpotRating.ExperiencePerMinute(player, map, area));
        }

        var calibration = Calibration(player);
        var spots = clusters.Values
            .Select(c => new
            {
                x = c.X,
                y = c.Y,
                monster = c.Monster.Designation.ValueInNeutralLanguage,
                level = (int)(c.Monster.Attributes.FirstOrDefault(a => a.AttributeDefinition == Stats.Level)?.Value ?? 0),
                count = c.Count,
                rate = Math.Round(c.Rate * calibration),
            })
            .OrderBy(s => s.x)
            .ThenBy(s => s.y)
            .ToList();
        var best = spots.Count > 0 && spots.Max(s => s.rate) > 0 ? spots.IndexOf(spots.MaxBy(s => s.rate)!) : -1;
        var pinned = OnlineHunt.Of(player)?.PinnedSpot;

        return JsonSerializer.Serialize(new
        {
            map = map.Number,
            spots,
            best,
            pinned = pinned is { } p ? new { x = (int)p.X, y = (int)p.Y } : null,
        });
    }

    /// <summary>
    /// Finds a walkable tile at or near the point (a spawn area's centre may lie on a wall).
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="x">The x coordinate.</param>
    /// <param name="y">The y coordinate.</param>
    /// <returns>The tile, or <c>null</c> without one within six tiles.</returns>
    public static Point? WalkableNear(GameMap map, int x, int y)
    {
        for (var radius = 0; radius <= 6; radius++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                for (var dy = -radius; dy <= radius; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius)
                    {
                        continue;
                    }

                    var tx = x + dx;
                    var ty = y + dy;
                    if (tx is >= 0 and <= 255 && ty is >= 0 and <= 255
                        && map.Terrain.WalkMap[tx, ty]
                        && !map.Terrain.SafezoneMap[tx, ty])
                    {
                        return new Point((byte)tx, (byte)ty);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// What the character really earned against the estimate, where both are known (the median, as the
    /// navigator weighs maps); 1 without a measure.
    /// </summary>
    private static double Calibration(Player player)
    {
        var statistics = OnlineHunt.Of(player)?.Statistics ?? new OfflineSessionStatistics();
        var ratios = new List<double>();
        foreach (var map in player.GameContext.Configuration.Maps)
        {
            if (MUIdleMapMemory.Measured(player, statistics, map.Name.ValueInNeutralLanguage) is { } measured
                && HuntingSpotRating.BestSpot(player, map).ExperiencePerMinute is > 0 and var estimated)
            {
                ratios.Add(measured / estimated);
            }
        }

        ratios.Sort();
        return ratios.Count > 0 ? ratios[ratios.Count / 2] : 1.0;
    }

    private sealed class Cluster(MonsterDefinition monster)
    {
        private double _sumX;
        private double _sumY;

        public MonsterDefinition Monster { get; } = monster;

        public int Count { get; private set; }

        public double Rate { get; private set; }

        public int X => this.Count > 0 ? (int)Math.Round(this._sumX / this.Count) : 0;

        public int Y => this.Count > 0 ? (int)Math.Round(this._sumY / this.Count) : 0;

        public void Add(int x, int y, int quantity, double rate)
        {
            this._sumX += x * (double)quantity;
            this._sumY += y * (double)quantity;
            this.Count += quantity;
            this.Rate = Math.Max(this.Rate, rate);
        }
    }
}
