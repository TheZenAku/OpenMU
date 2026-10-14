// <copyright file="MUIdleAutoBuildTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameServer.MUIdle;

/// <summary>
/// Tests the build presets of <see cref="MUIdleAutoBuild"/>.
/// </summary>
[TestFixture]
public class MUIdleAutoBuildTest
{
    /// <summary>
    /// All free points go out at once, by the class weights (Dark Wizard 5:2:2, the remainder to the
    /// main stat): each stat in one step, not one point at a time (10-14: 875 points stalled the browser).
    /// </summary>
    [Test]
    public async ValueTask SpendsAllPointsByTheWeightsAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var character = player.SelectedCharacter!;
        character.LevelUpPoints = 100;
        var energy = player.Attributes![Stats.BaseEnergy];
        var agility = player.Attributes[Stats.BaseAgility];
        var vitality = player.Attributes[Stats.BaseVitality];

        await MUIdleAutoBuild.DistributeAsync(player, string.Empty).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(character.LevelUpPoints, Is.Zero);
            Assert.That(player.Attributes[Stats.BaseEnergy], Is.EqualTo(energy + 56));
            Assert.That(player.Attributes[Stats.BaseAgility], Is.EqualTo(agility + 22));
            Assert.That(player.Attributes[Stats.BaseVitality], Is.EqualTo(vitality + 22));
        });
    }

    /// <summary>
    /// No stat goes past what the protocol carries (65 535, a 16-bit field): the rest of the points stay.
    /// </summary>
    [Test]
    public async ValueTask StatsStopAtTheProtocolMaximumAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var character = player.SelectedCharacter!;
        character.LevelUpPoints = 1_000_000;

        await MUIdleAutoBuild.DistributeAsync(player, string.Empty).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(player.Attributes![Stats.BaseEnergy], Is.EqualTo(ushort.MaxValue));
            Assert.That(player.Attributes[Stats.BaseAgility], Is.EqualTo(ushort.MaxValue));
            Assert.That(player.Attributes[Stats.BaseVitality], Is.EqualTo(ushort.MaxValue));
            Assert.That(character.LevelUpPoints, Is.GreaterThan(0));
        });
    }

    /// <summary>Each preset changes the main stat of its class only.</summary>
    [Test]
    public void PresetsChangeTheMainStat()
    {
        Assert.That(MUIdleAutoBuild.GetWeights(8, "support")[0].Stat, Is.EqualTo(Stats.BaseEnergy));
        Assert.That(MUIdleAutoBuild.GetWeights(12, "energy")[0].Stat, Is.EqualTo(Stats.BaseEnergy));
        Assert.That(MUIdleAutoBuild.GetWeights(16, "raven")[0].Stat, Is.EqualTo(Stats.BaseLeadership));
    }

    /// <summary>No preset, an unknown one, or one of another class: the class default.</summary>
    [Test]
    public void OtherwiseTheClassDefault()
    {
        Assert.That(MUIdleAutoBuild.GetWeights(8, null)[0].Stat, Is.EqualTo(Stats.BaseAgility));
        Assert.That(MUIdleAutoBuild.GetWeights(4, "raven")[0].Stat, Is.EqualTo(Stats.BaseStrength));
        Assert.That(MUIdleAutoBuild.GetWeights(12, string.Empty)[0].Stat, Is.EqualTo(Stats.BaseStrength));
    }
}
