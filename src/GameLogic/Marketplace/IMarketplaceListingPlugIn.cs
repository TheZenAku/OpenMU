// <copyright file="IMarketplaceListingPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Marketplace;

using System.ComponentModel;
using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// A plugin interface which is called before an item of a player goes into a marketplace escrow box.
/// </summary>
[Guid("5D0B7E2A-3C64-4F19-9A8E-6B1F2C7D4E90")]
[PlugInPoint("Marketplace listing", "Plugins which are called before an item of a player is put up on the marketplace. They can keep it in the bag, e.g. while the player has locked it.")]
public interface IMarketplaceListingPlugIn
{
    /// <summary>
    /// Is called before the item leaves the player's bag for an escrow box.
    /// </summary>
    /// <param name="player">The player who lists the item.</param>
    /// <param name="item">The item.</param>
    /// <param name="eventArgs">The listing is refused by setting <see cref="CancelEventArgs.Cancel"/> to <c>true</c>; the item stays in the bag.</param>
    ValueTask ListingAsync(Player player, Item item, CancelEventArgs eventArgs);
}
