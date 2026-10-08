// <copyright file="MUIdleStacks.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Properties;
using MUnique.OpenMU.GameLogic.Views.Inventory;

/// <summary>
/// Splitting a stack in the inventory (D28): potions and jewels stack far (MUIdleStackSizesPlugInSeason6),
/// and the mixes take a set number of pieces - one Jewel of Chaos, the soul a mix asks for - so the player
/// needs a stack of exactly that. The pieces move to a free square as a stack of their own; nothing is
/// created or lost (the source keeps the rest).
/// </summary>
internal static class MUIdleStacks
{
    /// <summary>
    /// Splits <paramref name="amount"/> pieces off the stack in <paramref name="slot"/> onto a free square.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="slot">The inventory slot of the stack.</param>
    /// <param name="amount">The pieces to split off: at least one, fewer than the stack holds.</param>
    /// <returns><c>true</c>, if split.</returns>
    public static async ValueTask<bool> SplitAsync(Player player, byte slot, int amount)
    {
        if (player.PlayerState.CurrentState != PlayerState.EnteredWorld
            || player.Inventory is not { } inventory
            || slot < InventoryConstants.EquippableSlotsCount
            || slot >= InventoryConstants.FirstStoreItemSlotIndex
            || inventory.GetItem(slot) is not { } stack
            || !stack.IsStackable()
            || amount < 1
            || amount >= stack.Durability)
        {
            return false;
        }

        if (inventory.CheckInvSpace(stack) is not { } freeSlot)
        {
            await player.ShowLocalizedBlueMessageAsync(nameof(PlayerMessage.InventoryFull)).ConfigureAwait(false);
            return false;
        }

        var piece = player.PersistenceContext.CreateNew<Item>();
        piece.Definition = stack.Definition;
        piece.Level = stack.Level;
        piece.Durability = amount;
        piece.ItemSlot = freeSlot;
        if (!await inventory.AddItemAsync(freeSlot, piece).ConfigureAwait(false))
        {
            await player.PersistenceContext.DeleteAsync(piece).ConfigureAwait(false);
            return false;
        }

        stack.Durability -= amount;
        await player.InvokeViewPlugInAsync<IItemDurabilityChangedPlugIn>(p => p.ItemDurabilityChangedAsync(stack, false)).ConfigureAwait(false);
        await player.InvokeViewPlugInAsync<IItemAppearPlugIn>(p => p.ItemAppearAsync(piece)).ConfigureAwait(false);
        player.Logger.LogDebug("MUIdle: {Player} split {Amount} of {Item} off slot {Slot} onto slot {FreeSlot}.", player.Name, amount, stack.Definition?.Name, slot, freeSlot);
        return true;
    }
}
