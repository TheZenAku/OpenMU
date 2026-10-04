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
    /// <summary>
    /// How often an online HUNT is asked to go on after its pause before it waits for the player.
    /// </summary>
    internal const int ResumeAttempts = 3;

    private static readonly TimeSpan ResumeInterval = TimeSpan.FromMinutes(1);

    private static readonly ConditionalWeakTable<Player, PauseState> Pauses = new();

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
    /// Gets the MU Helper fee per minute.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The fee per minute.</returns>
    public static double FeePerMinute(Player player)
        => HelperFee(player) / Math.Max(1, (Configuration(player)?.PayInterval ?? TimeSpan.FromMinutes(5)).TotalMinutes);

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
        var pause = Pauses.GetValue(player, _ => new PauseState());
        var helperOn = player.Attributes?[Stats.IsMuHelperActive] > 0;
        var now = DateTime.UtcNow;
        switch (Step(pause, helperOn, player.Money, fee, player.IsAtSafezone(), now))
        {
            case PauseStep.Pause:
                MUIdleHuntTracker.ReportPaused(player, player.Money, fee);
                Logger(player).LogInformation("MUIdle hunt {Character}: HUNT paused - {Money} zen, the MU Helper takes {Fee}.", player.Name, player.Money, fee);
                break;
            case PauseStep.StillPaused:
                MUIdleHuntTracker.ReportPaused(player, player.Money, fee);
                break;
            case PauseStep.GiveUp:
                MUIdleHuntTracker.Report(player, HuntActivityKind.Idle);
                Logger(player).LogInformation("MUIdle hunt {Character}: HUNT did not resume after the pause; it waits for the player.", player.Name);
                break;
            case PauseStep.Resume:
                // The client starts its helper only outside a safezone: in town it walks to a hunting ground first.
                var ground = player.IsAtSafezone() && player.CurrentMap is { } map ? MUIdleHuntMaps.PickGround(player, map) : null;
                MUIdleHuntTracker.Report(player, HuntActivityKind.Travelling);
                Logger(player).LogInformation(
                    "MUIdle hunt {Character}: HUNT resumes - {Money} zen (attempt {Attempt}, ground {Ground}).", player.Name, player.Money, pause.Attempts, ground?.ToString() ?? "-");
                var state = await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false);
                await MUIdleSessions.SendStateAsync(player, state, resume: true, ground: ground).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Advances the fee pause of an online HUNT by one check: it pauses when the helper is off and the
    /// fee cannot be paid outside a safezone (in town the helper stops by itself), stays paused while
    /// the money is short, then asks the client to go on - once a minute, <see cref="ResumeAttempts"/>
    /// times - and finally leaves HUNT to the player. A running helper ends the pause.
    /// </summary>
    /// <param name="pause">The pause of the player, changed by the step.</param>
    /// <param name="helperOn">Whether the MU Helper runs.</param>
    /// <param name="money">The player's Zen.</param>
    /// <param name="fee">The fee due per pay interval.</param>
    /// <param name="atSafezone">Whether the player stands in a safezone.</param>
    /// <param name="now">The time of the check.</param>
    /// <returns>What to do now.</returns>
    internal static PauseStep Step(PauseState pause, bool helperOn, long money, long fee, bool atSafezone, DateTime now)
    {
        if (helperOn)
        {
            pause.Clear();
            return PauseStep.None;
        }

        if (!pause.Paused)
        {
            if (fee <= 0 || money >= fee || atSafezone)
            {
                return PauseStep.None;
            }

            pause.Paused = true;
            return PauseStep.Pause;
        }

        if (money < fee)
        {
            return PauseStep.StillPaused;
        }

        // The money is there again (a sale, the vault, a reward): HUNT goes on by itself.
        if (now - pause.ResumedUtc < ResumeInterval)
        {
            return PauseStep.None;
        }

        if (pause.Attempts >= ResumeAttempts)
        {
            // The client did not start (the player keeps it in town): HUNT waits for the button.
            pause.Clear();
            return PauseStep.GiveUp;
        }

        pause.Attempts++;
        pause.ResumedUtc = now;
        return PauseStep.Resume;
    }

    /// <summary>
    /// Whether the player's online HUNT is paused for the fee (it waits for the money, or for its resume).
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns><c>true</c>, if so.</returns>
    public static bool IsPaused(Player player) => Pauses.TryGetValue(player, out var pause) && pause.Paused;

    /// <summary>
    /// Ends the fee pause of the player (HUNT was switched by the player or resumed by the client).
    /// </summary>
    /// <param name="player">The player.</param>
    public static void ClearPause(Player player)
    {
        if (Pauses.TryGetValue(player, out var pause))
        {
            pause.Clear();
        }
    }

    private static ILogger Logger(Player player) => player.GameContext.LoggerFactory.CreateLogger("MUnique.OpenMU.GameServer.MUIdle.Hunt");

    private static MuHelperConfiguration? Configuration(Player player)
        => player.GameContext.FeaturePlugIns.GetPlugIn<MuHelperFeaturePlugIn>()?.Configuration;

    /// <summary>
    /// What one check of the fee pause does.
    /// </summary>
    internal enum PauseStep
    {
        /// <summary>Nothing to do.</summary>
        None,

        /// <summary>HUNT pauses now.</summary>
        Pause,

        /// <summary>HUNT stays paused (the money is still short).</summary>
        StillPaused,

        /// <summary>The client is asked to resume HUNT.</summary>
        Resume,

        /// <summary>The resume did not happen: HUNT waits for the player.</summary>
        GiveUp,
    }

    /// <summary>
    /// The fee pause of one player.
    /// </summary>
    internal sealed class PauseState
    {
        public bool Paused { get; set; }

        public int Attempts { get; set; }

        public DateTime ResumedUtc { get; set; }

        public void Clear()
        {
            this.Paused = false;
            this.Attempts = 0;
            this.ResumedUtc = default;
        }
    }
}
