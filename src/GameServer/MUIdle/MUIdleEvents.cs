// <copyright file="MUIdleEvents.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.PlayerActions.MiniGames;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using MUnique.OpenMU.GameServer.RemoteView;

/// <summary>
/// Why a character cannot join an event now.
/// </summary>
internal enum EventRefusal
{
    /// <summary>No event level fits the character's level.</summary>
    Level,

    /// <summary>No valid ticket in the bag.</summary>
    Ticket,

    /// <summary>Not enough zen for the entrance fee.</summary>
    Zen,

    /// <summary>The event level asks for a master class.</summary>
    Master,

    /// <summary>An equipped item the event does not allow (Chaos Castle: mounts, transformation rings).</summary>
    Equipment,
}

/// <summary>
/// Event entry for hunting characters: when Blood Castle, Devil Square or Chaos Castle opens, a
/// character with HUNT on (online with its MU Helper running, or hunting offline) which is enrolled
/// and ready (ticket, level, fee) enters it through the player's own entry action - the same checks,
/// the same ticket and fee as at the NPC. Inside, the offline navigator fights the event's monsters;
/// afterwards the hunt goes on. One attempt per event and character per opening.
/// </summary>
internal static class MUIdleEvents
{
    /// <summary>The events MUIdle joins: the scheduled ones with a ticket.</summary>
    public static readonly MiniGameType[] Joinable = [MiniGameType.BloodCastle, MiniGameType.DevilSquare, MiniGameType.ChaosCastle];

    /// <summary>The log category of the entries (shares the hunt's, which is Debug-enabled).</summary>
    private const string LogCategory = "MUnique.OpenMU.GameServer.MUIdle.Hunt";

    /// <summary>Longer than an event's entry window: one attempt per opening.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);

    private static readonly EnterMiniGameAction EnterAction = new();

    private static readonly WarpAction WarpAction = new();

    private static readonly ConditionalWeakTable<Player, HashSet<object>> Rallied = new();

    private static readonly ConditionalWeakTable<Player, ConcurrentDictionary<MiniGameType, DateTime>> Attempts = new();

    /// <summary>
    /// Gets the wire key of an event (the client's schedule keys).
    /// </summary>
    /// <param name="type">The event type.</param>
    /// <returns>The key.</returns>
    public static string Key(MiniGameType type) => type switch
    {
        MiniGameType.BloodCastle => "bloodCastle",
        MiniGameType.DevilSquare => "devilSquare",
        MiniGameType.ChaosCastle => "chaosCastle",
        _ => type.ToString(),
    };

    /// <summary>
    /// Judges an event for the player: the event level that fits, and why it cannot enter now.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="type">The event type.</param>
    /// <returns>The fitting definition (if any) and the refusal (if any).</returns>
    public static (MiniGameDefinition? Definition, EventRefusal? Refusal) Judge(Player player, MiniGameType type)
        => Judge(player, type, out _);

    /// <summary>
    /// Judges an event for the player, naming the equipped item it refuses, if that is the reason.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="type">The event type.</param>
    /// <param name="forbiddenItem">The name of the equipped item the event refuses.</param>
    /// <returns>The fitting definition (if any) and the refusal (if any).</returns>
    public static (MiniGameDefinition? Definition, EventRefusal? Refusal) Judge(Player player, MiniGameType type, out string? forbiddenItem)
    {
        forbiddenItem = null;
        if (player.SelectedCharacter is not { CharacterClass: { } characterClass } character)
        {
            return (null, EventRefusal.Level);
        }

        var level = (int)(player.Attributes?[Stats.Level] ?? 0);
        var special = character.IsSpecialCharacter();
        var definition = player.GameContext.Configuration.MiniGameDefinitions
            .Where(d => d.Type == type)
            // A master-only level that spans every level (Devil Square 7) must not hide that the
            // character is simply below the normal levels: those come first.
            .OrderBy(d => d.RequiresMasterClass)
            .FirstOrDefault(d => special
                ? level >= d.MinimumSpecialCharacterLevel && level <= d.MaximumSpecialCharacterLevel
                : level >= d.MinimumCharacterLevel && level <= d.MaximumCharacterLevel);
        if (definition is null)
        {
            return (null, EventRefusal.Level);
        }

        if (definition.RequiresMasterClass && !characterClass.IsMasterClass)
        {
            // Below the first normal level, "level" is the honest reason, not "master class".
            var belowNormal = player.GameContext.Configuration.MiniGameDefinitions
                .Where(d => d.Type == type && !d.RequiresMasterClass)
                .Any(d => level < (special ? d.MinimumSpecialCharacterLevel : d.MinimumCharacterLevel));
            return (definition, belowNormal ? EventRefusal.Level : EventRefusal.Master);
        }

        if (definition.TicketItem is { } ticket
            && player.Inventory?.FindItemsByDefinition(ticket).Any(i => i.Durability > 0 && i.Level == definition.TicketItemLevel) is not true)
        {
            return (definition, EventRefusal.Ticket);
        }

        if (Math.Max(definition.EntranceFee, 0) > player.Money)
        {
            return (definition, EventRefusal.Zen);
        }

        if (type == MiniGameType.ChaosCastle
            && player.Inventory?.EquippedItems.FirstOrDefault(IsForbiddenInChaosCastle) is { } forbidden)
        {
            forbiddenItem = forbidden.Definition?.Name.ValueInNeutralLanguage;
            return (definition, EventRefusal.Equipment);
        }

        return (definition, null);
    }

    /// <summary>
    /// The rule of upstream's ChaosCastleContext.IsItemAllowedToEquip, judged before the entry (whose
    /// refusal an offline hunter never sees): no Uniria, Dinorant, Fenrir or transformation ring.
    /// </summary>
    private static bool IsForbiddenInChaosCastle(Item item)
    {
        if (item.Definition is not { } definition)
        {
            return false;
        }

        var identifier = new ItemIdentifier(definition.Number, definition.Group);
        return identifier == ItemConstants.Uniria
               || identifier == ItemConstants.Dinorant
               || identifier == ItemConstants.Fenrir
               || item.IsTransformationRing();
    }

    /// <summary>
    /// Gets the events for the client's Events window.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="settings">The idle settings of the character.</param>
    /// <returns>The events, serializable to JSON.</returns>
    public static IEnumerable<object> Info(Player player, MUIdleSettings settings)
    {
        foreach (var type in Joinable)
        {
            var (_, refusal) = Judge(player, type, out var forbiddenItem);
            yield return new
            {
                key = Key(type),
                enrolled = !settings.EventOptOut.Contains(Key(type)),
                ready = refusal is null,
                reason = refusal switch
                {
                    EventRefusal.Ticket => "ticket",
                    EventRefusal.Zen => "zen",
                    EventRefusal.Master => "master",
                    EventRefusal.Level => "level",
                    EventRefusal.Equipment => "equipment",
                    _ => null,
                },
                item = forbiddenItem,
            };
        }
    }

    /// <summary>
    /// Enters open events for the hunting characters which are enrolled and ready.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    public static async ValueTask EnrolAsync(IGameContext gameContext)
    {
        foreach (var player in await gameContext.GetPlayersAsync().ConfigureAwait(false))
        {
            try
            {
                await TryEnrolAsync(gameContext, player).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                player.Logger.LogWarning(ex, "MUIdle: event entry of {Player} failed.", player);
            }
        }
    }

    /// <summary>
    /// Sends players with a client and HUNT on to a live world boss of their level, once per boss:
    /// a warp on a player's terms when it is elsewhere, then a walk to the boss (the offline hunters
    /// go there through their navigator's rally point).
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    public static async ValueTask RallyAsync(IGameContext gameContext)
    {
        foreach (var player in (await gameContext.GetPlayersAsync().ConfigureAwait(false)).OfType<RemotePlayer>())
        {
            try
            {
                await TryRallyAsync(player).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                player.Logger.LogWarning(ex, "MUIdle: world boss rally of {Player} failed.", player);
            }
        }
    }

    private static async ValueTask TryRallyAsync(RemotePlayer player)
    {
        if (!IsHunting(player)
            || player.CurrentMiniGame is not null
            || !player.IsAlive
            || player.PlayerState.CurrentState != PlayerState.EnteredWorld
            || player.SelectedCharacter is not { } character
            || MUIdleHuntTracker.GetSettings(player) is not { BossesFirst: true }
            || MUIdleWorldBossPlugIn.LiveBossFor(player.GameContext, player.Level) is not { } boss
            || !Rallied.GetValue(player, _ => new HashSet<object>()).Add(boss.Monster))
        {
            return;
        }

        var spot = boss.Monster.Position;
        var name = boss.Definition.Name;
        if (player.CurrentMap is { } here
            && here.Definition.Number == boss.Definition.MapNumber
            && MUIdleHuntMaps.IsWalkable(here, player.Position, spot))
        {
            if (player.Position.EuclideanDistanceTo(spot) > 15)
            {
                MUIdleHuntTracker.Report(player, HuntActivityKind.Travelling);
                var state = await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false);
                await MUIdleSessions.SendStateAsync(player, state, resume: true, ground: spot).ConfigureAwait(false);
            }

            return;
        }

        // Through the warp whose landing walks to the boss: another floor of the same map is no way there.
        if (MUIdleHuntMaps.Evaluate(player, boss.Definition.MapNumber) is not { Refusal: null } target
            || await player.GameContext.GetMapAsync((ushort)boss.Definition.MapNumber).ConfigureAwait(false) is not { } bossMap
            || MUIdleHuntMaps.ReachingWarp(player, bossMap, spot) is not { } warp)
        {
            return;
        }

        player.GameContext.LoggerFactory.CreateLogger(LogCategory).LogDebug("MUIdle hunt {Character}: going to the world boss {Boss} on {Map}.", player.Name, name, target.Map.Name);
        MUIdleHuntTracker.Report(player, HuntActivityKind.Warping, target.Map.Name.ValueInNeutralLanguage);
        MUIdleHuntTracker.ExpectPinnedArrival(player, boss.Definition.MapNumber, spot);
        await player.RunPersistenceExclusiveAsync(async () =>
        {
            await WarpAction.WarpToAsync(player, warp).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    private static bool IsHunting(Player player)
        => player is HuntOfflinePlayer
           || (player is RemotePlayer && player.Attributes?[Stats.IsMuHelperActive] > 0);

    private static async ValueTask TryEnrolAsync(IGameContext gameContext, Player player)
    {
        if (!IsHunting(player)
            || player.CurrentMiniGame is not null
            || !player.IsAlive
            || player.PlayerState.CurrentState != PlayerState.EnteredWorld
            || MUIdleHuntTracker.GetSettings(player) is not { EventsFirst: true } settings)
        {
            return;
        }

        var attempts = Attempts.GetValue(player, _ => new ConcurrentDictionary<MiniGameType, DateTime>());
        foreach (var type in Joinable)
        {
            if (settings.EventOptOut.Contains(Key(type))
                || (attempts.TryGetValue(type, out var last) && DateTime.UtcNow - last < RetryAfter))
            {
                continue;
            }

            var (definition, refusal) = Judge(player, type);
            if (definition is null || refusal is not null || !await IsOpenAsync(gameContext, definition).ConfigureAwait(false))
            {
                continue;
            }

            attempts[type] = DateTime.UtcNow;
            gameContext.LoggerFactory.CreateLogger(LogCategory).LogDebug(
                "MUIdle hunt {Character}: entering {Event} (event level {Level}).", player.Name, definition.Name, definition.GameLevel);
            MUIdleHuntTracker.Report(player, HuntActivityKind.Event, definition.Name.ValueInNeutralLanguage);

            // The player's own entry: level, map requirements, ticket (searched in the bag), fee, PK state.
            await player.RunPersistenceExclusiveAsync(async () =>
            {
                await EnterAction.TryEnterMiniGameAsync(player, type, definition.GameLevel, 0xFF).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
            return;
        }
    }

    private static async ValueTask<bool> IsOpenAsync(IGameContext gameContext, MiniGameDefinition definition)
    {
        var strategy = gameContext.PlugInManager.GetStrategy<MiniGameType, IPeriodicMiniGameStartPlugIn>(definition.Type);
        return strategy is not null
               && await strategy.GetDurationUntilNextStartAsync(gameContext, definition).ConfigureAwait(false) == TimeSpan.Zero;
    }
}
