// <copyright file="MUIdleMarketplaceLockPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.ComponentModel;
using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Marketplace;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Keeps a locked item off the marketplace: the lock protects it from leaving the bag (auto sell,
/// junk), and putting it up for sale is leaving the bag. The player unlocks it first.
/// </summary>
[PlugIn]
[Display(Name = "MUIdle locked items stay off the marketplace", Description = "Refuses to list an item the player locked in the Idle settings.")]
[Guid("E4A2C9B1-7D35-4F86-A0C2-9B5E1D3F7A68")]
public sealed class MUIdleMarketplaceLockPlugIn : IMarketplaceListingPlugIn
{
    /// <inheritdoc />
    public async ValueTask ListingAsync(Player player, Item item, CancelEventArgs eventArgs)
    {
        if (player.SelectedCharacter is not { } character)
        {
            return;
        }

        var state = await player.PersistenceContext.GetMUIdleCharacterStateAsync(character.Id).ConfigureAwait(false);
        if (MUIdleSettings.Parse(state?.SettingsJson).LockedItems.Contains(item.GetId()))
        {
            eventArgs.Cancel = true;
        }
    }
}
