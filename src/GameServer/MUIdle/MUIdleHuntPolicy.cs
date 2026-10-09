// <copyright file="MUIdleHuntPolicy.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// What the navigator may do for a HUNT, offline (<see cref="HuntOfflinePlayer"/>) or online
/// (<see cref="OnlineHunt"/>, HUNT v2): one policy from the character's idle settings and the hunt's
/// statistics, so both hunts choose maps, errands and shopping the same way.
/// </summary>
internal static class MUIdleHuntPolicy
{
    /// <summary>
    /// Creates the navigator policy of a hunt.
    /// </summary>
    /// <param name="hunter">The hunter.</param>
    /// <param name="settings">The idle settings.</param>
    /// <param name="statistics">The statistics of the hunt (map earnings, deaths).</param>
    /// <returns>The policy.</returns>
    public static BotNavigatorPolicy Create(IHunter hunter, MUIdleSettings settings, OfflineSessionStatistics statistics)
    {
        var player = hunter.Player;
        return new()
        {
            FreeSupplies = false,
            GearProgression = false,
            Resets = false,
            Parties = false,
            Revenge = false,
            BuffTrips = true,

            // HUNT v2: maps and grounds by the experience per minute they promise (HuntingSpotRating).
            RateByExperience = true,

            // Manual: never changes maps. Auto: the navigator's own choice. A pinned map: there, when it can.
            WarpBetweenMaps = settings.AutoMapSelection || settings.PreferredMap is not null,
            PinnedMap = settings.AutoMapSelection ? null : settings.PreferredMap,
            ActivityChanged = (activity, map) => MUIdleHuntTracker.Report(player, MUIdleHuntTracker.FromBot(activity), map),
            // HUNT v2: what the character really earned per map, over its hunts (MUIdleMapMemory).
            BetterProvenMap = settings.AutoMapSelection ? name => MUIdleMapMemory.BetterProvenMap(player, statistics, name) : null,
            MeasuredExperiencePerMinute = name => MUIdleMapMemory.Measured(player, statistics, name),
            Rally = settings.BossesFirst || settings.QuestItemsFirst ? () => Rally(hunter, settings, statistics) : null,
            PreferredMonsterLevels = settings is { AutoMapSelection: true, QuestItemsFirst: true } ? () => MUIdleQuests.MissingItemBand(player) : null,

            // A live world boss of its level comes first (D19): the navigator checks errands before its warps.
            SpendingReserve = () => MUIdleEconomy.Reserve(player),
            FundsLow = () => MUIdleEconomy.FundsLow(player),
            AvoidMap = name => statistics.IsDeadly(name)
                               || MUIdleHuntDeaths.IsDeadly(player, name)
                               || statistics.IsUnsustainable(name, player.Money, MUIdleEconomy.Reserve(player))
                               || (MUIdleEconomy.FundsLow(player) && statistics.IsLosing(name, MUIdleEconomy.FeePerMinute(player))),
            Errand = settings.QuestsAuto
                ? () => settings.BossesFirst && MUIdleWorldBossPlugIn.LiveBossFor(player) is not null ? null : MUIdleQuests.ErrandFor(player)
                : null,
            SellJunk = settings.AutoSell,
            BuyPotions = settings.AutoBuyPotions,
            RepairAtMerchant = settings.AutoRepair,
            DestroyUnsellable = false,
            IsJunk = settings.IsJunk,
        };
    }

    /// <summary>
    /// The rally point of a hunt: a live world boss of the character's level first (bosses first), else
    /// the monster which alone drops a missing item of the active class quest (quest items first) - its
    /// spawn, and the monster itself as the priority target once it stands there.
    /// </summary>
    /// <param name="hunter">The hunter.</param>
    /// <param name="settings">The idle settings.</param>
    /// <param name="statistics">The statistics of the hunt.</param>
    /// <returns>The map and spot, or <c>null</c>.</returns>
    public static (int Map, Point Spot)? Rally(IHunter hunter, MUIdleSettings settings, OfflineSessionStatistics statistics)
    {
        var player = hunter.Player;
        if (settings.BossesFirst && WorldBossRally(hunter) is { } boss)
        {
            return boss;
        }

        hunter.PriorityTarget = null;
        if (!settings.QuestItemsFirst || MUIdleQuests.MissingItemMonster(player) is not { } target)
        {
            return null;
        }

        // Not to a map it keeps dying on (as the map choice avoids it): back to farming meanwhile.
        if (statistics.IsDeadly(player.GameContext.Configuration.Maps.FirstOrDefault(m => m.Number == target.Map)?.Name.ValueInNeutralLanguage)
            || MUIdleHuntDeaths.IsDeadly(player, target.Map))
        {
            return null;
        }

        if (player.CurrentMap is { } map && map.Definition.Number == target.Map)
        {
            hunter.PriorityTarget = map.GetAttackablesInRange(target.Spot, 30)
                .OfType<Monster>()
                .FirstOrDefault(m => m.IsAlive && m.Definition == target.Monster);
        }

        return (target.Map, target.Spot);
    }

    /// <summary>
    /// Gets where a live world boss of the character's level is, if any, and makes it the priority target.
    /// </summary>
    /// <param name="hunter">The hunter.</param>
    /// <returns>The boss map and position.</returns>
    public static (int Map, Point Spot)? WorldBossRally(IHunter hunter)
    {
        var boss = MUIdleWorldBossPlugIn.LiveBossFor(hunter.Player);

        // The combat handler hits the boss first and does not shy away from its long fight.
        hunter.PriorityTarget = boss?.Monster;
        return boss is { } b ? (b.Definition.MapNumber, b.Monster.Position) : null;
    }
}
