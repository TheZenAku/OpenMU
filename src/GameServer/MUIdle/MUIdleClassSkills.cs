// <copyright file="MUIdleClassSkills.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;

/// <summary>
/// Every character knows the skills of its class from its first entry on (D22): the skills its class would
/// learn from scrolls, orbs and parchments - the items with a skill which are not worn (gear lends its
/// skill only while worn). They are added the way an item teaches them (<see cref="ISkillList.AddLearnedSkillAsync(Skill)"/>):
/// learned, saved and shown in the client. A skill's own requirements (energy, level) still apply when
/// it is cast; master skills stay with the master tree.
/// </summary>
internal static class MUIdleClassSkills
{
    /// <summary>
    /// Gets the skills a class learns from items, the Summoning Orb's one per item level included.
    /// </summary>
    /// <param name="characterClass">The character class.</param>
    /// <param name="configuration">The game configuration.</param>
    /// <returns>The skills, ordered by number.</returns>
    internal static IReadOnlyList<Skill> LearnableBy(CharacterClass characterClass, GameConfiguration configuration)
    {
        var summonOrb = ItemConstants.SummonOrb;
        var skills = new List<Skill>();
        foreach (var definition in configuration.Items)
        {
            if (definition.Skill is not { } skill
                || definition.ItemSlot is not null
                || skill.MasterDefinition is not null
                || !definition.QualifiedCharacters.Contains(characterClass))
            {
                continue;
            }

            if (definition.Group == summonOrb.Group && definition.Number == summonOrb.Number)
            {
                // The orb's level selects the summon (SummoningOrbConsumeHandlerPlugIn).
                for (var level = 0; level <= definition.MaximumItemLevel; level++)
                {
                    if (configuration.Skills.FirstOrDefault(s => s.Number == skill.Number + level) is { } summon)
                    {
                        skills.Add(summon);
                    }
                }
            }
            else
            {
                skills.Add(skill);
            }
        }

        return skills.DistinctBy(s => s.Number).OrderBy(s => s.Number).ToList();
    }

    /// <summary>
    /// Teaches the player the skills of its class it does not know yet, if the feature is on.
    /// </summary>
    /// <param name="player">The player (with a client or an offline hunter).</param>
    /// <param name="configuration">The MUIdle configuration.</param>
    public static async ValueTask GrantAsync(Player player, MUIdleConfiguration configuration)
    {
        if (!configuration.GrantClassSkills
            || player.SelectedCharacter?.CharacterClass is not { } characterClass
            || player.SkillList is not { } skillList)
        {
            return;
        }

        var granted = new List<string>();
        foreach (var skill in LearnableBy(characterClass, player.GameContext.Configuration))
        {
            if (skillList.ContainsSkill(skill.Number.ToUnsigned()))
            {
                continue;
            }

            await skillList.AddLearnedSkillAsync(skill).ConfigureAwait(false);
            granted.Add(skill.Name.ValueInNeutralLanguage);
        }

        if (granted.Count == 0)
        {
            return;
        }

        await player.SaveProgressAsync().ConfigureAwait(false);
        player.Logger.LogInformation("MUIdle: {Character} knows the skills of its class now: {Skills}.", player.Name, string.Join(", ", granted));
    }
}
