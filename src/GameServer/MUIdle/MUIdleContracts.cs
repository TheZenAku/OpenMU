// <copyright file="MUIdleContracts.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// One daily contract.
/// </summary>
public sealed class DailyContract
{
    /// <summary>Gets or sets the kind: <c>kills</c>, <c>mapKills</c> or <c>huntMinutes</c>.</summary>
    public string Kind { get; set; } = "kills";

    /// <summary>Gets or sets the amount to reach.</summary>
    public int Target { get; set; }

    /// <summary>Gets or sets the map of a <c>mapKills</c> contract.</summary>
    public int? MapNumber { get; set; }

    /// <summary>Gets or sets the map name of a <c>mapKills</c> contract.</summary>
    public string? MapName { get; set; }

    /// <summary>Gets or sets the progress.</summary>
    public double Progress { get; set; }

    /// <summary>Gets or sets the zen paid on completion.</summary>
    public int RewardZen { get; set; }

    /// <summary>Gets or sets a value indicating whether the reward was paid.</summary>
    public bool Rewarded { get; set; }
}

/// <summary>
/// The contracts of one character for one UTC day.
/// </summary>
public sealed class DailyContracts
{
    /// <summary>Gets or sets the UTC day (yyyy-MM-dd).</summary>
    public string Day { get; set; } = string.Empty;

    /// <summary>Gets or sets the contracts.</summary>
    public List<DailyContract> Contracts { get; set; } = [];
}

/// <summary>
/// Daily contracts: three per character and UTC day - defeat monsters, defeat them on a map the
/// character can hunt, hunt for a while - made from the character's level and resets alone (no
/// randomness: the same character gets the same day twice). Progress comes from the server's own
/// events (kills, the hunt state) online and offline alike; a finished contract pays its zen at once.
/// Kept per player in memory and written to <see cref="MUIdleCharacterState.ContractsJson"/> inside the
/// player's persistence lock, so the reward and the "paid" flag are saved by the same save.
/// </summary>
internal static class MUIdleContracts
{
    private const string LogCategory = "MUnique.OpenMU.GameServer.MUIdle.Contracts";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly ConditionalWeakTable<Player, Holder> Live = new();

    /// <summary>
    /// Gets today's UTC day key.
    /// </summary>
    /// <param name="now">The time.</param>
    /// <returns>The key.</returns>
    public static string Day(DateTime now) => now.ToUniversalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Makes the contracts of a day.
    /// </summary>
    /// <param name="level">The character level.</param>
    /// <param name="resets">The character resets.</param>
    /// <param name="map">The highest map the character can hunt now (number and name), if any.</param>
    /// <param name="day">The day key.</param>
    /// <returns>The contracts.</returns>
    public static DailyContracts Generate(int level, int resets, (int Number, string Name)? map, string day)
    {
        var scale = 1 + (level / 50) + resets;
        var contracts = new List<DailyContract>
        {
            new() { Kind = "kills", Target = 150 + level, RewardZen = 20_000 * scale },
            new() { Kind = "huntMinutes", Target = 60, RewardZen = 15_000 * scale },
        };
        if (map is { } m)
        {
            contracts.Insert(1, new DailyContract { Kind = "mapKills", Target = 80 + (level / 2), MapNumber = m.Number, MapName = m.Name, RewardZen = 30_000 * scale });
        }

        return new DailyContracts { Day = day, Contracts = contracts };
    }

    /// <summary>
    /// Gets the player's contracts of today: loaded from the state, or made new on a new day.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="state">The character's MUIdle state.</param>
    /// <returns>The contracts.</returns>
    public static DailyContracts For(Player player, MUIdleCharacterState? state)
    {
        var holder = Live.GetValue(player, _ => new Holder());
        var today = Day(DateTime.UtcNow);
        lock (holder)
        {
            if (holder.Contracts is null && state?.ContractsJson is { } json)
            {
                try
                {
                    holder.Contracts = JsonSerializer.Deserialize<DailyContracts>(json, JsonOptions);
                }
                catch (JsonException)
                {
                    holder.Contracts = null;
                }
            }

            if (holder.Contracts is null || holder.Contracts.Day != today)
            {
                var best = MUIdleHuntMaps.Options(player).Where(o => o.Available).MaxBy(o => o.MinLevel);
                holder.Contracts = Generate(
                    (int)(player.Attributes?[Stats.Level] ?? 1),
                    (int)(player.Attributes?[Stats.Resets] ?? 0),
                    best is null ? null : ((int)best.Map.Number, best.Map.Name.ValueInNeutralLanguage),
                    today);
                holder.Dirty = true;
            }

            return holder.Contracts;
        }
    }

    /// <summary>
    /// Gets the contracts for the client.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="state">The character's MUIdle state.</param>
    /// <returns>The contracts, serializable to JSON.</returns>
    public static object Snapshot(Player player, MUIdleCharacterState state)
    {
        var contracts = For(player, state);
        lock (Live.GetValue(player, _ => new Holder()))
        {
            return new
            {
                day = contracts.Day,
                contracts = contracts.Contracts.Select(c => new { kind = c.Kind, target = c.Target, progress = Math.Min(c.Target, (int)c.Progress), mapName = c.MapName, rewardZen = c.RewardZen, rewarded = c.Rewarded }).ToList(),
            };
        }
    }

    /// <summary>
    /// Counts a monster kill of the player.
    /// </summary>
    /// <param name="player">The player.</param>
    public static void OnKill(Player player)
    {
        if (!Live.TryGetValue(player, out var holder))
        {
            return;
        }

        lock (holder)
        {
            if (holder.Contracts is not { } contracts || contracts.Day != Day(DateTime.UtcNow))
            {
                return;
            }

            var map = (int?)player.CurrentMap?.Definition.Number;
            foreach (var contract in contracts.Contracts.Where(c => !c.Rewarded))
            {
                if (contract.Kind == "kills" || (contract.Kind == "mapKills" && contract.MapNumber == map))
                {
                    contract.Progress++;
                    holder.Dirty = true;
                }
            }
        }
    }

    /// <summary>
    /// Adds hunting time, pays finished contracts and writes changed contracts into the state.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    /// <param name="minutes">The minutes since the last call.</param>
    public static async ValueTask TickAsync(IGameContext gameContext, double minutes)
    {
        foreach (var player in await gameContext.GetPlayersAsync().ConfigureAwait(false))
        {
            if (player.SelectedCharacter is not { } character
                || player.PlayerState.CurrentState != PlayerState.EnteredWorld)
            {
                continue;
            }

            try
            {
                if (!Live.TryGetValue(player, out var holder))
                {
                    // Offline hunters never get a state packet: their contracts are loaded here.
                    await player.RunPersistenceExclusiveAsync(async () =>
                    {
                        For(player, await player.PersistenceContext.GetMUIdleCharacterStateAsync(character.Id).ConfigureAwait(false));
                        return true;
                    }).ConfigureAwait(false);
                    holder = Live.GetValue(player, _ => new Holder());
                }

                await TickAsync(player, character, holder, minutes).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                player.Logger.LogWarning(ex, "MUIdle: daily contracts of {Player} failed.", player);
            }
        }
    }

    private static async ValueTask TickAsync(Player player, Character character, Holder holder, double minutes)
    {
        var hunting = player is HuntOfflinePlayer || player.Attributes?[Stats.IsMuHelperActive] > 0;
        var finished = new List<DailyContract>();
        string? json = null;
        lock (holder)
        {
            if (holder.Contracts is not { } contracts)
            {
                return;
            }

            foreach (var contract in contracts.Contracts.Where(c => !c.Rewarded))
            {
                if (hunting && contract.Kind == "huntMinutes")
                {
                    contract.Progress += minutes;
                    holder.Dirty = true;
                }

                if (contract.Progress >= contract.Target)
                {
                    contract.Rewarded = true;
                    finished.Add(contract);
                    holder.Dirty = true;
                }
            }

            if (holder.Dirty)
            {
                json = JsonSerializer.Serialize(contracts, JsonOptions);
                holder.Dirty = false;
            }
        }

        if (json is null)
        {
            return;
        }

        await player.RunPersistenceExclusiveAsync(async () =>
        {
            foreach (var contract in finished)
            {
                // At the zen limit the contract counts as done anyway; the log says so.
                var paid = player.TryAddMoney(contract.RewardZen);
                player.GameContext.LoggerFactory.CreateLogger(LogCategory).LogInformation(
                    "MUIdle contract {Character} {Day}: {Kind} {Target} done -> {Zen} zen{Paid}.",
                    player.Name,
                    Day(DateTime.UtcNow),
                    contract.Kind,
                    contract.Target,
                    contract.RewardZen,
                    paid ? string.Empty : " (not paid: zen limit)");
            }

            var state = await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false);
            state.ContractsJson = json;
            return true;
        }).ConfigureAwait(false);

        foreach (var contract in finished)
        {
            try
            {
                var english = $"Daily contract done: +{contract.RewardZen:N0} zen";
                var portuguese = $"Contrato diário concluído: +{contract.RewardZen:N0} zen";
                var text = player.Culture.TwoLetterISOLanguageName == "pt" ? portuguese : english;
                await player.InvokeViewPlugInAsync<IShowMessagePlugIn>(p => p.ShowMessageAsync(text, MessageType.BlueNormal)).ConfigureAwait(false);
            }
            catch
            {
                // No view (an offline hunter): the summary and the log keep it.
            }
        }
    }

    private sealed class Holder
    {
        public DailyContracts? Contracts { get; set; }

        public bool Dirty { get; set; }
    }
}

/// <summary>
/// Counts the monster kills of players (and their summons) for the daily contracts.
/// </summary>
[PlugIn]
[Display(Name = "MUIdle daily contracts: kills", Description = "Counts monster kills for the MUIdle daily contracts.")]
[Guid("3D8F1A6C-5E24-4B97-A0D3-7C9E2B4F6A18")]
public sealed class MUIdleContractKillPlugIn : IAttackableGotKilledPlugIn
{
    /// <inheritdoc />
    public ValueTask AttackableGotKilledAsync(IAttackable killed, IAttacker? killer)
    {
        if (killed is Monster { SummonedBy: null } && (killer as Player ?? (killer as Monster)?.SummonedBy) is { } player)
        {
            MUIdleContracts.OnKill(player);
        }

        return ValueTask.CompletedTask;
    }
}
