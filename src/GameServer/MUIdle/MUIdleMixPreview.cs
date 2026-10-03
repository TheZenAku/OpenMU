// <copyright file="MUIdleMixPreview.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Text.Json;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.CastleSiege;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.Views.NPC;
using MUnique.OpenMU.GameServer.RemoteView;

/// <summary>
/// The odds of a mix before the player presses the button: the crafting's own item check, success
/// rate and price (plus the Chaos Machine tax) for the items in the crafting dialog - the numbers the
/// mix itself uses, so the client never computes a chance.
/// </summary>
internal static class MUIdleMixPreview
{
    private static readonly ItemCraftAction Craft = new();

    private static readonly CastleSiegeTaxProvider Tax = new();

    /// <summary>
    /// Sends the preview of a mix of the items in the crafting dialog to the client.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="mixType">The crafting number the player picked.</param>
    public static async ValueTask SendAsync(RemotePlayer player, byte mixType)
    {
        if (player.OpenedNpc?.Definition is not { } npc || player.TemporaryStorage is null)
        {
            return;
        }

        (CraftingResult? Error, byte SuccessRate, int Price)? preview;
        byte? fits = null;

        // The action caches its crafting handlers in a plain dictionary: one caller at a time.
        lock (Craft)
        {
            preview = Craft.PreviewMix(player, mixType);
            if (preview is not { Error: null }
                && Craft.FindAppropriateCraftingByItems(player) is { } other
                && other.Number != mixType)
            {
                fits = other.Number;
            }
        }

        var cost = 0L;
        if (preview is { Error: null } ok)
        {
            // As CastleSiegeTaxProvider.TryPayChaosCostAsync: the tax applies at the Chaos Machine only.
            var tax = npc.NpcWindow == NpcWindow.ChaosMachine ? await Tax.GetChaosTaxAsync(player).ConfigureAwait(false) : 0;
            cost = ok.Price + (ok.Price * (long)tax / 100);
        }

        var json = JsonSerializer.Serialize(new
        {
            mixType = (int)mixType,
            result = preview is { } known ? known.Error?.ToString() : nameof(CraftingResult.IncorrectMixItems),
            rate = (int)(preview?.SuccessRate ?? 0),
            cost,
            fits = (int?)fits,
        });
        await MUIdlePackets.SendJsonAsync(player, MUIdlePackets.MixPreviewSubCode, json).ConfigureAwait(false);
    }
}
