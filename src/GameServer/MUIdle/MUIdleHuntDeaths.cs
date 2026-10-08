// <copyright file="MUIdleHuntDeaths.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Collections.Concurrent;
using MUnique.OpenMU.GameLogic;

/// <summary>
/// The recent deaths of a character per map (D20): HUNT on AUTO leaves a map it keeps dying on and does
/// not pick it again for a while - with a client and offline alike. Kept per character rather than per
/// session or player object, so the owner logging in and out (a new offline session each time) does not
/// wipe it. A death soon after arriving on a map counts at once and for longer: the map is far beyond the
/// character (seen: test0Dl warped to Dungeon after each buff, 7 deaths in 3 h, each 1-14 s after the
/// warp, because every new offline session had forgotten the earlier ones).
/// </summary>
internal static class MUIdleHuntDeaths
{
    /// <summary>The deaths within <see cref="Window"/> which make a map deadly.</summary>
    internal const int DeadlyDeaths = 3;

    /// <summary>The time the deaths count.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    /// <summary>A death this soon after arriving on the map is a death on arrival.</summary>
    internal static readonly TimeSpan ArrivalDeath = TimeSpan.FromSeconds(30);

    /// <summary>The time a death on arrival keeps the map deadly.</summary>
    internal static readonly TimeSpan ArrivalWindow = TimeSpan.FromHours(2);

    private static readonly ConcurrentDictionary<Guid, CharacterDeaths> ByCharacter = new();

    /// <summary>
    /// Records that the player was put on a map (a warp, a respawn, entering the world).
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="map">The map number.</param>
    public static void Arrived(Player player, short map)
    {
        if (player.SelectedCharacter?.Id is not { } id)
        {
            return;
        }

        var entry = ByCharacter.GetOrAdd(id, _ => new CharacterDeaths());
        lock (entry)
        {
            entry.ArrivalMap = map;
            entry.ArrivedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Records a death of the player on its map.
    /// </summary>
    /// <param name="player">The player.</param>
    public static void Record(Player player)
    {
        if (player.CurrentMap?.Definition.Number is not { } map || player.SelectedCharacter?.Id is not { } id)
        {
            return;
        }

        var entry = ByCharacter.GetOrAdd(id, _ => new CharacterDeaths());
        var now = DateTime.UtcNow;
        lock (entry)
        {
            entry.Deaths.Add((map, now));
            entry.Deaths.RemoveAll(d => now - d.At > Window);
            if (entry.ArrivalMap == map && now - entry.ArrivedAt <= ArrivalDeath)
            {
                entry.ArrivalDeaths.Add((map, now));
            }

            entry.ArrivalDeaths.RemoveAll(d => now - d.At > ArrivalWindow);
        }
    }

    /// <summary>
    /// Whether the player died too often on the map lately, or died there right after arriving.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="map">The map number.</param>
    /// <returns><c>true</c>, if so.</returns>
    public static bool IsDeadly(Player player, short map)
    {
        if (player.SelectedCharacter?.Id is not { } id || !ByCharacter.TryGetValue(id, out var entry))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        lock (entry)
        {
            return IsDeadly(entry.Deaths, map, now) || DiedOnArrival(entry.ArrivalDeaths, map, now);
        }
    }

    /// <summary>
    /// Whether the player died too often on the map lately, or died there right after arriving.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="mapName">The map name (neutral language), as the navigator names maps.</param>
    /// <returns><c>true</c>, if so.</returns>
    public static bool IsDeadly(Player player, string? mapName)
        => mapName is not null
           && player.GameContext.Configuration.Maps.FirstOrDefault(m => m.Name.ValueInNeutralLanguage == mapName) is { } definition
           && IsDeadly(player, definition.Number);

    /// <summary>
    /// Whether the deaths make the map deadly at the given time.
    /// </summary>
    /// <param name="deaths">The deaths.</param>
    /// <param name="map">The map number.</param>
    /// <param name="now">The time.</param>
    /// <returns><c>true</c>, if so.</returns>
    internal static bool IsDeadly(IEnumerable<(short Map, DateTime At)> deaths, short map, DateTime now)
        => deaths.Count(d => d.Map == map && now - d.At <= Window) >= DeadlyDeaths;

    /// <summary>
    /// Whether a death on arrival still keeps the map deadly at the given time.
    /// </summary>
    /// <param name="arrivalDeaths">The deaths on arrival.</param>
    /// <param name="map">The map number.</param>
    /// <param name="now">The time.</param>
    /// <returns><c>true</c>, if so.</returns>
    internal static bool DiedOnArrival(IEnumerable<(short Map, DateTime At)> arrivalDeaths, short map, DateTime now)
        => arrivalDeaths.Any(d => d.Map == map && now - d.At <= ArrivalWindow);

    private sealed class CharacterDeaths
    {
        public List<(short Map, DateTime At)> Deaths { get; } = [];

        public List<(short Map, DateTime At)> ArrivalDeaths { get; } = [];

        public short ArrivalMap { get; set; } = -1;

        public DateTime ArrivedAt { get; set; }
    }
}
