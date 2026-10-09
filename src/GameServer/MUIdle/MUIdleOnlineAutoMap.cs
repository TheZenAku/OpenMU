// <copyright file="MUIdleOnlineAutoMap.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// HUNT with the hunting map on AUTO, for a player with a client: when a clearly better map is open to
/// the character (its safe monsters at least <see cref="HuntingMapRating.ImprovementMargin"/> levels
/// stronger, or the current map has nothing it can safely fight), the hunt moves there on the player's
/// terms - the warp action, its fare and checks - and resumes on a hunting ground, like the offline
/// hunter's navigator does by the same rating.
/// </summary>
internal static class MUIdleOnlineAutoMap
{
    private const string MapNote = "map";

    private const string QuestMonsterNote = "questMonster";

    private static readonly WarpAction Warp = new();

    private static readonly ConditionalWeakTable<Player, StrongBox<DateTime>> NextCheckUtc = new();

    private static readonly ConditionalWeakTable<Player, StrongBox<DateTime>> NextQuestStepUtc = new();

    /// <summary>
    /// Moves the online AUTO hunters of the game context to a better map where one is due.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    /// <param name="configuration">The MUIdle configuration.</param>
    public static async ValueTask MaintainAsync(IGameContext gameContext, MUIdleConfiguration configuration)
    {
        if (configuration.OnlineAutoMapMinutes <= 0)
        {
            return;
        }

        var cooldown = TimeSpan.FromMinutes(configuration.OnlineAutoMapMinutes);
        foreach (var player in (await gameContext.GetPlayersAsync().ConfigureAwait(false)).OfType<RemotePlayer>())
        {
            // HUNT v2: an online hunt's navigator chooses the maps itself.
            if (OnlineHunt.Of(player) is not null)
            {
                continue;
            }

            try
            {
                await TryMoveAsync(player, cooldown).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                player.Logger.LogWarning(ex, "MUIdle: online AUTO map change of {Player} failed.", player);
            }
        }
    }

    /// <summary>
    /// Picks the map to move to: the strongest by safe monster level which clears the margin over the
    /// current one (any with something safe when the current has nothing); <c>null</c> to stay.
    /// </summary>
    /// <typeparam name="T">The type of the candidates.</typeparam>
    /// <param name="hereLevel">The safe monster level of the current map.</param>
    /// <param name="candidates">The other maps open to the character, with their safe monster level.</param>
    /// <returns>The map to move to, or <c>null</c>.</returns>
    internal static T? Choose<T>(int hereLevel, IEnumerable<(T Option, int Level)> candidates)
        where T : class
    {
        var threshold = hereLevel <= 0 ? 1 : hereLevel + HuntingMapRating.ImprovementMargin;
        return candidates
            .Where(c => c.Level >= threshold)
            .OrderByDescending(c => c.Level)
            .Select(c => c.Option)
            .FirstOrDefault();
    }

    /// <summary>
    /// Whether a check is due, and schedules the next one: the first sight of a player only starts the
    /// clock (a player just in the world settles first), then every <paramref name="cooldown"/>.
    /// </summary>
    /// <param name="next">The time of the next check; <c>null</c> on the first sight.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cooldown">The time between two checks.</param>
    /// <returns>Whether to check now, and the time of the next check.</returns>
    internal static (bool Due, DateTime Next) Schedule(DateTime? next, DateTime now, TimeSpan cooldown)
    {
        if (next is null)
        {
            return (false, now + (cooldown / 5));
        }

        return now < next ? (false, next.Value) : (true, now + cooldown);
    }

    private static async ValueTask GoToQuestMonsterAsync(RemotePlayer player, GameMapDefinition current, (MonsterDefinition Monster, short Map, Point Spot) target)
    {
        if (current.Number == target.Map)
        {
            if (player.Position.EuclideanDistanceTo(target.Spot) > 20 && player.SelectedCharacter is { } character)
            {
                MUIdleHuntTracker.Report(player, HuntActivityKind.Travelling, current.Name.ValueInNeutralLanguage);
                var state = await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false);
                await MUIdleSessions.SendStateAsync(player, state, resume: true, ground: target.Spot).ConfigureAwait(false);
            }

            return;
        }

        if (await player.GameContext.GetMapAsync((ushort)target.Map).ConfigureAwait(false) is not { } map
            || MUIdleHuntMaps.ReachingWarp(player, map, target.Spot) is not { } warp
            || player.Money - warp.Costs < MUIdleEconomy.Reserve(player))
        {
            return;
        }

        player.Logger.LogInformation(
            "MUIdle: HUNT (AUTO) takes {Character} to {Monster} on {Map} for a quest item.",
            player.Name,
            target.Monster.Designation.ValueInNeutralLanguage,
            map.Definition.Name.ValueInNeutralLanguage);
        MUIdleHuntTracker.Report(player, HuntActivityKind.Warping, map.Definition.Name.ValueInNeutralLanguage);
        MUIdleHuntTracker.ExpectPinnedArrival(player, target.Map, target.Spot);
        await Warp.WarpToAsync(player, warp).ConfigureAwait(false);
    }

    /// <summary>
    /// The map the AUTO choice goes to from the open options: a map with monsters of the quest item band,
    /// else the strongest safe map that pays more (<see cref="Choose{T}"/>); dying here, one step down.
    /// </summary>
    private static HuntMapOption? PickTarget(Player player, GameMapDefinition current, List<HuntMapOption> options, (int Min, int Max)? band, bool deadlyHere)
    {
        if (band is { } wanted)
        {
            return options
                .Select(o => (Option: o, Level: HuntingMapRating.BestSafeLevelInBand(player, o.Map, wanted.Min, wanted.Max)))
                .Where(x => x.Level > 0)
                .OrderByDescending(x => x.Level)
                .Select(x => x.Option)
                .FirstOrDefault();
        }

        // A mastered character earns only above the master experience floor: maps judged by what pays first.
        var floor = HuntingMapRating.MasterExperienceFloor(player);
        HuntMapOption? Pick(int minimum) => Choose(
            HuntingMapRating.BestSafeLevel(player, current, minimum),
            options.Select(o => (o, HuntingMapRating.BestSafeLevel(player, o.Map, minimum))));
        var target = (floor > 0 ? Pick(floor) : null) ?? Pick(0);
        if (target is not null || !deadlyHere)
        {
            return target;
        }

        // Dying here: one step down - the strongest safe map below this one.
        var here = HuntingMapRating.BestSafeLevel(player, current, 0);
        return options
            .Select(o => (Option: o, Level: HuntingMapRating.BestSafeLevel(player, o.Map, 0)))
            .Where(x => x.Level > 0 && (here == 0 || x.Level < here))
            .OrderByDescending(x => x.Level)
            .Select(x => x.Option)
            .FirstOrDefault();
    }

    private static async ValueTask TryMoveAsync(RemotePlayer player, TimeSpan cooldown)
    {
        if (player.PlayerState.CurrentState != PlayerState.EnteredWorld
            || !player.IsAlive
            || player.CurrentMiniGame is not null
            || player.Party is not null
            || player.CurrentMap?.Definition is not { } current
            || !(player.Attributes?[Stats.IsMuHelperActive] > 0)
            || player.IsAtSafezone()
            || MUIdleHuntTracker.GetSettings(player) is not { AutoTravel: true } settings
            || (!settings.AutoMapSelection && settings.PreferredMap is null))
        {
            // MANUAL stays on its map.
            return;
        }

        // A live world boss of the character's level: the rally decides where it goes. A quest errand
        // (accept or hand in at the giver) comes before the map choice too (D19).
        if ((settings.BossesFirst && MUIdleWorldBossPlugIn.LiveBossFor(player) is not null) || MUIdleOnlineQuests.Due(player) is not null)
        {
            return;
        }

        // A quest item only one monster drops: to that monster's spawn (its map, then the client walks) -
        // before the farming map, pinned or AUTO (D19), at most once a minute.
        // Not on a map the character keeps dying on (3 deaths in 30 minutes, D20): back to farming,
        // and the HUD says why - else every respawn would walk it into the same death.
        var questMonster = settings.QuestItemsFirst ? MUIdleQuests.MissingItemMonster(player) : null;
        var questMonsterDeadly = questMonster is { } wantedMonster && MUIdleHuntDeaths.IsDeadly(player, wantedMonster.Map);
        MUIdleHuntTracker.SetNote(player, QuestMonsterNote, questMonsterDeadly ? "questMonsterDeadly" : null);
        if (questMonster is { } monster && !questMonsterDeadly)
        {
            var step = NextQuestStepUtc.GetValue(player, _ => new StrongBox<DateTime>(DateTime.MinValue));
            if (DateTime.UtcNow >= step.Value)
            {
                step.Value = DateTime.UtcNow.AddMinutes(1);
                await GoToQuestMonsterAsync(player, current, monster).ConfigureAwait(false);
            }

            return;
        }

        if (!settings.AutoMapSelection || settings.PreferredMap is not null)
        {
            MUIdleHuntTracker.SetNote(player, MapNote, null);
            return;
        }

        // A map it keeps dying on is left at once, not after the cooldown (D20).
        var deadlyHere = MUIdleHuntDeaths.IsDeadly(player, current.Number);
        var box = NextCheckUtc.GetValue(player, _ => new StrongBox<DateTime>(DateTime.MinValue));
        var (due, next) = Schedule(box.Value == DateTime.MinValue ? null : box.Value, DateTime.UtcNow, cooldown);
        box.Value = next;
        if (!due && !deadlyHere)
        {
            return;
        }

        var open = MUIdleHuntMaps.Options(player)
            .Where(o => o is { Refusal: null, Warp: not null } && o.Map.Number != current.Number)
            .Where(o => !MUIdleHuntDeaths.IsDeadly(player, o.Map.Number))
            .ToList();

        // The missing item of the class quest drops only from monsters of a band: a map that has them.
        var band = settings.QuestItemsFirst ? MUIdleQuests.MissingItemBand(player) : null;
        if (band is { } wanted && HuntingMapRating.BestSafeLevelInBand(player, current, wanted.Min, wanted.Max) > 0)
        {
            MUIdleHuntTracker.SetNote(player, MapNote, null);
            return;
        }

        // A map change leaves the HUNT reserve for the MU Helper fee (D20).
        var reserve = MUIdleEconomy.Reserve(player);
        var target = PickTarget(player, current, open.Where(o => player.Money - o.Fare >= reserve).ToList(), band, deadlyHere);
        if (target is not { Warp: { } warp })
        {
            // Why it stays, on the HUD: the map it would go to costs more than the zen above the reserve,
            // or it keeps dying here and nothing safer is open.
            if (PickTarget(player, current, open, band, deadlyHere) is { } unaffordable)
            {
                MUIdleHuntTracker.SetNote(player, MapNote, "noFare", player.Money, unaffordable.Fare + reserve);
            }
            else
            {
                MUIdleHuntTracker.SetNote(player, MapNote, deadlyHere ? "noSafeMap" : null);
            }

            return;
        }

        MUIdleHuntTracker.SetNote(player, MapNote, null);

        player.Logger.LogInformation(
            "MUIdle: HUNT (AUTO) moves {Character} from {From} (safe monster level {Here}) to {To} (safe monster level {There}).",
            player.Name,
            current.Name.ValueInNeutralLanguage,
            HuntingMapRating.BestSafeLevel(player, current),
            target.Map.Name.ValueInNeutralLanguage,
            HuntingMapRating.BestSafeLevel(player, target.Map));
        MUIdleHuntTracker.Report(player, HuntActivityKind.Warping, target.Map.Name.ValueInNeutralLanguage);

        // As with a pinned map: the map change completes when the client loaded the map, then
        // MUIdleSessions.OnMapEnteredAsync sends the client to a hunting ground and resumes HUNT.
        var ground = band is not null && await player.GameContext.GetMapAsync((ushort)target.Map.Number).ConfigureAwait(false) is { } targetMap
            ? MUIdleHuntMaps.PickGround(player, targetMap, band)
            : null;
        MUIdleHuntTracker.ExpectPinnedArrival(player, target.Map.Number, ground);
        await Warp.WarpToAsync(player, warp).ConfigureAwait(false);
    }
}
