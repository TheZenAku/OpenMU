// <copyright file="HuntingMapRating.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Offline;

/// <summary>
/// How a map rates as hunting ground for a character: the level of the strongest monster which always
/// stands there and which the character can safely fight (see <see cref="CombatHandler.IsSafeTarget(Player, MonsterDefinition)"/>).
/// The bot navigator chooses its maps by it, and so does anything else that picks hunting grounds for a
/// character (MUIdle's online AUTO hunt) - one rule, so they judge a map alike.
/// </summary>
public static class HuntingMapRating
{
    /// <summary>
    /// How many monster levels stronger the safe monsters of another map must be before it is worth a warp.
    /// </summary>
    public const int ImprovementMargin = 8;

    /// <summary>
    /// Gets the level of the strongest permanent monster on the map which the player can safely fight;
    /// 0 if the map has none. Monsters below <paramref name="minimumLevel"/> are not counted.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="map">The map to judge.</param>
    /// <param name="minimumLevel">The lowest monster level worth counting.</param>
    /// <returns>The level of the strongest safe monster.</returns>
    public static int BestSafeLevel(Player player, GameMapDefinition map, int minimumLevel = 0)
    {
        var best = 0;
        foreach (var area in map.MonsterSpawns)
        {
            if (!IsPermanentMonsterSpawn(area))
            {
                continue;
            }

            var level = GetMonsterLevel(area.MonsterDefinition!);
            if (level >= minimumLevel && level > best && CombatHandler.IsSafeTarget(player, area.MonsterDefinition!))
            {
                best = level;
            }
        }

        return best;
    }

    /// <summary>
    /// Gets the level of the strongest permanent monster of the band on the map which the player can
    /// safely fight; 0 if the map has none.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="map">The map to judge.</param>
    /// <param name="minimumLevel">The lowest monster level of the band.</param>
    /// <param name="maximumLevel">The highest monster level of the band.</param>
    /// <returns>The level of the strongest safe monster of the band.</returns>
    public static int BestSafeLevelInBand(Player player, GameMapDefinition map, int minimumLevel, int maximumLevel)
    {
        var best = 0;
        foreach (var area in map.MonsterSpawns)
        {
            if (!IsPermanentMonsterSpawn(area))
            {
                continue;
            }

            var level = GetMonsterLevel(area.MonsterDefinition!);
            if (level >= minimumLevel && level <= maximumLevel && level > best && CombatHandler.IsSafeTarget(player, area.MonsterDefinition!))
            {
                best = level;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether the spawn area holds monsters which stand on the map at all times. Event spawns (golden
    /// monsters, invasion and wave areas) are configured on the regular maps too, but only exist while
    /// their event runs.
    /// </summary>
    /// <param name="area">The spawn area.</param>
    /// <returns><c>true</c> for a permanent monster spawn.</returns>
    public static bool IsPermanentMonsterSpawn(MonsterSpawnArea area)
        => area is { Quantity: > 0, SpawnTrigger: SpawnTrigger.Automatic, MonsterDefinition.ObjectKind: NpcObjectKind.Monster };

    /// <summary>
    /// Gets the level of a monster.
    /// </summary>
    /// <param name="definition">The monster definition.</param>
    /// <returns>The level.</returns>
    public static int GetMonsterLevel(MonsterDefinition definition)
        => (int)(definition.Attributes.FirstOrDefault(a => a.AttributeDefinition == Stats.Level)?.Value ?? 0f);

    /// <summary>
    /// Gets the lowest monster level which still pays a master class character at the maximum level
    /// anything (master experience); 0 for every other character, which hunts by safety alone.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The monster level floor.</returns>
    public static int MasterExperienceFloor(Player player)
    {
        var configuration = player.GameContext.Configuration;
        if (player.SelectedCharacter?.CharacterClass?.IsMasterClass != true
            || (player.Attributes?[Stats.Level] ?? 0) < configuration.MaximumLevel)
        {
            return 0;
        }

        return configuration.MinimumMonsterLevelForMasterExperience;
    }
}
