// <copyright file="MUIdleSessions.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence;

/// <summary>
/// The HUNT session lifecycle:
/// <list type="number">
///   <item>A player in HUNT mode (MU Helper running) disconnects: the intent and the start values are
///     written into the character's <see cref="MUIdleCharacterState"/> right before its session is saved.</item>
///   <item>Once the player's session is saved and it left the game, a <see cref="HuntOfflinePlayer"/>
///     loads the character fresh from the database and continues hunting on the server.</item>
///   <item>When the player returns, the login hands the offline session over (upstream
///     <c>LoginAction</c>): the hunter stops and saves its statistics, the account is reloaded.</item>
///   <item>On entering the world, the client gets the offline summary and HUNT resumes.</item>
/// </list>
/// </summary>
internal static class MUIdleSessions
{
    private static readonly ConcurrentDictionary<Player, PendingHunt> Pending = new();

    /// <summary>
    /// Gets the MUIdle configuration of a game context.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    /// <returns>The configuration.</returns>
    public static MUIdleConfiguration GetConfiguration(IGameContext gameContext)
        => gameContext.FeaturePlugIns.GetPlugIn<MUIdleFeaturePlugIn>()?.Configuration ?? new MUIdleConfiguration();

    /// <summary>
    /// Determines whether the game server of the context is shutting down.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    /// <returns><c>true</c>, if it's shutting down.</returns>
    public static bool IsShuttingDown(IGameContext gameContext)
        => gameContext is GameContext { IsDisposed: true } or GameContext { IsDisposing: true } or GameContext { ArePeriodicTasksStopped: true };

    /// <summary>
    /// Keeps a crash's view of an online player persisted (called by the periodic online save): with HUNT
    /// running where it could go on offline, the marker a regular shutdown writes (see
    /// <see cref="OnDisconnectingAsync"/>) - a restart after an unclean end (a killed process, the OS
    /// ending the session, a power cut) brings the character back as an offline hunter; else no marker.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>Whether the character carries the marker now.</returns>
    public static async ValueTask<bool> UpdateCrashMarkerAsync(RemotePlayer player)
    {
        if (player.SelectedCharacter is not { } character || player.IsTemplatePlayer)
        {
            return false;
        }

        var configuration = GetConfiguration(player.GameContext);
        var marked = false;
        await player.RunPersistenceExclusiveAsync(async () =>
        {
            var state = await GetOrCreateStateAsync(player, character).ConfigureAwait(false);
            if (CanContinueOffline(player, configuration))
            {
                state.HuntActive = true;
                MarkContinuesOffline(player, state);
                marked = true;
            }
            else if (state.OfflineSince is not null)
            {
                state.OfflineSince = null;
                state.OfflineStatisticsJson = null;
            }
        }).ConfigureAwait(false);
        return marked;
    }

    /// <summary>
    /// Whether the hunt of an online player would go on offline if it left now. HUNT switched on goes
    /// on wherever the character stands: in town HUNT walks out to a ground (D21) and the offline
    /// hunter does the same - the client's helper pauses there, so its flag says nothing (seen: test0Dl
    /// respawned in Devias on 10-08, left while HUNT walked it out, and no offline hunt followed). The
    /// original's helper without HUNT only goes on outside the safezone, where it does something.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="configuration">The MUIdle configuration.</param>
    /// <returns><c>true</c>, if it would.</returns>
    internal static bool CanContinueOffline(Player player, MUIdleConfiguration configuration)
        => configuration.ContinueHuntingOnDisconnect
           && player.IsAlive
           && player.CurrentMap is not null
           && player.CurrentMiniGame is null
           && HuntGoesOn(MUIdleHuntTracker.HasHuntIntent(player), player.Attributes?[Stats.IsMuHelperActive] > 0, player.IsAtSafezone());

    /// <summary>
    /// The hunt part of <see cref="CanContinueOffline(Player, MUIdleConfiguration)"/>.
    /// </summary>
    /// <param name="huntIntent">Whether the player switched HUNT on.</param>
    /// <param name="helperActive">Whether the client's helper runs.</param>
    /// <param name="inSafezone">Whether the character stands in a safezone.</param>
    /// <returns><c>true</c>, if the hunt goes on offline.</returns>
    internal static bool HuntGoesOn(bool huntIntent, bool helperActive, bool inSafezone)
        => huntIntent || (helperActive && !inSafezone);

    private static OfflineSessionStatistics MarkContinuesOffline(Player player, MUIdleCharacterState state)
    {
        var statistics = new OfflineSessionStatistics
        {
            StartedUtc = DateTime.UtcNow,
            StartLevel = player.Level,
            EndLevel = player.Level,
            StartMasterLevel = (int)(player.Attributes?[Stats.MasterLevel] ?? 0),
            EndMasterLevel = (int)(player.Attributes?[Stats.MasterLevel] ?? 0),
            StartMoney = player.Money,
            EndMoney = player.Money,
        };
        state.OfflineSince = statistics.StartedUtc;
        state.OfflineStatisticsJson = statistics.ToJson();
        return statistics;
    }

    /// <summary>
    /// Called when a real player advanced to <see cref="PlayerState.Disconnected"/>, right before its
    /// session gets saved.
    /// </summary>
    /// <param name="player">The player.</param>
    public static async ValueTask OnDisconnectingAsync(RemotePlayer player)
    {
        if (player.SelectedCharacter is not { } character || player.Account is not { } account || player.IsTemplatePlayer)
        {
            return;
        }

        var helperRunning = player.Attributes?[Stats.IsMuHelperActive] > 0 || MUIdleHuntTracker.HasHuntIntent(player);
        var state = await GetOrCreateStateAsync(player, character).ConfigureAwait(false);
        state.HuntActive = helperRunning;

        var configuration = GetConfiguration(player.GameContext);
        var shuttingDown = IsShuttingDown(player.GameContext);
        if (!CanContinueOffline(player, configuration))
        {
            // No offline hunt follows: a crash marker of the periodic save must not bring it back later.
            state.OfflineSince = null;
            state.OfflineStatisticsJson = null;
            return;
        }

        var statistics = MarkContinuesOffline(player, state);

        if (shuttingDown)
        {
            // The hunt continues after the restart (see RestoreOfflineHuntersAsync).
            player.Logger.LogInformation("MUIdle: {Character} was hunting when the server stopped; it continues after the restart.", character.Name);
            return;
        }

        Pending[player] = new PendingHunt(player.GameContext, account.LoginName, character.Name, MUIdleSettings.Parse(state.SettingsJson), statistics);
    }

    /// <summary>
    /// Called when a real player advanced to <see cref="PlayerState.Finished"/>: its session is saved
    /// and it left the game, so its character may continue as offline hunter.
    /// </summary>
    /// <param name="player">The player.</param>
    public static void OnFinished(Player player)
    {
        if (!Pending.TryRemove(player, out var pending))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await StartHunterAsync(pending.GameContext, pending.LoginName, pending.CharacterName, pending.Settings, pending.Statistics).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                pending.GameContext.LoggerFactory.CreateLogger(typeof(MUIdleSessions)).LogError(ex, "MUIdle: could not start the offline hunt of {Character}.", pending.CharacterName);
            }
        });
    }

    /// <summary>
    /// Starts an offline hunter for a character, unless its account is online again.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    /// <param name="loginName">The login name of the account.</param>
    /// <param name="characterName">The character name.</param>
    /// <param name="settings">The idle settings of the character.</param>
    /// <param name="statistics">The statistics of the session (continued after a restart).</param>
    /// <returns>The started hunter, or null.</returns>
    public static async ValueTask<HuntOfflinePlayer?> StartHunterAsync(IGameContext gameContext, string loginName, string characterName, MUIdleSettings settings, OfflineSessionStatistics statistics)
    {
        var logger = gameContext.LoggerFactory.CreateLogger(typeof(MUIdleSessions));
        if (await IsAccountOnlineAsync(gameContext, loginName).ConfigureAwait(false))
        {
            logger.LogInformation("MUIdle: {Account} is online again, no offline hunt for {Character}.", loginName, characterName);
            return null;
        }

        var hunter = await gameContext.OfflinePlayerManager
            .StartDetachedAsync(loginName, characterName, () => new HuntOfflinePlayer(gameContext, settings, statistics))
            .ConfigureAwait(false) as HuntOfflinePlayer;
        if (hunter is null)
        {
            logger.LogWarning("MUIdle: the offline hunt of {Character} could not be started.", characterName);
            return null;
        }

        // A login between the check above and the start would load the character a second time: the
        // returning player wins, the hunter steps aside before it changed anything.
        if (await IsAccountOnlineAsync(gameContext, loginName).ConfigureAwait(false))
        {
            hunter.EndReason = "returned";
            await gameContext.OfflinePlayerManager.StopAsync(loginName).ConfigureAwait(false);
            return null;
        }

        logger.LogInformation("MUIdle: {Character} ({Account}) hunts offline on {Map}.", characterName, loginName, hunter.CurrentMap?.Definition.Name);

        // An offline hunter fights with the class skills too, also one restored before its owner logged in again.
        await MUIdleClassSkills.GrantAsync(hunter, GetConfiguration(gameContext)).ConfigureAwait(false);
        MUIdleInventory.GrantFreePages(hunter, GetConfiguration(gameContext));
        return hunter;
    }

    /// <summary>
    /// Called when an offline hunter advanced to <see cref="PlayerState.Disconnected"/>, right before its
    /// session gets saved: the statistics are completed and stored with it.
    /// </summary>
    /// <param name="hunter">The hunter.</param>
    public static async ValueTask OnHunterDisconnectingAsync(HuntOfflinePlayer hunter)
    {
        if (hunter.SelectedCharacter is not { } character)
        {
            return;
        }

        hunter.OnMoneyChanged();
        hunter.UpdateEndValues();
        var state = await GetOrCreateStateAsync(hunter, character).ConfigureAwait(false);
        if (IsShuttingDown(hunter.GameContext))
        {
            // Keep it running: it's restored after the restart.
            state.OfflineStatisticsJson = hunter.Statistics.ToJson();
            return;
        }

        hunter.Statistics.EndedUtc = DateTime.UtcNow;
        hunter.Statistics.EndReason = hunter.EndReason ?? hunter.StopReason ?? (hunter.IsAlive ? "returned" : "died");
        state.OfflineSince = null;
        state.OfflineStatisticsJson = hunter.Statistics.ToJson();
        hunter.Logger.LogInformation(
            "MUIdle: offline hunt of {Character} ended ({Reason}): {Kills} kills, {Exp} exp, {Zen} zen earned.",
            character.Name,
            hunter.Statistics.EndReason,
            hunter.Statistics.MonstersKilled,
            hunter.Statistics.Experience,
            hunter.Statistics.ZenEarned);
    }

    /// <summary>
    /// Saves the progress and the statistics of a running hunter.
    /// </summary>
    /// <param name="hunter">The hunter.</param>
    public static async ValueTask SaveHunterAsync(HuntOfflinePlayer hunter)
    {
        if (hunter.SelectedCharacter is not { } character)
        {
            return;
        }

        await hunter.RunPersistenceExclusiveAsync(async () =>
        {
            hunter.OnMoneyChanged();
            hunter.UpdateEndValues();
            var state = await GetOrCreateStateAsync(hunter, character).ConfigureAwait(false);
            state.OfflineStatisticsJson = hunter.Statistics.ToJson();
            if (hunter.PlayerState.CurrentState == PlayerState.EnteredWorld)
            {
                // A running offline hunt carries its marker, so a restart brings it back. Once it went
                // missing while the hunter ran (2026-10-07, test0Dl after a login/logout: not reproduced),
                // and the next restart dropped the hunt; each save puts it back.
                state.HuntActive = true;
                state.OfflineSince ??= hunter.Statistics.StartedUtc;
            }
        }).ConfigureAwait(false);
        await hunter.SaveProgressAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Called when a player with a client entered a map after a respawn, warp or map change.
    /// </summary>
    /// <param name="player">The player.</param>
    public static async ValueTask OnMapEnteredAsync(RemotePlayer player)
    {
        // Sent to the pinned map by HUNT: the client walks to a hunting ground there, then resumes HUNT.
        if (MUIdleHuntTracker.TryTakePinnedArrival(player, out var spot)
            && player.SelectedCharacter is { } character
            && player.CurrentMap is { } map)
        {
            MUIdleHuntTracker.Report(player, HuntActivityKind.Travelling);
            var state = await GetOrCreateStateAsync(player, character).ConfigureAwait(false);
            await SendStateAsync(player, state, resume: true, ground: spot ?? MUIdleHuntMaps.PickGround(player, map)).ConfigureAwait(false);
            return;
        }

        // Respawned after a death with HUNT on: the client's helper stops in the respawn safezone, so
        // HUNT goes back to a hunting ground by itself, as the offline hunter does - unless the map is
        // one the character keeps dying on and the player chose it (pinned or MANUAL; AUTO moves on).
        if (MUIdleHuntTracker.WasDead(player)
            && MUIdleHuntTracker.HasHuntIntent(player)
            && player.IsAtSafezone()
            && player.SelectedCharacter is { } hunter
            && player.CurrentMap is { } here)
        {
            if (MUIdleHuntTracker.GetSettings(player) is not { AutoMapSelection: true }
                && MUIdleHuntDeaths.IsDeadly(player, (short)here.Definition.Number))
            {
                MUIdleHuntTracker.OnEnteredWorld(player);
                MUIdleHuntTracker.SetNote(player, MUIdleHuntTracker.DeathNote, "deadlyHere");
                return;
            }

            MUIdleHuntTracker.Report(player, HuntActivityKind.Travelling);
            var state = await GetOrCreateStateAsync(player, hunter).ConfigureAwait(false);
            await SendStateAsync(player, state, resume: true, ground: MUIdleHuntMaps.PickGround(player, here)).ConfigureAwait(false);
            return;
        }

        MUIdleHuntTracker.OnEnteredWorld(player);
    }

    /// <summary>
    /// Called when a real player entered the world: shows the summary of a finished offline session,
    /// sends the idle state and resumes HUNT.
    /// </summary>
    /// <param name="player">The player.</param>
    public static async ValueTask OnEnteredWorldAsync(RemotePlayer player)
    {
        if (player.SelectedCharacter is not { } character)
        {
            return;
        }

        var configuration = GetConfiguration(player.GameContext);

        // The class skills from the start (D22): a new character at its first entry, an older one now.
        await MUIdleClassSkills.GrantAsync(player, configuration).ConfigureAwait(false);
        MUIdleInventory.GrantFreePages(player, configuration);
        await MUIdleNotices.SendPendingAsync(player).ConfigureAwait(false);
        var existing = await player.PersistenceContext.GetMUIdleCharacterStateAsync(character.Id).ConfigureAwait(false);
        var state = existing ?? await GetOrCreateStateAsync(player, character).ConfigureAwait(false);
        var summary = OfflineSessionStatistics.Parse(state.OfflineStatisticsJson);

        // A new state is saved right away, so it is in the database before anything else reads it.
        var changed = existing is null;
        if (summary is not null)
        {
            if (summary.EndedUtc is null)
            {
                // The session didn't end regularly (e.g. the server crashed): what was saved last counts.
                summary.EndedUtc = DateTime.UtcNow;
                summary.EndReason ??= "interrupted";
            }

            state.OfflineStatisticsJson = null;
            changed = true;
        }

        if (state.OfflineSince is not null)
        {
            state.OfflineSince = null;
            changed = true;
        }

        if (changed)
        {
            await player.SaveProgressAsync().ConfigureAwait(false);
        }

        // The client resumes HUNT once its map is loaded (its helper loop stops in what it believes
        // to be a safezone, which is everywhere until the terrain arrived); the start request is
        // validated by the server like any other.
        var resume = state.HuntActive && configuration.ResumeHuntingOnLogin && !player.IsAtSafezone();
        await SendStateAsync(player, state, resume).ConfigureAwait(false);
        if (summary is not null)
        {
            await MUIdlePackets.SendJsonAsync(player, MUIdlePackets.OfflineSummarySubCode, summary.ToJson()).ConfigureAwait(false);
        }

        // Points earned while auto build was off, or by a hunter without it, are spent now.
        if (MUIdleSettings.Parse(state.SettingsJson).AutoBuild)
        {
            await MUIdleAutoBuild.DistributeAsync(player).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends the idle state (HUNT intent and settings) to the client.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="state">The state.</param>
    /// <param name="resume">If set to <c>true</c>, the client should start HUNT as soon as it's ready.</param>
    /// <param name="ground">Where to walk before (re)starting HUNT, after a warp to the pinned map.</param>
    public static async ValueTask SendStateAsync(RemotePlayer player, MUIdleCharacterState state, bool resume = false, Point? ground = null)
    {
        // The inventory pages carry the items of the storage pages, loaded on the first send.
        var inventory = await MUIdleInventory.SnapshotAsync(player, GetConfiguration(player.GameContext), state).ConfigureAwait(false);
        var settings = MUIdleSettings.Parse(state.SettingsJson);
        MUIdleHuntTracker.SetSettings(player, settings, state.HuntActive);

        // The client knows items by their inventory slot, the locks reference the item ids.
        var locked = settings.LockedItems.ToHashSet();
        var lockedSlots = player.Inventory?.Items
            .Where(item => locked.Contains(item.GetId()))
            .Select(item => (int)item.ItemSlot)
            .OrderBy(slot => slot)
            .ToList() ?? [];
        var json = JsonSerializer.Serialize(new
        {
            hunt = state.HuntActive,
            resume,
            settings = JsonDocument.Parse(settings.ToJson()).RootElement,
            lockedSlots,
            activity = MUIdleHuntTracker.Snapshot(player),
            maps = MUIdleHuntMaps.Options(player).Select(o => new
            {
                number = (int)o.Map.Number,
                name = o.Map.Name.ValueInNeutralLanguage,
                minLevel = o.MinLevel,
                fare = o.Fare,
                available = o.Available,
                reason = o.Refusal is { } refusal ? MUIdleHuntTracker.RefusalName(refusal) : null,
            }),
            ground = ground is { } point ? new { x = (int)point.X, y = (int)point.Y } : null,
            events = MUIdleEvents.Info(player, settings),
            bosses = MUIdleWorldBossPlugIn.Snapshot(player.GameContext),
            progression = MUIdleSeason.Snapshot(player, GetConfiguration(player.GameContext)),
            contracts = MUIdleContracts.Snapshot(player, state),
            quest = MUIdleQuests.Snapshot(player),
            inventory,
        });
        await MUIdlePackets.SendJsonAsync(player, MUIdlePackets.StateSubCode, json).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the idle state of the character from the player's persistence context, or creates it.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="character">The character.</param>
    /// <returns>The state.</returns>
    public static async ValueTask<MUIdleCharacterState> GetOrCreateStateAsync(Player player, Character character)
    {
        var state = await player.PersistenceContext.GetMUIdleCharacterStateAsync(character.Id).ConfigureAwait(false);
        if (state is null)
        {
            state = player.PersistenceContext.CreateNew<MUIdleCharacterState>();
            state.CharacterId = character.Id;
        }

        return state;
    }

    /// <summary>
    /// Restores the offline hunters which were hunting when the server stopped.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    /// <returns>The number of hunters which could not be started (worth another try a bit later).</returns>
    public static async ValueTask<int> RestoreOfflineHuntersAsync(IGameContext gameContext)
    {
        var logger = gameContext.LoggerFactory.CreateLogger(typeof(MUIdleSessions));
        IReadOnlyList<(string LoginName, string CharacterName)> hunters;
        using (var context = gameContext.PersistenceContextProvider.CreateNewPlayerContext(gameContext.Configuration))
        {
            hunters = await context.GetMUIdleOfflineHuntersAsync().ConfigureAwait(false);
        }

        var restored = 0;
        var failed = 0;
        foreach (var (loginName, characterName) in hunters)
        {
            // Already hunting (an earlier try started it): never a second hunter for the account.
            if (gameContext.OfflinePlayerManager.OfflinePlayers.Any(p => string.Equals(p.AccountLoginName, loginName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                // The statistics and settings are read again by the hunter's own context below.
                var (settings, statistics) = await LoadSessionAsync(gameContext, loginName, characterName).ConfigureAwait(false);
                if (await StartHunterAsync(gameContext, loginName, characterName, settings, statistics).ConfigureAwait(false) is not null)
                {
                    restored++;
                }
                else if (!await IsAccountOnlineAsync(gameContext, loginName).ConfigureAwait(false))
                {
                    // Not because its owner is back: the start failed (e.g. a plugin not ready at startup).
                    failed++;
                }
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogError(ex, "MUIdle: could not restore the offline hunt of {Character}.", characterName);
            }
        }

        if (hunters.Count > 0)
        {
            logger.LogInformation("MUIdle: restored {Restored} of {Count} offline hunter(s), {Failed} failed.", restored, hunters.Count, failed);
        }

        return failed;
    }

    private static async ValueTask<(MUIdleSettings Settings, OfflineSessionStatistics Statistics)> LoadSessionAsync(IGameContext gameContext, string loginName, string characterName)
    {
        using var context = gameContext.PersistenceContextProvider.CreateNewPlayerContext(gameContext.Configuration);
        var account = await context.GetAccountByLoginNameAsync(loginName).ConfigureAwait(false);
        var character = account?.Characters.FirstOrDefault(c => c.Name == characterName);
        var state = character is null ? null : await context.GetMUIdleCharacterStateAsync(character.Id).ConfigureAwait(false);
        var statistics = OfflineSessionStatistics.Parse(state?.OfflineStatisticsJson) ?? new OfflineSessionStatistics { StartedUtc = DateTime.UtcNow };
        return (MUIdleSettings.Parse(state?.SettingsJson), statistics);
    }

    private static async ValueTask<bool> IsAccountOnlineAsync(IGameContext gameContext, string loginName)
    {
        var players = await gameContext.GetPlayersAsync().ConfigureAwait(false);
        return players.Any(p => p is RemotePlayer
                                && string.Equals(p.Account?.LoginName, loginName, StringComparison.OrdinalIgnoreCase)
                                && !p.PlayerState.CurrentState.IsDisconnectedOrFinished());
    }

    private sealed record PendingHunt(IGameContext GameContext, string LoginName, string CharacterName, MUIdleSettings Settings, OfflineSessionStatistics Statistics);
}
