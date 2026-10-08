// <copyright file="MUIdleFreeHuntPlugInSeason6.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.MuHelper;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// MUIdle (D29): HUNT is free for now - the owner's call. The MU Helper's fee (OpenMU's
/// <see cref="MuHelperZenCostCalculator"/>: cost of the stage times the level, every pay interval,
/// online and offline) becomes 0 by setting every stage's cost to 0. The stages stay, so the fee comes
/// back by entering the costs again in the Admin Panel's plugin configuration; nothing else changes.
/// </summary>
[PlugIn]
[Display(Name = PlugInName, Description = PlugInDescription)]
[Guid("3F7C2D81-5B6A-4E09-8C14-9A2B7E6D1F35")]
public class MUIdleFreeHuntPlugInSeason6 : UpdatePlugInBase
{
    /// <summary>The plug in name.</summary>
    internal const string PlugInName = "MUIdle: free HUNT";

    /// <summary>The plug in description.</summary>
    internal const string PlugInDescription = "The MU Helper costs no zen: every stage's cost is set to 0.";

    /// <inheritdoc />
    public override string Name => PlugInName;

    /// <inheritdoc />
    public override string Description => PlugInDescription;

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override bool IsMandatory => true;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 10, 8, 16, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The configuration with every stage's cost at 0, in the stored JSON as it is (its reference
    /// metadata included); a configuration never stored gets the default one, free.
    /// </summary>
    /// <param name="json">The stored custom configuration of the MU Helper plugin.</param>
    /// <returns>The free configuration.</returns>
    public static string Free(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not JsonObject root)
        {
            var configuration = new MuHelperConfiguration();
            configuration.CostPerStage = configuration.CostPerStage.Select(_ => 0).ToList();
            return System.Text.Json.JsonSerializer.Serialize(configuration, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        // Written with reference handling, a list is { "$id": ..., "$values": [...] }; without, a plain array.
        var costs = root[nameof(MuHelperConfiguration.CostPerStage)] switch
        {
            JsonObject list when list["$values"] is JsonArray values => values,
            JsonArray values => values,
            _ => null,
        };
        if (costs is null)
        {
            root[nameof(MuHelperConfiguration.CostPerStage)] = new JsonArray(0);
        }
        else
        {
            for (var i = 0; i < costs.Count; i++)
            {
                costs[i] = 0;
            }
        }

        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        foreach (var plugIn in gameConfiguration.PlugInConfigurations.Where(c => c.TypeId == typeof(MuHelperFeaturePlugIn).GUID))
        {
            plugIn.CustomConfiguration = Free(plugIn.CustomConfiguration);
        }

        return ValueTask.CompletedTask;
    }
}
