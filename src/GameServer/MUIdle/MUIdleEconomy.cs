// <copyright file="MUIdleEconomy.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.MuHelper;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameServer.RemoteView;

/// <summary>
/// The money side of HUNT (D20): the MU Helper fee the hunt pays every pay interval (the server's own
/// formula, <see cref="MuHelperZenCostCalculator"/>), the reserve it keeps for it, and the pause when
/// the fee cannot be paid - shown to the player, and lifted as soon as the money is there again.
/// </summary>
internal static class MUIdleEconomy
{
    private static readonly ConditionalWeakTable<Player, StrongBox<bool>> Paused = new();

    /// <summary>
    /// Gets the MU Helper fee due per pay interval for the player now (offline: the stage of its
    /// session; online: the first stage - the client's helper keeps its own start).
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The fee; 0 without a fee.</returns>
    public static long HelperFee(Player player)
    {
        if (Configuration(player) is not { } configuration || player.Account?.IsBot == true)
        {
            return 0;
        }

        var start = player is OfflinePlayer offline ? offline.StartTimestamp : DateTime.UtcNow;
        return MuHelperZenCostCalculator.Calculate(player, configuration, start);
    }

    /// <summary>
    /// Gets the Zen the hunt keeps for its running costs: the MU Helper fees of the configured reserve
    /// minutes (<see cref="MUIdleConfiguration.HuntReserveMinutes"/>). Discretionary spending (map
    /// changes, quest fees) leaves at least this much; repairs and potions, which keep the character
    /// alive, are not held back.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The reserve.</returns>
    public static long Reserve(Player player)
        => Reserve(HelperFee(player), Configuration(player)?.PayInterval ?? TimeSpan.FromMinutes(5), MUIdleSessions.GetConfiguration(player.GameContext).HuntReserveMinutes);

    /// <summary>
    /// The reserve for a fee per pay interval and reserve minutes (whole payments, at least one with a fee).
    /// </summary>
    /// <param name="fee">The fee per pay interval.</param>
    /// <param name="payInterval">The pay interval.</param>
    /// <param name="reserveMinutes">The minutes to keep.</param>
    /// <returns>The reserve.</returns>
    internal static long Reserve(long fee, TimeSpan payInterval, int reserveMinutes)
    {
        if (fee <= 0 || reserveMinutes <= 0 || payInterval <= TimeSpan.Zero)
        {
            return 0;
        }

        var payments = Math.Max(1, (int)Math.Ceiling(reserveMinutes / payInterval.TotalMinutes));
        return fee * payments;
    }

    /// <summary>
    /// Whether the player is short of Zen for its running costs (below the reserve).
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns><c>true</c>, if so.</returns>
    public static bool FundsLow(Player player) => player.Money < Reserve(player);

    /// <summary>
    /// Shows the pause of an online HUNT whose MU Helper fee could not be paid (the client's helper
    /// stopped), and resumes it once the player has the fee again.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    public static async ValueTask MaintainAsync(IGameContext gameContext)
    {
        foreach (var player in (await gameContext.GetPlayersAsync().ConfigureAwait(false)).OfType<RemotePlayer>())
        {
            try
            {
                await CheckAsync(player).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                player.Logger.LogWarning(ex, "MUIdle: HUNT economy check of {Player} failed.", player);
            }
        }
    }

    private static async ValueTask CheckAsync(RemotePlayer player)
    {
        if (player.PlayerState.CurrentState != PlayerState.EnteredWorld
            || !player.IsAlive
            || player.CurrentMiniGame is not null
            || player.SelectedCharacter is not { } character
            || !MUIdleHuntTracker.HasHuntIntent(player))
        {
            return;
        }

        var fee = HelperFee(player);
        var paused = Paused.GetValue(player, _ => new StrongBox<bool>());
        var helperOn = player.Attributes?[Stats.IsMuHelperActive] > 0;
        if (!paused.Value)
        {
            if (fee > 0 && !helperOn && player.Money < fee && !player.IsAtSafezone())
            {
                paused.Value = true;
                MUIdleHuntTracker.ReportPaused(player, player.Money, fee);
                player.GameContext.LoggerFactory.CreateLogger("MUnique.OpenMU.GameServer.MUIdle.Hunt").LogInformation(
                    "MUIdle hunt {Character}: HUNT paused - {Money} zen, the MU Helper takes {Fee}.", player.Name, player.Money, fee);
            }

            return;
        }

        if (helperOn)
        {
            paused.Value = false;
            return;
        }

        if (player.Money >= fee)
        {
            // The money is there again (a sale, the vault, a reward): HUNT goes on by itself.
            paused.Value = false;
            MUIdleHuntTracker.Report(player, HuntActivityKind.Hunting);
            var state = await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false);
            await MUIdleSessions.SendStateAsync(player, state, resume: true).ConfigureAwait(false);
        }
        else
        {
            MUIdleHuntTracker.ReportPaused(player, player.Money, fee);
        }
    }

    private static MuHelperConfiguration? Configuration(Player player)
        => player.GameContext.FeaturePlugIns.GetPlugIn<MuHelperFeaturePlugIn>()?.Configuration;
}
