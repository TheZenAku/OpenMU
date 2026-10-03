// <copyright file="MUIdleOnlineAutoMapTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameServer.MUIdle;

/// <summary>
/// Tests the decisions of <see cref="MUIdleOnlineAutoMap"/>.
/// </summary>
[TestFixture]
public class MUIdleOnlineAutoMapTest
{
    /// <summary>
    /// A map counts only when its safe monsters clear the margin; the strongest such map wins.
    /// </summary>
    [Test]
    public void ChoosesTheStrongestMapAboveTheMargin()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MUIdleOnlineAutoMap.Choose(40, new[] { ("Devias", 47), ("Dungeon", 45) }), Is.Null);
            Assert.That(MUIdleOnlineAutoMap.Choose(40, new[] { ("Devias", 48), ("Lost Tower", 60), ("Atlans", 55) }), Is.EqualTo("Lost Tower"));
            Assert.That(MUIdleOnlineAutoMap.Choose(40, Array.Empty<(string, int)>()), Is.Null);
        });
    }

    /// <summary>
    /// A map with nothing safe to fight is left for any map with something safe.
    /// </summary>
    [Test]
    public void LeavesAMapWithNothingSafe()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MUIdleOnlineAutoMap.Choose(0, new[] { ("Lorencia", 3), ("Noria", 0) }), Is.EqualTo("Lorencia"));
            Assert.That(MUIdleOnlineAutoMap.Choose(0, new[] { ("Noria", 0) }), Is.Null);
        });
    }

    /// <summary>
    /// The first sight starts the clock; then a check every cooldown.
    /// </summary>
    [Test]
    public void ChecksOnACooldown()
    {
        var now = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        var cooldown = TimeSpan.FromMinutes(10);

        var first = MUIdleOnlineAutoMap.Schedule(null, now, cooldown);
        var early = MUIdleOnlineAutoMap.Schedule(first.Next, now.AddMinutes(1), cooldown);
        var due = MUIdleOnlineAutoMap.Schedule(first.Next, now.AddMinutes(2), cooldown);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo((false, now.AddMinutes(2))));
            Assert.That(early, Is.EqualTo((false, now.AddMinutes(2))));
            Assert.That(due, Is.EqualTo((true, now.AddMinutes(12))));
        });
    }
}
