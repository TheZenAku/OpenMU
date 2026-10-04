// <copyright file="MUIdleHuntDeathsTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameServer.MUIdle;

/// <summary>
/// Tests when an online player's map counts as deadly (<see cref="MUIdleHuntDeaths"/>, D20).
/// </summary>
[TestFixture]
public class MUIdleHuntDeathsTest
{
    private static readonly DateTime Now = new(2026, 10, 3, 22, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Three deaths on one map within the window make it deadly; older ones and other maps do not count.
    /// </summary>
    [Test]
    public void ThreeRecentDeathsOnTheMapMakeItDeadly()
    {
        var deaths = new List<(short Map, DateTime At)>
        {
            (4, Now.AddMinutes(-25)),
            (4, Now.AddMinutes(-10)),
            (4, Now.AddMinutes(-1)),
            (1, Now.AddMinutes(-2)),
            (1, Now.AddMinutes(-40)),
            (1, Now.AddMinutes(-45)),
        };

        Assert.Multiple(() =>
        {
            Assert.That(MUIdleHuntDeaths.IsDeadly(deaths, 4, Now), Is.True);
            Assert.That(MUIdleHuntDeaths.IsDeadly(deaths, 1, Now), Is.False);
            Assert.That(MUIdleHuntDeaths.IsDeadly(deaths, 4, Now.AddMinutes(10)), Is.False);
        });
    }
}
