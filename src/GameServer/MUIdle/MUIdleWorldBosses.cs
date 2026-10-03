// <copyright file="MUIdleWorldBosses.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// One world boss: which monster, where, when, and the zen it pays out.
/// </summary>
public sealed class WorldBossDefinition
{
    /// <summary>Gets or sets the name used in messages and the audit log.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of the monster definition.</summary>
    public short MonsterNumber { get; set; }

    /// <summary>Gets or sets the number of the map.</summary>
    public short MapNumber { get; set; }

    /// <summary>Gets or sets the spawn X; 0 for the monster's own spawn area on the map, else a random walkable tile.</summary>
    public byte X { get; set; }

    /// <summary>Gets or sets the spawn Y; see <see cref="X"/>.</summary>
    public byte Y { get; set; }

    /// <summary>Gets or sets the spawn times of day, UTC.</summary>
    public List<TimeSpan> SpawnTimesUtc { get; set; } = [];

    /// <summary>Gets or sets how long the boss stays when nobody kills it.</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Gets or sets the zen split between the players who hurt it, by their share of the damage.</summary>
    public int RewardZen { get; set; }

    /// <summary>Gets or sets the level from which HUNT characters go to it.</summary>
    public int MinimumLevel { get; set; }
}

/// <summary>
/// The configuration of <see cref="MUIdleWorldBossPlugIn"/> (editable in the admin panel).
/// </summary>
public sealed class MUIdleWorldBossConfiguration
{
    /// <summary>Gets or sets the world bosses.</summary>
    public List<WorldBossDefinition> Bosses { get; set; } = [];

    /// <summary>
    /// Creates the default bosses: classic monsters on their own maps, every six hours at :40 UTC,
    /// staggered by an hour or two so they do not overlap.
    /// </summary>
    /// <returns>The configuration.</returns>
    public static MUIdleWorldBossConfiguration CreateDefault() => new()
    {
        Bosses =
        [
            Boss("Gorgon", 18, 1, 200_000, 50, hourOffset: 0),
            Boss("Hydra", 49, 7, 500_000, 120, hourOffset: 1),
            Boss("Death Beam Knight", 63, 4, 1_000_000, 200, hourOffset: 2),
            Boss("Dark Phoenix", 77, 10, 2_000_000, 280, hourOffset: 4),
        ],
    };

    private static WorldBossDefinition Boss(string name, short monster, short map, int zen, int minimumLevel, int hourOffset) => new()
    {
        Name = name,
        MonsterNumber = monster,
        MapNumber = map,
        RewardZen = zen,
        MinimumLevel = minimumLevel,
        SpawnTimesUtc = Enumerable.Range(0, 4).Select(i => TimeSpan.FromHours((i * 6) + hourOffset) + TimeSpan.FromMinutes(40)).ToList(),
    };
}

/// <summary>
/// World bosses: spawned on a schedule from the configuration, the damage of every player (and their
/// summons) counted while they live, and on the kill the zen pool split by damage share - rounded down,
/// the remainder to the top damager, ties broken by name - so the same fight always pays the same,
/// and every payout is written to the log ("MUIdle world boss ..."). A boss nobody kills leaves after
/// its duration.
/// </summary>
[PlugIn]
[Display(Name = "MUIdle world bosses", Description = "Scheduled world bosses with damage contribution and a zen reward split by damage share.")]
[Guid("C2F4A6B8-1D3E-4F50-8A7C-9E1B3D5F7A20")]
public sealed class MUIdleWorldBossPlugIn : IPeriodicTaskPlugIn, IAttackableGotHitPlugIn, IAttackableGotKilledPlugIn,
    ISupportCustomConfiguration<MUIdleWorldBossConfiguration>, ISupportDefaultCustomConfiguration
{
    private const string LogCategory = "MUnique.OpenMU.GameServer.MUIdle.WorldBoss";

    private static readonly ConcurrentDictionary<Monster, BossFight> Fights = new();

    private static readonly ConcurrentDictionary<string, DateTime> Spawned = new();

    private DateTime _nextCheckUtc;

    /// <inheritdoc />
    public MUIdleWorldBossConfiguration? Configuration { get; set; }

    /// <summary>
    /// Gets the bosses for the client: alive (and where) or when next.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    /// <returns>The bosses, serializable to JSON.</returns>
    public static IEnumerable<object> Snapshot(IGameContext gameContext)
    {
        var configuration = gameContext.PlugInManager.GetActivePlugInsOf<IPeriodicTaskPlugIn>()
            .OfType<MUIdleWorldBossPlugIn>().FirstOrDefault()?.Configuration;
        if (configuration is null)
        {
            yield break;
        }

        var now = DateTime.UtcNow;
        foreach (var boss in configuration.Bosses)
        {
            var map = gameContext.Configuration.Maps.FirstOrDefault(m => m.Number == boss.MapNumber);
            var alive = Fights.Values.FirstOrDefault(f => f.Definition.Name == boss.Name && f.GameContext == gameContext);
            yield return new
            {
                name = boss.Name,
                map = map?.Name.ValueInNeutralLanguage ?? boss.MapNumber.ToString(),
                minimumLevel = boss.MinimumLevel,
                rewardZen = boss.RewardZen,
                alive = alive is not null,
                x = alive is null ? (int?)null : alive.Monster.Position.X,
                y = alive is null ? (int?)null : alive.Monster.Position.Y,
                nextSpawnUtc = NextSpawn(boss, now),
            };
        }
    }

    /// <summary>
    /// Gets a live boss a character of the given level should go to, if any (for HUNT).
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    /// <param name="level">The character level.</param>
    /// <returns>The live boss monster and its definition.</returns>
    public static (Monster Monster, WorldBossDefinition Definition)? LiveBossFor(IGameContext gameContext, int level)
    {
        var fight = Fights.Values
            .Where(f => f.GameContext == gameContext && f.Monster.IsAlive && level >= f.Definition.MinimumLevel)
            .OrderByDescending(f => f.Definition.MinimumLevel)
            .FirstOrDefault();
        return fight is null ? null : (fight.Monster, fight.Definition);
    }

    /// <inheritdoc />
    public object CreateDefaultConfig() => MUIdleWorldBossConfiguration.CreateDefault();

    /// <inheritdoc />
    public async ValueTask ExecuteTaskAsync(GameContext gameContext)
    {
        var now = DateTime.UtcNow;
        if (now < this._nextCheckUtc)
        {
            return;
        }

        this._nextCheckUtc = now.AddSeconds(10);
        var configuration = this.Configuration ??= MUIdleWorldBossConfiguration.CreateDefault();
        var logger = gameContext.LoggerFactory.CreateLogger(LogCategory);

        foreach (var boss in configuration.Bosses)
        {
            // A spawn slot is one minute wide; each slot spawns once per game context.
            var slot = boss.SpawnTimesUtc.Select(t => now.Date + t).FirstOrDefault(t => now >= t && now < t.AddMinutes(1));
            if (slot == default || !Spawned.TryAdd($"{gameContext.GetHashCode()}|{boss.Name}|{slot:O}", slot))
            {
                continue;
            }

            await SpawnAsync(gameContext, boss, logger).ConfigureAwait(false);
        }

        foreach (var (monster, fight) in Fights.Where(f => f.Value.GameContext == gameContext && now - f.Value.SpawnedUtc > f.Value.Definition.Duration).ToList())
        {
            if (Fights.TryRemove(monster, out _))
            {
                logger.LogInformation("MUIdle world boss {Boss} left {Map} unbeaten; {Players} player(s) had hurt it.", fight.Definition.Name, monster.CurrentMap.Definition.Name, fight.Damage.Count);
                await monster.CurrentMap.RemoveAsync(monster).ConfigureAwait(false);
                monster.Dispose();
            }
        }

        foreach (var old in Spawned.Where(s => now - s.Value > TimeSpan.FromDays(1)).Select(s => s.Key).ToList())
        {
            Spawned.TryRemove(old, out _);
        }
    }

    /// <inheritdoc />
    public void ForceStart()
    {
        this._nextCheckUtc = DateTime.MinValue;
    }

    /// <inheritdoc />
    public void AttackableGotHit(IAttackable attackable, IAttacker attacker, HitInfo hitInfo)
    {
        if (attackable is not Monster monster || !Fights.TryGetValue(monster, out var fight))
        {
            return;
        }

        var player = attacker as Player ?? (attacker as Monster)?.SummonedBy;
        if (player?.SelectedCharacter?.Name is not { } name || hitInfo.TotalDamage == 0)
        {
            return;
        }

        fight.Players[name] = player;
        fight.Damage.AddOrUpdate(name, hitInfo.TotalDamage, (_, sum) => sum + hitInfo.TotalDamage);
    }

    /// <inheritdoc />
    public async ValueTask AttackableGotKilledAsync(IAttackable killed, IAttacker? killer)
    {
        if (killed is not Monster monster || !Fights.TryRemove(monster, out var fight))
        {
            return;
        }

        var logger = fight.GameContext.LoggerFactory.CreateLogger(LogCategory);
        var shares = Split(fight.Definition.RewardZen, fight.Damage);
        var killerName = (killer as Player ?? (killer as Monster)?.SummonedBy)?.SelectedCharacter?.Name ?? "-";
        logger.LogInformation(
            "MUIdle world boss {Boss} (spawned {Spawned:u}) defeated on {Map} by {Killer}: {Total} damage from {Count} player(s), {Zen} zen split.",
            fight.Definition.Name,
            fight.SpawnedUtc,
            monster.CurrentMap.Definition.Name,
            killerName,
            shares.Sum(s => s.Damage),
            shares.Count,
            fight.Definition.RewardZen);

        foreach (var share in shares)
        {
            var paid = share.Zen > 0
                       && fight.Players.TryGetValue(share.Name, out var player)
                       && player.PlayerState.CurrentState == PlayerState.EnteredWorld
                       && player.TryAddMoney(share.Zen);
            logger.LogInformation(
                "MUIdle world boss {Boss} payout: {Player} dealt {Damage} ({Percent:0.00} %) -> {Zen} zen{Paid}.",
                fight.Definition.Name,
                share.Name,
                share.Damage,
                share.Percent,
                share.Zen,
                paid ? string.Empty : " (not paid: offline or at the zen limit)");
            if (paid && fight.Players.TryGetValue(share.Name, out var receiver))
            {
                await Tell(receiver, $"{fight.Definition.Name}: +{share.Zen:N0} zen ({share.Percent:0.#} % of the damage)", $"{fight.Definition.Name}: +{share.Zen:N0} zen ({share.Percent:0.#} % do dano)").ConfigureAwait(false);
            }
        }

        await AnnounceAsync(fight.GameContext, $"{fight.Definition.Name} was defeated by {killerName}!", $"{fight.Definition.Name} foi derrotado por {killerName}!").ConfigureAwait(false);
    }

    /// <summary>
    /// Splits a zen pool by damage share: each gets the rounded-down share, the remainder goes to the
    /// top damager; order (and ties) by damage, then name, so the result never depends on timing.
    /// </summary>
    /// <param name="pool">The zen pool.</param>
    /// <param name="damage">The damage per character name.</param>
    /// <returns>The shares, top damager first.</returns>
    internal static IReadOnlyList<(string Name, long Damage, double Percent, int Zen)> Split(int pool, IReadOnlyDictionary<string, long> damage)
    {
        var total = damage.Values.Sum();
        var ordered = damage.Where(d => d.Value > 0).OrderByDescending(d => d.Value).ThenBy(d => d.Key, StringComparer.Ordinal).ToList();
        if (total <= 0 || ordered.Count == 0)
        {
            return [];
        }

        var shares = ordered.Select(d => (Name: d.Key, Damage: d.Value, Percent: d.Value * 100.0 / total, Zen: (int)(Math.Max(pool, 0) * d.Value / total))).ToList();
        var remainder = Math.Max(pool, 0) - shares.Sum(s => s.Zen);
        shares[0] = shares[0] with { Zen = shares[0].Zen + remainder };
        return shares;
    }

    private static DateTime? NextSpawn(WorldBossDefinition boss, DateTime now)
    {
        var today = boss.SpawnTimesUtc.Select(t => now.Date + t).Where(t => t > now);
        var tomorrow = boss.SpawnTimesUtc.Select(t => now.Date.AddDays(1) + t);
        return today.Concat(tomorrow).OrderBy(t => t).Cast<DateTime?>().FirstOrDefault();
    }

    private static async ValueTask SpawnAsync(GameContext gameContext, WorldBossDefinition boss, ILogger logger)
    {
        var definition = gameContext.Configuration.Monsters.FirstOrDefault(m => m.Number == boss.MonsterNumber);
        var gameMap = await gameContext.GetMapAsync((ushort)boss.MapNumber).ConfigureAwait(false);
        if (definition is null || gameMap is null)
        {
            logger.LogWarning("MUIdle world boss {Boss}: monster {Monster} or map {Map} not found.", boss.Name, boss.MonsterNumber, boss.MapNumber);
            return;
        }

        if (SpawnPoint(gameMap, boss, definition) is not { } point)
        {
            logger.LogWarning("MUIdle world boss {Boss}: no walkable tile on {Map}.", boss.Name, gameMap.Definition.Name);
            return;
        }

        var area = new MonsterSpawnArea
        {
            GameMap = gameMap.Definition,
            MonsterDefinition = definition,
            SpawnTrigger = SpawnTrigger.OnceAtEventStart,
            Quantity = 1,
            X1 = point.X,
            X2 = point.X,
            Y1 = point.Y,
            Y2 = point.Y,
        };
        var monster = new Monster(area, definition, gameMap, gameContext.DropGenerator, new BasicMonsterIntelligence(), gameContext.PlugInManager, gameContext.PathFinderPool);
        monster.Initialize();
        Fights[monster] = new BossFight(boss, gameContext, monster, DateTime.UtcNow);
        await gameMap.AddAsync(monster).ConfigureAwait(false);
        monster.OnSpawn();

        logger.LogInformation("MUIdle world boss {Boss} spawned on {Map} at {X},{Y} ({Zen} zen to split).", boss.Name, gameMap.Definition.Name, point.X, point.Y, boss.RewardZen);
        var mapName = gameMap.Definition.Name.ValueInNeutralLanguage;
        await AnnounceAsync(gameContext, $"World boss {boss.Name} appeared in {mapName}!", $"Chefe do mundo {boss.Name} apareceu em {mapName}!").ConfigureAwait(false);
    }

    /// <summary>The configured point, else the middle of the monster's own spawn area on that map, else any walkable tile.</summary>
    private static Point? SpawnPoint(GameMap gameMap, WorldBossDefinition boss, MonsterDefinition definition)
    {
        if (boss.X > 0 && boss.Y > 0)
        {
            return new Point(boss.X, boss.Y);
        }

        if (gameMap.Definition.MonsterSpawns.FirstOrDefault(a => a.MonsterDefinition == definition) is { } own)
        {
            var terrain = gameMap.Terrain;
            for (var x = (int)own.X1; x <= own.X2; x++)
            {
                for (var y = (int)own.Y1; y <= own.Y2; y++)
                {
                    if (terrain.WalkMap[x, y] && !terrain.SafezoneMap[x, y])
                    {
                        return new Point((byte)x, (byte)y);
                    }
                }
            }
        }

        return gameMap.Terrain.RandomWalkableCoordinate;
    }

    private static ValueTask AnnounceAsync(IGameContext gameContext, string english, string portuguese)
        => gameContext.ForEachPlayerAsync(player => Tell(player, english, portuguese).AsTask());

    private static async ValueTask Tell(Player player, string english, string portuguese)
    {
        try
        {
            var text = player.Culture.TwoLetterISOLanguageName == "pt" ? portuguese : english;
            await player.InvokeViewPlugInAsync<IShowMessagePlugIn>(p => p.ShowMessageAsync(text, MessageType.GoldenCenter)).ConfigureAwait(false);
        }
        catch
        {
            // A player without a view (leaving) just misses the message.
        }
    }

    private sealed record BossFight(WorldBossDefinition Definition, IGameContext GameContext, Monster Monster, DateTime SpawnedUtc)
    {
        public ConcurrentDictionary<string, long> Damage { get; } = new();

        public ConcurrentDictionary<string, Player> Players { get; } = new();
    }
}
