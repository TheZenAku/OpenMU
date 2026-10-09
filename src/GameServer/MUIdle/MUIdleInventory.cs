// <copyright file="MUIdleInventory.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.Persistence;

/// <summary>
/// The inventory window of D33: pages of 8 x 8 squares, and selling and repairing from it anywhere.
/// Page I is the classic inventory, pages II and III its four extensions (two per page) - real
/// inventory slots, so loot, the MU Helper and every NPC dialog see them.
/// </summary>
internal static class MUIdleInventory
{
    /// <summary>The most pages the window offers.</summary>
    public const int MaximumPages = 7;

    private const int ExtensionsPerPage = 2;

    private static readonly ItemPriceCalculator PriceCalculator = new();

    private static readonly ItemRepairAction RepairAction = new();

    /// <summary>Gets the pages the inventory's own slots hold: the 8 rows, then two extensions of 4 rows each.</summary>
    public static int NativePages { get; } = 1 + (InventoryConstants.MaximumNumberOfExtensions / ExtensionsPerPage);

    /// <summary>
    /// Gets the pages the character has: page I, and one more per two inventory extensions.
    /// </summary>
    /// <param name="character">The character.</param>
    /// <returns>The number of pages.</returns>
    public static int UnlockedPages(Character character)
        => 1 + (Math.Clamp(character.InventoryExtensions, 0, (int)InventoryConstants.MaximumNumberOfExtensions) / ExtensionsPerPage);

    /// <summary>
    /// Gets the Zen price of a page; 0 for the free ones.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="page">The page (1-based).</param>
    /// <returns>The price.</returns>
    public static int PagePrice(MUIdleConfiguration configuration, int page)
    {
        if (page <= Math.Max(1, configuration.FreeInventoryPages))
        {
            return 0;
        }

        return Math.Max(0, page switch
        {
            3 => configuration.InventoryPage3Price,
            4 => configuration.InventoryPage4Price,
            5 => configuration.InventoryPage5Price,
            6 => configuration.InventoryPage6Price,
            7 => configuration.InventoryPage7Price,
            _ => 0,
        });
    }

    /// <summary>
    /// The pages for the idle state: how many the character has, how many there are, and their prices.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The snapshot, serialized with the state.</returns>
    public static object Snapshot(Player player, MUIdleConfiguration configuration) => new
    {
        unlocked = player.SelectedCharacter is { } character ? UnlockedPages(character) : 1,
        available = NativePages,
        prices = Enumerable.Range(1, MaximumPages).Select(page => PagePrice(configuration, page)).ToArray(),
    };

    /// <summary>
    /// Gives the character its free pages (two inventory extensions per page), at its entry.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="configuration">The configuration.</param>
    public static void GrantFreePages(Player player, MUIdleConfiguration configuration)
    {
        if (player.SelectedCharacter is not { } character)
        {
            return;
        }

        var extensions = (Math.Clamp(configuration.FreeInventoryPages, 1, NativePages) - 1) * ExtensionsPerPage;
        if (character.InventoryExtensions >= extensions)
        {
            return;
        }

        character.InventoryExtensions = extensions;
        player.Inventory?.AsInventoryStorage?.UpdateExtensions();
    }

    /// <summary>
    /// Buys the next page with Zen.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="page">The page (1-based); only the one after the last unlocked page is sold.</param>
    public static async ValueTask BuyPageAsync(RemotePlayer player, int page)
    {
        if (!CanAct(player) || player.SelectedCharacter is not { } character)
        {
            return;
        }

        if (page != UnlockedPages(character) + 1 || page > NativePages)
        {
            await SendResultAsync(player, "page", 0, 0, "unavailable").ConfigureAwait(false);
            return;
        }

        var price = PagePrice(MUIdleSessions.GetConfiguration(player.GameContext), page);
        if (!player.TryRemoveMoney(price))
        {
            await SendResultAsync(player, "page", 0, 0, "zen").ConfigureAwait(false);
            return;
        }

        character.InventoryExtensions = (page - 1) * ExtensionsPerPage;
        player.Inventory?.AsInventoryStorage?.UpdateExtensions();
        player.Logger.LogInformation("MUIdle: {Player} bought inventory page {Page} for {Price} zen.", player.Name, page, price);
        await player.SaveProgressAsync().ConfigureAwait(false);
        await SendResultAsync(player, "page", page, price, null).ConfigureAwait(false);
        await MUIdleSessions.SendStateAsync(player, await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <summary>
    /// Sells inventory items at the merchants' price, without a merchant. Worn and locked items are
    /// kept, and so is what no merchant buys.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="slots">The inventory slots.</param>
    /// <param name="junkOnly">Sell an item only while it is still junk: the slots come from a junk preview, and the
    /// bag may have changed since (loot, a move).</param>
    public static async ValueTask SellAsync(RemotePlayer player, IEnumerable<byte> slots, bool junkOnly)
    {
        if (!CanAct(player) || player.SelectedCharacter is not { } character || player.Inventory is not { } inventory)
        {
            return;
        }

        var settings = MUIdleSettings.Parse((await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false)).SettingsJson);
        var locked = settings.LockedItems.ToHashSet();
        var sold = 0;
        var zen = 0L;
        string? reason = null;
        foreach (var slot in slots.Distinct())
        {
            if (!IsBackpackSlot(player, slot) || inventory.GetItem(slot) is not { } item)
            {
                continue;
            }

            if (locked.Contains(item.GetId()) || !CanBeSold(item) || (junkOnly && !settings.IsJunk(item)))
            {
                reason ??= "kept";
                continue;
            }

            var price = (int)PriceCalculator.CalculateSellingPrice(item, item.Durability());
            if (!player.TryAddMoney(price))
            {
                reason = "zenFull";
                break;
            }

            await inventory.RemoveItemAsync(item).ConfigureAwait(false);
            await player.PersistenceContext.DeleteAsync(item).ConfigureAwait(false);
            await player.InvokeViewPlugInAsync<IItemRemovedPlugIn>(p => p.RemoveItemAsync(slot)).ConfigureAwait(false);
            sold++;
            zen += price;
        }

        if (sold > 0)
        {
            player.Logger.LogInformation("MUIdle: {Player} sold {Count} item(s) from the inventory for {Zen} zen.", player.Name, sold, zen);
            await player.SaveProgressAsync().ConfigureAwait(false);
        }

        await SendResultAsync(player, "sell", sold, zen, reason).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the junk of the inventory (<see cref="MUIdleSettings.IsJunk"/>): its slots and what it sells for.
    /// </summary>
    /// <param name="player">The player.</param>
    public static async ValueTask SendJunkPreviewAsync(RemotePlayer player)
    {
        if (!CanAct(player) || player.SelectedCharacter is not { } character || player.Inventory is not { } inventory)
        {
            return;
        }

        var settings = MUIdleSettings.Parse((await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false)).SettingsJson);
        var junk = Junk(player, inventory.Items, settings).ToList();
        var json = JsonSerializer.Serialize(new
        {
            slots = junk.Select(item => (int)item.ItemSlot).OrderBy(slot => slot),
            zen = junk.Sum(item => PriceCalculator.CalculateSellingPrice(item, item.Durability())),
        });
        await MUIdlePackets.SendJsonAsync(player, MUIdlePackets.JunkPreviewSubCode, json).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the junk among the items: what lies in the bag (pages I-III), sells, isn't locked, and
    /// <see cref="MUIdleSettings.IsJunk"/> calls junk.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="items">The inventory items.</param>
    /// <param name="settings">The idle settings (the locks and the junk rule).</param>
    /// <returns>The junk.</returns>
    public static IEnumerable<Item> Junk(Player player, IEnumerable<Item> items, MUIdleSettings settings)
        => items.Where(item => IsBackpackSlot(player, item.ItemSlot) && CanBeSold(item) && settings.IsJunk(item));

    /// <summary>
    /// Repairs the worn items, anywhere: at an open merchant for the merchant's price, else for the
    /// inventory's own (2.5 times, as the original's hammer). The pet stays for the pet trainer.
    /// </summary>
    /// <param name="player">The player.</param>
    public static async ValueTask RepairAllAsync(RemotePlayer player)
    {
        if (!CanAct(player) || player.Inventory is not { } inventory)
        {
            return;
        }

        var repaired = 0;
        var spent = 0L;
        string? reason = null;
        var atMerchant = player.OpenedNpc is not null;
        for (var slot = InventoryConstants.FirstEquippableItemSlotIndex; slot <= InventoryConstants.LastEquippableItemSlotIndex; slot++)
        {
            if (slot == InventoryConstants.PetSlot
                || inventory.GetItem(slot) is not { } item
                || !item.CanBeRepaired()
                || (int)item.Durability >= item.GetMaximumDurabilityOfOnePiece())
            {
                continue;
            }

            if (player.Money < PriceCalculator.CalculateRepairPrice(item, atMerchant))
            {
                reason = "zen";
                break;
            }

            var before = player.Money;
            await RepairAction.RepairItemAsync(player, slot).ConfigureAwait(false);
            if (player.Money < before)
            {
                repaired++;
                spent += before - player.Money;
            }
        }

        await SendResultAsync(player, "repair", repaired, spent, reason ?? (repaired == 0 ? "nothing" : null)).ConfigureAwait(false);
    }

    /// <summary>
    /// Determines whether a merchant would buy the item (SellItemToNpcAction's rules).
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns><c>true</c>, if it sells.</returns>
    public static bool CanBeSold(Item item)
        => item.Definition is { IsSellableToNpc: true } definition
           && !(definition.IsBoundToCharacter && (definition.Durability == 0 || item.Durability > 0));

    private static bool IsBackpackSlot(Player player, byte slot)
        => slot >= InventoryConstants.EquippableSlotsCount && slot < player.InventorySize;

    /// <summary>
    /// Selling and buying pages: in the world or at an NPC, never in a trade (its rollback restores the
    /// inventory from a backup).
    /// </summary>
    private static bool CanAct(Player player)
        => player.PlayerState.CurrentState == PlayerState.EnteredWorld
           || player.PlayerState.CurrentState == PlayerState.NpcDialogOpened;

    private static ValueTask SendResultAsync(RemotePlayer player, string action, long count, long zen, string? reason)
        => MUIdlePackets.SendJsonAsync(player, MUIdlePackets.InventoryResultSubCode, JsonSerializer.Serialize(new { action, ok = count > 0, count, zen, reason }));
}
