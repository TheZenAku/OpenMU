// <copyright file="MUIdleHuntMapsTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameServer.MUIdle;

/// <summary>
/// Tests how HUNT judges a hunting map (<see cref="MUIdleHuntMaps"/>).
/// </summary>
[TestFixture]
public class MUIdleHuntMapsTest
{
    private const short Icarus = 10;

    /// <summary>
    /// A map that needs flying (Icarus: wings, a Dark Horse, Fenrir) is refused without it, even with
    /// the level and the fare, and open with it.
    /// </summary>
    [Test]
    public async Task AMapThatNeedsWingsIsRefusedWithoutThemAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var map = new Mock<GameMapDefinition>();
        map.SetupAllProperties();
        map.Setup(m => m.MapRequirements).Returns(new List<AttributeRequirement> { new() { Attribute = Stats.CanFly, MinimumValue = 1 } });
        map.Object.Number = Icarus;
        var configuration = player.GameContext.Configuration;
        configuration.Maps.Add(map.Object);
        Mock.Get(configuration).Setup(c => c.WarpList).Returns(new List<WarpInfo>
        {
            new() { Costs = 10_000, LevelRequirement = 170, Gate = new ExitGate { Map = map.Object } },
        });
        player.Attributes![Stats.Level] = 380;
        player.Money = 100_000;

        var withoutWings = MUIdleHuntMaps.Evaluate(player, Icarus);
        player.Attributes.AddElement(new SimpleElement(1, AggregateType.AddRaw), Stats.CanFly);
        var withWings = MUIdleHuntMaps.Evaluate(player, Icarus);

        Assert.Multiple(() =>
        {
            Assert.That(withoutWings?.Refusal, Is.EqualTo(HuntMapRefusal.Requirement));
            Assert.That(withoutWings?.Warp, Is.Null);
            Assert.That(withWings?.Refusal, Is.Null);
            Assert.That(withWings?.Warp, Is.Not.Null);
        });
    }
}
