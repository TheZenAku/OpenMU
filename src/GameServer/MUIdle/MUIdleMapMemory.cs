// <copyright file="MUIdleMapMemory.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using MUnique.OpenMU.GameLogic;

/// <summary>
/// HUNT v2 (docs/HUNT_V2.md): the experience per minute a character really earned on each map, over
/// its hunts online and offline - the measure that decides the map once there is one. The estimate
/// (<see cref="GameLogic.Bots.HuntingSpotRating"/>) only says where to look first: it misjudged a
/// level 380 wizard's Devias against Lost Tower by a factor of four (10-09).
/// Kept in memory and, when <c>MUIDLE_MAP_MEMORY_FILE</c> names a file, there too (every update restarts
/// the server, which forgot all of it before); a measure from long ago or from a character 15 levels
/// apart no longer counts.
/// </summary>
internal static class MUIdleMapMemory
{
    /// <summary>The minutes on a map before its experience per minute counts as measured.</summary>
    public const double MinimumSampleMinutes = 5;

    /// <summary>How much more a measured map must have earned to move there.</summary>
    private const double ProvenMargin = 1.25;

    private const int LevelWindow = 15;

    private static readonly TimeSpan Forget = TimeSpan.FromHours(12);

    private static readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, Entry>> Memory = new();

    private static readonly object FileLock = new();

    private static int _loaded;

    private static string? FilePath => Environment.GetEnvironmentVariable("MUIDLE_MAP_MEMORY_FILE") is { Length: > 0 } file ? file : null;

    /// <summary>
    /// Keeps what a finished hunt earned per map.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="statistics">The statistics of the hunt.</param>
    public static void Merge(Player player, OfflineSessionStatistics statistics)
    {
        if (player.SelectedCharacter?.Id is not { } id)
        {
            return;
        }

        EnsureLoaded();
        var maps = Memory.GetOrAdd(id, _ => new ConcurrentDictionary<string, Entry>());
        lock (statistics.MapRates)
        {
            foreach (var rate in statistics.MapRates.Where(r => r.Minutes > 0))
            {
                maps.AddOrUpdate(
                    rate.Map,
                    _ => new Entry(rate.Minutes, rate.Experience, player.Level, DateTime.UtcNow),
                    (_, old) => Relevant(old, player.Level)
                        ? new Entry(old.Minutes + rate.Minutes, old.Experience + rate.Experience, player.Level, DateTime.UtcNow)
                        : new Entry(rate.Minutes, rate.Experience, player.Level, DateTime.UtcNow));
            }
        }

        Save();
    }

    /// <summary>
    /// Gets the experience per minute the character earned on a map (this hunt and the earlier ones),
    /// once it hunted there long enough; <c>null</c> before.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="statistics">The statistics of the running hunt.</param>
    /// <param name="map">The map name.</param>
    /// <returns>The experience per minute, or <c>null</c>.</returns>
    public static double? Measured(Player player, OfflineSessionStatistics statistics, string? map)
    {
        if (map is null)
        {
            return null;
        }

        EnsureLoaded();
        double minutes = 0, experience = 0;
        lock (statistics.MapRates)
        {
            if (statistics.MapRates.FirstOrDefault(r => r.Map == map) is { } live)
            {
                minutes += live.Minutes;
                experience += live.Experience;
            }
        }

        if (player.SelectedCharacter?.Id is { } id
            && Memory.TryGetValue(id, out var maps)
            && maps.TryGetValue(map, out var kept)
            && Relevant(kept, player.Level))
        {
            minutes += kept.Minutes;
            experience += kept.Experience;
        }

        return minutes >= MinimumSampleMinutes ? experience / minutes : null;
    }

    /// <summary>
    /// Gets a map which earned clearly more than the current one (both measured), the best of them.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="statistics">The statistics of the running hunt.</param>
    /// <param name="currentMap">The current map name.</param>
    /// <returns>The map name, or <c>null</c>.</returns>
    public static string? BetterProvenMap(Player player, OfflineSessionStatistics statistics, string? currentMap)
    {
        if (Measured(player, statistics, currentMap) is not { } here)
        {
            return null;
        }

        var names = new HashSet<string>();
        lock (statistics.MapRates)
        {
            names.UnionWith(statistics.MapRates.Select(r => r.Map));
        }

        if (player.SelectedCharacter?.Id is { } id && Memory.TryGetValue(id, out var maps))
        {
            names.UnionWith(maps.Keys);
        }

        string? best = null;
        var bestRate = here * ProvenMargin;
        foreach (var name in names.Where(n => n != currentMap && !statistics.IsDeadly(n)))
        {
            if (Measured(player, statistics, name) is { } rate && rate > bestRate)
            {
                best = name;
                bestRate = rate;
            }
        }

        return best;
    }

    /// <summary>
    /// Reads the file once, on the first use after a start.
    /// </summary>
    private static void EnsureLoaded()
    {
        if (Interlocked.Exchange(ref _loaded, 1) == 1 || FilePath is not { } path || !File.Exists(path))
        {
            return;
        }

        try
        {
            var kept = JsonSerializer.Deserialize<Dictionary<Guid, Dictionary<string, Entry>>>(File.ReadAllText(path));
            foreach (var (id, maps) in kept ?? [])
            {
                Memory.TryAdd(id, new ConcurrentDictionary<string, Entry>(maps));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A broken file is a lost memory, nothing more: the measures come again.
        }
    }

    /// <summary>
    /// Writes the memory (what is not forgotten yet) to the file, through a temporary one.
    /// </summary>
    private static void Save()
    {
        if (FilePath is not { } path)
        {
            return;
        }

        lock (FileLock)
        {
            try
            {
                var now = DateTime.UtcNow;
                var snapshot = Memory.ToDictionary(
                    character => character.Key,
                    character => character.Value.Where(map => now - map.Value.AtUtc <= Forget).ToDictionary(map => map.Key, map => map.Value));
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
                File.Move(temporary, path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Kept in memory still; the next hunt's end tries again.
            }
        }
    }

    private static bool Relevant(Entry entry, int level)
        => Math.Abs(entry.Level - level) <= LevelWindow && DateTime.UtcNow - entry.AtUtc <= Forget;

    private sealed record Entry(double Minutes, double Experience, int Level, DateTime AtUtc);
}
