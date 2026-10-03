// <copyright file="MUIdleSettings.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Text.Json;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Persistence;

/// <summary>
/// The idle settings of a character which are not part of its MU Helper configuration (the MU Helper
/// configuration covers skills, potions thresholds, pickup filters and field repair).
/// Stored as JSON in <see cref="MUIdleCharacterState.SettingsJson"/>.
/// </summary>
public sealed class MUIdleSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Gets or sets a value indicating whether the offline hunter walks to hunting grounds on its own,
    /// and back to them after a merchant visit or a death.
    /// </summary>
    public bool AutoTravel { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the offline hunter may warp to another map which suits
    /// its level better (paying the warp costs).
    /// </summary>
    public bool AutoMapSelection { get; set; } = true;

    /// <summary>
    /// Gets or sets the number of the preferred map; null for no preference. Reserved.
    /// </summary>
    public int? PreferredMap { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether junk gets sold at merchants (see <see cref="IsJunk"/>).
    /// </summary>
    public bool AutoSell { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the equipped gear gets repaired at merchants.
    /// </summary>
    public bool AutoRepair { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether potions get bought at merchants.
    /// </summary>
    public bool AutoBuyPotions { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the hunt continues after a death (needs <see cref="AutoTravel"/>).
    /// </summary>
    public bool KeepHuntingAfterDeath { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether level up points get distributed automatically.
    /// </summary>
    public bool AutoBuild { get; set; }

    /// <summary>
    /// Gets or sets the highest item level (+0..+15) of a plain weapon or armor which counts as junk.
    /// </summary>
    public int SellMaxItemLevel { get; set; } = 4;

    /// <summary>
    /// Gets or sets the identifiers of the items the player locked; they're never sold automatically.
    /// </summary>
    public List<Guid> LockedItems { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether HUNT enters an event when it opens and the character
    /// is ready for it (<see cref="MUIdleEvents"/>).
    /// </summary>
    public bool EventsFirst { get; set; } = true;

    /// <summary>
    /// Gets or sets the events (their keys, see <see cref="MUIdleEvents.Key"/>) the player took out of that.
    /// </summary>
    public List<string> EventOptOut { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether HUNT goes to a live world boss of the character's level
    /// (<see cref="MUIdleWorldBossPlugIn"/>).
    /// </summary>
    public bool BossesFirst { get; set; } = true;

    /// <summary>
    /// Gets or sets the auto build preset (Elf "support", Magic Gladiator "energy", Dark Lord "raven");
    /// <c>null</c> for the class default (<see cref="MUIdleAutoBuild.GetWeights(byte, string?)"/>).
    /// </summary>
    public string? BuildPreset { get; set; }

    /// <summary>
    /// Parses the settings from JSON; returns the defaults for null or invalid JSON.
    /// </summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The settings.</returns>
    public static MUIdleSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new MUIdleSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<MUIdleSettings>(json, JsonOptions) ?? new MUIdleSettings();
        }
        catch (JsonException)
        {
            return new MUIdleSettings();
        }
    }

    /// <summary>
    /// Serializes the settings to JSON.
    /// </summary>
    /// <returns>The JSON.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Determines whether an item of the backpack is junk which may be sold automatically. Only plain
    /// weapons and armor qualify: never anything excellent, ancient, socketed, wings, jewels, pets,
    /// jewelry, consumables or other misc items, nothing above <see cref="SellMaxItemLevel"/>, and
    /// nothing the player locked. Equipped items are excluded by the caller.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns><c>true</c>, if the item is junk.</returns>
    public bool IsJunk(Item item)
    {
        if (item.Definition is not { } definition
            || definition.Group > 11 // 12+: wings, jewels, orbs, scrolls, pets, jewelry, potions, misc.
            || this.LockedItems.Contains(item.GetId())
            || item.Level > this.SellMaxItemLevel
            || item.IsExcellent()
            || item.IsAncient()
            || item.SocketCount > 0
            || item.IsWing()
            || item.IsJewelry()
            || item.IsTrainablePet()
            || item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.HarmonyOption
                                         || o.ItemOption?.OptionType == ItemOptionTypes.GuardianOption))
        {
            return false;
        }

        return true;
    }
}
