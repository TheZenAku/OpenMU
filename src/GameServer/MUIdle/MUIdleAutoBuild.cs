// <copyright file="MUIdleAutoBuild.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Auto build: spends the free level up points of a character by its class's stat weights, with the
/// checks of the upstream IncreaseStatsAction (a stat the class may raise, its maximum value, the points
/// there are) - each stat in one step, told to the client in one packet.
/// </summary>
internal static class MUIdleAutoBuild
{
    /// <summary>The largest stat the client can be told (an unsigned 16-bit field).</summary>
    private const float ProtocolStatMaximum = ushort.MaxValue;

    /// <summary>
    /// The build presets a class can choose instead of its default (see <see cref="MUIdleSettings.BuildPreset"/>).
    /// </summary>
    public static readonly string[] Presets = ["support", "energy", "raven"];

    /// <summary>
    /// Gets the stat weights of a character class and build preset (first = main stat, which also takes
    /// the remainder): Elf "support" (energy), Magic Gladiator "energy", Dark Lord "raven" (command);
    /// any other preset is the class default.
    /// </summary>
    /// <param name="classNumber">The character class number (any evolution of the line).</param>
    /// <param name="preset">The build preset, or <c>null</c> for the default.</param>
    /// <returns>The weights.</returns>
    public static (AttributeDefinition Stat, int Weight)[] GetWeights(byte classNumber, string? preset) => (classNumber, preset) switch
    {
        (>= 8 and <= 11, "support") => [(Stats.BaseEnergy, 5), (Stats.BaseAgility, 2), (Stats.BaseVitality, 2)],
        (>= 12 and <= 15, "energy") => [(Stats.BaseEnergy, 5), (Stats.BaseStrength, 2), (Stats.BaseAgility, 2)],
        (>= 16 and <= 19, "raven") => [(Stats.BaseLeadership, 5), (Stats.BaseStrength, 2), (Stats.BaseEnergy, 2), (Stats.BaseAgility, 1)],
        _ => GetWeights(classNumber),
    };

    /// <summary>
    /// Gets the default stat weights of a character class (first = main stat, which also takes the remainder).
    /// </summary>
    /// <param name="classNumber">The character class number (any evolution of the line).</param>
    /// <returns>The weights.</returns>
    public static (AttributeDefinition Stat, int Weight)[] GetWeights(byte classNumber) => classNumber switch
    {
        <= 3 => [(Stats.BaseEnergy, 5), (Stats.BaseAgility, 2), (Stats.BaseVitality, 2)],                                  // Dark Wizard
        <= 7 => [(Stats.BaseStrength, 5), (Stats.BaseAgility, 2), (Stats.BaseVitality, 2), (Stats.BaseEnergy, 1)],          // Dark Knight
        <= 11 => [(Stats.BaseAgility, 5), (Stats.BaseEnergy, 2), (Stats.BaseVitality, 2), (Stats.BaseStrength, 1)],         // Fairy Elf
        <= 15 => [(Stats.BaseStrength, 5), (Stats.BaseEnergy, 3), (Stats.BaseAgility, 2)],                                   // Magic Gladiator
        <= 19 => [(Stats.BaseStrength, 4), (Stats.BaseLeadership, 3), (Stats.BaseEnergy, 2), (Stats.BaseAgility, 1)],       // Dark Lord
        <= 23 => [(Stats.BaseEnergy, 5), (Stats.BaseAgility, 2), (Stats.BaseVitality, 2)],                                  // Summoner
        _ => [(Stats.BaseStrength, 3), (Stats.BaseVitality, 3), (Stats.BaseAgility, 2), (Stats.BaseEnergy, 1)],             // Rage Fighter
    };

    /// <summary>
    /// Spends the free level up points of the player's character. Must run where the player's state may
    /// be mutated (its packet handling, or the offline AI tick).
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="preset">The build preset; the player's last known settings when <c>null</c>.</param>
    public static async ValueTask DistributeAsync(Player player, string? preset = null)
    {
        if (player.SelectedCharacter is not { LevelUpPoints: > 0 } character || character.CharacterClass is not { } characterClass)
        {
            return;
        }

        var weights = GetWeights(characterClass.Number, preset ?? MUIdleHuntTracker.GetSettings(player)?.BuildPreset);
        var points = character.LevelUpPoints;
        var totalWeight = weights.Sum(w => w.Weight);
        var shares = weights.Select(w => (w.Stat, Amount: points * w.Weight / totalWeight)).ToArray();
        shares[0].Amount += points - shares.Sum(s => s.Amount);

        // Upstream tells a client of an increase by the short stat-increase response, which says "one
        // more point" - several points at once it answers by re-sending the whole character and warping
        // the player onto its own position (a workaround for old clients). So the auto build used to
        // spend an online character's points one by one: 875 points were 875 attribute recalculations
        // and 875 responses in one burst, and the browser stuttered for seconds (10-14). The extended
        // response carries the amount and the new maximums, and the client applies it as such.
        foreach (var (stat, amount) in shares)
        {
            var added = Increase(player, character, stat, amount);
            if (added > 0 && player is RemotePlayer { Connection: { } connection } && player.Attributes is { } attributes)
            {
                for (var told = 0; told < added; told += ushort.MaxValue)
                {
                    await connection.SendCharacterStatIncreaseResponseExtendedAsync(
                        stat.GetStatType(),
                        (ushort)Math.Min(added - told, ushort.MaxValue),
                        (uint)attributes[Stats.MaximumHealth],
                        (uint)attributes[Stats.MaximumMana],
                        (uint)attributes[Stats.MaximumShield],
                        (uint)attributes[Stats.MaximumAbility]).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Raises a stat by up to the amount, as far as the class may raise it, its maximum allows and the
    /// free points reach.
    /// </summary>
    /// <returns>The points spent.</returns>
    private static int Increase(Player player, Character character, AttributeDefinition stat, int amount)
    {
        if (character.CharacterClass?.GetStatAttribute(stat) is not { IncreasableByPlayer: true, Attribute: { } attribute }
            || player.Attributes is not { } attributes)
        {
            return 0;
        }

        amount = Math.Min(amount, character.LevelUpPoints);

        // The game's own maximum, else what the protocol carries: a stat goes to the client as 16 bits, and
        // past 65 535 it shows wrapped (test0Dl's two million strength read 37 438, 10-14). Points above
        // stay unspent.
        var maximum = attribute.MaximumValue ?? ProtocolStatMaximum;
        amount = (int)Math.Min(amount, Math.Max(0, Math.Min(maximum, ProtocolStatMaximum) - attributes[attribute]));

        if (amount <= 0)
        {
            return 0;
        }

        attributes[attribute] += amount;
        character.LevelUpPoints -= amount;
        return amount;
    }
}

/// <summary>
/// Runs the auto build after a level up of a character which has it enabled.
/// </summary>
[PlugIn]
[Display(Name = "MUIdle auto build", Description = "Spends the level up points of characters with auto build enabled, by class weights.")]
[Guid("8C3D5E7F-9A1B-4C2D-8E3F-5A7B9C1D3E52")]
public sealed class MUIdleAutoBuildPlugIn : ICharacterLevelUpPlugIn
{
    /// <inheritdoc />
    public void CharacterLeveledUp(Player player)
    {
        if (player is HuntOfflinePlayer hunter)
        {
            if (hunter.Settings.AutoBuild)
            {
                // Serialized with the offline AI tick, which mutates the attributes too.
                hunter.PendingBotActions.Enqueue(() => MUIdleAutoBuild.DistributeAsync(hunter));
            }

            return;
        }

        if (player is not RemotePlayer remotePlayer || remotePlayer.SelectedCharacter is not { } character)
        {
            return;
        }

        // The level up happens inside the player's packet handling, which holds its persistence lock;
        // this waits for it and then runs serialized like the next packet would. The lock is reentrant
        // by an AsyncLocal flag, so the execution context must not flow into the task - it would
        // inherit "lock held" and run concurrently with the packet handling.
        using var noFlow = ExecutionContext.SuppressFlow();
        _ = Task.Run(async () =>
        {
            try
            {
                await remotePlayer.RunPersistenceExclusiveAsync(async () =>
                {
                    var state = await remotePlayer.PersistenceContext.GetMUIdleCharacterStateAsync(character.Id).ConfigureAwait(false);
                    if (MUIdleSettings.Parse(state?.SettingsJson).AutoBuild)
                    {
                        await MUIdleAutoBuild.DistributeAsync(remotePlayer).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                remotePlayer.Logger.LogWarning(ex, "MUIdle: auto build of {Character} failed.", character.Name);
            }
        });
    }
}
