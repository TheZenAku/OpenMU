// <copyright file="MUIdleSeason.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Resets;

/// <summary>
/// The season: a reset cap that rises every week from the season start, so nobody runs away with the
/// server in its first days and late starters can catch up. Upstream's reset feature stays in charge
/// of the reset itself; MUIdle only moves its limit (<see cref="ResetConfiguration.ResetLimit"/>).
/// </summary>
internal static class MUIdleSeason
{
    /// <summary>
    /// Gets the season week (1 in the first seven days).
    /// </summary>
    /// <param name="configuration">The MUIdle configuration.</param>
    /// <param name="now">The time.</param>
    /// <returns>The week.</returns>
    public static int Week(MUIdleConfiguration configuration, DateTime now)
        => Math.Max(1, (int)Math.Floor((now - configuration.SeasonStartUtc).TotalDays / 7) + 1);

    /// <summary>
    /// Gets the reset cap of the week, or <c>null</c> when the season sets none.
    /// </summary>
    /// <param name="configuration">The MUIdle configuration.</param>
    /// <param name="now">The time.</param>
    /// <returns>The cap.</returns>
    public static int? Cap(MUIdleConfiguration configuration, DateTime now)
        => configuration.SeasonResetsPerWeek > 0 ? configuration.SeasonResetsPerWeek * Week(configuration, now) : null;

    /// <summary>
    /// Gets when the cap rises next.
    /// </summary>
    /// <param name="configuration">The MUIdle configuration.</param>
    /// <param name="now">The time.</param>
    /// <returns>The time of the next raise.</returns>
    public static DateTime NextRaise(MUIdleConfiguration configuration, DateTime now)
        => configuration.SeasonStartUtc.AddDays(7 * Week(configuration, now));

    /// <summary>
    /// Sets the reset feature's limit to the cap of the week.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    /// <param name="configuration">The MUIdle configuration.</param>
    public static void Apply(IGameContext gameContext, MUIdleConfiguration configuration)
    {
        if (Cap(configuration, DateTime.UtcNow) is { } cap
            && gameContext.FeaturePlugIns.GetPlugIn<ResetFeaturePlugIn>()?.Configuration is { } resets
            && resets.ResetLimit != cap)
        {
            resets.ResetLimit = cap;
        }
    }

    /// <summary>
    /// Gets the season and the character's next reset for the client's progression window.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="configuration">The MUIdle configuration.</param>
    /// <returns>The progression, serializable to JSON.</returns>
    public static object Snapshot(Player player, MUIdleConfiguration configuration)
    {
        var now = DateTime.UtcNow;
        var resetConfiguration = player.GameContext.FeaturePlugIns.GetPlugIn<ResetFeaturePlugIn>()?.Configuration;
        var resets = (int)(player.Attributes?[Stats.Resets] ?? 0);
        var level = (int)(player.Attributes?[Stats.Level] ?? 0);
        var cap = Cap(configuration, now) ?? resetConfiguration?.ResetLimit;
        ResetProgression? next = resetConfiguration is null
            ? null
            : ResetProgressionCalculator.Calculate(resets, (int)(player.Attributes?[Stats.PointsPerReset] ?? 0), resetConfiguration);
        string? reason = resetConfiguration is null ? "disabled"
            : level < resetConfiguration.RequiredLevel ? "level"
            : cap is { } limit && limit > 0 && resets + 1 > limit ? "cap"
            : next is { } progression && player.Money < progression.RequiredZen ? "zen"
            : null;
        return new
        {
            season = configuration.SeasonNumber,
            week = Week(configuration, now),
            resetCap = cap,
            nextRaiseUtc = configuration.SeasonResetsPerWeek > 0 ? NextRaise(configuration, now) : (DateTime?)null,
            resetsPerWeek = configuration.SeasonResetsPerWeek,
            resets,
            level,
            requiredLevel = resetConfiguration?.RequiredLevel,
            requiredZen = next?.RequiredZen,
            levelAfterReset = resetConfiguration?.LevelAfterReset,
            canReset = reason is null,
            reason,
            bonus = MUIdleCatchUp.Bonus(player, configuration, now) is var bonus
                ? new { catchUp = bonus.CatchUp, newcomer = bonus.Newcomer, total = bonus.Total }
                : null,
        };
    }
}
