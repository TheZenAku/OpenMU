// <copyright file="MUIdleQaCharacter.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;

/// <summary>
/// QA tool: a request file <c>character.json</c> in the directory of the <c>MUIDLE_QA_DIR</c> environment
/// variable (unset = off; only someone on the server machine can drop it) sets the level of a character
/// of a QA account (the QA accounts of the world boss configuration) - to reach a class quest without
/// days of hunting. Only a character which is not in the game (no session, no offline hunt), through
/// the persistence layer like every load and save; the experience matches the level. Logged as QA.
/// </summary>
internal static class MUIdleQaCharacter
{
    private const string TriggerFile = "character.json";

    /// <summary>
    /// Reads a request (<c>{"account":"test300","character":"test300Dw","level":380}</c>).
    /// </summary>
    /// <param name="json">The request.</param>
    /// <returns>The account, character and level, or <c>null</c>.</returns>
    internal static (string Account, string Character, int Level)? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.TryGetProperty("account", out var a) && a.GetString() is { Length: > 0 } account
                   && root.TryGetProperty("character", out var c) && c.GetString() is { Length: > 0 } character
                   && root.TryGetProperty("level", out var l) && l.TryGetInt32(out var level) && level is >= 1 and <= 400
                ? (account, character, level)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Handles a pending request, if any.
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
            logger.LogWarning(ex, "QA character: could not read {File}.", file);
            return;
        }

        if (Parse(json) is not { } request)
        {
            logger.LogWarning("QA character: request {Request} is not one.", json);
            return;
        }

        if (!qaAccounts.Contains(request.Account, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogWarning("QA character: {Account} is no QA account - refused.", request.Account);
            return;
        }

        var inGame = (await gameContext.GetPlayersAsync().ConfigureAwait(false))
            .Any(p => string.Equals(p.Account?.LoginName, request.Account, StringComparison.OrdinalIgnoreCase));
        if (inGame)
        {
            logger.LogWarning("QA character: {Account} is in the game (a session or an offline hunt) - log it out first.", request.Account);
            return;
        }

        using var context = gameContext.PersistenceContextProvider.CreateNewPlayerContext(gameContext.Configuration);
        var account = await context.GetAccountByLoginNameAsync(request.Account).ConfigureAwait(false);
        var character = account?.Characters.FirstOrDefault(c => string.Equals(c.Name, request.Character, StringComparison.Ordinal));
        var levelAttribute = character?.Attributes.FirstOrDefault(a => a.Definition == Stats.Level);
        if (character is null || levelAttribute is null)
        {
            logger.LogWarning("QA character: {Account}/{Character} not found.", request.Account, request.Character);
            return;
        }

        var before = (int)levelAttribute.Value;
        var table = gameContext.ExperienceTable;
        levelAttribute.Value = request.Level;
        character.Experience = table[Math.Min(request.Level, table.Length - 1)];
        await context.SaveChangesAsync().ConfigureAwait(false);
        logger.LogInformation("QA character: {Account}/{Character} level {Before} -> {Level} [QA].", request.Account, request.Character, before, request.Level);
    }
}
