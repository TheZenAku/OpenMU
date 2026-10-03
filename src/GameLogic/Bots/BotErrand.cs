// <copyright file="BotErrand.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.Offline;

/// <summary>
/// An errand at an NPC which the navigator runs on a player's terms (e.g. a quest at its giver): it
/// travels to the map (through the warp whose landing walks to the NPC), walks up to the NPC, opens
/// its dialog like a player and runs <see cref="ActAsync"/> while it is open (in the MU Helper tick,
/// where every bot mutation runs), then closes the dialog.
/// </summary>
/// <param name="MapNumber">The map of the NPC.</param>
/// <param name="NpcNumber">The monster definition number of the NPC.</param>
/// <param name="Purpose">What the errand is for (logs).</param>
/// <param name="ActAsync">What to do while the NPC's dialog is open.</param>
public sealed record BotErrand(short MapNumber, short NpcNumber, string Purpose, Func<OfflinePlayer, NonPlayerCharacter, ValueTask> ActAsync);
