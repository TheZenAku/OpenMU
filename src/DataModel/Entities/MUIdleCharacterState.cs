// <copyright file="MUIdleCharacterState.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.DataModel.Entities;

/// <summary>
/// The idle (HUNT) state of a character: whether it should keep hunting while its player is away,
/// the statistics of the running or last offline session, and the idle settings which are not
/// part of the MU Helper configuration.
/// </summary>
/// <remarks>
/// It's kept in its own table, so that the character table stays untouched, and it gets
/// deleted together with its character.
/// </remarks>
[AggregateRoot]
public class MUIdleCharacterState
{
    /// <summary>
    /// Gets or sets the identifier.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the character.
    /// </summary>
    public Guid CharacterId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the character is in HUNT mode, i.e. it keeps hunting
    /// on the server when its player disconnects, and resumes hunting when the player returns.
    /// </summary>
    public bool HuntActive { get; set; }

    /// <summary>
    /// Gets or sets the time when the current offline session started; null, if there is none.
    /// </summary>
    public DateTime? OfflineSince { get; set; }

    /// <summary>
    /// Gets or sets the statistics of the running offline session, or of the last finished one which
    /// was not yet shown to the player, as JSON.
    /// </summary>
    public string? OfflineStatisticsJson { get; set; }

    /// <summary>
    /// Gets or sets the idle settings which are not part of the MU Helper configuration, as JSON.
    /// </summary>
    public string? SettingsJson { get; set; }
}
