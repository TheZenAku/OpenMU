// <copyright file="MUIdleAutoBuild.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.PlayerActions.Character;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Auto build: spends the free level up points of a character by its class's stat weights, through
/// the upstream <see cref="IncreaseStatsAction"/> (which validates points, class and maximum values).
/// </summary>
internal static class MUIdleAutoBuild
{
    private static readonly IncreaseStatsAction IncreaseAction = new();

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

        // A client hears of an increase by the stat-increase response, which says "one more point".
        // For several points at once upstream re-sends the whole character and warps the player onto
        // its own position (a workaround for old clients): on every level up of a hunting character
        // that was 3-4 warps, walk resyncs, and a crash when the next share hit a player mid-warp.
        // Offline hunters have no client to tell, so they keep the bulk step.
        var bulk = player is OfflinePlayer;
        foreach (var (stat, amount) in shares)
        {
            var left = amount;
            while (left > 0 && character.LevelUpPoints > 0)
            {
                var step = bulk ? (ushort)Math.Min(left, Math.Min(character.LevelUpPoints, ushort.MaxValue)) : (ushort)1;
                var before = character.LevelUpPoints;
                await IncreaseAction.IncreaseStatsAsync(player, stat, step).ConfigureAwait(false);
                if (character.LevelUpPoints >= before)
                {
                    break; // maximum reached or not increasable for this class
                }

                left -= before - character.LevelUpPoints;
            }
        }
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
