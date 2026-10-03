// <copyright file="MUIdleQaCharacterTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameServer.MUIdle;

/// <summary>
/// Tests the requests of the QA character tool (<see cref="MUIdleQaCharacter"/>).
/// </summary>
[TestFixture]
public class MUIdleQaCharacterTest
{
    /// <summary>
    /// A request names account, character and a level within the game's range; anything else is none.
    /// </summary>
    [Test]
    public void ReadsRequests()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MUIdleQaCharacter.Parse("""{"account":"test300","character":"test300Dw","level":380}"""), Is.EqualTo(("test300", "test300Dw", 380)));
            Assert.That(MUIdleQaCharacter.Parse("""{"account":"test300","character":"test300Dw","level":401}"""), Is.Null);
            Assert.That(MUIdleQaCharacter.Parse("""{"character":"test300Dw","level":380}"""), Is.Null);
            Assert.That(MUIdleQaCharacter.Parse("level 380"), Is.Null);
        });
    }
}
