// <copyright file="MUIdleNoticesTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameServer.MUIdle;

/// <summary>
/// Tests the update notices of the update tool (<see cref="MUIdleNotices"/>, D34).
/// </summary>
[TestFixture]
public class MUIdleNoticesTest
{
    /// <summary>
    /// A restart notice carries its time (UTC); a client notice needs none.
    /// </summary>
    [Test]
    public void ReadsRestartAndClientNotices()
    {
        var restart = MUIdleNotices.Parse("""{"id":"2026-10-09T10:00:00Z","kind":"restart","restartAtUtc":"2026-10-09T10:02:00Z"}""");
        var client = MUIdleNotices.Parse("""{"id":"abc","kind":"client"}""");

        Assert.Multiple(() =>
        {
            Assert.That(restart?.Kind, Is.EqualTo("restart"));
            Assert.That(restart?.RestartAtUtc, Is.EqualTo(new DateTime(2026, 10, 9, 10, 2, 0, DateTimeKind.Utc)));
            Assert.That(client?.Kind, Is.EqualTo("client"));
        });
    }

    /// <summary>
    /// Anything else is no notice: no id, an unknown kind, a restart without a time, broken JSON.
    /// </summary>
    [Test]
    public void RefusesAnythingElse()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MUIdleNotices.Parse("""{"kind":"client"}"""), Is.Null, "id");
            Assert.That(MUIdleNotices.Parse("""{"id":"a","kind":"party"}"""), Is.Null, "kind");
            Assert.That(MUIdleNotices.Parse("""{"id":"a","kind":"restart"}"""), Is.Null, "time");
            Assert.That(MUIdleNotices.Parse("not json"), Is.Null, "json");
            Assert.That(MUIdleNotices.Parse(string.Empty), Is.Null, "empty");
        });
    }
}
