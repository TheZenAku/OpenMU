// <copyright file="MUIdleHuntMaps.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Why a map cannot be the hunting map of a character right now.
/// </summary>
internal enum HuntMapRefusal
{
    /// <summary>The character's level is below every warp to it.</summary>
    Level,

    /// <summary>The character cannot pay the fare.</summary>
    Zen,

    /// <summary>No warp leads there.</summary>
    NoWarp,

    /// <summary>The map has an entry requirement the character does not meet (e.g. wings, a ticket).</summary>
    Requirement,
}

/// <summary>
/// A map judged as hunting target for a character.
/// </summary>
/// <param name="Map">The map.</param>
/// <param name="MinLevel">The level the cheapest legal (or else the easiest) warp asks for.</param>
/// <param name="Fare">The fare of that warp.</param>
/// <param name="Refusal">Why the character cannot go there now, or <c>null</c>.</param>
/// <param name="Warp">The warp to take, when it can.</param>
internal sealed record HuntMapOption(GameMapDefinition Map, int MinLevel, int Fare, HuntMapRefusal? Refusal, WarpInfo? Warp)
{
    /// <summary>Gets a value indicating whether the character can hunt there now.</summary>
    public bool Available => this.Refusal is null;
}

/// <summary>
/// The hunting maps of a character: which the warp list reaches, which it may enter now and why not,
/// and where on a map to hunt. The same rules the offline navigator follows (warp level with the class
/// reduction, fare, map entry requirements), so the online and the offline hunt agree.
/// </summary>
internal static class MUIdleHuntMaps
{
    /// <summary>
    /// Gets the maps the warp list reaches, judged for the player, by level requirement.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The options.</returns>
    public static IReadOnlyList<HuntMapOption> Options(Player player)
    {
        return player.GameContext.Configuration.WarpList
            .Where(w => w.Gate?.Map is not null)
            .Select(w => (int)w.Gate!.Map!.Number)
            .Distinct()
            .Select(number => Evaluate(player, number))
            .OfType<HuntMapOption>()
            .OrderBy(o => o.MinLevel)
            .ThenBy(o => o.Map.Number)
            .ToList();
    }

    /// <summary>
    /// Judges one map for the player.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="mapNumber">The map number.</param>
    /// <returns>The option, or <c>null</c> if no such map exists.</returns>
    public static HuntMapOption? Evaluate(Player player, int mapNumber)
    {
        var configuration = player.GameContext.Configuration;
        if (configuration.Maps.FirstOrDefault(m => m.Number == mapNumber) is not { } map)
        {
            return null;
        }

        var warps = configuration.WarpList.Where(w => w.Gate?.Map?.Number == mapNumber).ToList();
        if (warps.Count == 0)
        {
            return new HuntMapOption(map, 0, 0, HuntMapRefusal.NoWarp, null);
        }

        var character = player.SelectedCharacter;
        int Requirement(WarpInfo warp) => character?.GetEffectiveMoveLevelRequirement(warp.LevelRequirement) ?? warp.LevelRequirement;
        var level = (int)(player.Attributes?[Stats.Level] ?? 1);

        var legal = warps.Where(w => Requirement(w) <= level).OrderBy(w => w.Costs).FirstOrDefault();
        if (legal is null)
        {
            var easiest = warps.OrderBy(Requirement).ThenBy(w => w.Costs).First();
            return new HuntMapOption(map, Requirement(easiest), easiest.Costs, HuntMapRefusal.Level, null);
        }

        if (map.TryGetRequirementError(player, out _))
        {
            return new HuntMapOption(map, Requirement(legal), legal.Costs, HuntMapRefusal.Requirement, null);
        }

        if (player.Money < legal.Costs)
        {
            return new HuntMapOption(map, Requirement(legal), legal.Costs, HuntMapRefusal.Zen, null);
        }

        return new HuntMapOption(map, Requirement(legal), legal.Costs, null, legal);
    }

    /// <summary>
    /// Picks where on a map to hunt: the middle of a spawn area of the strongest monsters the player can
    /// safely fight (the navigator's own safety rule), else of the weakest ones.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="map">The map.</param>
    /// <returns>The point, or <c>null</c> when the map has no permanent monsters.</returns>
    public static Point? PickGround(Player player, GameMapDefinition map)
    {
        var areas = map.MonsterSpawns
            .Where(a => a is { Quantity: > 0, SpawnTrigger: SpawnTrigger.Automatic, MonsterDefinition.ObjectKind: NpcObjectKind.Monster })
            .Select(a => (Area: a, Level: MonsterLevel(a.MonsterDefinition!)))
            .Where(x => x.Level > 0)
            .ToList();
        if (areas.Count == 0)
        {
            return null;
        }

        var safe = areas.Where(x => CombatHandler.IsSafeTarget(player, x.Area.MonsterDefinition!)).ToList();
        var pick = safe.Count > 0 ? safe.MaxBy(x => x.Level) : areas.MinBy(x => x.Level);
        var area = pick.Area;
        return new Point((byte)((area.X1 + area.X2) / 2), (byte)((area.Y1 + area.Y2) / 2));
    }

    private static int MonsterLevel(MonsterDefinition definition)
        => (int)(definition.Attributes.FirstOrDefault(a => a.AttributeDefinition == Stats.Level)?.Value ?? 0f);
}
