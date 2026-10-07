// <copyright file="MUIdleQaItemsTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameServer.MUIdle;

/// <summary>
/// Tests the request of the QA item tool (<see cref="MUIdleQaItems"/>).
/// </summary>
[TestFixture]
public class MUIdleQaItemsTest
{
    /// <summary>
    /// A request lists one entry per item (count expanded, level 0 by default).
    /// </summary>
    [Test]
    public void ARequestListsEveryItem()
    {
        var request = MUIdleQaItems.Parse("""{"account":"test300","character":"test300Dl","items":[{"group":13,"number":31,"level":1},{"group":14,"number":13,"count":2},{"group":2,"number":6,"level":4,"option":1,"luck":true}]}""");

        Assert.That(request, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(request!.Account, Is.EqualTo("test300"));
            Assert.That(request.Character, Is.EqualTo("test300Dl"));
            Assert.That(request.Items, Is.EqualTo(new List<MUIdleQaItems.QaItem>
            {
                new(13, 31, 1),
                new(14, 13, 0),
                new(14, 13, 0),
                new(2, 6, 4, 1, true),
            }));
        });
    }

    /// <summary>
    /// An additional option above level 4 is no request.
    /// </summary>
    [Test]
    public void AnOptionAboveFourIsRefused()
    {
        Assert.That(MUIdleQaItems.Parse("""{"account":"a","character":"c","items":[{"group":2,"number":6,"option":5}]}"""), Is.Null);
    }

    /// <summary>
    /// Too many items, a level above 15, a missing item number or no account are no request.
    /// </summary>
    [Test]
    public void BadRequestsAreRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MUIdleQaItems.Parse("""{"account":"a","character":"c","items":[{"group":14,"number":13,"count":21}]}"""), Is.Null, "count");
            Assert.That(MUIdleQaItems.Parse("""{"account":"a","character":"c","items":[{"group":14,"number":13,"count":15},{"group":14,"number":14,"count":6}]}"""), Is.Null, "total");
            Assert.That(MUIdleQaItems.Parse("""{"account":"a","character":"c","items":[{"group":12,"number":4,"level":16}]}"""), Is.Null, "level");
            Assert.That(MUIdleQaItems.Parse("""{"account":"a","character":"c","items":[{"group":12}]}"""), Is.Null, "number");
            Assert.That(MUIdleQaItems.Parse("""{"character":"c","items":[{"group":12,"number":4}]}"""), Is.Null, "account");
            Assert.That(MUIdleQaItems.Parse("""{"account":"a","character":"c","items":[]}"""), Is.Null, "empty");
            Assert.That(MUIdleQaItems.Parse("not json"), Is.Null, "json");
        });
    }
}
