// <copyright file="MUIdleContractsTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameServer.MUIdle;

/// <summary>
/// Tests the daily contracts of <see cref="MUIdleContracts"/>: made from level and resets, no randomness.
/// </summary>
[TestFixture]
public class MUIdleContractsTest
{
    /// <summary>Three contracts: kills, kills on the given map, hunting minutes.</summary>
    [Test]
    public void ThreeContractsForADay()
    {
        var contracts = MUIdleContracts.Generate(100, 0, (7, "Atlans"), "2026-10-03");

        Assert.That(contracts.Day, Is.EqualTo("2026-10-03"));
        Assert.That(contracts.Contracts.Select(c => c.Kind), Is.EqualTo(new[] { "kills", "mapKills", "huntMinutes" }));
        Assert.That(contracts.Contracts[1].MapName, Is.EqualTo("Atlans"));
        Assert.That(contracts.Contracts[0].Target, Is.EqualTo(250));
    }

    /// <summary>The same character on the same day gets the same contracts.</summary>
    [Test]
    public void SameInputsSameContracts()
    {
        var first = MUIdleContracts.Generate(250, 2, (4, "Lost Tower"), "2026-10-03");
        var second = MUIdleContracts.Generate(250, 2, (4, "Lost Tower"), "2026-10-03");

        Assert.That(second.Contracts.Select(c => (c.Kind, c.Target, c.RewardZen)), Is.EqualTo(first.Contracts.Select(c => (c.Kind, c.Target, c.RewardZen))));
    }

    /// <summary>Rewards grow with level and resets; without a huntable map there is no map contract.</summary>
    [Test]
    public void RewardsScaleAndMapIsOptional()
    {
        var low = MUIdleContracts.Generate(10, 0, null, "2026-10-03");
        var high = MUIdleContracts.Generate(300, 3, null, "2026-10-03");

        Assert.That(low.Contracts, Has.Count.EqualTo(2));
        Assert.That(high.Contracts[0].RewardZen, Is.GreaterThan(low.Contracts[0].RewardZen));
    }

    /// <summary>The day key is the UTC date.</summary>
    [Test]
    public void DayIsUtc()
    {
        Assert.That(MUIdleContracts.Day(new DateTime(2026, 10, 3, 23, 30, 0, DateTimeKind.Utc)), Is.EqualTo("2026-10-03"));
        Assert.That(MUIdleContracts.Day(new DateTimeOffset(2026, 10, 3, 22, 0, 0, TimeSpan.FromHours(-3)).UtcDateTime), Is.EqualTo("2026-10-04"));
    }
}
