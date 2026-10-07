// <copyright file="OfflineSessionStatisticsTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameServer.MUIdle;

/// <summary>
/// Tests the per-map rates of <see cref="OfflineSessionStatistics"/> and the proven-map rule the
/// offline navigator follows (<see cref="OfflineSessionStatistics.BetterProvenMap"/>).
/// </summary>
[TestFixture]
public class OfflineSessionStatisticsTest
{
    private static readonly DateTime Start = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Time counts towards the map the character was on, earnings towards the map named.</summary>
    [Test]
    public void TimeAndEarningsGoToTheirMaps()
    {
        var statistics = new OfflineSessionStatistics();
        statistics.Record("Devias", at: Start);
        statistics.Record("Devias", experience: 500, kills: 1, at: Start.AddMinutes(1));
        statistics.Record("Atlans", at: Start.AddMinutes(2));
        statistics.Record("Atlans", zen: 70, at: Start.AddMinutes(3));

        var devias = statistics.MapRates.Single(r => r.Map == "Devias");
        var atlans = statistics.MapRates.Single(r => r.Map == "Atlans");
        Assert.That(devias.Minutes, Is.EqualTo(2).Within(0.001));
        Assert.That(devias.Experience, Is.EqualTo(500));
        Assert.That(devias.Kills, Is.EqualTo(1));
        Assert.That(atlans.Minutes, Is.EqualTo(1).Within(0.001));
        Assert.That(atlans.Zen, Is.EqualTo(70));
    }

    /// <summary>A long silence (a pause, a restart) does not count as hunting time.</summary>
    [Test]
    public void LongGapsAreCapped()
    {
        var statistics = new OfflineSessionStatistics();
        statistics.Record("Devias", at: Start);
        statistics.Record("Devias", at: Start.AddHours(3));

        Assert.That(statistics.MapRates.Single().Minutes, Is.EqualTo(2).Within(0.001));
    }

    /// <summary>A map that earned 25 % more per minute, both measured long enough, is the better one.</summary>
    [Test]
    public void ClearlyBetterMeasuredMapIsNamed()
    {
        var statistics = Measured(("Devias", 12, 12_000), ("Atlans", 12, 18_000));

        Assert.That(statistics.BetterProvenMap("Devias"), Is.EqualTo("Atlans"));
        Assert.That(statistics.BetterProvenMap("Atlans"), Is.Null);
    }

    /// <summary>Within the margin the hunt stays: no bouncing between two similar maps.</summary>
    [Test]
    public void SimilarMapsKeepTheCurrentOne()
    {
        var statistics = Measured(("Devias", 12, 12_000), ("Atlans", 12, 14_000));

        Assert.That(statistics.BetterProvenMap("Devias"), Is.Null);
    }

    /// <summary>Too short a sample proves nothing, here or there.</summary>
    [Test]
    public void ShortSamplesProveNothing()
    {
        Assert.That(Measured(("Devias", 3, 1_000), ("Atlans", 12, 90_000)).BetterProvenMap("Devias"), Is.Null);
        Assert.That(Measured(("Devias", 12, 1_000), ("Atlans", 3, 90_000)).BetterProvenMap("Devias"), Is.Null);
    }

    /// <summary>Dying three times in an hour makes a map deadly; it is then no proven better map (D20).</summary>
    [Test]
    public void FrequentDeathsMakeAMapDeadly()
    {
        var statistics = Measured(("Devias", 12, 12_000), ("Lost Tower", 60, 120_000));
        Assert.That(statistics.BetterProvenMap("Devias"), Is.EqualTo("Lost Tower"));

        statistics.Record("Lost Tower", deaths: 3, at: Start.AddMinutes(72));

        Assert.Multiple(() =>
        {
            Assert.That(statistics.IsDeadly("Lost Tower"), Is.True);
            Assert.That(statistics.IsDeadly("Devias"), Is.False);
            Assert.That(statistics.BetterProvenMap("Devias"), Is.Null);
        });
    }

    /// <summary>A map earning less zen per minute than the running costs is losing; a short sample is not judged.</summary>
    [Test]
    public void LessZenThanTheCostsIsLosing()
    {
        var statistics = new OfflineSessionStatistics();
        statistics.Record("Lost Tower", at: Start);
        for (var minute = 1; minute <= 12; minute++)
        {
            statistics.Record("Lost Tower", zen: 1_000, at: Start.AddMinutes(minute));
        }

        statistics.Record("Atlans", at: Start.AddMinutes(13));

        Assert.Multiple(() =>
        {
            Assert.That(statistics.IsLosing("Lost Tower", 1_520), Is.True); // 1 000 zen/min against level 380 fees
            Assert.That(statistics.IsLosing("Lost Tower", 500), Is.False);
            Assert.That(statistics.IsLosing("Lost Tower", 0), Is.False);
            Assert.That(statistics.IsLosing("Atlans", 1_520), Is.False);
        });
    }

    /// <summary>
    /// A map whose repairs cost more than it earns (test300Dl in Devias: 22 K earned, 1.58 M spent on
    /// 4 repairs in 10 minutes) drains a character whose money would fall below the reserve within an
    /// hour; a rich one, or a map that pays, is not drained.
    /// </summary>
    [Test]
    public void RepairsAboveTheEarningsMakeAMapUnsustainable()
    {
        var statistics = new OfflineSessionStatistics();
        statistics.Record("Devias", at: Start);
        for (var minute = 1; minute <= 11; minute++)
        {
            statistics.Record("Devias", zen: 2_000, at: Start.AddMinutes(minute));
        }

        statistics.Record("Devias", spent: 1_580_000, at: Start.AddMinutes(11));
        statistics.Record("Lost Tower", at: Start.AddMinutes(12));
        for (var minute = 13; minute <= 24; minute++)
        {
            statistics.Record("Lost Tower", zen: 20_000, at: Start.AddMinutes(minute));
        }

        statistics.Record("Lost Tower", spent: 100_000, at: Start.AddMinutes(24));
        statistics.Record("Atlans", at: Start.AddMinutes(25));

        Assert.Multiple(() =>
        {
            Assert.That(statistics.IsUnsustainable("Devias", 211_443, 45_600), Is.True);
            Assert.That(statistics.IsUnsustainable("Devias", 100_000_000, 45_600), Is.False, "rich enough for an hour");
            Assert.That(statistics.IsUnsustainable("Lost Tower", 211_443, 45_600), Is.False, "pays for its repairs");
            Assert.That(statistics.IsUnsustainable("Atlans", 0, 45_600), Is.False, "not measured");
        });
    }

    /// <summary>The rates survive the JSON round trip of the periodic saves.</summary>
    [Test]
    public void RatesSurviveSaving()
    {
        var copy = OfflineSessionStatistics.Parse(Measured(("Devias", 12, 12_000)).ToJson())!;

        Assert.That(copy.MapRates.Single(), Has.Property(nameof(MapRate.Experience)).EqualTo(12_000));
    }

    /// <summary>Builds statistics where each map was hunted for the given minutes and earned the given experience.</summary>
    private static OfflineSessionStatistics Measured(params (string Map, int Minutes, long Experience)[] maps)
    {
        var statistics = new OfflineSessionStatistics();
        var at = Start;
        foreach (var (map, minutes, experience) in maps)
        {
            statistics.Record(map, at: at);
            for (var minute = 1; minute <= minutes; minute++)
            {
                at = at.AddMinutes(1);
                statistics.Record(map, experience: experience / minutes, at: at);
            }
        }

        statistics.Record(null, at: at);
        return statistics;
    }
}
