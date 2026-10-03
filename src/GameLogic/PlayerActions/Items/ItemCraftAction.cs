// <copyright file="ItemCraftAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Items;

using MUnique.OpenMU.DataModel.Configuration.ItemCrafting;
using MUnique.OpenMU.GameLogic.Views.NPC;

/// <summary>
/// The action to craft items with crafting NPCs.
/// </summary>
public class ItemCraftAction
{
    private readonly IDictionary<ItemCrafting, IItemCraftingHandler> _craftingHandlerCache = new Dictionary<ItemCrafting, IItemCraftingHandler>();

    /// <summary>
    /// Mixes the items at the currently opened crafting NPC.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="mixTypeId">The mix type identifier.</param>
    /// <param name="socketSlot">The socket slot.</param>
    public async ValueTask MixItemsAsync(Player player, byte mixTypeId, byte socketSlot)
    {
        var npcStats = player.OpenedNpc?.Definition;

        var crafting = npcStats?.ItemCraftings.FirstOrDefault(c => c.Number == mixTypeId);
        if (crafting is null)
        {
            await player.InvokeViewPlugInAsync<IShowItemCraftingResultPlugIn>(p => p.ShowResultAsync(CraftingResult.IncorrectMixItems, null)).ConfigureAwait(false);
            return;
        }

        var craftingHandler = this.GetCraftingHandler(crafting);

        (CraftingResult, Item?) result;
        try
        {
            result = await craftingHandler.DoMixAsync(player, socketSlot).ConfigureAwait(false);
        }
        catch
        {
            result = (CraftingResult.LackingMixItems, null);
        }

        var itemList = player.TemporaryStorage?.Items.ToList() ?? new List<Item>();
        await player.InvokeViewPlugInAsync<IShowItemCraftingResultPlugIn>(p => p.ShowResultAsync(result.Item1, itemList.Count > 1 ? null : result.Item2)).ConfigureAwait(false);
        await player.InvokeViewPlugInAsync<IShowMerchantStoreItemListPlugIn>(
            p => p.ShowMerchantStoreItemListAsync(
                itemList,
                npcStats!.NpcWindow == NpcWindow.PetTrainer && result.Item1 != CraftingResult.Success ? StoreKind.ResurrectionFailed : StoreKind.ChaosMachine))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the relevant <see cref="ItemCrafting"/> by testing the mix items against every crafting's item requirements.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The relevant <see cref="ItemCrafting"/>.</returns>
    public ItemCrafting? FindAppropriateCraftingByItems(Player player)
    {
        if (player.OpenedNpc?.Definition is { } npc)
        {
            // Wing crafting is similar but has one extra requirement (chaos weapon) than
            // chaos weapon crafting, so we set a descending order so it gets checked first
            foreach (var itemCrafting in npc.ItemCraftings.OrderByDescending(c => c.Number))
            {
                if (this.GetCraftingHandler(itemCrafting).TryGetRequiredItems(player, out _, out _) is null)
                {
                    return itemCrafting;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Previews a mix at the currently opened crafting NPC without mixing (MUIdle: the odds shown
    /// before the mix): the crafting's own item check, its success rate and its untaxed price.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="mixTypeId">The mix type identifier.</param>
    /// <returns>
    /// The preview, with the crafting the items fit (<c>Fits</c>) when the given one refuses them;
    /// <c>null</c> when the opened NPC has no such crafting.
    /// </returns>
    public (CraftingResult? Error, byte SuccessRate, int Price, byte? Fits)? PreviewMix(Player player, byte mixTypeId)
    {
        var crafting = player.OpenedNpc?.Definition.ItemCraftings.FirstOrDefault(c => c.Number == mixTypeId);
        if (crafting is null || player.TemporaryStorage is null)
        {
            return null;
        }

        return BaseItemCraftingHandler.Preview<(CraftingResult? Error, byte SuccessRate, int Price, byte? Fits)?>(() =>
        {
            var craftingHandler = this.GetCraftingHandler(crafting);
            if (craftingHandler.TryGetRequiredItems(player, out var items, out var successRate) is { } error)
            {
                var fits = this.FindAppropriateCraftingByItems(player)?.Number;
                return (error, 0, 0, fits == mixTypeId ? null : fits);
            }

            var price = craftingHandler is BaseItemCraftingHandler baseHandler ? baseHandler.GetPriceOf(successRate, items) : 0;
            return (null, successRate, price, null);
        });
    }

    private IItemCraftingHandler GetCraftingHandler(ItemCrafting crafting)
    {
        if (!this._craftingHandlerCache.TryGetValue(crafting, out var craftingHandler))
        {
            craftingHandler = this.CreateCraftingHandler(crafting);
            this._craftingHandlerCache.Add(crafting, craftingHandler);
        }

        return craftingHandler;
    }

    private IItemCraftingHandler CreateCraftingHandler(ItemCrafting crafting)
    {
        if (!string.IsNullOrWhiteSpace(crafting.ItemCraftingHandlerClassName))
        {
            var type = Type.GetType(crafting.ItemCraftingHandlerClassName);
            if (type != null)
            {
                if (type.BaseType == typeof(SimpleItemCraftingHandler))
                {
                    return (IItemCraftingHandler)Activator.CreateInstance(type, crafting.SimpleCraftingSettings)!;
                }

                return (IItemCraftingHandler)Activator.CreateInstance(type)!;
            }

            throw new ArgumentException($"Item crafting handler '{crafting.ItemCraftingHandlerClassName}' not found.", nameof(crafting));
        }

        if (crafting.SimpleCraftingSettings != null)
        {
            return new SimpleItemCraftingHandler(crafting.SimpleCraftingSettings);
        }

        throw new ArgumentException("No simple crafting settings or item crafting handler name specified.", nameof(crafting));
    }
}