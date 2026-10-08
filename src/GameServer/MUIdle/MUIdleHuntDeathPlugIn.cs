// <copyright file="MUIdleHuntDeathPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Tells the hunt state of a player with a client that the character died (offline hunters report
/// their own deaths). The respawn ends in EnteredWorld, which sets it back. Also notes when any player
/// is put on a map, for the deaths on arrival (<see cref="MUIdleHuntDeaths"/>).
/// </summary>
[PlugIn]
[Display(Name = "MUIdle hunt state: deaths", Description = "Shows 'dead' as the hunt activity of a player who died.")]
[Guid("7B2E4C91-3A6D-4F58-9E1B-C4D7A2F06E35")]
public sealed class MUIdleHuntDeathPlugIn : IAttackableGotKilledPlugIn, IObjectAddedToMapPlugIn
{
    /// <inheritdoc />
    public ValueTask AttackableGotKilledAsync(IAttackable killed, IAttacker? killer)
    {
        if (killed is RemotePlayer player)
        {
            MUIdleHuntTracker.Report(player, HuntActivityKind.Dead);
            MUIdleHuntDeaths.Record(player);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ObjectAddedToMapAsync(GameMap map, ILocateable addedObject)
    {
        if (addedObject is Player player)
        {
            MUIdleHuntDeaths.Arrived(player, map.Definition.Number);
        }

        return ValueTask.CompletedTask;
    }
}
