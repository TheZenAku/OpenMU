// <copyright file="MUIdleInventory.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.Properties;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.Persistence;

/// <summary>
/// The inventory window of D33: pages of 8 x 8 squares, and selling and repairing from it anywhere.
/// Page I is the classic inventory, pages II and III its four extensions (two per page) - real
/// inventory slots, so loot, the MU Helper and every NPC dialog see them. Pages IV to VII don't fit
/// the inventory's slots (a byte, and the personal store follows the extensions): they are an item
/// storage of the character's own, where the player puts items and takes them back.
/// </summary>
internal static class MUIdleInventory
{
    /// <summary>The most pages the window offers.</summary>
    public const int MaximumPages = 7;

    /// <summary>The squares of one page.</summary>
    public const int PageSquares = 64;

    private const int ExtensionsPerPage = 2;

    private static readonly ItemPriceCalculator PriceCalculator = new();

    private static readonly ItemRepairAction RepairAction = new();

    /// <summary>The storage pages of a player, loaded once into its persistence context.</summary>
    private static readonly ConditionalWeakTable<Player, ItemStorage> LoadedStorages = new();

    /// <summary>Gets the pages the inventory's own slots hold: the 8 rows, then two extensions of 4 rows each.</summary>
    public static int NativePages { get; } = 1 + (InventoryConstants.MaximumNumberOfExtensions / ExtensionsPerPage);

    /// <summary>Gets the pages kept in the storage of their own (IV to VII).</summary>
    public static int StoragePageCount { get; } = MaximumPages - NativePages;

    /// <summary>
    /// Gets the inventory pages the character has: page I, and one more per two inventory extensions.
    /// </summary>
    /// <param name="character">The character.</param>
    /// <returns>The number of pages.</returns>
    public static int UnlockedPages(Character character)
        => 1 + (Math.Clamp(character.InventoryExtensions, 0, (int)InventoryConstants.MaximumNumberOfExtensions) / ExtensionsPerPage);

    /// <summary>
    /// Gets all pages the character has: the inventory pages and, once all of them, the storage pages bought.
    /// </summary>
    /// <param name="character">The character.</param>
    /// <param name="state">The idle state of the character.</param>
    /// <returns>The number of pages.</returns>
    public static int TotalPages(Character character, MUIdleCharacterState state)
    {
        var native = UnlockedPages(character);
        return native < NativePages ? native : native + Math.Clamp(state.StoragePages, 0, StoragePageCount);
    }

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
    /// Gets the first storage slot of a storage page.
    /// </summary>
    /// <param name="page">The page (IV to VII: 4 to 7).</param>
    /// <returns>The slot within the storage.</returns>
    public static int FirstStorageSlot(int page) => (page - NativePages - 1) * PageSquares;

    /// <summary>
    /// The pages for the idle state: how many the character has, how many there are, their prices,
    /// and the items of the storage pages (the client's item format, as hex).
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="configuration">The configuration.</param>
    /// <param name="state">The idle state of the character.</param>
    /// <returns>The snapshot, serialized with the state.</returns>
    public static async ValueTask<object> SnapshotAsync(RemotePlayer player, MUIdleConfiguration configuration, MUIdleCharacterState state)
    {
        var character = player.SelectedCharacter;
        var storage = await GetStorageAsync(player, state).ConfigureAwait(false);
        var locked = MUIdleSettings.Parse(state.SettingsJson).LockedItems.ToHashSet();
        var serializer = player.ItemSerializer;
        var buffer = new byte[serializer.NeededSpace];
        var items = storage?.Items
            .Where(item => item.Definition is not null)
            .OrderBy(item => item.ItemSlot)
            .Select(item =>
            {
                var length = serializer.SerializeItem(buffer, item);
                return new { slot = (int)item.ItemSlot, data = Convert.ToHexString(buffer, 0, length), locked = locked.Contains(item.GetId()) };
            })
            .ToList();
        return new
        {
            unlocked = character is null ? 1 : TotalPages(character, state),
            native = character is null ? 1 : UnlockedPages(character),
            available = MaximumPages,
            prices = Enumerable.Range(1, MaximumPages).Select(page => PagePrice(configuration, page)).ToArray(),
            storage = items ?? [],
        };
    }

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

        var state = await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false);
        if (page != TotalPages(character, state) + 1 || page > MaximumPages)
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

        if (page <= NativePages)
        {
            character.InventoryExtensions = (page - 1) * ExtensionsPerPage;
            player.Inventory?.AsInventoryStorage?.UpdateExtensions();
        }
        else
        {
            await player.RunPersistenceExclusiveAsync(() =>
            {
                if (state.StoragePagesId is null)
                {
                    var storage = player.PersistenceContext.CreateNew<ItemStorage>();
                    state.StoragePagesId = storage.GetId();
                    LoadedStorages.AddOrUpdate(player, storage);
                }

                state.StoragePages = page - NativePages;
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        }

        player.Logger.LogInformation("MUIdle: {Player} bought inventory page {Page} for {Price} zen.", player.Name, page, price);
        await player.SaveProgressAsync().ConfigureAwait(false);
        await SendResultAsync(player, "page", page, price, null).ConfigureAwait(false);
        await MUIdleSessions.SendStateAsync(player, state).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts an inventory item onto a storage page, at the first place it fits.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="slot">The inventory slot of the item.</param>
    /// <param name="page">The storage page (IV to VII: 4 to 7).</param>
    public static async ValueTask StoreAsync(RemotePlayer player, byte slot, int page)
    {
        if (!CanAct(player) || player.SelectedCharacter is not { } character || player.Inventory is not { } inventory)
        {
            return;
        }

        var state = await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false);
        if (page <= NativePages || page > TotalPages(character, state)
            || !IsBackpackSlot(player, slot)
            || inventory.GetItem(slot) is not { } item
            || await GetStorageAsync(player, state).ConfigureAwait(false) is not { } storage)
        {
            await SendResultAsync(player, "store", 0, 0, "unavailable").ConfigureAwait(false);
            return;
        }

        if (FreeStorageSlot(storage, page, item) is not { } target)
        {
            await SendResultAsync(player, "store", 0, 0, "full").ConfigureAwait(false);
            return;
        }

        // One step for the progress save: the item never stands outside both storages.
        await player.RunPersistenceExclusiveAsync(async () =>
        {
            await inventory.RemoveItemAsync(item).ConfigureAwait(false);
            item.ItemSlot = target;
            storage.Items.Add(item);
        }).ConfigureAwait(false);
        await player.InvokeViewPlugInAsync<IItemRemovedPlugIn>(p => p.RemoveItemAsync(slot)).ConfigureAwait(false);
        await player.SaveProgressAsync().ConfigureAwait(false);
        await MUIdleSessions.SendStateAsync(player, state).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes an item of a storage page back into the inventory, at the first place it fits.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="storageSlot">The slot of the item in the storage.</param>
    public static async ValueTask RetrieveAsync(RemotePlayer player, byte storageSlot)
    {
        if (!CanAct(player) || player.SelectedCharacter is not { } character || player.Inventory is not { } inventory)
        {
            return;
        }

        var state = await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false);
        if (await GetStorageAsync(player, state).ConfigureAwait(false) is not { } storage
            || storage.Items.FirstOrDefault(i => i.ItemSlot == storageSlot) is not { } item)
        {
            return;
        }

        if (inventory.CheckInvSpace(item) is not { } free)
        {
            await player.ShowLocalizedBlueMessageAsync(nameof(PlayerMessage.InventoryFull)).ConfigureAwait(false);
            await SendResultAsync(player, "retrieve", 0, 0, "full").ConfigureAwait(false);
            return;
        }

        var added = await player.RunPersistenceExclusiveAsync(async () =>
        {
            storage.Items.Remove(item);
            item.ItemSlot = free;
            if (await inventory.AddItemAsync(free, item).ConfigureAwait(false))
            {
                return true;
            }

            // Back where it was: nothing may be lost between the two.
            item.ItemSlot = storageSlot;
            storage.Items.Add(item);
            return false;
        }).ConfigureAwait(false);
        if (added)
        {
            await player.InvokeViewPlugInAsync<IItemAppearPlugIn>(p => p.ItemAppearAsync(item)).ConfigureAwait(false);
            await player.SaveProgressAsync().ConfigureAwait(false);
        }

        await MUIdleSessions.SendStateAsync(player, state).ConfigureAwait(false);
    }

    /// <summary>
    /// Sells items at the merchants' price, without a merchant: inventory slots and slots of the storage
    /// pages. Worn and locked items are kept, and so is what no merchant buys.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="slots">The inventory slots.</param>
    /// <param name="junkOnly">Sell an item only while it is still junk: the slots come from a junk preview, and the
    /// bag may have changed since (loot, a move).</param>
    /// <param name="storageSlots">The slots of the storage pages.</param>
    public static async ValueTask SellAsync(RemotePlayer player, IEnumerable<byte> slots, bool junkOnly, IEnumerable<byte>? storageSlots = null)
    {
        if (!CanAct(player) || player.SelectedCharacter is not { } character || player.Inventory is not { } inventory)
        {
            return;
        }

        var state = await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false);
        var settings = MUIdleSettings.Parse(state.SettingsJson);
        var locked = settings.LockedItems.ToHashSet();
        var sold = 0;
        var zen = 0L;
        string? reason = null;
        bool Keep(Item item) => locked.Contains(item.GetId()) || !CanBeSold(item) || (junkOnly && !MUIdleJunk.IsJunk(player, item, settings));

        foreach (var slot in slots.Distinct())
        {
            if (!IsBackpackSlot(player, slot) || inventory.GetItem(slot) is not { } item)
            {
                continue;
            }

            if (Keep(item))
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

        var soldFromStorage = 0;
        if (!junkOnly && reason != "zenFull" && storageSlots?.Distinct().ToList() is { Count: > 0 } wanted
            && await GetStorageAsync(player, state).ConfigureAwait(false) is { } storage)
        {
            foreach (var item in storage.Items.Where(i => wanted.Contains(i.ItemSlot)).ToList())
            {
                if (Keep(item))
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

                await player.RunPersistenceExclusiveAsync(async () =>
                {
                    storage.Items.Remove(item);
                    await player.PersistenceContext.DeleteAsync(item).ConfigureAwait(false);
                }).ConfigureAwait(false);
                soldFromStorage++;
                zen += price;
            }
        }

        sold += soldFromStorage;
        if (sold > 0)
        {
            player.Logger.LogInformation("MUIdle: {Player} sold {Count} item(s) from the inventory window for {Zen} zen.", player.Name, sold, zen);
            await player.SaveProgressAsync().ConfigureAwait(false);
        }

        await SendResultAsync(player, "sell", sold, zen, reason).ConfigureAwait(false);
        if (soldFromStorage > 0)
        {
            await MUIdleSessions.SendStateAsync(player, state).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends the junk of the inventory (<see cref="MUIdleJunk.IsJunk"/>): its slots and what it sells for.
    /// The storage pages are kept: the player put those items there.
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
    /// <see cref="MUIdleJunk.IsJunk"/> calls junk.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="items">The inventory items.</param>
    /// <param name="settings">The idle settings (the locks and the junk rule).</param>
    /// <returns>The junk.</returns>
    public static IEnumerable<Item> Junk(Player player, IEnumerable<Item> items, MUIdleSettings settings)
        => items.Where(item => IsBackpackSlot(player, item.ItemSlot) && CanBeSold(item) && MUIdleJunk.IsJunk(player, item, settings));

    /// <summary>
    /// HUNT sells the junk of the bag where the character stands, at the merchants' price - as the window
    /// does (D33). A merchant trip only came when the bag was nearly full or the potions ran low: a strong
    /// character with a big bag never went, and the bag filled with junk (owner, 10-15).
    /// </summary>
    /// <param name="player">The player (online, or an offline hunter).</param>
    /// <param name="settings">The idle settings.</param>
    /// <returns>The number of items sold.</returns>
    public static async ValueTask<int> SellJunkHereAsync(Player player, MUIdleSettings settings)
    {
        if (!CanAct(player) || player.Inventory is not { } inventory)
        {
            return 0;
        }

        var sold = 0;
        var zen = 0L;
        foreach (var item in Junk(player, inventory.Items.ToList(), settings).ToList())
        {
            var slot = item.ItemSlot;
            var price = (int)PriceCalculator.CalculateSellingPrice(item, item.Durability());
            if (!player.TryAddMoney(price))
            {
                break;
            }

            await inventory.RemoveItemAsync(item).ConfigureAwait(false);
            await player.PersistenceContext.DeleteAsync(item).ConfigureAwait(false);
            await player.InvokeViewPlugInAsync<IItemRemovedPlugIn>(p => p.RemoveItemAsync(slot)).ConfigureAwait(false);
            (player as HuntOfflinePlayer)?.Statistics.AddSoldItem();
            sold++;
            zen += price;
        }

        if (sold > 0)
        {
            player.Logger.LogInformation("MUIdle: HUNT of {Player} sold {Count} junk item(s) for {Zen} zen.", player.Name, sold, zen);
            if (player is RemotePlayer remote)
            {
                await SendResultAsync(remote, "autoSell", sold, zen, null).ConfigureAwait(false);
            }
        }

        return sold;
    }

    /// <summary>
    /// Tells the player which piece HUNT put on (its equipment slot; the client names it).
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="slot">The equipment slot.</param>
    public static ValueTask SendEquippedAsync(RemotePlayer player, byte slot)
        => SendResultAsync(player, "equip", slot, 0, null);

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

    /// <summary>
    /// Finds the first place on a storage page where the item fits (row by row, from the top left).
    /// </summary>
    /// <param name="storage">The storage of the pages IV to VII.</param>
    /// <param name="page">The page (4 to 7).</param>
    /// <param name="item">The item.</param>
    /// <returns>The storage slot; null on a full page.</returns>
    public static byte? FreeStorageSlot(ItemStorage storage, int page, Item item)
    {
        int columns = InventoryConstants.RowSize;
        var rows = PageSquares / columns;
        var first = FirstStorageSlot(page);
        var used = new bool[PageSquares];
        foreach (var other in storage.Items)
        {
            var square = other.ItemSlot - first;
            if (other == item || other.Definition is not { } definition || square < 0 || square >= PageSquares)
            {
                continue;
            }

            for (var y = 0; y < definition.Height && (square / columns) + y < rows; y++)
            {
                for (var x = 0; x < definition.Width && (square % columns) + x < columns; x++)
                {
                    used[square + (y * columns) + x] = true;
                }
            }
        }

        var width = item.Definition?.Width ?? 1;
        var height = item.Definition?.Height ?? 1;
        for (var row = 0; row + height <= rows; row++)
        {
            for (var column = 0; column + width <= columns; column++)
            {
                var fits = true;
                for (var y = 0; y < height && fits; y++)
                {
                    for (var x = 0; x < width && fits; x++)
                    {
                        fits = !used[((row + y) * columns) + column + x];
                    }
                }

                if (fits)
                {
                    return (byte)(first + (row * columns) + column);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the storage of the pages IV to VII, loaded into the player's persistence context once.
    /// </summary>
    private static async ValueTask<ItemStorage?> GetStorageAsync(Player player, MUIdleCharacterState state)
    {
        if (state.StoragePagesId is not { } id)
        {
            return null;
        }

        if (LoadedStorages.TryGetValue(player, out var loaded) && loaded.GetId() == id)
        {
            return loaded;
        }

        try
        {
            var storage = await player.PersistenceContext.GetByIdAsync<ItemStorage>(id).ConfigureAwait(false);
            if (storage is not null)
            {
                LoadedStorages.AddOrUpdate(player, storage);
            }
            else
            {
                player.Logger.LogWarning("MUIdle: the storage pages {Id} of {Player} were not found.", id, player.Name);
            }

            return storage;
        }
        catch (Exception ex)
        {
            // The idle state still goes out (HUNT, the window): the pages read as empty, the items stay in the database.
            player.Logger.LogError(ex, "MUIdle: the storage pages {Id} of {Player} could not be loaded.", id, player.Name);
            return null;
        }
    }

    private static bool IsBackpackSlot(Player player, byte slot)
        => slot >= InventoryConstants.EquippableSlotsCount && slot < player.InventorySize;

    /// <summary>
    /// Selling, moving and buying pages: in the world or at an NPC, never in a trade (its rollback
    /// restores the inventory from a backup).
    /// </summary>
    private static bool CanAct(Player player)
        => player.PlayerState.CurrentState == PlayerState.EnteredWorld
           || player.PlayerState.CurrentState == PlayerState.NpcDialogOpened;

    private static ValueTask SendResultAsync(RemotePlayer player, string action, long count, long zen, string? reason)
        => MUIdlePackets.SendJsonAsync(player, MUIdlePackets.InventoryResultSubCode, JsonSerializer.Serialize(new { action, ok = count > 0, count, zen, reason }));
}
