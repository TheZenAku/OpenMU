// <copyright file="MUIdleSettingsTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameServer.MUIdle;
using MUnique.OpenMU.Persistence.BasicModel;
using IncreasableItemOption = MUnique.OpenMU.Persistence.BasicModel.IncreasableItemOption;
using ItemDefinition = MUnique.OpenMU.Persistence.BasicModel.ItemDefinition;
using ItemOfItemSet = MUnique.OpenMU.Persistence.BasicModel.ItemOfItemSet;
using ItemSetGroup = MUnique.OpenMU.Persistence.BasicModel.ItemSetGroup;

/// <summary>
/// Tests the junk classification of the MUIdle auto sell (<see cref="MUIdleSettings.IsJunk"/>):
/// only plain weapons and armor up to the configured level may ever be sold automatically.
/// </summary>
[TestFixture]
public class MUIdleSettingsTest
{
    /// <summary>A plain weapon within the level limit is junk.</summary>
    [Test]
    public void PlainWeaponIsJunk()
    {
        Assert.That(new MUIdleSettings().IsJunk(CreateItem(0, 3)), Is.True);
    }

    /// <summary>Above the configured level, a plain item is kept.</summary>
    [Test]
    public void HighLevelItemIsKept()
    {
        var settings = new MUIdleSettings { SellMaxItemLevel = 4 };
        Assert.That(settings.IsJunk(CreateItem(7, 5)), Is.False);
    }

    /// <summary>Excellent items are never junk.</summary>
    [Test]
    public void ExcellentItemIsKept()
    {
        var item = CreateItem(0, 0);
        item.ItemOptions.Add(new ItemOptionLink { ItemOption = new IncreasableItemOption { OptionType = ItemOptionTypes.Excellent } });
        Assert.That(new MUIdleSettings().IsJunk(item), Is.False);
    }

    /// <summary>Ancient items are never junk.</summary>
    [Test]
    public void AncientItemIsKept()
    {
        var item = CreateItem(8, 0);
        item.ItemSetGroups.Add(new ItemOfItemSet { AncientSetDiscriminator = 1, ItemSetGroup = new ItemSetGroup() });
        Assert.That(new MUIdleSettings().IsJunk(item), Is.False);
    }

    /// <summary>Socket items are never junk.</summary>
    [Test]
    public void SocketItemIsKept()
    {
        var item = CreateItem(0, 0);
        item.SocketCount = 2;
        Assert.That(new MUIdleSettings().IsJunk(item), Is.False);
    }

    /// <summary>Wings, jewels, jewelry, pets, orbs and potions (groups 12+) are never junk.</summary>
    /// <param name="group">The item group.</param>
    [TestCase(12)]
    [TestCase(13)]
    [TestCase(14)]
    [TestCase(15)]
    public void MiscGroupsAreKept(byte group)
    {
        Assert.That(new MUIdleSettings().IsJunk(CreateItem(group, 0)), Is.False);
    }

    /// <summary>Items the player locked are never junk.</summary>
    [Test]
    public void LockedItemIsKept()
    {
        var item = CreateItem(0, 0);
        var settings = new MUIdleSettings();
        settings.LockedItems.Add(item.Id);
        Assert.That(settings.IsJunk(item), Is.False);
    }

    /// <summary>Settings survive the JSON round trip, and invalid JSON gives the safe defaults.</summary>
    [Test]
    public void JsonRoundTrip()
    {
        var settings = new MUIdleSettings { AutoSell = false, SellMaxItemLevel = 2 };
        settings.LockedItems.Add(Guid.NewGuid());
        var parsed = MUIdleSettings.Parse(settings.ToJson());
        Assert.That(parsed.AutoSell, Is.False);
        Assert.That(parsed.SellMaxItemLevel, Is.EqualTo(2));
        Assert.That(parsed.LockedItems, Is.EquivalentTo(settings.LockedItems));
        Assert.That(MUIdleSettings.Parse("{not json").AutoSell, Is.True);
    }

    private static Item CreateItem(byte group, byte level)
    {
        return new Item
        {
            Id = Guid.NewGuid(),
            Level = level,
            Definition = new ItemDefinition { Group = group, Number = 1, Name = "Test" },
        };
    }
}
