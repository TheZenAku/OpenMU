// <copyright file="MUIdleTelemetryPlugIns.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Counts the merchant business and the potions of offline hunters into their statistics. The other
/// statistics come from the hunter's view events (see <see cref="HuntOfflinePlayer"/>).
/// </summary>
[PlugIn]
[Display(Name = "MUIdle telemetry", Description = "Counts sold items, purchases and used potions of offline hunters.")]
[Guid("D9B4F2E6-7A13-4C8B-B5E0-3F6A9C1D2E47")]
public sealed class MUIdleTelemetryPlugIns : IItemSoldToMerchantPlugIn, IItemBoughtFromMerchantPlugIn, IItemConsumedPlugIn
{
    /// <inheritdoc />
    public void ItemSold(Player player, Item item, NonPlayerCharacter merchant)
    {
        if (player is HuntOfflinePlayer hunter)
        {
            hunter.Statistics.AddSoldItem();
        }
    }

    /// <inheritdoc />
    public void ItemBought(Player player, Item item, Item sourceItem, NonPlayerCharacter merchant)
    {
        if (player is HuntOfflinePlayer hunter)
        {
            // The bought item appeared in the backpack like a pickup - it's none.
            hunter.Statistics.RemoveCollectedItem();
        }
    }

    /// <inheritdoc />
    public void ItemConsumed(Player player, Item item, Item? targetItem)
    {
        // Group 14, 0..6: apples and the healing, mana and complex potions.
        if (player is HuntOfflinePlayer hunter && item.Definition is { Group: 14, Number: <= 6 })
        {
            hunter.Statistics.AddPotion();
        }
    }
}
