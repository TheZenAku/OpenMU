// <copyright file="MUIdlePlayerStatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Drives the HUNT session lifecycle by the player states (see <see cref="MUIdleSessions"/>).
/// </summary>
[PlugIn]
[Display(Name = "MUIdle player state", Description = "Continues the hunt of a disconnecting HUNT character offline, and resumes it when the player returns.")]
[Guid("A3E1C7D4-58B2-4F06-8E9A-2C4D6B8F0A12")]
public sealed class MUIdlePlayerStatePlugIn : IPlayerStateChangedPlugIn
{
    /// <inheritdoc />
    public async ValueTask PlayerStateChangedAsync(Player player, State previousState, State currentState)
    {
        try
        {
            if (player is HuntOfflinePlayer hunter)
            {
                if (currentState == PlayerState.Disconnected)
                {
                    await MUIdleSessions.OnHunterDisconnectingAsync(hunter).ConfigureAwait(false);
                }
                else if (currentState == PlayerState.EnteredWorld && hunter.CurrentMap?.Definition.Name is { } mapName)
                {
                    // Every map change (warps, merchant trips, respawns) ends in EnteredWorld.
                    hunter.Statistics.AddMap(mapName);
                }

                return;
            }

            if (player is not RemotePlayer remotePlayer)
            {
                return;
            }

            if (currentState == PlayerState.EnteredWorld && previousState == PlayerState.CharacterSelection)
            {
                await MUIdleSessions.OnEnteredWorldAsync(remotePlayer).ConfigureAwait(false);
            }
            else if (currentState == PlayerState.Disconnected)
            {
                await MUIdleSessions.OnDisconnectingAsync(remotePlayer).ConfigureAwait(false);
            }
            else if (currentState == PlayerState.Finished)
            {
                MUIdleSessions.OnFinished(remotePlayer);
            }
        }
        catch (Exception ex)
        {
            player.Logger.LogError(ex, "MUIdle: error at the state change of {Player} from {Previous} to {Current}.", player, previousState, currentState);
        }
    }
}
