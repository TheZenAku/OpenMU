// <copyright file="MUIdlePacketHandlerPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text.Json;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameServer.MessageHandler;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Handles the MUIdle packets of the client (see <see cref="MUIdlePackets"/>). Everything the client
/// sends is validated here; the client never decides about anything but the player's own preferences.
/// </summary>
[PlugIn]
[Display(Name = "MUIdle packet handler", Description = "Handles the idle settings, item locks and HUNT/MANUAL switches of the client.")]
[Guid("5F7A2B9C-1E3D-4A6F-8B0C-9D2E4F6A8B13")]
public sealed class MUIdlePacketHandlerPlugIn : IPacketHandlerPlugIn
{
    private const int MaximumLockedItems = 300;

    /// <inheritdoc />
    public bool IsEncryptionExpected => false;

    /// <inheritdoc />
    public byte Key => MUIdlePackets.Code;

    /// <inheritdoc />
    public async ValueTask HandlePacketAsync(Player player, Memory<byte> packet)
    {
        if (player is not RemotePlayer remotePlayer
            || player.SelectedCharacter is not { } character
            || player.PlayerState.CurrentState.IsDisconnectedOrFinished()
            || !MUIdlePackets.TryRead(packet.Span, out var subCode, out var payloadSpan))
        {
            return;
        }

        var payload = payloadSpan.ToArray();
        try
        {
            switch (subCode)
            {
                case MUIdlePackets.RequestStateSubCode:
                    await MUIdleSessions.SendStateAsync(remotePlayer, await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case MUIdlePackets.SaveSettingsSubCode:
                    await this.SaveSettingsAsync(remotePlayer, character, payload).ConfigureAwait(false);
                    break;
                case MUIdlePackets.LockItemSubCode:
                    await this.LockItemAsync(remotePlayer, character, payload).ConfigureAwait(false);
                    break;
                case MUIdlePackets.HuntModeSubCode:
                    await this.SetHuntModeAsync(remotePlayer, character, payload).ConfigureAwait(false);
                    break;
            }
        }
        catch (JsonException)
        {
            player.Logger.LogDebug("MUIdle: malformed packet {SubCode} from {Player}.", subCode, player);
        }
    }

    private async ValueTask SaveSettingsAsync(RemotePlayer player, Character character, byte[] payload)
    {
        var requested = MUIdleSettings.Parse(Encoding.UTF8.GetString(payload));
        await player.RunPersistenceExclusiveAsync(async () =>
        {
            var state = await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false);
            var current = MUIdleSettings.Parse(state.SettingsJson);

            // The locks are only changed item by item (they reference items of this character).
            requested.LockedItems = current.LockedItems;
            requested.SellMaxItemLevel = Math.Clamp(requested.SellMaxItemLevel, 0, 15);
            requested.PreferredMap = requested.PreferredMap is { } map && player.GameContext.Configuration.Maps.Any(m => m.Number == map) ? map : null;
            state.SettingsJson = requested.ToJson();
        }).ConfigureAwait(false);
        await player.SaveProgressAsync().ConfigureAwait(false);
        await MUIdleSessions.SendStateAsync(player, await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private async ValueTask LockItemAsync(RemotePlayer player, Character character, byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("slot", out var slotElement)
            || !slotElement.TryGetByte(out var slot)
            || player.Inventory?.GetItem(slot) is not { } item)
        {
            return;
        }

        var locked = document.RootElement.TryGetProperty("locked", out var lockedElement) && lockedElement.ValueKind == JsonValueKind.True;
        var itemId = item.GetId();
        await player.RunPersistenceExclusiveAsync(async () =>
        {
            var state = await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false);
            var settings = MUIdleSettings.Parse(state.SettingsJson);

            // Drop locks of items which are gone (sold by hand, moved to the vault, ...).
            var existing = player.Inventory!.Items.Select(i => i.GetId()).ToHashSet();
            settings.LockedItems.RemoveAll(id => !existing.Contains(id));
            if (locked && !settings.LockedItems.Contains(itemId) && settings.LockedItems.Count < MaximumLockedItems)
            {
                settings.LockedItems.Add(itemId);
            }
            else if (!locked)
            {
                settings.LockedItems.Remove(itemId);
            }

            state.SettingsJson = settings.ToJson();
        }).ConfigureAwait(false);
        await player.SaveProgressAsync().ConfigureAwait(false);
        await MUIdleSessions.SendStateAsync(player, await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private async ValueTask SetHuntModeAsync(RemotePlayer player, Character character, byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        var hunt = document.RootElement.TryGetProperty("hunt", out var huntElement) && huntElement.ValueKind == JsonValueKind.True;
        await player.RunPersistenceExclusiveAsync(async () =>
        {
            var state = await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false);
            state.HuntActive = hunt;
        }).ConfigureAwait(false);
        await player.SaveProgressAsync().ConfigureAwait(false);
    }
}
