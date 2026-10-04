// <copyright file="MUIdleQuests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.DataModel.Entities;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.GameLogic.PlayerActions.Quests;

/// <summary>
/// The class quest line (quest group 0) of a character as the server sees it: the active quest or the
/// next one, its giver and where it stands, and how far the character is - read-only (QUEST_AUTOMATION.md).
/// </summary>
internal static class MUIdleQuests
{
    /// <summary>The quest group of the class quest line.</summary>
    public const short ClassLine = 0;

    private const string LogCategory = "MUnique.OpenMU.GameServer.MUIdle.Quests";

    private static readonly QuestStartAction StartAction = new();

    private static readonly QuestCompletionAction CompleteAction = new();

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
    /// Gets where the hunt goes for a quest item which only one monster drops (Evidence of Strength:
    /// Hell Maine, Death Beam Knight, Dark Phoenix): a permanent spawn of the monster of the first such
    /// missing item, on a map the character is on or can warp to, and only a monster it could fight (the
    /// opt-in boss rule of <see cref="CombatHandler.ExplainUnsafe"/>); <c>null</c> when there is none.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The monster, its map and the middle of its spawn area.</returns>
    public static (MonsterDefinition Monster, short Map, Point Spot)? MissingItemMonster(Player player)
    {
        if (player.SelectedCharacter?.QuestStates.FirstOrDefault(s => s.Group == ClassLine)?.ActiveQuest is not { } active)
        {
            return null;
        }

        var bag = player.Inventory?.Items.ToList() ?? [];
        foreach (var required in active.RequiredItems)
        {
            if (required.DropItemGroup?.Monster is not { } monster
                || bag.Count(i => Equals(i.Definition, required.Item)) >= required.MinimumNumber
                || CombatHandler.ExplainUnsafe(player, monster, requireQuickKill: false) is not null)
            {
                continue;
            }

            var spawn = player.GameContext.Configuration.Maps
                .SelectMany(map => map.MonsterSpawns.Select(area => (Map: map, Area: area)))
                .Where(x => x.Area.MonsterDefinition == monster && HuntingMapRating.IsPermanentMonsterSpawn(x.Area))
                .OrderByDescending(x => x.Map == player.CurrentMap?.Definition)
                .FirstOrDefault(x => x.Map == player.CurrentMap?.Definition || MUIdleHuntMaps.Evaluate(player, x.Map.Number) is { Refusal: null });
            if (spawn.Area is { } area)
            {
                return (monster, spawn.Map.Number, new Point((byte)((area.X1 + area.X2) / 2), (byte)((area.Y1 + area.Y2) / 2)));
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
        if (player.SelectedCharacter is null)
        {
            return null;
        }

        if (Resolve(player) is not { } line)
        {
            return new { finished = true };
        }

        var (quest, state, active, giver, stand) = line;

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

        var done = active && items.All(i => i.have >= i.need) && kills.All(k => k.have >= k.need);
        var auto = MUIdleHuntTracker.GetSettings(player)?.QuestsAuto == true;
        var automation = !auto ? "off"
            : quest.RequiresClientAction ? "manual"
            : done ? "handIn"
            : active ? "collecting"
            : player.Level < quest.MinimumCharacterLevel ? "level"
            : player.Money < AcceptCost(player, quest, stand.Map) ? "zen"
            : "accept";
        return new
        {
            finished = false,
            name = quest.Name.ValueInNeutralLanguage,
            active,
            minLevel = quest.MinimumCharacterLevel,
            canAccept = !active && player.Level >= quest.MinimumCharacterLevel,
            done,
            startZen = quest.RequiredStartMoney,
            automation,
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

    /// <summary>
    /// The errand HUNT runs for the class quest line (with <see cref="MUIdleSettings.QuestsAuto"/>): hand
    /// the active quest in at its giver once everything is collected, or accept the next one there when
    /// the character has the level and the start fee. Both through the upstream quest actions, at the
    /// giver with its dialog open - nothing a player could not do. <c>null</c> when nothing is due.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The errand, or <c>null</c>.</returns>
    public static BotErrand? ErrandFor(Player player)
    {
        if (Resolve(player) is not { Giver: { } giver, Stand.Map: { } map } line || line.Quest.RequiresClientAction)
        {
            return null;
        }

        var quest = line.Quest;
        var name = quest.Name.ValueInNeutralLanguage;
        if (line.Active)
        {
            if (!IsComplete(player, quest, line.State))
            {
                return null;
            }

            return new BotErrand((short)map.Number, giver.Number, $"hand in {name}", async (hunter, _) =>
            {
                await CompleteAction.CompleteQuestAsync(hunter, quest.Group, quest.Number).ConfigureAwait(false);
                var finished = hunter.SelectedCharacter?.QuestStates.FirstOrDefault(s => s.Group == ClassLine)?.LastFinishedQuest;
                Log(hunter, Equals(finished, quest) ? "handed in" : "could not hand in", name, giver);
            });
        }

        if (player.Level < quest.MinimumCharacterLevel || player.Money < AcceptCost(player, quest, map))
        {
            return null;
        }

        return new BotErrand((short)map.Number, giver.Number, $"accept {name}", async (hunter, _) =>
        {
            await StartAction.StartQuestAsync(hunter, quest.Group, quest.Number).ConfigureAwait(false);
            var running = hunter.SelectedCharacter?.QuestStates.FirstOrDefault(s => s.Group == ClassLine)?.ActiveQuest;
            Log(hunter, Equals(running, quest) ? $"accepted (paid {quest.RequiredStartMoney:N0} zen)" : "could not accept", name, giver);
        });
    }

    /// <summary>
    /// The zen accepting the quest needs from here: the start fee, the fare to the giver's map (a character
    /// with exactly the fee would arrive short of it after the warp), and the HUNT reserve (D20).
    /// </summary>
    private static long AcceptCost(Player player, QuestDefinition quest, GameMapDefinition? giverMap)
    {
        var fare = giverMap is null || player.CurrentMap?.Definition == giverMap
            ? 0
            : MUIdleHuntMaps.Evaluate(player, giverMap.Number)?.Fare ?? 0;
        return (long)quest.RequiredStartMoney + fare + MUIdleEconomy.Reserve(player);
    }

    private static void Log(Player player, string what, string quest, MonsterDefinition giver)
        => player.GameContext.LoggerFactory.CreateLogger(LogCategory).LogInformation(
            "MUIdle quest {Character}: {What} {Quest} at {Giver}.", player.Name, what, quest, giver.Designation.ValueInNeutralLanguage);

    /// <summary>Whether everything the quest needs is there, counted like QuestCompletionAction counts it.</summary>
    private static bool IsComplete(Player player, QuestDefinition quest, CharacterQuestState? state)
    {
        var bag = player.Inventory?.Items.ToList() ?? [];
        return quest.RequiredItems.All(r =>
                   bag.Count(i => Equals(i.Definition, r.Item) && (r.DropItemGroup?.ItemLevel is not { } level || level == i.Level)) >= r.MinimumNumber)
               && quest.RequiredMonsterKills.All(k =>
                   (state?.RequirementStates.FirstOrDefault(r => Equals(r.Requirement, k))?.KillCount ?? 0) >= k.MinimumNumber);
    }

    /// <summary>Where the class quest line of the character stands: the quest to show, its state and giver.</summary>
    private static LineState? Resolve(Player player)
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
            return null;
        }

        var active = state?.ActiveQuest is { } running && Equals(running, quest);
        var giver = givers.FirstOrDefault(m => m.Quests.Contains(quest));
        // A giver may stand in several places (Marlon in four towns, Priest Devin in Devias and Crywolf):
        // like a player picks, the one on the character's map, else one it can warp to (Crywolf has no
        // warp entry), else any.
        var stands = giver is null
            ? []
            : configuration.Maps
                .SelectMany(map => map.MonsterSpawns.Select(spawn => (Map: (GameMapDefinition?)map, Spawn: (MonsterSpawnArea?)spawn)))
                .Where(x => x.Spawn!.MonsterDefinition == giver)
                .ToList();
        var stand = stands.FirstOrDefault(x => x.Map == player.CurrentMap?.Definition) is { Map: not null } here ? here
            : stands.FirstOrDefault(x => MUIdleHuntMaps.Evaluate(player, x.Map!.Number) is { Refusal: null }) is { Map: not null } reachable ? reachable
            : stands.FirstOrDefault();
        return new LineState(quest, state, active, giver, stand);
    }

    private sealed record LineState(QuestDefinition Quest, CharacterQuestState? State, bool Active, MonsterDefinition? Giver, (GameMapDefinition? Map, MonsterSpawnArea? Spawn) Stand);
}
