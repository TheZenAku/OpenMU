// <copyright file="MUIdleOnlineAutoMap.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameServer.RemoteView;

/// <summary>
/// HUNT with the hunting map on AUTO, for a player with a client: when a clearly better map is open to
/// the character (its safe monsters at least <see cref="HuntingMapRating.ImprovementMargin"/> levels
/// stronger, or the current map has nothing it can safely fight), the hunt moves there on the player's
/// terms - the warp action, its fare and checks - and resumes on a hunting ground, like the offline
/// hunter's navigator does by the same rating.
/// </summary>
internal static class MUIdleOnlineAutoMap
{
    private static readonly WarpAction Warp = new();

    private static readonly ConditionalWeakTable<Player, StrongBox<DateTime>> NextCheckUtc = new();

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

    private static async ValueTask TryMoveAsync(RemotePlayer player, TimeSpan cooldown)
    {
        if (player.PlayerState.CurrentState != PlayerState.EnteredWorld
            || !player.IsAlive
            || player.CurrentMiniGame is not null
            || player.Party is not null
            || player.CurrentMap?.Definition is not { } current
            || !(player.Attributes?[Stats.IsMuHelperActive] > 0)
            || player.IsAtSafezone()
            || MUIdleHuntTracker.GetSettings(player) is not { AutoTravel: true, AutoMapSelection: true, PreferredMap: null } settings)
        {
            return;
        }

        // A live world boss of the character's level: the rally decides where it goes.
        if (settings.BossesFirst && MUIdleWorldBossPlugIn.LiveBossFor(player.GameContext, player.Level) is not null)
        {
            return;
        }

        var box = NextCheckUtc.GetValue(player, _ => new StrongBox<DateTime>(DateTime.MinValue));
        var (due, next) = Schedule(box.Value == DateTime.MinValue ? null : box.Value, DateTime.UtcNow, cooldown);
        box.Value = next;
        if (!due)
        {
            return;
        }

        // A mastered character earns only above the master experience floor: maps judged by what pays first.
        var floor = HuntingMapRating.MasterExperienceFloor(player);
        var options = MUIdleHuntMaps.Options(player)
            .Where(o => o is { Refusal: null, Warp: not null } && o.Map.Number != current.Number)
            .ToList();
        HuntMapOption? Pick(int minimum) => Choose(
            HuntingMapRating.BestSafeLevel(player, current, minimum),
            options.Select(o => (o, HuntingMapRating.BestSafeLevel(player, o.Map, minimum))));
        if (((floor > 0 ? Pick(floor) : null) ?? Pick(0)) is not { Warp: { } warp } target)
        {
            return;
        }

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
        MUIdleHuntTracker.ExpectPinnedArrival(player, target.Map.Number);
        await Warp.WarpToAsync(player, warp).ConfigureAwait(false);
    }
}
