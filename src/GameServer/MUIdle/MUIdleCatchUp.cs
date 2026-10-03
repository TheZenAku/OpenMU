// <copyright file="MUIdleCatchUp.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Runtime.CompilerServices;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;

/// <summary>
/// Catch-up and newcomer experience bonus, server-side and visible: a character more than a week
/// behind the season (fewer resets than last week's cap) earns <see cref="MUIdleConfiguration.CatchUpPerReset"/>
/// more experience per missing reset, a character created less than <see cref="MUIdleConfiguration.NewcomerDays"/>
/// ago earns <see cref="MUIdleConfiguration.NewcomerBonus"/> more; together at most <see cref="MUIdleConfiguration.MaximumBonus"/>.
/// The bonus is an element on <see cref="Stats.BonusExperienceRate"/>, which upstream adds to every
/// experience gain (normal and master), like happy hour does.
/// </summary>
internal static class MUIdleCatchUp
{
    // Keyed by the attribute system: choosing another character builds a new one.
    private static readonly ConditionalWeakTable<object, SimpleElement> Elements = new();

    /// <summary>
    /// Computes the bonus parts of a character.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="configuration">The MUIdle configuration.</param>
    /// <param name="now">The time.</param>
    /// <returns>The catch-up part, the newcomer part and the (capped) total, as fractions (0.1 = +10 %).</returns>
    public static (double CatchUp, double Newcomer, double Total) Bonus(Player player, MUIdleConfiguration configuration, DateTime now)
        => Bonus((int)(player.Attributes?[Stats.Resets] ?? 0), player.SelectedCharacter?.CreateDate ?? DateTime.MinValue, configuration, now);

    /// <summary>
    /// Computes the bonus parts from the resets and the creation date.
    /// </summary>
    /// <param name="resets">The character's resets.</param>
    /// <param name="created">When the character was created.</param>
    /// <param name="configuration">The MUIdle configuration.</param>
    /// <param name="now">The time.</param>
    /// <returns>The catch-up part, the newcomer part and the (capped) total.</returns>
    internal static (double CatchUp, double Newcomer, double Total) Bonus(int resets, DateTime created, MUIdleConfiguration configuration, DateTime now)
    {
        var lastWeeksCap = configuration.SeasonResetsPerWeek * (MUIdleSeason.Week(configuration, now) - 1);
        var behind = Math.Max(0, lastWeeksCap - resets);
        var catchUp = behind * Math.Max(0, configuration.CatchUpPerReset);
        var newcomer = configuration.NewcomerDays > 0 && now - created < TimeSpan.FromDays(configuration.NewcomerDays)
            ? Math.Max(0, configuration.NewcomerBonus)
            : 0;
        return (catchUp, newcomer, Math.Min(Math.Max(0, configuration.MaximumBonus), catchUp + newcomer));
    }

    /// <summary>
    /// Brings the bonus element of every player of the game context up to date.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    /// <param name="configuration">The MUIdle configuration.</param>
    public static async ValueTask ApplyAsync(IGameContext gameContext, MUIdleConfiguration configuration)
    {
        var now = DateTime.UtcNow;
        foreach (var player in await gameContext.GetPlayersAsync().ConfigureAwait(false))
        {
            if (player.Attributes is not { } attributes || player.SelectedCharacter is null)
            {
                continue;
            }

            var total = (float)Bonus(player, configuration, now).Total;
            var element = Elements.GetValue(attributes, _ => new SimpleElement(0, AggregateType.AddRaw));
            if (Math.Abs(element.Value - total) < 0.0001f)
            {
                continue;
            }

            if (element.Value == 0 && total > 0)
            {
                attributes.AddElement(element, Stats.BonusExperienceRate);
            }

            element.Value = total;
            if (total == 0)
            {
                attributes.RemoveElement(element, Stats.BonusExperienceRate);
            }
        }
    }
}
