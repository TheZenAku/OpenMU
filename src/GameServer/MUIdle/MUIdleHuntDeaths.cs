// <copyright file="MUIdleHuntDeaths.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Runtime.CompilerServices;
using MUnique.OpenMU.GameLogic;

/// <summary>
/// The recent deaths of a player with a client per map (D20): HUNT on AUTO leaves a map it keeps dying
/// on and does not pick it again for a while - the same rule as the offline hunter's statistics.
/// </summary>
internal static class MUIdleHuntDeaths
{
    /// <summary>The deaths within <see cref="Window"/> which make a map deadly.</summary>
    internal const int DeadlyDeaths = 3;

    /// <summary>The time the deaths count.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    private static readonly ConditionalWeakTable<Player, List<(short Map, DateTime At)>> Deaths = new();

    /// <summary>
    /// Records a death of the player on its map.
    /// </summary>
    /// <param name="player">The player.</param>
    public static void Record(Player player)
    {
        if (player.CurrentMap?.Definition.Number is not { } map)
        {
            return;
        }

        var list = Deaths.GetValue(player, _ => []);
        lock (list)
        {
            list.Add((map, DateTime.UtcNow));
            list.RemoveAll(d => DateTime.UtcNow - d.At > Window);
        }
    }

    /// <summary>
    /// Whether the player died too often on the map lately.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="map">The map number.</param>
    /// <returns><c>true</c>, if so.</returns>
    public static bool IsDeadly(Player player, short map)
    {
        if (!Deaths.TryGetValue(player, out var list))
        {
            return false;
        }

        lock (list)
        {
            return IsDeadly(list, map, DateTime.UtcNow);
        }
    }

    /// <summary>
    /// Whether the deaths make the map deadly at the given time.
    /// </summary>
    /// <param name="deaths">The deaths.</param>
    /// <param name="map">The map number.</param>
    /// <param name="now">The time.</param>
    /// <returns><c>true</c>, if so.</returns>
    internal static bool IsDeadly(IEnumerable<(short Map, DateTime At)> deaths, short map, DateTime now)
        => deaths.Count(d => d.Map == map && now - d.At <= Window) >= DeadlyDeaths;
}
