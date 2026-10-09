// <copyright file="CombatObservations.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Offline;

using System.Runtime.CompilerServices;

/// <summary>
/// MUIdle (HUNT v2): what the hunting engine saw its character deal - the damage of a strike before the
/// monster's defense, and how many monsters an area strike hits. The experience rating of hunting spots
/// (<see cref="Bots.HuntingSpotRating"/>) judges a kill by it rather than by the attack power the engine
/// estimates from the attributes, which missed a level 380 wizard's skills by a factor of ten (10-09).
/// Moving averages: they follow new gear and skills.
/// </summary>
internal static class CombatObservations
{
    /// <summary>The strikes before an observation counts.</summary>
    private const int MinimumStrikes = 20;

    /// <summary>The weight of a new strike in the averages.</summary>
    private const double Weight = 0.05;

    private static readonly ConditionalWeakTable<Player, Tally> Tallies = new();

    /// <summary>
    /// Records a strike.
    /// </summary>
    /// <param name="player">The striking character.</param>
    /// <param name="targetsHit">The monsters it hit.</param>
    /// <param name="rawDamage">The damage per monster before its defense, if a monster lived through it.</param>
    /// <param name="rawDamageAtLeast">The least damage per monster before its defense, from monsters the strike killed.</param>
    public static void RecordStrike(Player player, int targetsHit, double? rawDamage, double? rawDamageAtLeast)
    {
        if (targetsHit <= 0)
        {
            return;
        }

        var tally = Tallies.GetOrCreateValue(player);
        lock (tally)
        {
            tally.Strikes++;
            tally.TargetsPerStrike = tally.Strikes == 1 ? targetsHit : tally.TargetsPerStrike + (Weight * (targetsHit - tally.TargetsPerStrike));
            if (rawDamage is { } damage)
            {
                tally.RawDamage = tally.RawDamage <= 0 ? damage : tally.RawDamage + (Weight * (damage - tally.RawDamage));
            }

            if (rawDamageAtLeast is { } least && least > tally.RawDamage)
            {
                tally.RawDamage = least;
            }
        }
    }

    /// <summary>
    /// Gets what the character's strikes deal, once there were enough of them.
    /// </summary>
    /// <param name="player">The character.</param>
    /// <returns>The damage per monster before its defense and the monsters per strike, or <c>null</c>.</returns>
    public static (double RawDamage, double TargetsPerStrike)? Observed(Player player)
    {
        if (!Tallies.TryGetValue(player, out var tally))
        {
            return null;
        }

        lock (tally)
        {
            return tally.Strikes >= MinimumStrikes && tally.RawDamage > 0 ? (tally.RawDamage, Math.Max(1, tally.TargetsPerStrike)) : null;
        }
    }

    private sealed class Tally
    {
        public int Strikes;
        public double RawDamage;
        public double TargetsPerStrike;
    }
}
