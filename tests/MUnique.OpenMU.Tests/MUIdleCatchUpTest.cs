// <copyright file="MUIdleCatchUpTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameServer.MUIdle;

/// <summary>
/// Tests the catch-up and newcomer experience bonus of <see cref="MUIdleCatchUp"/>.
/// </summary>
[TestFixture]
public class MUIdleCatchUpTest
{
    private static readonly MUIdleConfiguration Season = new()
    {
        SeasonStartUtc = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc),
        SeasonResetsPerWeek = 5,
        CatchUpPerReset = 0.1,
        NewcomerDays = 7,
        NewcomerBonus = 0.5,
        MaximumBonus = 1.0,
    };

    private static readonly DateTime Veteran = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>In week 1 nobody is behind: last week's cap is 0.</summary>
    [Test]
    public void NoCatchUpInTheFirstWeek()
    {
        Assert.That(MUIdleCatchUp.Bonus(0, Veteran, Season, Season.SeasonStartUtc.AddDays(3)).Total, Is.EqualTo(0));
    }

    /// <summary>In week 3 last week's cap is 10: 3 resets is 7 behind, +70 %.</summary>
    [Test]
    public void CatchUpPerMissingReset()
    {
        var bonus = MUIdleCatchUp.Bonus(3, Veteran, Season, Season.SeasonStartUtc.AddDays(15));

        Assert.That(bonus.CatchUp, Is.EqualTo(0.7).Within(1e-9));
        Assert.That(bonus.Total, Is.EqualTo(0.7).Within(1e-9));
    }

    /// <summary>A character created this week is a newcomer; with catch-up the total stops at the maximum.</summary>
    [Test]
    public void NewcomerAddsAndTheTotalIsCapped()
    {
        var now = Season.SeasonStartUtc.AddDays(15);

        Assert.That(MUIdleCatchUp.Bonus(10, now.AddDays(-2), Season, now).Total, Is.EqualTo(0.5).Within(1e-9));
        Assert.That(MUIdleCatchUp.Bonus(0, now.AddDays(-2), Season, now).Total, Is.EqualTo(1.0).Within(1e-9));
        Assert.That(MUIdleCatchUp.Bonus(0, now.AddDays(-8), Season, now).Newcomer, Is.EqualTo(0));
    }
}
