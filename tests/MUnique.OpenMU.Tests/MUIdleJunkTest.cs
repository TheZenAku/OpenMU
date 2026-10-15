// <copyright file="MUIdleJunkTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameServer.MUIdle;
using MUnique.OpenMU.Persistence.BasicModel;
using CharacterClass = MUnique.OpenMU.Persistence.BasicModel.CharacterClass;
using Item = MUnique.OpenMU.Persistence.BasicModel.Item;
using ItemDefinition = MUnique.OpenMU.Persistence.BasicModel.ItemDefinition;
using MiniGameDefinition = MUnique.OpenMU.Persistence.BasicModel.MiniGameDefinition;
using Skill = MUnique.OpenMU.Persistence.BasicModel.Skill;

/// <summary>
/// Tests what HUNT sells beside the plain weapons and armor (<see cref="MUIdleJunk"/>, owner 10-15): skill
/// items the character never learns, the weaker potions and event materials below its event level.
/// </summary>
[TestFixture]
public class MUIdleJunkTest
{
    /// <summary>An orb of another class is junk; one the character will learn is kept.</summary>
    [Test]
    public async ValueTask OrbOfAnotherClassIsJunkAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var other = Bag(Definition(12, 7, skill: new Skill { Number = 41 }), 20);
        other.Definition!.QualifiedCharacters.Add(new CharacterClass());
        var own = Bag(Definition(12, 8, skill: new Skill { Number = 42 }), 21);

        Assert.Multiple(() =>
        {
            Assert.That(MUIdleJunk.IsJunk(player, other, new MUIdleSettings()), Is.True);
            Assert.That(MUIdleJunk.IsJunk(player, own, new MUIdleSettings()), Is.False);
            Assert.That(MUIdleJunk.IsJunk(player, other, new MUIdleSettings { SellSkillItems = false }), Is.False);
        });
    }

    /// <summary>An orb of a skill the character knows is junk.</summary>
    [Test]
    public async ValueTask OrbOfAKnownSkillIsJunkAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var skill = new Skill { Number = 43, Name = "Known" };
        await player.SkillList!.AddLearnedSkillAsync(skill).ConfigureAwait(false);

        Assert.That(MUIdleJunk.IsJunk(player, Bag(Definition(12, 9, skill: skill), 20), new MUIdleSettings()), Is.True);
    }

    /// <summary>
    /// A potion is junk while the stronger ones of its kind fill the potion stock (60 charges): one large
    /// potion is not enough - HUNT would sell the medium ones it just bought where no large ones are sold.
    /// Antidotes always.
    /// </summary>
    [Test]
    public async ValueTask WeakerPotionsAreJunkAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var small = await CarryAsync(player, Definition(14, 1), 20).ConfigureAwait(false);
        var apple = await CarryAsync(player, Definition(14, 0), 21).ConfigureAwait(false);
        var antidote = await CarryAsync(player, Definition(14, 8), 22).ConfigureAwait(false);
        var smallMana = await CarryAsync(player, Definition(14, 4), 23).ConfigureAwait(false);
        var large = await CarryAsync(player, Definition(14, 3), 24).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(MUIdleJunk.IsJunk(player, small, new MUIdleSettings()), Is.False);
            Assert.That(MUIdleJunk.IsJunk(player, apple, new MUIdleSettings()), Is.False);
            Assert.That(MUIdleJunk.IsJunk(player, antidote, new MUIdleSettings()), Is.True);
        });

        large.Durability = 60;

        Assert.Multiple(() =>
        {
            Assert.That(MUIdleJunk.IsJunk(player, small, new MUIdleSettings()), Is.True);
            Assert.That(MUIdleJunk.IsJunk(player, apple, new MUIdleSettings()), Is.True);
            Assert.That(MUIdleJunk.IsJunk(player, smallMana, new MUIdleSettings()), Is.False);
            Assert.That(MUIdleJunk.IsJunk(player, small, new MUIdleSettings { SellWeakPotions = false }), Is.False);
        });
    }

    /// <summary>
    /// A Devil's Eye of an event level the character is past is junk; one of its level or above is kept.
    /// </summary>
    [Test]
    public async ValueTask OutgrownEventMaterialIsJunkAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        Mock.Get(player.GameContext.Configuration).Setup(c => c.MiniGameDefinitions).Returns(new List<DataModel.Configuration.MiniGameDefinition>
        {
            new MiniGameDefinition { Type = MiniGameType.DevilSquare, TicketItemLevel = 1, MaximumCharacterLevel = 99, MaximumSpecialCharacterLevel = 79 },
            new MiniGameDefinition { Type = MiniGameType.DevilSquare, TicketItemLevel = 2, MaximumCharacterLevel = 179, MaximumSpecialCharacterLevel = 159 },
            new MiniGameDefinition { Type = MiniGameType.DevilSquare, TicketItemLevel = 3, MaximumCharacterLevel = 400, MaximumSpecialCharacterLevel = 400 },
        });
        player.SelectedCharacter!.Attributes.First(a => a.Definition == Stats.Level).Value = 150;
        var eye = Definition(14, 17);

        Assert.Multiple(() =>
        {
            Assert.That(MUIdleJunk.IsJunk(player, Bag(eye, 20, level: 1), new MUIdleSettings()), Is.True);
            Assert.That(MUIdleJunk.IsJunk(player, Bag(eye, 21, level: 2), new MUIdleSettings()), Is.False);
            Assert.That(MUIdleJunk.IsJunk(player, Bag(eye, 22, level: 3), new MUIdleSettings()), Is.False);
            Assert.That(MUIdleJunk.IsJunk(player, Bag(eye, 23, level: 1), new MUIdleSettings { SellOldEventItems = false }), Is.False);
        });
    }

    /// <summary>A plain ring the character does not wear better is junk; an excellent one never.</summary>
    [Test]
    public async ValueTask PlainRingIsJunkAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var ring = Bag(Definition(13, 9), 20);
        var excellent = Bag(Definition(13, 9), 21);
        excellent.ItemOptions.Add(new ItemOptionLink { ItemOption = new IncreasableItemOption { OptionType = DataModel.Configuration.Items.ItemOptionTypes.Excellent } });

        Assert.Multiple(() =>
        {
            Assert.That(MUIdleJunk.IsJunk(player, ring, new MUIdleSettings()), Is.True);
            Assert.That(MUIdleJunk.IsJunk(player, excellent, new MUIdleSettings()), Is.False);
        });
    }

    /// <summary>Locked and worn items are never junk.</summary>
    [Test]
    public async ValueTask LockedAndWornAreKeptAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var antidote = Bag(Definition(14, 8), 20);
        var worn = Bag(Definition(14, 8), InventoryConstants.PendantSlot);

        Assert.Multiple(() =>
        {
            Assert.That(MUIdleJunk.IsJunk(player, antidote, new MUIdleSettings { LockedItems = [antidote.Id] }), Is.False);
            Assert.That(MUIdleJunk.IsJunk(player, worn, new MUIdleSettings()), Is.False);
        });
    }

    private static ItemDefinition Definition(byte group, short number, Skill? skill = null)
        => new() { Group = group, Number = number, Name = $"Test {group}/{number}", Width = 1, Height = 1, Durability = 1, Skill = skill };

    private static Item Bag(ItemDefinition definition, byte slot, byte level = 0)
        => new() { Id = Guid.NewGuid(), Definition = definition, ItemSlot = slot, Durability = 1, Level = level };

    private static async ValueTask<Item> CarryAsync(Player player, ItemDefinition definition, byte slot)
    {
        var item = Bag(definition, slot);
        await player.Inventory!.AddItemAsync(slot, item).ConfigureAwait(false);
        return item;
    }
}
