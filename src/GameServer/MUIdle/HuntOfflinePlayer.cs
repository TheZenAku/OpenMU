// <copyright file="HuntOfflinePlayer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Threading;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.PlayerActions.MiniGames;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameLogic.Views.Character;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.GameLogic.Views.MuHelper;
using MUnique.OpenMU.GameLogic.Views.World;
using MUnique.OpenMU.GameServer.RemoteView.MuHelper;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// The offline session of a real player's character in HUNT mode. It's the upstream
/// <see cref="OfflinePlayer"/> (same combat, healing, pickup, repair and buff handlers), driven by
/// the character's own MU Helper configuration, optionally navigated by a <see cref="BotNavigator"/>
/// under a restricted <see cref="BotNavigatorPolicy"/>, and it records what happens into
/// <see cref="Statistics"/>.
/// </summary>
public sealed class HuntOfflinePlayer : OfflinePlayer
{
    private readonly EventHandler<DeathInformation> _deathHandler;
    private BotNavigator? _navigator;
    private long _lastMoney;

    /// <summary>
    /// Initializes a new instance of the <see cref="HuntOfflinePlayer"/> class.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    /// <param name="settings">The idle settings of the character.</param>
    /// <param name="statistics">The statistics of the session.</param>
    public HuntOfflinePlayer(IGameContext gameContext, MUIdleSettings settings, OfflineSessionStatistics statistics)
        : base(gameContext)
    {
        this.Settings = settings;
        this.Statistics = statistics;
        this._deathHandler = (_, _) =>
        {
            this.Statistics.AddDeath();
            this.Statistics.Record(this.CurrentMapName, deaths: 1);
            MUIdleHuntDeaths.Record(this);
            MUIdleHuntTracker.Report(this, HuntActivityKind.Dead);
        };
        this.Died += this._deathHandler;
        MUIdleHuntTracker.SetSettings(this, settings);
    }

    /// <summary>
    /// Gets the idle settings of the character.
    /// </summary>
    public MUIdleSettings Settings { get; }

    /// <summary>
    /// Gets the statistics of this session.
    /// </summary>
    public OfflineSessionStatistics Statistics { get; }

    /// <summary>
    /// Gets or sets the reason why the session ended, written into the statistics when it stops.
    /// </summary>
    public string? EndReason { get; set; }

    /// <inheritdoc />
    public override bool RespawnAndContinue => this.Settings.AutoTravel && this.Settings.KeepHuntingAfterDeath;

    /// <summary>
    /// The rally point of the hunt (<see cref="MUIdleHuntPolicy.Rally"/>).
    /// </summary>
    /// <returns>The map and spot, or <c>null</c>.</returns>
    internal (int Map, Point Spot)? HuntRally() => MUIdleHuntPolicy.Rally(this, this.Settings, this.Statistics);

    /// <summary>
    /// Gets the name of the current map, as the statistics record it.
    /// </summary>
    internal string? CurrentMapName => this.CurrentMap?.Definition.Name.ValueInNeutralLanguage;

    /// <summary>
    /// Gets the current master level.
    /// </summary>
    internal int MasterLevel => (int)(this.Attributes?[Stats.MasterLevel] ?? 0);

    /// <summary>
    /// Writes the end values (level, zen) into the statistics.
    /// </summary>
    internal void UpdateEndValues()
    {
        this.Statistics.EndLevel = this.Level;
        this.Statistics.EndMasterLevel = this.MasterLevel;
        this.Statistics.EndMoney = this.Money;
        if (this.CurrentMap?.Definition.Name is { } mapName)
        {
            this.Statistics.AddMap(mapName);
        }
    }

    /// <inheritdoc />
    public override async ValueTask StopAsync()
    {
        await this.StopNavigatorAsync().ConfigureAwait(false);
        await base.StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Records a change of the money.
    /// </summary>
    internal void OnMoneyChanged()
    {
        var current = (long)this.Money;
        var previous = Interlocked.Exchange(ref this._lastMoney, current);
        this.Statistics.AddZenDelta(current - previous);
        if (current > previous)
        {
            this.Statistics.Record(this.CurrentMapName, zen: current - previous);
        }
        else if (current < previous)
        {
            // What hunting here costs (repairs, potions, the fee) counts against the map (D20).
            this.Statistics.Record(this.CurrentMapName, spent: previous - current);
        }
    }

    /// <inheritdoc />
    protected override void StartIntelligence()
    {
        // The character's own MU Helper configuration drives the offline AI (skills, potion thresholds,
        // pickup filters) - plus the guarantees HUNT needs (see HuntMuHelperSettings).
        var own = this.SelectedCharacter?.MuHelperConfiguration is { } blob
            ? MuHelperSettingsSerializer.TryDeserialize(blob)
            : null;
        this.MuHelperSettings = new HuntMuHelperSettings(own);
        Interlocked.Exchange(ref this._lastMoney, this.Money);

        if (this.CurrentMap?.Definition.Name is { } mapName)
        {
            this.Statistics.AddMap(mapName);
        }

        base.StartIntelligence();

        if (this.Settings.AutoTravel)
        {
            this._navigator = new BotNavigator(this, this.CreatePolicy());
            this._navigator.Start();
        }
    }

    /// <inheritdoc />
    protected override ICustomPlugInContainer<IViewPlugIn> CreateViewPlugInContainer()
        => new TelemetryViewPlugInContainer(this, base.CreateViewPlugInContainer());

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        this.Died -= this._deathHandler;
        await this.StopNavigatorAsync().ConfigureAwait(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

    private BotNavigatorPolicy CreatePolicy() => MUIdleHuntPolicy.Create(this, this.Settings, this.Statistics);

    private async ValueTask StopNavigatorAsync()
    {
        if (Interlocked.Exchange(ref this._navigator, null) is { } navigator)
        {
            await navigator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wraps the view plugins of the offline player and turns the view events (which a remote player
    /// would send to its client) into statistics.
    /// </summary>
    private sealed class TelemetryViewPlugInContainer : ICustomPlugInContainer<IViewPlugIn>,
        IAddExperiencePlugIn, IUpdateMoneyPlugIn, IItemAppearPlugIn, IItemDurabilityChangedPlugIn, IMapChangePlugIn,
        IShowMiniGameEnterResultPlugIn, IMuHelperStatusUpdatePlugIn
    {
        private readonly HuntOfflinePlayer _player;
        private readonly ICustomPlugInContainer<IViewPlugIn> _inner;

        public TelemetryViewPlugInContainer(HuntOfflinePlayer player, ICustomPlugInContainer<IViewPlugIn> inner)
        {
            this._player = player;
            this._inner = inner;
        }

        public T? GetPlugIn<T>()
            where T : class, IViewPlugIn
        {
            if (this is T self)
            {
                return self;
            }

            return this._inner.GetPlugIn<T>();
        }

        public ValueTask ShowResultAsync(MiniGameType miniGameType, EnterResult enterResult)
        {
            // What a player would read on screen, kept: an offline hunter has no screen.
            this._player.GameContext.LoggerFactory.CreateLogger("MUnique.OpenMU.GameServer.MUIdle.Hunt").LogDebug(
                "MUIdle hunt {Character}: {Event} entry {Result}.", this._player.Name, miniGameType, enterResult);
            return this._inner.GetPlugIn<IShowMiniGameEnterResultPlugIn>()?.ShowResultAsync(miniGameType, enterResult) ?? ValueTask.CompletedTask;
        }

        public ValueTask AddExperienceAsync(int gainedExperience, IAttackable? killedObject, ExperienceType experienceType)
        {
            if (gainedExperience > 0)
            {
                this._player.Statistics.AddExperience(gainedExperience, experienceType == ExperienceType.Master);
            }

            if (killedObject is not null)
            {
                this._player.Statistics.AddKill();
                MUIdleHuntTracker.Report(this._player, HuntActivityKind.Hunting);
            }

            if (gainedExperience > 0 || killedObject is not null)
            {
                this._player.Statistics.Record(this._player.CurrentMapName, experience: Math.Max(0, gainedExperience), kills: killedObject is null ? 0 : 1);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask UpdateMoneyAsync()
        {
            this._player.OnMoneyChanged();
            return ValueTask.CompletedTask;
        }

        public ValueTask StartAsync()
            => this._inner.GetPlugIn<IMuHelperStatusUpdatePlugIn>()?.StartAsync() ?? ValueTask.CompletedTask;

        public ValueTask StopAsync()
            => this._inner.GetPlugIn<IMuHelperStatusUpdatePlugIn>()?.StopAsync() ?? ValueTask.CompletedTask;

        public ValueTask ConsumeMoneyAsync(uint money)
        {
            // The MU Helper fee (ZenConsumptionHandler) sends no money update: recorded now as spent,
            // not netted into the next zen pickup.
            this._player.OnMoneyChanged();
            return this._inner.GetPlugIn<IMuHelperStatusUpdatePlugIn>()?.ConsumeMoneyAsync(money) ?? ValueTask.CompletedTask;
        }

        public ValueTask ItemAppearAsync(Item newItem)
        {
            // With a merchant dialog open, it's a purchase, not a pickup.
            if (this._player.OpenedNpc is null)
            {
                this._player.Statistics.AddCollectedItem();
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ItemDurabilityChangedAsync(Item item, bool causedByConsumption)
        {
            if (causedByConsumption)
            {
                return ValueTask.CompletedTask;
            }

            // A repair raises the durability of an equipped piece.
            if (item.ItemSlot <= InventoryConstants.LastEquippableItemSlotIndex
                && item.Durability >= item.GetMaximumDurabilityOfOnePiece())
            {
                this._player.Statistics.AddRepair();
            }

            return ValueTask.CompletedTask;
        }

        public async ValueTask MapChangeAsync()
        {
            // The inner plugin completes the warp (ClientReadyAfterMapChange); only then is the
            // current map the new one.
            if (this._inner.GetPlugIn<IMapChangePlugIn>() is { } inner)
            {
                await inner.MapChangeAsync().ConfigureAwait(false);
            }

            if (this._player.CurrentMap?.Definition.Name is { } mapName)
            {
                this._player.Statistics.AddMap(mapName);
            }
        }

        public async ValueTask MapChangeFailedAsync()
        {
            if (this._inner.GetPlugIn<IMapChangePlugIn>() is { } inner)
            {
                await inner.MapChangeFailedAsync().ConfigureAwait(false);
            }
        }
    }
}
