// <copyright file="MUIdleEquipmentTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameServer.MUIdle;
using MUnique.OpenMU.Persistence;

/// <summary>
/// Tests HUNT's gear (<see cref="MUIdleEquipment"/>): the better piece by the character's own numbers
/// goes on, and the player's locks are kept.
/// </summary>
[TestFixture]
public class MUIdleEquipmentTest
{
    private const byte ArmorGroup = 8;
    private const byte SwordGroup = 0;
    private const byte StaffGroup = 5;

    /// <summary>An armor with more defense than the worn one goes on; the old one goes to the bag.</summary>
    [Test]
    public async ValueTask StrongerArmorGoesOnAsync()
    {
        var player = await CreatePlayerAsync().ConfigureAwait(false);
        var worn = await WearAsync(player, Armor(player, 1, defense: 10), InventoryConstants.ArmorSlot).ConfigureAwait(false);
        var better = await CarryAsync(player, Armor(player, 2, defense: 30)).ConfigureAwait(false);

        var slot = await MUIdleEquipment.EquipBestAsync(player, new MUIdleSettings()).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(slot, Is.EqualTo(InventoryConstants.ArmorSlot));
            Assert.That(player.Inventory!.GetItem(InventoryConstants.ArmorSlot), Is.SameAs(better));
            Assert.That(worn.ItemSlot, Is.GreaterThanOrEqualTo(InventoryConstants.EquippableSlotsCount));
        });
    }

    /// <summary>A weaker piece is no upgrade and stays in the bag.</summary>
    [Test]
    public async ValueTask WeakerArmorStaysAsync()
    {
        var player = await CreatePlayerAsync().ConfigureAwait(false);
        await WearAsync(player, Armor(player, 1, defense: 30), InventoryConstants.ArmorSlot).ConfigureAwait(false);
        var weaker = await CarryAsync(player, Armor(player, 2, defense: 10)).ConfigureAwait(false);

        var upgrade = MUIdleEquipment.IsUpgrade(player, weaker, new MUIdleSettings());
        var slot = await MUIdleEquipment.EquipBestAsync(player, new MUIdleSettings()).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(upgrade, Is.False);
            Assert.That(slot, Is.Null);
        });
    }

    /// <summary>The higher item level counts as the game counts it (here: +4 defense per level).</summary>
    [Test]
    public async ValueTask ItemLevelCountsAsync()
    {
        var player = await CreatePlayerAsync().ConfigureAwait(false);
        var definition = Armor(player, 1, defense: 10);
        var table = new Persistence.BasicModel.ItemLevelBonusTable();
        table.BonusPerLevel.Add(new Persistence.BasicModel.LevelBonus { Level = 9, AdditionalValue = 36 });
        definition.BasePowerUpAttributes.Single().BonusPerLevelTable = table;
        await WearAsync(player, definition, InventoryConstants.ArmorSlot).ConfigureAwait(false);
        var plusNine = await CarryAsync(player, definition, level: 9).ConfigureAwait(false);

        Assert.That(MUIdleEquipment.IsUpgrade(player, plusNine, new MUIdleSettings()), Is.True);
    }

    /// <summary>A locked slot is the player's: nothing goes in or out.</summary>
    [Test]
    public async ValueTask LockedSlotIsKeptAsync()
    {
        var player = await CreatePlayerAsync().ConfigureAwait(false);
        var worn = await WearAsync(player, Armor(player, 1, defense: 10), InventoryConstants.ArmorSlot).ConfigureAwait(false);
        await CarryAsync(player, Armor(player, 2, defense: 30)).ConfigureAwait(false);
        var settings = new MUIdleSettings { LockedEquipSlots = [InventoryConstants.ArmorSlot] };

        var slot = await MUIdleEquipment.EquipBestAsync(player, settings).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(slot, Is.Null);
            Assert.That(player.Inventory!.GetItem(InventoryConstants.ArmorSlot), Is.SameAs(worn));
        });
    }

    /// <summary>A locked worn item stays on; a locked item of the bag is never put on.</summary>
    [Test]
    public async ValueTask LockedItemsAreKeptAsync()
    {
        var player = await CreatePlayerAsync().ConfigureAwait(false);
        var worn = await WearAsync(player, Armor(player, 1, defense: 10), InventoryConstants.ArmorSlot).ConfigureAwait(false);
        var better = await CarryAsync(player, Armor(player, 2, defense: 30)).ConfigureAwait(false);

        var wornLocked = await MUIdleEquipment.EquipBestAsync(player, new MUIdleSettings { LockedItems = [worn.GetId()] }).ConfigureAwait(false);
        var betterLocked = await MUIdleEquipment.EquipBestAsync(player, new MUIdleSettings { LockedItems = [better.GetId()] }).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(wornLocked, Is.Null);
            Assert.That(betterLocked, Is.Null);
            Assert.That(player.Inventory!.GetItem(InventoryConstants.ArmorSlot), Is.SameAs(worn));
        });
    }

    /// <summary>A weapon's skill is not given up for numbers: the Blade Knight's combo starts with one.</summary>
    [Test]
    public async ValueTask WeaponKeepsItsSkillAsync()
    {
        var player = await CreatePlayerAsync().ConfigureAwait(false);
        var skill = new Skill { Number = 19, Name = "Falling Slash" };
        var weak = Sword(player, 1, damage: 10);
        weak.Skill = skill;
        var worn = await WearAsync(player, weak, InventoryConstants.LeftHandSlot).ConfigureAwait(false);
        worn.HasSkill = true;
        var strong = Sword(player, 2, damage: 40);
        strong.Skill = skill;
        var withoutSkill = await CarryAsync(player, strong).ConfigureAwait(false);

        Assert.That(MUIdleEquipment.IsUpgrade(player, withoutSkill, new MUIdleSettings()), Is.False);
        withoutSkill.HasSkill = true;
        Assert.That(MUIdleEquipment.IsUpgrade(player, withoutSkill, new MUIdleSettings()), Is.True);
    }

    /// <summary>
    /// A two-handed weapon replaces a one-handed one of its group (their slot types differ): a Dark
    /// Wizard's Legendary Staff gave way to nothing better on 10-15 because of that.
    /// </summary>
    [Test]
    public async ValueTask TwoHandedReplacesOneHandedOfItsGroupAsync()
    {
        var player = await CreatePlayerAsync().ConfigureAwait(false);
        await WearAsync(player, Staff(player, 1, damage: 10), InventoryConstants.LeftHandSlot).ConfigureAwait(false);
        var twoHanded = Staff(player, 2, damage: 40);
        twoHanded.Width = 2;
        var carried = await CarryAsync(player, twoHanded).ConfigureAwait(false);

        Assert.That(MUIdleEquipment.IsUpgrade(player, carried, new MUIdleSettings()), Is.True);
    }

    /// <summary>The kind of damage the character deals counts: a wizard's numbers are its wizardry.</summary>
    [Test]
    public void RateCountsTheStrongestDamage()
    {
        var stats = new AttributeSystem(
            [
                new StatAttribute(Stats.MinimumWizBaseDmg, 100),
                new StatAttribute(Stats.MaximumWizBaseDmg, 120),
                new StatAttribute(Stats.MinimumPhysBaseDmg, 10),
                new StatAttribute(Stats.MaximumPhysBaseDmg, 12),
            ],
            [],
            []);

        var (score, kind) = MUIdleEquipment.Rate(stats);

        Assert.Multiple(() =>
        {
            Assert.That(kind, Is.EqualTo(DamageType.Wizardry));
            Assert.That(score, Is.GreaterThan(MUIdleEquipment.Rate(stats, DamageType.Physical).Score));
        });
    }

    private static async ValueTask<Player> CreatePlayerAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var combinations = player.SelectedCharacter!.CharacterClass!.AttributeCombinations;
        combinations.Add(new AttributeRelationship(Stats.DefensePvm, 1, Stats.DefenseBase));
        combinations.Add(new AttributeRelationship(Stats.MinimumPhysBaseDmg, 1, Stats.MinimumPhysBaseDmgByWeapon));
        combinations.Add(new AttributeRelationship(Stats.MaximumPhysBaseDmg, 1, Stats.MaximumPhysBaseDmgByWeapon));
        return player;
    }

    private static ItemDefinition Armor(Player player, short number, float defense)
        => Definition(player, ArmorGroup, number, InventoryConstants.ArmorSlot, (Stats.DefenseBase, defense));

    private static ItemDefinition Sword(Player player, short number, float damage)
        => Definition(player, SwordGroup, number, InventoryConstants.LeftHandSlot, (Stats.MinimumPhysBaseDmgByWeapon, damage), (Stats.MaximumPhysBaseDmgByWeapon, damage + 5));

    private static ItemDefinition Staff(Player player, short number, float damage)
        => Definition(player, StaffGroup, number, InventoryConstants.LeftHandSlot, (Stats.MinimumPhysBaseDmgByWeapon, damage), (Stats.MaximumPhysBaseDmgByWeapon, damage + 5));

    private static ItemDefinition Definition(Player player, byte group, short number, int slot, params (AttributeDefinition Target, float Value)[] powerUps)
    {
        var slotType = new Mock<ItemSlotType>();
        slotType.Setup(s => s.ItemSlots).Returns(new List<int> { slot });
        var definition = new Mock<ItemDefinition>();
        definition.SetupAllProperties();
        definition.Setup(d => d.QualifiedCharacters).Returns(new List<CharacterClass> { player.SelectedCharacter!.CharacterClass! });
        definition.Setup(d => d.PossibleItemOptions).Returns(new List<ItemOptionDefinition>());
        definition.Setup(d => d.Requirements).Returns(new List<AttributeRequirement>());
        definition.Setup(d => d.PossibleItemSetGroups).Returns(new List<ItemSetGroup>());
        definition.Setup(d => d.BasePowerUpAttributes).Returns(powerUps
            .Select(p => new ItemBasePowerUpDefinition { TargetAttribute = p.Target, BaseValue = p.Value, AggregateType = AggregateType.AddRaw })
            .ToList());
        definition.Setup(d => d.ItemSlot).Returns(slotType.Object);
        var result = definition.Object;
        result.Group = group;
        result.Number = number;
        result.Width = 1;
        result.Height = 1;
        result.Durability = 100;
        result.DropLevel = 10;
        result.Name = $"Test {group}/{number}";
        player.GameContext.Configuration.Items.Add(result);
        return result;
    }

    private static Item Create(ItemDefinition definition, byte level)
        => new Persistence.BasicModel.Item
        {
            Id = Guid.NewGuid(),
            Definition = definition,
            Durability = definition.Durability,
            Level = level,
        };

    private static async ValueTask<Item> WearAsync(Player player, ItemDefinition definition, byte slot)
    {
        var item = Create(definition, 0);
        await player.Inventory!.AddItemAsync(slot, item).ConfigureAwait(false);
        return item;
    }

    private static async ValueTask<Item> CarryAsync(Player player, ItemDefinition definition, byte level = 0)
    {
        var item = Create(definition, level);
        var slot = player.Inventory!.CheckInvSpace(item) ?? throw new InvalidOperationException("no room");
        await player.Inventory.AddItemAsync(slot, item).ConfigureAwait(false);
        return item;
    }
}
