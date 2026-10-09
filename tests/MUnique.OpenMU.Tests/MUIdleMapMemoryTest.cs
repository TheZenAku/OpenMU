// <copyright file="MUIdleMapMemoryTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameServer.MUIdle;

/// <summary>
/// Tests the measured experience per map (<see cref="MUIdleMapMemory"/>, HUNT v2).
/// </summary>
[TestFixture]
public class MUIdleMapMemoryTest
{
    private static readonly DateTime Start = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A map counts as measured after five minutes there, not before.
    /// </summary>
    [Test]
    public async ValueTask FiveMinutesMakeAMeasureAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.SelectedCharacter!.Id = Guid.NewGuid();
        var statistics = new OfflineSessionStatistics();
        var now = Hunt(statistics, "Lost Tower", 4, 1_000, Start);
        Assert.That(MUIdleMapMemory.Measured(player, statistics, "Lost Tower"), Is.Null);

        Hunt(statistics, "Lost Tower", 1, 1_000, now);
        Assert.That(MUIdleMapMemory.Measured(player, statistics, "Lost Tower"), Is.EqualTo(1_000).Within(0.01));
    }

    /// <summary>
    /// A finished hunt is kept: the next hunt starts with its measure.
    /// </summary>
    [Test]
    public async ValueTask FinishedHuntsAreKeptAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.SelectedCharacter!.Id = Guid.NewGuid();
        var first = new OfflineSessionStatistics();
        Hunt(first, "Devias", 6, 2_000, Start);
        MUIdleMapMemory.Merge(player, first);

        var next = new OfflineSessionStatistics();
        Assert.That(MUIdleMapMemory.Measured(player, next, "Devias"), Is.EqualTo(2_000).Within(0.01));
    }

    /// <summary>
    /// The hunt moves back to a map only when it earned clearly more there (25 %), and never to an
    /// unmeasured one this way.
    /// </summary>
    [Test]
    public async ValueTask AProvenMapMustEarnClearlyMoreAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.SelectedCharacter!.Id = Guid.NewGuid();
        var statistics = new OfflineSessionStatistics();
        var now = Hunt(statistics, "Lost Tower", 6, 5_000, Start);
        now = Hunt(statistics, "Devias", 6, 2_000, now);
        now = Hunt(statistics, "Aida", 6, 200, now);
        Hunt(statistics, "Kanturu", 2, 1_000, now);

        // Lost Tower 5 000/min, Devias 2 000/min, Aida 200/min; Kanturu not measured.
        Assert.Multiple(() =>
        {
            Assert.That(MUIdleMapMemory.BetterProvenMap(player, statistics, "Devias"), Is.EqualTo("Lost Tower"));
            Assert.That(MUIdleMapMemory.BetterProvenMap(player, statistics, "Lost Tower"), Is.Null);
            Assert.That(MUIdleMapMemory.BetterProvenMap(player, statistics, "Kanturu"), Is.Null, "the current map is not measured yet");
        });
    }

    /// <summary>Hunts on a map for some minutes, one record a minute (as the hunts record every second).</summary>
    private static DateTime Hunt(OfflineSessionStatistics statistics, string map, int minutes, long experiencePerMinute, DateTime from)
    {
        statistics.Record(map, at: from);
        for (var minute = 1; minute <= minutes; minute++)
        {
            statistics.Record(map, experience: experiencePerMinute, at: from.AddMinutes(minute));
        }

        return from.AddMinutes(minutes);
    }
}
