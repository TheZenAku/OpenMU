// <copyright file="MUIdleMixPreviewTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.Views.NPC;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Tests the mix preview (<see cref="ItemCraftAction.PreviewMix"/>) behind the odds the client shows.
/// </summary>
[TestFixture]
public class MUIdleMixPreviewTest
{
    private const byte MixNumber = 42;

    /// <summary>
    /// The preview reports the crafting's own success rate and price for the items in the tray,
    /// with the bonus for each extra item, and leaves the items where they are.
    /// </summary>
    [Test]
    public async ValueTask ReportsRateAndPriceWithoutMixingAsync()
    {
        var (player, jewel) = await CreatePlayerAtMachineAsync().ConfigureAwait(false);
        await player.TemporaryStorage!.AddItemAsync(0, CreateItem(jewel)).ConfigureAwait(false);
        await player.TemporaryStorage.AddItemAsync(1, CreateItem(jewel)).ConfigureAwait(false);
        player.Money = 123;

        var preview = new ItemCraftAction().PreviewMix(player, MixNumber);

        Assert.Multiple(() =>
        {
            Assert.That(preview, Is.Not.Null);
            Assert.That(preview!.Value.Error, Is.Null);
            Assert.That(preview.Value.SuccessRate, Is.EqualTo(60)); // 50 + 10 for the second jewel
            Assert.That(preview.Value.Price, Is.EqualTo(1_000 + (60 * 100)));
            Assert.That(player.TemporaryStorage.Items.Count(), Is.EqualTo(2));
            Assert.That(player.Money, Is.EqualTo(123));
        });
    }

    /// <summary>
    /// Missing items give the crafting's own refusal; an unknown crafting gives no preview.
    /// </summary>
    [Test]
    public async ValueTask RefusesLikeTheMixAsync()
    {
        var (player, _) = await CreatePlayerAtMachineAsync().ConfigureAwait(false);
        var action = new ItemCraftAction();

        Assert.Multiple(() =>
        {
            Assert.That(action.PreviewMix(player, MixNumber)?.Error, Is.EqualTo(CraftingResult.LackingMixItems));
            Assert.That(action.PreviewMix(player, MixNumber + 1), Is.Null);
        });
    }

    private static async ValueTask<(Player Player, ItemDefinition Jewel)> CreatePlayerAtMachineAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var jewel = CreateDefinition();
        var required = new BasicModel.ItemCraftingRequiredItem { MinimumAmount = 1, MaximumAmount = 3, MaximumItemLevel = 15, AddPercentage = 10 };
        required.PossibleItems.Add(jewel);
        var settings = new BasicModel.SimpleCraftingSettings { Money = 1_000, MoneyPerFinalSuccessPercentage = 100, SuccessPercent = 50 };
        settings.RequiredItems.Add(required);
        var npc = new BasicModel.MonsterDefinition { NpcWindow = NpcWindow.ChaosMachine };
        npc.ItemCraftings.Add(new BasicModel.ItemCrafting { Number = MixNumber, SimpleCraftingSettings = settings });
        player.OpenedNpc = new NonPlayerCharacter(null!, npc, null!);
        return (player, jewel);
    }

    private static ItemDefinition CreateDefinition()
        => new BasicModel.ItemDefinition { Width = 1, Height = 1, Durability = 1 };

    private static Item CreateItem(ItemDefinition definition)
    {
        var item = new Mock<Item>();
        item.SetupAllProperties();
        item.Setup(i => i.ItemOptions).Returns(new List<ItemOptionLink>());
        item.Setup(i => i.ItemSetGroups).Returns(new List<ItemOfItemSet>());
        item.Object.Definition = definition;
        item.Object.Durability = 1;
        return item.Object;
    }
}
