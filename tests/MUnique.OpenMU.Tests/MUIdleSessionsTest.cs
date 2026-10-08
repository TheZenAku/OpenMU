// <copyright file="MUIdleSessionsTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameServer.MUIdle;

/// <summary>
/// Tests the rule which decides whether a hunt goes on offline - for a regular disconnect and for the
/// crash marker of the periodic online save alike (<see cref="MUIdleSessions.CanContinueOffline"/>).
/// </summary>
[TestFixture]
public class MUIdleSessionsTest
{
    /// <summary>
    /// Without HUNT running, or with the server setting off, nothing goes on offline.
    /// </summary>
    [Test]
    public async ValueTask OnlyARunningHuntGoesOnOfflineAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var on = new MUIdleConfiguration { ContinueHuntingOnDisconnect = true };
        var off = new MUIdleConfiguration { ContinueHuntingOnDisconnect = false };

        player.Attributes![Stats.IsMuHelperActive] = 0;
        var idle = MUIdleSessions.CanContinueOffline(player, on);
        player.Attributes[Stats.IsMuHelperActive] = 1;
        var disabled = MUIdleSessions.CanContinueOffline(player, off);

        Assert.Multiple(() =>
        {
            Assert.That(idle, Is.False);
            Assert.That(disabled, Is.False);
        });
    }

    /// <summary>
    /// HUNT switched on goes on offline even in town (where the client's helper pauses and HUNT walks
    /// out); the original's helper alone only outside the safezone; nothing without either.
    /// </summary>
    [Test]
    public void HuntIntentGoesOnEvenInTown()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MUIdleSessions.HuntGoesOn(huntIntent: true, helperActive: false, inSafezone: true), Is.True);
            Assert.That(MUIdleSessions.HuntGoesOn(huntIntent: true, helperActive: true, inSafezone: false), Is.True);
            Assert.That(MUIdleSessions.HuntGoesOn(huntIntent: false, helperActive: true, inSafezone: false), Is.True);
            Assert.That(MUIdleSessions.HuntGoesOn(huntIntent: false, helperActive: true, inSafezone: true), Is.False);
            Assert.That(MUIdleSessions.HuntGoesOn(huntIntent: false, helperActive: false, inSafezone: false), Is.False);
        });
    }
}
