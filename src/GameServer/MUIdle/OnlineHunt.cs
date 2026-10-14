// <copyright file="OnlineHunt.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.GameServer.RemoteView.MuHelper;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// HUNT v2 (docs/HUNT_V2.md): the HUNT of a player who is in the game, run by the server - the engine
/// of the offline hunt (<see cref="OfflinePlayerMuHelper"/>: combat, buffs, healing, pickup, repair,
/// pet) and its <see cref="BotNavigator"/> (hunting grounds, maps, shopping, errands) under the same
/// policy (<see cref="MUIdleHuntPolicy"/>). The client only shows what happens: it does no hunting of
/// its own, so a tab in the background, a slow network or a busy browser no longer stop or move the
/// hunt. The player's own walks, attacks and skills pause it for a moment (<see cref="Pause"/>).
/// </summary>
internal sealed class OnlineHunt : IHunter
{
    /// <summary>How long the hunt holds still after the player's own action.</summary>
    public static readonly TimeSpan ManualControlPause = TimeSpan.FromSeconds(15);

    private static readonly ConcurrentDictionary<Player, OnlineHunt> Hunts = new();

    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    private readonly RemotePlayer _player;
    private readonly EventHandler<DeathInformation> _deathHandler;
    private readonly object _sampleLock = new();
    private OfflinePlayerMuHelper? _engine;
    private BotNavigator? _navigator;
    private DateTime _pausedUntilUtc;
    private DateTime _nextSampleUtc;
    private long _lastExperience;
    private long _lastMasterExperience;
    private long _lastMoney;
    private int _stopped;

    private OnlineHunt(RemotePlayer player, MUIdleSettings settings)
    {
        this._player = player;
        this.Settings = settings;
        this.Statistics = new OfflineSessionStatistics
        {
            StartedUtc = DateTime.UtcNow,
            StartLevel = player.Level,
            EndLevel = player.Level,
            StartMoney = player.Money,
            EndMoney = player.Money,
        };
        this._deathHandler = (_, _) => this.OnDied();
    }

    /// <summary>Gets the idle settings the hunt runs with.</summary>
    public MUIdleSettings Settings { get; }

    /// <summary>Gets what the hunt earned on which map (the navigator's proven maps, deaths).</summary>
    public OfflineSessionStatistics Statistics { get; }

    /// <inheritdoc />
    public Player Player => this._player;

    /// <inheritdoc />
    public DateTime StartTimestamp { get; } = DateTime.UtcNow;

    /// <inheritdoc />
    public Point HuntingOrigin { get; set; }

    /// <inheritdoc />
    public IAttackable? PriorityTarget { get; set; }

    /// <inheritdoc />
    public string? StopReason { get; set; }

    /// <inheritdoc />
    /// <remarks>The player respawns in town; the navigator takes the hunt back out (unless the settings say otherwise).</remarks>
    public bool RespawnAndContinue => this.Settings.KeepHuntingAfterDeath;

    /// <inheritdoc />
    public ConcurrentQueue<Func<ValueTask>> PendingBotActions { get; } = new();

    /// <inheritdoc />
    /// <remarks>No self-defense against players: the player is there and decides.</remarks>
    public Player? RecentAggressor => null;

    /// <inheritdoc />
    public bool IsOnShoppingTrip { get; set; }

    /// <inheritdoc />
    public bool IsOnBuffTrip { get; set; }

    /// <inheritdoc />
    public DateTime LastAttackUtc { get; set; } = DateTime.UtcNow;

    /// <inheritdoc />
    public bool IsPaused => DateTime.UtcNow < this._pausedUntilUtc;

    /// <summary>
    /// Gets the online hunt of a player, if it runs.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The hunt.</returns>
    public static OnlineHunt? Of(Player player) => Hunts.TryGetValue(player, out var hunt) ? hunt : null;

    /// <summary>
    /// Starts (or restarts, with the settings given) the online hunt of a player.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="settings">The idle settings.</param>
    /// <returns>The task.</returns>
    public static async ValueTask StartAsync(RemotePlayer player, MUIdleSettings settings)
    {
        await StopAsync(player, null).ConfigureAwait(false);
        if (player.PlayerState.CurrentState.IsDisconnectedOrFinished() || player.SelectedCharacter is null)
        {
            return;
        }

        var hunt = new OnlineHunt(player, settings);
        if (!Hunts.TryAdd(player, hunt))
        {
            return;
        }

        hunt.Begin();
    }

    /// <summary>
    /// Stops the online hunt of a player, if it runs.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="reason">Why, for the log; <c>null</c> for a restart.</param>
    /// <returns>The task.</returns>
    public static async ValueTask StopAsync(Player player, string? reason)
    {
        if (Hunts.TryRemove(player, out var hunt))
        {
            await hunt.EndAsync(reason).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The player took the controls (a walk, an attack, a skill of its own): the hunt holds still for a
    /// moment, then goes on from wherever the player left the character.
    /// </summary>
    /// <param name="player">The player.</param>
    public static void Pause(Player player)
    {
        if (Of(player) is { } hunt)
        {
            hunt._pausedUntilUtc = DateTime.UtcNow + ManualControlPause;
            hunt.HuntingOrigin = player.Position;
        }
    }

    /// <inheritdoc />
    public bool TryGetRevengeDestination(GameMapDefinition currentMap, out Point deathSite)
    {
        deathSite = default;
        return false;
    }

    /// <inheritdoc />
    public void ExpireRevenge(string reason)
    {
    }

    /// <inheritdoc />
    public bool TryGetDeathSiteToAvoid(GameMapDefinition currentMap, out Point deathSite)
    {
        deathSite = default;
        return false;
    }

    /// <inheritdoc />
    public async ValueTask DrainPendingBotActionsAsync()
    {
        while (this.PendingBotActions.TryDequeue(out var action))
        {
            await action().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void OnAiTickSucceeded() => this.Sample();

    /// <inheritdoc />
    public void OnAiTickFailed()
    {
    }

    /// <inheritdoc />
    public void ArmRevengeAfterRespawn()
    {
    }

    /// <inheritdoc />
    public ValueTask StopSessionAsync()
    {
        this._player.Logger.LogInformation("MUIdle: the online hunt of {Character} stopped itself ({Reason}).", this._player.Name, this.StopReason ?? "-");
        MUIdleHuntTracker.SetHuntIntent(this._player, false);
        return StopAsync(this._player, this.StopReason ?? "stopped itself");
    }

    private void Begin()
    {
        var player = this._player;

        // The character's own MU Helper configuration drives the engine (skills, potions, pickup filters),
        // plus the guarantees HUNT needs - as for the offline hunt.
        var own = player.SelectedCharacter?.MuHelperConfiguration is { } blob ? MuHelperSettingsSerializer.TryDeserialize(blob) : null;
        player.MuHelperSettings = new HuntMuHelperSettings(own, this.Settings);
        this.HuntingOrigin = player.Position;
        this.TakeBaseline();
        player.Died += this._deathHandler;

        this._engine = new OfflinePlayerMuHelper(this);
        this._engine.Start();
        if (this.Settings.AutoTravel)
        {
            this._navigator = new BotNavigator(this, MUIdleHuntPolicy.Create(this, this.Settings, this.Statistics));
            this._navigator.Start();
        }

        MUIdleHuntTracker.Report(player, HuntActivityKind.Hunting, player.CurrentMap?.Definition.Name.ValueInNeutralLanguage);
        player.Logger.LogInformation("MUIdle: online hunt of {Character} started on {Map}.", player.Name, player.CurrentMap?.Definition.Name.ValueInNeutralLanguage);
    }

    private async ValueTask EndAsync(string? reason)
    {
        if (Interlocked.Exchange(ref this._stopped, 1) != 0)
        {
            return;
        }

        this._player.Died -= this._deathHandler;
        if (Interlocked.Exchange(ref this._navigator, null) is { } navigator)
        {
            await navigator.DisposeAsync().ConfigureAwait(false);
        }

        if (Interlocked.Exchange(ref this._engine, null) is { } engine)
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }

        this._player.MuHelperSettings = null;
        MUIdleMapMemory.Merge(this._player, this.Statistics);
        if (reason is not null)
        {
            MUIdleHuntTracker.Report(this._player, HuntActivityKind.Idle);
            this._player.Logger.LogInformation("MUIdle: online hunt of {Character} stopped ({Reason}).", this._player.Name, reason);
        }
    }

    private void OnDied()
    {
        var map = this._player.CurrentMap?.Definition.Name.ValueInNeutralLanguage;
        this.Statistics.AddDeath();
        this.Statistics.Record(map, deaths: 1);
        MUIdleHuntDeaths.Record(this._player);
        MUIdleHuntTracker.Report(this._player, HuntActivityKind.Dead);
    }

    private void TakeBaseline()
    {
        lock (this._sampleLock)
        {
            this._lastExperience = this._player.SelectedCharacter?.Experience ?? 0;
            this._lastMasterExperience = this._player.SelectedCharacter?.MasterExperience ?? 0;
            this._lastMoney = this._player.Money;
            this._nextSampleUtc = DateTime.UtcNow + SampleInterval;
        }
    }

    /// <summary>
    /// What the hunt earned since the last look, per map: the navigator moves to a map that proved
    /// better (experience per minute), and keeps away from maps it dies on or loses money on.
    /// </summary>
    private void Sample()
    {
        if (this._player.SelectedCharacter is not { } character)
        {
            return;
        }

        long experience, masterExperience, zen = 0, spent = 0;
        lock (this._sampleLock)
        {
            var now = DateTime.UtcNow;
            if (now < this._nextSampleUtc)
            {
                return;
            }

            this._nextSampleUtc = now + SampleInterval;

            // A level reset lowers the totals: that is no loss of the hunt.
            experience = Math.Max(0, character.Experience - this._lastExperience);
            masterExperience = Math.Max(0, character.MasterExperience - this._lastMasterExperience);
            var money = (long)this._player.Money;
            if (money > this._lastMoney)
            {
                zen = money - this._lastMoney;
            }
            else
            {
                spent = this._lastMoney - money;
            }

            this._lastExperience = character.Experience;
            this._lastMasterExperience = character.MasterExperience;
            this._lastMoney = money;
        }

        if (experience == 0 && masterExperience == 0 && zen == 0 && spent == 0)
        {
            return;
        }

        var map = this._player.CurrentMap?.Definition.Name.ValueInNeutralLanguage;
        if (experience > 0 || masterExperience > 0)
        {
            MUIdleHuntTracker.ReportKill(this._player);
        }

        this.Statistics.AddExperience(experience, false);
        this.Statistics.AddExperience(masterExperience, true);
        this.Statistics.AddZenDelta(zen - spent);
        this.Statistics.Record(map, experience: experience + masterExperience, zen: zen, spent: spent);
    }
}
