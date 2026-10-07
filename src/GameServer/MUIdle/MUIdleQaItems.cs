// <copyright file="MUIdleQaItems.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.GameLogic.Views.Inventory;

/// <summary>
/// QA tool: gives items to a character of a QA account while it is in the game, to test crafting
/// (wings, Dark Horse, Dark Raven) without farming the ingredients. The request file <c>items.json</c>
/// in <c>MUIDLE_QA_DIR</c> is written by <c>scripts\qa\Give-Items.ps1</c>. Refused for other accounts;
/// the items go into the character's own inventory (a free square, else "no room"), logged with [QA].
/// </summary>
internal static class MUIdleQaItems
{
    /// <summary>
    /// The most items one request may give.
    /// </summary>
    internal const int MaxItems = 20;

    private const string TriggerFile = "items.json";

    /// <summary>
    /// Reads a request: <c>{"account":..,"character":..,"items":[{"group":13,"number":31,"level":0,"count":1}]}</c>.
    /// </summary>
    /// <param name="json">The request.</param>
    /// <returns>The request with one entry per item, or <c>null</c> when it is not one.</returns>
    internal static Request? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("account", out var a) || a.GetString() is not { Length: > 0 } account
                || !root.TryGetProperty("character", out var c) || c.GetString() is not { Length: > 0 } character
                || !root.TryGetProperty("items", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var items = new List<(byte Group, short Number, byte Level)>();
            foreach (var entry in list.EnumerateArray())
            {
                if (!entry.TryGetProperty("group", out var g) || !g.TryGetByte(out var group)
                    || !entry.TryGetProperty("number", out var n) || !n.TryGetInt16(out var number))
                {
                    return null;
                }

                var level = entry.TryGetProperty("level", out var l) && l.TryGetByte(out var itemLevel) ? itemLevel : (byte)0;
                var count = entry.TryGetProperty("count", out var k) && k.TryGetInt32(out var itemCount) ? itemCount : 1;
                if (level > 15 || count is < 1 or > MaxItems)
                {
                    return null;
                }

                items.AddRange(Enumerable.Repeat((group, number, level), count));
            }

            return items.Count is > 0 and <= MaxItems ? new Request(account, character, items) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Handles a pending request, if the QA tools are on and one is there.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    /// <param name="qaAccounts">The QA accounts.</param>
    public static async ValueTask TryHandleAsync(IGameContext gameContext, IReadOnlyCollection<string> qaAccounts)
    {
        if (Environment.GetEnvironmentVariable("MUIDLE_QA_DIR") is not { Length: > 0 } directory
            || Path.Combine(directory, TriggerFile) is not { } file
            || !File.Exists(file))
        {
            return;
        }

        var logger = gameContext.LoggerFactory.CreateLogger("MUnique.OpenMU.GameServer.MUIdle.Qa");
        string json;
        try
        {
            json = await File.ReadAllTextAsync(file).ConfigureAwait(false);
            File.Delete(file);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "QA items: could not read {File}.", file);
            return;
        }

        if (Parse(json) is not { } request)
        {
            logger.LogWarning("QA items: request {Request} is not one.", json);
            return;
        }

        if (!qaAccounts.Contains(request.Account, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogWarning("QA items: {Account} is no QA account - refused.", request.Account);
            return;
        }

        // In the world, also with an NPC window open (the trainer, a shop); not in a trade.
        var player = (await gameContext.GetPlayersAsync().ConfigureAwait(false)).FirstOrDefault(p =>
            p is not OfflinePlayer
            && (p.PlayerState.CurrentState == PlayerState.EnteredWorld || p.PlayerState.CurrentState == PlayerState.NpcDialogOpened)
            && string.Equals(p.Account?.LoginName, request.Account, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.SelectedCharacter?.Name, request.Character, StringComparison.Ordinal));
        if (player?.Inventory is not { } inventory)
        {
            logger.LogWarning("QA items: {Account}/{Character} is not in the world (logged out, or in a trade) - log it in first.", request.Account, request.Character);
            return;
        }

        var given = new List<string>();
        foreach (var (group, number, level) in request.Items)
        {
            if (gameContext.Configuration.Items.FirstOrDefault(d => d.Group == group && d.Number == number) is not { } definition)
            {
                logger.LogWarning("QA items: no item {Group}/{Number}.", group, number);
                continue;
            }

            var item = player.PersistenceContext.CreateNew<Item>();
            item.Definition = definition;
            item.Level = Math.Min(level, definition.MaximumItemLevel);
            item.Durability = item.IsStackable() ? 1 : definition.Durability;
            if (!await inventory.AddItemAsync(item).ConfigureAwait(false))
            {
                await player.PersistenceContext.DeleteAsync(item).ConfigureAwait(false);
                logger.LogWarning("QA items: no room for {Item} in the inventory of {Character}.", definition.Name.ValueInNeutralLanguage, request.Character);
                break;
            }

            await player.InvokeViewPlugInAsync<IItemAppearPlugIn>(p => p.ItemAppearAsync(item)).ConfigureAwait(false);
            given.Add($"{definition.Name.ValueInNeutralLanguage} +{item.Level}");
        }

        if (given.Count > 0)
        {
            await player.SaveProgressAsync().ConfigureAwait(false);
        }

        logger.LogInformation("QA items: {Account}/{Character} got {Items} [QA].", request.Account, request.Character, given.Count > 0 ? string.Join(", ", given) : "nothing");
    }

    /// <summary>
    /// A request: the account, the character and one entry per item.
    /// </summary>
    /// <param name="Account">The QA account.</param>
    /// <param name="Character">The character, in the game.</param>
    /// <param name="Items">The items: group, number and level, one entry each.</param>
    internal sealed record Request(string Account, string Character, IReadOnlyList<(byte Group, short Number, byte Level)> Items);
}
