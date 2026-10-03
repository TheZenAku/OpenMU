// <copyright file="MUIdleOnlineQuests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// The class quest errand (<see cref="MUIdleQuests.ErrandFor(Player)"/>) for a player with a client in
/// HUNT: to the giver's map through the warp whose landing walks to it, the walk to the giver (the
/// client walks to the ground the server names, as for a pinned map), the dialog at the giver on the
/// server (talk, accept or hand in, close), then back out to a hunting ground. Priority (D19): an event
/// and a world boss come first, the errand before the map choice of AUTO.
/// </summary>
internal static class MUIdleOnlineQuests
{
    /// <summary>The tiles from the giver within which its dialog opens (like the merchant trips).</summary>
    private const int TalkRange = 3;

    private static readonly WarpAction Warp = new();

    private static readonly TalkNpcAction Talk = new();

    private static readonly CloseNpcDialogAction Close = new();

    private static readonly ConditionalWeakTable<Player, StrongBox<DateTime>> NextStepUtc = new();

    /// <summary>
    /// Runs the quest errands of the online hunters of the game context.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    public static async ValueTask MaintainAsync(IGameContext gameContext)
    {
        foreach (var player in (await gameContext.GetPlayersAsync().ConfigureAwait(false)).OfType<RemotePlayer>())
        {
            try
            {
                await TryStepAsync(player).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                player.Logger.LogWarning(ex, "MUIdle: quest errand of {Player} failed.", player);
            }
        }
    }

    /// <summary>
    /// Gets the errand an online hunter has to run now, if any (the AUTO map choice waits for it).
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The errand, or <c>null</c>.</returns>
    public static BotErrand? Due(Player player)
        => player.PlayerState.CurrentState == PlayerState.EnteredWorld
           && player.IsAlive
           && player.CurrentMiniGame is null
           && player.Attributes?[Stats.IsMuHelperActive] > 0
           && MUIdleHuntTracker.GetSettings(player) is { QuestsAuto: true, BossesFirst: var bosses }
           && !(bosses && MUIdleWorldBossPlugIn.LiveBossFor(player) is not null)
            ? MUIdleQuests.ErrandFor(player)
            : null;

    private static async ValueTask TryStepAsync(RemotePlayer player)
    {
        if (Due(player) is not { } errand || player.SelectedCharacter is not { } character)
        {
            return;
        }

        var box = NextStepUtc.GetValue(player, _ => new StrongBox<DateTime>(DateTime.MinValue));
        if (DateTime.UtcNow < box.Value)
        {
            return;
        }

        var giverSpawn = player.GameContext.Configuration.Maps.FirstOrDefault(m => m.Number == errand.MapNumber)?.MonsterSpawns
            .FirstOrDefault(s => s.MonsterDefinition?.Number == errand.NpcNumber);
        if (giverSpawn is null || await player.GameContext.GetMapAsync((ushort)errand.MapNumber).ConfigureAwait(false) is not { } giverMap)
        {
            box.Value = DateTime.UtcNow.AddMinutes(10);
            return;
        }

        var spot = new Point((byte)((giverSpawn.X1 + giverSpawn.X2) / 2), (byte)((giverSpawn.Y1 + giverSpawn.Y2) / 2));
        var logger = player.GameContext.LoggerFactory.CreateLogger("MUnique.OpenMU.GameServer.MUIdle.Quests");

        // Another map, or another floor of it: through the warp whose landing walks to the giver.
        if (player.CurrentMap is not { } here || here.Definition.Number != errand.MapNumber || !MUIdleHuntMaps.IsWalkable(here, player.Position, spot))
        {
            if (MUIdleHuntMaps.ReachingWarp(player, giverMap, spot) is not { } warp)
            {
                logger.LogInformation("MUIdle quest {Character}: no warp of its own to the giver on {Map} now.", player.Name, giverMap.Definition.Name);
                box.Value = DateTime.UtcNow.AddMinutes(5);
                return;
            }

            logger.LogInformation("MUIdle quest {Character}: travelling to {Map} to {Purpose}.", player.Name, giverMap.Definition.Name, errand.Purpose);
            MUIdleHuntTracker.Report(player, HuntActivityKind.Quest, giverMap.Definition.Name.ValueInNeutralLanguage);
            MUIdleHuntTracker.ExpectPinnedArrival(player, errand.MapNumber, spot);
            box.Value = DateTime.UtcNow.AddMinutes(1);
            await player.RunPersistenceExclusiveAsync(async () =>
            {
                await Warp.WarpToAsync(player, warp).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
            return;
        }

        // On the giver's floor but away from it: the client walks there (HUNT pauses in town).
        if (player.Position.EuclideanDistanceTo(spot) > TalkRange)
        {
            MUIdleHuntTracker.Report(player, HuntActivityKind.Quest, here.Definition.Name.ValueInNeutralLanguage);
            box.Value = DateTime.UtcNow.AddSeconds(45);
            var state = await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false);
            await MUIdleSessions.SendStateAsync(player, state, resume: true, ground: spot).ConfigureAwait(false);
            return;
        }

        // At the giver: its dialog like a player opens it, the quest action, close - under the player's
        // persistence lock, like its packet handling.
        var npc = here.GetNpcsInRange(spot, 4).FirstOrDefault(n => n.Definition.Number == errand.NpcNumber);
        if (npc is null)
        {
            box.Value = DateTime.UtcNow.AddMinutes(2);
            return;
        }

        box.Value = DateTime.UtcNow.AddSeconds(30);
        await player.RunPersistenceExclusiveAsync(async () =>
        {
            await Talk.TalkToNpcAsync(player, npc).ConfigureAwait(false);
            if (ReferenceEquals(player.OpenedNpc, npc))
            {
                try
                {
                    await errand.ActAsync(player, npc).ConfigureAwait(false);
                }
                finally
                {
                    await Close.CloseNpcDialogAsync(player).ConfigureAwait(false);
                }
            }

            return true;
        }).ConfigureAwait(false);
        await player.SaveProgressAsync().ConfigureAwait(false);

        // Back out to hunt - where the next quest item drops when one is missing.
        var settings = MUIdleHuntTracker.GetSettings(player);
        var band = settings?.QuestItemsFirst == true ? MUIdleQuests.MissingItemBand(player) : null;
        var after = await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false);
        await MUIdleSessions.SendStateAsync(player, after, resume: true, ground: MUIdleHuntMaps.PickGround(player, here, band)).ConfigureAwait(false);
    }
}
