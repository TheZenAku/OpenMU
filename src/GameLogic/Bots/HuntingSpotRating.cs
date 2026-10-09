// <copyright file="HuntingSpotRating.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Offline;

/// <summary>
/// MUIdle (HUNT v2, docs/HUNT_V2.md): the experience per minute a character can expect from a hunting
/// spot - a permanent monster spawn area of a map. Not the level of the strongest monster it may fight
/// (<see cref="HuntingMapRating"/>), which favours monsters that take long to kill and maps with few of
/// them: experience per kill (the game's own formula, the map's multiplier) times kills per minute (the
/// hits a kill takes, the walk between the monsters of the area, and how fast they come back).
/// </summary>
public static class HuntingSpotRating
{
    /// <summary>The hunting engine strikes about once a second: a 500 ms tick and one tick of rest after a skill.</summary>
    internal const double SecondsPerStrike = 1.0;

    /// <summary>A walking character covers 3 to 4 tiles a second (movement speed 12 to 15).</summary>
    internal const double TilesPerSecond = 3.0;

    /// <summary>The farthest walk counted between two monsters: a sparse area is still hunted in a corner of it.</summary>
    private const double MaximumSpacingTiles = 12;

    /// <summary>
    /// Gets the base experience of a kill, as the game calculates it (<see cref="AttackableExtensions.CalculateBaseExperience"/>):
    /// before the server's and the character's rates, which are the same for every spot.
    /// </summary>
    /// <param name="monsterLevel">The level of the monster.</param>
    /// <param name="killerLevel">The total level of the character.</param>
    /// <returns>The experience.</returns>
    public static double BaseExperience(double monsterLevel, double killerLevel)
    {
        var experience = (monsterLevel + 25) * monsterLevel / 3.0;
        if (killerLevel > monsterLevel + 10)
        {
            experience *= (monsterLevel + 10) / killerLevel;
        }

        if (monsterLevel >= 65)
        {
            experience += (monsterLevel - 64) * (monsterLevel / 4);
        }

        return Math.Max(experience, 0) * 1.25;
    }

    /// <summary>
    /// Gets the experience per minute the character can expect from the spawn area; 0 when it cannot
    /// safely fight its monster, does not get through its defense, or earns nothing from it.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="map">The map of the area.</param>
    /// <param name="area">The spawn area.</param>
    /// <returns>The expected experience per minute.</returns>
    public static double ExperiencePerMinute(Player player, GameMapDefinition map, MonsterSpawnArea area)
    {
        if (area.MonsterDefinition is not { } monster || area.Quantity <= 0 || !HuntingMapRating.IsPermanentMonsterSpawn(area))
        {
            return 0;
        }

        var (hits, safe, level) = CombatHandler.EstimateKill(player, monster);
        if (!safe || float.IsInfinity(hits) || level <= 0)
        {
            return 0;
        }

        // A mastered character earns master experience, and only from monsters above the floor.
        var floor = HuntingMapRating.MasterExperienceFloor(player);
        if (floor > 0 && level < floor)
        {
            return 0;
        }

        var killerLevel = player.Attributes?[Stats.TotalLevel] ?? player.Level;
        var experience = BaseExperience(level, killerLevel) * (map.ExpMultiplier > 0 ? map.ExpMultiplier : 1);

        var width = Math.Abs(area.X2 - area.X1) + 1;
        var height = Math.Abs(area.Y2 - area.Y1) + 1;
        var spacing = Math.Min(MaximumSpacingTiles, Math.Sqrt(width * height / (double)area.Quantity));
        var killSeconds = Math.Ceiling(hits) * SecondsPerStrike;
        var walkSeconds = spacing / TilesPerSecond;
        var killsPerMinute = 60.0 / (killSeconds + walkSeconds);

        // A few monsters are killed faster than they come back.
        var respawnSeconds = monster.RespawnDelay.TotalSeconds;
        if (respawnSeconds > 0)
        {
            killsPerMinute = Math.Min(killsPerMinute, area.Quantity * 60.0 / (respawnSeconds + killSeconds));
        }

        return experience * killsPerMinute;
    }

    /// <summary>
    /// Gets the best spot of a map for the character, with its experience per minute (0 without one).
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="map">The map.</param>
    /// <returns>The spawn area and its experience per minute.</returns>
    public static (MonsterSpawnArea? Area, double ExperiencePerMinute) BestSpot(Player player, GameMapDefinition map)
    {
        MonsterSpawnArea? best = null;
        var bestRate = 0.0;
        foreach (var area in map.MonsterSpawns)
        {
            var rate = ExperiencePerMinute(player, map, area);
            if (rate > bestRate)
            {
                best = area;
                bestRate = rate;
            }
        }

        return (best, bestRate);
    }
}
