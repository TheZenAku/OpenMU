// <copyright file="CombatObservationsTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic.Offline;

/// <summary>
/// Tests what the hunting engine records of its strikes (<see cref="CombatObservations"/>, HUNT v2).
/// </summary>
[TestFixture]
public class CombatObservationsTest
{
    /// <summary>
    /// Nothing counts before twenty strikes; then the damage before defense and the monsters per strike.
    /// </summary>
    [Test]
    public async ValueTask TwentyStrikesMakeAnObservationAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        for (var i = 0; i < 19; i++)
        {
            CombatObservations.RecordStrike(player, 3, 1_000, null);
        }

        Assert.That(CombatObservations.Observed(player), Is.Null);

        CombatObservations.RecordStrike(player, 3, 1_000, null);
        var observed = CombatObservations.Observed(player);
        Assert.Multiple(() =>
        {
            Assert.That(observed?.RawDamage, Is.EqualTo(1_000).Within(0.01));
            Assert.That(observed?.TargetsPerStrike, Is.EqualTo(3).Within(0.01));
        });
    }

    /// <summary>
    /// One-hit kills say how much at least the strike dealt: the damage is raised to it.
    /// </summary>
    [Test]
    public async ValueTask KillsRaiseTheDamageAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        for (var i = 0; i < 20; i++)
        {
            CombatObservations.RecordStrike(player, 1, null, 2_500);
        }

        Assert.That(CombatObservations.Observed(player)?.RawDamage, Is.EqualTo(2_500).Within(0.01));
    }
}
