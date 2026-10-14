// <copyright file="IHunter.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Offline;

using System.Collections.Concurrent;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// MUIdle (HUNT v2, docs/HUNT_V2.md): a character the server hunts for. The hunting handlers of
/// <see cref="OfflinePlayerMuHelper"/> and the <see cref="Bots.BotNavigator"/> work on it rather than on
/// the <see cref="OfflinePlayer"/> type, so that the same engine also hunts for a player who is online
/// (MUIdle's online hunt keeps its own state). The offline player is its own hunter.
/// </summary>
public interface IHunter
{
    /// <summary>Gets the character in the world.</summary>
    Player Player { get; }

    /// <summary>Gets the start of the hunting session.</summary>
    DateTime StartTimestamp { get; }

    /// <summary>Gets or sets the position the hunt keeps around (see <see cref="OfflinePlayer.HuntingOrigin"/>).</summary>
    Point HuntingOrigin { get; set; }

    /// <summary>Gets or sets a target the session was sent for (see <see cref="OfflinePlayer.PriorityTarget"/>).</summary>
    IAttackable? PriorityTarget { get; set; }

    /// <summary>Gets or sets why the session stopped itself; <c>null</c> when stopped from outside.</summary>
    string? StopReason { get; set; }

    /// <summary>Gets a value indicating whether the hunt goes on after a death and the respawn.</summary>
    bool RespawnAndContinue { get; }

    /// <summary>Gets actions queued from outside the hunting tick, drained at its start.</summary>
    ConcurrentQueue<Func<ValueTask>> PendingBotActions { get; }

    /// <summary>Gets the player who attacked the character lately (self-defense), if any.</summary>
    Player? RecentAggressor { get; }

    /// <summary>Gets or sets a value indicating whether the character is on a shopping trip.</summary>
    bool IsOnShoppingTrip { get; set; }

    /// <summary>Gets or sets a value indicating whether the character is on a trip to a buff NPC.</summary>
    bool IsOnBuffTrip { get; set; }

    /// <summary>Gets or sets the time the character last struck something.</summary>
    DateTime LastAttackUtc { get; set; }

    /// <summary>
    /// Gets the death site to walk back to for a revenge (bots only).
    /// </summary>
    /// <param name="currentMap">The current map.</param>
    /// <param name="deathSite">The death site.</param>
    /// <returns><c>true</c>, if a revenge leads there.</returns>
    bool TryGetRevengeDestination(GameMapDefinition currentMap, out Point deathSite);

    /// <summary>
    /// Ends a revenge (bots only).
    /// </summary>
    /// <param name="reason">Why.</param>
    void ExpireRevenge(string reason);

    /// <summary>
    /// Gets a death site to keep away from (see <see cref="OfflinePlayer.TryGetDeathSiteToAvoid"/>).
    /// </summary>
    /// <param name="currentMap">The current map.</param>
    /// <param name="deathSite">The death site.</param>
    /// <returns><c>true</c>, if there is one on this map.</returns>
    bool TryGetDeathSiteToAvoid(GameMapDefinition currentMap, out Point deathSite);

    /// <summary>
    /// Runs the actions queued from outside the hunting tick.
    /// </summary>
    /// <returns>The task.</returns>
    ValueTask DrainPendingBotActionsAsync();

    /// <summary>
    /// Called after every hunting tick which completed.
    /// </summary>
    void OnAiTickSucceeded();

    /// <summary>
    /// Called after a hunting tick which threw.
    /// </summary>
    void OnAiTickFailed();

    /// <summary>
    /// Arms a pending revenge after the respawn (bots only).
    /// </summary>
    void ArmRevengeAfterRespawn();

    /// <summary>
    /// Gets a value indicating whether the hunt holds still for now (MUIdle: the player of an online hunt
    /// took the controls); the engine and the navigator skip their ticks meanwhile.
    /// </summary>
    bool IsPaused => false;

    /// <summary>
    /// Gets the spot of the current map the player chose for the hunt (the map window, HUNT v2): the hunt
    /// walks there and hunts there, and neither picks other grounds nor other maps meanwhile.
    /// </summary>
    Point? PinnedSpot => null;

    /// <summary>
    /// Lets the chosen spot go (no way leads there).
    /// </summary>
    void ForgetPinnedSpot()
    {
    }

    /// <summary>
    /// Ends the hunting session itself (it could not pay its fee, or it died and does not go on).
    /// </summary>
    /// <returns>The task.</returns>
    ValueTask StopSessionAsync();
}
