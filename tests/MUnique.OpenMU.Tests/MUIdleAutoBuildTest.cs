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
