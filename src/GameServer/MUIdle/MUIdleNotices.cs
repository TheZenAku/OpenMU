// <copyright file="MUIdleNotices.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameServer.RemoteView;

/// <summary>
/// The notices of an update (D34), left by the update tool in <c>MUIDLE_NOTICE_FILE</c>: a new client
/// build ("client") or a server restart in a few minutes ("restart"). A new notice goes to every player
/// in the game; a pending restart also to who enters later. The client shows a window with the reload.
/// </summary>
internal static class MUIdleNotices
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static DateTime _lastWrite;

    private static Notice? _current;

    /// <summary>
    /// A notice of the update tool.
    /// </summary>
    /// <param name="Id">Tells one notice from the next: the same notice is sent once.</param>
    /// <param name="Kind">"client" (a new build is on the web server) or "restart" (the server restarts at <paramref name="RestartAtUtc"/>).</param>
    /// <param name="RestartAtUtc">When the server restarts.</param>
    internal sealed record Notice(string Id, string Kind, DateTime? RestartAtUtc);

    /// <summary>
    /// Reads a notice; null for anything else than a client or restart notice with an id.
    /// </summary>
    /// <param name="json">The JSON of the notice file.</param>
    /// <returns>The notice.</returns>
    public static Notice? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var notice = JsonSerializer.Deserialize<Notice>(json, JsonOptions);
            return notice is { Id.Length: > 0 } && (notice.Kind == "client" || (notice.Kind == "restart" && notice.RestartAtUtc is not null))
                ? notice with { RestartAtUtc = notice.RestartAtUtc?.ToUniversalTime() }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Looks at the notice file (it is a file stamp when nothing changed) and sends a new notice to the players.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    public static async ValueTask TickAsync(IGameContext gameContext)
    {
        if (Environment.GetEnvironmentVariable("MUIDLE_NOTICE_FILE") is not { Length: > 0 } file)
        {
            return;
        }

        string json;
        try
        {
            if (!File.Exists(file))
            {
                _current = null;
                _lastWrite = default;
                return;
            }

            var write = File.GetLastWriteTimeUtc(file);
            if (write == _lastWrite)
            {
                return;
            }

            _lastWrite = write;
            json = await File.ReadAllTextAsync(file).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Being written: the next tick reads it.
            _lastWrite = default;
            return;
        }

        var notice = Parse(json);
        if (notice is null || notice.Id == _current?.Id)
        {
            return;
        }

        _current = notice;
        var players = (await gameContext.GetPlayersAsync().ConfigureAwait(false)).OfType<RemotePlayer>().ToList();
        gameContext.LoggerFactory.CreateLogger(typeof(MUIdleNotices)).LogInformation(
            "MUIdle: update notice {Kind} ({Id}) to {Count} player(s).", notice.Kind, notice.Id, players.Count);
        foreach (var player in players)
        {
            await SendAsync(player, notice).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tells a player who enters the game about a restart still to come.
    /// </summary>
    /// <param name="player">The player.</param>
    public static ValueTask SendPendingAsync(RemotePlayer player)
        => _current is { Kind: "restart", RestartAtUtc: { } at } notice && at > DateTime.UtcNow
            ? SendAsync(player, notice)
            : ValueTask.CompletedTask;

    private static async ValueTask SendAsync(RemotePlayer player, Notice notice)
    {
        if (player.PlayerState.CurrentState.IsDisconnectedOrFinished())
        {
            return;
        }

        // Seconds rather than a time: the player's clock may be off.
        var seconds = notice.RestartAtUtc is { } at ? (int)Math.Max(0, (at - DateTime.UtcNow).TotalSeconds) : 0;
        try
        {
            await MUIdlePackets.SendJsonAsync(player, MUIdlePackets.NoticeSubCode, JsonSerializer.Serialize(new { id = notice.Id, kind = notice.Kind, seconds })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            player.Logger.LogDebug(ex, "MUIdle: the update notice did not reach {Player}.", player.Name);
        }
    }
}
