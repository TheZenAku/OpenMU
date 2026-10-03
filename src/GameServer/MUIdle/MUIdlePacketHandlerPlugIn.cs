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
            requested.EventOptOut = requested.EventOptOut.Where(key => MUIdleEvents.Joinable.Any(t => MUIdleEvents.Key(t) == key)).Distinct().ToList();
            requested.BuildPreset = MUIdleAutoBuild.Presets.Contains(requested.BuildPreset) ? requested.BuildPreset : null;
            state.SettingsJson = requested.ToJson();
        }).ConfigureAwait(false);
        if (requested.AutoBuild)
        {
            await MUIdleAutoBuild.DistributeAsync(player, requested.BuildPreset ?? string.Empty).ConfigureAwait(false);
        }

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
        MUIdleSettings? settings = null;
        await player.RunPersistenceExclusiveAsync(async () =>
        {
            var state = await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false);
            state.HuntActive = hunt;
            settings = MUIdleSettings.Parse(state.SettingsJson);
        }).ConfigureAwait(false);
        await player.SaveProgressAsync().ConfigureAwait(false);
        MUIdleHuntTracker.SetHuntIntent(player, hunt);

        if (!hunt || settings is not { AutoTravel: true, AutoMapSelection: false, PreferredMap: { } pinned })
        {
            return;
        }

        // Already on the pinned map, but in town: walk out to a hunting ground, then HUNT.
        if (player.CurrentMap is { } current && current.Definition.Number == pinned)
        {
            if (player.IsAtSafezone())
            {
                MUIdleHuntTracker.Report(player, HuntActivityKind.Travelling);
                var state = await MUIdleSessions.GetOrCreateStateAsync(player, character).ConfigureAwait(false);
                await MUIdleSessions.SendStateAsync(player, state, resume: true, ground: MUIdleHuntMaps.PickGround(player, current)).ConfigureAwait(false);
            }

            return;
        }

        // The same rule as offline: with a pinned map elsewhere, HUNT starts by going there.
        if (MUIdleHuntMaps.Evaluate(player, pinned) is { Warp: { } warp } target)
        {
            MUIdleHuntTracker.Report(player, HuntActivityKind.Warping, target.Map.Name.ValueInNeutralLanguage);

            // Through the player action: the player pays the fare and passes every check of a warp. The
            // map change completes when the client loaded the map; MUIdleSessions.OnMapEnteredAsync goes on.
            MUIdleHuntTracker.ExpectPinnedArrival(player, pinned);
            await Warp.WarpToAsync(player, warp).ConfigureAwait(false);
        }
    }
}
