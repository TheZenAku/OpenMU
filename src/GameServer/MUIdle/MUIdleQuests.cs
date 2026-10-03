// <copyright file="MUIdleQuests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;

/// <summary>
/// The class quest line (quest group 0) of a character as the server sees it: the active quest or the
/// next one, its giver and where it stands, and how far the character is - read-only (QUEST_AUTOMATION.md).
/// </summary>
internal static class MUIdleQuests
{
    /// <summary>The quest group of the class quest line.</summary>
    public const short ClassLine = 0;

    /// <summary>
    /// Picks the quest of the line to show: the active one, else the first quest for the character class
    /// after the last finished one; <c>null</c> when the line is done for the class.
    /// </summary>
    /// <param name="line">The quests of the line.</param>
    /// <param name="characterClass">The character class.</param>
    /// <param name="lastFinished">The last finished quest of the line.</param>
    /// <param name="active">The active quest of the line.</param>
    /// <returns>The quest, or <c>null</c>.</returns>
    internal static QuestDefinition? Current(IEnumerable<QuestDefinition> line, CharacterClass? characterClass, QuestDefinition? lastFinished, QuestDefinition? active)
    {
        if (active is not null)
        {
            return active;
        }

        var after = lastFinished?.Number ?? -1;
        return line
            .Where(q => q.Group == ClassLine && q.Number > after && (q.QualifiedCharacter is null || Equals(q.QualifiedCharacter, characterClass)))
            .OrderBy(q => q.Number)
            .FirstOrDefault();
    }

    /// <summary>
    /// Gets the monster level band of the first quest item the active quest still misses (it drops only
    /// from monsters of that band while the quest runs); <c>null</c> when nothing like that is missing.
    /// </summary>
    /// <param name="active">The active quest.</param>
    /// <param name="bag">The items of the inventory.</param>
    /// <returns>The band, or <c>null</c>.</returns>
    internal static (int Min, int Max)? MissingItemBand(QuestDefinition? active, IEnumerable<Item> bag)
    {
        if (active is null)
        {
            return null;
        }

        var items = bag.ToList();
        foreach (var required in active.RequiredItems)
        {
            if (required.DropItemGroup is not { MinimumMonsterLevel: { } min } group)
            {
                continue;
            }

            var level = group.ItemLevel;
            var have = items.Count(i => Equals(i.Definition, required.Item) && (level is null || level == i.Level));
            if (have < required.MinimumNumber)
            {
                return (min, group.MaximumMonsterLevel ?? byte.MaxValue);
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the monster level band the hunt of the player prefers for its class quest; <c>null</c> for none.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The band, or <c>null</c>.</returns>
    public static (int Min, int Max)? MissingItemBand(Player player)
        => player.SelectedCharacter?.QuestStates.FirstOrDefault(s => s.Group == ClassLine)?.ActiveQuest is { } active
            ? MissingItemBand(active, player.Inventory?.Items ?? [])
            : null;

    /// <summary>
    /// The state of the class quest line for the client.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The snapshot, or <c>null</c> without a character.</returns>
    public static object? Snapshot(Player player)
    {
        if (player.SelectedCharacter is not { } character)
        {
            return null;
        }

        var configuration = player.GameContext.Configuration;
        var givers = configuration.Monsters.Where(m => m.Quests.Any(q => q.Group == ClassLine)).ToList();
        var state = character.QuestStates.FirstOrDefault(s => s.Group == ClassLine);
        if (Current(givers.SelectMany(m => m.Quests).Distinct(), character.CharacterClass, state?.LastFinishedQuest, state?.ActiveQuest) is not { } quest)
        {
            return new { finished = true };
        }

        var active = state?.ActiveQuest is { } running && Equals(running, quest);
        var giver = givers.FirstOrDefault(m => m.Quests.Contains(quest));
        var stand = giver is null
            ? default
            : configuration.Maps
                .SelectMany(map => map.MonsterSpawns.Select(spawn => (Map: map, Spawn: spawn)))
                .FirstOrDefault(x => x.Spawn.MonsterDefinition == giver);

        // Counted like QuestCompletionAction counts them.
        var items = quest.RequiredItems.Select(r =>
        {
            var level = r.DropItemGroup?.ItemLevel;
            return new
            {
                name = r.Item?.GetNameForLevel(level ?? 0) ?? "?",
                have = player.Inventory?.Items.Count(i => Equals(i.Definition, r.Item) && (level is null || level == i.Level)) ?? 0,
                need = r.MinimumNumber,
                minMonsterLevel = (int?)r.DropItemGroup?.MinimumMonsterLevel,
                maxMonsterLevel = (int?)r.DropItemGroup?.MaximumMonsterLevel,
                monster = r.DropItemGroup?.Monster?.Designation.ValueInNeutralLanguage,
            };
        }).ToList();
        var kills = quest.RequiredMonsterKills.Select(k => new
        {
            monster = k.Monster?.Designation.ValueInNeutralLanguage ?? "?",
            have = active ? state!.RequirementStates.FirstOrDefault(r => Equals(r.Requirement, k))?.KillCount ?? 0 : 0,
            need = k.MinimumNumber,
        }).ToList();

        return new
        {
            finished = false,
            name = quest.Name.ValueInNeutralLanguage,
            active,
            minLevel = quest.MinimumCharacterLevel,
            canAccept = !active && player.Level >= quest.MinimumCharacterLevel,
            done = active && items.All(i => i.have >= i.need) && kills.All(k => k.have >= k.need),
            giver = giver is null
                ? null
                : new
                {
                    name = giver.Designation.ValueInNeutralLanguage,
                    map = stand.Map?.Name.ValueInNeutralLanguage,
                    x = stand.Spawn is { } s ? (s.X1 + s.X2) / 2 : 0,
                    y = stand.Spawn is { } t ? (t.Y1 + t.Y2) / 2 : 0,
                },
            items,
            kills,
        };
    }
}
