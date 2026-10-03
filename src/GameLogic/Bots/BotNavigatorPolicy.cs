// <copyright file="BotNavigatorPolicy.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.Pathfinding;

/// <summary>
/// What a <see cref="BotNavigator"/> (and the <see cref="BotShoppingHandler"/> it drives) may do for
/// the character it navigates. Bots get <see cref="Bot"/>, which is everything; the offline session
/// of a real player's character gets a restricted policy: no free supplies, no automatic gear
/// progression, and nothing of value is ever sold or destroyed without the player's consent.
/// </summary>
public sealed record BotNavigatorPolicy
{
    /// <summary>
    /// Gets the unrestricted policy of the server-side bots.
    /// </summary>
    public static BotNavigatorPolicy Bot { get; } = new();

    /// <summary>
    /// Gets a value indicating whether potions and ammunition are topped up without paying for them.
    /// </summary>
    public bool FreeSupplies { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether looted gear gets equipped, skills learned, wings advanced,
    /// master points spent and jewels applied to gear automatically.
    /// </summary>
    public bool GearProgression { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the character resets when it is due.
    /// </summary>
    public bool Resets { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether party invitations are answered and party leaders followed.
    /// </summary>
    public bool Parties { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether a player killer is hunted down after respawning.
    /// </summary>
    public bool Revenge { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the character warps to other maps which suit its level better.
    /// </summary>
    public bool WarpBetweenMaps { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether free buffs of NPCs (e.g. the Elf Soldier) are claimed.
    /// </summary>
    public bool BuffTrips { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether junk is sold at merchants.
    /// </summary>
    public bool SellJunk { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether potions are bought at merchants.
    /// </summary>
    public bool BuyPotions { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the equipped gear is repaired at merchants.
    /// </summary>
    public bool RepairAtMerchant { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether items which a merchant refused to buy may get destroyed.
    /// </summary>
    public bool DestroyUnsellable { get; init; } = true;

    /// <summary>
    /// Gets the classifier of sellable junk; null for the bot classification, which sells everything
    /// the bot has no use for, treasures included.
    /// </summary>
    public Func<Item, bool>? IsJunk { get; init; }

    /// <summary>
    /// Gets the number of the map to hunt on whenever the bot can get there (legal warp, affordable
    /// fare, entry requirements); <c>null</c> lets the navigator choose. On that map the bot stays: no
    /// stepping down from a barren stretch, no moving up.
    /// </summary>
    public int? PinnedMap { get; init; }

    /// <summary>
    /// Gets a callback told what the bot starts doing, with the map it concerns.
    /// </summary>
    public Action<BotActivity, string?>? ActivityChanged { get; init; }

    /// <summary>
    /// Gets a judge which, given the current map's name, names a map that has earned clearly more
    /// (measured, not guessed); the bot returns there when a player could. <c>null</c>: no such judge.
    /// </summary>
    public Func<string?, string?>? BetterProvenMap { get; init; }

    /// <summary>
    /// Gets a rally point (map number and spot, e.g. a live world boss) the bot goes to - on a
    /// player's terms - and stays at while it is given; <c>null</c> when there is none.
    /// </summary>
    public Func<(int Map, Point Spot)?>? Rally { get; init; }

    /// <summary>
    /// Gets a band of monster levels the bot prefers to hunt while it is given (e.g. a quest item drops
    /// only from monsters of that band): a ground of such safe monsters on its map, else a map that has
    /// them. <c>null</c> when there is none.
    /// </summary>
    public Func<(int Min, int Max)?>? PreferredMonsterLevels { get; init; }

    /// <summary>
    /// Gets a value indicating whether any merchant business is allowed at all.
    /// </summary>
    public bool Shopping => this.SellJunk || this.BuyPotions || this.RepairAtMerchant || this.GearProgression;
}
