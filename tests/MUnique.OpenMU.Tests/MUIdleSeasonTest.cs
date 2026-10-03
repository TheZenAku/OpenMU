// <copyright file="MUIdleSeasonTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameServer.MUIdle;

/// <summary>
/// Tests the weekly reset cap of <see cref="MUIdleSeason"/>.
/// </summary>
[TestFixture]
public class MUIdleSeasonTest
{
    private static readonly MUIdleConfiguration Season = new()
    {
        SeasonStartUtc = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc),
        SeasonResetsPerWeek = 5,
    };

    /// <summary>The first seven days are week 1 with the base cap; each week adds it again.</summary>
    [Test]
    public void CapRisesEveryWeek()
    {
        Assert.That(MUIdleSeason.Cap(Season, Season.SeasonStartUtc), Is.EqualTo(5));
        Assert.That(MUIdleSeason.Cap(Season, Season.SeasonStartUtc.AddDays(6.99)), Is.EqualTo(5));
        Assert.That(MUIdleSeason.Cap(Season, Season.SeasonStartUtc.AddDays(7)), Is.EqualTo(10));
        Assert.That(MUIdleSeason.Week(Season, Season.SeasonStartUtc.AddDays(20)), Is.EqualTo(3));
    }

    /// <summary>The next raise is the start of the next season week.</summary>
    [Test]
    public void NextRaiseIsTheNextWeek()
    {
        Assert.That(MUIdleSeason.NextRaise(Season, Season.SeasonStartUtc.AddDays(8)), Is.EqualTo(Season.SeasonStartUtc.AddDays(14)));
    }

    /// <summary>Before the start counts as week 1; no per-week value means no season cap.</summary>
    [Test]
    public void EdgesAreSafe()
    {
        Assert.That(MUIdleSeason.Week(Season, Season.SeasonStartUtc.AddDays(-3)), Is.EqualTo(1));
        Assert.That(MUIdleSeason.Cap(new MUIdleConfiguration { SeasonResetsPerWeek = 0 }, DateTime.UtcNow), Is.Null);
    }
}
