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

    /// <summary>
    /// An unpaid fee pauses HUNT outside town only; it stays paused while the zen is short, then asks
    /// the client to resume once a minute, gives up after the attempts, and a running helper ends it.
    /// </summary>
    [Test]
    public void UnpaidFeePausesAndResumesWhenTheZenIsThere()
    {
        var now = new DateTime(2026, 10, 3, 23, 0, 0, DateTimeKind.Utc);
        var pause = new MUIdleEconomy.PauseState();
        Assert.Multiple(() =>
        {
            Assert.That(MUIdleEconomy.Step(pause, false, 774, 7_600, true, now), Is.EqualTo(MUIdleEconomy.PauseStep.None), "in town");
            Assert.That(MUIdleEconomy.Step(pause, false, 774, 0, false, now), Is.EqualTo(MUIdleEconomy.PauseStep.None), "no fee");
            Assert.That(MUIdleEconomy.Step(pause, false, 774, 7_600, false, now), Is.EqualTo(MUIdleEconomy.PauseStep.Pause));
            Assert.That(MUIdleEconomy.Step(pause, false, 774, 7_600, true, now), Is.EqualTo(MUIdleEconomy.PauseStep.StillPaused));
            Assert.That(MUIdleEconomy.Step(pause, false, 100_774, 7_600, true, now), Is.EqualTo(MUIdleEconomy.PauseStep.Resume));
            Assert.That(MUIdleEconomy.Step(pause, false, 100_774, 7_600, true, now.AddSeconds(30)), Is.EqualTo(MUIdleEconomy.PauseStep.None), "within the minute");
            Assert.That(MUIdleEconomy.Step(pause, false, 100_774, 7_600, true, now.AddMinutes(1)), Is.EqualTo(MUIdleEconomy.PauseStep.Resume));
            Assert.That(MUIdleEconomy.Step(pause, false, 100_774, 7_600, true, now.AddMinutes(2)), Is.EqualTo(MUIdleEconomy.PauseStep.Resume));
            Assert.That(MUIdleEconomy.Step(pause, false, 100_774, 7_600, true, now.AddMinutes(3)), Is.EqualTo(MUIdleEconomy.PauseStep.GiveUp));
            Assert.That(pause.Paused, Is.False);

            var running = new MUIdleEconomy.PauseState { Paused = true, Attempts = 1 };
            Assert.That(MUIdleEconomy.Step(running, true, 0, 7_600, false, now), Is.EqualTo(MUIdleEconomy.PauseStep.None));
            Assert.That(running.Paused, Is.False);
        });
    }
}
