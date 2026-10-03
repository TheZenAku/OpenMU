// <copyright file="BotActivity.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Bots;

/// <summary>
/// What a <see cref="BotNavigator"/> starts doing, as told through <see cref="BotNavigatorPolicy.ActivityChanged"/>.
/// Fighting and dying are not reported here: the player's own events say those.
/// </summary>
public enum BotActivity
{
    /// <summary>Walking to a hunting ground on the current map.</summary>
    Travelling,

    /// <summary>Warping to another map.</summary>
    Warping,

    /// <summary>On a trip to a merchant (repair, sell, potions).</summary>
    Shopping,

    /// <summary>On a trip to the buff NPC.</summary>
    Buff,
}
