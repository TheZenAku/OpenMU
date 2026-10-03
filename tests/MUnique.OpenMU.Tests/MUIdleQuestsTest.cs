// <copyright file="MUIdleQuestsTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.GameServer.MUIdle;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Tests which quest of the class line <see cref="MUIdleQuests"/> shows.
/// </summary>
[TestFixture]
public class MUIdleQuestsTest
{
    private readonly BasicModel.CharacterClass _knight = new() { Id = Guid.NewGuid(), Name = "Dark Knight" };
    private readonly BasicModel.CharacterClass _bladeKnight = new() { Id = Guid.NewGuid(), Name = "Blade Knight" };

    /// <summary>
    /// The first quest for the class, then the one after the last finished, then nothing.
    /// </summary>
    [Test]
    public void FollowsTheLineForTheClass()
    {
        var scrollWizard = Quest(0, new BasicModel.CharacterClass { Id = Guid.NewGuid(), Name = "Dark Wizard" });
        var scrollKnight = Quest(0, this._knight);
        var treasures = Quest(1, this._knight);
        var hero = Quest(2, this._bladeKnight);
        var line = new[] { hero, treasures, scrollWizard, scrollKnight };

        Assert.Multiple(() =>
        {
            Assert.That(MUIdleQuests.Current(line, this._knight, null, null), Is.SameAs(scrollKnight));
            Assert.That(MUIdleQuests.Current(line, this._knight, scrollKnight, null), Is.SameAs(treasures));
            Assert.That(MUIdleQuests.Current(line, this._bladeKnight, treasures, null), Is.SameAs(hero));
            Assert.That(MUIdleQuests.Current(line, this._bladeKnight, hero, null), Is.Null);
        });
    }

    /// <summary>
    /// A running quest is the one shown.
    /// </summary>
    [Test]
    public void ShowsTheActiveQuest()
    {
        var scroll = Quest(0, this._knight);
        var treasures = Quest(1, this._knight);

        Assert.That(MUIdleQuests.Current(new[] { scroll, treasures }, this._knight, null, treasures), Is.SameAs(treasures));
    }

    /// <summary>
    /// The band of the first missing quest item; none once the bag holds it.
    /// </summary>
    [Test]
    public void PrefersTheBandOfTheMissingItem()
    {
        var scroll = new BasicModel.ItemDefinition { Id = Guid.NewGuid(), Name = "Scroll of Emperor" };
        var quest = (BasicModel.QuestDefinition)Quest(0, this._knight);
        quest.RequiredItems.Add(new BasicModel.QuestItemRequirement
        {
            Item = scroll,
            MinimumNumber = 1,
            DropItemGroup = new BasicModel.DropItemGroup { MinimumMonsterLevel = 45, MaximumMonsterLevel = 60 },
        });
        var found = new BasicModel.Item { Definition = scroll };

        Assert.Multiple(() =>
        {
            Assert.That(MUIdleQuests.MissingItemBand(quest, []), Is.EqualTo((45, 60)));
            Assert.That(MUIdleQuests.MissingItemBand(quest, [found]), Is.Null);
            Assert.That(MUIdleQuests.MissingItemBand(null, []), Is.Null);
        });
    }

    private static QuestDefinition Quest(short number, CharacterClass characterClass)
        => new BasicModel.QuestDefinition { Id = Guid.NewGuid(), Group = 0, Number = number, QualifiedCharacter = characterClass };
}
