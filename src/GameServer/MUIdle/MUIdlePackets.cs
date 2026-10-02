// <copyright file="MUIdlePackets.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.Network;

/// <summary>
/// The MUIdle packets: code <see cref="Code"/> (unused by the season 6 protocol), a sub code, and a
/// UTF-8 JSON payload. Server to client they are C2 frames (not encrypted, like any C2 of the game
/// server); client to server C1 or C2 frames (decrypted by the regular pipeline).
/// </summary>
internal static class MUIdlePackets
{
    /// <summary>The packet code.</summary>
    public const byte Code = 0xEE;

    /// <summary>Server to client: the idle state of the character (HUNT intent and settings).</summary>
    public const byte StateSubCode = 0x01;

    /// <summary>Server to client: the summary of a finished offline hunting session.</summary>
    public const byte OfflineSummarySubCode = 0x02;

    /// <summary>Client to server: save the idle settings (JSON of <see cref="MUIdleSettings"/>).</summary>
    public const byte SaveSettingsSubCode = 0x10;

    /// <summary>Client to server: send the idle state again.</summary>
    public const byte RequestStateSubCode = 0x11;

    /// <summary>Client to server: lock or unlock an inventory item (<c>{"slot":12,"locked":true}</c>).</summary>
    public const byte LockItemSubCode = 0x12;

    /// <summary>Client to server: the player switched between HUNT and MANUAL (<c>{"hunt":true}</c>).</summary>
    public const byte HuntModeSubCode = 0x13;

    /// <summary>The maximum payload size which is accepted from a client.</summary>
    public const int MaximumClientPayload = 4096;

    /// <summary>
    /// Sends a JSON payload to the client of a player.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="subCode">The sub code.</param>
    /// <param name="json">The JSON.</param>
    public static async ValueTask SendJsonAsync(RemotePlayer player, byte subCode, string json)
    {
        if (player.Connection is not { Connected: true } connection)
        {
            return;
        }

        var payload = Encoding.UTF8.GetBytes(json);
        var length = 5 + payload.Length;
        if (length > ushort.MaxValue)
        {
            player.Logger.LogWarning("MUIdle packet {SubCode} of {Length} bytes is too large to send.", subCode, length);
            return;
        }

        int WritePacket()
        {
            var span = connection.Output.GetSpan(length)[..length];
            span[0] = 0xC2;
            span[1] = (byte)(length >> 8);
            span[2] = (byte)(length & 0xFF);
            span[3] = Code;
            span[4] = subCode;
            payload.CopyTo(span[5..]);
            return length;
        }

        await connection.SendAsync(WritePacket).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the sub code and the payload of a (decrypted) MUIdle packet from a client.
    /// </summary>
    /// <param name="packet">The packet, including its header.</param>
    /// <param name="subCode">The sub code.</param>
    /// <param name="payload">The payload.</param>
    /// <returns><c>true</c>, if the packet is well-formed.</returns>
    public static bool TryRead(ReadOnlySpan<byte> packet, out byte subCode, out ReadOnlySpan<byte> payload)
    {
        subCode = 0;
        payload = default;
        if (packet.Length < 4)
        {
            return false;
        }

        var headerSize = packet[0] switch
        {
            0xC1 or 0xC3 => 2,
            0xC2 or 0xC4 => 3,
            _ => -1,
        };

        if (headerSize < 0 || packet.Length < headerSize + 2 || packet[headerSize] != Code)
        {
            return false;
        }

        subCode = packet[headerSize + 1];
        payload = packet[(headerSize + 2)..];
        return payload.Length <= MaximumClientPayload;
    }
}
