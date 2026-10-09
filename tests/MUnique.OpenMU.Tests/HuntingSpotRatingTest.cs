// <copyright file="HuntingSpotRatingTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic.Bots;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Tests the experience per minute of hunting spots (<see cref="HuntingSpotRating"/>, HUNT v2).
/// </summary>
[TestFixture]
public class HuntingSpotRatingTest
{
    /// <summary>
    /// The base experience follows the game's formula: (level + 25) x level / 3, less for a much stronger
    /// killer, more from level 65 on, times 1.25.
    /// </summary>
    [Test]
    public void BaseExperienceFollowsTheGame()
    {
        Assert.Multiple(() =>
        {
            // 30 x 5 / 3 = 50, x 1.25.
            Assert.That(HuntingSpotRating.BaseExperience(5, 10), Is.EqualTo(62.5).Within(0.001));

            // 105 x 80 / 3 = 2800, x 90 / 100 for the killer above level 90, + 16 x 20, x 1.25.
            Assert.That(HuntingSpotRating.BaseExperience(80, 100), Is.EqualTo(3550).Within(0.001));

            // The killer within ten levels: no reduction.
            Assert.That(HuntingSpotRating.BaseExperience(80, 85), Is.EqualTo((2800 + 320) * 1.25).Within(0.001));
        });
    }

    /// <summary>
    /// Stronger monsters pay more per kill, also for a strong killer.
    /// </summary>
    [Test]
    public void StrongerMonstersPayMorePerKill()
    {
        Assert.That(HuntingSpotRating.BaseExperience(90, 380), Is.GreaterThan(HuntingSpotRating.BaseExperience(60, 380)));
    }

    /// <summary>
    /// An area without monsters, or with an event's monsters only, pays nothing.
    /// </summary>
    [Test]
    public async ValueTask EmptyAreasPayNothingAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var map = new BasicModel.GameMapDefinition { ExpMultiplier = 1 };
        var monster = new BasicModel.MonsterDefinition { Number = 7 };
        var empty = new BasicModel.MonsterSpawnArea { MonsterDefinition = monster, Quantity = 0, X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 };
        var noMonster = new BasicModel.MonsterSpawnArea { Quantity = 5, X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 };

        Assert.Multiple(() =>
        {
            Assert.That(HuntingSpotRating.ExperiencePerMinute(player, map, empty), Is.Zero);
            Assert.That(HuntingSpotRating.ExperiencePerMinute(player, map, noMonster), Is.Zero);
        });
    }
}
