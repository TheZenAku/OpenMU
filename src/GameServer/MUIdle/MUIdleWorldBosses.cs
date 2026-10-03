// <copyright file="MUIdleWorldBosses.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameLogic.Views.Inventory;
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
    /// Gets or sets the accounts a QA world boss (spawned through the local QA trigger, see
    /// <see cref="MUIdleWorldBossPlugIn"/>) is for: only their characters rally to it and get its reward.
    /// </summary>
    public List<string> QaAccounts { get; set; } = ["test300", "qa001", "qa002", "qa003", "idle01"];

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

    /// <summary>The log category of the QA boss telemetry (only written while a QA boss lives).</summary>
    private const string QaLogCategory = "MUnique.OpenMU.GameServer.MUIdle.WorldBoss.Qa";

    /// <summary>The QA trigger file in the directory named by the <c>MUIDLE_QA_DIR</c> environment variable.</summary>
    private const string QaTriggerFile = "worldboss.json";

    /// <summary>How far, walking from the warp's landing, a boss may appear.</summary>
    private const int MaxWalkSteps = 90;

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
            var alive = Fights.Values.FirstOrDefault(f => f.Definition.Name == boss.Name && f.GameContext == gameContext && !f.IsQa);
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
    /// Gets a live boss the player's character should go to, if any (for HUNT): of its level, and a QA
    /// boss only for a QA account.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The live boss monster and its definition.</returns>
    public static (Monster Monster, WorldBossDefinition Definition)? LiveBossFor(Player player)
    {
        var level = player.Level;
        var qa = IsQaAccount(player);
        var fight = Fights.Values
            .Where(f => f.GameContext == player.GameContext && f.Monster.IsAlive && level >= f.Definition.MinimumLevel && (!f.IsQa || qa))
            .OrderByDescending(f => f.IsQa)
            .ThenByDescending(f => f.Definition.MinimumLevel)
            .FirstOrDefault();
        return fight is null ? null : (fight.Monster, fight.Definition);
    }

    /// <summary>
    /// Reads a QA spawn request (<c>{"boss":"Hydra","map":7,"zen":1000,"minutes":15}</c>; only the boss
    /// is required): <c>null</c> when it is not one.
    /// </summary>
    /// <param name="json">The request.</param>
    /// <returns>The boss name, map override, zen pool and duration.</returns>
    internal static (string Boss, short? Map, int Zen, int Minutes)? ParseQaRequest(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("boss", out var boss) || boss.GetString() is not { Length: > 0 } name)
            {
                return null;
            }

            short? map = root.TryGetProperty("map", out var m) && m.TryGetInt16(out var number) ? number : null;
            var zen = root.TryGetProperty("zen", out var z) && z.TryGetInt32(out var pool) ? Math.Clamp(pool, 0, 1_000_000) : 1000;
            var minutes = root.TryGetProperty("minutes", out var d) && d.TryGetInt32(out var duration) ? Math.Clamp(duration, 1, 60) : 15;
            return (name, map, zen, minutes);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the QA accounts of the configuration (the QA tools act only for them).
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    /// <returns>The QA accounts.</returns>
    public static IReadOnlyCollection<string> QaAccountsOf(IGameContext gameContext)
        => GetConfiguration(gameContext)?.QaAccounts ?? [];

    private static MUIdleWorldBossConfiguration? GetConfiguration(IGameContext gameContext)
        => gameContext.PlugInManager.GetActivePlugInsOf<IPeriodicTaskPlugIn>().OfType<MUIdleWorldBossPlugIn>().FirstOrDefault()?.Configuration;

    private static bool IsQaAccount(Player? player)
        => player?.Account?.LoginName is { } login
           && GetConfiguration(player.GameContext)?.QaAccounts.Contains(login, StringComparer.OrdinalIgnoreCase) == true;

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

        await this.TryQaSpawnAsync(gameContext, configuration, logger).ConfigureAwait(false);
        await LogQaTelemetryAsync(gameContext).ConfigureAwait(false);

        foreach (var (monster, fight) in Fights.Where(f => f.Value.GameContext == gameContext && now - f.Value.SpawnedUtc > f.Value.Definition.Duration).ToList())
        {
            if (Fights.TryRemove(monster, out _))
            {
                logger.LogInformation("MUIdle world boss {Boss}{Qa} left {Map} unbeaten; {Players} player(s) had hurt it.", fight.Definition.Name, fight.IsQa ? " [QA]" : string.Empty, monster.CurrentMap.Definition.Name, fight.Damage.Count);
                await monster.CurrentMap.RemoveAsync(monster).ConfigureAwait(false);
                monster.Dispose();
            }
        }

        foreach (var old in Spawned.Where(s => now - s.Value > TimeSpan.FromDays(1)).Select(s => s.Key).ToList())
        {
            Spawned.TryRemove(old, out _);
        }
    }

    /// <summary>
    /// The QA trigger: a request file in the directory of the <c>MUIDLE_QA_DIR</c> environment variable
    /// (unset = off; only someone on the server machine can drop it) spawns a QA instance of a configured
    /// boss through the real spawn, fight and payout - for the QA accounts only, without touching the
    /// schedule and without a broadcast. The file is consumed.
    /// </summary>
    private async ValueTask TryQaSpawnAsync(GameContext gameContext, MUIdleWorldBossConfiguration configuration, ILogger logger)
    {
        if (Environment.GetEnvironmentVariable("MUIDLE_QA_DIR") is not { Length: > 0 } directory
            || Path.Combine(directory, QaTriggerFile) is not { } file
            || !File.Exists(file))
        {
            return;
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(file).ConfigureAwait(false);
            File.Delete(file);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "MUIdle QA world boss: could not read {File}.", file);
            return;
        }

        if (ParseQaRequest(json) is not { } request
            || configuration.Bosses.FirstOrDefault(b => string.Equals(b.Name, request.Boss, StringComparison.OrdinalIgnoreCase)) is not { } boss)
        {
            logger.LogWarning("MUIdle QA world boss: request {Request} names no configured boss.", json);
            return;
        }

        var instance = new WorldBossDefinition
        {
            Name = boss.Name,
            MonsterNumber = boss.MonsterNumber,
            MapNumber = request.Map ?? boss.MapNumber,
            X = request.Map is null ? boss.X : (byte)0,
            Y = request.Map is null ? boss.Y : (byte)0,
            Duration = TimeSpan.FromMinutes(request.Minutes),
            RewardZen = request.Zen,
            MinimumLevel = boss.MinimumLevel,
        };
        await SpawnAsync(gameContext, instance, logger, qa: true).ConfigureAwait(false);
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
        if (player?.SelectedCharacter?.Name is not { } name)
        {
            return;
        }

        fight.Hits.AddOrUpdate(name, 1, (_, count) => count + 1);
        if (hitInfo.TotalDamage == 0)
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
            "MUIdle world boss {Boss}{Qa} (spawned {Spawned:u}) defeated on {Map} by {Killer}: {Total} damage from {Count} player(s), {Zen} zen split.",
            fight.Definition.Name,
            fight.IsQa ? " [QA]" : string.Empty,
            fight.SpawnedUtc,
            monster.CurrentMap.Definition.Name,
            killerName,
            shares.Sum(s => s.Damage),
            shares.Count,
            fight.Definition.RewardZen);

        foreach (var share in shares)
        {
            // The character's session now - a hunter that went offline (or came back) during the fight
            // is a new player object; the damage counts by character.
            var receiver = fight.GameContext.GetPlayerByCharacterName(share.Name)
                           ?? (fight.Players.TryGetValue(share.Name, out var hitter) ? hitter : null);
            var refusal = share.Zen <= 0 ? "nothing to pay"
                : receiver is null || receiver.PlayerState.CurrentState != PlayerState.EnteredWorld ? "not in the game"
                : fight.IsQa && !IsQaAccount(receiver) ? "QA boss, not a QA account"
                : null;
            var paid = refusal is null
                       && await receiver!.RunPersistenceExclusiveAsync(() => ValueTask.FromResult(receiver.TryAddMoney(share.Zen))).ConfigureAwait(false);
            logger.LogInformation(
                "MUIdle world boss {Boss}{Qa} payout: {Player} dealt {Damage} ({Percent:0.00} %) in {Hits} hit(s) -> {Zen} zen{Paid}.",
                fight.Definition.Name,
                fight.IsQa ? " [QA]" : string.Empty,
                share.Name,
                share.Damage,
                share.Percent,
                fight.Hits.TryGetValue(share.Name, out var hits) ? hits : 0,
                share.Zen,
                paid ? string.Empty : $" (not paid: {refusal ?? "zen limit"})");
            if (paid)
            {
                await receiver!.InvokeViewPlugInAsync<IUpdateMoneyPlugIn>(p => p.UpdateMoneyAsync()).ConfigureAwait(false);
                await Tell(receiver!, $"{fight.Definition.Name}: +{share.Zen:N0} zen ({share.Percent:0.#} % of the damage)", $"{fight.Definition.Name}: +{share.Zen:N0} zen ({share.Percent:0.#} % do dano)").ConfigureAwait(false);
            }
        }

        if (fight.IsQa)
        {
            return;
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

    private static async ValueTask SpawnAsync(GameContext gameContext, WorldBossDefinition boss, ILogger logger, bool qa = false)
    {
        var definition = gameContext.Configuration.Monsters.FirstOrDefault(m => m.Number == boss.MonsterNumber);
        var gameMap = await gameContext.GetMapAsync((ushort)boss.MapNumber).ConfigureAwait(false);
        if (definition is null || gameMap is null)
        {
            logger.LogWarning("MUIdle world boss {Boss}: monster {Monster} or map {Map} not found.", boss.Name, boss.MonsterNumber, boss.MapNumber);
            return;
        }

        if (SpawnPoint(gameMap, boss, definition, gameContext) is not { } point)
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
        Fights[monster] = new BossFight(boss, gameContext, monster, DateTime.UtcNow) { IsQa = qa };
        await gameMap.AddAsync(monster).ConfigureAwait(false);
        monster.OnSpawn();

        logger.LogInformation("MUIdle world boss {Boss}{Qa} spawned on {Map} at {X},{Y} ({Zen} zen to split).", boss.Name, qa ? " [QA]" : string.Empty, gameMap.Definition.Name, point.X, point.Y, boss.RewardZen);
        if (qa)
        {
            // A QA boss is for the QA accounts: no broadcast to the players.
            return;
        }

        var mapName = gameMap.Definition.Name.ValueInNeutralLanguage;
        await AnnounceAsync(gameContext, $"World boss {boss.Name} appeared in {mapName}!", $"Chefe do mundo {boss.Name} apareceu em {mapName}!").ConfigureAwait(false);
    }

    /// <summary>
    /// The configured point; else a tile a player can walk to, and soon, from where the map's warp
    /// lands (at most <see cref="MaxWalkSteps"/> steps): the one of the monster's own spawn area nearest
    /// its middle, or the tile in walking range nearest that area. (Behind walls or on a floor joined
    /// only by inner gates nobody reached it; across Atlans, 200 tiles of trash away, a rallied hunter
    /// walked for half an hour without getting there.)
    /// </summary>
    private static Point? SpawnPoint(GameMap gameMap, WorldBossDefinition boss, MonsterDefinition definition, IGameContext gameContext)
    {
        if (boss.X > 0 && boss.Y > 0)
        {
            return new Point(boss.X, boss.Y);
        }

        var terrain = gameMap.Terrain;
        var steps = WalkStepsFromWarp(gameMap, gameContext);
        if (steps is null)
        {
            return terrain.RandomWalkableCoordinate;
        }

        var own = gameMap.Definition.MonsterSpawns.FirstOrDefault(a => a.MonsterDefinition == definition);
        int centerX = own is null ? 128 : (own.X1 + own.X2) / 2, centerY = own is null ? 128 : (own.Y1 + own.Y2) / 2;
        bool InOwnArea(int x, int y) => own is not null && x >= own.X1 && x <= own.X2 && y >= own.Y1 && y <= own.Y2;

        Point? best = null;
        var bestScore = int.MaxValue;
        for (var x = 0; x < 256; x++)
        {
            for (var y = 0; y < 256; y++)
            {
                if (steps[x, y] < 0 || steps[x, y] > MaxWalkSteps || terrain.SafezoneMap[x, y])
                {
                    continue;
                }

                // A tile of the own area always beats one outside it.
                var score = Math.Abs(x - centerX) + Math.Abs(y - centerY) + (InOwnArea(x, y) ? 0 : 1000);
                if (score < bestScore)
                {
                    best = new Point((byte)x, (byte)y);
                    bestScore = score;
                }
            }
        }

        return best ?? terrain.RandomWalkableCoordinate;
    }

    /// <summary>
    /// The walking steps from the landing area of a warp to the map to every tile (-1: not reachable
    /// on foot), or null without a warp.
    /// </summary>
    private static int[,]? WalkStepsFromWarp(GameMap gameMap, IGameContext gameContext)
    {
        var gate = gameContext.Configuration.WarpList
            .Where(w => w.Gate?.Map?.Number == gameMap.Definition.Number)
            .OrderBy(w => w.LevelRequirement)
            .Select(w => w.Gate)
            .FirstOrDefault();
        if (gate is null)
        {
            return null;
        }

        var landing = new List<Point>();
        for (var x = (int)gate.X1; x <= gate.X2; x++)
        {
            for (var y = (int)gate.Y1; y <= gate.Y2; y++)
            {
                landing.Add(new Point((byte)x, (byte)y));
            }
        }

        return WalkSteps(gameMap, landing);
    }

    /// <summary>
    /// The walking steps from the nearest of the start tiles to every tile (-1: not reachable on foot;
    /// floors which share one map id are not joined).
    /// </summary>
    /// <param name="gameMap">The map, with its terrain.</param>
    /// <param name="starts">The start tiles; unwalkable ones are skipped.</param>
    /// <returns>The steps per tile.</returns>
    internal static int[,] WalkSteps(GameMap gameMap, IEnumerable<Point> starts)
    {
        var walk = gameMap.Terrain.WalkMap;
        var steps = new int[256, 256];
        for (var x = 0; x < 256; x++)
        {
            for (var y = 0; y < 256; y++)
            {
                steps[x, y] = -1;
            }
        }

        var queue = new Queue<(int X, int Y)>();
        foreach (var start in starts)
        {
            if (walk[start.X, start.Y] && steps[start.X, start.Y] < 0)
            {
                steps[start.X, start.Y] = 0;
                queue.Enqueue((start.X, start.Y));
            }
        }

        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
            {
                if (nx is >= 0 and < 256 && ny is >= 0 and < 256 && walk[nx, ny] && steps[nx, ny] < 0)
                {
                    steps[nx, ny] = steps[x, y] + 1;
                    queue.Enqueue((nx, ny));
                }
            }
        }

        return steps;
    }

    /// <summary>
    /// The QA telemetry of a live QA boss, every check (10 s): for each QA character hunting it, where it
    /// stands, what it does, why it would not fight, and what it achieved.
    /// </summary>
    private static async ValueTask LogQaTelemetryAsync(GameContext gameContext)
    {
        var fights = Fights.Values.Where(f => f.IsQa && f.GameContext == gameContext && f.Monster.IsAlive).ToList();
        if (fights.Count == 0)
        {
            return;
        }

        var logger = gameContext.LoggerFactory.CreateLogger(QaLogCategory);
        foreach (var fight in fights)
        {
            var boss = fight.Monster;
            logger.LogInformation(
                "QA boss {Boss} #{Id} on {Map} at {X},{Y}: health {Health:0}/{MaxHealth:0}, {Players} damager(s).",
                fight.Definition.Name,
                boss.Id,
                boss.CurrentMap.Definition.Name,
                boss.Position.X,
                boss.Position.Y,
                boss.Health,
                boss.Attributes[GameLogic.Attributes.Stats.MaximumHealth],
                fight.Damage.Count);
            foreach (var player in await gameContext.GetPlayersAsync().ConfigureAwait(false))
            {
                if (!IsQaAccount(player) || player.SelectedCharacter?.Name is not { } name || LiveBossFor(player) is not { } live || !ReferenceEquals(live.Monster, boss))
                {
                    continue;
                }

                var combat = (player as GameLogic.Offline.OfflinePlayer)?.CombatTelemetry;
                string target = combat?.Target switch
                {
                    null => "-",
                    var t when ReferenceEquals(t, boss) => "the boss",
                    Monster m => m.Definition.Designation.ValueInNeutralLanguage,
                    var t => t.ToString() ?? "?",
                };
                logger.LogInformation(
                    "QA boss {Boss} hunter {Character} ({Kind}) on {Map} at {X},{Y}, {Distance:0.0} tiles: activity {Activity}, target {Target}, range {Range}, skill {Skill}, boss attacks {Attacks}, hits {Hits}, damage {Damage}, safety {Safety}.",
                    fight.Definition.Name,
                    name,
                    player is GameLogic.Offline.OfflinePlayer ? "offline" : "online",
                    player.CurrentMap?.Definition.Name.ValueInNeutralLanguage ?? "-",
                    player.Position.X,
                    player.Position.Y,
                    player.CurrentMap == boss.CurrentMap ? player.Position.EuclideanDistanceTo(boss.Position) : double.NaN,
                    System.Text.Json.JsonSerializer.Serialize(MUIdleHuntTracker.Snapshot(player)),
                    target,
                    combat?.AttackRange.ToString() ?? "-",
                    combat?.Skill ?? "-",
                    combat?.PriorityAttacks.ToString() ?? "-",
                    fight.Hits.TryGetValue(name, out var hits) ? hits : 0,
                    fight.Damage.TryGetValue(name, out var damage) ? damage : 0,
                    GameLogic.Offline.CombatHandler.ExplainUnsafe(player, boss.Definition, requireQuickKill: false) ?? "fights it");
            }
        }
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
        /// <summary>Gets a value indicating whether this is a QA instance (QA trigger, QA accounts only).</summary>
        public bool IsQa { get; init; }

        public ConcurrentDictionary<string, long> Damage { get; } = new();

        /// <summary>Gets the hits per character, blocked ones (no damage) included.</summary>
        public ConcurrentDictionary<string, int> Hits { get; } = new();

        public ConcurrentDictionary<string, Player> Players { get; } = new();
    }
}
