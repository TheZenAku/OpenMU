// <copyright file="ItemPickupHandler.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Offline;

using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.MuHelper;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;

/// <summary>
/// Handles item and zen pickup for the offline player.
/// </summary>
public sealed class ItemPickupHandler
{
    private const byte MinPickupRange = 1;

    /// <summary>
    /// MUIdle (HUNT v2): the farthest a drop may lie for the pickup action (PickupItemAction refuses
    /// anything farther, and online the player reads "can't pick that up" for each such try).
    /// </summary>
    private const int PickupReach = 3;

    /// <summary>
    /// MUIdle (HUNT v2): how long a drop which the pickup refused is left alone. Online, every refusal
    /// reaches the player's screen as a message, and the handler tries every two ticks a second.
    /// </summary>
    private static readonly TimeSpan RefusedDropPause = TimeSpan.FromSeconds(30);

    private static readonly PickupItemAction PickupAction = new();

    private static readonly HashSet<ItemIdentifier> Jewels =
    [
        ItemConstants.JewelOfChaos,
        ItemConstants.JewelOfBless,
        ItemConstants.JewelOfSoul,
        ItemConstants.JewelOfLife,
        ItemConstants.JewelOfCreation,
        ItemConstants.JewelOfGuardian,
        ItemConstants.Gemstone,
        ItemConstants.JewelOfHarmony,
        ItemConstants.LowerRefineStone,
        ItemConstants.HigherRefineStone,
    ];

    private readonly Player _player;
    private readonly IMuHelperSettings? _config;
    private readonly MovementHandler? _movement;
    private readonly Dictionary<ushort, DateTime> _refusedUntil = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemPickupHandler"/> class.
    /// </summary>
    /// <param name="player">The offline player.</param>
    /// <param name="config">The MU Helper configuration.</param>
    /// <param name="movement">Walks the player to a drop out of reach (the MU Helper's "obtain"); without it, only drops in reach are taken.</param>
    public ItemPickupHandler(Player player, IMuHelperSettings? config, MovementHandler? movement = null)
    {
        this._player = player;
        this._config = config;
        this._movement = movement;
    }

    /// <summary>
    /// Scans for and picks up items within a configurable range.
    /// </summary>
    public async ValueTask PickupItemsAsync()
    {
        if (this._config is null || this._player.CurrentMap is not { } map)
        {
            return;
        }

        if (!this._config.PickAllItems && !this._config.PickSelectItems)
        {
            return;
        }

        byte range = (byte)Math.Max(this._config.ObtainRange, MinPickupRange);
        var drops = map.GetDropsInRange(this._player.Position, range);

        var now = DateTime.UtcNow;
        if (this._refusedUntil.Count > 64)
        {
            foreach (var expired in this._refusedUntil.Where(r => r.Value <= now).Select(r => r.Key).ToList())
            {
                this._refusedUntil.Remove(expired);
            }
        }

        ILocateable? walkTo = null;
        var walkDistance = double.MaxValue;
        foreach (var drop in drops)
        {
            if (!this.ShouldPickUpDrop(drop)
                || (this._refusedUntil.TryGetValue(drop.Id, out var until) && until > now))
            {
                continue;
            }

            // MUIdle (HUNT v2): what the pickup would refuse anyway is not asked for - another's loot
            // while its owner still has the priority, an item without room in the bag.
            if (!this.CanTake(drop))
            {
                this._refusedUntil[drop.Id] = now + RefusedDropPause;
                continue;
            }

            // Out of reach: walked to (the nearest first), then taken.
            var distance = this._player.GetDistanceTo(drop);
            if ((int)distance > PickupReach)
            {
                if (drop is ILocateable located && distance < walkDistance)
                {
                    walkTo = located;
                    walkDistance = distance;
                }

                continue;
            }

            await PickupAction.PickupItemAsync(this._player, drop.Id).ConfigureAwait(false);
            if (map.GetDrop(drop.Id) is not null)
            {
                // Still lying there: refused for a reason of its own (a quest item of another quest...).
                this._refusedUntil[drop.Id] = now + RefusedDropPause;
            }
        }

        if (walkTo is not null && this._movement is not null && !this._player.IsWalking
            && !await this._movement.WalkNearAsync(walkTo.Position).ConfigureAwait(false))
        {
            // No way there (behind a wall): not tried again for a while.
            if (walkTo is IIdentifiable unreachable)
            {
                this._refusedUntil[unreachable.Id] = now + RefusedDropPause;
            }
        }
    }

    private bool CanTake(IIdentifiable drop)
    {
        if (drop is not DroppedItem droppedItem)
        {
            return true;
        }

        if (!droppedItem.IsPlayerAnOwner(this._player) && droppedItem.IsOwnerPickupPriorityActive)
        {
            return false;
        }

        if (this._player.Inventory is not { } inventory || inventory.CheckInvSpace(droppedItem.Item) is not null)
        {
            return true;
        }

        // No free square: a stackable item still goes onto a stack of its kind with room for all of it.
        var item = droppedItem.Item;
        return item.IsStackable() && inventory.Items.Any(item.CanCompletelyStackOn);
    }

    private static bool IsJewel(Item item)
    {
        if (item.Definition is not { } definition)
        {
            return false;
        }

        return Jewels.Contains(new(definition.Number, definition.Group));
    }

    private bool ShouldPickUpDrop(IIdentifiable drop)
    {
        if (this._config!.PickAllItems)
        {
            return true;
        }

        if (!this._config.PickSelectItems)
        {
            return false;
        }

        if (drop is DroppedMoney && this._config.PickZen)
        {
            return true;
        }

        if (drop is DroppedItem droppedItem)
        {
            return this.ShouldPickUp(droppedItem.Item);
        }

        return false;
    }

    private bool ShouldPickUp(Item item)
    {
        if (this._config is null)
        {
            return false;
        }

        if (this._config.PickJewel && IsJewel(item))
        {
            // A human's helper takes every jewel - its owner trades, crafts or hoards them later. A bot
            // has no later: it can only spend Bless, Soul and Life on its own gear, so a Jewel of Chaos
            // or a stock-exceeding Soul is a backpack slot it never gets back.
            return this._player.Account?.IsBot != true
                   || Bots.BotJewelHandler.WantsMoreOf(this._player, item);
        }

        var isAncient = item.ItemSetGroups.Any(s => s.AncientSetDiscriminator != 0);
        var isExcellent = item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.Excellent);
        if ((this._config.PickAncient && isAncient) || (this._config.PickExcellent && isExcellent))
        {
            // A human's helper hoards every excellent/ancient piece - its owner sorts the treasure
            // out later. A bot has no later: it cannot trade, so it only takes what it can actually
            // wear as an upgrade; everything else would silt up its backpack until the loot pickup
            // stops.
            return this._player.Account?.IsBot != true
                   || Bots.BotEquipmentHandler.IsUpgradeFor(this._player, item);
        }

        if (this._config.PickUpgradeItems && Bots.BotEquipmentHandler.IsUpgradeFor(this._player, item))
        {
            // The item is class-qualified gear which beats what the bot currently wears - worth picking
            // up; the BotEquipmentHandler will equip it on one of its next passes.
            return true;
        }

        if (this._config.PickUpgradeItems && this._player.Account?.IsBot == true && Bots.BotSkillHandler.WantsSkillItem(this._player, item))
        {
            // The item is an orb or scroll teaching a skill the bot does not know yet and may currently
            // consume - picked up like a human would; the BotSkillHandler consumes it on its next pass.
            // Gated by the same upgrade-items toggle as gear: with loot progression off, orb and scroll
            // skills stay out of reach, just like better gear does.
            return true;
        }

        if (this._config.PickExtraItems && item.Definition is { } definition)
        {
            return this._config.ExtraItemNames.Any(name => definition.Name.ToString()?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        return false;
    }
}
