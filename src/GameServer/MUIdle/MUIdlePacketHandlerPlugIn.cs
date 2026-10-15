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
using MUnique.OpenMU.GameLogic.PlayerActions;
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

    private static readonly WarpAction Warp = new();

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
                case MUIdlePackets.ResetSubCode:
                    // Upstream's reset: level, zen, items, the (season) limit, logging out after it.
                    await new GameLogic.Resets.ResetCharacterAction(remotePlayer).ResetCharacterAsync().ConfigureAwait(false);
                    if (remotePlayer.PlayerState.CurrentState == PlayerState.EnteredWorld)
                    {
                        await MUIdleSessions.SendStateAsync(remotePlayer, await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false)).ConfigureAwait(false);
                    }

                    break;
                case MUIdlePackets.RequestMixPreviewSubCode:
                    using (var request = JsonDocument.Parse(payload))
                    {
                        if (request.RootElement.TryGetProperty("mixType", out var mixType) && mixType.TryGetByte(out var number))
                        {
                            await MUIdleMixPreview.SendAsync(remotePlayer, number).ConfigureAwait(false);
                        }
                    }

                    break;
                case MUIdlePackets.SplitStackSubCode:
                    using (var request = JsonDocument.Parse(payload))
                    {
                        if (request.RootElement.TryGetProperty("slot", out var slotElement) && slotElement.TryGetByte(out var slot)
                            && request.RootElement.TryGetProperty("amount", out var amountElement) && amountElement.TryGetInt32(out var amount))
                        {
                            await MUIdleStacks.SplitAsync(remotePlayer, slot, amount).ConfigureAwait(false);
                        }
                    }

                    break;
                case MUIdlePackets.SellItemsSubCode:
                    using (var request = JsonDocument.Parse(payload))
                    {
                        if (request.RootElement.TryGetProperty("slots", out var slotsElement) && slotsElement.ValueKind == JsonValueKind.Array)
                        {
                            var slots = slotsElement.EnumerateArray()
                                .Select(e => e.TryGetByte(out var s) ? s : (byte?)null)
                                .OfType<byte>()
                                .ToList();
                            var junkOnly = request.RootElement.TryGetProperty("junk", out var junk) && junk.ValueKind == JsonValueKind.True;
                            var storageSlots = request.RootElement.TryGetProperty("storage", out var storageElement) && storageElement.ValueKind == JsonValueKind.Array
                                ? storageElement.EnumerateArray().Select(e => e.TryGetByte(out var s) ? s : (byte?)null).OfType<byte>().ToList()
                                : null;
                            await MUIdleInventory.SellAsync(remotePlayer, slots, junkOnly, storageSlots).ConfigureAwait(false);
                        }
                    }

                    break;
                case MUIdlePackets.RequestJunkPreviewSubCode:
                    await MUIdleInventory.SendJunkPreviewAsync(remotePlayer).ConfigureAwait(false);
                    break;
                case MUIdlePackets.RepairAllSubCode:
                    await MUIdleInventory.RepairAllAsync(remotePlayer).ConfigureAwait(false);
                    break;
                case MUIdlePackets.StoreItemSubCode:
                    using (var request = JsonDocument.Parse(payload))
                    {
                        if (request.RootElement.TryGetProperty("slot", out var slotElement) && slotElement.TryGetByte(out var slot)
                            && request.RootElement.TryGetProperty("page", out var pageElement) && pageElement.TryGetInt32(out var page))
                        {
                            await MUIdleInventory.StoreAsync(remotePlayer, slot, page).ConfigureAwait(false);
                        }
                    }

                    break;
                case MUIdlePackets.RetrieveItemSubCode:
                    using (var request = JsonDocument.Parse(payload))
                    {
                        if (request.RootElement.TryGetProperty("slot", out var slotElement) && slotElement.TryGetByte(out var slot))
                        {
                            await MUIdleInventory.RetrieveAsync(remotePlayer, slot).ConfigureAwait(false);
                        }
                    }

                    break;
                case MUIdlePackets.ManualControlSubCode:
                    OnlineHunt.Pause(remotePlayer);
                    break;
                case MUIdlePackets.RequestMapSpotsSubCode:
                    await MUIdlePackets.SendJsonAsync(remotePlayer, MUIdlePackets.MapSpotsSubCode, MUIdleMapSpots.Build(remotePlayer)).ConfigureAwait(false);
                    break;
                case MUIdlePackets.PinSpotSubCode:
                    using (var request = JsonDocument.Parse(payload))
                    {
                        var root = request.RootElement;
                        if (root.TryGetProperty("clear", out var clear) && clear.ValueKind == JsonValueKind.True)
                        {
                            OnlineHunt.PinSpot(remotePlayer, null);
                        }
                        else if (root.TryGetProperty("x", out var xElement) && xElement.TryGetInt32(out var x)
                                 && root.TryGetProperty("y", out var yElement) && yElement.TryGetInt32(out var y)
                                 && remotePlayer.CurrentMap is { } map
                                 && MUIdleMapSpots.WalkableNear(map, x, y) is { } spot)
                        {
                            OnlineHunt.PinSpot(remotePlayer, spot);
                        }
                    }

                    await MUIdlePackets.SendJsonAsync(remotePlayer, MUIdlePackets.MapSpotsSubCode, MUIdleMapSpots.Build(remotePlayer)).ConfigureAwait(false);
                    break;
                case MUIdlePackets.BuyInventoryPageSubCode:
                    using (var request = JsonDocument.Parse(payload))
                    {
                        if (request.RootElement.TryGetProperty("page", out var pageElement) && pageElement.TryGetInt32(out var page))
                        {
                            await MUIdleInventory.BuyPageAsync(remotePlayer, page).ConfigureAwait(false);
                        }
                    }

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

            // The locks are only changed one by one (they reference items and slots of this character).
            requested.LockedItems = current.LockedItems;
            requested.LockedEquipSlots = current.LockedEquipSlots;
            requested.SellMaxItemLevel = Math.Clamp(requested.SellMaxItemLevel, 0, 15);
            requested.PreferredMap = requested.PreferredMap is { } map && player.GameContext.Configuration.Maps.Any(m => m.Number == map) ? map : null;
            requested.EventOptOut = requested.EventOptOut.Where(key => MUIdleEvents.Joinable.Any(t => MUIdleEvents.Key(t) == key)).Distinct().ToList();
            requested.BuildPreset = MUIdleAutoBuild.Presets.Contains(requested.BuildPreset) ? requested.BuildPreset : null;
            state.SettingsJson = requested.ToJson();
        }).ConfigureAwait(false);
        if (requested.AutoBuild)
        {
            await MUIdleAutoBuild.DistributeAsync(player, requested.BuildPreset ?? string.Empty).ConfigureAwait(false);
        }

        await player.SaveProgressAsync().ConfigureAwait(false);
        if (OnlineHunt.Of(player) is not null)
        {
            await OnlineHunt.StartAsync(player, requested).ConfigureAwait(false);
        }

        await MUIdleSessions.SendStateAsync(player, await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <summary>
    /// Locks or unlocks: an equipment slot (0..11) itself - HUNT never changes it, also while empty - or the
    /// item in a slot of the bag, which keeps its lock wherever the player moves it.
    /// </summary>
    private async ValueTask LockItemAsync(RemotePlayer player, Character character, byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("slot", out var slotElement)
            || !slotElement.TryGetByte(out var slot))
        {
            return;
        }

        var locked = document.RootElement.TryGetProperty("locked", out var lockedElement) && lockedElement.ValueKind == JsonValueKind.True;
        if (slot <= InventoryConstants.LastEquippableItemSlotIndex)
        {
            await this.LockEquipSlotAsync(player, character, slot, locked).ConfigureAwait(false);
            return;
        }

        if (player.Inventory?.GetItem(slot) is not { } item)
        {
            return;
        }

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

    private async ValueTask LockEquipSlotAsync(RemotePlayer player, Character character, byte slot, bool locked)
    {
        await player.RunPersistenceExclusiveAsync(async () =>
        {
            var state = await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false);
            var settings = MUIdleSettings.Parse(state.SettingsJson);
            settings.LockedEquipSlots.RemoveAll(s => s == slot || s < 0 || s > InventoryConstants.LastEquippableItemSlotIndex);
            if (locked)
            {
                settings.LockedEquipSlots.Add(slot);
            }
            else if (player.Inventory?.GetItem(slot) is { } worn)
            {
                // The window shows a slot as locked while its item is: one click unlocks both.
                settings.LockedItems.Remove(worn.GetId());
            }

            settings.LockedEquipSlots.Sort();
            state.SettingsJson = settings.ToJson();
        }).ConfigureAwait(false);
        await player.SaveProgressAsync().ConfigureAwait(false);
        await MUIdleSessions.SendStateAsync(player, await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private async ValueTask SetHuntModeAsync(RemotePlayer player, Character character, byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        var hunt = document.RootElement.TryGetProperty("hunt", out var huntElement) && huntElement.ValueKind == JsonValueKind.True;
        MUIdleSettings? settings = null;
        await player.RunPersistenceExclusiveAsync(async () =>
        {
            var state = await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false);
            state.HuntActive = hunt;
            settings = MUIdleSettings.Parse(state.SettingsJson);
        }).ConfigureAwait(false);
        await player.SaveProgressAsync().ConfigureAwait(false);
        MUIdleHuntTracker.SetHuntIntent(player, hunt);

        // HUNT v2 (docs/HUNT_V2.md): the server hunts for the player - the engine and navigator of the
        // offline hunt. The navigator takes the hunt out of town, to the pinned map or the best one.
        if (hunt && settings is not null)
        {
            await OnlineHunt.StartAsync(player, settings).ConfigureAwait(false);
        }
        else
        {
            await OnlineHunt.StopAsync(player, "MANUAL").ConfigureAwait(false);
        }
    }
}
