// <copyright file="MUIdleInventoryTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameServer.MUIdle;
using MUnique.OpenMU.Persistence;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Tests the inventory pages, selling and junk of the inventory window (<see cref="MUIdleInventory"/>, D33).
/// </summary>
[TestFixture]
public class MUIdleInventoryTest
{
    /// <summary>
    /// Page I is the inventory; every two extensions are one more page, up to the four extensions.
    /// </summary>
    [Test]
    public void TwoExtensionsMakeAPage()
    {
        static int Pages(int extensions) => MUIdleInventory.UnlockedPages(new BasicModel.Character { InventoryExtensions = extensions });

        Assert.Multiple(() =>
        {
            Assert.That(Pages(0), Is.EqualTo(1));
            Assert.That(Pages(1), Is.EqualTo(1));
            Assert.That(Pages(2), Is.EqualTo(2));
            Assert.That(Pages(3), Is.EqualTo(2));
            Assert.That(Pages(4), Is.EqualTo(3));
            Assert.That(MUIdleInventory.NativePages, Is.EqualTo(3));
        });
    }

    /// <summary>
    /// The free pages cost nothing; the others what the configuration says.
    /// </summary>
    [Test]
    public void FreePagesCostNothing()
    {
        var configuration = new MUIdleConfiguration { FreeInventoryPages = 2, InventoryPage3Price = 1_000, InventoryPage7Price = 7_000 };

        Assert.Multiple(() =>
        {
            Assert.That(MUIdleInventory.PagePrice(configuration, 1), Is.Zero);
            Assert.That(MUIdleInventory.PagePrice(configuration, 2), Is.Zero);
            Assert.That(MUIdleInventory.PagePrice(configuration, 3), Is.EqualTo(1_000));
            Assert.That(MUIdleInventory.PagePrice(configuration, 7), Is.EqualTo(7_000));
            Assert.That(MUIdleInventory.PagePrice(configuration, 8), Is.Zero);
            Assert.That(MUIdleInventory.PagePrice(new MUIdleConfiguration { FreeInventoryPages = 3, InventoryPage3Price = 1_000 }, 3), Is.Zero);
        });
    }

    /// <summary>
    /// What no merchant buys is never sold from the window either.
    /// </summary>
    [Test]
    public void OnlyWhatMerchantsBuySells()
    {
        var sellable = CreateItem(new BasicModel.ItemDefinition { Durability = 20 });
        var notSellable = CreateItem(new BasicModel.ItemDefinition { Durability = 20, IsSellableToNpc = false });
        var bound = CreateItem(new BasicModel.ItemDefinition { Durability = 20, IsBoundToCharacter = true });

        Assert.Multiple(() =>
        {
            Assert.That(MUIdleInventory.CanBeSold(sellable), Is.True);
            Assert.That(MUIdleInventory.CanBeSold(notSellable), Is.False);
            Assert.That(MUIdleInventory.CanBeSold(bound), Is.False);
        });
    }

    /// <summary>
    /// The junk is what lies in the bag: worn and locked items stay, and so does anything better than junk.
    /// </summary>
    [Test]
    public async ValueTask JunkIsWhatLiesInTheBagAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var sword = new BasicModel.ItemDefinition { Group = 0, Width = 1, Height = 3, Durability = 20 };
        var worn = CreateItem(sword, 0);
        var junk = CreateItem(sword, 12);
        var locked = CreateItem(sword, 14);
        var good = CreateItem(sword, 16);
        good.Level = 9;
        var settings = new MUIdleSettings { LockedItems = [locked.GetId()] };

        var result = MUIdleInventory.Junk(player, [worn, junk, locked, good], settings).ToList();

        Assert.That(result, Is.EqualTo(new[] { junk }));
    }

    /// <summary>
    /// The free pages are inventory extensions the character gets at its entry, usable at once: an item
    /// placed on page II is part of the character's inventory, and leaves it again when removed.
    /// </summary>
    [Test]
    public async ValueTask FreePagesAreUsableAtOnceAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var character = player.SelectedCharacter!;
        var inventory = player.Inventory!;
        var pageTwo = InventoryConstants.FirstExtensionItemSlotIndex;
        Assert.That(character.InventoryExtensions, Is.Zero);

        MUIdleInventory.GrantFreePages(player, new MUIdleConfiguration());

        var item = CreateItem(new BasicModel.ItemDefinition { Width = 1, Height = 1, Durability = 1 });
        var added = await inventory.AddItemAsync(pageTwo, item).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(character.InventoryExtensions, Is.EqualTo(2));
            Assert.That(player.InventorySize, Is.EqualTo(InventoryConstants.GetInventorySize(2)));
            Assert.That(added, Is.True);
            Assert.That(inventory.GetItem(pageTwo), Is.SameAs(item));
            Assert.That(character.Inventory!.Items, Does.Contain(item));
        });

        await inventory.RemoveItemAsync(item).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(inventory.GetItem(pageTwo), Is.Null);
            Assert.That(character.Inventory!.Items, Does.Not.Contain(item));
        });
    }

    /// <summary>
    /// Granting again changes nothing, and a character with more extensions keeps them.
    /// </summary>
    [Test]
    public async ValueTask GrantingKeepsMoreExtensionsAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.SelectedCharacter!.InventoryExtensions = 4;

        MUIdleInventory.GrantFreePages(player, new MUIdleConfiguration());

        Assert.That(player.SelectedCharacter.InventoryExtensions, Is.EqualTo(4));
    }

    /// <summary>
    /// The storage pages come after the three inventory pages, and only once all three are there.
    /// </summary>
    [Test]
    public void StoragePagesFollowTheInventoryPages()
    {
        var state = new BasicModel.MUIdleCharacterState { StoragePages = 2 };

        Assert.Multiple(() =>
        {
            Assert.That(MUIdleInventory.TotalPages(new BasicModel.Character { InventoryExtensions = 2 }, state), Is.EqualTo(2));
            Assert.That(MUIdleInventory.TotalPages(new BasicModel.Character { InventoryExtensions = 4 }, state), Is.EqualTo(5));
            Assert.That(MUIdleInventory.TotalPages(new BasicModel.Character { InventoryExtensions = 4 }, new BasicModel.MUIdleCharacterState { StoragePages = 9 }), Is.EqualTo(7));
            Assert.That(MUIdleInventory.FirstStorageSlot(4), Is.Zero);
            Assert.That(MUIdleInventory.FirstStorageSlot(7), Is.EqualTo(192));
        });
    }

    /// <summary>
    /// An item goes to the first place on its page where it fits, never onto another page.
    /// </summary>
    [Test]
    public void StoredItemsFitTheirPage()
    {
        var storage = new BasicModel.ItemStorage();
        var sword = new BasicModel.ItemDefinition { Width = 1, Height = 3 };
        var gem = new BasicModel.ItemDefinition { Width = 1, Height = 1 };

        // Page V (slots 64-127): column 0 holds a sword; the next item goes beside it.
        storage.Items.Add(CreateItem(sword, 64));
        Assert.That(MUIdleInventory.FreeStorageSlot(storage, 5, CreateItem(gem)), Is.EqualTo(65));

        // Page IV is empty: its first square.
        Assert.That(MUIdleInventory.FreeStorageSlot(storage, 4, CreateItem(sword)), Is.Zero);

        // A full page VII has no place, and the item does not spill elsewhere.
        for (var slot = 192; slot <= 255; slot++)
        {
            storage.Items.Add(CreateItem(gem, (byte)slot));
        }

        Assert.That(MUIdleInventory.FreeStorageSlot(storage, 7, CreateItem(gem)), Is.Null);
    }

    private static Item CreateItem(ItemDefinition definition, byte slot = 12)
        => new BasicModel.Item { Id = Guid.NewGuid(), Definition = definition, Durability = 1, ItemSlot = slot };
}
