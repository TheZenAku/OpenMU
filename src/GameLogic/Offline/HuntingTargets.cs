// <copyright file="HuntingTargets.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Offline;

using System.Runtime.CompilerServices;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// MUIdle (HUNT v2): the monsters of its map which are not worth a hunter's time - the permanent spawns
/// paying far less experience per minute than the best ground of the map (the navigator rates them,
/// <see cref="Bots.HuntingSpotRating"/>). The navigator neither walks after them nor stops its travel for
/// them, and the combat handler leaves them alone unless they attack the character: a level 300 knight
/// in Devias goes to the monsters which pay, instead of swinging at the worms around the town gate.
/// Monsters which are no permanent spawn (bosses, events, invasions) are always worth it.
/// </summary>
internal static class HuntingTargets
{
    private static readonly ConditionalWeakTable<Player, HashSet<short>> Unworthy = new();

    private static readonly ConditionalWeakTable<Player, StrongBox<(Point Center, byte Radius)>> Zones = new();

    /// <summary>
    /// Sets the zone of a spot the player chose, or none: the monsters in it are all worth the time, the
    /// ones outside of it only when they attack (the hunt walks there without stopping for them).
    /// </summary>
    /// <param name="player">The hunting player.</param>
    /// <param name="center">The spot; <c>null</c>: no zone.</param>
    /// <param name="radius">The radius of the zone.</param>
    public static void SetZone(Player player, Point? center, byte radius)
    {
        if (center is { } spot)
        {
            Zones.AddOrUpdate(player, new StrongBox<(Point, byte)>((spot, radius)));
        }
        else
        {
            Zones.Remove(player);
        }
    }

    /// <summary>
    /// Sets the monsters (numbers) which are not worth the player's time, or none.
    /// </summary>
    /// <param name="player">The hunting player.</param>
    /// <param name="monsterNumbers">The numbers of the monster definitions; <c>null</c> or empty: every monster.</param>
    public static void SetUnworthy(Player player, HashSet<short>? monsterNumbers)
    {
        if (monsterNumbers is null || monsterNumbers.Count == 0)
        {
            Unworthy.Remove(player);
            return;
        }

        Unworthy.AddOrUpdate(player, monsterNumbers);
    }

    /// <summary>
    /// Determines whether the monster is worth the player's time: not on its list, or attacking it.
    /// </summary>
    /// <param name="player">The hunting player.</param>
    /// <param name="monster">The monster.</param>
    /// <returns><c>true</c>, if the hunter may go for it.</returns>
    public static bool IsWorth(Player player, Monster monster)
    {
        if (Zones.TryGetValue(player, out var zone))
        {
            return monster.IsInRange(zone.Value.Center, zone.Value.Radius) || monster.IsTargetingPlayer(player);
        }

        return !Unworthy.TryGetValue(player, out var unworthy)
               || !unworthy.Contains(monster.Definition.Number)
               || monster.IsTargetingPlayer(player);
    }
}
