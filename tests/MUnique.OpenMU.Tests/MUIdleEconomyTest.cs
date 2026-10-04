// <copyright file="MUIdleEconomyTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameServer.MUIdle;

/// <summary>
/// Tests the HUNT reserve (<see cref="MUIdleEconomy"/>, D20).
/// </summary>
[TestFixture]
public class MUIdleEconomyTest
{
    /// <summary>
    /// The reserve is whole MU Helper payments for the reserve minutes, at least one; none without a fee.
    /// </summary>
    [Test]
    public void ReserveIsWholeHelperPayments()
    {
        var interval = TimeSpan.FromMinutes(5);
        Assert.Multiple(() =>
        {
            Assert.That(MUIdleEconomy.Reserve(7_600, interval, 30), Is.EqualTo(45_600)); // level 380, first stage
            Assert.That(MUIdleEconomy.Reserve(6_000, interval, 1), Is.EqualTo(6_000));
            Assert.That(MUIdleEconomy.Reserve(6_000, interval, 31), Is.EqualTo(42_000));
            Assert.That(MUIdleEconomy.Reserve(0, interval, 30), Is.EqualTo(0));
            Assert.That(MUIdleEconomy.Reserve(7_600, interval, 0), Is.EqualTo(0));
        });
    }
}
