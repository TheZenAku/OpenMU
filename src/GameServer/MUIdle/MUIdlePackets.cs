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

    /// <summary>Server to client: what the hunt does now (<see cref="MUIdleHuntTracker"/>).</summary>
    public const byte ActivitySubCode = 0x03;

    /// <summary>Server to client: the odds of a mix of the items in the crafting dialog (<see cref="MUIdleMixPreview"/>).</summary>
    public const byte MixPreviewSubCode = 0x04;

    /// <summary>Server to client: the outcome of an inventory action (<see cref="MUIdleInventory"/>).</summary>
    public const byte InventoryResultSubCode = 0x05;

    /// <summary>Server to client: the junk of the inventory, slots and price (<see cref="MUIdleInventory"/>).</summary>
    public const byte JunkPreviewSubCode = 0x06;

    /// <summary>Server to client: an update notice - a new client build, or a server restart in some seconds (<see cref="MUIdleNotices"/>).</summary>
    public const byte NoticeSubCode = 0x07;

    /// <summary>Server to client: the hunting spots of the current map for the map window (HUNT v2, <see cref="MUIdleMapSpots"/>).</summary>
    public const byte MapSpotsSubCode = 0x08;

    /// <summary>Client to server: save the idle settings (JSON of <see cref="MUIdleSettings"/>).</summary>
    public const byte SaveSettingsSubCode = 0x10;

    /// <summary>Client to server: send the idle state again.</summary>
    public const byte RequestStateSubCode = 0x11;

    /// <summary>Client to server: lock or unlock an inventory item (<c>{"slot":12,"locked":true}</c>).</summary>
    public const byte LockItemSubCode = 0x12;

    /// <summary>Client to server: the player switched between HUNT and MANUAL (<c>{"hunt":true}</c>).</summary>
    public const byte HuntModeSubCode = 0x13;

    /// <summary>Client to server: reset the character (the reset feature's own action and checks).</summary>
    public const byte ResetSubCode = 0x14;

    /// <summary>Client to server: preview a mix of the items in the crafting dialog (<c>{"mixType":11}</c>).</summary>
    public const byte RequestMixPreviewSubCode = 0x15;

    /// <summary>Client to server: split pieces off a stack in the inventory (<c>{"slot":14,"amount":5}</c>).</summary>
    public const byte SplitStackSubCode = 0x16;

    /// <summary>Client to server: sell inventory items, anywhere (<c>{"slots":[12,40]}</c>).</summary>
    public const byte SellItemsSubCode = 0x17;

    /// <summary>Client to server: list the junk of the inventory (answered with <see cref="JunkPreviewSubCode"/>).</summary>
    public const byte RequestJunkPreviewSubCode = 0x18;

    /// <summary>Client to server: repair the equipped items, anywhere.</summary>
    public const byte RepairAllSubCode = 0x19;

    /// <summary>Client to server: buy the next inventory page (<c>{"page":3}</c>).</summary>
    public const byte BuyInventoryPageSubCode = 0x1A;

    /// <summary>Client to server: put an inventory item onto a storage page (<c>{"slot":30,"page":4}</c>).</summary>
    public const byte StoreItemSubCode = 0x1B;

    /// <summary>Client to server: take an item of a storage page back into the inventory (<c>{"slot":5}</c>).</summary>
    public const byte RetrieveItemSubCode = 0x1C;

    /// <summary>Client to server: the player walked, attacked or cast on its own; the online hunt holds still a moment.</summary>
    public const byte ManualControlSubCode = 0x1D;

    /// <summary>Client to server: send the hunting spots of the current map (<c>{}</c>).</summary>
    public const byte RequestMapSpotsSubCode = 0x1E;

    /// <summary>Client to server: hunt at this spot of the current map (<c>{"x":120,"y":80}</c>), or choose again (<c>{"clear":true}</c>).</summary>
    public const byte PinSpotSubCode = 0x1F;

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
