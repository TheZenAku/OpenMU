// <copyright file="MUIdleFeaturePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// The MUIdle feature: HUNT mode which continues on the server while the player is away. Its periodic
/// task restores the offline hunters after a restart, saves their progress regularly and enforces the
/// offline allowance.
/// </summary>
[PlugIn]
[Display(Name = "MUIdle", Description = "HUNT mode: characters keep hunting on the server while their players are away (based on the offline player and the bot navigator).")]
[Guid("6B0C9E2A-3D1F-4E8A-9C55-1A7F2B8D4E01")]
public class MUIdleFeaturePlugIn : IFeaturePlugIn, IPeriodicTaskPlugIn, ISupportCustomConfiguration<MUIdleConfiguration>, ISupportDefaultCustomConfiguration
{
    private static int _restored;

    private readonly ConcurrentDictionary<GameContext, ServerState> _states = new();

    /// <inheritdoc />
    public MUIdleConfiguration? Configuration { get; set; }

    /// <inheritdoc />
    public object CreateDefaultConfig() => new MUIdleConfiguration();

    /// <inheritdoc />
    public async ValueTask ExecuteTaskAsync(GameContext gameContext)
    {
        if (MUIdleSessions.IsShuttingDown(gameContext))
        {
            return;
        }

        var state = this._states.GetOrAdd(gameContext, _ => new ServerState());
        if (Interlocked.CompareExchange(ref state.Running, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var configuration = this.Configuration ??= new MUIdleConfiguration();

            // Restored once per process, by the first game server which gets here.
            if (configuration.RestoreOfflineHuntersOnStartup && Interlocked.Exchange(ref _restored, 1) == 0)
            {
                await MUIdleSessions.RestoreOfflineHuntersAsync(gameContext).ConfigureAwait(false);
            }

            var now = DateTime.UtcNow;
            if (now < state.NextRunUtc)
            {
                return;
            }

            state.NextRunUtc = now + TimeSpan.FromSeconds(15);
            await this.MaintainHuntersAsync(gameContext, configuration, state, now).ConfigureAwait(false);
            MUIdleSeason.Apply(gameContext, configuration);
            await MUIdleCatchUp.ApplyAsync(gameContext, configuration).ConfigureAwait(false);
            await MUIdleEvents.EnrolAsync(gameContext).ConfigureAwait(false);
            await MUIdleEvents.RallyAsync(gameContext).ConfigureAwait(false);
            await MUIdleQaCharacter.TryHandleAsync(gameContext, MUIdleWorldBossPlugIn.QaAccountsOf(gameContext)).ConfigureAwait(false);
            await MUIdleEconomy.MaintainAsync(gameContext).ConfigureAwait(false);
            await MUIdleOnlineQuests.MaintainAsync(gameContext).ConfigureAwait(false);
            await MUIdleOnlineAutoMap.MaintainAsync(gameContext, configuration).ConfigureAwait(false);
            await MUIdleContracts.TickAsync(gameContext, 0.25).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref state.Running, 0);
        }
    }

    /// <inheritdoc />
    public void ForceStart()
    {
        foreach (var state in this._states.Values)
        {
            state.NextRunUtc = DateTime.MinValue;
        }
    }

    private async ValueTask MaintainHuntersAsync(GameContext gameContext, MUIdleConfiguration configuration, ServerState state, DateTime now)
    {
        var hunters = gameContext.OfflinePlayerManager.OfflinePlayers.OfType<HuntOfflinePlayer>().ToList();
        var save = now >= state.NextSaveUtc;
        if (save)
        {
            state.NextSaveUtc = now + (configuration.OfflineSaveInterval > TimeSpan.Zero ? configuration.OfflineSaveInterval : TimeSpan.FromMinutes(2));
        }

        foreach (var hunter in hunters)
        {
            try
            {
                if (configuration.MaximumOfflineMinutes > 0
                    && now - hunter.Statistics.StartedUtc > TimeSpan.FromMinutes(configuration.MaximumOfflineMinutes)
                    && hunter.AccountLoginName is { } loginName)
                {
                    hunter.EndReason = "allowance";
                    await gameContext.OfflinePlayerManager.StopAsync(loginName).ConfigureAwait(false);
                    continue;
                }

                if (save && hunter.PlayerState.CurrentState == PlayerState.EnteredWorld)
                {
                    await MUIdleSessions.SaveHunterAsync(hunter).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                hunter.Logger.LogError(ex, "MUIdle: maintenance of the offline hunter {Name} failed.", hunter.Name);
            }
        }

        // Upstream saves an online player when it leaves. A crash or a power cut (the VM rebooting)
        // would lose the whole session, so the online players are saved regularly as well. The save
        // takes the player's persistence lock, like its packet handling does.
        if (configuration.OnlineSaveInterval > TimeSpan.Zero && now >= state.NextOnlineSaveUtc)
        {
            state.NextOnlineSaveUtc = now + configuration.OnlineSaveInterval;
            foreach (var player in await gameContext.GetPlayersAsync().ConfigureAwait(false))
            {
                if (player is not RemoteView.RemotePlayer || player.PlayerState.CurrentState != PlayerState.EnteredWorld)
                {
                    continue;
                }

                try
                {
                    // With the crash marker in the same save: an unclean end restores a HUNT as offline hunt.
                    await MUIdleSessions.UpdateCrashMarkerAsync((RemoteView.RemotePlayer)player).ConfigureAwait(false);
                    await player.SaveProgressAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    player.Logger.LogError(ex, "MUIdle: periodic save of {Player} failed.", player);
                }
            }
        }
    }

    private sealed class ServerState
    {
        public int Running;

        public DateTime NextRunUtc;

        public DateTime NextSaveUtc = DateTime.UtcNow + TimeSpan.FromMinutes(1);

        public DateTime NextOnlineSaveUtc = DateTime.UtcNow + TimeSpan.FromMinutes(5);
    }
}
