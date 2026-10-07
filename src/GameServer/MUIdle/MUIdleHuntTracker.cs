// <copyright file="MUIdleHuntTracker.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// What the hunt of a character is doing.
/// </summary>
internal enum HuntActivityKind
{
    /// <summary>Not hunting (MANUAL, or waiting).</summary>
    Idle,

    /// <summary>Fighting monsters.</summary>
    Hunting,

    /// <summary>Walking to a hunting ground.</summary>
    Travelling,

    /// <summary>Warping to another map.</summary>
    Warping,

    /// <summary>On a trip to a merchant.</summary>
    Shopping,

    /// <summary>On a trip to the buff NPC.</summary>
    Buff,

    /// <summary>Dead, waiting for the respawn.</summary>
    Dead,

    /// <summary>In an event (Blood Castle, Devil Square, Chaos Castle).</summary>
    Event,

    /// <summary>On the way to a quest giver, or at it (accepting or handing in a quest).</summary>
    Quest,

    /// <summary>HUNT is on but cannot go on (the MU Helper fee cannot be paid).</summary>
    Paused,
}

/// <summary>
/// The one hunt state of a character, online and offline alike. The offline navigator reports its
/// walks, trips and warps (<see cref="BotNavigatorPolicy.ActivityChanged"/>); the player's own events
/// say hunting, dying and idling. Every change is logged at Debug ("MUIdle hunt ...") and sent to the
/// player's client when there is one (<see cref="MUIdlePackets.ActivitySubCode"/>). When a map is pinned
/// and the character is elsewhere, the state also says why (<see cref="HuntMapRefusal"/>).
/// </summary>
internal static class MUIdleHuntTracker
{
    /// <summary>How long a judged pinned map stays judged while nothing else changes (kills report often).</summary>
    private static readonly TimeSpan RefusalCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long a warp to the pinned map may take before the trip is forgotten (e.g. the warp was refused).</summary>
    private static readonly TimeSpan ArrivalTimeout = TimeSpan.FromMinutes(2);

    private static readonly ConditionalWeakTable<Player, Entry> Entries = new();

    /// <summary>The log category of the hunt transitions (Debug, enabled in appsettings.json).</summary>
    /// <summary>
    /// The note source of a HUNT held back after a death (see <see cref="MUIdleSessions.OnMapEnteredAsync"/>).
    /// </summary>
    internal const string DeathNote = "death";

    private const string LogCategory = "MUnique.OpenMU.GameServer.MUIdle.Hunt";

    /// <summary>
    /// Maps what the navigator reported to a hunt activity.
    /// </summary>
    /// <param name="activity">The navigator's activity.</param>
    /// <returns>The hunt activity.</returns>
    public static HuntActivityKind FromBot(BotActivity activity) => activity switch
    {
        BotActivity.Travelling => HuntActivityKind.Travelling,
        BotActivity.Warping => HuntActivityKind.Warping,
        BotActivity.Shopping => HuntActivityKind.Shopping,
        BotActivity.Buff => HuntActivityKind.Buff,
        BotActivity.Errand => HuntActivityKind.Quest,
        _ => HuntActivityKind.Hunting,
    };

    /// <summary>
    /// Sets the map the player pinned (null: none), so the state can say why the hunt is elsewhere.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="settings">The idle settings of the character.</param>
    /// <param name="huntIntent">The stored HUNT intent, when known.</param>
    public static void SetSettings(Player player, MUIdleSettings settings, bool? huntIntent = null)
    {
        var entry = Entries.GetValue(player, _ => new Entry());
        lock (entry)
        {
            entry.Settings = settings;
            entry.PinnedMap = settings.AutoMapSelection ? null : settings.PreferredMap;
            entry.RefusalCheckedUtc = DateTime.MinValue;
            if (huntIntent is { } hunt)
            {
                entry.HuntIntent = hunt;
            }
        }
    }

    /// <summary>
    /// Gets the idle settings last set for the player (null until then).
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The settings.</returns>
    public static MUIdleSettings? GetSettings(Player player)
    {
        var entry = Entries.GetValue(player, _ => new Entry());
        lock (entry)
        {
            return entry.Settings;
        }
    }

    /// <summary>
    /// Reports HUNT paused because the MU Helper fee cannot be paid, with the money and the fee.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="money">The player's Zen.</param>
    /// <param name="fee">The fee.</param>
    public static void ReportPaused(Player player, long money, long fee)
    {
        var entry = Entries.GetValue(player, _ => new Entry());
        lock (entry)
        {
            entry.PauseMoney = money;
            entry.PauseFee = fee;
        }

        Report(player, HuntActivityKind.Paused);
    }

    /// <summary>
    /// Sets, or clears with a <c>null</c> reason, a note the HUD shows under the activity: why HUNT
    /// does not do what it would (no safer map, a fare it cannot pay, a quest waiting for zen). One per source.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="source">Who sets the note (<c>map</c>, <c>quest</c>).</param>
    /// <param name="reason">The reason, a text key of the client; <c>null</c> clears the note.</param>
    /// <param name="have">The player's amount (Zen), if the reason has one.</param>
    /// <param name="need">The needed amount, if the reason has one.</param>
    public static void SetNote(Player player, string source, string? reason, long have = 0, long need = 0)
    {
        var entry = Entries.GetValue(player, _ => new Entry());
        var note = reason is null ? null : new Note(reason, have, need);
        Note? old;
        lock (entry)
        {
            entry.Notes.TryGetValue(source, out old);
            if (old == note)
            {
                return;
            }

            if (note is null)
            {
                entry.Notes.Remove(source);
            }
            else
            {
                entry.Notes[source] = note;
            }
        }

        if (note is not null && note.Reason != old?.Reason)
        {
            player.GameContext.LoggerFactory.CreateLogger(LogCategory).LogInformation(
                "MUIdle hunt {Character}: note {Reason} ({Have} of {Need}).", player.Name, note.Reason, have, need);
        }

        if (player is RemotePlayer { Connection.Connected: true } remotePlayer)
        {
            _ = SendAsync(remotePlayer);
        }
    }

    /// <summary>
    /// Whether the player switched HUNT on - its intent, which holds while the client's helper pauses
    /// (in town, the helper stops itself).
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns><c>true</c>, if HUNT is the player's choice now.</returns>
    public static bool HasHuntIntent(Player player)
    {
        if (!Entries.TryGetValue(player, out var entry))
        {
            return false;
        }

        lock (entry)
        {
            return entry.HuntIntent;
        }
    }

    /// <summary>
    /// Whether the last thing the hunt reported was the character's death (it is respawning now).
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns><c>true</c>, if so.</returns>
    public static bool WasDead(Player player)
    {
        if (!Entries.TryGetValue(player, out var entry))
        {
            return false;
        }

        lock (entry)
        {
            return entry.Kind == HuntActivityKind.Dead;
        }
    }

    /// <summary>
    /// Records whether the player switched HUNT on, and reports it.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="hunt">HUNT (<c>true</c>) or MANUAL.</param>
    public static void SetHuntIntent(Player player, bool hunt)
    {
        var entry = Entries.GetValue(player, _ => new Entry());
        lock (entry)
        {
            entry.HuntIntent = hunt;
            if (!hunt)
            {
                // The notes explain what HUNT does not do; MANUAL needs none.
                entry.Notes.Clear();
            }

            // Switched by the player: it decided after the death that held HUNT back.
            entry.Notes.Remove(DeathNote);
        }

        // The player's own switch (or the resume) ends a fee pause; still short of the fee, the
        // next check pauses again.
        MUIdleEconomy.ClearPause(player);
        Report(player, hunt ? HuntActivityKind.Hunting : HuntActivityKind.Idle);
    }

    /// <summary>
    /// Remembers that the player was sent to its pinned map. The warp completes when the client has
    /// loaded the map, so the hunt goes on from <see cref="TryTakePinnedArrival"/> at the map entry.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="mapNumber">The pinned map.</param>
    /// <param name="ground">Where to walk on arrival; a hunting ground of the map when <c>null</c>.</param>
    public static void ExpectPinnedArrival(Player player, int mapNumber, Point? ground = null)
    {
        var entry = Entries.GetValue(player, _ => new Entry());
        lock (entry)
        {
            entry.ArrivalMap = mapNumber;
            entry.ArrivalGround = ground;
            entry.ArrivalExpectedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Whether the player just entered the pinned map it was sent to (and forgets the trip).
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="ground">Where to walk, when the trip named a spot.</param>
    /// <returns><c>true</c> when the trip ended on the pinned map.</returns>
    public static bool TryTakePinnedArrival(Player player, out Point? ground)
    {
        var entry = Entries.GetValue(player, _ => new Entry());
        lock (entry)
        {
            ground = entry.ArrivalGround;
            if (entry.ArrivalMap is not { } map)
            {
                return false;
            }

            var arrived = player.CurrentMap?.Definition.Number == map;
            if (arrived || DateTime.UtcNow - entry.ArrivalExpectedUtc > ArrivalTimeout)
            {
                entry.ArrivalMap = null;
            }

            return arrived;
        }
    }

    /// <summary>
    /// A player with a client entered a map (respawn, warp, map change): hunting when its MU Helper runs, else idle.
    /// </summary>
    /// <param name="player">The player.</param>
    public static void OnEnteredWorld(Player player)
    {
        // The server's own word on the MU Helper beats the intent: after a warp into town it stops.
        // Closing an NPC window ends here too; a HUNT paused for the fee stays shown as paused.
        var hunt = player.Attributes?[Stats.IsMuHelperActive] > 0;
        if (!hunt && MUIdleEconomy.IsPaused(player))
        {
            return;
        }

        Report(player, hunt ? HuntActivityKind.Hunting : HuntActivityKind.Idle);
    }

    /// <summary>
    /// Reports what the hunt does now. Nothing happens when nothing changed.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="kind">The activity.</param>
    /// <param name="map">The map it concerns; the current map when <c>null</c>.</param>
    public static void Report(Player player, HuntActivityKind kind, string? map = null)
    {
        map ??= player.CurrentMap?.Definition.Name.ValueInNeutralLanguage;
        var entry = Entries.GetValue(player, _ => new Entry());
        var now = DateTime.UtcNow;
        lock (entry)
        {
            var sameActivity = entry.Kind == kind && entry.Map == map;
            if (sameActivity && now - entry.RefusalCheckedUtc < RefusalCheckInterval)
            {
                return;
            }

            var refusal = JudgePinnedMap(player, entry.PinnedMap);
            entry.RefusalCheckedUtc = now;
            if (sameActivity && refusal == entry.PinnedRefusal)
            {
                return;
            }

            // Its own category (MUnique.OpenMU.GameServer.MUIdle.Hunt), so Debug can be switched on for it alone.
            player.GameContext.LoggerFactory.CreateLogger(LogCategory).LogDebug(
                "MUIdle hunt {Character}: {From} -> {To} on {Map} after {Seconds:0} s (pinned map {Pinned}: {Refusal}).",
                player.Name,
                entry.Kind,
                kind,
                map,
                (now - entry.SinceUtc).TotalSeconds,
                entry.PinnedMap?.ToString() ?? "none",
                refusal?.ToString() ?? "-");
            if (!sameActivity)
            {
                entry.SinceUtc = now;
            }

            entry.Kind = kind;
            entry.Map = map;
            entry.PinnedRefusal = refusal;
        }

        if (player is RemotePlayer { Connection.Connected: true } remotePlayer)
        {
            _ = SendAsync(remotePlayer);
        }
    }

    /// <summary>
    /// Gets the current state for the client (also part of the full MUIdle state).
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The state, serializable to JSON.</returns>
    public static object Snapshot(Player player)
    {
        var entry = Entries.GetValue(player, _ => new Entry());
        lock (entry)
        {
            return new
            {
                kind = Name(entry.Kind),
                map = entry.Map ?? player.CurrentMap?.Definition.Name.ValueInNeutralLanguage,
                pinnedReason = entry.PinnedRefusal is { } refusal ? RefusalName(refusal) : null,
                pause = entry.Kind == HuntActivityKind.Paused ? new { reason = "zen", have = entry.PauseMoney, need = entry.PauseFee } : null,
                notes = entry.Notes.Values.Select(n => new { reason = n.Reason, have = n.Have, need = n.Need }).ToList(),
            };
        }
    }

    /// <summary>
    /// Gets the wire name of a refusal (the client's <c>HuntMapReason</c>).
    /// </summary>
    /// <param name="refusal">The refusal.</param>
    /// <returns>The name.</returns>
    public static string RefusalName(HuntMapRefusal refusal) => refusal switch
    {
        HuntMapRefusal.Level => "level",
        HuntMapRefusal.Zen => "zen",
        HuntMapRefusal.NoWarp => "noWarp",
        _ => "requirement",
    };

    private static string Name(HuntActivityKind kind) => kind switch
    {
        HuntActivityKind.Hunting => "hunting",
        HuntActivityKind.Travelling => "travelling",
        HuntActivityKind.Warping => "warping",
        HuntActivityKind.Shopping => "shopping",
        HuntActivityKind.Buff => "buff",
        HuntActivityKind.Dead => "dead",
        HuntActivityKind.Event => "event",
        HuntActivityKind.Quest => "quest",
        HuntActivityKind.Paused => "paused",
        _ => "idle",
    };

    /// <summary>Why the pinned map is not where the character is; null when it is there, or on its way, or none is pinned.</summary>
    private static HuntMapRefusal? JudgePinnedMap(Player player, int? pinnedMap)
    {
        if (pinnedMap is not { } pinned || player.CurrentMap?.Definition.Number == pinned)
        {
            return null;
        }

        return MUIdleHuntMaps.Evaluate(player, pinned)?.Refusal;
    }

    private static async Task SendAsync(RemotePlayer player)
    {
        try
        {
            await MUIdlePackets.SendJsonAsync(player, MUIdlePackets.ActivitySubCode, JsonSerializer.Serialize(Snapshot(player))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            player.Logger.LogDebug(ex, "MUIdle: could not send the hunt activity to {Player}.", player);
        }
    }

    private sealed record Note(string Reason, long Have, long Need);

    private sealed class Entry
    {
        public Dictionary<string, Note> Notes { get; } = new();

        public long PauseMoney { get; set; }

        public long PauseFee { get; set; }

        public HuntActivityKind Kind { get; set; } = HuntActivityKind.Idle;

        public string? Map { get; set; }

        public int? PinnedMap { get; set; }

        public bool HuntIntent { get; set; }

        public MUIdleSettings? Settings { get; set; }

        public int? ArrivalMap { get; set; }

        public Point? ArrivalGround { get; set; }

        public DateTime ArrivalExpectedUtc { get; set; }

        public HuntMapRefusal? PinnedRefusal { get; set; }

        public DateTime SinceUtc { get; set; } = DateTime.UtcNow;

        public DateTime RefusalCheckedUtc { get; set; } = DateTime.MinValue;
    }
}
