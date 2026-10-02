// <copyright file="MUIdleConfiguration.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

/// <summary>
/// The configuration of the MUIdle feature (HUNT mode which continues on the server while the
/// player is away).
/// </summary>
public class MUIdleConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether a character in HUNT mode keeps hunting on the server
    /// when the connection of its player drops or the browser gets closed.
    /// </summary>
    [Display(Name = "Continue hunting on disconnect", Description = "A character with a running MU Helper (HUNT) keeps hunting on the server when its player disconnects.")]
    public bool ContinueHuntingOnDisconnect { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the MU Helper gets started again when a player returns
    /// to a character which was hunting.
    /// </summary>
    [Display(Name = "Resume hunting on login", Description = "Starts the MU Helper again when the player returns to a character which was hunting.")]
    public bool ResumeHuntingOnLogin { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the offline hunters of the last run get started again
    /// after a server restart.
    /// </summary>
    [Display(Name = "Restore offline hunters on startup", Description = "Characters which were hunting offline when the server stopped continue after the restart.")]
    public bool RestoreOfflineHuntersOnStartup { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum duration of an offline hunting session in minutes. 0 means unlimited.
    /// </summary>
    [Display(Name = "Maximum offline minutes", Description = "Maximum duration of an offline hunting session in minutes; 0 = unlimited.")]
    public int MaximumOfflineMinutes { get; set; }

    /// <summary>
    /// Gets or sets the offline minutes of a regular account. Reserved for a later allowance model;
    /// <see cref="MaximumOfflineMinutes"/> is the effective limit for now.
    /// </summary>
    [Display(Name = "Free offline minutes (reserved)", Description = "Reserved for a later allowance model.")]
    public int FreeOfflineMinutes { get; set; }

    /// <summary>
    /// Gets or sets the offline minutes of a VIP account. Reserved for a later allowance model.
    /// </summary>
    [Display(Name = "VIP offline minutes (reserved)", Description = "Reserved for a later allowance model.")]
    public int VipOfflineMinutes { get; set; }

    /// <summary>
    /// Gets or sets the item drop rate multiplier (1 = upstream drop chances). The experience rate is
    /// the game configuration's <c>ExperienceRate</c>.
    /// </summary>
    [Display(Name = "Item drop rate", Description = "Multiplier of item drops (1 = normal, 2 = twice as many, 0.5 = half).")]
    public double ItemDropRate { get; set; } = 1;

    /// <summary>
    /// Gets or sets the zen drop rate multiplier (1 = upstream amounts).
    /// </summary>
    [Display(Name = "Zen rate", Description = "Multiplier of dropped zen (1 = normal).")]
    public double ZenRate { get; set; } = 1;

    /// <summary>
    /// Gets or sets the interval in which the progress of the offline hunters is saved.
    /// </summary>
    [Display(Name = "Offline save interval", Description = "How often the progress of offline hunters is saved.")]
    public TimeSpan OfflineSaveInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets the interval in which the progress of online players is saved (upstream only saves
    /// when a player leaves). Zero disables it.
    /// </summary>
    [Display(Name = "Online save interval", Description = "How often the progress of online players is saved, so a crash loses little; 0 = only on leaving.")]
    public TimeSpan OnlineSaveInterval { get; set; } = TimeSpan.FromMinutes(5);
}
